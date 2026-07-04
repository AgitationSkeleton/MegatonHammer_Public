using System.Globalization;
using System.Linq;
using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

/// <summary>
/// Per-actor configuration (opened by double-clicking an entity). Edits the actor id,
/// its variable/parameters — with a dropdown of known presets and their descriptions
/// (covers door transitions, spawn types, switch flags, etc.) — plus position/rotation.
/// </summary>
public sealed class EntityConfigDialog : Form
{
    private static readonly Color BgDark   = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput  = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);
    private static readonly Color HdrFg    = Color.FromArgb(140, 190, 255);

    private readonly ZActor        _actor;
    private readonly ActorDatabase _db;
    private readonly bool          _isOoT;
    private readonly MapDocument?  _doc;   // for the flag-channel allocator (optional)

    private readonly Label    _nameLabel;
    private readonly TextBox  _idBox;
    private readonly TextBox  _varBox;
    private readonly ComboBox _presetCombo;
    private readonly ComboBox _namePicker;
    private readonly CheckBox?[] _bits = new CheckBox?[16];   // Hammer-style spawnflags view of the variable
    private readonly Label    _logicHeader;
    private readonly TableLayoutPanel _logicHost;             // typed bit-field controls (rebuilt per actor)
    private Label?            _ioHeader;
    private FlowLayoutPanel?  _ioHost;                         // Hammer-style Outputs/Inputs list (rebuilt per actor)

    /// <summary>Raised when the user clicks a connected actor in the I/O list (Hammer's "Mark") — the host
    /// selects + opens that actor so you can follow a wire from setter to reader.</summary>
    public event Action<ZActor>? GoToActor;
    private bool _loading;

    public event Action? Changed;

    public EntityConfigDialog(ZActor actor, ActorDatabase db, bool isOoT, MapDocument? doc = null)
    {
        _actor = actor;
        _db    = db;
        _isOoT = isOoT;
        _doc   = doc;

        Text            = "Entity Configuration";
        // Resizable both ways (a scroll panel hosts the content) so long fields/dropdowns can be widened
        // and the whole sheet made taller — actor sheets vary a lot in height.
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition   = FormStartPosition.CenterParent;
        MaximizeBox     = true; MinimizeBox = false;
        ClientSize      = new Size(440, 446);
        MinimumSize     = new Size(340, 220);
        BackColor       = BgDark; ForeColor = FgNormal;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var info = _db.Get(actor.Number);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14),
            AutoSize = false, BackColor = BgDark, GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Header
        _nameLabel = new Label
        {
            Text = info?.Name ?? $"Actor 0x{actor.Number:X4}", Dock = DockStyle.Fill, Height = 24,
            ForeColor = HdrFg, Font = UiFonts.Get("Segoe UI", 11f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft,
        };
        AddSpan(table, _nameLabel);
        if (info?.DebugName != null)
            AddSpan(table, new Label { Text = info.DebugName, Dock = DockStyle.Fill, Height = 16,
                ForeColor = Color.FromArgb(140,140,140), Font = UiFonts.Get("Consolas", 8f), TextAlign = ContentAlignment.MiddleLeft });

        AddHeader(table, "IDENTITY");
        _idBox = MakeBox($"0x{actor.Number:X4}");
        _idBox.TextChanged += (_, _) =>
        {
            if (_loading) return;
            if (TryHex(_idBox.Text, out int v)) { _actor.Number = (ushort)v; SyncNamePicker(); RebuildForId(); Bubble(); }
        };
        AddRow(table, "Actor ID", _idBox);

        // Friendly-name picker: type to filter actors by name, pick one to set the id.
        _namePicker = new ComboBox
        {
            // #28: manual SUBSTRING filter, not prefix-autocomplete — every item renders as "0x00A8  Name",
            // so prefix matching on "0x…" matched everything and was useless. Typing now filters by name.
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.None,
            BackColor = BgInput, ForeColor = FgNormal,
            FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 8.5f), Margin = new Padding(2), MaxDropDownItems = 18,
        };
        // Dummy Link (editor-only placeholder) FIRST, matching the Entity Tool + Options actor lists, so
        // a placed entity can be switched to it from the properties dialog too (was missing here).
        var allActorItems = new List<ActorItem> { new(Tools.EntityTool.EditorDummyLinkId, "Dummy Link (editor-only scale)") };
        allActorItems.AddRange(_db.All.Select(a => new ActorItem(a.Id, a.Name)));
        _namePicker.Items.AddRange(allActorItems.Cast<object>().ToArray());
        bool actorFiltering = false;
        _namePicker.TextUpdate += (_, _) =>
        {
            if (actorFiltering) return;
            string t = _namePicker.Text;
            var matches = string.IsNullOrWhiteSpace(t)
                ? allActorItems
                : allActorItems.Where(it => it.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                                         || it.ToString().Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
            actorFiltering = true;
            _namePicker.BeginUpdate();
            _namePicker.Items.Clear();
            _namePicker.Items.AddRange(matches.Cast<object>().ToArray());
            _namePicker.EndUpdate();
            _namePicker.Select(t.Length, 0);
            actorFiltering = false;
            if (matches.Count > 0 && t.Length > 0)
                BeginInvoke(() => { if (!_namePicker.IsDisposed) { _namePicker.DroppedDown = true; _namePicker.Select(t.Length, 0); } });
        };
        _namePicker.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _namePicker.SelectedItem is not ActorItem ai) return;
            _actor.Number = ai.Id;
            _loading = true; _idBox.Text = $"0x{ai.Id:X4}"; _loading = false;
            RebuildForId(); Bubble();
        };
        AddRow(table, "Actor", _namePicker);
        SyncNamePicker();

        AddHeader(table, "PARAMETERS (VARIABLE)");
        _varBox = MakeBox($"0x{actor.Variable:X4}");
        _varBox.TextChanged += (_, _) =>
        {
            if (_loading) return;
            if (TryHex(_varBox.Text, out int v)) { _actor.Variable = (ushort)v; SyncPresetSelection(); RebuildLogic(); Bubble(); }
        };
        AddRow(table, "Variable", _varBox);

        _presetCombo = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgInput,
            ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 8.5f), Margin = new Padding(2),
        };
        _presetCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            if (_presetCombo.SelectedItem is PresetItem p)
            {
                _actor.Variable = p.Var;
                _loading = true; _varBox.Text = $"0x{p.Var:X4}"; _loading = false;
                RebuildLogic();
                Bubble();
            }
        };
        AddRow(table, "Preset", _presetCombo);

        // Hammer-style "Flags" page: the 16-bit variable as individual togglable bits, for
        // actors whose params pack independent flags (the bit grid + the hex box stay in sync).
        AddHeader(table, "PARAMETER BITS  (15 → 0)");
        AddSpan(table, BuildBitsGrid());

        // Decoded logic: the variable's bit-fields as named typed controls (Hammer-style keyvalues),
        // shown only for actors with a registered parameter schema. Rebuilt when the actor id changes.
        _logicHeader = new Label { Text = "LOGIC  (DECODED PARAMETERS)", Dock = DockStyle.Fill, Height = 20,
            Margin = new Padding(0, 8, 0, 2), ForeColor = HdrFg, Font = UiFonts.Get("Segoe UI", 7.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft, Visible = false };
        AddSpan(table, _logicHeader);
        _logicHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true,
            BackColor = BgDark, Margin = new Padding(0), GrowStyle = TableLayoutPanelGrowStyle.AddRows };
        _logicHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        _logicHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddSpan(table, _logicHost);

        // Connections (Hammer Outputs/Inputs): the flags THIS actor sets that others read (outputs) and the
        // flags it reads that others set (inputs) — the actual wiring, click to jump to the connected actor.
        _ioHeader = new Label { Text = "CONNECTIONS  (I/O)", Dock = DockStyle.Fill, Height = 20,
            Margin = new Padding(0, 8, 0, 2), ForeColor = HdrFg, Font = UiFonts.Get("Segoe UI", 7.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft, Visible = false };
        AddSpan(table, _ioHeader);
        _ioHost = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, BackColor = BgDark, Margin = new Padding(2, 0, 0, 0) };
        AddSpan(table, _ioHost);

        AddHeader(table, "TRANSFORM");
        AddInt(table, "X", () => (int)MathF.Round(_actor.XPos), v => { _actor.XPos = v; Bubble(); });
        AddInt(table, "Y", () => (int)MathF.Round(_actor.YPos), v => { _actor.YPos = v; Bubble(); });
        AddInt(table, "Z", () => (int)MathF.Round(_actor.ZPos), v => { _actor.ZPos = v; Bubble(); });
        AddInt(table, "Rot X", () => _actor.XRot, v => { _actor.XRot = (short)v; Bubble(); });
        AddInt(table, "Rot Y", () => _actor.YRot, v => { _actor.YRot = (short)v; Bubble(); });
        AddInt(table, "Rot Z", () => _actor.ZRot, v => { _actor.ZRot = (short)v; Bubble(); });

        // MM only: per-actor day/half-day spawn gating (HALFDAYBIT). Lets you populate a town with
        // actors that appear only on specific days/times — the lever behind MM's living towns.
        if (!_isOoT) AddHalfDayEditor(table);

        // Survival guide for bosses/minibosses with special spawn requirements (D17).
        string? guide = Editor.ActorGuide.For(info?.Name);
        if (guide != null)
        {
            AddHeader(table, "SURVIVAL GUIDE");
            AddSpan(table, new Label
            {
                Text = guide, Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(400, 0),
                ForeColor = Color.FromArgb(230, 200, 120), Font = UiFonts.Get("Segoe UI", 8.5f),
                Margin = new Padding(2, 4, 2, 4),
            });
            ClientSize = new Size(440, 556);   // room for the note
        }

        var close = new Button
        {
            Text = "Close", Dock = DockStyle.Bottom, Height = 30, BackColor = Color.FromArgb(0,122,204),
            ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
        };
        close.Click += (_, _) => Close();

        // The sheet can grow tall (logic fields + survival guide); host it in a scroll panel.
        table.Dock = DockStyle.Top;
        table.AutoSize = true;
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BgDark };
        scroll.Controls.Add(table);
        Controls.Add(scroll);
        Controls.Add(close);
        AcceptButton = close;

        // Item 2: fields whose text runs past the control (long preset/actor descriptions) get a hover
        // tooltip with the full string, and their dropdowns widen so the open list isn't truncated either.
        AttachOverflowTip(_nameLabel);
        AttachOverflowTip(_namePicker);
        AttachOverflowTip(_presetCombo);

        RebuildForId();
    }

    // Show the full text as a hover tooltip when a control's content is clipped, and widen a combo's
    // dropdown so the open list shows untruncated items (item 2).
    private void AttachOverflowTip(Control c)
    {
        void Update()
        {
            string full = c is ComboBox cb ? (cb.SelectedItem?.ToString() ?? cb.Text) : c.Text;
            _tip.SetToolTip(c, full ?? "");
        }
        Update();
        if (c is ComboBox combo)
        {
            combo.SelectedIndexChanged += (_, _) => Update();
            combo.TextChanged += (_, _) => Update();
            combo.DropDown += (_, _) =>
            {
                int w = combo.Width;
                using var g = combo.CreateGraphics();
                foreach (var it in combo.Items)
                    w = Math.Max(w, TextRenderer.MeasureText(g, it?.ToString() ?? "", combo.Font).Width + 28);
                combo.DropDownWidth = Math.Min(Math.Max(w, combo.Width), 1000);
            };
        }
        else c.TextChanged += (_, _) => Update();
    }

    private void RebuildForId()
    {
        _loading = true;
        var info = _db.Get(_actor.Number);
        _nameLabel.Text = info?.Name ?? $"Actor 0x{_actor.Number:X4}";
        Text = $"Entity — {_nameLabel.Text}";

        _presetCombo.Items.Clear();
        _presetCombo.Items.Add(new PresetItem(0xFFFF, "— presets —"));
        if (info != null)
            foreach (var kv in info.Variables.OrderBy(k => k.Key))
                _presetCombo.Items.Add(new PresetItem(kv.Key, kv.Value));
        _loading = false;
        SyncPresetSelection();
        RebuildLogic();
    }

    // Detach AND dispose a host's child controls. Controls.Clear() alone only detaches them, leaking their
    // Win32 window handles every rebuild — enough rebuilds exhaust the process handle pool ("Error creating
    // window handle"). Disposing the container's children releases the handles immediately.
    private static void DisposeAndClear(Control host)
    {
        var stale = host.Controls.Cast<Control>().ToArray();
        host.Controls.Clear();
        foreach (var c in stale) c.Dispose();
    }

    // Rebuilds the typed logic controls for the current actor from its parameter schema (if any).
    private void RebuildLogic()
    {
        _logicHost.SuspendLayout();
        DisposeAndClear(_logicHost);   // dispose old controls, not just detach (window-handle leak otherwise)
        _logicHost.RowCount = 0;
        var def = ActorParamSchema.For(_isOoT, _actor.Number);
        _logicHeader.Visible = def != null;
        _logicHost.Visible = def != null;
        if (def != null)
        {
            foreach (var f in def.Fields) AddLogicField(f);
            if (IsLockedDoor(_actor)) AddLockSideRow();
            if (def.Note != null)
            {
                var note = new Label { Text = def.Note, Dock = DockStyle.Fill, AutoSize = true,
                    MaximumSize = new Size(390, 0), ForeColor = Color.FromArgb(150, 150, 150),
                    Font = UiFonts.Get("Segoe UI", 8f, FontStyle.Italic), Margin = new Padding(2, 4, 2, 2) };
                int row = _logicHost.RowCount;
                _logicHost.Controls.Add(note, 0, row);
                _logicHost.SetColumnSpan(note, 2);
                _logicHost.RowCount = row + 1;
            }
        }
        // Vanilla-dialogue catalog: for a talkable NPC whose lines are hardcoded (no message param, so no
        // Message field above), show its known vanilla lines and let the user override one (reskin the words)
        // via the Dialogue Editor. Shows even when the actor has no schema Def at all (e.g. Talon).
        var catalog = DialogueCatalog.For(!_isOoT, _actor.Number);
        bool hasMsgField = def?.Fields.Any(f => f.Kind == ActorParamSchema.FieldKind.Message) ?? false;
        if (catalog != null && !hasMsgField)
        {
            _logicHeader.Visible = _logicHost.Visible = true;
            AddCatalogDialogueRow(catalog);
        }
        _logicHost.ResumeLayout();
        RebuildConnections();
    }

    // A "Dialogue" row for a catalogued NPC without a message param: a dropdown of its vanilla lines + a
    // Customize button that overrides the chosen line's text via the Dialogue Editor (keeps the NPC's own
    // behaviour; just replaces the words — vanilla-portable).
    private void AddCatalogDialogueRow(DialogueCatalog.Line[] lines)
    {
        var lbl = new Label { Text = "Dialogue", Dock = DockStyle.Fill, ForeColor = FgNormal,
            Font = UiFonts.Get("Segoe UI", 8.5f), Margin = new Padding(2), TextAlign = ContentAlignment.MiddleLeft };
        var host = new Panel { Dock = DockStyle.Fill, Height = 26, Margin = new Padding(0) };
        var edit = new Button { Text = "Customize…", Dock = DockStyle.Right, Width = 82, Height = 24,
            BackColor = Color.FromArgb(60, 60, 63), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 8f) };
        var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Consolas", 8.5f), Margin = new Padding(2) };
        foreach (var l in lines) combo.Items.Add(l);
        combo.SelectedIndex = 0;
        _tip.SetToolTip(combo, "This NPC's vanilla lines (what it says in the game). Pick one, then Customize… to replace it.");
        _tip.SetToolTip(edit, "Override the selected vanilla line with your own text — keeps the NPC's behaviour, just changes the words.");
        edit.Click += (_, _) =>
        {
            if (_doc == null || combo.SelectedItem is not DialogueCatalog.Line line) return;
            var bm = _doc.Scene.Messages.FirstOrDefault(m => m.Id == line.TextId);
            if (bm == null) { bm = new MhMessage(line.TextId, "New dialogue."); _doc.Scene.Messages.Add(bm); }
            bm.IsOverride = true;
            using var dlg = new DialogueEditorDialog(_doc.Scene, line.TextId, line.TextId, line.TextId, _actor.Number, !_isOoT);
            dlg.ShowDialog(this);
        };
        host.Controls.Add(edit); host.Controls.Add(combo);
        int row = _logicHost.RowCount;
        _logicHost.Controls.Add(lbl, 0, row);
        _logicHost.Controls.Add(host, 1, row);
        _logicHost.RowCount = row + 1;
    }

    // The Hammer Outputs/Inputs view for THIS actor: the flags it sets that others read (outputs) and the
    // flags it reads that others set (inputs). Only ACTUAL connections appear (matching the viewport wires);
    // each row links to the connected actor. Hidden entirely when the actor is wired to nothing.
    private void RebuildConnections()
    {
        if (_ioHost == null || _ioHeader == null) return;
        _ioHost.SuspendLayout();
        DisposeAndClear(_ioHost);

        var (outs, ins) = _doc != null
            ? FlagConnectionAnalyzer.ConnectionsFor(_actor, _doc.AllActors, _isOoT)
            : (new System.Collections.Generic.List<FlagConnectionAnalyzer.IoEndpoint>(),
               new System.Collections.Generic.List<FlagConnectionAnalyzer.IoEndpoint>());

        bool any = outs.Count > 0 || ins.Count > 0;
        _ioHeader.Visible = any;
        _ioHost.Visible = any;
        if (any)
        {
            void SubHead(string t) => _ioHost.Controls.Add(new Label {
                Text = t, AutoSize = true, ForeColor = Color.FromArgb(150, 150, 150),
                Font = UiFonts.Get("Segoe UI", 7.5f, FontStyle.Bold), Margin = new Padding(0, 4, 0, 1) });

            string Flag(FlagConnectionAnalyzer.IoEndpoint e)
                => $"{KindShort(e.Kind)} {e.Index}";
            void Row(string verb, FlagConnectionAnalyzer.IoEndpoint e, Color col)
            {
                var link = new LinkLabel {
                    Text = $"{verb} {Flag(e)}  ->  {ActorNameOf(e.Other)}  ({e.Other.XPos:0},{e.Other.YPos:0},{e.Other.ZPos:0})",
                    AutoSize = true, LinkColor = col, ActiveLinkColor = Color.White, VisitedLinkColor = col,
                    Font = UiFonts.Get("Segoe UI", 8f), Margin = new Padding(2, 0, 0, 0) };
                var other = e.Other;
                link.LinkClicked += (_, _) => GoToActor?.Invoke(other);
                _ioHost.Controls.Add(link);
            }

            if (outs.Count > 0)
            {
                SubHead($"OUTPUTS  -  this sets, read by ({outs.Count})");
                foreach (var e in outs) Row("sets", e, Rendering.SolidRenderer.ConnectionColorRgb(e.Kind));
            }
            if (ins.Count > 0)
            {
                SubHead($"INPUTS  -  this reads, set by ({ins.Count})");
                foreach (var e in ins) Row("reads", e, Rendering.SolidRenderer.ConnectionColorRgb(e.Kind));
            }
        }
        _ioHost.ResumeLayout();
    }

    private static string KindShort(ActorParamSchema.FlagKind k) => k switch
    {
        ActorParamSchema.FlagKind.Switch      => "Switch flag",
        ActorParamSchema.FlagKind.Chest       => "Chest flag",
        ActorParamSchema.FlagKind.Collectible => "Collectible flag",
        ActorParamSchema.FlagKind.Clear       => "Room-clear",
        ActorParamSchema.FlagKind.Event       => "Event flag",
        ActorParamSchema.FlagKind.GoldSkulltula => "GS token",
        _ => "flag",
    };

    private string ActorNameOf(ZActor a)
        => _db.Get(a.Number)?.Name ?? a.DisplayName ?? $"0x{a.Number:X4}";

    // Adds one typed control (dropdown / spinner / checkbox) for a parameter bit-field, wired to
    // write its slice back into the variable and keep the hex box, bit grid and presets in sync.
    // A locked door: En_Door DOOR_LOCKED (type 1), or a Door_Shutter KEY_LOCKED (0xB) / BOSS (0x5).
    private bool IsLockedDoor(ZActor a) => _isOoT && (
        (a.Number == 0x0009 && ((a.Variable >> 7) & 7) == 1) ||
        (a.Number == 0x002E && (((a.Variable >> 6) & 0xF) is 0xB or 0x5)));

    // Editor-only "Lock side" control for a locked door. Vanilla draws the lock on the door's local +Z (its
    // front, set by facing), and a door opens from both sides, so "Back" is exported as a 180° Y flip of the
    // door (see ZActor.LockBack/ExportYRot). Fully vanilla-compatible — just the door faced the other way.
    private void AddLockSideRow()
    {
        var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
            Font = UiFonts.Get("Segoe UI", 8.5f), Margin = new Padding(2) };
        combo.Items.Add("Front (door's facing side)");
        combo.Items.Add("Back (the opposite room)");
        combo.SelectedIndex = _actor.LockBack ? 1 : 0;
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            _actor.LockBack = combo.SelectedIndex == 1;
            Changed?.Invoke();
        };
        _tip.SetToolTip(combo, "Which side the small-key/boss lock faces. Vanilla puts it on the door's front; " +
            "\"Back\" exports the door rotated 180° so the lock is on the other room's side (still vanilla-compatible).");
        int row = _logicHost.RowCount;
        var lbl = new Label { Text = "Lock side", Dock = DockStyle.Fill, ForeColor = FgNormal,
            Font = UiFonts.Get("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(2),
            AutoSize = false, Height = 24 };
        _logicHost.Controls.Add(lbl, 0, row);
        _logicHost.Controls.Add(combo, 1, row);
        _logicHost.RowCount = row + 1;
    }

    private void AddLogicField(ActorParamSchema.Field f)
    {
        Control input;
        switch (f.Kind)
        {
            case ActorParamSchema.FieldKind.Enum:
                var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
                    Font = UiFonts.Get("Segoe UI", 8.5f), Margin = new Padding(2) };
                for (int i = 0; i < (f.Options?.Count ?? 0); i++) combo.Items.Add($"{i}  {f.Options![i]}");
                int cur = f.Get(_actor.Variable);
                combo.SelectedIndex = cur < combo.Items.Count ? cur : -1;
                if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.Text = cur.ToString();
                combo.SelectedIndexChanged += (_, _) => { if (!_loading && combo.SelectedIndex >= 0) ApplyField(f, combo.SelectedIndex); };
                AttachOverflowTip(combo);
                input = combo;
                break;
            case ActorParamSchema.FieldKind.Flag:
                var cb = new CheckBox { Text = "", AutoSize = true, ForeColor = FgNormal, Checked = f.Get(_actor.Variable) != 0,
                    FlatStyle = FlatStyle.Flat, Margin = new Padding(2) };
                cb.CheckedChanged += (_, _) => { if (!_loading) ApplyField(f, cb.Checked ? 1 : 0); };
                input = cb;
                break;
            case ActorParamSchema.FieldKind.Message:
            {
                // Picker of bank messages in this field's textId range + an Edit… button → Message Bank.
                var mcombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
                    Font = UiFonts.Get("Segoe UI", 8.5f), Margin = new Padding(2) };
                int baseId = f.TextIdBase, maxId = f.TextIdBase + f.Mask;
                void ReloadMsgs()
                {
                    bool prev = _loading; _loading = true;
                    mcombo.Items.Clear();
                    var bank = _doc?.Scene.Messages;
                    int curId = f.TextId(_actor.Variable);
                    // Item 8: if this imported actor's message isn't in the editable bank yet, decode it from the
                    // source ROM and add it — so the field shows the real dialogue and the user can edit it.
                    if (bank != null && _doc?.Imported?.RomMessages is { } rmr && bank.All(m => m.Id != curId)
                        && rmr.Read(curId) is { } imported)
                    { imported.IsOverride = false; bank.Add(imported); }   // vanilla text = a reference, not an override
                    if (bank != null)
                        foreach (var m in bank.Where(m => m.Id >= baseId && m.Id <= maxId).OrderBy(m => m.Id))
                            mcombo.Items.Add(new MsgItem(m));
                    int sel = -1;
                    for (int i = 0; i < mcombo.Items.Count; i++)
                        if (mcombo.Items[i] is MsgItem mi2 && mi2.Msg.Id == curId) { sel = i; break; }
                    mcombo.SelectedIndex = sel;
                    if (sel < 0) mcombo.Text = $"textId 0x{curId:X3} (not in bank)";
                    _loading = prev;
                }
                ReloadMsgs();
                AttachOverflowTip(mcombo);
                mcombo.SelectedIndexChanged += (_, _) =>
                { if (!_loading && mcombo.SelectedItem is MsgItem mi) ApplyField(f, mi.Msg.Id - f.TextIdBase); };
                var mhost = new Panel { Dock = DockStyle.Fill, Height = 26, Margin = new Padding(0) };
                var mEdit = new Button { Text = "Edit…", Dock = DockStyle.Right, Width = 52, Height = 24,
                    BackColor = Color.FromArgb(60, 60, 63), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
                    Font = UiFonts.Get("Segoe UI", 8f) };
                // Default = keep the game's vanilla dialogue (grays out editing); Custom = override it with our own.
                var mMode = new ComboBox { Dock = DockStyle.Left, Width = 74, DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Margin = new Padding(0) };
                mMode.Items.AddRange(new object[] { "Default", "Custom" });
                _tip.SetToolTip(mMode, "Default = keep the game's vanilla dialogue for this actor. Custom = override it with your own (enables Edit).");
                MhMessage? MsgAt() => _doc?.Scene.Messages.FirstOrDefault(m => m.Id == f.TextId(_actor.Variable));
                mMode.SelectedIndex = (MsgAt()?.IsOverride ?? false) ? 1 : 0;
                mcombo.Dock = DockStyle.Fill;
                mEdit.Enabled = mMode.SelectedIndex == 1;
                mMode.SelectedIndexChanged += (_, _) =>
                {
                    if (_loading || _doc == null) return;
                    bool custom = mMode.SelectedIndex == 1;
                    int id = f.TextId(_actor.Variable);
                    var bm = _doc.Scene.Messages.FirstOrDefault(m => m.Id == id);
                    if (custom) { if (bm == null) { bm = new MhMessage(id, "New dialogue."); _doc.Scene.Messages.Add(bm); } bm.IsOverride = true; }
                    else if (bm != null) bm.IsOverride = false;
                    mEdit.Enabled = custom;
                    ReloadMsgs();
                };
                _tip.SetToolTip(mEdit, "Author/override this actor's dialogue in the Dialogue Editor");
                mEdit.Click += (_, _) =>
                {
                    if (_doc == null) return;
                    if (mMode.SelectedIndex != 1) mMode.SelectedIndex = 1;   // editing implies Custom
                    using var dlg = new DialogueEditorDialog(_doc.Scene, baseId, maxId, f.TextId(_actor.Variable), _actor.Number, _doc.IsMM);
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        ReloadMsgs();
                        if (dlg.SelectedId is int sid) ApplyField(f, sid - f.TextIdBase);
                    }
                };
                mhost.Controls.Add(mEdit); mhost.Controls.Add(mMode); mhost.Controls.Add(mcombo);
                input = mhost;
                break;
            }
            default: // Int
                var num = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = f.Mask, Value = f.Get(_actor.Variable),
                    BackColor = BgInput, ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle,
                    Font = UiFonts.Get("Consolas", 9f), Margin = new Padding(2) };
                num.ValueChanged += (_, _) => { if (!_loading) ApplyField(f, (int)num.Value); };
                // Flag fields get a channel allocator ("pick a fresh targetname") + the channel's name.
                if (f.Flag != ActorParamSchema.FlagKind.None && _doc != null)
                {
                    var host = new Panel { Dock = DockStyle.Fill, Height = 26, Margin = new Padding(0) };
                    var alloc = new Button { Text = "Free", Dock = DockStyle.Right, Width = 48, Height = 24,
                        BackColor = Color.FromArgb(60, 60, 63), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
                        Font = UiFonts.Get("Segoe UI", 8f) };
                    _tip.SetToolTip(alloc, "Assign the lowest unused flag in this namespace (allocate a new channel)");
                    alloc.Click += (_, _) => num.Value = _doc.NextFreeFlag(f.Flag, _isOoT, f.Mask + 1);
                    host.Controls.Add(num); host.Controls.Add(alloc);
                    input = host;
                    string? cn = _doc.FlagName(f.Flag, f.Get(_actor.Variable));
                    if (cn != null) _tip.SetToolTip(num, $"Channel: “{cn}”");
                }
                else input = num;
                break;
        }

        int row = _logicHost.RowCount;
        var lbl = new Label { Text = f.Name, Dock = DockStyle.Fill, ForeColor = FgNormal,
            Font = UiFonts.Get("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(2),
            AutoSize = false, Height = 24 };
        if (f.Desc != null) _tip.SetToolTip(lbl, f.Desc);
        if (f.Desc != null && input is Control ic) _tip.SetToolTip(ic, f.Desc);
        _logicHost.Controls.Add(lbl, 0, row);
        _logicHost.Controls.Add(input, 1, row);
        _logicHost.RowCount = row + 1;
    }

    private void ApplyField(ActorParamSchema.Field f, int value)
    {
        _actor.Variable = f.Set(_actor.Variable, value);
        _loading = true; _varBox.Text = $"0x{_actor.Variable:X4}"; _loading = false;
        SyncBits();
        SyncPresetSelection();   // does not rebuild logic, avoiding recursion
        Bubble();
    }

    private readonly ToolTip _tip = new();

    private void SyncPresetSelection()
    {
        _loading = true;
        int idx = 0;
        // Exact match first.
        for (int i = 1; i < _presetCombo.Items.Count; i++)
            if (_presetCombo.Items[i] is PresetItem p && p.Var == _actor.Variable) { idx = i; break; }
        // Item 4: imported actors usually carry extra per-instance bits (switch/GS flags, message ids), so an
        // exact match misses. Fall back to matching only the bits the presets actually vary over (their union),
        // so e.g. a coloured tentacle still shows its colour preset even with a flag packed alongside.
        if (idx == 0)
        {
            ushort mask = 0;
            for (int i = 1; i < _presetCombo.Items.Count; i++)
                if (_presetCombo.Items[i] is PresetItem p) mask |= p.Var;
            if (mask != 0)
                for (int i = 1; i < _presetCombo.Items.Count; i++)
                    if (_presetCombo.Items[i] is PresetItem p && p.Var == (ushort)(_actor.Variable & mask)) { idx = i; break; }
        }
        _presetCombo.SelectedIndex = idx;
        SyncBits();
        _loading = false;
    }

    private void Bubble() => Changed?.Invoke();

    // Selects the picker row matching the current id (or clears it for unknown ids).
    private void SyncNamePicker()
    {
        _loading = true;
        int idx = -1;
        for (int i = 0; i < _namePicker.Items.Count; i++)
            if (_namePicker.Items[i] is ActorItem ai && ai.Id == _actor.Number) { idx = i; break; }
        _namePicker.SelectedIndex = idx;
        if (idx < 0) _namePicker.Text = $"0x{_actor.Number:X4} (unknown)";
        _loading = false;
    }

    private sealed record ActorItem(ushort Id, string Name)
    {
        public override string ToString() => $"0x{Id:X4}  {Name}";
    }

    // Builds the 16 checkbox bit-grid (two rows of 8, high bit first). Toggling a box flips that
    // bit in the variable and updates the hex box; the boxes are refreshed by SyncBits().
    private Control BuildBitsGrid()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 2, AutoSize = true,
            BackColor = BgDark, Margin = new Padding(2, 2, 2, 4),
        };
        for (int bit = 15; bit >= 0; bit--)
        {
            int b = bit;
            var cb = new CheckBox
            {
                Text = b.ToString(CultureInfo.InvariantCulture), AutoSize = true, ForeColor = FgNormal,
                Font = UiFonts.Get("Consolas", 7.5f), Margin = new Padding(2, 1, 2, 1),
                FlatStyle = FlatStyle.Flat, Padding = new Padding(0),
            };
            cb.CheckedChanged += (_, _) =>
            {
                if (_loading) return;
                ushort v = _actor.Variable;
                v = (ushort)(cb.Checked ? (v | (1 << b)) : (v & ~(1 << b)));
                _actor.Variable = v;
                _loading = true; _varBox.Text = $"0x{v:X4}"; _loading = false;
                SyncPresetSelection();
                RebuildLogic();
                Bubble();
            };
            _bits[b] = cb;
            int col = (15 - b) % 8, row = (15 - b) / 8;
            grid.Controls.Add(cb, col, row);
        }
        return grid;
    }

    // Refreshes the bit checkboxes from the current variable (without re-triggering handlers).
    private void SyncBits()
    {
        for (int b = 0; b < 16; b++)
            if (_bits[b] is { } cb) cb.Checked = (_actor.Variable & (1 << b)) != 0;
    }

    // ── Row helpers ───────────────────────────────────────────────────────

    // MM HALFDAYBIT spawn-condition editor. The actor id's top bits 0x4000/0x2000 flag that Rot X/Rot Z
    // carry a packed 10-bit half-day mask instead of rotations: halfDaysBits = ((rotX&7)<<7)|(rotZ&0x7F),
    // one bit per (day 0–4 × dawn/night). The engine spawns/kills the actor at each dawn/dusk boundary by
    // this mask (all-zero = always). We expose it as a checkbox grid; enabling it repurposes Rot X & Rot Z.
    private void AddHalfDayEditor(TableLayoutPanel table)
    {
        const int IdBits = 0x6000;   // 0x4000 (rotX packed) | 0x2000 (rotZ packed)
        AddHeader(table, "SPAWN TIME / DAY  (MM 3-day cycle)");
        AddSpan(table, new Label
        {
            Text = "Spawn only on the checked half-days. Enabling this repurposes Rot X & Rot Z (the\n" +
                   "actor keeps its facing via Rot Y). Disabled / all checked = always spawns.",
            Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(400, 0),
            ForeColor = FgNormal, Font = UiFonts.Get("Segoe UI", 8f), Margin = new Padding(2, 2, 2, 2),
        });

        var enable = new CheckBox { Text = "Time-gated spawn", AutoSize = true, ForeColor = FgNormal,
            Checked = (_actor.IdFlags & IdBits) == IdBits, Margin = new Padding(2, 2, 2, 2) };
        AddSpan(table, enable);

        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill,
            BackColor = BgDark, Margin = new Padding(12, 0, 0, 0) };
        grid.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 0);
        grid.Controls.Add(new Label { Text = "Dawn", AutoSize = true, ForeColor = HdrFg, Font = UiFonts.Get("Segoe UI", 8f) }, 1, 0);
        grid.Controls.Add(new Label { Text = "Night", AutoSize = true, ForeColor = HdrFg, Font = UiFonts.Get("Segoe UI", 8f) }, 2, 0);

        string[] dayNames = ["Day 0 (arrival)", "Day 1", "Day 2", "Day 3", "Day 4 (final)"];
        var dawn = new CheckBox[5]; var night = new CheckBox[5];
        int curMask = ((_actor.XRot & 7) << 7) | (_actor.ZRot & 0x7F);
        for (int d = 0; d < 5; d++)
        {
            grid.Controls.Add(new Label { Text = dayNames[d], AutoSize = true, ForeColor = FgNormal,
                Font = UiFonts.Get("Segoe UI", 8f), Margin = new Padding(0, 4, 6, 0) }, 0, d + 1);
            dawn[d]  = new CheckBox { AutoSize = true, Checked = (curMask & (1 << (9 - 2 * d))) != 0 };
            night[d] = new CheckBox { AutoSize = true, Checked = (curMask & (1 << (8 - 2 * d))) != 0 };
            grid.Controls.Add(dawn[d], 1, d + 1);
            grid.Controls.Add(night[d], 2, d + 1);
        }
        AddSpan(table, grid);

        void Apply()
        {
            if (_loading) return;
            if (!enable.Checked) { _actor.IdFlags = (ushort)(_actor.IdFlags & ~IdBits); grid.Enabled = false; Bubble(); return; }
            grid.Enabled = true;
            int mask = 0;
            for (int d = 0; d < 5; d++)
            {
                if (dawn[d].Checked)  mask |= 1 << (9 - 2 * d);
                if (night[d].Checked) mask |= 1 << (8 - 2 * d);
            }
            _actor.XRot = (short)((_actor.XRot & ~7) | ((mask >> 7) & 7));
            _actor.ZRot = (short)((_actor.ZRot & ~0x7F) | (mask & 0x7F));
            _actor.IdFlags = (ushort)(_actor.IdFlags | IdBits);
            Bubble();
        }
        enable.CheckedChanged += (_, _) => Apply();
        foreach (var cb in dawn.Concat(night)) cb.CheckedChanged += (_, _) => Apply();
        grid.Enabled = enable.Checked;

        // Optional cutscene link (csId, packed into Rot Y with id bit 0x8000). 0 = none. Repurposes the
        // facing field, so it's separate from the half-day mask.
        var csHost = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(2, 6, 2, 2) };
        csHost.Controls.Add(new Label { Text = "Cutscene link (csId, 0 = none, repurposes Rot Y):",
            AutoSize = true, ForeColor = FgNormal, Font = UiFonts.Get("Segoe UI", 8f), Margin = new Padding(0, 6, 6, 0) });
        var csNum = new NumericUpDown { Width = 60, Minimum = 0, Maximum = 0x7F,
            Value = (_actor.IdFlags & 0x8000) != 0 ? (_actor.YRot & 0x7F) : 0,
            BackColor = BgInput, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        csNum.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            int cs = (int)csNum.Value;
            if (cs == 0) _actor.IdFlags = (ushort)(_actor.IdFlags & ~0x8000);
            else { _actor.IdFlags = (ushort)(_actor.IdFlags | 0x8000); _actor.YRot = (short)cs; }
            Bubble();
        };
        csHost.Controls.Add(csNum);
        AddSpan(table, csHost);

        // NPC schedule (custom-engine convention): be at a position by time/day. Applied by the 2Ship fork.
        var schedBtn = new Button { Text = "NPC Schedule…", AutoSize = true, Margin = new Padding(2, 4, 2, 2),
            BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 8.5f) };
        void RefreshSchedLabel() => schedBtn.Text = (_actor.Schedule?.Count ?? 0) is var n && n > 0 ? $"NPC Schedule… ({n} rule{(n == 1 ? "" : "s")})" : "NPC Schedule…";
        RefreshSchedLabel();
        schedBtn.Click += (_, _) => { using var dlg = new ScheduleDialog(_actor); if (dlg.ShowDialog(this) == DialogResult.OK) { RefreshSchedLabel(); Bubble(); } };
        AddSpan(table, schedBtn);
    }

    private void AddInt(TableLayoutPanel t, string label, Func<int> get, Action<int> set)
    {
        var box = MakeBox(get().ToString(CultureInfo.InvariantCulture));
        box.TextChanged += (_, _) =>
        {
            if (_loading) return;
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) set(v);
        };
        AddRow(t, label, box);
    }

    private static void AddHeader(TableLayoutPanel t, string text)
    {
        var l = new Label { Text = text, Dock = DockStyle.Fill, Height = 20, Margin = new Padding(0, 8, 0, 2),
            ForeColor = HdrFg, Font = UiFonts.Get("Segoe UI", 7.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        AddSpan(t, l);
    }

    private static void AddSpan(TableLayoutPanel t, Control c)
    {
        int row = t.RowCount;
        t.Controls.Add(c, 0, row);
        t.SetColumnSpan(c, 2);
        t.RowCount = row + 1;
    }

    private static void AddRow(TableLayoutPanel t, string label, Control control)
    {
        int row = t.RowCount;
        t.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, ForeColor = FgNormal,
            Font = UiFonts.Get("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(2) }, 0, row);
        t.Controls.Add(control, 1, row);
        t.RowCount = row + 1;
    }

    private static TextBox MakeBox(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, BackColor = BgInput, ForeColor = FgNormal,
        BorderStyle = BorderStyle.FixedSingle, Font = UiFonts.Get("Consolas", 9f), Margin = new Padding(2),
    };

    private static bool TryHex(string s, out int v)
    {
        s = s.Trim(); if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    private sealed record PresetItem(ushort Var, string Desc)
    {
        public override string ToString() => Var == 0xFFFF ? Desc : $"0x{Var:X4}  {Desc}";
    }

    private sealed record MsgItem(MhMessage Msg)
    {
        public override string ToString() => $"0x{Msg.Id:X3}  {Msg.Preview()}";
    }
}
