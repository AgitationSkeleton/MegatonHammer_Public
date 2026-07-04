using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

/// <summary>
/// Hammer's "Entity Report": a filterable list of every entity in the map. Filter by class (actor
/// type) or by a name/targetname substring, then Go to (select + centre views), edit Properties, or
/// Delete. Point entities are actors; "brush entities" are trigger volumes. Modeless so it can stay
/// open while you work. The host wires <see cref="GoToRequested"/> / <see cref="PropertiesRequested"/>.
/// </summary>
public sealed class EntityReportDialog : Form
{
    private static readonly Color BgDark = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);

    private readonly MapDocument _doc;
    private readonly ActorDatabase? _actorDb;
    private readonly ListBox _list = new();
    private readonly RadioButton _everything, _pointEnts, _brushEnts;
    private readonly CheckBox _byClass, _byName;
    private readonly ComboBox _classBox;
    private readonly TextBox _nameBox;
    private readonly List<object> _items = [];   // ZActor or Solid (trigger), parallel to _list

    /// <summary>Raised when the user double-clicks or hits "Go to": select + centre on this entity.</summary>
    public event Action<object>? GoToRequested;
    /// <summary>Raised on "Properties": edit this actor.</summary>
    public event Action<ZActor>? PropertiesRequested;

    public EntityReportDialog(MapDocument doc, ActorDatabase? actorDb = null)
    {
        _doc = doc;
        _actorDb = actorDb;

        Text = "Entity Report";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(500, 460);
        BackColor = BgDark; ForeColor = FgNormal;
        Font = new Font("Segoe UI", 8.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Controls.Add(new Label { Text = "Entities:", Left = 12, Top = 10, Width = 200, ForeColor = FgNormal });

        _list.SetBounds(12, 30, 370, 260);
        _list.BackColor = BgInput; _list.ForeColor = FgNormal; _list.BorderStyle = BorderStyle.FixedSingle;
        _list.Font = new Font("Consolas", 9f);
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        // SDK2013 Hammer: shift/ctrl multi-select + Delete key to remove the whole selection.
        _list.SelectionMode = SelectionMode.MultiExtended;
        _list.DoubleClick += (_, _) => GoTo();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) { DeleteSel(); e.Handled = true; } };
        Controls.Add(_list);

        var goBtn   = Btn("Go to",      398, 30,  () => GoTo());
        var delBtn  = Btn("Delete",     398, 62,  () => DeleteSel());
        var propBtn = Btn("Properties", 398, 94,  () => Props());
        foreach (var b in new[] { goBtn, delBtn, propBtn }) b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.AddRange([goBtn, delBtn, propBtn]);

        // ── Filters ──────────────────────────────────────────────────────────
        int fy = 300;
        _everything = Radio("Everything", 14, fy, true);
        _pointEnts  = Radio("Point entities (actors)", 14, fy + 24, false);
        _brushEnts  = Radio("Brush entities (triggers)", 14, fy + 48, false);
        foreach (var r in new[] { _everything, _pointEnts, _brushEnts }) { r.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; r.CheckedChanged += (_, _) => Refresh2(); Controls.Add(r); }

        _byName = new CheckBox { Text = "By name:", Left = 250, Top = fy, Width = 80, ForeColor = FgNormal, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        _nameBox = new TextBox { Left = 334, Top = fy - 2, Width = 150, BackColor = BgInput, ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        _byName.CheckedChanged += (_, _) => Refresh2(); _nameBox.TextChanged += (_, _) => { if (_byName.Checked) Refresh2(); };
        Controls.Add(_byName); Controls.Add(_nameBox);

        _byClass = new CheckBox { Text = "By class:", Left = 250, Top = fy + 26, Width = 80, ForeColor = FgNormal, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        _classBox = new ComboBox { Left = 334, Top = fy + 24, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        _byClass.CheckedChanged += (_, _) => Refresh2(); _classBox.SelectedIndexChanged += (_, _) => { if (_byClass.Checked) Refresh2(); };
        Controls.Add(_byClass); Controls.Add(_classBox);

        // Modeless form: DialogResult does nothing here, so close explicitly. Esc also closes via the
        // KeyPreview handler below.
        var close = new Button { Text = "Close", Width = 80, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        close.Location = new Point(ClientSize.Width - 92, ClientSize.Height - 34);
        close.BackColor = Color.FromArgb(60, 60, 65); close.ForeColor = FgNormal; close.FlatStyle = FlatStyle.Flat;
        close.Click += (_, _) => Close();
        Controls.Add(close);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        PopulateClasses();
        Refresh2();
    }

    // Friendly class name for an actor — resolved from the actor-name database by id (the stored
    // DisplayName isn't serialized, so it reads "Unknown" after a project reload).
    private string ClassName(ZActor a)
    {
        string? n = _actorDb?.GetName(a.Number);
        if (!string.IsNullOrEmpty(n) && !n.StartsWith("Actor_", StringComparison.Ordinal)) return n;
        if (a.DisplayName is { Length: > 0 } and not "Unknown") return a.DisplayName;
        return n ?? $"Actor 0x{a.Number:X4}";
    }

    private void PopulateClasses()
    {
        _classBox.Items.Clear();
        foreach (var name in _doc.AllActors.Select(ClassName).Distinct().OrderBy(s => s))
            _classBox.Items.Add(name);
        if (_classBox.Items.Count > 0) _classBox.SelectedIndex = 0;
    }

    /// <summary>Rebuilds the entity list from the current filters.</summary>
    public void Refresh2()
    {
        _items.Clear();
        _list.BeginUpdate();
        _list.Items.Clear();

        bool wantPoints = _everything.Checked || _pointEnts.Checked;
        bool wantBrush  = _everything.Checked || _brushEnts.Checked;
        string nameFilter = _byName.Checked ? _nameBox.Text.Trim() : "";
        string? classFilter = _byClass.Checked ? _classBox.SelectedItem as string : null;

        if (wantPoints)
            foreach (var a in _doc.AllActors)
            {
                string cls = ClassName(a);
                if (classFilter != null && cls != classFilter) continue;
                string label = $"{cls}  (id 0x{a.Number:X4}, var 0x{a.Variable:X4})";
                if (nameFilter.Length > 0 && !label.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) continue;
                _items.Add(a); _list.Items.Add(label);
            }

        if (wantBrush)
            foreach (var s in _doc.Solids.Where(s => s.IsTrigger))
            {
                string label = $"trigger  (entrance {s.ExitEntrance})";
                if (classFilter != null) continue;   // triggers have no class
                if (nameFilter.Length > 0 && !label.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) continue;
                _items.Add(s); _list.Items.Add(label);
            }

        _list.EndUpdate();
        Text = $"Entity Report — {_list.Items.Count} entit{(_list.Items.Count == 1 ? "y" : "ies")}";
    }

    private object? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _items.Count ? _items[_list.SelectedIndex] : null;

    private void GoTo() { if (Selected is { } o) GoToRequested?.Invoke(o); }
    private void Props() { if (Selected is ZActor a) PropertiesRequested?.Invoke(a); }

    private void DeleteSel()
    {
        // All shift/ctrl-selected rows (SDK2013 Hammer): collect the entities first (indices shift as
        // we remove), record ONE undo, then delete actors + trigger solids.
        var sel = _list.SelectedIndices.Cast<int>()
            .Where(i => i >= 0 && i < _items.Count).Select(i => _items[i]).ToList();
        if (sel.Count == 0) return;
        _doc.RecordUndo();
        foreach (var o in sel)
        {
            if (o is ZActor a) foreach (var r in _doc.Scene.Rooms) r.Actors.Remove(a);
            else if (o is Solid s) _doc.Remove(s);
        }
        _doc.NotifyChanged();
        PopulateClasses();
        Refresh2();
    }

    private Button Btn(string text, int x, int y, Action onClick)
    {
        var b = new Button { Text = text, Left = x, Top = y, Width = 90, Height = 26,
                             BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static RadioButton Radio(string t, int x, int y, bool chk) => new()
    { Text = t, Left = x, Top = y, Width = 230, Checked = chk, ForeColor = FgNormal };
}
