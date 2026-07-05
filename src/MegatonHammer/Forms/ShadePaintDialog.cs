using MegatonHammer.Tools;
using OpenTK.Mathematics;

namespace MegatonHammer.Forms;

/// <summary>
/// Modeless properties sheet for the vertex-shade spray tool (Hammer's Face Edit is the model): a colour
/// swatch/picker plus brush-size and opacity sliders. Stays open while you spray in the 3D view — it never
/// steals focus from the viewport. Editing any control updates the live tool.
/// </summary>
public sealed class ShadePaintDialog : Form
{
    private static readonly Color BgDark  = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);
    private static readonly Color HdrFg   = Color.FromArgb(140, 190, 255);

    private readonly ShadePaintTool _tool;
    private readonly Panel   _swatch;
    private readonly TrackBar _size, _opacity;
    private readonly Label   _sizeVal, _opacityVal;
    private readonly CheckBox _erase;

    public ShadePaintDialog(ShadePaintTool tool)
    {
        _tool = tool;
        Text = "Shade Paint";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(252, 322);
        MinimumSize = new Size(240, 320);
        BackColor = BgDark; ForeColor = FgNormal;
        Font = new Font("Segoe UI", 8.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        // Don't take focus from the 3D viewport when it opens / is clicked (Hammer's tool sheet behaviour).
        ShowInTaskbar = false;

        int y = 12;
        Controls.Add(Header("SPRAY COLOUR", y)); y += 24;

        _swatch = new Panel { Left = 14, Top = y, Width = 92, Height = 46, BorderStyle = BorderStyle.FixedSingle,
            BackColor = ToColor(_tool.PaintColor), Cursor = Cursors.Hand };
        _swatch.Click += (_, _) => PickColour();
        Controls.Add(_swatch);
        // Pick… spans the full width to the right of the swatch; Black/White presets sit on the row below it.
        Controls.Add(Btn("Pick…", 116, y, 120, 22, PickColour));
        Controls.Add(Btn("Black", 116, y + 26, 58, 20, () => SetColour(Vector3.Zero)));
        Controls.Add(Btn("White", 178, y + 26, 58, 20, () => SetColour(Vector3.One)));
        y += 56;

        // Erase mode: drag to remove shade (blend vertices back to the face's base colour) instead of adding it.
        _erase = new CheckBox { Left = 14, Top = y, Width = 224, Height = 20, ForeColor = FgNormal,
            Text = "Erase (drag to remove shade)", FlatStyle = FlatStyle.Flat, Checked = _tool.Erase };
        _erase.CheckedChanged += (_, _) => _tool.Erase = _erase.Checked;
        Controls.Add(_erase); y += 26;

        Controls.Add(Header("BRUSH SIZE", y)); y += 22;
        _sizeVal = new Label { Left = 150, Top = y, Width = 90, Height = 16, ForeColor = FgNormal, TextAlign = ContentAlignment.MiddleRight };
        _size = new TrackBar { Left = 8, Top = y, Width = 150, Minimum = 8, Maximum = 512, TickFrequency = 64,
            Value = (int)Math.Clamp(_tool.Radius, 8, 512), AutoSize = false, Height = 30 };
        _size.ValueChanged += (_, _) => { _tool.Radius = _size.Value; _sizeVal.Text = $"{_size.Value} u"; };
        Controls.Add(_size); Controls.Add(_sizeVal); _sizeVal.Text = $"{_size.Value} u"; y += 34;

        Controls.Add(Header("OPACITY", y)); y += 22;
        _opacityVal = new Label { Left = 150, Top = y, Width = 90, Height = 16, ForeColor = FgNormal, TextAlign = ContentAlignment.MiddleRight };
        _opacity = new TrackBar { Left = 8, Top = y, Width = 150, Minimum = 1, Maximum = 100, TickFrequency = 20,
            Value = (int)Math.Clamp(_tool.Opacity * 100f, 1, 100), AutoSize = false, Height = 30 };
        _opacity.ValueChanged += (_, _) => { _tool.Opacity = _opacity.Value / 100f; _opacityVal.Text = $"{_opacity.Value}%"; };
        Controls.Add(_opacity); Controls.Add(_opacityVal); _opacityVal.Text = $"{_opacity.Value}%"; y += 36;

        // Quick "remove all paint" — clears sprayed shade from the selection (if any solids are selected) or the
        // whole scene, in one undo step.
        var clear = Btn("Remove all paint", 14, y, 222, 24, RemoveAllPaint);
        Controls.Add(clear); y += 30;

        Controls.Add(new Label { Left = 14, Top = y, Width = 224, Height = 30, ForeColor = Color.FromArgb(150, 150, 150),
            Font = new Font("Segoe UI", 7.5f), Text = "Drag over brush faces in the 3D view to spray shade into their vertices." });
    }

    private void RemoveAllPaint()
    {
        bool sel = _tool.HasSelection;
        int n = _tool.ClearPaint(selectionOnly: sel);
        if (n == 0)
            MessageBox.Show(this, "No sprayed shade to remove.", "Shade Paint",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // Remembered across open/close for the session (the sheet is recreated each time the Shade tool is
    // re-selected, so persist the position statically like the texture browser keeps its place).
    private static Point? _lastLoc;

    /// <summary>Centre on the editor's top-level window (modeless Show doesn't honour CenterParent).</summary>
    public void CenterOnOwner()
    {
        var root = Owner; while (root?.Owner != null) root = root.Owner;
        Location = root != null
            ? new Point(root.Left + (root.Width - Width) / 2, root.Top + (root.Height - Height) / 2)
            : new Point(Math.Max(0, (Screen.PrimaryScreen!.WorkingArea.Width - Width) / 2), 120);
    }

    // First show: reopen where the user last left it (if still on a screen), else centre on the editor window.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_lastLoc is { } l && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(l, Size))))
            Location = l;
        else CenterOnOwner();
    }

    // Remember the user's chosen position (drag) so the next open reopens there.
    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (Visible && WindowState == FormWindowState.Normal) _lastLoc = Location;
    }

    private void PickColour()
    {
        using var dlg = new ColorDialog { Color = ToColor(_tool.PaintColor), FullOpen = true, AnyColor = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) SetColour(new Vector3(dlg.Color.R / 255f, dlg.Color.G / 255f, dlg.Color.B / 255f));
    }

    private void SetColour(Vector3 c) { _tool.PaintColor = c; _swatch.BackColor = ToColor(c); }

    private static Color ToColor(Vector3 c) =>
        Color.FromArgb((int)(Math.Clamp(c.X, 0, 1) * 255), (int)(Math.Clamp(c.Y, 0, 1) * 255), (int)(Math.Clamp(c.Z, 0, 1) * 255));

    private static Label Header(string t, int y) => new()
    { Text = t, Left = 12, Top = y, Width = 220, Height = 18, ForeColor = HdrFg, Font = new Font("Segoe UI", 8f, FontStyle.Bold) };

    private Button Btn(string t, int x, int y, int w, int h, Action onClick)
    {
        var b = new Button { Text = t, Left = x, Top = y, Width = w, Height = h, BackColor = Color.FromArgb(60, 60, 65),
            ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8f) };
        b.Click += (_, _) => onClick();
        return b;
    }

    // Never take activation from the 3D viewport (so spraying keeps working with the sheet up), mirroring
    // the Face Edit window: WS_EX_NOACTIVATE keeps clicks on this sheet from stealing keyboard focus.
    protected override bool ShowWithoutActivation => true;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_NOACTIVATE; return cp; }
    }
}
