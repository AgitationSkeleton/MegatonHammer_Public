using MegatonHammer.Editor;
using MegatonHammer.Textures;

namespace MegatonHammer.Forms;

/// <summary>
/// Left-dock texture browser: search + sort (name / type / use), a scrollable thumbnail
/// grid, and a folder loader. Clicking a thumbnail selects the active paint texture.
/// </summary>
public sealed class TexturePanel : UserControl
{
    private static readonly Color BgDark   = Color.FromArgb(37, 37, 38);
    private static readonly Color BgDarker = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(205, 205, 205);
    private static readonly Color SelBlue  = Color.FromArgb(0, 122, 204);

    private const int TileW = 78, TileH = 96, Thumb = 64, MaxTiles = 400;

    private readonly TextureLibrary _lib;
    private readonly MapDocument    _doc;

    private readonly TextBox         _search;
    private readonly ComboBox        _sortCombo;
    private readonly FlowLayoutPanel _grid;
    private readonly Label           _countLabel;

    private Panel?  _selectedTile;
    private string? _selectedName;

    // Robust double-click: time two MouseDowns on the same tile (every press fires MouseDown).
    private string?  _lastDownName;
    private DateTime _lastDownTime;

    /// <summary>Raised when the user clicks a texture; argument is the texture name.</summary>
    public event Action<string>? TextureSelected;

    /// <summary>Raised when a texture is committed (browser double-click): set active AND apply to
    /// any currently-selected faces.</summary>
    public event Action<string>? TextureCommitted;

    /// <summary>Raised on right-click — author a scroll animation for this texture in the current scene.</summary>
    public event Action<string>? AnimateRequested;

    public string? SelectedTexture => _selectedName;

    public TexturePanel(TextureLibrary lib, MapDocument doc, bool showHeader = true)
    {
        _lib = lib;
        _doc = doc;

        BackColor = BgDark;
        Width     = 210;
        Dock      = DockStyle.Left;

        var header = new Label
        {
            Dock = DockStyle.Top, Height = 22, Text = "  TEXTURES",
            BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(180,180,180),
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft,
        };

        _search = new TextBox
        {
            Dock = DockStyle.Top, BackColor = BgDarker, ForeColor = FgNormal,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f),
        };
        _search.TextChanged += (_, _) => RefreshGrid();
        var searchHint = new Label
        {
            Dock = DockStyle.Top, Height = 14, Text = "  Search:",
            ForeColor = Color.FromArgb(130,130,130), Font = new Font("Segoe UI", 7f),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _sortCombo = new ComboBox
        {
            Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgDarker, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5f),
        };
        _sortCombo.Items.AddRange(["Sort: Name", "Sort: Type", "Sort: Usage"]);
        _sortCombo.SelectedIndex = 0;
        _sortCombo.SelectedIndexChanged += (_, _) => RefreshGrid();

        var browseBtn = new Button
        {
            Dock = DockStyle.Top, Height = 26, Text = "Browse All Textures…",
            BackColor = Color.FromArgb(0, 90, 158), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
        };
        browseBtn.Click += (_, _) => OpenBrowser();

