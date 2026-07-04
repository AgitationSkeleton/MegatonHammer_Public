using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

public sealed class GameSelectDialog : Form
{
    public GameConfig SelectedConfig { get; private set; } = new();

    private readonly ListBox _list;
    private readonly Label _desc;
    private readonly Button _ok;
    private readonly Button _cancel;
    private readonly Button _browse;
    private readonly TextBox _pathBox;
    private readonly Label _pathLabel;

    private static readonly (GameMode Mode, string Description)[] Entries =
    [
        (GameMode.OcarinaOfTime,     "Vanilla OoT ROM (.z64/.n64). All textures and actors read directly from ROM."),
        (GameMode.MajorasMask,       "Vanilla MM ROM (.z64/.n64). All textures and actors read directly from ROM."),
        (GameMode.ShipOfHarkinian,   "Ship of Harkinian build directory. Removes engine limits; enables live preview."),
        (GameMode.TwoShip2Harkinian, "2Ship2Harkinian build directory. MM with limit removal; enables live preview."),
        (GameMode.CustomOoT,         "Custom OoT-based ROM or build (romhacks, etc.)."),
        (GameMode.CustomMM,          "Custom MM-based ROM or build (romhacks, etc.)")
    ];

    public GameSelectDialog()
    {
        Text = "Megaton Hammer — Select Game";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 520;
        Height = 400;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(220, 220, 220);

        var title = new Label
        {
            Text = "MEGATON HAMMER",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 200, 0),
            AutoSize = true,
            Location = new Point(16, 12)
        };

        var subtitle = new Label
        {
            Text = "Zelda 64 Level Editor",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(160, 160, 160),
            AutoSize = true,
            Location = new Point(18, 44)
        };

        var listLabel = new Label
        {
            Text = "Select game target:",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(180, 180, 180),
            AutoSize = true,
            Location = new Point(16, 72)
        };

        _list = new ListBox
        {
            Location = new Point(16, 92),
            Size = new Size(480, 130),
            BackColor = Color.FromArgb(45, 45, 48),
            ForeColor = Color.FromArgb(220, 220, 220),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9.5f)
        };
        foreach (var (mode, _) in Entries)
            _list.Items.Add(new GameConfig { Mode = mode }.DisplayName);
        _list.SelectedIndex = 0;

        _desc = new Label
        {
            Location = new Point(16, 230),
            Size = new Size(480, 40),
            ForeColor = Color.FromArgb(160, 160, 160),
            Font = new Font("Segoe UI", 8.5f),
            Text = Entries[0].Description
        };

        _pathLabel = new Label
        {
            Text = "ROM / Game Path:",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(180, 180, 180),
            AutoSize = true,
            Location = new Point(16, 278)
        };

        _pathBox = new TextBox
        {
            Location = new Point(16, 298),
            Size = new Size(396, 24),
            BackColor = Color.FromArgb(45, 45, 48),
            ForeColor = Color.FromArgb(220, 220, 220),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f)
        };

        _browse = new Button
        {
            Text = "Browse...",
            Location = new Point(420, 296),
            Size = new Size(76, 26),
            BackColor = Color.FromArgb(62, 62, 66),
            ForeColor = Color.FromArgb(220, 220, 220),
            FlatStyle = FlatStyle.Flat
        };
        _browse.FlatAppearance.BorderColor = Color.FromArgb(100, 100, 100);
        _browse.Click += BrowseClick;

        _ok = new Button
        {
            Text = "OK",
            Location = new Point(320, 334),
            Size = new Size(84, 28),
            BackColor = Color.FromArgb(0, 122, 204),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.OK
        };
        _ok.FlatAppearance.BorderSize = 0;
        _ok.Click += OkClick;

        _cancel = new Button
        {
            Text = "Cancel",
            Location = new Point(412, 334),
            Size = new Size(84, 28),
            BackColor = Color.FromArgb(62, 62, 66),
            ForeColor = Color.FromArgb(220, 220, 220),
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.Cancel
        };
        _cancel.FlatAppearance.BorderColor = Color.FromArgb(100, 100, 100);

        AcceptButton = _ok;
        CancelButton = _cancel;

        Controls.AddRange([title, subtitle, listLabel, _list, _desc, _pathLabel, _pathBox, _browse, _ok, _cancel]);

        _list.SelectedIndexChanged += (s, e) =>
        {
            if (_list.SelectedIndex < 0) return;
            _desc.Text = Entries[_list.SelectedIndex].Description;
            // Recall the path last used for this game mode (or the configured base ROM as a fallback).
            _pathBox.Text = PathFor(Entries[_list.SelectedIndex].Mode) ?? "";
        };

        // Reopen on the last-used game mode (not always OoT) and prefill its stored/auto-detected path,
        // so the ROM/Game path field is populated when the splash reappears (e.g. via Close Project).
        int lastIdx = Array.FindIndex(Entries, en => en.Mode.ToString() == EditorSettings.LastGameMode);
        _list.SelectedIndex = lastIdx >= 0 ? lastIdx : 0;        // fires the handler when it differs from 0
        _pathBox.Text = PathFor(Entries[_list.SelectedIndex].Mode) ?? "";   // also covers the no-change case
    }

    // The path to prefill for a mode: the value last entered for it, else the configured/auto-detected
    // base ROM (OoT/MM) so ROM targets aren't blank before the user has browsed once.
    private static string? PathFor(GameMode mode)
    {
        var stored = EditorSettings.GetLastGamePath(mode);
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        return mode switch
        {
            GameMode.OcarinaOfTime or GameMode.CustomOoT => EditorSettings.OotRomPath,
            GameMode.MajorasMask or GameMode.CustomMM    => EditorSettings.MmRomPath,
            _ => null,
        };
    }

    private void BrowseClick(object? sender, EventArgs e)
    {
        int idx = Math.Max(0, _list.SelectedIndex);
        bool isRom = Entries[idx].Mode is GameMode.OcarinaOfTime or GameMode.MajorasMask
                     or GameMode.CustomOoT or GameMode.CustomMM;

        if (isRom)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select ROM file",
                Filter = "N64 ROM files (*.z64;*.n64;*.v64)|*.z64;*.n64;*.v64|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == DialogResult.OK)
                _pathBox.Text = dlg.FileName;
        }
        else
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select game build directory"
            };
            if (dlg.ShowDialog() == DialogResult.OK)
                _pathBox.Text = dlg.SelectedPath;
        }
    }

    private void OkClick(object? sender, EventArgs e)
    {
        int idx = Math.Max(0, _list.SelectedIndex);
        var mode = Entries[idx].Mode;
        string? path = _pathBox.Text.Length > 0 ? _pathBox.Text : null;

        // Fork builds (SoH/2Ship) point at a directory; everything else at a ROM file.
        bool isFolder = mode is GameMode.ShipOfHarkinian or GameMode.TwoShip2Harkinian;
        SelectedConfig = new GameConfig
        {
            Mode = mode,
            RomPath = isFolder ? null : path,
            GameDirectory = isFolder ? path : null,
        };

        // Remember this path + mode so the next launch (and Close Project) defaults to them.
        EditorSettings.SetLastGamePath(mode, path);
        EditorSettings.LastGameMode = mode.ToString();
    }
}
