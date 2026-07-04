using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

/// <summary>
/// Properties for a scene path (Hammer's path_track properties): name, the loop/closed flag, and the
/// MM path-header fields (additionalPathIndex / customValue). Values are written back to the ZPath on OK.
/// </summary>
public sealed class PathPropertiesDialog : Form
{
    private static readonly Color BgDark = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);

    private readonly ZPath _path;
    private readonly TextBox _name;
    private readonly CheckBox _closed;
    private readonly NumericUpDown _addIdx, _custom;

    public PathPropertiesDialog(ZPath path)
    {
        _path = path;
        Text = "Path Properties";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(340, 210);
        BackColor = BgDark; ForeColor = FgNormal;
        Font = new Font("Segoe UI", 8.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        int y = 16;
        Controls.Add(Label("Name:", 14, y, 90));
        _name = new TextBox { Left = 110, Top = y - 2, Width = 210, Text = path.Name,
                              BackColor = BgInput, ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(_name); y += 32;

        _closed = new CheckBox { Text = "Closed loop (draw closing segment)", Left = 16, Top = y, Width = 300,
                                 Checked = path.Closed, ForeColor = FgNormal };
        Controls.Add(_closed); y += 30;

        Controls.Add(Label("Additional path index (MM):", 14, y, 170));
        _addIdx = Spin(196, y - 2, 0, 255, path.AdditionalPathIndex); Controls.Add(_addIdx); y += 30;

        Controls.Add(Label("Custom value (MM):", 14, y, 170));
        _custom = Spin(196, y - 2, -32768, 32767, path.CustomValue); Controls.Add(_custom); y += 36;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(154, y), Width = 80,
                              BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(240, y), Width = 80,
                                 BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        ok.Click += (_, _) =>
        {
            _path.Name = _name.Text.Length > 0 ? _name.Text : _path.Name;
            _path.Closed = _closed.Checked;
            _path.AdditionalPathIndex = (byte)_addIdx.Value;
            _path.CustomValue = (short)_custom.Value;
        };
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
    }

    private static Label Label(string t, int x, int y, int w) => new()
    { Text = t, Left = x, Top = y, Width = w, ForeColor = FgNormal, TextAlign = ContentAlignment.MiddleLeft };
    private static NumericUpDown Spin(int x, int y, int min, int max, int val) => new()
    { Location = new Point(x, y), Width = 110, Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max),
      BackColor = BgInput, ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle };
}
