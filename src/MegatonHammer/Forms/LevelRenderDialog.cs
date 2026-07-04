namespace MegatonHammer.Forms;

/// <summary>Which camera angle(s) to export a full-level render from.</summary>
public enum RenderView { Isometric, TopDown, Both }

/// <summary>Options for exporting a textured + shaded render of the whole level (isometric and/or
/// top-down), with or without actors, at a chosen resolution.</summary>
public sealed class LevelRenderDialog : Form
{
    private readonly RadioButton _rbIso  = new() { Text = "Isometric", AutoSize = true, Checked = true, Location = new Point(12, 22) };
    private readonly RadioButton _rbTop  = new() { Text = "Top-down (map)", AutoSize = true, Location = new Point(12, 44) };
    private readonly RadioButton _rbBoth = new() { Text = "Both", AutoSize = true, Location = new Point(12, 66) };
    private readonly CheckBox _actors = new() { Text = "Include actors / entities", AutoSize = true, Checked = true, Location = new Point(12, 132) };
    private readonly ComboBox _res = new() { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(80, 156), Width = 120 };
    private readonly int[] _resValues = [1024, 2048, 4096];

    public RenderView View => _rbTop.Checked ? RenderView.TopDown : _rbBoth.Checked ? RenderView.Both : RenderView.Isometric;
    public bool IncludeActors => _actors.Checked;
    public int Resolution => _resValues[Math.Max(0, _res.SelectedIndex)];

    public LevelRenderDialog()
    {
        Text = "Export Level Render";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(300, 230);
        BackColor = Color.FromArgb(45, 45, 48);
        ForeColor = Color.FromArgb(220, 220, 220);

        var viewBox = new GroupBox { Text = "View", ForeColor = ForeColor, Location = new Point(12, 8), Size = new Size(276, 96) };
        viewBox.Controls.AddRange([_rbIso, _rbTop, _rbBoth]);

        var resLabel = new Label { Text = "Resolution:", AutoSize = true, Location = new Point(12, 159) };
        foreach (var r in _resValues) _res.Items.Add($"{r} × {r}");
        _res.SelectedIndex = 1;   // 2048

        var ok = new Button { Text = "Export…", DialogResult = DialogResult.OK, Location = new Point(114, 192), Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(200, 192), Width = 80 };

        Controls.AddRange([viewBox, _actors, resLabel, _res, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
