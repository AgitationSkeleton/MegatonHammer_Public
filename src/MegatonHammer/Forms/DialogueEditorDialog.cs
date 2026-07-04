using System.Linq;
using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

/// <summary>
/// The friendly Dialogue Editor (the "wire it visually" front-end): edit a conversation as a list of message
/// boxes, each a plain Message or a two-choice Prompt, with colour/timing/sound, a gesture for the speaking
/// actor, per-choice outcomes (branch / give item / charge rupees / set flag) and an "already fulfilled"
/// fallback. Presentation lowers to standard vanilla message bytes; outcomes are carried in the project +
/// exported for the portable talk actor (fork-independent). Opened from an actor's Message field.
/// </summary>
public sealed class DialogueEditorDialog : Form
{
    private static readonly Color BgDark = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);
    private static readonly Color Accent = Color.FromArgb(140, 190, 255);

    private readonly ZScene _scene;
    private readonly int _baseId, _maxId;
    private readonly ListBox _list;
    private readonly Label _idLabel;
    private readonly TextBox _text, _choice1, _choice2;
    private readonly RadioButton _kMsg, _kPrompt;
    private readonly NumericUpDown _doneFlag;
    private readonly ComboBox _gestureCombo, _sfxCombo, _afterCombo;
    private readonly NpcGestures.Gesture[] _gestures;
    private readonly string[] _itemNames;
    private readonly OutcomeControls _o1, _o2;
    private readonly Label _o1Head, _o2Head;
    private bool _loading;

    public int? SelectedId { get; private set; }

    public DialogueEditorDialog(ZScene scene, int baseId, int maxId, int currentId, ushort actorId = 0, bool isMM = false)
    {
        _scene = scene; _baseId = baseId; _maxId = maxId;
        _itemNames = isMM ? GetItemTable.MM : GetItemTable.OoT;
        _gestures = NpcGestures.For(isMM, actorId) ?? NpcGestures.Generic();
        Text = "Dialogue Editor";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(700, 574);
        MinimumSize = new Size(716, 612);
        AutoScroll = true;   // if a control ever exceeds the client area, it stays reachable
        BackColor = BgDark; ForeColor = FgNormal;
        Font = new Font("Segoe UI", 8.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // ── Left: the box list ──
        _list = new ListBox { Left = 12, Top = 12, Width = 176, Height = 470, BackColor = BgInput,
            ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
        _list.SelectedIndexChanged += (_, _) => LoadSelected();
        Controls.Add(_list);
        var add = Btn("Add Message", 12, 488, 108); add.Click += (_, _) => AddNew();
        var del = Btn("Del", 124, 488, 64);        del.Click += (_, _) => DeleteSelected();
        Controls.Add(add); Controls.Add(del);

        int rx = 202;   // right column origin
        _idLabel = new Label { Left = rx, Top = 12, Width = 470, Height = 18, ForeColor = Accent,
            Font = new Font("Consolas", 9f, FontStyle.Bold), Text = "(no message)" };
        Controls.Add(_idLabel);

        // ── Colour / Timing / structure toolbar (inserts markup) ──
        Controls.Add(Lab("Colour:", rx, 36, 46));
        int cx = rx + 50;
        foreach (var (mk, col, name) in new[] {
            ("%r", Color.FromArgb(230,90,90), "red"), ("%g", Color.FromArgb(90,200,110), "green"),
            ("%b", Color.FromArgb(110,160,255), "blue"), ("%y", Color.FromArgb(230,210,90), "yellow"),
            ("%p", Color.FromArgb(200,140,230), "purple"), ("%w", Color.FromArgb(230,230,230), "white") })
        { var b = MarkBtn(name[..1].ToUpper(), cx, 34, col, mk); Controls.Add(b); cx += 26; }
        var bBox = MarkBtn("Box", cx + 6, 34, FgNormal, "^"); bBox.Width = 40; Controls.Add(bBox);
        Controls.Add(Lab("Timing:", rx, 60, 48));
        var bSlow = MarkBtn("slow ~2", rx + 52, 58, FgNormal, "~2"); bSlow.Width = 60;
        var bFast = MarkBtn("fast ~0", rx + 116, 58, FgNormal, "~0"); bFast.Width = 60;
        Controls.Add(bSlow); Controls.Add(bFast);

        // ── Message text ──
        _text = new TextBox { Left = rx, Top = 84, Width = 470, Height = 96, Multiline = true,
            ScrollBars = ScrollBars.Vertical, BackColor = BgInput, ForeColor = FgNormal,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9.5f) };
        _text.TextChanged += (_, _) => { if (!_loading && Cur is { } m) m.Text = _text.Text; };
        Controls.Add(_text);

        // ── Kind: Message vs Prompt ──
        _kMsg    = Radio("Message", rx, 186);
        _kPrompt = Radio("Prompt (Yes/No)", rx + 90, 186);
        _kMsg.CheckedChanged    += (_, _) => { if (!_loading && _kMsg.Checked && Cur is { } m)    { m.Kind = MhMsgKind.Message; RefreshEnabled(); } };
        _kPrompt.CheckedChanged += (_, _) => { if (!_loading && _kPrompt.Checked && Cur is { } m) { m.Kind = MhMsgKind.Prompt;  RefreshEnabled(); } };
        Controls.Add(_kMsg); Controls.Add(_kPrompt);

        _choice1 = SmallText(rx + 60, 208, 120); _choice1.TextChanged += (_, _) => { if (!_loading && Cur is { } m) m.Choice1 = _choice1.Text; };
        _choice2 = SmallText(rx + 250, 208, 120); _choice2.TextChanged += (_, _) => { if (!_loading && Cur is { } m) m.Choice2 = _choice2.Text; };
        Controls.Add(Lab("Choice 1:", rx, 210, 56)); Controls.Add(_choice1);
        Controls.Add(Lab("Choice 2:", rx + 192, 210, 56)); Controls.Add(_choice2);

        // ── Sound + Gesture ──
        Controls.Add(Lab("Sound:", rx, 236, 44));
        _sfxCombo = new ComboBox { Left = rx + 46, Top = 234, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, DropDownWidth = 200 };
        foreach (var s in SfxNames.Common) _sfxCombo.Items.Add(s);
        _sfxCombo.Items.Add(new SfxNames.Sfx(-2, "Custom id…"));   // enter a raw sound id not in the list
        _sfxCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || Cur is not { } m || _sfxCombo.SelectedItem is not SfxNames.Sfx s) return;
            if (s.Id == -2)   // "Custom id…"
            {
                int id = PromptHex("Custom sound id (hex, e.g. 6836):", m.Sfx);
                _loading = true; SelectSfx(id >= 0 ? id : m.Sfx); _loading = false;
                if (id >= 0) m.Sfx = id;
            }
            else m.Sfx = s.Id;
        };
        Controls.Add(_sfxCombo);
        Controls.Add(Lab("Gesture:", rx + 250, 236, 52));
        _gestureCombo = new ComboBox { Left = rx + 302, Top = 234, Width = 126, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        foreach (var g in _gestures) _gestureCombo.Items.Add(g);
        _gestureCombo.SelectedIndexChanged += (_, _) =>
        { if (!_loading && Cur is { } m && _gestureCombo.SelectedItem is NpcGestures.Gesture g) m.Gesture = g.Index; };
        Controls.Add(_gestureCombo);

        Controls.Add(Sep(rx, 262, 470));

        // ── Outcomes (Option 1 / Option 2) ──
        _o1Head = new Label { Left = rx, Top = 270, Width = 220, ForeColor = Accent, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Text = "On advance:" };
        _o2Head = new Label { Left = rx + 236, Top = 270, Width = 220, ForeColor = Accent, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Text = "Option 2 (No):" };
        Controls.Add(_o1Head); Controls.Add(_o2Head);
        _o1 = new OutcomeControls(this, rx, 292, () => Cur?.Outcome1, () => _loading, _itemNames, () => BoxList("(end / close)"));
        _o2 = new OutcomeControls(this, rx + 236, 292, () => Cur?.Outcome2, () => _loading, _itemNames, () => BoxList("(end / close)"));

        // ── Fulfilled state ──
        Controls.Add(Sep(rx, 430, 470));
        Controls.Add(Lab("Once done — Trigger flag set:", rx, 440, 168));
        _doneFlag = Spin(rx + 172, 438, -1, 4095, 70);
        _doneFlag.ValueChanged += (_, _) => { if (!_loading && Cur is { } m) m.DoneFlag = (int)_doneFlag.Value; };
        Controls.Add(_doneFlag);
        Controls.Add(Lab("show:", rx + 250, 440, 40));
        _afterCombo = new ComboBox { Left = rx + 292, Top = 438, Width = 178, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, DropDownWidth = 260 };
        _afterCombo.SelectedIndexChanged += (_, _) => { if (!_loading && Cur is { } m && _afterCombo.SelectedItem is BoxRef b) m.AfterMsgId = b.Id; };
        Controls.Add(_afterCombo);

        // ── Save / Close ──
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(516, 488), Width = 76,
            Height = 28, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(598, 488), Width = 76,
            Height = 28, BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        ok.Click += (_, _) => { if (Cur is { } m) SelectedId = m.Id; };
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;

        Reload(currentId);
    }

    private MhMessage? Cur => _list.SelectedItem as MhMessage;

    // A picker entry referencing one message box (or "none/end" at id -1).
    private sealed class BoxRef { public int Id; public string Label = ""; public override string ToString() => Label; }

    // The conversation's boxes (in this field's textId range) as picker entries, "firstLabel" as the id=-1 option.
    private List<BoxRef> BoxList(string firstLabel)
    {
        var list = new List<BoxRef> { new() { Id = -1, Label = firstLabel } };
        foreach (var m in _scene.Messages.Where(m => m.Id >= _baseId && m.Id <= _maxId).OrderBy(m => m.Id))
            list.Add(new BoxRef { Id = m.Id, Label = $"0x{m.Id:X4}  {m.Preview()}" });
        return list;
    }

    private static void FillBoxCombo(ComboBox c, List<BoxRef> boxes, int selId)
    {
        c.Items.Clear();
        foreach (var b in boxes) c.Items.Add(b);
        int idx = 0; for (int i = 0; i < boxes.Count; i++) if (boxes[i].Id == selId) { idx = i; break; }
        c.SelectedIndex = boxes.Count > 0 ? idx : -1;
    }

    // Select the SFX combo entry for id (adding a "Custom 0x.." row before the "Custom id…" sentinel if needed).
    private void SelectSfx(int id)
    {
        int si = -1;
        for (int i = 0; i < _sfxCombo.Items.Count; i++)
            if (_sfxCombo.Items[i] is SfxNames.Sfx s && s.Id == id) { si = i; break; }
        if (si < 0 && id >= 0)
        {
            _sfxCombo.Items.Insert(_sfxCombo.Items.Count - 1, new SfxNames.Sfx(id, $"Custom 0x{id:X4}"));
            si = _sfxCombo.Items.Count - 2;
        }
        _sfxCombo.SelectedIndex = si >= 0 ? si : 0;
    }

    // Tiny modal hex-id prompt. Returns the entered value, or -1 on cancel / bad input.
    private int PromptHex(string prompt, int current)
    {
        using var f = new Form { Text = "Sound id", FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(252, 98), MinimizeBox = false,
            MaximizeBox = false, BackColor = BgDark, ForeColor = FgNormal };
        var lbl = new Label { Text = prompt, Left = 12, Top = 12, Width = 232, ForeColor = FgNormal };
        var tb  = new TextBox { Left = 12, Top = 34, Width = 228, BackColor = BgInput, ForeColor = FgNormal,
            Font = new Font("Consolas", 9.5f), Text = current >= 0 ? current.ToString("X") : "" };
        var ok  = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 84, Top = 64, Width = 70,
            BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var no  = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 162, Top = 64, Width = 78,
            BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        f.Controls.AddRange(new Control[] { lbl, tb, ok, no }); f.AcceptButton = ok; f.CancelButton = no;
        if (f.ShowDialog(this) != DialogResult.OK) return -1;
        var t = tb.Text.Trim(); if (t.StartsWith("0x")) t = t[2..];
        return int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out int v) && v >= 0 ? v : -1;
    }

    // Insert markup at the text caret.
    private void Insert(string mk)
    {
        if (!_text.Enabled) return;
        int p = _text.SelectionStart;
        _text.Text = _text.Text.Insert(p, mk);
        _text.SelectionStart = p + mk.Length;
        _text.Focus();
    }

    private void Reload(int selectId)
    {
        _loading = true;
        _list.Items.Clear();
        foreach (var m in _scene.Messages.Where(m => m.Id >= _baseId && m.Id <= _maxId).OrderBy(m => m.Id))
            _list.Items.Add(m);
        int sel = -1;
        for (int i = 0; i < _list.Items.Count; i++) if (_list.Items[i] is MhMessage mm && mm.Id == selectId) { sel = i; break; }
        _loading = false;
        _list.SelectedIndex = sel >= 0 ? sel : (_list.Items.Count > 0 ? 0 : -1);
        LoadSelected();
    }

    private void LoadSelected()
    {
        _loading = true;
        if (Cur is { } m)
        {
            _idLabel.Text = $"MsgBox  textId 0x{m.Id:X3}   (range 0x{_baseId:X3}-0x{_maxId:X3})";
            _text.Text = m.Text;
            _kMsg.Checked = m.Kind == MhMsgKind.Message; _kPrompt.Checked = m.Kind == MhMsgKind.Prompt;
            _choice1.Text = m.Choice1; _choice2.Text = m.Choice2;
            SelectSfx(m.Sfx);
            int gi = 0; for (int i = 0; i < _gestures.Length; i++) if (_gestures[i].Index == m.Gesture) { gi = i; break; }
            _gestureCombo.SelectedIndex = gi;
            _doneFlag.Value = Math.Clamp(m.DoneFlag, -1, 4095);
            FillBoxCombo(_afterCombo, BoxList("(none)"), m.AfterMsgId);
            _o1.Load(m.Outcome1); _o2.Load(m.Outcome2);
        }
        _loading = false;
        RefreshEnabled();
    }

    private void RefreshEnabled()
    {
        bool has = Cur != null, prompt = has && Cur!.Kind == MhMsgKind.Prompt;
        _text.Enabled = _kMsg.Enabled = _kPrompt.Enabled = _sfxCombo.Enabled = _gestureCombo.Enabled =
            _doneFlag.Enabled = _afterCombo.Enabled = has;
        _choice1.Enabled = _choice2.Enabled = prompt;
        _o1Head.Text = prompt ? "Option 1 (Yes):" : "On advance:";
        _o2Head.Text = "Option 2 (No):";
        _o2.SetEnabled(prompt);
        _o1.SetEnabled(has);
    }

    private void AddNew()
    {
        int id = _scene.NextFreeMessageId(_baseId, _maxId - _baseId);
        _scene.Messages.Add(new MhMessage(id, "New message."));
        Reload(id);
        _text.Focus(); _text.SelectAll();
    }

    private void DeleteSelected() { if (Cur is { } m) { _scene.Messages.Remove(m); Reload(-1); } }

    // ── control factories ──
    private static Button Btn(string t, int x, int y, int w) => new()
    { Text = t, Left = x, Top = y, Width = w, Height = 26, BackColor = Color.FromArgb(60, 60, 63),
      ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8f) };
    private Button MarkBtn(string t, int x, int y, Color fg, string markup)
    {
        var b = new Button { Text = t, Left = x, Top = y, Width = 24, Height = 22, BackColor = Color.FromArgb(50, 50, 53),
            ForeColor = fg, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8f, FontStyle.Bold), Margin = new Padding(0) };
        b.Click += (_, _) => Insert(markup);
        return b;
    }
    private static Label Lab(string t, int x, int y, int w) => new() { Text = t, Left = x, Top = y, Width = w, ForeColor = FgNormal };
    private RadioButton Radio(string t, int x, int y) => new() { Text = t, Left = x, Top = y, Width = 130, ForeColor = FgNormal, AutoSize = true };
    private TextBox SmallText(int x, int y, int w) => new() { Left = x, Top = y, Width = w, Height = 20, BackColor = BgInput,
        ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9f) };
    internal static NumericUpDown Spin(int x, int y, int min, int max, int w) => new()
    { Location = new Point(x, y), Width = w, Minimum = min, Maximum = max, BackColor = BgInput,
      ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle };
    private static Panel Sep(int x, int y, int w) => new() { Left = x, Top = y, Width = w, Height = 1, BackColor = Color.FromArgb(64, 64, 66) };

    internal void Add(Control c) => Controls.Add(c);
    internal static Label OLab(string t, int x, int y, int w) => new() { Text = t, Left = x, Top = y, Width = w, ForeColor = FgNormal };

    /// <summary>The four outcome fields for one option column (branch / flag / item / charge rupees).</summary>
    private sealed class OutcomeControls
    {
        private readonly NumericUpDown _flag, _cost;
        private readonly ComboBox _item, _next;
        private readonly CheckBox _charge;
        private readonly int _maxItem;
        private readonly System.Func<MhOutcome?> _get;
        private readonly System.Func<bool> _loading;
        private readonly System.Func<List<BoxRef>> _boxes;

        public OutcomeControls(DialogueEditorDialog dlg, int x, int y, System.Func<MhOutcome?> get, System.Func<bool> loading,
                               string[] itemNames, System.Func<List<BoxRef>> boxes)
        {
            _get = get; _loading = loading; _maxItem = itemNames.Length - 1; _boxes = boxes;
            dlg.Add(OLab("Go to box:", x, y, 62));
            _next = new ComboBox { Left = x + 64, Top = y - 2, Width = 152, DropDownWidth = 240, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
            _next.SelectedIndexChanged += (_, _) => { if (!_loading() && _get() is { } o && _next.SelectedItem is BoxRef b) o.NextMsgId = b.Id; };
            dlg.Add(_next);
            dlg.Add(OLab("Fire Trigger #:", x, y + 26, 96));
            _flag = Spin(x + 100, y + 24, -1, 4095, 80);
            _flag.ValueChanged += (_, _) => { if (!_loading() && _get() is { } o) o.FireFlag = (int)_flag.Value; };
            dlg.Add(_flag);
            dlg.Add(OLab("Give Item:", x, y + 52, 56));
            _item = new ComboBox { Left = x + 58, Top = y + 50, Width = 158, DropDownWidth = 240, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
            _item.Items.AddRange(itemNames);   // index 0 = "None (empty)"
            _item.SelectedIndexChanged += (_, _) => { if (!_loading() && _get() is { } o) o.GiveItem = _item.SelectedIndex <= 0 ? -1 : _item.SelectedIndex; };
            dlg.Add(_item);
            _charge = new CheckBox { Text = "Charge rupees:", Left = x, Top = y + 78, Width = 108, ForeColor = FgNormal, AutoSize = true };
            _charge.CheckedChanged += (_, _) => { if (!_loading() && _get() is { } o) { o.ChargeRupees = _charge.Checked; _cost!.Enabled = _charge.Checked; } };
            dlg.Add(_charge);
            _cost = Spin(x + 112, y + 76, 0, 9999, 66);
            _cost.ValueChanged += (_, _) => { if (!_loading() && _get() is { } o) o.RupeeCost = (int)_cost.Value; };
            dlg.Add(_cost);
        }

        public void Load(MhOutcome o)
        {
            FillBoxCombo(_next, _boxes(), o.NextMsgId);
            _flag.Value = Math.Clamp(o.FireFlag, -1, 4095);
            _item.SelectedIndex = o.GiveItem <= 0 ? 0 : Math.Min(o.GiveItem, _maxItem);
            _charge.Checked = o.ChargeRupees; _cost.Value = Math.Clamp(o.RupeeCost, 0, 9999); _cost.Enabled = o.ChargeRupees;
        }

        public void SetEnabled(bool on)
        { _next.Enabled = _flag.Enabled = _item.Enabled = _charge.Enabled = on; _cost.Enabled = on && _charge.Checked; }
    }
}
