using MegatonHammer.Textures;

namespace MegatonHammer.Forms;

/// <summary>
/// Hammer's "Replace Textures": swap one texture for another across the whole map (or just the
/// selected faces). Each field has a "…" button that opens the texture browser to pick the texture
/// (mirroring Hammer's ReplaceTexDlg browse buttons). The host reads <see cref="Find"/> /
/// <see cref="Replace"/> / <see cref="SelectedOnly"/> after an OK result and performs the replacement.
/// </summary>
public sealed class ReplaceTexturesDialog : Form
{
    private static readonly Color BgDark = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);

    private readonly TextureLibrary _lib;
    private readonly TextBox _find = new(), _replace = new();
    private readonly CheckBox _selOnly = new();
    private readonly Label _status = new();

    // Session-persistent "Selected faces only" preference (defaults OFF; remembered across opens this run).
    private static bool _sessionSelectedOnly;

    public string Find => _find.Text.Trim();
    public string Replace => _replace.Text.Trim();
    public bool SelectedOnly => _selOnly.Checked;

    /// <summary>#1: fill the "Find texture" field from a face the user just clicked in the 3D view, so the
    /// replace flow targets whatever they're looking at without retyping. Ignores blank/untextured faces.</summary>
    public void SetFind(string? texture)
    {
        if (!string.IsNullOrWhiteSpace(texture)) _find.Text = texture;
    }

    /// <summary>Fired when the user clicks Replace; the host performs the swap with the CURRENT face
    /// selection and calls <see cref="ShowResult"/>. The dialog stays open (modeless) so the user can
    /// change the 3D selection between replacements.</summary>
    public event Action? ReplaceRequested;

    public ReplaceTexturesDialog(TextureLibrary lib, string? preFind = null)
    {
        _lib = lib;

        Text = "Replace Textures";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;   // modeless tool window (stays above, doesn't block)
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(400, 196);
        BackColor = BgDark; ForeColor = FgNormal;
        Font = new Font("Segoe UI", 8.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Controls.Add(new Label { Text = "Find texture:", Left = 14, Top = 16, Width = 100 });
        Field(_find, 120, 13, preFind);

        Controls.Add(new Label { Text = "Replace with:", Left = 14, Top = 50, Width = 100 });
        Field(_replace, 120, 47, null);

        _selOnly.SetBounds(120, 84, 240, 22);
        _selOnly.Text = "Selected faces only"; _selOnly.ForeColor = FgNormal; _selOnly.FlatStyle = FlatStyle.Flat;
        _selOnly.Checked = _sessionSelectedOnly;   // default OFF, remembered for the session
        _selOnly.CheckedChanged += (_, _) => _sessionSelectedOnly = _selOnly.Checked;
        Controls.Add(_selOnly);

        _status.SetBounds(14, 110, 372, 18); _status.ForeColor = Color.FromArgb(150, 200, 150);
        Controls.Add(_status);

        var ok = new Button { Text = "Replace", Left = 214, Top = 150, Width = 80, Height = 28,
            BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        ok.Click += (_, _) => { if (Find.Length > 0) ReplaceRequested?.Invoke(); };
        var close = new Button { Text = "Close", Left = 302, Top = 150, Width = 80, Height = 28, DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        close.Click += (_, _) => Close();
        Controls.AddRange([ok, close]); AcceptButton = ok; CancelButton = close;
    }

    /// <summary>Updates the in-dialog status after a replacement (no modal popup — keeps the dialog modeless).</summary>
    public void ShowResult(int n) => _status.Text = $"Replaced texture on {n} face(s).";

    // A text field + a "…" browse button that opens the texture browser; double-clicking a texture
    // fills the field (and the browser closes itself).
    private void Field(TextBox box, int x, int y, string? initial)
    {
        box.SetBounds(x, y, 210, 22);
        box.BackColor = BgInput; box.ForeColor = FgNormal; box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = new Font("Consolas", 9f);
        if (!string.IsNullOrEmpty(initial)) box.Text = initial;
        Controls.Add(box);

        var browse = new Button { Text = "…", Left = x + 214, Top = y - 1, Width = 30, Height = 24,
            BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        browse.Click += (_, _) =>
        {
            using var b = new TextureBrowserForm(_lib) { StartPosition = FormStartPosition.CenterParent };
            b.TextureCommitted += name => box.Text = name;   // double-click fills the field; browser closes itself
            b.ShowDialog(this);
        };
        Controls.Add(browse);
    }
}
