using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

/// <summary>Which tab the Options dialog opens on.</summary>
public enum OptionsTab { General, Viewports, AutoSave, Playtest, CrossGame, Logging, Discord }

/// <summary>
/// Editor preferences, organised into Hammer-style tabs:
///   • General   — default grid, snap, ROM-safety override
///   • Viewports — sky / entity visibility defaults
///   • Auto-Save — backup interval + crash recovery
///   • Playtest  — SoH / 2Ship engine builds and the Project64 emulator
///   • Cross-Game — borrow the other game's music/textures from a ROM/O2R
/// Persists via <see cref="EditorSettings"/>. Events tell the host to react (reload textures,
/// re-apply the grid size, redraw viewports, restart the auto-save timer).
/// </summary>
public sealed class OptionsDialog : Form
{
    private static readonly Color BgDark  = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);
    private static readonly Color Accent   = Color.FromArgb(0, 122, 204);

    private static readonly int[] GridSizes = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024];

    private readonly bool _nativeIsOoT;
    private readonly int  _initialGrid;

    // General
    private readonly ComboBox _gridCombo;
    private readonly ComboBox _lighting;
    private readonly CheckBox _snap, _allowOverwrite, _cullFaces, _recentColors;
    private readonly ComboBox? _defaultActor;          // default Entity-tool placement actor (per game)
    private readonly List<ushort> _defaultActorIds = [];
    // Viewports
    private readonly CheckBox _sky, _grid3d, _ent3d, _ent2d, _trilinear, _tintGray, _tintEnv, _perLevelTint, _shiftSameTex;
    // Auto-save
    private readonly CheckBox _autoSave;
    private readonly CheckBox _autoDetect;
    private readonly NumericUpDown _autoInterval, _autoCount;
    // Playtest
    private readonly TextBox _sohBox, _twoShipBox, _pj64Box;
    private readonly Label _sohStatus, _twoShipStatus;
    private readonly CheckBox _n64Debug;
    // Cross-game (paths for both games)
    private readonly TextBox _ootRom, _ootO2r, _mmRom, _mmO2r;
    private readonly CheckBox _music, _textures;
    // Logging
    private readonly NumericUpDown _logMax;
    private readonly RadioButton _logSeparate, _logOneFile;
    // Discord Rich Presence
    private readonly CheckBox _discordEnabled, _discordShowMap, _discordShowGame;
    private readonly TextBox _discordAppId;

    /// <summary>Raised on OK when a Discord Rich Presence setting changed (host re-applies the presence).</summary>
    public event Action? DiscordChanged;
    /// <summary>Raised on OK when the cross-game source or texture toggle changed.</summary>
    public event Action? SourcesChanged;
    /// <summary>Raised on OK when an auto-save setting changed (host restarts the timer).</summary>
    public event Action? AutoSaveChanged;
    /// <summary>Raised on OK with the new default grid size when it changed.</summary>
    public event Action<int>? GridChanged;
    /// <summary>Raised on OK after view defaults change (host redraws the viewports).</summary>
    public event Action? ViewChanged;
    /// <summary>Raised on OK when the default placement actor changed (host re-selects the entity combo).</summary>
    public event Action? DefaultActorChanged;

    public OptionsDialog(bool nativeIsOoT, int currentGrid = 64, OptionsTab initialTab = OptionsTab.General,
                         IReadOnlyList<(ushort id, string name)>? placementActors = null)
    {
        _nativeIsOoT = nativeIsOoT;
        _initialGrid = currentGrid;
        string other = nativeIsOoT ? "Majora's Mask" : "Ocarina of Time";

        Text = "Options";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(588, 516);
        BackColor = BgDark; ForeColor = FgNormal;
        Font = new Font("Segoe UI", 8.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var tabs = new TabControl { Location = new Point(8, 8), Size = new Size(572, 456) };

        // ── General ─────────────────────────────────────────────────────────
        var gen = Page("General");
        int y = 14;
        gen.Controls.Add(Header("GRID & SNAPPING", y)); y += 26;
        gen.Controls.Add(Label("Default grid size:", 16, y));
        _gridCombo = new ComboBox
        {
            Left = 204, Top = y - 3, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList,   // #2: clear the 184px label
            BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
        };
        foreach (var g in GridSizes) _gridCombo.Items.Add($"{g} u");
        _gridCombo.SelectedIndex = Math.Max(0, Array.IndexOf(GridSizes, ClampGrid(currentGrid)));
        gen.Controls.Add(_gridCombo); y += 30;
        _snap = Check("Snap to grid", 16, y, ViewOptions.SnapToGrid); gen.Controls.Add(_snap); y += 34;

        if (placementActors != null)
        {
            gen.Controls.Add(Header($"ENTITY TOOL ({(nativeIsOoT ? "OoT" : "MM")})", y)); y += 26;
            gen.Controls.Add(Label("Default placement entity:", 16, y));
            _defaultActor = new ComboBox
            {
                Left = 200, Top = y - 3, Width = 246, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
            };
            int cur = EditorSettings.DefaultActor(!nativeIsOoT), sel = 0;
            foreach (var (id, name) in placementActors)
            {
                if (id == MegatonHammer.Tools.EntityTool.EditorDummyLinkId) _defaultActor.Items.Add($"{name}");
                else _defaultActor.Items.Add($"0x{id:X4}  {name}");
                if (id == cur) sel = _defaultActorIds.Count;
                _defaultActorIds.Add(id);
            }
            if (_defaultActor.Items.Count > 0) _defaultActor.SelectedIndex = sel;
            gen.Controls.Add(_defaultActor); y += 34;
        }

        gen.Controls.Add(Header("ROM SAFETY", y)); y += 26;
        gen.Controls.Add(Note("Original game ROMs are kept read-only — the editor always produces a NEW ROM. " +
                              "Only override this if you really intend to overwrite an original.", y)); y += 36;
        _allowOverwrite = Check("Allow overwriting original ROMs (unsafe)", 16, y, RomSafety.AllowOverwriteOriginals);
        gen.Controls.Add(_allowOverwrite); y += 34;

        gen.Controls.Add(Header("COMPILE / EXPORT", y)); y += 26;
        gen.Controls.Add(Note("Drop brush faces fully buried against a neighbouring brush from the compiled " +
                              "geometry (render-only, like nodraw — collision is untouched). Trims triangles " +
                              "and hides internal faces if the camera clips a wall. Off by default.", y)); y += 48;
        _cullFaces = Check("Cull unseen faces on compile", 16, y, EditorSettings.CullUnseenFaces); y += 34;
        gen.Controls.Add(_cullFaces);

        gen.Controls.Add(Header("INTERFACE", y)); y += 26;
        _recentColors = Check("Colour-code recent files by game  (OoT/SoH blue, MM/2Ship purple)", 16, y, EditorSettings.ColorCodeRecentByGame);
        gen.Controls.Add(_recentColors);

        // ── Viewports ───────────────────────────────────────────────────────
        var vw = Page("Viewports");
        y = 14;
        vw.Controls.Add(Header("3D VIEW", y)); y += 26;
        _sky   = Check("Render scene sky", 16, y, ViewOptions.ShowSky); vw.Controls.Add(_sky); y += 26;
        _grid3d = Check("Show ground grid", 16, y, ViewOptions.ShowGrid3D); vw.Controls.Add(_grid3d); y += 26;
        _ent3d = Check("Show entities / actors", 16, y, ViewOptions.ShowEntities3D); vw.Controls.Add(_ent3d); y += 34;
        vw.Controls.Add(Header("TEXTURES", y)); y += 26;
        _trilinear = Check("Trilinear texture filtering  (off = crisp, N64-style point sampling)", 16, y, ViewOptions.TrilinearFilter);
        vw.Controls.Add(_trilinear); y += 26;
        _tintGray = Check("Tint grayscale (i8/ia16) textures by their in-game prim colour", 16, y, EditorSettings.TintGrayscaleTextures);
        vw.Controls.Add(_tintGray); y += 26;
        _tintEnv = Check("    └ also fall back to env colour when no prim tint (may over-tint)", 16, y, EditorSettings.TintGrayscaleWithEnv);
        vw.Controls.Add(_tintEnv); y += 26;
        _perLevelTint = Check("Preview each level's textures with its in-game colour (per-level vertex hue)", 16, y, EditorSettings.PerLevelTextureTint);
        vw.Controls.Add(_perLevelTint); y += 34;
        vw.Controls.Add(Header("LIGHTING", y)); y += 26;
        vw.Controls.Add(new Label { Text = "Method", Left = 16, Top = y, Width = 56, AutoSize = true });
        _lighting = new ComboBox { Left = 80, Top = y - 4, Width = 360, DropDownStyle = ComboBoxStyle.DropDownList };
        _lighting.Items.AddRange(new object[]
        {
            "Fullbright (textures show true colour, no lighting baked)",
            "Shaded (bake scene environment lighting; indoor reads dark)",
        });
        _lighting.SelectedIndex = Math.Clamp(EditorSettings.LightingMethod - 1, 0, 1);
        vw.Controls.Add(_lighting); y += 34;
        vw.Controls.Add(Header("2D VIEWS", y)); y += 26;
        _ent2d = Check("Show entity markers", 16, y, ViewOptions.ShowEntities2D); vw.Controls.Add(_ent2d); y += 34;
        vw.Controls.Add(Header("FACE EDITING (TEXTURE TOOL)", y)); y += 26;
        _shiftSameTex = Check("Shift-click selects only same-texture faces  (off = all adjacent coplanar faces)",
                              16, y, EditorSettings.ShiftSelectSameTextureOnly);
        vw.Controls.Add(_shiftSameTex);

        // ── Auto-Save ───────────────────────────────────────────────────────
        var au = Page("Auto-Save");
        y = 14;
        au.Controls.Add(Header("AUTO-SAVE & CRASH RECOVERY", y)); y += 26;
        au.Controls.Add(Note("Periodically writes a recoverable backup of the project. After a crash the next launch " +
                             "offers the most recent backup.", y)); y += 36;
        _autoSave = Check("Auto-save backups of the project", 16, y, EditorSettings.AutoSaveEnabled);
        au.Controls.Add(_autoSave); y += 30;
        // #17: the Label helper is a fixed 184px wide, so put each spinner on its own row with the field
        // clear of the label (x=220 > 16+184) — previously the fields sat under the overlapping labels.
        au.Controls.Add(Label("Every (minutes):", 30, y));
        _autoInterval = Spin(220, y - 3, 1, 60, EditorSettings.AutoSaveIntervalMinutes); au.Controls.Add(_autoInterval); y += 30;
        au.Controls.Add(Label("Keep backups:", 30, y));
        _autoCount = Spin(220, y - 3, 1, 100, EditorSettings.AutoSaveBackupCount); au.Controls.Add(_autoCount);

        // ── Playtest ────────────────────────────────────────────────────────
        var pt = Page("Playtest");
        y = 14;
        pt.Controls.Add(Header("MEGATON HAMMER ENGINE FORKS (SoH / 2Ship)", y)); y += 24;
        pt.Controls.Add(Note("Point to each Megaton Hammer engine-fork executable. The matching game archive " +
                             "(oot.otr / mm.o2r) is generated by the engine the first time it runs against your ROM.", y)); y += 36;

        pt.Controls.Add(Label("Ship of Harkinian (OoT) — soh.exe:", 16, y)); y += 20;
        _sohBox = Input(EditorSettings.SohExePath, 16, y, 430); pt.Controls.Add(_sohBox);
        pt.Controls.Add(Browse(452, y - 1, () => PickExe(_sohBox))); y += 22;
        _sohStatus = StatusLabel(16, y); pt.Controls.Add(_sohStatus); y += 26;

        pt.Controls.Add(Label("2Ship (Majora's Mask) — 2ship.exe:", 16, y)); y += 20;
        _twoShipBox = Input(EditorSettings.TwoShipExePath, 16, y, 430); pt.Controls.Add(_twoShipBox);
        pt.Controls.Add(Browse(452, y - 1, () => PickExe(_twoShipBox))); y += 22;
        _twoShipStatus = StatusLabel(16, y); pt.Controls.Add(_twoShipStatus); y += 30;

        pt.Controls.Add(Header("N64 EMULATOR — PROJECT64 (MEGATON HAMMER FORK)", y)); y += 24;
        pt.Controls.Add(Label("Project64 — Project64.exe:", 16, y)); y += 20;
        _pj64Box = Input(EditorSettings.Project64Path, 16, y, 430); pt.Controls.Add(_pj64Box);
        pt.Controls.Add(Browse(452, y - 1, () => PickExe(_pj64Box))); y += 26;
        _n64Debug = Check("Enable N64 debug controls (L+R+Z map-select, L+D-pad no-clip — no debug inventory)",
                          16, y, EditorSettings.PlaytestN64DebugControls);
        pt.Controls.Add(_n64Debug); y += 30;

        // #12b: startup auto-detection of base ROMs (validated by MD5) + forks at their known build paths.
        // The "Detect & verify now" button shares the checkbox row (the page bottom was clipping it).
        pt.Controls.Add(Header("ASSET AUTO-DETECTION", y)); y += 24;
        _autoDetect = Check("Auto-detect ROMs + forks at startup (MD5-verified)", 16, y + 3, EditorSettings.AutoDetectAssetsOnStartup);
        _autoDetect.Width = 360;   // don't let the default 520px checkbox overlap (and hide) the button beside it
        pt.Controls.Add(_autoDetect);
        var detectNow = new Button { Text = "Detect && verify now", Location = new Point(396, y), Width = 150, Height = 24,
            BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        detectNow.Click += (_, _) => DetectAndReport();
        pt.Controls.Add(detectNow);
        detectNow.BringToFront();   // ensure it draws in front of any sibling control sharing its row

        _sohBox.TextChanged     += (_, _) => UpdateEngineStatus();
        _twoShipBox.TextChanged += (_, _) => UpdateEngineStatus();
        UpdateEngineStatus();

        // ── Cross-Game ──────────────────────────────────────────────────────
        var cg = Page("Cross-Game");
        y = 14;
        cg.Controls.Add(Header("CROSS-GAME ASSET SOURCES", y)); y += 24;
        cg.Controls.Add(Note($"Configure a ROM or O2R for BOTH games. This {(nativeIsOoT ? "Ocarina of Time" : "Majora's Mask")} " +
                             $"project borrows from the {other} entries below; the other fields are remembered for when you " +
                             "work on that game. Cross-game songs whose instruments are missing here are omitted.", y)); y += 44;

        cg.Controls.Add(Header("OCARINA OF TIME", y)); y += 22;
        cg.Controls.Add(Label("ROM (.z64/.n64):", 26, y));
        _ootRom = Input(EditorSettings.OotRomPath, 200, y - 3, 300); cg.Controls.Add(_ootRom);
        cg.Controls.Add(Browse(506, y - 4, () => Pick(_ootRom, "ROM (*.z64;*.n64;*.v64)|*.z64;*.n64;*.v64"))); y += 28;
        cg.Controls.Add(Label("O2R (.o2r):", 26, y));
        _ootO2r = Input(EditorSettings.OotO2RPath, 200, y - 3, 300); cg.Controls.Add(_ootO2r);
        cg.Controls.Add(Browse(506, y - 4, () => Pick(_ootO2r, "O2R archive (*.o2r)|*.o2r"))); y += 32;

        cg.Controls.Add(Header("MAJORA'S MASK", y)); y += 22;
        cg.Controls.Add(Label("ROM (.z64/.n64):", 26, y));
        _mmRom = Input(EditorSettings.MmRomPath, 200, y - 3, 300); cg.Controls.Add(_mmRom);
        cg.Controls.Add(Browse(506, y - 4, () => Pick(_mmRom, "ROM (*.z64;*.n64;*.v64)|*.z64;*.n64;*.v64"))); y += 28;
        cg.Controls.Add(Label("O2R (.o2r):", 26, y));
        _mmO2r = Input(EditorSettings.MmO2RPath, 200, y - 3, 300); cg.Controls.Add(_mmO2r);
        cg.Controls.Add(Browse(506, y - 4, () => Pick(_mmO2r, "O2R archive (*.o2r)|*.o2r"))); y += 34;

        _music = Check($"Enable {other} music in the scene-music list", 16, y, EditorSettings.EnableCrossGameMusic); y += 24;
        _textures = Check($"Enable {other} texture areas in the texture browser", 16, y, EditorSettings.EnableCrossGameTextures);
        cg.Controls.Add(_music); cg.Controls.Add(_textures);

        // ── Logging ─────────────────────────────────────────────────────────
        var lg = Page("Logging");
        y = 14;
        lg.Controls.Add(Header("PLAYTEST LOGS", y)); y += 24;
        lg.Controls.Add(Note("One log per playtest launch captures the full config, inventory, every room setting, " +
                             "the injection steps, and the engine's own log (crashes flagged). Saved under:\n" +
                             PlaytestLog.LogDir, y)); y += 48;

        lg.Controls.Add(Label("Maximum logs to keep:", 16, y));
        _logMax = Spin(220, y - 3, -1, 9999, EditorSettings.PlaytestLogMax); lg.Controls.Add(_logMax); y += 24;
        lg.Controls.Add(Note("Oldest logs beyond this are deleted on each launch.   0 = logging off.   -1 = keep all (no limit).", y)); y += 40;

        lg.Controls.Add(Header("LOG FILE MODE", y)); y += 26;
        _logSeparate = new RadioButton { Text = "Separate timestamped file per playtest (full history)", Left = 16, Top = y,
            AutoSize = true, ForeColor = FgNormal, Checked = !EditorSettings.PlaytestLogOneFile };
        lg.Controls.Add(_logSeparate); y += 24;
        _logOneFile = new RadioButton { Text = "One file per engine (PJ64-MM / PJ64-OoT / SoH / 2Ship) — overwrite each launch",
            Left = 16, Top = y, AutoSize = true, ForeColor = FgNormal, Checked = EditorSettings.PlaytestLogOneFile };
        lg.Controls.Add(_logOneFile);

        // ── Discord Rich Presence ───────────────────────────────────────────
        var dc = Page("Discord");
        y = 14;
        dc.Controls.Add(Header("DISCORD RICH PRESENCE", y)); y += 26;
        _discordEnabled = Check("Show my activity on Discord (Rich Presence)", 16, y, EditorSettings.DiscordRpcEnabled);
        dc.Controls.Add(_discordEnabled); y += 30;
        _discordShowMap = Check("Show the name of the map I'm editing  (“Editing: oot_pvpmap”)", 32, y, EditorSettings.DiscordShowMap);
        dc.Controls.Add(_discordShowMap); y += 26;
        _discordShowGame = Check("Show which game it's for  (“For Ocarina of Time” / “Majora's Mask” / “Zelda 64”)", 32, y, EditorSettings.DiscordShowGame);
        dc.Controls.Add(_discordShowGame); y += 34;

        dc.Controls.Add(Header("DISCORD APPLICATION ID", y)); y += 26;
        dc.Controls.Add(Label("Application (client) ID:", 16, y));
        _discordAppId = Input(EditorSettings.DiscordAppId, 200, y - 3, 356); dc.Controls.Add(_discordAppId); y += 30;
        dc.Controls.Add(new Label
        {
            Left = 16, Top = y, Width = 540, Height = 96, ForeColor = Color.FromArgb(150, 150, 150),
            Font = new Font("Segoe UI", 8f),
            Text = "Pre-filled with the official “Megaton Hammer” Discord application (its name is the top line of "
                 + "the presence, and it provides the mh / oot / mm icons). You normally don't need to change this "
                 + "— only override it with your own Application ID (from discord.com/developers/applications) if "
                 + "you want a different title/icons. Clear it to disable. Nothing is uploaded except the two lines above.",
        }); y += 100;

        tabs.TabPages.AddRange([gen, vw, au, pt, cg, lg, dc]);
        tabs.SelectedIndex = (int)initialTab;
        Controls.Add(tabs);

        // ── OK / Cancel ─────────────────────────────────────────────────────
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(408, 476), Width = 80,
                              BackColor = Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(496, 476), Width = 80,
                                 BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        ok.Click += (_, _) => Commit();
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
    }

    private static int ClampGrid(int g) => GridSizes.Contains(g) ? g : 64;

    private void UpdateEngineStatus()
    {
        SetStatus(_sohStatus,     _sohBox.Text,     mm: false);
        SetStatus(_twoShipStatus, _twoShipBox.Text, mm: true);
    }

    private static void SetStatus(Label lbl, string exePath, bool mm)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        { lbl.Text = "○ not configured"; lbl.ForeColor = Color.FromArgb(150, 150, 150); return; }
        if (EditorSettings.GameArchiveInDir(Path.GetDirectoryName(exePath), mm))
        { lbl.Text = "● ready — game archive found"; lbl.ForeColor = Color.FromArgb(120, 200, 120); }
        else
        { lbl.Text = "● exe found, but no game archive yet (run the engine once to generate it)";
          lbl.ForeColor = Color.FromArgb(220, 180, 90); }
    }

    private void Commit()
    {
        // General — grid
        int newGrid = GridSizes[Math.Max(0, _gridCombo.SelectedIndex)];
        EditorSettings.GridSize = newGrid;
        if (newGrid != _initialGrid) GridChanged?.Invoke(newGrid);

        // General — snap (live + persisted)
        ViewOptions.SnapToGrid = _snap.Checked;
        EditorSettings.SnapToGrid = _snap.Checked;
        EditorSettings.CullUnseenFaces = _cullFaces.Checked;
        EditorSettings.ColorCodeRecentByGame = _recentColors.Checked;

        // General — default placement actor (per game)
        if (_defaultActor != null && _defaultActor.SelectedIndex >= 0 && _defaultActor.SelectedIndex < _defaultActorIds.Count)
        {
            ushort id = _defaultActorIds[_defaultActor.SelectedIndex];
            if (id != EditorSettings.DefaultActor(!_nativeIsOoT)) { EditorSettings.SetDefaultActor(!_nativeIsOoT, id); DefaultActorChanged?.Invoke(); }
        }

        // General — ROM safety override (session-only; warn before enabling)
        bool wantOverwrite = _allowOverwrite.Checked;
        if (wantOverwrite && !RomSafety.AllowOverwriteOriginals &&
            MessageBox.Show(this,
                "This lets the editor overwrite original game ROMs. Originals are normally kept read-only and " +
                "the editor always produces a NEW ROM.\n\nEnable anyway?",
                "Safety Override", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            wantOverwrite = false;
        RomSafety.AllowOverwriteOriginals = wantOverwrite;

        // Viewports (live + persisted)
        ViewOptions.ShowSky        = EditorSettings.ShowSky        = _sky.Checked;
        ViewOptions.ShowGrid3D     = EditorSettings.ShowGrid3D     = _grid3d.Checked;
        ViewOptions.ShowEntities3D = EditorSettings.ShowEntities3D = _ent3d.Checked;
        ViewOptions.ShowEntities2D = EditorSettings.ShowEntities2D = _ent2d.Checked;
        ViewOptions.TrilinearFilter = EditorSettings.TrilinearFilter = _trilinear.Checked;
        bool tintChanged = EditorSettings.TintGrayscaleTextures != _tintGray.Checked
                           || EditorSettings.TintGrayscaleWithEnv != _tintEnv.Checked
                           || EditorSettings.PerLevelTextureTint != _perLevelTint.Checked;
        EditorSettings.TintGrayscaleTextures = _tintGray.Checked;
        EditorSettings.TintGrayscaleWithEnv = _tintEnv.Checked;
        EditorSettings.PerLevelTextureTint = _perLevelTint.Checked;
        EditorSettings.LightingMethod = _lighting.SelectedIndex + 1;   // 1 = full-bright, 2 = baked env
        EditorSettings.ShiftSelectSameTextureOnly = _shiftSameTex.Checked;
        ViewChanged?.Invoke();
        // The tint is baked at decode time; changing it must drop cached decodes so they re-tint.
        if (tintChanged) SourcesChanged?.Invoke();

        // Playtest engines / emulator
        EditorSettings.SohExePath     = Blank(_sohBox.Text);
        EditorSettings.TwoShipExePath = Blank(_twoShipBox.Text);
        EditorSettings.Project64Path  = Blank(_pj64Box.Text);
        EditorSettings.AutoDetectAssetsOnStartup = _autoDetect.Checked;
        EditorSettings.PlaytestN64DebugControls = _n64Debug.Checked;

        // Cross-game (both games' sources kept independently). Detect a change to the source the
        // current project actually borrows from (the opposite game) so we only reload when needed.
        string? oldOppRom = EditorSettings.OppositeRomPath(_nativeIsOoT);
        string? oldOppO2r = EditorSettings.OppositeO2RPath(_nativeIsOoT);
        bool oldTex = EditorSettings.EnableCrossGameTextures;

        EditorSettings.OotRomPath = Blank(_ootRom.Text);
        EditorSettings.OotO2RPath = Blank(_ootO2r.Text);
        EditorSettings.MmRomPath  = Blank(_mmRom.Text);
        EditorSettings.MmO2RPath  = Blank(_mmO2r.Text);
        EditorSettings.EnableCrossGameMusic = _music.Checked;
        EditorSettings.EnableCrossGameTextures = _textures.Checked;

        if (oldOppRom != EditorSettings.OppositeRomPath(_nativeIsOoT) ||
            oldOppO2r != EditorSettings.OppositeO2RPath(_nativeIsOoT) ||
            oldTex != EditorSettings.EnableCrossGameTextures)
            SourcesChanged?.Invoke();

        // Auto-save
        EditorSettings.AutoSaveEnabled = _autoSave.Checked;
        EditorSettings.AutoSaveIntervalMinutes = (int)_autoInterval.Value;
        EditorSettings.AutoSaveBackupCount = (int)_autoCount.Value;
        AutoSaveChanged?.Invoke();

        // Logging
        EditorSettings.PlaytestLogMax = (int)_logMax.Value;
        EditorSettings.PlaytestLogOneFile = _logOneFile.Checked;

        // Discord Rich Presence
        EditorSettings.DiscordRpcEnabled = _discordEnabled.Checked;
        EditorSettings.DiscordShowMap = _discordShowMap.Checked;
        EditorSettings.DiscordShowGame = _discordShowGame.Checked;
        EditorSettings.DiscordAppId = _discordAppId.Text.Trim();
        DiscordChanged?.Invoke();
    }

    // ── tiny control helpers ────────────────────────────────────────────────
    // #12b: run auto-detect now, refresh the path boxes from anything found, and report ROM MD5 status.
    private void DetectAndReport()
    {
        // Persist whatever the user has typed first so AutoDetect only fills genuinely-empty paths.
        if (!string.IsNullOrWhiteSpace(_sohBox.Text))     EditorSettings.SohExePath     = _sohBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_twoShipBox.Text)) EditorSettings.TwoShipExePath = _twoShipBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_pj64Box.Text))    EditorSettings.Project64Path  = _pj64Box.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_ootRom.Text))     EditorSettings.OotRomPath     = _ootRom.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_mmRom.Text))      EditorSettings.MmRomPath      = _mmRom.Text.Trim();

        var det = Rom.RomFingerprint.AutoDetect();

        // Reflect anything newly found back into the visible boxes.
        _sohBox.Text     = EditorSettings.SohExePath     ?? "";
        _twoShipBox.Text = EditorSettings.TwoShipExePath ?? "";
        _pj64Box.Text    = EditorSettings.Project64Path  ?? "";
        _ootRom.Text     = EditorSettings.OotRomPath     ?? "";
        _mmRom.Text      = EditorSettings.MmRomPath      ?? "";

        var (ootOk, ootMsg) = Rom.RomFingerprint.CheckRom(EditorSettings.OotRomPath, oot: true);
        var (mmOk,  mmMsg)  = Rom.RomFingerprint.CheckRom(EditorSettings.MmRomPath,  oot: false);
        string report =
            "Detected / verified:\n\n" +
            (det.Found.Count > 0 ? "• " + string.Join("\n• ", det.Found) + "\n\n" : "(nothing new to fill)\n\n") +
            $"OoT ROM: {(ootOk ? "OK" : "⚠")} {ootMsg}\n" +
            $"MM ROM:  {(mmOk ? "OK" : "⚠")} {mmMsg}" +
            (det.Mismatched.Count > 0 ? "\n\nNote:\n• " + string.Join("\n• ", det.Mismatched) : "");
        MessageBox.Show(this, report, "Asset detection", MessageBoxButtons.OK,
            (ootOk || mmOk) ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    // AutoScroll so any page whose content is taller than the tab body gets a scrollbar instead of being
    // clipped (belt-and-suspenders with the taller dialog — covers long pages like Playtest/Viewports).
    private static TabPage Page(string title) => new() { Text = title, BackColor = BgDark, UseVisualStyleBackColor = false, AutoScroll = true };

    private NumericUpDown Spin(int x, int y, int min, int max, int val) => new()
    {
        Location = new Point(x, y), Width = 70, Minimum = min, Maximum = max,
        Value = Math.Clamp(val, min, max), BackColor = BgInput, ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle,
    };

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void PickExe(TextBox box)
    {
        using var dlg = new OpenFileDialog { Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*" };
        if (File.Exists(box.Text)) dlg.FileName = box.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
    }

    private void Pick(TextBox box, string filter)
    {
        using var dlg = new OpenFileDialog { Filter = filter };
        if (File.Exists(box.Text)) dlg.FileName = box.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
    }

    private static Label Header(string t, int y) => new()
    { Text = t, Left = 14, Top = y, Width = 540, Height = 20, ForeColor = Color.FromArgb(140, 190, 255),
      Font = new Font("Segoe UI", 8f, FontStyle.Bold) };
    private static Label Note(string t, int y) => new()
    { Text = t, Left = 14, Top = y, Width = 540, Height = 36, ForeColor = Color.FromArgb(150, 150, 150),
      Font = new Font("Segoe UI", 8f) };
    private static Label Label(string t, int x, int y) => new()
    { Text = t, Left = x, Top = y, Width = 184, ForeColor = FgNormal, Font = new Font("Segoe UI", 8.5f),
      TextAlign = ContentAlignment.MiddleLeft };
    private static Label StatusLabel(int x, int y) => new()
    { Left = x, Top = y, Width = 520, Height = 16, Font = new Font("Segoe UI", 8f), TextAlign = ContentAlignment.MiddleLeft };
    private static TextBox Input(string? t, int x, int y, int w) => new()
    { Text = t ?? "", Left = x, Top = y, Width = w, BackColor = BgInput, ForeColor = FgNormal,
      BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 8.5f) };
    private Button Browse(int x, int y, Action onClick)
    { var b = new Button { Text = "…", Left = x, Top = y, Width = 30, Height = 24, BackColor = Color.FromArgb(60, 60, 65),
        ForeColor = FgNormal, FlatStyle = FlatStyle.Flat }; b.Click += (_, _) => onClick(); return b; }
    private static CheckBox Check(string t, int x, int y, bool chk) => new()
    { Text = t, Left = x, Top = y, Width = 520, Checked = chk, ForeColor = FgNormal, Font = new Font("Segoe UI", 8.5f) };
}
