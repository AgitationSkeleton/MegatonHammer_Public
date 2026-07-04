using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

/// <summary>
/// Playtest starting-inventory editor. Lays out the game's items/masks/songs/equipment as
/// checkbox grids + tier dropdowns mirroring the in-game inventory subscreens (e.g. MM's 6×4 mask
/// pane), with built-in presets (Default / Nothing / Full inventory), named save/load, and a
/// reset-to-default button. Per-game; the chosen inventory is remembered across restarts.
/// </summary>
public sealed class InventoryDialog : Form
{
    private static readonly Color BgDark   = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput  = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);
    private static readonly Color Accent   = Color.FromArgb(0, 122, 204);

    private readonly bool _mm;
    private readonly bool _oot;
    private readonly NumericUpDown _hearts;
    private readonly TextBox _playerName;   // #21: injected save's player name (default "Link")
    private readonly ComboBox _zTarget;     // #5: Z-targeting mode (Toggle/Hold), persisted with the inventory
    private readonly ComboBox _presetCombo;
    private readonly Dictionary<string, CheckBox> _checks = new();
    private readonly Dictionary<string, ComboBox> _combos = new();
    private readonly Dictionary<string, NumericUpDown> _spinners = new();   // #13: ammo count boxes
    private readonly Dictionary<string, Label> _cellIcons = new();          // per-cell icon labels (sprite refresh)

    private const int CellW = 140, IconW = 20;   // composite item-cell width + left icon column
    private readonly Dictionary<string, Image?> _iconCache = new();   // #10b: per-key sprite, lazily decoded
    private Rom.ItemIconSource? _icons;                               // game's icon_item_static

    /// <summary>The edited inventory (valid after an OK result).</summary>
    public PlaytestInventory Result { get; private set; }

    private const string BuiltinPrefix = "★ ";

    public InventoryDialog(bool mm, PlaytestInventory initial)
    {
        _mm = mm; _oot = !mm;
        Result = initial.Clone();

        // #10b: load the game's item-icon sheet (icon_item_static) from the configured ROM, so each
        // entry can show its real in-game sprite. Best-effort — no ROM/icons just means text-only.
        try
        {
            string? romPath = mm ? EditorSettings.MmRomPath : EditorSettings.OotRomPath;
            if (!string.IsNullOrWhiteSpace(romPath) && File.Exists(romPath))
                _icons = new Rom.ItemIconSource(new Rom.RomImage(romPath));
        }
        catch { _icons = null; }

        Text = mm ? "Playtest Inventory — MM" : "Playtest Inventory — OoT";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(904, 624);   // wide enough for 6 composite cells (icon + control) + scrollbar
        MinimumSize = new Size(560, 480);
        BackColor = BgDark; ForeColor = FgNormal;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // ── Header: row 1 = presets, row 2 = hearts + player name + sprite toggle, row 3 = hint ──
        var header = new Panel { Dock = DockStyle.Top, Height = 102, BackColor = BgDark };

        header.Controls.Add(new Label { Text = "Preset:", Left = 12, Top = 14, AutoSize = true, ForeColor = FgNormal });
        _presetCombo = new ComboBox
        {
            Left = 64, Top = 11, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
        };
        RefreshPresetList();
        _presetCombo.SelectedIndexChanged += (_, _) => { };   // load is explicit via the Load button
        header.Controls.Add(_presetCombo);

        var load   = MkButton("Load",      290, 10, 64); load.Click += (_, _) => LoadSelectedPreset();
        var saveAs = MkButton("Save As…",  358, 10, 78); saveAs.Click += (_, _) => SaveAsPreset();
        var del    = MkButton("Delete",    440, 10, 64); del.Click += (_, _) => DeleteSelectedPreset();
        var reset  = MkButton("Reset",     508, 10, 60); reset.Click += (_, _) => LoadInto(PlaytestInventory.Default(_oot));
        header.Controls.AddRange([load, saveAs, del, reset]);

        // Row 2: hearts | player name | sprite toggle — spaced so nothing overlaps.
        header.Controls.Add(new Label { Text = "Hearts:", Left = 12, Top = 47, AutoSize = true, ForeColor = FgNormal });
        _hearts = new NumericUpDown
        {
            Left = 66, Top = 44, Width = 54, Minimum = 1, Maximum = 20, Value = Math.Clamp(initial.Hearts, 1, 20),
            BackColor = BgInput, ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle,
        };
        header.Controls.Add(_hearts);

        // #21: player name (default "Link"; persisted with the inventory). Max 8 chars (OoT name field).
        header.Controls.Add(new Label { Text = "Player name:", Left = 144, Top = 47, AutoSize = true, ForeColor = FgNormal });
        _playerName = new TextBox
        {
            Left = 224, Top = 44, Width = 96, MaxLength = 8,
            Text = string.IsNullOrWhiteSpace(initial.PlayerName) ? "Link" : initial.PlayerName,
            BackColor = BgInput, ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle,
        };
        header.Controls.Add(_playerName);

        // #10b: live toggle for the in-game item sprites (persisted; default on).
        var spriteToggle = new CheckBox
        {
            Left = 344, Top = 45, Width = 150, AutoSize = false, Height = 22, ForeColor = FgNormal,
            Text = "Show item sprites", Checked = EditorSettings.InventorySprites, Font = new Font("Segoe UI", 8f),
        };
        spriteToggle.CheckedChanged += (_, _) => { EditorSettings.InventorySprites = spriteToggle.Checked; RefreshIcons(); };
        header.Controls.Add(spriteToggle);

        // #5: Z-targeting mode (persisted with the inventory; applied on every engine via gSaveContext.zTargetSetting).
        header.Controls.Add(new Label { Text = "Z-Target:", Left = 506, Top = 47, AutoSize = true, ForeColor = FgNormal });
        _zTarget = new ComboBox
        {
            Left = 566, Top = 44, Width = 96, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
        };
        _zTarget.Items.AddRange(["Toggle", "Hold"]);   // index 0 = Switch/Toggle, 1 = Hold
        _zTarget.SelectedIndex = initial.ZTargetHold ? 1 : 0;
        header.Controls.Add(_zTarget);

        // Row 3: the hint, on its own line so it can't collide with the player-name field.
        header.Controls.Add(new Label
        {
            Left = 12, Top = 76, AutoSize = true, ForeColor = Color.FromArgb(150, 150, 150), Font = new Font("Segoe UI", 7.5f),
            Text = "Tiered upgrades grant everything up to the chosen tier. Items with a count get the matching capacity upgrade.",
        });

        // ── Scrollable content ──
        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(8), BackColor = BgDark,
        };

        // Tier dropdowns.
        var tierTable = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 0, 0, 8) };
        var tiers = InventoryCatalog.Tiers(_oot);
        foreach (var t in tiers)
        {
            var lbl = new Label { Text = t.Label, AutoSize = true, ForeColor = FgNormal, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) };
            var combo = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Margin = new Padding(3, 3, 18, 3) };
            combo.Items.AddRange(t.Options);
            combo.SelectedIndex = Math.Clamp(initial.Tier(t.Key), 0, t.Options.Length - 1);
            _combos[t.Key] = combo;
            tierTable.Controls.Add(lbl); tierTable.Controls.Add(combo);
        }
        content.Controls.Add(tierTable);

        // Toggle groups (checkbox grids in game-menu order).
        foreach (var g in InventoryCatalog.Groups(_oot))
        {
            content.Controls.Add(new Label
            {
                // UseMnemonic=false: an '&' in a WinForms caption is an accelerator, so "Medallions & Stones"
                // rendered as "Medallions _Stones" (underlined S). Disable it so the literal text shows.
                Text = g.Name, AutoSize = true, ForeColor = Color.FromArgb(140, 190, 255), UseMnemonic = false,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold), Margin = new Padding(2, 8, 2, 2),
            });
            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = g.Columns, Margin = new Padding(2, 0, 2, 4) };
            foreach (var (key, label) in g.Items)
            {
                if (label == "—") { grid.Controls.Add(new Label { Width = CellW }); continue; }   // spacer to keep grid alignment
                // Every item is a uniform composite cell: [icon][control]. #13 ammo items get a count
                // spinner instead of a checkbox. Composite panels keep the icon clear of the checkbox glyph
                // and the text, instead of overlapping it.
                grid.Controls.Add(InventoryCatalog.AmmoFor(_oot, key) is { } ammo
                    ? MakeAmmoCell(key, label, ammo, initial.Amount(key))
                    : MakeToggleCell(key, label, initial.Has(key)));
            }
            content.Controls.Add(grid);
        }

        // ── Footer ──
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = BgDark };
        var ok = MkButton("OK", 333, 9, 90); ok.BackColor = Accent; ok.ForeColor = Color.White;
        ok.Click += (_, _) => { Result = ReadFrom(); DialogResult = DialogResult.OK; Close(); };
        var cancel = MkButton("Cancel", 431, 9, 84); cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        footer.Controls.AddRange([ok, cancel]);

        Controls.Add(content);
        Controls.Add(footer);
        Controls.Add(header);
        AcceptButton = ok; CancelButton = cancel;
    }

    // A left icon column shared by every item cell, so the sprite never overlaps the checkbox glyph or text.
    private Label CellIcon(string key)
    {
        var icon = new Label { Left = 2, Top = 1, Width = IconW, Height = 24, Image = IconFor(key),
                               ImageAlign = ContentAlignment.MiddleCenter };
        _cellIcons[key] = icon;
        return icon;
    }

    // A toggle item: [icon] [checkbox + name]. UseMnemonic=false so '&' and long names show in full.
    private Panel MakeToggleCell(string key, string label, bool isChecked)
    {
        var cell = new Panel { Width = CellW, Height = 26, Margin = new Padding(2) };
        var cb = new CheckBox
        {
            Text = label, AutoSize = false, Left = IconW + 4, Top = 0, Width = CellW - IconW - 6, Height = 26,
            ForeColor = FgNormal, UseMnemonic = false, Checked = isChecked, Font = new Font("Segoe UI", 8f),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _checks[key] = cb;
        cell.Controls.Add(CellIcon(key)); cell.Controls.Add(cb);
        return cell;
    }

    // #13: a varying-amount item: [icon] [name] [0..capacity spinner]. The count drives ammo[slot] at
    // playtest and auto-grants the requisite capacity upgrade (see N64SavePokes / fork).
    private Panel MakeAmmoCell(string key, string label, AmmoSpec ammo, int initial)
    {
        var cell = new Panel { Width = CellW, Height = 26, Margin = new Padding(2) };
        var lbl = new Label
        {
            Text = label, Left = IconW + 4, Top = 0, Width = CellW - IconW - 4 - 42, Height = 26, ForeColor = FgNormal,
            UseMnemonic = false, Font = new Font("Segoe UI", 8f), TextAlign = ContentAlignment.MiddleLeft,
        };
        var num = new NumericUpDown
        {
            Left = CellW - 40, Top = 2, Width = 38, Height = 22, Minimum = 0, Maximum = ammo.Max,
            Value = Math.Clamp(initial, 0, ammo.Max), BackColor = BgInput, ForeColor = FgNormal,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 8f),
        };
        _spinners[key] = num;
        cell.Controls.Add(CellIcon(key)); cell.Controls.Add(lbl); cell.Controls.Add(num);
        return cell;
    }

    // #10b: the (scaled) sprite for an inventory key — the ROM's ITEM icon, or a generic note for songs
    // with no icon. Null when sprites are off or unavailable (entry stays text-only). Cached per key.
    private Image? IconFor(string key)
    {
        if (!EditorSettings.InventorySprites) return null;
        if (_iconCache.TryGetValue(key, out var cached)) return cached;
        Image? img = null;
        int idx = InventoryIcons.IconIndex(key, _mm);
        if (idx >= 0 && _icons is { Available: true })
        {
            var raw = _icons.Icon(idx);
            if (raw != null) img = new Bitmap(raw, new Size(20, 20));
        }
        if (img == null && InventoryIcons.IsSong(key)) img = new Bitmap(InventoryIcons.NoteGlyph(), new Size(20, 20));
        _iconCache[key] = img;
        return img;
    }

    // Re-apply sprites to every cell's icon column after the toggle changes.
    private void RefreshIcons()
    {
        _iconCache.Clear();
        foreach (var (key, icon) in _cellIcons) icon.Image = IconFor(key);
    }

    private void RefreshPresetList()
    {
        _presetCombo.Items.Clear();
        foreach (var n in PlaytestInventory.PresetNames) _presetCombo.Items.Add(BuiltinPrefix + n);
        foreach (var n in EditorSettings.GetInventoryPresetNames(_mm)) _presetCombo.Items.Add(n);
        if (_presetCombo.Items.Count > 0) _presetCombo.SelectedIndex = 0;
    }

    private void LoadSelectedPreset()
    {
        if (_presetCombo.SelectedItem is not string sel) return;
        if (sel.StartsWith(BuiltinPrefix, StringComparison.Ordinal))
            LoadInto(PlaytestInventory.Preset(sel[BuiltinPrefix.Length..], _oot));
        else
        {
            var json = EditorSettings.GetInventoryPreset(_mm, sel);
            if (json != null) LoadInto(PlaytestInventory.FromJson(json));
        }
    }

    private void SaveAsPreset()
    {
        string? name = Prompt("Save inventory preset as:", "Save Preset",
            _presetCombo.SelectedItem is string s && !s.StartsWith(BuiltinPrefix, StringComparison.Ordinal) ? s : "");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (name.StartsWith(BuiltinPrefix, StringComparison.Ordinal)) { Warn("That name is reserved."); return; }
        EditorSettings.SaveInventoryPreset(_mm, name, ReadFrom().ToJson());
        RefreshPresetList();
        _presetCombo.SelectedItem = name;
    }

    private void DeleteSelectedPreset()
    {
        if (_presetCombo.SelectedItem is not string sel || sel.StartsWith(BuiltinPrefix, StringComparison.Ordinal))
        { Warn("Pick a user-saved preset to delete."); return; }
        if (MessageBox.Show(this, $"Delete preset \"{sel}\"?", "Delete Preset",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        EditorSettings.DeleteInventoryPreset(_mm, sel);
        RefreshPresetList();
    }

    // Reflect an inventory into the controls.
    private void LoadInto(PlaytestInventory inv)
    {
        _hearts.Value = Math.Clamp(inv.Hearts, (int)_hearts.Minimum, (int)_hearts.Maximum);
        _playerName.Text = string.IsNullOrWhiteSpace(inv.PlayerName) ? "Link" : inv.PlayerName;
        foreach (var (key, combo) in _combos)
            combo.SelectedIndex = Math.Clamp(inv.Tier(key), 0, combo.Items.Count - 1);
        foreach (var (key, cb) in _checks) cb.Checked = inv.Has(key);
        foreach (var (key, num) in _spinners) num.Value = Math.Clamp(inv.Amount(key), (int)num.Minimum, (int)num.Maximum);
        _zTarget.SelectedIndex = inv.ZTargetHold ? 1 : 0;
    }

    // Build an inventory from the current control state.
    private PlaytestInventory ReadFrom()
    {
        var inv = new PlaytestInventory
        {
            Hearts = (int)_hearts.Value,
            PlayerName = string.IsNullOrWhiteSpace(_playerName.Text) ? "Link" : _playerName.Text.Trim(),
            ZTargetHold = _zTarget.SelectedIndex == 1,
        };
        foreach (var (key, combo) in _combos) if (combo.SelectedIndex > 0) inv.SetTier(key, combo.SelectedIndex);
        foreach (var (key, cb) in _checks) if (cb.Checked) inv.Set(key, true);
        foreach (var (key, num) in _spinners) if (num.Value > 0) inv.SetAmount(key, (int)num.Value);
        return inv;
    }

    // ── small helpers ──
    private static Button MkButton(string t, int x, int y, int w) => new()
    { Text = t, Left = x, Top = y, Width = w, Height = 26, BackColor = Color.FromArgb(60, 60, 65),
      ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f) };

    private void Warn(string m) => MessageBox.Show(this, m, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static string? Prompt(string text, string title, string initial)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(320, 110), MinimizeBox = false, MaximizeBox = false,
            BackColor = BgDark, ForeColor = FgNormal,
        };
        var lbl = new Label { Text = text, Left = 12, Top = 12, AutoSize = true, ForeColor = FgNormal };
        var box = new TextBox { Text = initial, Left = 12, Top = 36, Width = 296, BackColor = BgInput, ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 142, Top = 70, Width = 78, FlatStyle = FlatStyle.Flat, ForeColor = FgNormal };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 228, Top = 70, Width = 80, FlatStyle = FlatStyle.Flat, ForeColor = FgNormal };
        f.Controls.AddRange([lbl, box, ok, cancel]);
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(box.Text) ? box.Text : null;
    }
}
