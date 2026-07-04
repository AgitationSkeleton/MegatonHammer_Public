namespace MegatonHammer.Forms;

/// <summary>Hammer's "Find Entities" — type a targetname (or class name) to locate. The entered
/// text is read from <see cref="Query"/> after OK; the host selects and centres on the match.</summary>
public sealed class FindEntitiesDialog : Form
{
    private static readonly Color BgDark = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);

    private readonly TextBox _box;
    public string Query => _box.Text.Trim();

    public FindEntitiesDialog()
    {
        Text = "Find Entities";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(300, 110);
        BackColor = BgDark; ForeColor = FgNormal;
        Font = new Font("Segoe UI", 8.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Controls.Add(new Label { Text = "Targetname to find:", Left = 14, Top = 16, Width = 270, ForeColor = FgNormal });
        _box = new TextBox { Left = 14, Top = 38, Width = 272, BackColor = BgInput, ForeColor = FgNormal,
                             BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(_box);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(118, 74), Width = 80,
                              BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(204, 74), Width = 80,
                                 BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
    }
}
