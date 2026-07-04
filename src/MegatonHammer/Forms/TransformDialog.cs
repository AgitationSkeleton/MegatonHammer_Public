using System.Globalization;

namespace MegatonHammer.Forms;

/// <summary>
/// Hammer's Transform dialog: apply an exact Move / Rotate / Scale to the selection by typed X/Y/Z
/// values (rather than dragging). Modal; the host reads <see cref="Mode"/> and <see cref="X"/>/<see
/// cref="Y"/>/<see cref="Z"/> after an OK result.
/// </summary>
public sealed class TransformDialog : Form
{
    public enum TransformMode { Move, Rotate, Scale }

    private static readonly Color BgDark = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);

    private readonly ComboBox _mode = new();
    private readonly TextBox _x = new(), _y = new(), _z = new();

    public TransformMode Mode => (TransformMode)_mode.SelectedIndex;
    public float X => Parse(_x.Text);
    public float Y => Parse(_y.Text);
    public float Z => Parse(_z.Text);

    public TransformDialog()
    {
        Text = "Transform";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(260, 200);
        BackColor = BgDark; ForeColor = FgNormal;
        Font = new Font("Segoe UI", 8.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Controls.Add(new Label { Text = "Mode:", Left = 14, Top = 16, Width = 60, ForeColor = FgNormal });
        _mode.SetBounds(80, 13, 160, 24);
        _mode.DropDownStyle = ComboBoxStyle.DropDownList;
        _mode.BackColor = BgInput; _mode.ForeColor = FgNormal; _mode.FlatStyle = FlatStyle.Flat;
        _mode.Items.AddRange(["Move (units)", "Rotate (degrees)", "Scale (factor)"]);
        _mode.SelectedIndex = 0;
        _mode.SelectedIndexChanged += (_, _) => SetDefaults();
        Controls.Add(_mode);

        AddRow("X", _x, 56);
        AddRow("Y", _y, 86);
        AddRow("Z", _z, 116);
        SetDefaults();

        var ok = new Button { Text = "Apply", Left = 80, Top = 156, Width = 76, Height = 28,
            BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 164, Top = 156, Width = 76, Height = 28,
            BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
        Controls.AddRange([ok, cancel]);
        AcceptButton = ok; CancelButton = cancel;
    }

    private void SetDefaults()
    {
        // Identity per mode: move/rotate = 0, scale = 1.
        string d = Mode == TransformMode.Scale ? "1" : "0";
        _x.Text = d; _y.Text = d; _z.Text = d;
    }

    private void AddRow(string label, TextBox box, int top)
    {
        Controls.Add(new Label { Text = label, Left = 14, Top = top + 3, Width = 60, ForeColor = FgNormal });
        box.SetBounds(80, top, 160, 22);
        box.BackColor = BgInput; box.ForeColor = FgNormal; box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = new Font("Consolas", 9f);
        Controls.Add(box);
    }

    private static float Parse(string s) =>
        float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
}
