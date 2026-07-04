using System.Linq;
using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

/// <summary>
/// The project's Message Bank editor: author/edit dialogue (the text an actor's Message field references
/// by textId). Opened from an actor's Message field, scoped to that field's textId range so "New" picks a
/// free id in-range. Markup: &amp; = newline, ^ = new box/page, %r %g %b %y %w %p = colour.
/// On OK, <see cref="SelectedId"/> holds the chosen message's textId (for the caller to assign).
/// </summary>
public sealed class MessageBankDialog : Form
{
    private static readonly Color BgDark = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);

    private readonly ZScene _scene;
    private readonly int _baseId, _maxId;
    private readonly ListBox _list;
    private readonly TextBox _text;
    private readonly NumericUpDown _box, _pos;
    private readonly Label _idLabel;
    private bool _loading;

    public int? SelectedId { get; private set; }

    public MessageBankDialog(ZScene scene, int baseId, int maxId, int currentId)
    {
        _scene = scene; _baseId = baseId; _maxId = maxId;
        Text = "Message Bank";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(560, 360);
        BackColor = BgDark; ForeColor = FgNormal;
        Font = new Font("Segoe UI", 8.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _list = new ListBox { Left = 12, Top = 12, Width = 200, Height = 270, BackColor = BgInput,
            ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
        _list.SelectedIndexChanged += (_, _) => LoadSelected();
        Controls.Add(_list);

        var add = Btn("New", 12, 290, 62); add.Click += (_, _) => AddNew();
        var del = Btn("Delete", 80, 290, 62); del.Click += (_, _) => DeleteSelected();
        Controls.Add(add); Controls.Add(del);

        _idLabel = new Label { Left = 226, Top = 14, Width = 320, Height = 18, ForeColor = Color.FromArgb(140, 190, 255),
            Font = new Font("Consolas", 9f, FontStyle.Bold), Text = "(no message selected)" };
        Controls.Add(_idLabel);

        Controls.Add(Lab("Text  (&=newline  ^=new box  %r/%g/%b/%y/%w/%p=colour):", 226, 38, 320));
        _text = new TextBox { Left = 226, Top = 58, Width = 320, Height = 180, Multiline = true, ScrollBars = ScrollBars.Vertical,
            BackColor = BgInput, ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9.5f) };
        _text.TextChanged += (_, _) => { if (!_loading && Current is { } m) m.Text = _text.Text; };
        Controls.Add(_text);

        Controls.Add(Lab("Box type:", 226, 246, 60));
        _box = Spin(290, 244, 0, 15); _box.ValueChanged += (_, _) => { if (!_loading && Current is { } m) m.BoxType = (int)_box.Value; };
        Controls.Add(_box);
        Controls.Add(Lab("Position:", 360, 246, 56));
        _pos = Spin(420, 244, 0, 2); _pos.ValueChanged += (_, _) => { if (!_loading && Current is { } m) m.YPos = (int)_pos.Value; };
        Controls.Add(_pos);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(384, 290), Width = 76,
            BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(468, 290), Width = 78,
            BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        ok.Click += (_, _) => { if (Current is { } m) SelectedId = m.Id; };
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;

        Reload(currentId);
    }

    private MhMessage? Current => _list.SelectedItem as MhMessage;

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
        if (Current is { } m)
        {
            _idLabel.Text = $"textId  0x{m.Id:X3}   (range 0x{_baseId:X3}–0x{_maxId:X3})";
            _text.Text = m.Text; _box.Value = Math.Clamp(m.BoxType, 0, 15); _pos.Value = Math.Clamp(m.YPos, 0, 2);
            _text.Enabled = _box.Enabled = _pos.Enabled = true;
        }
        else
        {
            _idLabel.Text = "(no message — click New)";
            _text.Text = ""; _text.Enabled = _box.Enabled = _pos.Enabled = false;
        }
        _loading = false;
    }

    private void AddNew()
    {
        int id = _scene.NextFreeMessageId(_baseId, _maxId - _baseId);
        var m = new MhMessage(id, "New message.");
        _scene.Messages.Add(m);
        Reload(id);
        _text.Focus(); _text.SelectAll();
    }

    private void DeleteSelected()
    {
        if (Current is { } m) { _scene.Messages.Remove(m); Reload(-1); }
    }

    private static Button Btn(string t, int x, int y, int w) => new()
    { Text = t, Left = x, Top = y, Width = w, Height = 26, BackColor = Color.FromArgb(60, 60, 63),
      ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8f) };
    private static Label Lab(string t, int x, int y, int w) => new()
    { Text = t, Left = x, Top = y, Width = w, ForeColor = FgNormal };
    private static NumericUpDown Spin(int x, int y, int min, int max) => new()
    { Location = new Point(x, y), Width = 56, Minimum = min, Maximum = max, BackColor = BgInput,
      ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle };
}
