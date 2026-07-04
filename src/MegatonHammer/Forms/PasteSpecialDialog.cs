namespace MegatonHammer.Forms;

/// <summary>
/// Hammer's "Paste Special": paste N copies of the clipboard, each offset and rotated by a
/// per-copy increment, optionally re-centred on the original and uniquely renamed. The options
/// are read back from <see cref="Result"/> after the dialog returns OK.
/// </summary>
public sealed class PasteSpecialDialog : Form
{
    private static readonly Color BgDark = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);

    public struct Options
    {
        public int Copies;
        public bool StartAtCenter;
        public bool UsePivot;            // rotate about the selection centre rather than world origin
        public float OffX, OffY, OffZ;   // per-copy translation
        public float RotX, RotY, RotZ;   // per-copy rotation (degrees)
        public bool MakeNamesUnique;
        public string? Prefix;
    }

    public Options Result { get; private set; }

    private readonly NumericUpDown _copies;
    private readonly CheckBox _startCenter, _usePivot, _unique, _addPrefix;
    private readonly NumericUpDown _ox, _oy, _oz, _rx, _ry, _rz;
    private readonly TextBox _prefix;

    public PasteSpecialDialog()
    {
        Text = "Paste Special";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(380, 380);
        BackColor = BgDark; ForeColor = FgNormal;
        Font = new Font("Segoe UI", 8.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        int y = 14;
        Controls.Add(Label("Number of copies to paste:", 14, y, 170));
        _copies = Spin(196, y - 2, 1, 999, 1); Controls.Add(_copies); y += 30;

        _startCenter = Check("Start at center of original", 16, y, true); Controls.Add(_startCenter); y += 22;
        _usePivot    = Check("Use pivot from Selection Tool as rotate origin", 16, y, true); Controls.Add(_usePivot); y += 30;

        Controls.Add(Header("OFFSET (per copy)", y)); y += 22;
        Controls.Add(Label("X:", 24, y, 20)); _ox = Spin(46, y - 2, -100000, 100000, 0); Controls.Add(_ox);
        Controls.Add(Label("Y:", 140, y, 20)); _oy = Spin(162, y - 2, -100000, 100000, 0); Controls.Add(_oy);
        Controls.Add(Label("Z:", 256, y, 20)); _oz = Spin(278, y - 2, -100000, 100000, 0); Controls.Add(_oz); y += 32;

        Controls.Add(Header("ROTATION (degrees, per copy)", y)); y += 22;
        Controls.Add(Label("X:", 24, y, 20)); _rx = Spin(46, y - 2, -3600, 3600, 0); Controls.Add(_rx);
        Controls.Add(Label("Y:", 140, y, 20)); _ry = Spin(162, y - 2, -3600, 3600, 0); Controls.Add(_ry);
        Controls.Add(Label("Z:", 256, y, 20)); _rz = Spin(278, y - 2, -3600, 3600, 0); Controls.Add(_rz); y += 34;

        _unique    = Check("Make pasted entity names unique", 16, y, true); Controls.Add(_unique); y += 24;
        _addPrefix = Check("Add this prefix to all named entities:", 16, y, false); Controls.Add(_addPrefix);
        _prefix = new TextBox { Left = 250, Top = y - 2, Width = 110, BackColor = BgInput, ForeColor = FgNormal,
                                BorderStyle = BorderStyle.FixedSingle }; Controls.Add(_prefix); y += 34;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(196, y), Width = 80,
                              BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(282, y), Width = 80,
                                 BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        ok.Click += (_, _) => Result = new Options
        {
            Copies = (int)_copies.Value,
            StartAtCenter = _startCenter.Checked,
            UsePivot = _usePivot.Checked,
            OffX = (float)_ox.Value, OffY = (float)_oy.Value, OffZ = (float)_oz.Value,
            RotX = (float)_rx.Value, RotY = (float)_ry.Value, RotZ = (float)_rz.Value,
            MakeNamesUnique = _unique.Checked,
            Prefix = _addPrefix.Checked && _prefix.Text.Length > 0 ? _prefix.Text : null,
        };
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
    }

    private static Label Label(string t, int x, int y, int w) => new()
    { Text = t, Left = x, Top = y, Width = w, ForeColor = FgNormal, TextAlign = ContentAlignment.MiddleLeft };
    private static Label Header(string t, int y) => new()
    { Text = t, Left = 14, Top = y, Width = 350, ForeColor = Color.FromArgb(140, 190, 255),
      Font = new Font("Segoe UI", 8f, FontStyle.Bold) };
    private static CheckBox Check(string t, int x, int y, bool chk) => new()
    { Text = t, Left = x, Top = y, Width = 340, Checked = chk, ForeColor = FgNormal };
    private static NumericUpDown Spin(int x, int y, int min, int max, int val) => new()
    { Location = new Point(x, y), Width = 84, Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max),
      BackColor = BgInput, ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle };
}