        var loadBtn = new Button
        {
            Dock = DockStyle.Top, Height = 26, Text = "Load Folder…",
            BackColor = Color.FromArgb(60,60,65), ForeColor = FgNormal,
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f),
        };
        loadBtn.Click += OnLoadFolder;

        _countLabel = new Label
        {
            Dock = DockStyle.Bottom, Height = 18, ForeColor = Color.FromArgb(130,130,130),
            Font = new Font("Segoe UI", 7.5f), TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };

        _grid = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = BgDarker, AutoScroll = true,
            WrapContents = true, FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(4),
        };

        // Add in reverse dock priority: Fill first, then the stacked Top controls.
        Controls.Add(_grid);
        Controls.Add(_countLabel);
        Controls.Add(loadBtn);
        Controls.Add(browseBtn);
        Controls.Add(_sortCombo);
        Controls.Add(_search);
        Controls.Add(searchHint);
        if (showHeader) Controls.Add(header);

        RefreshGrid();
    }

    /// <summary>Recomputes usage counts and rebuilds the grid (call after textures change).</summary>
    public void Refresh(bool recount)
    {
        if (recount) _lib.RecountUsage(_doc);
        RefreshGrid();
        if (_browser is { IsDisposed: false }) _browser.ReloadLibrary();
    }

    private TextureBrowserForm? _browser;

    private void OpenBrowser()
    {
        if (_browser is { IsDisposed: false }) { _browser.Activate(); return; }
        _browser = new TextureBrowserForm(_lib);
        _browser.TextureSelected  += name => TextureSelected?.Invoke(name);
        _browser.TextureCommitted += name => TextureCommitted?.Invoke(name);  // double-click: apply + close
        _browser.FormClosed += (_, _) => _browser = null;
        _browser.Show(FindForm());
    }

    private TextureSort CurrentSort => _sortCombo.SelectedIndex switch
    {
        1 => TextureSort.Type,
        2 => TextureSort.Usage,
        _ => TextureSort.Name,
    };

    private void RefreshGrid()
    {
        if (CurrentSort == TextureSort.Usage) _lib.RecountUsage(_doc);

        _grid.SuspendLayout();
        foreach (Control c in _grid.Controls) { DisposeTile(c); }
        _grid.Controls.Clear();
        _selectedTile = null;

        var list  = _lib.Query(_search.Text, CurrentSort);
        int shown = Math.Min(list.Count, MaxTiles);

        for (int i = 0; i < shown; i++)
            _grid.Controls.Add(BuildTile(list[i]));

        _countLabel.Text = list.Count > shown
            ? $"Showing {shown} of {list.Count}"
            : $"{list.Count} textures";

        _grid.ResumeLayout();
    }

    private Panel BuildTile(TextureEntry entry)
    {
        var tile = new Panel
        {
            Width = TileW, Height = TileH, Margin = new Padding(3),
            BackColor = BgDark, Tag = entry, Cursor = Cursors.Hand,
        };

        var pic = new PictureBox
        {
            Left = (TileW - Thumb) / 2, Top = 3, Width = Thumb, Height = Thumb,
            SizeMode = PictureBoxSizeMode.Zoom, BackColor = BgDarker,
        };
        try { pic.Image = entry.Image; } catch { /* leave blank */ }

        var label = new Label
        {
            Left = 0, Top = Thumb + 4, Width = TileW, Height = 24,
            Text = entry.Name + (entry.UsageCount > 0 ? $"  ({entry.UsageCount})" : ""),
            ForeColor = FgNormal, Font = new Font("Segoe UI", 6.8f),
            TextAlign = ContentAlignment.TopCenter, AutoEllipsis = true,
        };

        tile.Controls.Add(pic);
        tile.Controls.Add(label);

        // MouseDown.Clicks == 2 is WM_LBUTTONDBLCLK (tiles carry CS_DBLCLKS via their default
        // StandardDoubleClick style): double-click commits (applies the texture to the selected
        // faces), a single click selects. (The Click-timing approach didn't fire because the second
        // click of a double-click is consumed as DoubleClick, not a second Click.)
        void OnMouseDown(object? s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { _selectedName = entry.Name; SelectTile(tile, entry); AnimateRequested?.Invoke(entry.Name); return; }
            if (e.Button != MouseButtons.Left) return;
            var now = DateTime.Now;
            bool dbl = e.Clicks >= 2 ||
                       (_lastDownName == entry.Name && (now - _lastDownTime).TotalMilliseconds <= SystemInformation.DoubleClickTime);
            _lastDownName = entry.Name;
            _lastDownTime = now;
            if (dbl) { _lastDownName = null; _selectedName = entry.Name; TextureCommitted?.Invoke(entry.Name); }
            else SelectTile(tile, entry);
        }
        tile.MouseDown  += OnMouseDown;
        pic.MouseDown   += OnMouseDown;
        label.MouseDown += OnMouseDown;

        if (entry.Name == _selectedName) MarkSelected(tile);
        return tile;
    }

    private void SelectTile(Panel tile, TextureEntry entry)
    {
        if (_selectedTile != null && !_selectedTile.IsDisposed)
            _selectedTile.BackColor = BgDark;
        MarkSelected(tile);
        _selectedName = entry.Name;
        TextureSelected?.Invoke(entry.Name);
    }

    private void MarkSelected(Panel tile)
    {
        _selectedTile = tile;
        tile.BackColor = SelBlue;
    }

    private void OnLoadFolder(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select a folder of texture images (PNG/BMP/JPG, scanned recursively)",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int added = _lib.LoadFolder(dlg.SelectedPath);
        RefreshGrid();
        MessageBox.Show($"Loaded {added} texture(s) from:\n{dlg.SelectedPath}",
            "Texture Library", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void DisposeTile(Control tile)
    {
        foreach (Control c in tile.Controls)
            if (c is PictureBox pb) pb.Image = null;   // shared bitmap owned by the entry
        tile.Dispose();
    }
}
