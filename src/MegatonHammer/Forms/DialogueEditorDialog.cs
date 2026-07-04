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
    private readonly NumericUpDown _sfx, _gesture, _doneFlag, _afterMsg;
    private readonly OutcomeControls _o1, _o2;
    private readonly Label _o1Head, _o2Head;
    private bool _loading;

    public int? SelectedId { get; private set; }

    public DialogueEditorDialog(ZScene scene, int baseId, int maxId, int currentId)
    {
        _scene = scene; _baseId = baseId; _maxId = maxId;
        Text = "Dialogue Editor";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(690, 566);
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
        Controls.Add(Lab("Sound (SFX id, -1 none):", rx, 236, 138));
        _sfx = Spin(rx + 142, 234, -1, 0xFFFF, 96); _sfx.Hexadecimal = true;
        _sfx.ValueChanged += (_, _) => { if (!_loading && Cur is { } m) m.Sfx = (int)_sfx.Value; };
        Controls.Add(_sfx);
        Controls.Add(Lab("Gesture (-1 default):", rx + 250, 236, 116));
        _gesture = Spin(rx + 368, 234, -1, 31, 60);
        _gesture.ValueChanged += (_, _) => { if (!_loading && Cur is { } m) m.Gesture = (int)_gesture.Value; };
        Controls.Add(_gesture);

        Controls.Add(Sep(rx, 262, 470));

        // ── Outcomes (Option 1 / Option 2) ──
        _o1Head = new Label { Left = rx, Top = 270, Width = 220, ForeColor = Accent, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Text = "On advance:" };
        _o2Head = new Label { Left = rx + 236, Top = 270, Width = 220, ForeColor = Accent, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Text = "Option 2 (No):" };
        Controls.Add(_o1Head); Controls.Add(_o2Head);
        _o1 = new OutcomeControls(this, rx, 292, () => Cur?.Outcome1, () => _loading);
        _o2 = new OutcomeControls(this, rx + 236, 292, () => Cur?.Outcome2, () => _loading);

        // ── Fulfilled state ──
        Controls.Add(Sep(rx, 430, 470));
        Controls.Add(Lab("Once done — Trigger flag set:", rx, 440, 168));
        _doneFlag = Spin(rx + 172, 438, -1, 4095, 70);
        _doneFlag.ValueChanged += (_, _) => { if (!_loading && Cur is { } m) m.DoneFlag = (int)_doneFlag.Value; };
        Controls.Add(_doneFlag);
        Controls.Add(Lab("show message #:", rx + 250, 440, 100));
        _afterMsg = Spin(rx + 352, 438, -1, 0xFFFF, 86); _afterMsg.Hexadecimal = true;
        _afterMsg.ValueChanged += (_, _) => { if (!_loading && Cur is { } m) m.AfterMsgId = (int)_afterMsg.Value; };
        Controls.Add(_afterMsg);

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
            _sfx.Value = Math.Clamp(m.Sfx, -1, 0xFFFF); _gesture.Value = Math.Clamp(m.Gesture, -1, 31);
            _doneFlag.Value = Math.Clamp(m.DoneFlag, -1, 4095); _afterMsg.Value = Math.Clamp(m.AfterMsgId, -1, 0xFFFF);
            _o1.Load(m.Outcome1); _o2.Load(m.Outcome2);
        }
        _loading = false;
        RefreshEnabled();
    }

    private void RefreshEnabled()
    {
        bool has = Cur != null, prompt = has && Cur!.Kind == MhMsgKind.Prompt;
        _text.Enabled = _kMsg.Enabled = _kPrompt.Enabled = _sfx.Enabled = _gesture.Enabled =
            _doneFlag.Enabled = _afterMsg.Enabled = has;
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
        private readonly NumericUpDown _next, _flag, _item, _cost;
        private readonly CheckBox _charge;
        private readonly System.Func<MhOutcome?> _get;
        private readonly System.Func<bool> _loading;

        public OutcomeControls(DialogueEditorDialog dlg, int x, int y, System.Func<MhOutcome?> get, System.Func<bool> loading)
        {
            _get = get; _loading = loading;
            dlg.Add(OLab("Go to MsgBox #:", x, y, 96));
            _next = Spin(x + 100, y - 2, -1, 0xFFFF, 80); _next.Hexadecimal = true;
            _next.ValueChanged += (_, _) => { if (!_loading() && _get() is { } o) o.NextMsgId = (int)_next.Value; };
            dlg.Add(_next);
            dlg.Add(OLab("Fire Trigger #:", x, y + 26, 96));
            _flag = Spin(x + 100, y + 24, -1, 4095, 80);
            _flag.ValueChanged += (_, _) => { if (!_loading() && _get() is { } o) o.FireFlag = (int)_flag.Value; };
            dlg.Add(_flag);
            dlg.Add(OLab("Give Item id:", x, y + 52, 96));
            _item = Spin(x + 100, y + 50, -1, 0x7F, 80);
            _item.ValueChanged += (_, _) => { if (!_loading() && _get() is { } o) o.GiveItem = (int)_item.Value; };
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
            _next.Value = Math.Clamp(o.NextMsgId, -1, 0xFFFF);
            _flag.Value = Math.Clamp(o.FireFlag, -1, 4095);
            _item.Value = Math.Clamp(o.GiveItem, -1, 0x7F);
            _charge.Checked = o.ChargeRupees; _cost.Value = Math.Clamp(o.RupeeCost, 0, 9999); _cost.Enabled = o.ChargeRupees;
        }

        public void SetEnabled(bool on)
        { _next.Enabled = _flag.Enabled = _item.Enabled = _charge.Enabled = on; _cost.Enabled = on && _charge.Checked; }
    }
}
