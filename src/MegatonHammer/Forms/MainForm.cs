using MegatonHammer.Editor;
using MegatonHammer.Textures;
using MegatonHammer.Tools;

namespace MegatonHammer.Forms;

public sealed class MainForm : Form, IMessageFilter
{
    private readonly GameConfig    _config;
    private readonly ViewportPanel _vp3D;
    private readonly ViewportPanel _vpTop;
    private readonly ViewportPanel _vpFront;
    private readonly ViewportPanel _vpSide;
    private readonly System.Windows.Forms.Timer _renderTimer;
    private DateTime _lastTick = DateTime.UtcNow;

    // ── Editor state ──────────────────────────────────────────────────────
    private readonly MapDocument     _document   = new();
    private readonly ActorDatabase   _actorDb;
    private readonly TextureLibrary  _textureLib = new();
    private readonly SelectTool      _selectTool;
    private readonly BrushTool       _brushTool;
    private readonly ClipTool        _clipTool;
    private readonly VertexTool      _vertexTool;
    private readonly PathTool        _pathTool;
    private readonly EntityTool      _entityTool;
    private readonly TextureTool     _textureTool;
    private readonly ShadePaintTool  _shadeTool;
    private readonly DecalTool       _decalTool;
    private readonly MagnifyTool     _magnifyTool;
    private readonly CameraTool      _cameraTool;
    private readonly HierarchyPanel  _hierarchyPanel;
    private readonly PropertiesPanel _propertiesPanel;
    private ImportedRoomsForm?       _roomsForm;
    private FaceEditDialog?          _faceEditDialog;
    private ShadePaintDialog?        _shadePaintDialog;
    private readonly TexturePanel    _texturePanel;
    private O2RTextureSource?        _o2rSource;
    private Rom.RomTextureSource?    _romSource;
    private Rom.RomTextureSource?    _crossRomSource;   // opposite-game ROM (cross-game textures)
    private ITool                    _activeTool;
    private string?                  _currentPath;
    private bool                     _dirty;
    private int _gridSize = 64;

    // ── UI refs that need post-init updates ───────────────────────────────
    private ToolStripStatusLabel? _statusLabel;
    private ToolStripStatusLabel? _statusCoords, _statusSize, _statusGrid;
    private ToolStripButton?      _btnSelect;
    private ToolStripButton?      _btnBrush;
    private ToolStripButton?      _btnClip;
    private ToolStripButton?      _btnVertex;
    private ToolStripButton?      _btnPath;
    private ToolStripButton?      _btnEntity;
    private ToolStripButton?      _btnTexture;
    private ToolStripButton?      _btnShade;
    private ToolStripButton?      _btnShadeColor;
    private ToolStripButton?      _btnDecal;
    private ToolStripButton?      _btnMagnify;
    private ToolStripButton?      _btnCamera;
    private System.Windows.Forms.Timer? _autoSaveTimer;
    private readonly bool _recoverPrompt;
    private readonly string? _recoveryFile;
    private ToolStripMenuItem? _openRecentMenu;
    private readonly string? _pendingOpenPath;   // project to open on first show (jump-list launch)
    private GLViewport? _lastFocused2D;           // 2D view that arrow-nudge falls back to

    // Set by Close Project when the user re-picks a game target at the splash: the Program run-loop
    // relaunches a fresh MainForm with this config (a clean, startup-identical re-init for the new mode).
    public GameConfig? PendingGameChange { get; private set; }

    // 4-pane splitters (kept for pane maximize: double-click a viewport header to fill the window).
    private SplitContainer? _outerSplit, _leftSplit, _rightSplit;
    private ViewportPanel?  _maximizedPanel;      // non-null while one viewport is maximized

    public MainForm(GameConfig config, string? openProjectPath = null)
    {
        _pendingOpenPath = openProjectPath;
        // Detect a previous bad exit (crash/kill) BEFORE we claim the session lock, so we can
        // offer the recovered backup once the editor is up.
        _recoverPrompt = Editor.AutoSave.PreviousSessionEndedBadly();
        _recoveryFile  = _recoverPrompt ? Editor.AutoSave.RecoveryFile() : null;
        Editor.AutoSave.BeginSession();
        // Hammer routes its map accelerators to the main frame even while a modeless tool window (Face Edit
        // sheet / entity properties / texture browser) is focused. A WinForms message filter is the same
        // idea: it lets those owned windows forward editor shortcuts so the views stay keyboard-drivable.
        Application.AddMessageFilter(this);
        // Save a crash backup of the document if the app dies on an unhandled exception.
        Application.ThreadException += (_, ex) => Editor.AutoSave.WriteCrashBackup(_document);
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Editor.AutoSave.WriteCrashBackup(_document);

        _config      = config;
        // Record the target game and seed the initial blank scene with its defaults (OoT: Hyrule Field;
        // MM: Termina Field sky/lighting/music + the persistent flow-of-time default). Runs before any
        // project load, which replaces scenes with their own saved settings.
        _document.InitGameDefaults(_config.IsMMBased);

        // Restore persisted editor defaults before the menu/viewports read them.
        _gridSize = Editor.EditorSettings.GridSize;
        Editor.Solid.TextureLock = Editor.EditorSettings.TextureLock;
        Editor.ViewOptions.SnapToGrid     = Editor.EditorSettings.SnapToGrid;
        Editor.ViewOptions.ShowSky        = Editor.EditorSettings.ShowSky;
        Editor.ViewOptions.ShowGrid3D     = Editor.EditorSettings.ShowGrid3D;
        Editor.ViewOptions.ShowPrerenderedBackground = Editor.EditorSettings.ShowPrerenderedBackground;
        Editor.ViewOptions.ShowEntities3D = Editor.EditorSettings.ShowEntities3D;
        Editor.ViewOptions.ShowEntities2D = Editor.EditorSettings.ShowEntities2D;
        Editor.ViewOptions.TrilinearFilter = Editor.EditorSettings.TrilinearFilter;

        _actorDb     = ActorDatabase.Load(config.IsOoTBased);
        Editor.ViewOptions.IsOoT = config.IsOoTBased;   // selects the actor-flag schema for the logic graph
        _selectTool  = new SelectTool(_document);
        _brushTool   = new BrushTool(_document) { ActiveTextureProvider = () => _textureTool?.ActiveTexture };
        _clipTool    = new ClipTool(_document);
        _vertexTool  = new VertexTool(_document);
        _pathTool    = new PathTool(_document);
        _entityTool  = new EntityTool(_document);
        _textureTool = new TextureTool(_document);
        _shadeTool   = new ShadePaintTool(_document);
        _decalTool   = new DecalTool(_document);
        _magnifyTool = new MagnifyTool();
        // Lambdas defer to _vp3D / AllViewports(), which are constructed further down.
        _cameraTool  = new CameraTool(_document, () => _vp3D!.Viewport.ActiveCamera3D,
                                      () => { foreach (var vp in AllViewports()) vp.RequestRedraw(); });
        _activeTool  = _selectTool;

        // ── Form properties ───────────────────────────────────────────────
        Text            = $"Megaton Hammer — {config.DisplayName}";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        WindowState     = FormWindowState.Maximized;
        BackColor       = Color.FromArgb(20, 20, 20);
        ForeColor       = Color.FromArgb(220, 220, 220);
        MinimumSize     = new Size(800, 600);
        KeyPreview      = true;   // intercept keys before focused controls

        // ── Menu strip ────────────────────────────────────────────────────
        var menu = BuildMenuStrip();

        // ── Tool strip ────────────────────────────────────────────────────
        var tools = BuildToolStrip();

        // ── Viewport panels ───────────────────────────────────────────────
        _vp3D    = new ViewportPanel(ViewportType.Perspective3D);
        _vpTop   = new ViewportPanel(ViewportType.Top);
        _vpFront = new ViewportPanel(ViewportType.Front);
        _vpSide  = new ViewportPanel(ViewportType.Side);

        // ── Attach shared document + texture library ──────────────────────
        foreach (var vp in AllViewports())
        {
            vp.Document = _document;
            vp.GridSize = _gridSize;
            vp.Textures = _textureLib;
            vp.ActorDoubleClicked += OpenEntityConfig;
            vp.SolidDoubleClicked += OpenSolidProperties;   // double-click a brush → Brush Properties pop-out (full inspector)
            vp.RedrawAllRequested += RedrawAll;             // clip tool: a 2D drag refreshes the 3D cut preview
            // Double-click a path waypoint → OPEN ITS PROPERTIES (what it is + the MM path-header fields),
            // like double-clicking any other billboard entity — NOT auto-switch to the vertex tool (which was
            // clunky: you just want to inspect the node, not start editing points).
            vp.PathNodeDoubleClicked += (pi, pt) =>
            {
                if (pi < 0 || pi >= _document.Scene.Paths.Count) return;
                using var dlg = new Forms.PathPropertiesDialog(_document.Scene.Paths[pi]);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _propertiesPanel.ForceRefresh();
                    foreach (var v in AllViewports()) v.RequestRedraw();
                }
            };
            // Single-click a path waypoint → switch to the Path tool and select that node (highlighted +
            // editable), so the diamonds behave like clickable entities. Double-click still opens properties.
            vp.PathNodeClicked += (pi, pt) =>
            {
                if (pi < 0 || pi >= _document.Scene.Paths.Count) return;
                SetActiveTool(_pathTool);
                _pathTool.SelectNode(pi, pt);
                foreach (var v in AllViewports()) v.RequestRedraw();
            };
            vp.CursorMoved += (v, zoom) => SetCursorCoords(v.X, v.Y, v.Z, zoom);
            var vpRef = vp;
            vp.ContextMenuRequested2D += pt => ShowEditContextMenu(vpRef, pt);
            // Remember the last-focused 2D view so arrow-nudge keeps working after the toolbar or a
            // panel takes focus — Hammer nudges in the active 2D view regardless of the current tool.
            vp.Enter += (_, _) => { if (vpRef.ViewportType != ViewportType.Perspective3D) _lastFocused2D = vpRef; };
        }
        _document.Changed += OnDocumentChanged;
        SetActiveTool(_selectTool);

        // ── Hierarchy panel (right dock stack) ────────────────────────────
        _hierarchyPanel = new HierarchyPanel(_document, _entityTool, _actorDb, showHeader: false, mm: !config.IsOoTBased);
        _hierarchyPanel.Dock = DockStyle.Fill;   // fills its collapsible section (see right-side dock stack)
        _hierarchyPanel.ActiveRoomChanged      += () => { foreach (var vp in AllViewports()) vp.RequestRedraw(); _propertiesPanel?.ForceRefresh(); };
        _hierarchyPanel.DocumentStructureChanged += () => { foreach (var vp in AllViewports()) vp.RequestRedraw(); _propertiesPanel?.ForceRefresh(); };
        _hierarchyPanel.RoomVisibilityChanged  += () => { foreach (var vp in AllViewports()) vp.RequestRedraw(); };
        _hierarchyPanel.ActiveSceneChanged     += () =>
        {
            // Switching scenes swaps the whole workspace (geometry, actors, imported mesh, settings).
            _roomsForm?.Close(); _roomsForm = null;
            if (_document.Imported != null) FrameImported(_document.Imported);
            foreach (var vp in AllViewports()) vp.RequestRedraw();
            _propertiesPanel?.ForceRefresh();
            UpdateStatus();
        };

        // ── Properties panel (right dock stack, below hierarchy) ──────────
        _propertiesPanel = new PropertiesPanel(_document, _actorDb, config.IsOoTBased, showHeader: false);
        _propertiesPanel.Changed += () => { foreach (var vp in AllViewports()) vp.RequestRedraw(); UpdateStatus(); };

        // ── Texture panel (right dock stack, top section) ─────────────────
        _texturePanel = new TexturePanel(_textureLib, _document, showHeader: false);
        // Hammer: selecting a texture (single OR double click, dock or popout browser) ONLY sets the
        // current/active texture. It never paints faces, never switches tools, and never opens/
        // re-centres the Face Edit sheet — applying is explicit (Face Edit ▸ Apply, or Shift-click a
        // face with the texture tool). The sheet's preview just refreshes to the new current texture.
        void SelectTexture(string name)
        {
            _textureTool.ActiveTexture = name;
            _decalTool.ActiveTexture   = name;   // decal stamps the same active texture
            // Make the picked swatch the Face Edit sheet's shown+applied texture too, so its preview and
            // Apply/Align follow the last-clicked texture instead of sticking on a stale one.
            if (IsFaceEditOpen()) _faceEditDialog!.SetCurrentTexture(name); else _faceEditDialog?.Refresh2();
        }
        _texturePanel.TextureSelected  += SelectTexture;
        _texturePanel.TextureCommitted += SelectTexture;
        _texturePanel.AnimateRequested += AuthorTextureScroll;
        _decalTool.Applied += () => { foreach (var vp in AllViewports()) vp.RequestRedraw(); };
        // Painting a face must NOT rebuild the texture grid (that re-decodes hundreds of
        // thumbnails and causes severe lag). Just redraw the viewports; usage counts
        // refresh next time the browser is opened/sorted.
        // Right-click apply paints the SELECTED texture (the one you picked in the browser); if nothing's
        // selected it falls back to whatever the Face Edit sheet is previewing — so applying works while
        // the sheet is up even before you've explicitly picked a texture.
        _textureTool.CurrentMaterial = () =>
            !string.IsNullOrEmpty(_textureTool.ActiveTexture) ? _textureTool.ActiveTexture
            : (IsFaceEditOpen() ? _faceEditDialog!.CurrentMaterial : null);
        _textureTool.FacePainted += () =>
        {
            _faceEditDialog?.Refresh2();
            foreach (var vp in AllViewports()) vp.RequestRedraw();
        };
        _textureTool.FaceSelectionChanged += () =>
        {
            _faceEditDialog?.Refresh2();
            foreach (var vp in AllViewports()) vp.RequestRedraw();
        };
        // Alt-click eyedropper: a lifted texture becomes the global active texture.
        _textureTool.TextureLifted += name => { if (!string.IsNullOrEmpty(name)) SelectTexture(name); };
        // #1: clicking a face while the Replace Textures dialog is open fills its "Find texture" field —
        // from EITHER the material (Texture) tool or the default Select tool, so it works whatever's active.
        void FeedReplaceFind(string? name) { if (_replaceDlg is { IsDisposed: false } dlg) dlg.SetFind(name); }
        // Hammer parity: LEFT-clicking a face with the Texture tool lifts THAT face's texture as the current
        // material — one source of truth. Before, the Face Edit sheet followed the clicked face but the tool's
        // ActiveTexture (what right-click "apply current" paints) stayed on the last browser pick, so the two
        // diverged: the popout showed the clicked face's texture yet the next right-click painted a stale one.
        // Now selecting a face updates both, so what you clicked is what the sheet shows and what apply uses.
        _textureTool.FaceClickedTexture += name =>
        {
            if (!string.IsNullOrEmpty(name)) SelectTexture(name);   // becomes the current/active material
            FeedReplaceFind(name);
        };
        _selectTool.FaceClickedTexture += FeedReplaceFind;

        // ── 4-pane layout ─────────────────────────────────────────────────
        var outerSplit = new SplitContainer
        {
            Dock          = DockStyle.Fill,
            Orientation   = Orientation.Vertical,
            SplitterWidth = 4,
            BackColor     = Color.FromArgb(10, 10, 10)
        };

        var leftSplit = new SplitContainer
        {
            Dock          = DockStyle.Fill,
            Orientation   = Orientation.Horizontal,
            SplitterWidth = 4,
            BackColor     = Color.FromArgb(10, 10, 10)
        };
        leftSplit.Panel1.Controls.Add(_vp3D);
        leftSplit.Panel2.Controls.Add(_vpFront);

        var rightSplit = new SplitContainer
        {
            Dock          = DockStyle.Fill,
            Orientation   = Orientation.Horizontal,
            SplitterWidth = 4,
            BackColor     = Color.FromArgb(10, 10, 10)
        };
        rightSplit.Panel1.Controls.Add(_vpTop);
        rightSplit.Panel2.Controls.Add(_vpSide);

        outerSplit.Panel1.Controls.Add(leftSplit);
        outerSplit.Panel2.Controls.Add(rightSplit);

        _outerSplit = outerSplit; _leftSplit = leftSplit; _rightSplit = rightSplit;
        // Hammer: double-clicking a viewport's title bar maximizes it (double-click again to restore).
        foreach (var p in new[] { _vp3D, _vpTop, _vpFront, _vpSide })
            p.HeaderDoubleClicked += ToggleMaximizeViewport;

        // ── Right-side dock stack: Texture / Objects / Properties ─────────
        // Hammer-style stacked panels: each section collapses to its header bar, and the gaps
        // between expanded sections drag to re-balance them.
        _texturePanel.Dock = DockStyle.Fill;
        var rightStack = new DockStack { Dock = DockStyle.Right, Width = 264 };
        rightStack.AddSection(new CollapsibleSection("TEXTURES",   _texturePanel),    1.4f);
        rightStack.AddSection(new CollapsibleSection("OBJECTS",    _hierarchyPanel),  1.1f);
        rightStack.AddSection(new CollapsibleSection("PROPERTIES", _propertiesPanel), 1.0f);

        // ── Status bar (Hammer-style panes: tool/hint · coords · selection size · grid/zoom) ──
        var status = new StatusStrip { BackColor = Color.FromArgb(0, 122, 204) };
        ToolStripStatusLabel Pane(int w, bool spring = false) => new()
        {
            ForeColor = Color.White, Font = new Font("Segoe UI", 8.5f),
            AutoSize = false, Width = w, Spring = spring,
            BorderSides = ToolStripStatusLabelBorderSides.Left, BorderStyle = Border3DStyle.Etched,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _statusLabel = Pane(0, spring: true);     // tool + hint (stretches)
        _statusCoords = Pane(160);                // cursor world coords
        _statusSize   = Pane(170);                // selection bounding-box size
        _statusGrid   = Pane(170);                // grid + zoom
        status.Items.AddRange([_statusLabel, _statusCoords, _statusSize, _statusGrid]);
        UpdateStatus();

        // ── Assemble (WinForms dock is processed in reverse Controls order) ──
        // Fill must be at index 0 so it fills last (all other docks claim their
        // edges first before Fill expands into the remaining rectangle).
        Controls.Add(outerSplit);       // index 0 — Fill
        Controls.Add(rightStack);       // index 1 — Right (Texture / Objects / Properties stack)
        Controls.Add(tools);            // index 2 — Left (Hammer-style vertical tool column)
        Controls.Add(menu);             // index 3 — Top
        Controls.Add(status);           // index 4 — Bottom
        MainMenuStrip = menu;

        // ── Render timer ──────────────────────────────────────────────────
        _renderTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();

        // ── Auto-save timer ───────────────────────────────────────────────
        _autoSaveTimer = new System.Windows.Forms.Timer();
        _autoSaveTimer.Tick += (_, _) => DoAutoSave();
        RestartAutoSaveTimer();

        Shown += (_, _) =>
        {
            SplitterFix(outerSplit, leftSplit, rightSplit);
            StartTextureAutoLoad();
            if (Editor.EditorSettings.EnableCrossGameTextures && Editor.EditorSettings.HasOppositeSource(_config.IsOoTBased))
                StartCrossGameTextureLoad();
            _dirty = false;   // ignore any change events fired during initialization
            EnsureModelSource();
            // Open a project handed in on the command line (taskbar jump-list launch) before the
            // recovery prompt, so a deliberate "open recent" wins over a stale auto-recovery.
            if (_pendingOpenPath != null && File.Exists(_pendingOpenPath)) OpenRecentProject(_pendingOpenPath);
            else MaybeOfferRecovery();
            MaybeOfferEngineSetup();
            WindowsJumpList.Update(Editor.EditorSettings.RecentFiles);   // reflect persisted recents
        };
    }

    // ── Auto-save + crash recovery ────────────────────────────────────────

    private void RestartAutoSaveTimer()
    {
        if (_autoSaveTimer == null) return;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Interval = Math.Max(1, Editor.EditorSettings.AutoSaveIntervalMinutes) * 60_000;
        if (Editor.EditorSettings.AutoSaveEnabled) _autoSaveTimer.Start();
    }

    private void DoAutoSave()
    {
        if (!Editor.EditorSettings.AutoSaveEnabled) return;
        // Nothing worth backing up if the project is empty and untouched.
        if (!_dirty && _document.Count == 0 && _document.ActorCount == 0 && _document.Imported == null) return;
        try
        {
            Editor.AutoSave.WriteAutoSave(_document, Editor.EditorSettings.AutoSaveBackupCount);
            if (_statusLabel != null) _statusLabel.Text = $"Auto-saved backup  ·  {DateTime.Now:HH:mm}";
        }
        catch { /* backup is best-effort; never interrupt editing */ }
    }

    // After a crash/kill, offer to open the recovered backup on the next launch.
    private void MaybeOfferRecovery()
    {
        if (!_recoverPrompt || _recoveryFile == null || !File.Exists(_recoveryFile)) return;
        var when = File.GetLastWriteTime(_recoveryFile);
        if (MessageBox.Show(
                $"Megaton Hammer didn't close cleanly last time.\n\nA recovered backup from {when:g} is available:\n" +
                $"{Path.GetFileName(_recoveryFile)}\n\nOpen it?",
                "Recover Project", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        try
        {
            ProjectSerializer.Load(_document, _recoveryFile);
            _currentPath = null;        // it's a recovered backup — Save will prompt for a real location
            _dirty = true;
            AfterDocumentLoad();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open the recovered backup:\n{ex.Message}", "Recover Project",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // On first launch with an unconfigured playtest target, offer to set it up. Vanilla ROM
    // projects play-test through Project64; SoH/2Ship builds through their own executable.
    // We only ask once per target (a "prompted" flag), but the user can always reconfigure
    // later via Options ▸ Playtest engines.
    private void MaybeOfferEngineSetup()
    {
        bool fork = _config.Mode is Editor.GameMode.ShipOfHarkinian or Editor.GameMode.TwoShip2Harkinian;
        if (fork)
        {
            if (Editor.EditorSettings.EngineSetupPrompted || Editor.EditorSettings.IsEngineConfigured(_config.IsMMBased))
                return;
            Editor.EditorSettings.EngineSetupPrompted = true;
            string name = _config.IsMMBased ? "2Ship (Majora's Mask)" : "Ship of Harkinian (Ocarina of Time)";
            if (MessageBox.Show(this,
                    $"The {name} engine build used for play-testing isn't configured yet.\n\nSet it up now?",
                    "Configure Playtest", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                OpenOptionsDialog(OptionsTab.Playtest);
        }
        else
        {
            // Vanilla / custom ROM → Project64.
            if (Editor.EditorSettings.Project64SetupPrompted) return;
            var pj = Editor.EditorSettings.Project64Path;
            if (!string.IsNullOrWhiteSpace(pj) && File.Exists(pj)) return;
            Editor.EditorSettings.Project64SetupPrompted = true;
            if (MessageBox.Show(this,
                    "Vanilla ROM projects play-test through the Project64 (N64) emulator, which isn't configured yet.\n\n" +
                    "Set it up now?",
                    "Configure Playtest", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                OpenOptionsDialog(OptionsTab.Playtest);
        }
    }

    // Reference model source for actor models when no ROM level is imported. Uses the project's
    // own ROM if it's a .z64, else the configured same-game vanilla ROM (cross-game OoT/MM path),
    // so Link's spawn and other actors show models in a fresh scene.
    private Rom.RomImage? _modelRom;
    private Editor.ActorModelResolver? _modelResolver;

    // A usable raw ROM for reading game data (scenes, models, textures): the configured ROM if the
    // game mode is a vanilla/custom ROM, otherwise the matching vanilla reference ROM (SoH/2Ship
    // builds don't carry raw scene data — they use a derived O2R/OTR, so importing needs the ROM the
    // archive came from). Null if none is configured.
    private string? ResolveRomPath() => IsRomPath(_config.RomPath) ? _config.RomPath
        : (_config.IsOoTBased ? Editor.EditorSettings.OotRomPath : Editor.EditorSettings.MmRomPath);

    private void EnsureModelSource()
    {
        if (_modelResolver != null) return;
        string? romPath = ResolveRomPath();
        if (!IsRomPath(romPath)) return;
        try
        {
            _modelRom = new Rom.RomImage(romPath!);
            _modelResolver = new Editor.ActorModelResolver(_modelRom);
            // All viewports (not just 3D) need the resolver so the 2D views can pick actors by their
            // model footprint and draw their model-box, matching the 3D view.
            foreach (var vp in AllViewports())
            {
                vp.FallbackResolver = _modelResolver;
                vp.FallbackRom = _modelRom;
                vp.RequestRedraw();
            }
        }
        catch { _modelRom = null; _modelResolver = null; }
    }

    // ── Auto-load game textures on boot (background) ──────────────────────
    // Prefer extracting directly from a loaded .z64/.n64 ROM; otherwise fall back
    // to a matching O2R archive; otherwise keep the built-in procedural samples.

    private void StartTextureAutoLoad()
    {
        Editor.RomSafety.Protect(_config.RomPath);   // the loaded ROM is read-only by default
        if (IsRomPath(_config.RomPath)) { StartRomTextureLoad(_config.RomPath!); return; }

        // O2R-based builds (SoH / 2Ship) ship only their own custom assets (UI, tracker icons) in the
        // archive — a couple hundred textures — not the game's scenes/objects. Load those, but also
        // scan the vanilla ROM (the settings path, same one the model viewer uses) so the library gets
        // the full set grouped into friendly scene categories (Clock Town, Stone Tower, …).
        StartO2RTextureLoad();
        string? vanilla = ResolveRomPath();
        if (IsRomPath(vanilla))
        {
            Editor.RomSafety.Protect(vanilla);
            StartRomTextureLoad(vanilla!);
        }
    }

    private static bool IsRomPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".z64" or ".n64" or ".v64";
    }

    private void StartRomTextureLoad(string romPath)
    {
        if (_statusLabel != null) _statusLabel.Text = $"Extracting textures from {Path.GetFileName(romPath)}…";

        System.Threading.Tasks.Task.Run(() =>
        {
            List<Rom.RomTexInfo> combined;
            Rom.RomTextureSource src;
            Dictionary<int, string> cats;
            List<HashSet<string>> folders;
            try
            {
                var rom = new Rom.RomImage(romPath);
                src     = new Rom.RomTextureSource(rom);
                var map = Rom.RomAssetIndex.BuildMap(rom);       // scene/room categories + room→scene-file links
                cats    = map.FileScene;
                var infos = src.Scan();
                // Resolve cross-segment refs (room → scene/keep files): completes scene folders
                // AND patches CI palettes that live in another file. Returns the full texture list.
                var (allTex, allFolders) = Rom.SceneTextureMapper.Build(rom, infos, map);
                combined = allTex;
                folders  = allFolders;
            }
            catch { return; }   // not a usable ROM; leave samples in place

            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _romSource = src;
                    Editor.LevelTints.SetRom(src.Rom);   // per-level texture tint provider (browser previews each level's hue)
                    Textures.WaterTexture.SetRom(src.Rom);   // real OoT water texture for WATERBOX surfaces
                    _textureLib.AddRomTextures(combined, src, cats, folders);
                    _texturePanel.Refresh(recount: false);
                    UpdateStatus();
                    RunTextureDedupe(announce: false);   // collapse byte-identical extracted textures
                }));
            }
            catch { /* form closing */ }
        });
    }

    // Loads (or reloads / clears) the OTHER game's textures as area categories tagged "⟨MM⟩"/"⟨OoT⟩",
    // so an OoT project can browse MM's area textures and vice-versa. Driven by Options + EditorSettings.
    private void StartCrossGameTextureLoad()
    {
        // Always clear the previous cross-game set first (source may have changed or been disabled).
        _textureLib.RemoveCrossGameTextures();
        _crossRomSource = null;

        if (!Editor.EditorSettings.EnableCrossGameTextures || !Editor.EditorSettings.HasOppositeSource(_config.IsOoTBased))
        {
            _texturePanel.Refresh(recount: false);
            return;
        }

        string? romPath = Editor.EditorSettings.OppositeRomPath(_config.IsOoTBased);
        if (!IsRomPath(romPath))   // only the ROM path yields area-mapped categories
        {
            _texturePanel.Refresh(recount: false);
            return;
        }

        string prefix = $"{Textures.TextureLibrary.CrossGameMarker}{(_config.IsOoTBased ? "MM" : "OoT")}⟩ ";
        if (_statusLabel != null) _statusLabel.Text = $"Loading cross-game textures from {Path.GetFileName(romPath)}…";

        System.Threading.Tasks.Task.Run(() =>
        {
            List<Rom.RomTexInfo> combined; Rom.RomTextureSource src;
            List<HashSet<string>> folders; Dictionary<int, string> cats;
            try
            {
                var rom = new Rom.RomImage(romPath!);
                Editor.RomSafety.Protect(romPath);
                src = new Rom.RomTextureSource(rom);
                var map = Rom.RomAssetIndex.BuildMap(rom);
                cats = map.FileScene;
                var (allTex, allFolders) = Rom.SceneTextureMapper.Build(rom, src.Scan(), map);
                combined = allTex; folders = allFolders;
            }
            catch { return; }

            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _crossRomSource = src;
                    _textureLib.AddRomTextures(combined, src, cats, folders, categoryPrefix: prefix);
                    _texturePanel.Refresh(recount: false);
                    UpdateStatus();
                }));
            }
            catch { /* form closing */ }
        });
    }

    private void StartO2RTextureLoad()
    {
        var extraDirs = new List<string?>
        {
            _config.GameDirectory,
            _config.RomPath != null ? Path.GetDirectoryName(_config.RomPath) : null,
        };

        string? path = O2RLocator.Find(_config.IsOoTBased, extraDirs);
        if (path == null) return;   // keep built-in procedural samples only

        _o2rSource = new O2RTextureSource(path);
        var src = _o2rSource;

        if (_statusLabel != null) _statusLabel.Text = $"Loading textures from {Path.GetFileName(path)}…";

        System.Threading.Tasks.Task.Run(() =>
        {
            var infos = src.Scan();
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                Func<string, string?> friendly = _config.IsOoTBased
                    ? Rom.OotSceneFiles.FriendlyName : Rom.MmSceneFiles.FriendlyName;
                BeginInvoke(new Action(() =>
                {
                    _textureLib.AddO2RTextures(infos, src, friendly);
                    _texturePanel.Refresh(recount: false);
                    UpdateStatus();
                    RunTextureDedupe(announce: false);   // collapse byte-identical extracted textures
                }));
            }
            catch { /* form closing */ }
        });
    }

    // ── Tool management ───────────────────────────────────────────────────

    private bool _openingFaceEdit;

    private void SetActiveTool(ITool tool)
    {
        _activeTool = tool;

        foreach (var vp in AllViewports())
            vp.ActiveTool = tool;

        if (_btnSelect  != null) _btnSelect.Checked  = tool == _selectTool;
        if (_btnBrush   != null) _btnBrush.Checked   = tool == _brushTool;
        if (_btnClip    != null) _btnClip.Checked    = tool == _clipTool;
        if (_btnVertex  != null) _btnVertex.Checked  = tool == _vertexTool;
        if (_btnPath    != null) _btnPath.Checked    = tool == _pathTool;
        if (_btnEntity  != null) _btnEntity.Checked  = tool == _entityTool;
        if (_btnTexture != null) _btnTexture.Checked = tool == _textureTool;
        if (_btnShade   != null) _btnShade.Checked   = tool == _shadeTool;
        if (_btnDecal   != null) _btnDecal.Checked   = tool == _decalTool;
        if (_btnMagnify != null) _btnMagnify.Checked = tool == _magnifyTool;
        if (_btnCamera  != null) _btnCamera.Checked  = tool == _cameraTool;

        // #27: switching to any other tool exits face-editing — close the Face Edit sheet so its
        // highlighted (yellow) faces don't linger under Select/etc. Its FormClosed clears the face
        // selection and redraws. ALSO clear directly: if the sheet was already closed while the Texture
        // tool stayed active and more faces got selected, the dialog-open check alone missed them, so the
        // yellow face highlight lingered into Select. Clearing unconditionally on leaving Texture fixes that.
        if (tool != _textureTool)
        {
            if (IsFaceEditOpen()) _faceEditDialog!.Close();
            if (_document.SelectedFaces.Any()) { _document.ClearFaceSelection(); RedrawAll(); }
        }

        // The Texture tool opens the Hammer-style Face Edit dialog (modeless). Guard against
        // re-entry: ShowFaceEditDialog must never loop back into SetActiveTool(_textureTool) here
        // (that recursion stack-overflows the process), so the link only fires one level deep.
        if (tool == _textureTool && !_openingFaceEdit)
        {
            _openingFaceEdit = true;
            try { ShowFaceEditDialog(); } finally { _openingFaceEdit = false; }
        }

        // The Shade tool opens its own modeless spray-properties sheet (colour + size + opacity), and
        // closes it when you switch away — same pattern as Face Edit.
        if (tool == _shadeTool) ShowShadePaintDialog();
        else if (_shadePaintDialog is { IsDisposed: false, Visible: true }) _shadePaintDialog.Close();

        UpdateStatus();
    }

    private void OnDocumentChanged()
    {
        _dirty = true;
        UpdateStatus();
        SyncOpenEntityConfig();   // keep an open actor-config pop-out on the currently-selected actor
        // viewports redraw on next timer tick
    }

    // If the actor-config pop-out is open and the selection moves to a DIFFERENT actor (single-click, not just
    // double-click), reopen it on that actor — matching Hammer, where the properties window follows selection.
    // Deferred via BeginInvoke so we never rebuild the dialog from inside a selection-change event, and guarded
    // by the tracked actor so OpenEntityConfig's own re-selection doesn't loop. (The Brush Properties pop-out
    // already follows selection through its embedded PropertiesPanel.)
    private void SyncOpenEntityConfig()
    {
        if (_entityDlg is not { IsDisposed: false }) return;
        var a = _document.SelectedActor;
        if (a == null || ReferenceEquals(a, _entityDlgActor) || !IsHandleCreated) return;
        BeginInvoke((Action)(() =>
        {
            if (_entityDlg is { IsDisposed: false } && _document.SelectedActor is { } cur && !ReferenceEquals(cur, _entityDlgActor))
                OpenEntityConfig(cur);
        }));
    }

    // Right-clicking a texture opens this little dialog to make it scroll (animate) in the scene. Faces
    // painted with it scroll in the 3D view; on MM export an AnimatedMaterial makes them scroll in-game.
    private void AuthorTextureScroll(string name)
    {
        var st = _document.Scene.Settings;
        var cur = st.TextureScrolls.FirstOrDefault(t => t.Name == name);
        using var dlg = new Form
        {
            Text = $"Scroll animation — {name}", FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(300, 138),
            MaximizeBox = false, MinimizeBox = false,
        };
        NumericUpDown Spin(int top, float val) => new()
        { Left = 160, Top = top, Width = 120, DecimalPlaces = 2, Minimum = -20, Maximum = 20, Increment = 0.1m, Value = (decimal)val };
        var numU = Spin(14, cur?.U ?? 0f);
        var numV = Spin(46, cur?.V ?? 0f);
        var ok = new Button { Text = "OK", Left = 124, Top = 96, Width = 75, DialogResult = DialogResult.OK };
        var rm = new Button { Text = "Remove", Left = 205, Top = 96, Width = 75 };
        rm.Click += (_, __) => { st.TextureScrolls.RemoveAll(t => t.Name == name); _dirty = true; dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
        dlg.Controls.AddRange(new Control[]
        {
            new Label { Text = "Scroll U (tiles/sec):", Left = 14, Top = 16, Width = 140 }, numU,
            new Label { Text = "Scroll V (tiles/sec):", Left = 14, Top = 48, Width = 140 }, numV,
            new Label { Text = "(0,0 = static. Faces using this texture scroll.)", Left = 14, Top = 78, Width = 280, ForeColor = SystemColors.GrayText }, ok, rm,
        });
        dlg.AcceptButton = ok;
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            st.TextureScrolls.RemoveAll(t => t.Name == name);
            if (numU.Value != 0 || numV.Value != 0)
                st.TextureScrolls.Add(new Editor.TextureScroll(name, (float)numU.Value, (float)numV.Value));
            _dirty = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // A pending game change already prompted-to-save in CloseProject; don't double-prompt here.
        if (PendingGameChange == null && !e.Cancel && !PromptSaveIfDirty("closing")) e.Cancel = true;
        if (!e.Cancel) Editor.DiscordRpc.Stop();   // clear the Discord presence on exit
        base.OnFormClosing(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateDiscordPresence();   // start Rich Presence for this session (even before a project is opened)
    }

    // #8a: while the window is being border-dragged/resized, suspend the four viewports' live GL renders
    // (they otherwise each re-render the whole scene on every resize tick — the severe stutter). One real
    // redraw is issued when the resize gesture ends.
    protected override void OnResizeBegin(EventArgs e)
    {
        GLViewport.SuspendRender = true;
        base.OnResizeBegin(e);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        GLViewport.SuspendRender = false;
        base.OnResizeEnd(e);
        RedrawAll();
    }

    /// <summary>If there are unsaved changes, asks Yes/No/Cancel to save first. Returns false only
    /// when the user cancels (or a requested save fails), meaning the caller should abort.</summary>
    private bool PromptSaveIfDirty(string action)
    {
        if (!_dirty) return true;
        var r = MessageBox.Show(
            $"Save changes to the current project before {action}?",
            "Megaton Hammer", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) { SaveProject(); return !_dirty; }  // save cancelled/failed → abort
        return true;   // No → discard
    }

    // Audits the texture catalogue and collapses byte-identical duplicates (frees memory + de-clutters the
    // browser). Decoding must happen on the UI thread — the ROM/O2R texture sources are not thread-safe — so
    // this is synchronous behind a wait cursor (one-time cost; the browser would decode these lazily anyway).
    // `announce` = report the count in the status bar (explicit Tools-menu run); silent for the post-load pass.
    private void RunTextureDedupe(bool announce)
    {
        int n;
        var prev = Cursor;
        try { Cursor = Cursors.WaitCursor; n = _textureLib.DedupeIdentical(); }
        finally { Cursor = prev; }

        if (n > 0) _texturePanel.Refresh(recount: false);
        if (announce && _statusLabel != null)
            _statusLabel.Text = n > 0
                ? $"Removed {n} duplicate texture(s) — identical copies merged."
                : "No duplicate textures found.";
    }

    private void UpdateStatus()
    {
        if (_statusLabel == null) return;
        string toolName = _activeTool switch
        {
            SelectTool st => $"Select [Q] — {st.Mode}",
            BrushTool   => "Brush [B]",
            ClipTool c  => $"Clip [X] — keep {c.KeepMode}",
            VertexTool  => "Vertex [V]",
            PathTool    => "Path [P]",
            EntityTool  => "Entity [E]",
            TextureTool => "Texture [T]",
            _           => _activeTool.Name
        };
        string hint = _activeTool switch
        {
            SelectTool st => st.Mode switch
            {
                SelectTool.SelectMode.Rotate => "drag a handle to rotate (15° steps, Ctrl = free) · click selection to cycle to Skew",
                SelectTool.SelectMode.Skew   => "drag an edge handle to skew · click selection to cycle back to Scale",
                _                            => "drag handles to resize · Alt-drag to move · click selection to cycle to Rotate · drag brushes/actors in 3D to move",
            },
            BrushTool   => "drag a box in a 2D view · Enter creates the brush · Esc cancels · drag again to redraw",
            ClipTool    => "drag a line in 2D to set the cut · X cycles the kept side · Enter applies · Esc cancels",
            VertexTool  => "drag vertex handles in the 2D views",
            PathTool    => "click to drop a waypoint · drag to move · Delete removes · Enter new path · L toggles loop · double-click a waypoint for properties",
            EntityTool  => "click in 2D, or a surface in 3D, to place an actor",
            TextureTool => "click a face to select · Ctrl-click multi-select · Shift-click selects the whole coplanar surface · right-click paints the current texture (aligns to an adjacent same-texture face)",
            ShadePaintTool => "drag over faces in 3D to spray vertex shade",
            _           => "RMB+WASD to fly · Z mouselook · [ ] grid",
        };
        _statusLabel.Text =
            $"  {toolName}: {hint}    ·    Brushes {_document.Count} · Actors {_document.ActorCount} · " +
            $"Textures {_textureLib.Entries.Count} · {_config.DisplayName}";
        if (_statusGrid != null) _statusGrid.Text = $"Grid: {_gridSize}u";
        UpdateSelectionSize();
    }

    /// <summary>Updates the cursor-coordinate + zoom panes (called by viewports on mouse move).</summary>
    public void SetCursorCoords(float x, float y, float z, float zoomPct)
    {
        if (_statusCoords != null) _statusCoords.Text = $"  X {x:0}  Y {y:0}  Z {z:0}";
        if (_statusGrid != null)
        {
            string snap = Editor.ViewOptions.SnapToGrid ? $"Snap {_gridSize}u" : "Snap off";
            string zoom = zoomPct > 0 ? $" · {zoomPct:0}%" : "";
            _statusGrid.Text = $"Grid {_gridSize}u · {snap}{zoom}";
        }
    }

    // Shows the combined bounding-box dimensions of the current brush selection.
    private void UpdateSelectionSize()
    {
        if (_statusSize == null) return;
        bool any = false;
        OpenTK.Mathematics.Vector3 mn = default, mx = default;
        foreach (var s in _document.Solids)
        {
            if (!s.IsSelected) continue;
            var (smn, smx) = s.GetAABB();
            if (!any) { mn = smn; mx = smx; any = true; }
            else { mn = OpenTK.Mathematics.Vector3.ComponentMin(mn, smn); mx = OpenTK.Mathematics.Vector3.ComponentMax(mx, smx); }
        }
        _statusSize.Text = any ? $"  Size {mx.X - mn.X:0} × {mx.Y - mn.Y:0} × {mx.Z - mn.Z:0}" : "";
    }

    private IEnumerable<GLViewport> AllViewports()
    {
        yield return _vp3D.Viewport;
        yield return _vpTop.Viewport;
        yield return _vpFront.Viewport;
        yield return _vpSide.Viewport;
    }

    // True when keyboard focus is in a text-entry control, so global tool shortcuts
    // should be ignored to allow normal typing.
    private bool IsTextInputFocused()
    {
        Control? c = this;
        while (c is ContainerControl cc && cc.ActiveControl != null)
            c = cc.ActiveControl;
        return c is TextBoxBase;
    }

    // True when focus is in a control that itself uses arrow keys (text fields, spinners, combos,
    // lists, trees) — arrow-nudge must not hijack those, or panel navigation / typing would break.
    private bool IsArrowConsumingFocused()
    {
        Control? c = this;
        while (c is ContainerControl cc && cc.ActiveControl != null)
            c = cc.ActiveControl;
        return c is TextBoxBase or UpDownBase or ListControl or ListView or TreeView or DateTimePicker;
    }

    // True while the Face Edit popout (our texture/face tool) is open. Matches Hammer's Face Edit
    // Sheet mode, where the active tool is the Material tool and arrow keys don't nudge objects.
    private bool IsFaceEditOpen() => _faceEditDialog is { IsDisposed: false, Visible: true };

    // The 2D view arrow-nudge acts in: the focused 2D view if any, else the last 2D view that had
    // focus, else any 2D view. Decouples nudging from which control currently holds focus, so it
    // keeps working after switching tools (toolbar steals focus) — matching Valve Hammer.
    private GLViewport? NudgeView()
    {
        var focused = AllViewports().FirstOrDefault(p => p.Focused && p.ActiveCamera2D != null);
        if (focused != null) return focused;
        if (_lastFocused2D is { IsDisposed: false } last && last.ActiveCamera2D != null) return last;
        return AllViewports().FirstOrDefault(p => p.ActiveCamera2D != null);
    }

    // ── Route editor shortcuts from focused modeless tool windows (Hammer parity) ──

    // The owned, modeless tool windows whose keystrokes we forward to the map shortcut handlers, so the
    // editor stays keyboard-drivable while one of them is focused (Hammer's TranslateAccelerator-to-frame).
    private bool IsOwnedToolWindow(Form f) =>
        f.Owner == this && f is FaceEditDialog or EntityConfigDialog or TextureBrowserForm or ReplaceTexturesDialog or ShadePaintDialog;

    /// <summary>App-wide message filter: when a key is pressed while one of our modeless tool windows is
    /// focused, offer it to the editor's shortcut handlers (unless a text/number field in that window wants
    /// it). Returns true to consume. Mirrors Hammer routing its map accelerators to the main frame.</summary>
    public bool PreFilterMessage(ref Message m)
    {
        const int WM_KEYDOWN = 0x0100;
        if (m.Msg != WM_KEYDOWN) return false;
        var focused = Control.FromHandle(m.HWnd);

        // A focused text box must keep the standard editing chords in EVERY window (docked panels and popout
        // dialogs alike). Otherwise the editor's menu accelerators hijack them — Ctrl+A "Select All" was
        // selecting every brush in the world instead of the field's text, and Ctrl+Z would fire a global
        // undo mid-type. WinForms TextBox has no built-in Ctrl+A, so we run select-all ourselves. Handled
        // here (an app-wide message filter) so it wins before ProcessCmdKey / the menu ever see the key.
        if (focused is TextBoxBase tb
            && (Control.ModifierKeys & Keys.Control) != 0 && (Control.ModifierKeys & Keys.Alt) == 0)
        {
            switch ((Keys)(int)m.WParam & Keys.KeyCode)
            {
                case Keys.A: tb.SelectAll();                          return true;
                case Keys.C: tb.Copy();                               return true;
                case Keys.X: if (!tb.ReadOnly) tb.Cut(); else tb.Copy(); return true;
                case Keys.V: if (!tb.ReadOnly) tb.Paste();            return true;
                case Keys.Z: if (tb.CanUndo) tb.Undo();               return true;
            }
        }

        if (focused?.FindForm() is not { } top || !IsOwnedToolWindow(top)) return false;

        Keys keyData = (Keys)(int)m.WParam | Control.ModifierKeys;
        Keys key = keyData & Keys.KeyCode;
        // A focused text / number / combo / list field keeps every key it needs (typing, caret nav, its own
        // edit shortcuts) — don't hijack those.
        if (focused is TextBoxBase or UpDownBase or ComboBox or ListControl) return false;
        // Let the window handle its own navigation / commit / close keys (Esc hides it, like Hammer's props).
        if (key is Keys.Tab or Keys.Enter or Keys.Escape) return false;

        return ForwardEditorShortcut(keyData);
    }

    // Re-run the editor's command-key + tool-hotkey handlers for a key that arrived while a tool window
    // was focused. Reuses the exact same logic the main window uses (no duplicated shortcut table).
    private bool ForwardEditorShortcut(Keys keyData)
    {
        var msg = new Message();
        if (ProcessCmdKey(ref msg, keyData)) return true;   // arrows / clipboard / group / delete / esc
        var ke = new KeyEventArgs(keyData);
        OnKeyDown(ke);                                       // tool hotkeys (B/T/V/A/…), etc.
        return ke.Handled;
    }

    // ── Keyboard shortcuts (KeyPreview = true) ────────────────────────────

    // Arrow keys nudge the selection in the active 2D view (Hammer's Selection3D::OnKeyDown2D).
    // They're handled here because WinForms otherwise consumes arrows as control navigation before
    // OnKeyDown runs — ProcessCmdKey sees them first. We gate on "something is selected" and "focus
    // isn't in a control that needs arrows" (text fields, spinners, lists, trees) — NOT on a specific
    // viewport holding focus, which would otherwise stop nudging the moment the toolbar or a panel
    // took focus.
    //
    // Object-nudge is a Selection-tool behavior: in Hammer it lives in Selection3D::OnKeyDown2D, and
    // opening the Face Edit Sheet switches the active tool to the Material tool (CToolMaterial, which
    // has no nudge), so arrow keys do NOT move objects while that popout is open — you're selecting
    // faces, not objects. Match that: suppress object-nudge while our Face Edit popout is open.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        bool arrow = key is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown;
        // Hammer's rule: arrow keys NUDGE the selection only when the Select tool is active AND something
        // is selected; in every other case (no selection, or a different tool is active) they SCROLL the
        // active 2D view. Nudging with any tool active felt disjointed — pressing an arrow while drawing a
        // brush or painting a texture would move the brush unexpectedly.
        bool canNudge = _activeTool == _selectTool && HasSelection()
                        && !IsArrowConsumingFocused() && !IsFaceEditOpen() && NudgeView() != null;
        if (arrow && canNudge)
        {
            bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0;
            switch (key)
            {
                case Keys.Left:     NudgeSelection(-1, 0, 0, ctrl, shift); return true;
                case Keys.Right:    NudgeSelection( 1, 0, 0, ctrl, shift); return true;
                case Keys.Up:       NudgeSelection( 0, 1, 0, ctrl, shift); return true;
                case Keys.Down:     NudgeSelection( 0,-1, 0, ctrl, shift); return true;
                case Keys.PageUp:   NudgeSelection( 0, 0, 1, ctrl, shift); return true;
                case Keys.PageDown: NudgeSelection( 0, 0,-1, ctrl, shift); return true;
            }
        }
        // Otherwise arrow keys SCROLL the focused/last 2D view by ~1/4 of the viewport per press (Hammer).
        else if (arrow && key is Keys.Left or Keys.Right or Keys.Up or Keys.Down
                 && !IsArrowConsumingFocused() && !IsFaceEditOpen())
        {
            var nv = NudgeView();
            var cam = nv?.ActiveCamera2D;
            if (cam != null)
            {
                float stepH = nv!.Width * 0.25f, stepV = nv.Height * 0.25f;   // a quarter-viewport, like Hammer
                switch (key)
                {
                    case Keys.Left:  cam.Pan( stepH, 0); break;
                    case Keys.Right: cam.Pan(-stepH, 0); break;
                    case Keys.Up:    cam.Pan(0,  stepV); break;
                    case Keys.Down:  cam.Pan(0, -stepV); break;
                }
                nv.Invalidate();
                return true;
            }
        }

        // Clipboard / clone from a focused viewport. A focused GL viewport can swallow the menu
        // accelerators (Ctrl+C/X/V/D), so handle them here too — unless a text field has focus, where
        // Ctrl+C/V should edit text. Mirrors Hammer's Edit-menu shortcuts working from the 2D/3D views.
        if (!IsTextInputFocused() && (keyData & Keys.Control) != 0)
            switch (key)
            {
                case Keys.C: CopySelection();    return true;
                case Keys.X: CutSelection();     return true;
                case Keys.V: PasteClipboard();   return true;
                case Keys.D: DuplicateSelection(); return true;
                case Keys.G: _document.GroupSelection();   RedrawAll(); return true;
                case Keys.U: _document.UngroupSelection(); RedrawAll(); return true;
            }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Don't hijack typing in text fields (e.g. the texture search box) as tool shortcuts.
        if (IsTextInputFocused()) return;

        // While fly-navigating the 3D view (RMB held or Z fly mode), route WASD/QE to it regardless
        // of which child control holds keyboard focus, so movement isn't lost to a panel/dialog.
        if (_vp3D.Viewport.IsFlying && IsFlyKey(e.KeyCode))
        { _vp3D.Viewport.SetFlyKey(e.KeyCode, true); e.Handled = true; return; }

        // Ctrl combos are menu accelerators (Copy/Paste/Undo/Save…) handled before this; never let
        // them fall through to the bare-letter tool shortcuts (Ctrl+V must not select the Vertex tool).
        if (e.Control) return;

        // ── Valve Hammer parity: Shift+<letter> selects tools (muscle memory) ──
        if (e.Shift)
        {
            ITool? t = e.KeyCode switch
            {
                Keys.S => _selectTool,  Keys.B => _brushTool,   Keys.E => _entityTool,
                Keys.X => _clipTool,    Keys.V => _vertexTool,  Keys.A => _textureTool,
                Keys.G => _shadeTool,   Keys.Z => _magnifyTool, Keys.C => _cameraTool,
                _ => null,
            };
            if (t != null) { SetActiveTool(t); e.Handled = true; return; }
        }

        // [ and ] shrink / grow the grid (Hammer), through 1..1024.
        if (e.KeyCode == Keys.OemOpenBrackets)  { CycleGrid(-1); e.Handled = true; return; }
        if (e.KeyCode == Keys.OemCloseBrackets) { CycleGrid(+1); e.Handled = true; return; }

        switch (e.KeyCode)
        {
            // Q/E are also 3D-fly camera keys (down/up); only intercept as tool
            // shortcuts when the user is NOT actively fly-navigating with RMB.
            case Keys.Q:
                if (!_vp3D.Viewport.IsRightMouseDown && !_vp3D.Viewport.IsMouseLook)
                { SetActiveTool(_selectTool); e.Handled = true; }
                break;
            case Keys.E:
                if (!_vp3D.Viewport.IsRightMouseDown && !_vp3D.Viewport.IsMouseLook)
                { SetActiveTool(_entityTool); e.Handled = true; }
                break;

            // Z: toggle Hammer/GZDB-style locked fly mode in the 3D view.
            case Keys.Z:      _vp3D.Viewport.ToggleMouseLook(); e.Handled = true; break;

            case Keys.B:      SetActiveTool(_brushTool);   e.Handled = true; break;
            case Keys.T:      SetActiveTool(_textureTool); e.Handled = true; break;
            // D is left unbound as a tool shortcut — it's the 3D-fly strafe-right key, and toggling the
            // Decal tool on it was easy to trigger accidentally while moving the camera. Pick Decal from
            // the toolbar/menu instead.
            case Keys.G:      SetActiveTool(_shadeTool);   e.Handled = true; break;
            case Keys.V:      SetActiveTool(_vertexTool);  e.Handled = true; break;
            case Keys.P:      SetActiveTool(_pathTool);    e.Handled = true; break;
            // X selects the clip tool, or cycles its keep mode if already active.
            case Keys.X:
                if (_activeTool == _clipTool) { _clipTool.CycleKeep(); UpdateStatus(); foreach (var vp in AllViewports()) vp.RequestRedraw(); }
                else SetActiveTool(_clipTool);
                e.Handled = true;
                break;
            // Enter commits the pending clip / brush box (Hammer-style deferred apply).
            case Keys.Enter:
                if (_activeTool == _clipTool) { _clipTool.Apply(); UpdateStatus(); e.Handled = true; }
                else if (_activeTool == _brushTool) { _brushTool.Commit(); UpdateStatus(); e.Handled = true; }
                break;
            case Keys.Escape:
                _vp3D.Viewport.SetMouseLook(false);
                if (_activeTool == _brushTool) _brushTool.Cancel();   // discard the pending box first
                else { _document.ClearSelection(); _document.ClearFaceSelection(); _faceEditDialog?.Refresh2(); RedrawAll(); }
                e.Handled = true;
                break;
            case Keys.Delete: _document.DeleteSelected();  e.Handled = true; break;

            // Arrow keys nudge the selection by one grid unit in the focused 2D view's plane
            // (Hammer); Ctrl = a fine 1-unit nudge, Shift = a coarse 10× grid nudge. PageUp/PageDown
            // nudge along depth (up/down).
            case Keys.Left:  NudgeSelection(-1, 0, 0, e.Control, e.Shift); e.Handled = true; break;
            case Keys.Right: NudgeSelection( 1, 0, 0, e.Control, e.Shift); e.Handled = true; break;
            case Keys.Up:    NudgeSelection( 0, 1, 0, e.Control, e.Shift); e.Handled = true; break;
            case Keys.Down:  NudgeSelection( 0,-1, 0, e.Control, e.Shift); e.Handled = true; break;
            case Keys.PageUp:   NudgeSelection(0, 0,  1, e.Control, e.Shift); e.Handled = true; break;
            case Keys.PageDown: NudgeSelection(0, 0, -1, e.Control, e.Shift); e.Handled = true; break;
        }
    }

    // Moves the selection by (h,v,depth) grid steps in the focused 2D view's basis.
    private void NudgeSelection(int h, int v, int depth, bool fine, bool coarse = false)
    {
        if (!HasSelection()) return;
        var vp = NudgeView();
        var axis = vp?.ActiveCamera2D?.Axis ?? Rendering.ViewAxis.Top;
        var (hDir, vDir, dDir) = axis switch
        {
            Rendering.ViewAxis.Top   => (new OpenTK.Mathematics.Vector3(1, 0, 0), new OpenTK.Mathematics.Vector3(0, 0, -1), new OpenTK.Mathematics.Vector3(0, 1, 0)),
            Rendering.ViewAxis.Front => (new OpenTK.Mathematics.Vector3(1, 0, 0), new OpenTK.Mathematics.Vector3(0, 1, 0),  new OpenTK.Mathematics.Vector3(0, 0, 1)),
            Rendering.ViewAxis.Side  => (new OpenTK.Mathematics.Vector3(0, 0, 1), new OpenTK.Mathematics.Vector3(0, 1, 0),  new OpenTK.Mathematics.Vector3(1, 0, 0)),
            _                        => (new OpenTK.Mathematics.Vector3(1, 0, 0), new OpenTK.Mathematics.Vector3(0, 0, -1), new OpenTK.Mathematics.Vector3(0, 1, 0)),
        };
        // Nudge by the SAME zoom-adaptive grid the 2D view shows and brush-drawing snaps to — so
        // "one cell" means the cell you currently see (finer when zoomed in), not the raw grid-size
        // setting. Ctrl = fine 1-unit, Shift = 10x coarse. Snap-off (or no 2D view) falls back to 1.
        float baseStep = vp?.ActiveCamera2D != null
            ? Editor.GridSnap.ActiveStep(_gridSize, vp.ActiveCamera2D.Zoom)
            : (Editor.ViewOptions.SnapToGrid ? _gridSize : 1);
        if (baseStep < 1) baseStep = 1;
        float step = fine ? 1f : coarse ? baseStep * 10f : baseStep;
        var delta = (hDir * h + vDir * v + dDir * depth) * step;
        if (delta.LengthSquared <= 0) return;

        _document.RecordUndo();
        foreach (var s in _document.Solids) if (s.IsSelected) s.Translate(delta);
        foreach (var a in _document.AllActors) if (a.IsSelected) a.Position += delta;
        foreach (var d in _document.AllDecals) if (d.IsSelected) d.Position += delta;
        _document.NotifyChanged();
        RedrawAll();
    }

    // Hammer-style Clone: duplicates the selection (brushes + actors), offset by one grid unit, and
    // leaves the copies selected so they can be immediately nudged/dragged.
    private void DuplicateSelection()
    {
        if (!HasSelection()) return;
        var offset = new OpenTK.Mathematics.Vector3(_gridSize, 0, _gridSize);
        _document.RecordUndo();
        foreach (var s in _document.Solids.Where(s => s.IsSelected).ToList())
        {
            s.IsSelected = false;
            var c = s.Clone(); c.Translate(offset); c.IsSelected = true;
            _document.AddSolid(c);
        }
        foreach (var a in _document.AllActors.Where(a => a.IsSelected).ToList())
        {
            a.IsSelected = false;
            var c = a.Clone(); c.Position += offset; c.IsSelected = true;
            _document.AddActor(c);
        }
        foreach (var d in _document.AllDecals.Where(d => d.IsSelected).ToList())
        {
            d.IsSelected = false;
            var c = d.Clone(); c.Position += offset; c.IsSelected = true;
            _document.AddDecal(c);
        }
        _document.NormalizeChestFlags();   // a duplicated chest gets a fresh treasure flag (no shared opened-state)
        _document.NotifyChanged();
        RedrawAll();
    }

    // Mirrors the selection across its bounding-box centre on a world axis (0=X,1=Y,2=Z),
    // matching Hammer's Flip. Brushes reflect their clip planes; actors mirror position and
    // (for horizontal flips) facing yaw.
    private void FlipSelection(int axis)
    {
        if (!HasSelection()) return;
        var mn = new OpenTK.Mathematics.Vector3(float.MaxValue);
        var mx = new OpenTK.Mathematics.Vector3(float.MinValue);
        bool any = false;
        foreach (var s in _document.Solids) if (s.IsSelected) { var (a, b) = s.GetAABB(); mn = OpenTK.Mathematics.Vector3.ComponentMin(mn, a); mx = OpenTK.Mathematics.Vector3.ComponentMax(mx, b); any = true; }
        foreach (var ac in _document.AllActors) if (ac.IsSelected) { mn = OpenTK.Mathematics.Vector3.ComponentMin(mn, ac.Position); mx = OpenTK.Mathematics.Vector3.ComponentMax(mx, ac.Position); any = true; }
        foreach (var d in _document.AllDecals) if (d.IsSelected) { mn = OpenTK.Mathematics.Vector3.ComponentMin(mn, d.Position); mx = OpenTK.Mathematics.Vector3.ComponentMax(mx, d.Position); any = true; }
        if (!any) return;
        float center = axis == 0 ? (mn.X + mx.X) * 0.5f : axis == 1 ? (mn.Y + mx.Y) * 0.5f : (mn.Z + mx.Z) * 0.5f;

        _document.RecordUndo();
        foreach (var s in _document.Solids) if (s.IsSelected) s.Flip(axis, center);
        foreach (var ac in _document.AllActors) if (ac.IsSelected)
        {
            var p = ac.Position;
            if (axis == 0) p.X = 2 * center - p.X; else if (axis == 1) p.Y = 2 * center - p.Y; else p.Z = 2 * center - p.Z;
            ac.Position = p;
            // Mirror facing for horizontal flips (yaw is a binary angle about the vertical Y axis).
            if (axis == 0) ac.YRot = (short)(-ac.YRot);
            else if (axis == 2) ac.YRot = (short)(0x8000 - ac.YRot);
        }
        // Decals reflect their position + surface normal across the plane (a decal on a wall facing the flip
        // axis also mirrors its texture, since its U/V axes derive from the normal); Rotation sense flips.
        foreach (var d in _document.AllDecals) if (d.IsSelected)
        {
            var p = d.Position; var n = d.Normal;
            if (axis == 0)      { p.X = 2 * center - p.X; n.X = -n.X; }
            else if (axis == 1) { p.Y = 2 * center - p.Y; n.Y = -n.Y; }
            else                { p.Z = 2 * center - p.Z; n.Z = -n.Z; }
            d.Position = p; d.Normal = n;
            d.Rotation = -d.Rotation;
        }
        _document.NotifyChanged();
        RedrawAll();
    }

    // World-space bounds of the whole selection (solids' AABBs + actor positions).
    private (OpenTK.Mathematics.Vector3 min, OpenTK.Mathematics.Vector3 max, bool any) SelectionBounds()
    {
        var mn = new OpenTK.Mathematics.Vector3(float.MaxValue);
        var mx = new OpenTK.Mathematics.Vector3(float.MinValue);
        bool any = false;
        foreach (var s in _document.Solids) if (s.IsSelected) { var (a, b) = s.GetAABB(); mn = OpenTK.Mathematics.Vector3.ComponentMin(mn, a); mx = OpenTK.Mathematics.Vector3.ComponentMax(mx, b); any = true; }
        foreach (var ac in _document.AllActors) if (ac.IsSelected) { mn = OpenTK.Mathematics.Vector3.ComponentMin(mn, ac.Position); mx = OpenTK.Mathematics.Vector3.ComponentMax(mx, ac.Position); any = true; }
        return (mn, mx, any);
    }

    // Hammer Transform dialog: exact Move / Rotate / Scale of the selection by typed values.
    private void TransformSelection()
    {
        if (!HasSelection()) return;
        using var dlg = new TransformDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var (mn, mx, any) = SelectionBounds();
        if (!any) return;
        var pivot = (mn + mx) * 0.5f;
        var v = new OpenTK.Mathematics.Vector3(dlg.X, dlg.Y, dlg.Z);
        _document.RecordUndo();
        foreach (var s in _document.Solids) if (s.IsSelected)
            switch (dlg.Mode)
            {
                case TransformDialog.TransformMode.Move:   s.Translate(v); break;
                case TransformDialog.TransformMode.Rotate: s.Rotate(pivot, v); break;
                case TransformDialog.TransformMode.Scale:  s.ScaleAbout(pivot, NonZero(v)); break;
            }
        foreach (var ac in _document.AllActors) if (ac.IsSelected)
        {
            var p = ac.Position;
            switch (dlg.Mode)
            {
                case TransformDialog.TransformMode.Move: ac.Position = p + v; break;
                case TransformDialog.TransformMode.Rotate:
                    // Rotate position about the pivot in the X/Z plane by the Y angle; spin the yaw too.
                    float t = v.Y * MathF.PI / 180f, ct = MathF.Cos(t), st = MathF.Sin(t);
                    float dx = p.X - pivot.X, dz = p.Z - pivot.Z;
                    ac.Position = new OpenTK.Mathematics.Vector3(pivot.X + dx * ct - dz * st, p.Y, pivot.Z + dx * st + dz * ct);
                    ac.YRot = (short)(ac.YRot + (int)(v.Y / 360f * 65536f));
                    break;
                case TransformDialog.TransformMode.Scale:
                    var sc = NonZero(v);
                    ac.Position = new OpenTK.Mathematics.Vector3(pivot.X + (p.X - pivot.X) * sc.X, pivot.Y + (p.Y - pivot.Y) * sc.Y, pivot.Z + (p.Z - pivot.Z) * sc.Z);
                    break;
            }
        }
        _document.NotifyChanged();
        RedrawAll();

        static OpenTK.Mathematics.Vector3 NonZero(OpenTK.Mathematics.Vector3 s) =>
            new(s.X == 0 ? 1 : s.X, s.Y == 0 ? 1 : s.Y, s.Z == 0 ? 1 : s.Z);
    }

    // Hammer Align: move each selected object so its bound edge meets the selection's edge.
    // edge: 0=Left(min X), 1=Right(max X), 2=Top(max Z), 3=Bottom(min Z) — in the Top view plane.
    private void AlignSelection(int edge)
    {
        if (!HasSelection()) return;
        var (mn, mx, any) = SelectionBounds();
        if (!any) return;
        float target = edge switch { 0 => mn.X, 1 => mx.X, 2 => mx.Z, _ => mn.Z };
        _document.RecordUndo();
        foreach (var s in _document.Solids) if (s.IsSelected)
        {
            var (a, b) = s.GetAABB();
            float cur = edge switch { 0 => a.X, 1 => b.X, 2 => b.Z, _ => a.Z };
            s.Translate(edge < 2 ? new OpenTK.Mathematics.Vector3(target - cur, 0, 0) : new OpenTK.Mathematics.Vector3(0, 0, target - cur));
        }
        foreach (var ac in _document.AllActors) if (ac.IsSelected)
        {
            var p = ac.Position;
            ac.Position = edge < 2 ? new OpenTK.Mathematics.Vector3(target, p.Y, p.Z) : new OpenTK.Mathematics.Vector3(p.X, p.Y, target);
        }
        _document.NotifyChanged();
        RedrawAll();
    }

    // Snap each selected object's origin/AABB-min to the grid (Hammer "Snap Selected to Grid").
    private void SnapSelectionToGrid()
    {
        if (!HasSelection()) return;
        _document.RecordUndo();
        float g = _gridSize;
        float Snap(float v) => MathF.Round(v / g) * g;
        foreach (var s in _document.Solids) if (s.IsSelected)
        {
            var (a, _) = s.GetAABB();
            s.Translate(new OpenTK.Mathematics.Vector3(Snap(a.X) - a.X, Snap(a.Y) - a.Y, Snap(a.Z) - a.Z));
        }
        foreach (var ac in _document.AllActors) if (ac.IsSelected)
            ac.Position = new OpenTK.Mathematics.Vector3(Snap(ac.XPos), Snap(ac.YPos), Snap(ac.ZPos));
        _document.NotifyChanged();
        RedrawAll();
    }

    // Hammer "Replace Textures": swap one texture for another across the map (or selected faces).
    private ReplaceTexturesDialog? _replaceDlg;

    private void OpenReplaceTextures()
    {
        // Modeless (Hammer keeps the map interactive): the user can change the 3D face selection between
        // replacements, and "Selected faces only" applies to the CURRENT selection each time Replace is hit.
        if (_replaceDlg is { IsDisposed: false })
        {
            if (_replaceDlg.WindowState == FormWindowState.Minimized) _replaceDlg.WindowState = FormWindowState.Normal;
            _replaceDlg.Activate();
            return;
        }
        _replaceDlg = new ReplaceTexturesDialog(_textureLib);
        _replaceDlg.ReplaceRequested += () =>
        {
            _document.RecordUndo();
            bool selOnly = _replaceDlg.SelectedOnly;
            string find = _replaceDlg.Find, repl = _replaceDlg.Replace;
            int n = 0;
            foreach (var s in _document.Solids)
                foreach (var f in s.Faces)
                {
                    if (selOnly && !f.FaceSelected) continue;
                    if (string.Equals(f.TextureName, find, StringComparison.OrdinalIgnoreCase)) { f.TextureName = repl; n++; }
                }
            _document.NotifyChanged();
            RedrawAll();
            _replaceDlg.ShowResult(n);
        };
        _replaceDlg.Show(this);   // modeless, owned by the main window
    }

    // Release forwarded fly keys (paired with the IsFlying forwarding in OnKeyDown), so a key
    // held during fly navigation is cleared even if this viewport never had keyboard focus.
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (IsFlyKey(e.KeyCode)) _vp3D.Viewport.SetFlyKey(e.KeyCode, false);
    }

    private static bool IsFlyKey(Keys k) =>
        k is Keys.W or Keys.A or Keys.S or Keys.D or Keys.Q or Keys.E;

    // Grid sizes Hammer cycles through with [ and ].
    private static readonly int[] GridSizes = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024];
    private ToolStripButton? _btnGrid;

    private void CycleGrid(int dir)
    {
        int idx = Array.IndexOf(GridSizes, _gridSize);
        if (idx < 0) idx = 6; // 64
        idx = (idx + dir + GridSizes.Length) % GridSizes.Length;   // wrap (never stuck at 1 or 1024)
        _gridSize = GridSizes[idx];
        foreach (var vp in AllViewports()) { vp.GridSize = _gridSize; vp.RequestRedraw(); }
        UpdateGridButton();
        UpdateStatus();
    }

    // ── Clipboard / edit operations (Hammer Cut/Copy/Paste) ────────────────

    private void RedrawAll() { foreach (var vp in AllViewports()) vp.RequestRedraw(); }
    private bool HasSelection() => _document.SelectedSolid != null || _document.SelectedActor != null || _document.SelectedDecal != null;

    private void CopySelection()
    {
        Editor.EditClipboard.CopyFrom(_document);
        UpdateStatus();
    }

    private void CutSelection()
    {
        if (!HasSelection()) return;
        Editor.EditClipboard.CopyFrom(_document);
        _document.DeleteSelected();    // records its own undo
        RedrawAll(); UpdateStatus();
    }

    private void PasteClipboard()
    {
        if (!Editor.EditClipboard.HasContent) return;
        _document.RecordUndo();
        _document.ClearSelection();
        var (solids, actors, decals) = Editor.EditClipboard.Instantiate();
        // Hammer's GetBestPastePoint drops the clipboard into the view you're WORKING IN: the centre of the
        // active 2D view (keeping the original depth on the off-plane axis), or the 3D view's centre when
        // that's the active view. Grid-snapped. (Paste Special is what keeps the exact original position.)
        var target = SnapToGrid(PasteTarget());
        var delta = target - Editor.EditClipboard.Center;
        foreach (var s in solids) { s.Translate(delta); s.IsSelected = true; _document.AddSolid(s); }
        foreach (var a in actors) { a.Position += delta; a.IsSelected = true; _document.AddActor(a); }
        foreach (var d in decals) { d.Position += delta; d.IsSelected = true; _document.AddDecal(d); }
        // Clones inherit the source's GroupId (Clone copies it — needed for undo snapshots), so a pasted
        // object would otherwise join the ORIGINAL's group: clicking the paste would also grab the sources
        // (and same-actor pastes would appear "linked"). Clear it so every pasted item is INDEPENDENT — the
        // pasted set is NOT auto-grouped; use Ctrl+G to group them if you want the paste to move as one unit.
        foreach (var s in solids) s.GroupId = 0;
        foreach (var a in actors) a.GroupId = 0;
        foreach (var d in decals) d.GroupId = 0;
        // A pasted chest keeps the source's treasure flag (a collision → shared opened-state); give it a fresh one.
        _document.NormalizeChestFlags();
        // #14: the pasted content is now the selection — make the Select tool active in fresh Scale mode so
        // it's immediately draggable and a click cycles to Rotate (Hammer pastes ready-to-manipulate).
        SetActiveTool(_selectTool);
        _selectTool.ResetToScale();
        _document.NotifyChanged();
        RedrawAll(); UpdateStatus();
        if (_statusLabel != null)
            _statusLabel.Text = $"Pasted {solids.Count} brush(es) + {actors.Count} entity(s) + {decals.Count} decal(s) at the view centre — now selected; drag or arrow-key to move";
    }

    // Where plain Paste drops the clipboard: the centre of the view the user is working in (Hammer's
    // GetBestPastePoint). The active 2D view's centre keeps the clipboard's original depth on the off-plane
    // axis; the 3D view falls back to its centre ray-hit.
    private OpenTK.Mathematics.Vector3 PasteTarget()
    {
        var vp = AllViewports().FirstOrDefault(p => p.Focused)
                 ?? (_lastFocused2D is { IsDisposed: false } l ? l : _vp3D.Viewport);
        var cam = vp.ActiveCamera2D;
        if (cam == null) return View3DCenter();
        var c = Editor.EditClipboard.Center;   // off-plane (depth) axis stays where the originals were
        return cam.Axis switch
        {
            Rendering.ViewAxis.Top   => new(cam.PanX, c.Y, -cam.PanY),
            Rendering.ViewAxis.Front => new(cam.PanX, cam.PanY, c.Z),
            Rendering.ViewAxis.Side  => new(c.X, cam.PanY, cam.PanX),
            _                        => new(cam.PanX, cam.PanY, c.Z),
        };
    }

    // The world point at the centre of the 3D view: where the centre ray hits geometry/ground, else a
    // fixed distance ahead. Used by plain Paste (Hammer drops the clipboard here).
    private OpenTK.Mathematics.Vector3 View3DCenter()
    {
        var vp = _vp3D.Viewport;
        var cam = vp.ActiveCamera3D;
        // The 3D camera may not exist yet (its GL context initialises on the first paint) — returning the
        // world origin here made Paste drop the brush at (0,0,0), off-screen for any scene not near origin
        // (the "invisible pasted brush" that started working after a few tries). Paste in place instead, so
        // the clone appears exactly over its source (selected, ready to drag) rather than vanishing.
        if (cam == null) return Editor.EditClipboard.Center;
        var ray = Editor.Picking.RayFromScreen(cam, Math.Max(1, vp.Width) / 2, Math.Max(1, vp.Height) / 2, vp.Width, vp.Height);
        return Editor.Picking.PickPoint(_document.Scene, ray, out var p) ? p : ray.Origin + ray.Direction * 384f;
    }

    private OpenTK.Mathematics.Vector3 SnapToGrid(OpenTK.Mathematics.Vector3 v)
    {
        if (!Editor.GridSnap.SnappingActive || _gridSize < 1) return v;
        float g = _gridSize;
        return new(MathF.Round(v.X / g) * g, MathF.Round(v.Y / g) * g, MathF.Round(v.Z / g) * g);
    }

    private void OpenPasteSpecial()
    {
        if (!Editor.EditClipboard.HasContent)
        {
            MessageBox.Show(this, "The clipboard is empty — copy a selection first.",
                "Paste Special", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new PasteSpecialDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        ApplyPasteSpecial(dlg.Result);
    }

    private void ApplyPasteSpecial(PasteSpecialDialog.Options o)
    {
        _document.RecordUndo();
        _document.ClearSelection();

        var pivot = o.UsePivot ? Editor.EditClipboard.Center : OpenTK.Mathematics.Vector3.Zero;
        var used  = new HashSet<string>(
            _document.AllActors.Where(a => a.Name != null).Select(a => a.Name!), StringComparer.OrdinalIgnoreCase);

        for (int k = 0; k < o.Copies; k++)
        {
            // "Start at center of original" places the first copy on the original (factor 0).
            int factor = o.StartAtCenter ? k : k + 1;
            var (solids, actors, decals) = Editor.EditClipboard.Instantiate();
            var offset = new OpenTK.Mathematics.Vector3(o.OffX * factor, o.OffY * factor, o.OffZ * factor);
            var q = OpenTK.Mathematics.Quaternion.FromEulerAngles(
                OpenTK.Mathematics.MathHelper.DegreesToRadians(o.RotX * factor),
                OpenTK.Mathematics.MathHelper.DegreesToRadians(o.RotY * factor),
                OpenTK.Mathematics.MathHelper.DegreesToRadians(o.RotZ * factor));

            foreach (var s in solids)
            {
                // Rotate about the pivot (rotation is orthogonal, so normal-map == point-map), then offset.
                var snap = s.SnapshotPlanes();
                s.TransformAbout(snap,
                    v => OpenTK.Mathematics.Vector3.Transform(v, q),
                    v => OpenTK.Mathematics.Vector3.Transform(v, q), pivot);
                s.Translate(offset);
                s.IsSelected = true;
                _document.AddSolid(s);
            }
            foreach (var a in actors)
            {
                var rel = a.Position - pivot;
                a.Position = pivot + OpenTK.Mathematics.Vector3.Transform(rel, q) + offset;
                a.XRot = (short)(a.XRot + DegToBinAngle(o.RotX * factor));
                a.YRot = (short)(a.YRot + DegToBinAngle(o.RotY * factor));
                a.ZRot = (short)(a.ZRot + DegToBinAngle(o.RotZ * factor));

                if (o.Prefix != null) a.Name = o.Prefix + (a.Name ?? a.DisplayName);
                if (o.MakeNamesUnique) a.Name = UniqueName(a.Name ?? a.DisplayName, used);
                a.IsSelected = true;
                _document.AddActor(a);
            }
            foreach (var d in decals)
            {
                // Orbit the decal's centre + its facing normal about the pivot (rotation is orthogonal), then
                // offset — so a special-pasted decal array rotates with the rest of the selection.
                var rel = d.Position - pivot;
                d.Position = pivot + OpenTK.Mathematics.Vector3.Transform(rel, q) + offset;
                d.Normal   = OpenTK.Mathematics.Vector3.Transform(d.Normal, q);
                d.IsSelected = true;
                _document.AddDecal(d);
            }
            // Clones inherit the source's GroupId (Clone copies it) — clear it so a special-pasted copy
            // isn't "linked" to the original. Not auto-grouped (Ctrl+G to group), matching plain Paste.
            foreach (var s in solids) s.GroupId = 0;
            foreach (var a in actors) a.GroupId = 0;
            foreach (var d in decals) d.GroupId = 0;
            _document.NormalizeChestFlags();   // pasted chests get a fresh treasure flag (no shared opened-state)
        }
        _document.NotifyChanged();
        RedrawAll(); UpdateStatus();
    }

    private static short DegToBinAngle(float deg) => (short)(deg * 65536f / 360f);

    private static string UniqueName(string baseName, HashSet<string> used)
    {
        string name = baseName;
        int n = 1;
        while (used.Contains(name)) name = $"{baseName}{n++:00}";
        used.Add(name);
        return name;
    }

    // ── Entity tooling (Find Entities / Entity Report) ─────────────────────

    private EntityReportDialog? _entityReport;

    private void OpenEntityReport()
    {
        if (_entityReport is { IsDisposed: false }) { _entityReport.Activate(); _entityReport.Refresh2(); return; }
        _entityReport = new EntityReportDialog(_document, _actorDb);
        _entityReport.GoToRequested += GoToEntity;
        _entityReport.PropertiesRequested += OpenEntityConfig;
        _entityReport.Show(this);            // modeless — stays open while editing
    }

    private FlagConnectionsDialog? _flagConnections;

    private void OpenFlagConnections()
    {
        if (_flagConnections is { IsDisposed: false }) { _flagConnections.Activate(); _flagConnections.Rebuild(); return; }
        _flagConnections = new FlagConnectionsDialog(_document, _config.IsOoTBased);
        _flagConnections.GoToRequested += GoToEntity;
        _flagConnections.Show(this);         // modeless
    }

    private SceneCheckDialog? _sceneCheck;

    private void OpenSceneCheck()
    {
        if (_sceneCheck is { IsDisposed: false }) { _sceneCheck.Activate(); _sceneCheck.Rebuild(); return; }
        _sceneCheck = new SceneCheckDialog(_document, _config.IsOoTBased);
        _sceneCheck.GoToRequested += GoToEntity;
        _sceneCheck.Show(this);              // modeless
    }

    private void OpenFindEntities()
    {
        using var dlg = new FindEntitiesDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Query.Length == 0) return;
        string q = dlg.Query;
        var match = _document.AllActors.FirstOrDefault(a => string.Equals(a.Name, q, StringComparison.OrdinalIgnoreCase))
                 ?? _document.AllActors.FirstOrDefault(a => (a.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                 ?? _document.AllActors.FirstOrDefault(a => a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            MessageBox.Show(this, $"No entity matching \"{q}\".", "Find Entities", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        GoToEntity(match);
    }

    // Selects an entity (actor or trigger brush) and centres every view on it.
    private void GoToEntity(object o)
    {
        _document.ClearSelection();
        OpenTK.Mathematics.Vector3 pos;
        if (o is ZActor a) { a.IsSelected = true; pos = a.Position; }
        else if (o is Solid s) { s.IsSelected = true; var (mn, mx) = s.GetAABB(); pos = (mn + mx) * 0.5f; }
        else return;
        _document.NotifyChanged();
        CenterViewsOn(pos);
    }

    private void CenterViewsOn(OpenTK.Mathematics.Vector3 p)
    {
        foreach (var panel in new[] { _vpTop, _vpFront, _vpSide })
        {
            var cam = panel.Viewport.ActiveCamera2D;
            if (cam == null) continue;
            (cam.PanX, cam.PanY) = cam.Axis switch
            {
                Rendering.ViewAxis.Top   => (p.X, -p.Z),
                Rendering.ViewAxis.Front => (p.X,  p.Y),
                Rendering.ViewAxis.Side  => (p.Z,  p.Y),
                _                        => (cam.PanX, cam.PanY),
            };
        }
        var cam3 = _vp3D.Viewport.ActiveCamera3D;
        if (cam3 != null)
        {
            cam3.Position = p + new OpenTK.Mathematics.Vector3(0f, 200f, 400f);
            cam3.Yaw = -90f; cam3.Pitch = -20f;
        }
        RedrawAll();
    }

    // Hammer "Center Views on Selection" (Ctrl+E).
    private void CenterOnSelection()
    {
        var (mn, mx, any) = SelectionBounds();
        if (any) CenterViewsOn((mn + mx) * 0.5f);
    }

    // Hammer "Go to Coordinates": type X Y Z and centre all views there.
    private void GoToCoordinates()
    {
        using var dlg = new Form
        {
            Text = "Go to Coordinates", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false,
            MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(280, 96),
            BackColor = Color.FromArgb(37, 37, 38), ForeColor = Color.FromArgb(210, 210, 210), Font = new Font("Segoe UI", 8.5f),
        };
        dlg.Controls.Add(new Label { Text = "X  Y  Z  (world units):", Left = 12, Top = 12, Width = 250 });
        var box = new TextBox { Left = 12, Top = 34, Width = 256, Text = "0 0 0",
            BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.FromArgb(210, 210, 210), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9f) };
        dlg.Controls.Add(box);
        var ok = new Button { Text = "Go", Left = 112, Top = 62, Width = 70, Height = 26, DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", Left = 190, Top = 62, Width = 78, Height = 26, DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(60, 60, 65), ForeColor = Color.FromArgb(210, 210, 210), FlatStyle = FlatStyle.Flat };
        dlg.Controls.AddRange([ok, cancel]); dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var parts = box.Text.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries);
        float P(int i) => i < parts.Length && float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
        CenterViewsOn(new OpenTK.Mathematics.Vector3(P(0), P(1), P(2)));
    }

    // Hammer "Properties" (Alt+Enter): open the config dialog for the selected actor.
    private void OpenSelectedProperties()
    {
        if (_document.SelectedActor is { } a) OpenEntityConfig(a);
    }

    private void ShowEditContextMenu(GLViewport vp, Point pt)
    {
        bool sel = HasSelection();
        bool clip = Editor.EditClipboard.HasContent;
        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(220, 220, 220),
            ShowImageMargin = false,
        };
        ToolStripMenuItem MI(string text, string shortcut, Action act, bool enabled)
        {
            var it = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = shortcut, Enabled = enabled };
            it.Click += (_, _) => act();
            return it;
        }
        menu.Items.Add(MI("Cut", "Ctrl+X", CutSelection, sel));
        menu.Items.Add(MI("Copy", "Ctrl+C", CopySelection, sel));
        menu.Items.Add(MI("Paste", "Ctrl+V", PasteClipboard, clip));
        menu.Items.Add(MI("Paste Special…", "", OpenPasteSpecial, clip));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MI("Delete", "Del", () => { _document.DeleteSelected(); RedrawAll(); UpdateStatus(); }, sel));
        menu.Items.Add(new ToolStripSeparator());
        // #25: view-relative Flip Horizontal / Vertical (the Edit menu has the world-axis Flip X/Y/Z).
        // H/V map to world axes by the 2D view's plane: Top→(X,Z), Front→(X,Y), Side→(Z,Y).
        var (hAxis, vAxis) = vp.ActiveCamera2D?.Axis switch
        {
            Rendering.ViewAxis.Front => (0, 1),
            Rendering.ViewAxis.Side  => (2, 1),
            _                        => (0, 2),   // Top (and default)
        };
        menu.Items.Add(MI("Flip Horizontal", "", () => { FlipSelection(hAxis); RedrawAll(); }, sel));
        menu.Items.Add(MI("Flip Vertical",   "", () => { FlipSelection(vAxis); RedrawAll(); }, sel));
        menu.Items.Add(new ToolStripSeparator());
        // Grouping (Hammer Ctrl+G / Ctrl+U) — clicking any member then selects the whole group.
        menu.Items.Add(MI("Group", "Ctrl+G", () => { _document.GroupSelection(); RedrawAll(); }, sel));
        menu.Items.Add(MI("Ungroup", "Ctrl+U", () => { _document.UngroupSelection(); RedrawAll(); }, sel));
        menu.Items.Add(new ToolStripSeparator());
        // Rooms: create a new room from the selection, or move the selection into an existing room.
        menu.Items.Add(MI("Create Room from Selection…", "", CreateRoomFromSelection, sel));
        var moveItem = new ToolStripMenuItem("Assign Selection to Room") { Enabled = sel && _document.Scene.Rooms.Count > 0 };
        foreach (var room in _document.Scene.Rooms)
        {
            var r = room;
            var sub = new ToolStripMenuItem(room.Name) { Checked = SelectionLivesIn(r) };
            sub.Click += (_, _) => MoveSelectionToRoom(r);
            moveItem.DropDownItems.Add(sub);
        }
        if (moveItem.DropDownItems.Count == 0) moveItem.Enabled = false;   // no rooms → grayed out
        menu.Items.Add(moveItem);
        menu.Items.Add(new ToolStripSeparator());
        // Properties…: an actor opens its config; a brush opens the full Brush Properties pop-out.
        menu.Items.Add(MI("Properties…", "",
            () =>
            {
                var a = _document.SelectedActor;
                if (a != null) OpenEntityConfig(a);
                else if (_document.SelectedSolid is { } s) OpenSolidProperties(s);
            },
            _document.SelectedActor != null || _document.SelectedSolid != null));
        menu.Show(vp, pt);
    }

    // ── Rooms from selection (right-click menu) ─────────────────────────────

    /// <summary>The selected brushes/actors (skipping editor-only dummies like the Player-start marker).</summary>
    private (List<Editor.Solid> solids, List<Editor.ZActor> actors) SelectionForRoom()
        => (_document.Solids.Where(s => s.IsSelected).ToList(),
            _document.AllActors.Where(a => a.IsSelected && !a.IsEditorOnly).ToList());

    /// <summary>True if every selected object already belongs to <paramref name="room"/> (for the checkmark).</summary>
    private bool SelectionLivesIn(Editor.ZRoom room)
    {
        var (solids, actors) = SelectionForRoom();
        if (solids.Count + actors.Count == 0) return false;
        return solids.All(room.Geometry.Contains) && actors.All(room.Actors.Contains);
    }

    /// <summary>Prompt for a name, create a new room, and move the selection into it.</summary>
    private void CreateRoomFromSelection()
    {
        if (!HasSelection()) return;
        string? name = PromptForString("New Room", "Room name:", $"Room {_document.Scene.Rooms.Count}");
        if (name == null) return;                       // cancelled
        name = name.Trim();
        if (name.Length == 0) name = $"Room {_document.Scene.Rooms.Count}";
        _document.RecordUndo();
        var room = _document.Scene.AddRoom();
        room.Name = name;
        MoveSelectionInto(room);
        _document.Scene.ActiveRoom = room;              // make it current so further edits land here
        _document.NotifyChanged();                      // refreshes the hierarchy room list + viewports
        UpdateStatus();
    }

    /// <summary>Move the current selection into an existing room.</summary>
    private void MoveSelectionToRoom(Editor.ZRoom target)
    {
        if (!HasSelection()) return;
        _document.RecordUndo();
        MoveSelectionInto(target);
        _document.NotifyChanged();
        RedrawAll();
        UpdateStatus();
    }

    // Detach the selected solids/actors from whatever room holds them and add them to target.
    private void MoveSelectionInto(Editor.ZRoom target)
    {
        var (solids, actors) = SelectionForRoom();
        foreach (var r in _document.Scene.Rooms)
        {
            foreach (var s in solids) r.Geometry.Remove(s);
            foreach (var a in actors) r.Actors.Remove(a);
        }
        foreach (var s in solids) target.Geometry.Add(s);
        foreach (var a in actors) target.Actors.Add(a);
    }

    // A small modal text prompt (OK / Cancel). Returns null if cancelled.
    private string? PromptForString(string title, string label, string initial)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(320, 104),
            BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(220, 220, 220),
        };
        var lbl = new Label { Text = label, Left = 12, Top = 12, AutoSize = true };
        var tb = new TextBox { Text = initial, Left = 12, Top = 34, Width = 296,
            BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        tb.SelectAll();
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 152, Top = 68, Width = 74,
            BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 234, Top = 68, Width = 74,
            BackColor = Color.FromArgb(60, 60, 65), ForeColor = Color.FromArgb(220, 220, 220), FlatStyle = FlatStyle.Flat };
        f.Controls.AddRange([lbl, tb, ok, cancel]);
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
    }

    // ── Render loop ───────────────────────────────────────────────────────

    private void SplitterFix(SplitContainer outer, SplitContainer left, SplitContainer right)
    {
        outer.SplitterDistance = outer.Width  / 2;
        left.SplitterDistance  = left.Height  / 2;
        right.SplitterDistance = right.Height / 2;
    }

    // Hammer-style pane maximize: double-clicking a viewport header fills the window with that view;
    // double-clicking again (any header) restores the 4-pane grid. Implemented by collapsing the split
    // panels that don't contain the target so only its pane remains visible.
    private void ToggleMaximizeViewport(ViewportPanel panel)
    {
        if (_outerSplit == null || _leftSplit == null || _rightSplit == null) return;

        if (_maximizedPanel != null) { RestorePanes(); if (_maximizedPanel == panel) { _maximizedPanel = null; return; } }

        bool onLeft = panel == _vp3D || panel == _vpFront;     // _vp3D/_vpFront live in _leftSplit
        // Outer: keep the side holding this panel; inner: keep this panel's half.
        _outerSplit.Panel1Collapsed = !onLeft;
        _outerSplit.Panel2Collapsed =  onLeft;
        var inner = onLeft ? _leftSplit : _rightSplit;
        bool isPanel1 = panel == _vp3D || panel == _vpTop;     // Panel1 of each inner split
        inner.Panel1Collapsed = !isPanel1;
        inner.Panel2Collapsed =  isPanel1;
        _maximizedPanel = panel;
        panel.Viewport.Focus();
    }

    private void RestorePanes()
    {
        if (_outerSplit == null || _leftSplit == null || _rightSplit == null) return;
        _outerSplit.Panel1Collapsed = _outerSplit.Panel2Collapsed = false;
        _leftSplit.Panel1Collapsed  = _leftSplit.Panel2Collapsed  = false;
        _rightSplit.Panel1Collapsed = _rightSplit.Panel2Collapsed = false;
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        float dt = (float)(now - _lastTick).TotalSeconds;
        _lastTick = now;
        // Clamp: after a stall (GC pause, window unfocused) a huge dt would teleport the camera.
        if (dt > 0.1f) dt = 0.1f;

        _vp3D.Viewport.Tick(dt);

        foreach (var vp in AllViewports())
            vp.RequestRedraw();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _renderTimer.Stop();
        _autoSaveTimer?.Stop();
        Editor.AutoSave.EndSessionCleanly();   // clean exit → clear the lock + crash backup
        _o2rSource?.Dispose();
        _romSource?.Dispose();
        base.OnFormClosed(e);
    }

    // ── Menu builder ──────────────────────────────────────────────────────

    private MenuStrip BuildMenuStrip()
    {
        var strip = new MenuStrip
        {
            BackColor = Color.FromArgb(45, 45, 48),
            ForeColor = Color.FromArgb(220, 220, 220)
        };

        _openRecentMenu = new ToolStripMenuItem("Open Recent") { ForeColor = Color.FromArgb(220, 220, 220) };
        PopulateRecentMenu();   // populate once now; refreshed each time the File menu opens

        var file = Menu("File",
            Item("New Project",   Keys.Control | Keys.N, (_, _) => NewMap()),
            Item("Open Project…", Keys.Control | Keys.O, (_, _) => OpenProject()),
            _openRecentMenu,
            Item("Close Project", Keys.Control | Keys.W, (_, _) => CloseProject()),
            Item("Save Project",  Keys.Control | Keys.S, (_, _) => SaveProject()),
            Item("Save As…",      Keys.Control | Keys.Shift | Keys.S, (_, _) => SaveProjectAs()),
            new ToolStripSeparator(),
            Item("Import Level from ROM…", Keys.None, (_, _) => ImportFromRom()),
            BuildGhostMenu(),
            Menu("Import",
                Item("Wavefront OBJ…  (.obj)",       Keys.None, (_, _) => ImportObj()),
                Item("Blender  (via .obj)",          Keys.None, (_, _) => ImportObj()),
                Item("OcarinaSharp scene  (.obj/.zobj)", Keys.None, (_, _) => ImportObj())),
            Menu("Export",
                Item("Wavefront OBJ…  (.obj)",       Keys.None, (_, _) => ExportFormat("obj")),
                Item("Blender  (.obj)",              Keys.None, (_, _) => ExportFormat("obj")),
                Item("Valve Hammer VMF…  (.vmf)",    Keys.None, (_, _) => ExportFormat("vmf")),
                Item("OcarinaSharp / N64 scene…",    Keys.None, (_, _) => OpenExportDialog()),
                new ToolStripSeparator(),
                Item("Level render (isometric / top-down)…", Keys.None, (_, _) => ExportLevelRender())),
            new ToolStripSeparator(),
            Item("Exit", Keys.Alt | Keys.F4, (_, _) => Close()));
        file.DropDownOpening += (_, _) => PopulateRecentMenu();

        var edit = Menu("Edit",
            Item("Undo",        Keys.Control | Keys.Z, (_, _) => DoUndo()),
            Item("Redo",        Keys.Control | Keys.Y, (_, _) => DoRedo()),
            new ToolStripSeparator(),
            Item("Cut",          Keys.Control | Keys.X, (_, _) => CutSelection()),
            Item("Copy",         Keys.Control | Keys.C, (_, _) => CopySelection()),
            Item("Paste",        Keys.Control | Keys.V, (_, _) => PasteClipboard()),
            Item("Paste Special…", Keys.None,           (_, _) => OpenPasteSpecial()),
            Item("Clone (duplicate)", Keys.Control | Keys.D, (_, _) => DuplicateSelection()),
            new ToolStripSeparator(),
            Item("Group",   Keys.Control | Keys.G, (_, _) => { _document.GroupSelection();   RedrawAll(); }),
            Item("Ungroup", Keys.Control | Keys.U, (_, _) => { _document.UngroupSelection(); RedrawAll(); }),
            new ToolStripSeparator(),
            Item("Select All",  Keys.Control | Keys.A, (_, _) => { foreach (var s in _document.Solids) s.IsSelected = true; RedrawAll(); }),
            Item("Deselect (Esc)", Keys.None,           (_, _) => _document.ClearSelection()),
            Item("Delete",      Keys.None,              (_, _) => _document.DeleteSelected()),
            new ToolStripSeparator(),
            Item("Flip X",      Keys.None,              (_, _) => FlipSelection(0)),
            Item("Flip Y (vertical)", Keys.None,        (_, _) => FlipSelection(1)),
            Item("Flip Z",      Keys.None,              (_, _) => FlipSelection(2)),
            new ToolStripSeparator(),
            Item("Properties",  Keys.Alt | Keys.Enter,  (_, _) => OpenSelectedProperties()));

        var view = Menu("View",
            Item("Reset 3D Camera",   Keys.None, (_, _) =>
            {
                var cam3 = _vp3D.Viewport.ActiveCamera3D;
                if (cam3 != null) { cam3.Position = new(0f, 128f, 512f); cam3.Yaw = -90f; cam3.Pitch = -10f; }
            }),
            Item("Reset 2D Views",    Keys.None, (_, _) => ResetCameras2D()),
            Item("Center on Selection", Keys.Control | Keys.E, (_, _) => CenterOnSelection()),
            Item("Go to Coordinates…", Keys.None, (_, _) => GoToCoordinates()),
            new ToolStripSeparator(),
            CheckItem("Show Sky (3D)", Editor.ViewOptions.ShowSky,
                      v => { Editor.ViewOptions.ShowSky = v; Editor.EditorSettings.ShowSky = v; _vp3D.Viewport.RequestRedraw(); }),
            CheckItem("Show 3D Grid", Editor.ViewOptions.ShowGrid3D,
                      v => { Editor.ViewOptions.ShowGrid3D = v; Editor.EditorSettings.ShowGrid3D = v; _vp3D.Viewport.RequestRedraw(); }),
            CheckItem("Show Pre-rendered Background", Editor.ViewOptions.ShowPrerenderedBackground,
                      v => { Editor.ViewOptions.ShowPrerenderedBackground = v; Editor.EditorSettings.ShowPrerenderedBackground = v; foreach (var vp in AllViewports()) vp.RequestRedraw(); }),
            CheckItem("Show Entities (3D)", Editor.ViewOptions.ShowEntities3D,
                      v => { Editor.ViewOptions.ShowEntities3D = v; foreach (var vp in AllViewports()) vp.RequestRedraw(); }),
            CheckItem("Show Entities (2D)", Editor.ViewOptions.ShowEntities2D,
                      v => { Editor.ViewOptions.ShowEntities2D = v; foreach (var vp in AllViewports()) vp.RequestRedraw(); }),
            CheckItem("Actor wireframes in 2D", Editor.EditorSettings.Actor2DWireframe,
                      v => { Editor.EditorSettings.Actor2DWireframe = v; foreach (var vp in AllViewports()) vp.RequestRedraw(); }),
            CheckItem("Show Logic Connections", Editor.ViewOptions.ShowLogicConnections,
                      v => { Editor.ViewOptions.ShowLogicConnections = v; foreach (var vp in AllViewports()) vp.RequestRedraw(); }),
            new ToolStripSeparator(),
            CheckItem("Snap to Grid", Editor.ViewOptions.SnapToGrid,
                      v => { Editor.ViewOptions.SnapToGrid = v; UpdateStatus(); }),
            new ToolStripSeparator(),
            Item("Grid & view settings…", Keys.None, (_, _) => OpenOptionsDialog(OptionsTab.General)));

        var build = Menu("Build",
            Item("Build & Export",      Keys.F9,  (_, _) => OpenExportDialog()),
            Item("Inject into ROM…",    Keys.F11, (_, _) => OpenInjectDialog()),
            Item("Playtest in SoH/2Ship…", Keys.F12, (_, _) => OpenPlaytestDialog()),
            Item("Playtest in Project64 (N64)…", Keys.None, (_, _) => OpenN64PlaytestDialog()),
            CheckItem("Boot straight into level (N64)", Editor.EditorSettings.PlaytestN64AutoBoot,
                      v => Editor.EditorSettings.PlaytestN64AutoBoot = v),
            new ToolStripSeparator(),
            Item("Export as .o2r (vanilla SoH)…", Keys.None, (_, _) => OpenExportO2RDialog()),
            Item("Export Scene Binary", Keys.None, (_, _) => OpenExportDialog()),
            Item("Generate Minimap…",   Keys.None, (_, _) => GenerateMinimap()));

        var options = Menu("Options",
            Item("Editor Options…", Keys.None, (_, _) => OpenOptionsDialog()),
            Item("Playtest engines (SoH / 2Ship / PJ64)…", Keys.None, (_, _) => OpenOptionsDialog(OptionsTab.Playtest)),
            new ToolStripSeparator(),
            CheckItem("Associate .mhproj files with this app", Editor.EditorSettings.AssociateProjectFiles,
                      v =>
                      {
                          Editor.EditorSettings.AssociateProjectFiles = v;
                          if (v) Editor.FileAssociation.EnsureRegistered();
                          else   Editor.FileAssociation.Unregister();
                      }),
            new ToolStripSeparator(),
            CheckItem("Allow overwriting original ROMs (unsafe)", Editor.RomSafety.AllowOverwriteOriginals,
                      v =>
                      {
                          if (v && MessageBox.Show(
                                  "This lets the editor overwrite original game ROMs. Originals are normally " +
                                  "kept read-only and the editor always produces a NEW ROM.\n\nEnable anyway?",
                                  "Safety Override", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                          { Editor.RomSafety.AllowOverwriteOriginals = false; return; }
                          Editor.RomSafety.AllowOverwriteOriginals = v;
                      }));

        var tools = Menu("Tools",
            Item("Transform…", Keys.Control | Keys.M, (_, _) => TransformSelection()),
            Menu("Block Shape (Brush tool)",
                Item("Block",    Keys.None, (_, _) => { _brushTool.Shape = BrushTool.BlockShape.Block;    SetActiveTool(_brushTool); }),
                Item("Wedge",    Keys.None, (_, _) => { _brushTool.Shape = BrushTool.BlockShape.Wedge;    SetActiveTool(_brushTool); }),
                Item("Cylinder", Keys.None, (_, _) => { _brushTool.Shape = BrushTool.BlockShape.Cylinder; SetActiveTool(_brushTool); }),
                Item("Spike",    Keys.None, (_, _) => { _brushTool.Shape = BrushTool.BlockShape.Spike;    SetActiveTool(_brushTool); }),
                Item("Sphere",   Keys.None, (_, _) => { _brushTool.Shape = BrushTool.BlockShape.Sphere;   SetActiveTool(_brushTool); })),
            Menu("Align",
                Item("Left",   Keys.None, (_, _) => AlignSelection(0)),
                Item("Right",  Keys.None, (_, _) => AlignSelection(1)),
                Item("Top",    Keys.None, (_, _) => AlignSelection(2)),
                Item("Bottom", Keys.None, (_, _) => AlignSelection(3))),
            Item("Snap Selected to Grid", Keys.Control | Keys.B, (_, _) => SnapSelectionToGrid()),
            new ToolStripSeparator(),
            Item("Replace Textures…", Keys.None, (_, _) => OpenReplaceTextures()),
            Item("Remove Duplicate Textures", Keys.None, (_, _) => RunTextureDedupe(announce: true)),
            CheckItem("Texture Lock (move texture with brush)", Editor.EditorSettings.TextureLock,
                      v => { Editor.EditorSettings.TextureLock = v; Editor.Solid.TextureLock = v; }),
            CheckItem("Simplified actor properties (Show Advanced Options)", Editor.EditorSettings.SimplifiedActorProperties,
                      v => { Editor.EditorSettings.SimplifiedActorProperties = v; _propertiesPanel.ForceRefresh(); }));

        var visgroups = new ToolStripMenuItem("Visgroups") { ForeColor = Color.FromArgb(220, 220, 220) };
        visgroups.DropDownOpening += (_, _) => RebuildVisgroupsMenu(visgroups);
        RebuildVisgroupsMenu(visgroups);

        var mechanisms = new ToolStripMenuItem("Dungeon Mechanisms") { ForeColor = Color.FromArgb(220, 220, 220) };
        mechanisms.DropDownOpening += (_, _) => RebuildMechanismsMenu(mechanisms);
        RebuildMechanismsMenu(mechanisms);

        var map = Menu("Map",
            Item("Entity Report…",  Keys.None, (_, _) => OpenEntityReport()),
            Item("Find Entities…",  Keys.Control | Keys.Shift | Keys.F, (_, _) => OpenFindEntities()),
            new ToolStripSeparator(),
            mechanisms,
            visgroups,
            new ToolStripSeparator(),
            Item("Flag Connections (logic)…", Keys.None, (_, _) => OpenFlagConnections()),
            Item("Export Dialogue Data (.c)…", Keys.None, (_, _) => ExportDialogueData()),
            Item("Check for Problems…", Keys.Alt | Keys.P, (_, _) => OpenSceneCheck()));

        var help = Menu("Help",
            Item("About Megaton Hammer", Keys.None, (_, _) =>
                MessageBox.Show(
                    "Megaton Hammer — Zelda 64 Level Editor\n" +
                    "Brush editing · texture painting · ROM texture extraction · scene export & ROM injection",
                    "About", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        strip.Items.AddRange([file, edit, view, tools, map, build, options, help]);
        return strip;
    }

    // Rebuilds the dynamic Visgroups submenu: create-from-selection, show-all, and a checkable toggle
    // per visgroup (Hammer's visgroup show/hide).
    private void RebuildVisgroupsMenu(ToolStripMenuItem root)
    {
        _document.RefreshVisGroups();
        root.DropDownItems.Clear();
        Color fg = Color.FromArgb(220, 220, 220);

        var create = new ToolStripMenuItem("New Visgroup from Selection") { ForeColor = fg };
        create.Click += (_, _) => { _document.CreateVisGroupFromSelection($"Visgroup {_document.VisGroups.Count + 1}"); RedrawAll(); };
        root.DropDownItems.Add(create);

        var showAll = new ToolStripMenuItem("Show All") { ForeColor = fg };
        showAll.Click += (_, _) => { _document.ShowAllVisGroups(); RedrawAll(); };
        root.DropDownItems.Add(showAll);

        if (_document.VisGroups.Count > 0) root.DropDownItems.Add(new ToolStripSeparator());
        foreach (var vg in _document.VisGroups)
        {
            int id = vg.Id;
            var it = new ToolStripMenuItem(vg.Name) { ForeColor = fg, CheckOnClick = true, Checked = vg.Visible };
            it.Click += (_, _) => { _document.ToggleVisGroup(id); RedrawAll(); };
            root.DropDownItems.Add(it);
        }
    }

    // Rebuilds the "Dungeon Mechanisms" submenu for the current game (OoT/MM). Each item drops a pre-wired
    // group of real vanilla actors (a puzzle template) near the player spawn; see DungeonMechanismPresets.
    private void RebuildMechanismsMenu(ToolStripMenuItem root)
    {
        root.DropDownItems.Clear();
        Color fg = Color.FromArgb(220, 220, 220);
        foreach (var preset in Editor.DungeonMechanismPresets.For(_document.IsMM))
        {
            var p = preset;
            var it = new ToolStripMenuItem(p.Name) { ForeColor = fg, ToolTipText = p.Description };
            it.Click += (_, _) => InsertMechanism(p);
            root.DropDownItems.Add(it);
        }
        if (root.DropDownItems.Count == 0)
            root.DropDownItems.Add(new ToolStripMenuItem("(none for this game yet)") { Enabled = false, ForeColor = fg });
    }

    // Drops a mechanism preset near the current player spawn, selects the placed group, and refreshes.
    private void InsertMechanism(Editor.MechanismPreset preset)
    {
        var s = _document.Scene.Settings;
        var at = new OpenTK.Mathematics.Vector3(s.SpawnPos.X, s.SpawnPos.Y, s.SpawnPos.Z)
                 + new OpenTK.Mathematics.Vector3(0, 0, -200);   // just in front of the spawn, easy to find
        var placed = Editor.DungeonMechanismPresets.Insert(_document, preset, at);

        _document.ClearSelection();
        foreach (var a in placed) a.IsSelected = true;
        _document.NotifyChanged();     // refreshes the object tree (HierarchyPanel listens to Changed)
        _propertiesPanel.ForceRefresh();
        RedrawAll();
        if (_statusLabel != null)
            _statusLabel.Text = $"Inserted “{preset.Name}” ({placed.Count} actors) near the player spawn — drag it into place.";
    }

    // Writes mh_dialogue_data.c (the behaviour table the portable ovl_En_MhTalk actor reads). Presentation
    // is already in the message bytes; this is only for dialogue that gives items / charges / branches / etc.
    private void ExportDialogueData()
    {
        int rows = _document.Scene.Messages.Count(Export.MhDialogueDataWriter.NeedsEntry);
        if (rows == 0)
        {
            MessageBox.Show(this, "No dialogue has behaviour to export yet (add a prompt, an outcome, a sound, or a fulfilled-state fallback in the Dialogue Editor).\n\nPlain text/colour/timing already lives in the message data and needs no table.",
                "Export Dialogue Data", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var sfd = new SaveFileDialog { Title = "Export dialogue behaviour table",
            Filter = "C source (*.c)|*.c", FileName = "mh_dialogue_data.c" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            System.IO.File.WriteAllText(sfd.FileName, Export.MhDialogueDataWriter.Write(_document.Scene.Messages));
            if (_statusLabel != null) _statusLabel.Text = $"Exported {rows} dialogue row(s) → {System.IO.Path.GetFileName(sfd.FileName)}  (drop it in with ovl_En_MhTalk — see portable/README.md)";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void NewMap()
    {
        if (!PromptSaveIfDirty("starting a new project")) return;
        _roomsForm?.Close(); _roomsForm = null;
        _document.Reset();
        _currentPath = null;
        _dirty = false;
        AfterDocumentLoad();
    }

    // Closes the current workspace and re-prompts the startup splash so the user can pick a (possibly
    // different) game target. Picking one relaunches the editor fresh in that mode — identical to a cold
    // start (full game-specific re-init: textures, actor DB, scene defaults). Cancelling the splash leaves
    // the current project and editor untouched.
    private void CloseProject()
    {
        if (!PromptSaveIfDirty("closing the project")) return;

        using var dlg = new GameSelectDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;   // cancel → keep the current session

        PendingGameChange = dlg.SelectedConfig;   // Program's run-loop relaunches with this config
        Close();
    }

    private void OpenProject()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = $"Megaton Hammer project (*{ProjectSerializer.Extension})|*{ProjectSerializer.Extension}|All files (*.*)|*.*",
            Title  = "Open Project",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            ProjectSerializer.Load(_document, dlg.FileName);
            _currentPath = dlg.FileName;
            RememberRecent(dlg.FileName);
            AfterDocumentLoad();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open project:\n{ex.Message}", "Open Project",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Fills the File ▸ Open Recent submenu from the persisted recent-projects list. Called once at
    // build time and again whenever the File menu opens, so it always reflects the latest state.
    private void PopulateRecentMenu()
    {
        if (_openRecentMenu == null) return;
        _openRecentMenu.DropDownItems.Clear();

        var recents = Editor.EditorSettings.RecentFiles;
        if (recents.Count == 0)
        {
            _openRecentMenu.DropDownItems.Add(new ToolStripMenuItem("(No recent projects)") { Enabled = false });
            return;
        }

        int n = 1;
        foreach (var path in recents)
        {
            bool exists = File.Exists(path);
            // "&1 name" gives Alt-number access keys for the first nine, as Hammer/most editors do.
            // Colour-code by target game (Options ▸ recent-file colours): OoT/SoH = blue, MM/2Ship = purple.
            Color fg = Color.FromArgb(220, 220, 220);
            if (!exists) fg = Color.Gray;
            else if (Editor.EditorSettings.ColorCodeRecentByGame)
                fg = RecentProjectGame(path) switch
                {
                    "mm"  => Color.FromArgb(185, 135, 235),   // purple (legible on dark, not glaring)
                    "oot" => Color.FromArgb(105, 165, 230),   // blue
                    _     => fg,
                };
            var it = new ToolStripMenuItem($"&{n}  {Path.GetFileName(path)}")
            {
                ToolTipText = path,
                ForeColor   = fg,
            };
            string captured = path;
            it.Click += (_, _) => OpenRecentProject(captured);
            _openRecentMenu.DropDownItems.Add(it);
            if (++n > 9) { /* keep access keys single-digit; remaining still listed below */ }
        }

        _openRecentMenu.DropDownItems.Add(new ToolStripSeparator());
        var clear = new ToolStripMenuItem("Clear Recent") { ForeColor = Color.FromArgb(220, 220, 220) };
        clear.Click += (_, _) =>
        {
            Editor.EditorSettings.ClearRecentFiles();
            WindowsJumpList.Update(Editor.EditorSettings.RecentFiles);
        };
        _openRecentMenu.DropDownItems.Add(clear);
    }

    // Opens a project chosen from the recent list (menu or jump list). Offers to prune the entry if
    // the file has since been deleted or moved.
    private void OpenRecentProject(string path)
    {
        if (!File.Exists(path))
        {
            if (MessageBox.Show($"\"{path}\" could not be found.\n\nRemove it from the recent list?",
                    "Open Recent", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                Editor.EditorSettings.RemoveRecentFile(path);
                WindowsJumpList.Update(Editor.EditorSettings.RecentFiles);
            }
            return;
        }
        if (!PromptSaveIfDirty("opening another project")) return;

        try
        {
            ProjectSerializer.Load(_document, path);
            _currentPath = path;
            RememberRecent(path);
            AfterDocumentLoad();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open project:\n{ex.Message}", "Open Recent",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Records a successfully opened/saved project in both the persisted recent list and the Windows
    // taskbar jump list.
    private void RememberRecent(string path)
    {
        Editor.EditorSettings.AddRecentFile(path);
        WindowsJumpList.Update(Editor.EditorSettings.RecentFiles);
    }

    // Reads a project's target game ("oot"/"mm") from just the first bytes of the file (the "game" field is
    // declared early in the JSON), for the recent-menu colour coding. Null if absent (older projects — they
    // get tagged the next time they're saved) or unreadable.
    private static string? RecentProjectGame(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[512];
            int read = fs.Read(buf, 0, buf.Length);
            string head = System.Text.Encoding.UTF8.GetString(buf, 0, read);
            var m = System.Text.RegularExpressions.Regex.Match(head, "\"[Gg]ame\"\\s*:\\s*\"(oot|mm)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private void SaveProject()
    {
        if (_currentPath == null) { SaveProjectAs(); return; }
        TrySave(_currentPath);
    }

    private void SaveProjectAs()
    {
        using var dlg = new SaveFileDialog
        {
            Filter   = $"Megaton Hammer project (*{ProjectSerializer.Extension})|*{ProjectSerializer.Extension}",
            Title    = "Save Project As",
            FileName = _document.Scene.Name + ProjectSerializer.Extension,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (TrySave(dlg.FileName)) _currentPath = dlg.FileName;
    }

    private bool TrySave(string path)
    {
        // Visible save feedback (#8): flash the wait cursor + a status line so a fast save is acknowledged
        // (the user couldn't tell Ctrl-S did anything). UseWaitCursor shows the OS busy cursor app-wide.
        UseWaitCursor = true;
        try
        {
            ProjectSerializer.Save(_document, path, _config.IsOoTBased ? "oot" : "mm");
            _dirty = false;
            RememberRecent(path);
            UpdateTitle();
            if (_statusLabel != null) _statusLabel.Text = $"Saved {Path.GetFileName(path)} at {DateTime.Now:HH:mm:ss}";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save project:\n{ex.Message}", "Save Project",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            UseWaitCursor = false;
            // Force the cursor to actually repaint so the wait cursor is briefly visible even on a fast save.
            Cursor.Current = Cursors.WaitCursor; Application.DoEvents(); Cursor.Current = Cursors.Default;
        }
    }

    private void DoUndo()
    {
        if (!_document.CanUndo) return;
        _document.Undo();
        _propertiesPanel.ForceRefresh();
        foreach (var vp in AllViewports()) vp.RequestRedraw();
    }

    private void DoRedo()
    {
        if (!_document.CanRedo) return;
        _document.Redo();
        _propertiesPanel.ForceRefresh();
        foreach (var vp in AllViewports()) vp.RequestRedraw();
    }

    // Refresh all UI after the document is replaced (load/new).
    private void AfterDocumentLoad()
    {
        _document.Ghost = null;     // a trace ghost is transient/session-scoped — never carries into another project
        RemapCrossGameTextures();   // opposite-game project → swap rom_/xrom_ so textures resolve seamlessly
        // Self-heal chests that share a treasure flag (opening one would open all — the "chest already open on
        // playtest" bug). Projects made before chest auto-flagging, or with pasted/cloned chests, collide.
        int fixedChests = _document.NormalizeChestFlags();
        if (fixedChests > 0) DiagnosticLog.Step($"normalized {fixedChests} colliding chest treasure flag(s)");
        _document.NotifyChanged();
        _propertiesPanel.ForceRefresh();
        _texturePanel.Refresh(recount: true);
        foreach (var vp in AllViewports()) vp.RequestRedraw();
        _dirty = false;
        UpdateTitle();
        UpdateStatus();
    }

    private void UpdateTitle()
    {
        string file = _currentPath != null ? Path.GetFileName(_currentPath) : "untitled";
        Text = $"Megaton Hammer — {file} — {_config.DisplayName}";
        UpdateDiscordPresence();
    }

    // Discord Rich Presence: "Megaton Hammer" (the app name) / "Editing: <map>" / "For <game>", with an icon
    // per game (oot / mm / mh-generic). No SoH-vs-OoT or 2Ship-vs-MM distinction; a custom romhack base is
    // "Zelda 64" (mh icon). Best-effort — does nothing if disabled, no App ID, or Discord isn't running.
    private (string game, string image) DiscordGameInfo() => _config.Mode switch
    {
        Editor.GameMode.OcarinaOfTime or Editor.GameMode.ShipOfHarkinian => ("Ocarina of Time", "oot"),
        Editor.GameMode.MajorasMask or Editor.GameMode.TwoShip2Harkinian => ("Majora's Mask", "mm"),
        _ => ("Zelda 64", "mh"),   // CustomOoT / CustomMM
    };

    private void UpdateDiscordPresence()
    {
        if (!Editor.EditorSettings.DiscordRpcEnabled) { Editor.DiscordRpc.Stop(); return; }
        Editor.DiscordRpc.Start(Editor.EditorSettings.DiscordAppId);
        var (game, gameImage) = DiscordGameInfo();
        string map = _currentPath != null ? Path.GetFileNameWithoutExtension(_currentPath) : "";
        string? details = Editor.EditorSettings.DiscordShowMap
            ? (map.Length > 0 ? $"Editing: {map}" : "Editing a new level")
            : "Editing a level";
        string? state = Editor.EditorSettings.DiscordShowGame ? $"For {game}" : null;
        // Icon: the game being edited; mh (generic) when nothing is open or it's a Zelda-64 custom base.
        string image = (map.Length > 0) ? gameImage : "mh";
        Editor.DiscordRpc.SetPresence(details, state, image);
    }

    // Loading a project made for the OTHER game: its native ROM textures ("rom_FILE_OFF") are THIS session's
    // cross-game textures ("xrom_FILE_OFF"), and any cross-game textures it used are this session's native
    // ones — so the two prefixes just swap. Do that automatically (using the project's recorded "game") so
    // the level's textures resolve without hand-replacing every face, and make sure the opposite game's
    // cross-game textures are actually loaded. Untagged (old) projects are left alone.
    private void RemapCrossGameTextures()
    {
        if (_currentPath == null) return;
        string? projGame = RecentProjectGame(_currentPath);
        if (projGame == null) return;                       // no "game" tag → can't tell; leave as-is
        if ((projGame == "oot") == _config.IsOoTBased) return;   // same game → native names already resolve

        int swapped = 0;
        foreach (var scene in _document.Scenes)
            foreach (var room in scene.Rooms)
                foreach (var solid in room.Geometry)
                    foreach (var face in solid.Faces)
                    {
                        string? nm = face.TextureName;
                        if (nm == null) continue;
                        if (nm.StartsWith("xrom_", StringComparison.Ordinal)) { face.TextureName = "rom_" + nm[5..]; swapped++; }
                        else if (nm.StartsWith("rom_", StringComparison.Ordinal)) { face.TextureName = "xrom_" + nm[4..]; swapped++; }
                    }

        // Ensure the opposite game's textures load as cross-game so the swapped "xrom_" names resolve.
        if (!Editor.EditorSettings.EnableCrossGameTextures && Editor.EditorSettings.HasOppositeSource(_config.IsOoTBased))
        {
            Editor.EditorSettings.EnableCrossGameTextures = true;
            StartCrossGameTextureLoad();
        }
        if (swapped > 0 && _statusLabel != null)
            _statusLabel.Text = $"Opposite-game project: remapped {swapped} texture reference(s) to this session's library.";
    }

    private void OpenExportDialog()
    {
        using var dlg = new ExportDialog(_document.Scene);
        dlg.ShowDialog(this);
    }

    // Exports the level's brush geometry to an interchange format (OBJ for Blender, VMF for Hammer).
    private void ExportFormat(string fmt)
    {
        string ext = fmt == "vmf" ? "vmf" : "obj";
        string desc = fmt == "vmf" ? "Valve Map (*.vmf)" : "Wavefront OBJ (*.obj)";
        using var sfd = new SaveFileDialog { Filter = $"{desc}|*.{ext}", FileName = $"{_document.Scene.Name}.{ext}" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (fmt == "vmf") Export.VmfIO.Export(_document.Scene, sfd.FileName);
            else              Export.ObjIO.Export(_document.Scene, sfd.FileName);
            MessageBox.Show($"Exported:\n{sfd.FileName}", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show($"Export failed:\n{ex.Message}", "Export", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // Imports an OBJ (Blender / SharpOcarina geometry) as first-class textured, exportable level mesh
    // geometry on the active room — with UVs, .mtl map_Kd textures, and the #nocollision/#nomesh/#door
    // group conventions honoured on export. (Hold Shift to import as a read-only tracing backdrop instead.)
    private void ImportObj()
    {
        using var ofd = new OpenFileDialog { Filter = "Wavefront OBJ (*.obj)|*.obj|All files (*.*)|*.*" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            bool asReference = (Control.ModifierKeys & Keys.Shift) != 0;
            if (asReference)
            {
                var tris = Export.ObjIO.ImportTriangles(ofd.FileName);
                _document.ReferenceMesh = tris.Select(t => new Rom.MeshTri { P0 = t.a, P1 = t.b, P2 = t.c,
                    C0 = OpenTK.Mathematics.Vector3.One, C1 = OpenTK.Mathematics.Vector3.One, C2 = OpenTK.Mathematics.Vector3.One }).ToList();
                foreach (var vp in AllViewports()) vp.RequestRedraw();
                MessageBox.Show($"Imported {tris.Count} triangles as a reference mesh.", "Import OBJ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var mesh = Export.ObjIO.ImportMesh(ofd.FileName);
            var room = _document.Scene.ActiveRoom ?? _document.Scene.Rooms[0];
            room.ObjMesh = mesh;
            foreach (var vp in AllViewports()) vp.RequestRedraw();
            int textured = mesh.Materials.Values.Count(b => b != null);
            int noColl = mesh.Tris.Count(t => t.NoCollision);
            MessageBox.Show(
                $"Imported {mesh.Tris.Count} triangles into room \"{room.Name}\" as exportable level geometry.\n" +
                $"Materials: {mesh.Materials.Count} ({textured} textured)\n" +
                $"#nocollision triangles: {noColl}\n\n" +
                $"This mesh exports to the engine (OTR) as textured display lists + collision. " +
                $"Hold Shift while importing to load as a tracing backdrop instead.",
                "Import OBJ", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show($"Import failed:\n{ex.Message}", "Import OBJ", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void OpenInjectDialog()
    {
        using var dlg = new RomInjectDialog(_document.Scene, _config.RomPath, n => _textureLib.Find(n)?.Image);
        dlg.ShowDialog(this);
    }

    private void ShowFaceEditDialog()
    {
        if (_faceEditDialog == null || _faceEditDialog.IsDisposed)
        {
            _faceEditDialog = new FaceEditDialog(_document, _textureLib,
                () => _textureTool.ActiveTexture,
                () => { foreach (var vp in AllViewports()) vp.RequestRedraw(); },
                name => { _textureTool.ActiveTexture = name; _decalTool.ActiveTexture = name; })
            { Owner = this };
            // Closing the Face Edit sheet exits face-editing mode (Hammer): drop the face selection
            // and redraw so the yellow highlight clears immediately.
            _faceEditDialog.FormClosed += (_, _) => { _document.ClearFaceSelection(); RedrawAll(); };
        }
        // The Face Edit sheet IS the face tool (Hammer): it only ever opens from SetActiveTool when the
        // texture tool is already active (see SetActiveTool), so clicking faces in the 3D view selects
        // them for "Apply (to selected)". Do NOT call SetActiveTool(_textureTool) here — that re-enters
        // SetActiveTool -> ShowFaceEditDialog endlessly and stack-overflows the process.
        _faceEditDialog.Show(this);
        _faceEditDialog.CenterOnOwner();   // open centred on the editor window (re-show won't fire OnShown)
        _faceEditDialog.Refresh2();
    }

    // The Shade tool's modeless spray-properties sheet (colour picker + brush-size + opacity), opened when
    // the Shade tool is selected — mirrors the Face Edit sheet (stays open while you spray in the 3D view).
    private void ShowShadePaintDialog()
    {
        if (_shadePaintDialog == null || _shadePaintDialog.IsDisposed)
            _shadePaintDialog = new ShadePaintDialog(_shadeTool) { Owner = this };
        // First open centres on the editor window; after that it reopens where the user left it — the
        // dialog handles both in OnShown (mirrors how the Face Edit sheet / texture browser place themselves).
        if (!_shadePaintDialog.Visible) _shadePaintDialog.Show(this);
    }

    // Auto-generates a top-down minimap of the level (imported geometry if present, else the
    // editable brushes) and saves it as a PNG (D15).
    private void GenerateMinimap()
    {
        using var sfd = new SaveFileDialog { Title = "Save Minimap", Filter = "PNG image (*.png)|*.png",
            FileName = $"{_document.Imported?.Scene.Name ?? _document.Scene.Name}_minimap.png" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var map = _document.Imported != null
                ? Rom.MinimapGenerator.FromImported(_document.Imported)
                : Rom.MinimapGenerator.FromScene(_document.Scene);
            Rom.MinimapGenerator.Save(map, sfd.FileName);
            MessageBox.Show($"Minimap saved:\n{sfd.FileName}", "Generate Minimap",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Minimap failed:\n{ex.Message}", "Generate Minimap",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Loads an existing scene from the ROM read-only: textured world geometry as a backdrop,
    // plus its actors as editable placements (unknown ids flagged obsolete — D7).
    // ── Ghost reference overlay ─────────────────────────────────────────────
    private ToolStripMenuItem? _ghostUnloadItem, _ghostShowItem, _ghostXrayItem;
    private TrackBar? _ghostOpacityBar;
    private ToolStripControlHost? _ghostOpacityHost;

    // The "Ghost reference (trace)" submenu, next to Import Level: load a whole vanilla level as a
    // translucent trace-over reference (no rooms/actors imported), toggle it, set its opacity, unload it.
    private ToolStripMenuItem BuildGhostMenu()
    {
        _ghostShowItem = new ToolStripMenuItem("Show ghost") { CheckOnClick = true, Checked = Editor.EditorSettings.GhostVisible };
        _ghostShowItem.CheckedChanged += (_, _) =>
        { Editor.EditorSettings.GhostVisible = _ghostShowItem.Checked; foreach (var v in AllViewports()) v.RequestRedraw(); };

        _ghostXrayItem = new ToolStripMenuItem("See through walls (X-ray)") { CheckOnClick = true, Checked = Editor.EditorSettings.GhostXray };
        _ghostXrayItem.ToolTipText = "Draw the ghost without occluding your brushes and actors, so you can see and edit them through its walls.";
        _ghostXrayItem.CheckedChanged += (_, _) =>
        { Editor.EditorSettings.GhostXray = _ghostXrayItem.Checked; foreach (var v in AllViewports()) v.RequestRedraw(); };

        _ghostOpacityBar = new TrackBar
        { Minimum = 2, Maximum = 100, TickFrequency = 25, AutoSize = false, Width = 180, Height = 30,
          Value = (int)Math.Round(Editor.EditorSettings.GhostOpacity * 100) };
        _ghostOpacityBar.ValueChanged += (_, _) =>
        { Editor.EditorSettings.GhostOpacity = _ghostOpacityBar.Value / 100f; foreach (var v in AllViewports()) v.RequestRedraw(); };
        _ghostOpacityHost = new ToolStripControlHost(_ghostOpacityBar) { AutoSize = false, Width = 186, Height = 34 };

        _ghostUnloadItem = Item("Unload ghost", Keys.None, (_, _) => UnloadGhost());

        var opacityHeader = new ToolStripMenuItem("Opacity") { Enabled = false };
        var menu = Menu("Ghost reference (trace)",
            Item("Load ghost from ROM…", Keys.None, (_, _) => LoadGhost()),
            new ToolStripSeparator(),
            _ghostShowItem,
            _ghostXrayItem,
            opacityHeader,
            _ghostOpacityHost,
            new ToolStripSeparator(),
            _ghostUnloadItem);
        // Reflect current state whenever the menu opens (enabled only when a ghost is loaded).
        menu.DropDownOpening += (_, _) =>
        {
            bool has = _document.Ghost != null;
            _ghostUnloadItem.Enabled = _ghostShowItem.Enabled = _ghostXrayItem.Enabled = opacityHeader.Enabled = _ghostOpacityHost.Enabled = has;
            _ghostShowItem.Checked = Editor.EditorSettings.GhostVisible;
            _ghostXrayItem.Checked = Editor.EditorSettings.GhostXray;
            _ghostOpacityBar.Value = (int)Math.Round(Editor.EditorSettings.GhostOpacity * 100);
        };
        return menu;
    }

    // Load a vanilla level from ROM as the transient ghost (backdrop mesh only — no rooms/actors/logic
    // added to the project, never serialized). Mirrors ImportFromRom's ROM/scene selection.
    private void LoadGhost()
    {
        string? romPath = ResolveRomPath();
        if (!IsRomPath(romPath))
        {
            MessageBox.Show("Configure the reference ROM (Options) before loading a ghost.",
                "Ghost reference", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        bool crossAvailable = Editor.EditorSettings.EnableCrossGameTextures
            && IsRomPath(Editor.EditorSettings.OppositeRomPath(_config.IsOoTBased));
        using var dlg = new ImportRomDialog(_config.IsMMBased, crossAvailable, hideScope: true) { Text = "Load Ghost Reference from ROM" };
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SceneId < 0) return;

        string? srcRom = dlg.CrossGame ? Editor.EditorSettings.OppositeRomPath(_config.IsOoTBased) : romPath;
        if (!IsRomPath(srcRom)) return;
        try
        {
            var level = Editor.ImportedLevel.Load(new Rom.RomImage(srcRom!), dlg.SceneId);
            if (level == null)
            {
                MessageBox.Show("That scene could not be loaded (test/debug scenes aren't present in retail ROMs).",
                    "Ghost reference", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _document.Ghost = level;                       // transient — NOT added to the project, NOT saved
            Editor.EditorSettings.GhostVisible = true;
            foreach (var v in AllViewports()) v.RequestRedraw();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load ghost:\n{ex.Message}", "Ghost reference",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UnloadGhost()
    {
        _document.Ghost = null;
        foreach (var v in AllViewports()) v.RequestRedraw();
        UpdateStatus();
    }

    private void ImportFromRom()
    {
        // SoH/2Ship modes point at a build folder, not a ROM — use the matching vanilla reference ROM
        // (the one the build's O2R was extracted from), same as the model/texture sources do.
        string? romPath = ResolveRomPath();
        if (!IsRomPath(romPath))
        {
            string hint = _config.IsVanilla
                ? "Configure an OoT or MM ROM (Options) before importing a level."
                : $"Importing a level needs the vanilla {(_config.IsOoTBased ? "OoT" : "MM")} ROM the build " +
                  "was made from — set it under Options ▸ Editor Options as the cross-game/reference ROM. " +
                  "(SoH/2Ship builds store assets in an O2R, not raw importable scenes.)";
            MessageBox.Show(hint, "Import from ROM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Offer a cross-game import when the other game's ROM is configured and cross-game is enabled.
        bool crossAvailable = Editor.EditorSettings.EnableCrossGameTextures
            && IsRomPath(Editor.EditorSettings.OppositeRomPath(_config.IsOoTBased));

        // If a ghost reference is already loaded, the level's geometry is already on screen, so default
        // to importing just the actors — otherwise the solid backdrop mesh would cover the ghost.
        var defScope = _document.Ghost != null
            ? Forms.ImportScope.ActorsOnly : Forms.ImportScope.Everything;
        using var dlg = new ImportRomDialog(_config.IsMMBased, crossAvailable, defaultScope: defScope);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SceneId < 0) return;

        // Pick the source ROM: the native reference ROM, or the other game's when cross-game was chosen.
        string? srcRom = dlg.CrossGame ? Editor.EditorSettings.OppositeRomPath(_config.IsOoTBased) : romPath;
        if (!IsRomPath(srcRom))
        {
            MessageBox.Show("The selected source ROM is not configured.", "Import from ROM",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var rom = new Rom.RomImage(srcRom!);
        bool mm = rom.Game == Rom.RomGame.MM;
        // Use the actor database for the game being imported (reuse the cached native one when it matches).
        var actorDb = (!mm && _config.IsOoTBased) ? _actorDb : Editor.ActorDatabase.Load(isOoT: !mm);

        bool importGeom   = dlg.Scope != ImportScope.ActorsOnly;
        bool importActors = dlg.Scope != ImportScope.GeometryOnly;

        try
        {
            var level = Editor.ImportedLevel.Load(rom, dlg.SceneId);
            if (level == null)
            {
                MessageBox.Show("That scene could not be imported (test/debug scenes aren't present in retail ROMs).",
                    "Import from ROM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Each import becomes its own scene in the project: reuse the active scene if it's empty,
            // otherwise add a fresh one (so nothing already in the project is lost).
            _roomsForm?.Close(); _roomsForm = null;
            if (_document.ActiveSceneIsEmpty) _document.ResetActiveScene();
            else _document.AddScene();

            // Geometry-only and whole-level imports keep the read-only backdrop mesh; an actors-only
            // import leaves it null (no geometry shows, just the editable entities/logic).
            if (importGeom) _document.Imported = level;
            ApplyImportedEnvironment(level);

            // Reconstruct one editable room per imported room — preserving the multi-room structure
            // and each room's header settings (behaviour/time/echo/skybox-mod) so a recompile keeps
            // them instead of collapsing to a single default room.
            var scene = _document.Scene;
            for (int i = scene.Rooms.Count; i < Math.Max(1, level.Scene.Rooms.Count); i++) scene.AddRoom();
            for (int i = 0; i < level.Scene.Rooms.Count; i++)
            {
                var ir = level.Scene.Rooms[i];
                var zr = scene.Rooms[i];
                zr.Name = $"Room {ir.Index}";
                var rs = zr.Settings;
                rs.BehaviorType = ir.BehaviorType;
                rs.ShowInvisibleActors = ir.ShowInvisibleActors;
                rs.TimeOverride = ir.TimeOverride;
                rs.TimeSpeed = ir.TimeSpeed;
                rs.DisableSkybox = ir.DisableSkybox;
                rs.DisableSunMoon = ir.DisableSunMoon;
                rs.Echo = ir.Echo;
                if (!importActors) continue;
                foreach (var a in ir.Actors)
                {
                    var info = actorDb.Get(a.Id);
                    zr.Actors.Add(new ZActor
                    {
                        Number = a.Id, Variable = a.Params, IdFlags = a.IdFlags,
                        XPos = a.X, YPos = a.Y, ZPos = a.Z,
                        XRot = a.RX, YRot = a.RY, ZRot = a.RZ,
                        IsObsolete = info == null,
                        DisplayName = info?.Name ?? $"Obsolete 0x{a.Id:X4}",
                    });
                }
            }

            // Transition actors (doors / area loading planes) — the walk-into warps between scenes
            // and rooms. Scene-level, so they live in the spawn room, marked with ⇄.
            var room = scene.Rooms[Math.Clamp(_document.Scene.Settings.SpawnRoom, 0, scene.Rooms.Count - 1)];
            scene.ActiveRoom = room;
            if (importActors)
            foreach (var t in level.Scene.Transitions)
            {
                var info = actorDb.Get(t.Id);
                room.Actors.Add(new ZActor
                {
                    Number = t.Id, Variable = t.Params,
                    XPos = t.X, YPos = t.Y, ZPos = t.Z, YRot = t.RY,
                    IsTransition = true,
                    FrontRoom = t.FrontRoom, FrontEffect = t.FrontEffect,
                    BackRoom = t.BackRoom, BackEffect = t.BackEffect,
                    IsObsolete = info == null,
                    DisplayName = "⇄ " + (info?.Name ?? $"Transition 0x{t.Id:X4}"),
                });
            }

            // Collision exit warps → editable trigger brushes (the walk-into loading zones).
            if (importActors)
            foreach (var ex in level.Scene.Exits)
            {
                var mn = ex.Min; var mx = ex.Max;
                // Pad any near-flat axis so the trigger volume is visible/selectable.
                const float pad = 20f;
                if (mx.X - mn.X < pad) { mn.X -= pad; mx.X += pad; }
                if (mx.Y - mn.Y < pad) { mx.Y += 2 * pad; }     // give flat triggers some height
                if (mx.Z - mn.Z < pad) { mn.Z -= pad; mx.Z += pad; }
                var trigger = Editor.Solid.CreateBox(mn, mx);
                trigger.IsTrigger = true;
                trigger.ExitEntrance = ex.EntranceIndex;
                room.Geometry.Add(trigger);
            }

            if (importActors)
            {
                // Scene paths (moving platforms / time-or-action NPC routes) → editable waypoint polylines.
                scene.Paths = level.Scene.Paths.Select((p, i) => new Editor.ZPath(p.Points)
                { Name = $"Path {i}", AdditionalPathIndex = p.AdditionalPathIndex, CustomValue = p.CustomValue }).ToList();

                // Cutscene script (0x17) — retained verbatim so it survives a recompile.
                scene.CutsceneData = level.Scene.CutsceneData;
                scene.CutsceneOrigOff = level.Scene.CutsceneOrigOff;
            }

            // Water boxes → editable water brushes (the top face is the water surface). These are
            // spatial/visual, so they belong with the geometry.
            if (importGeom)
            foreach (var wb in level.Scene.WaterBoxes)
            {
                var water = Editor.Solid.CreateBox(
                    new OpenTK.Mathematics.Vector3(wb.X, wb.Y - 100, wb.Z),
                    new OpenTK.Mathematics.Vector3(wb.X + wb.XLen, wb.Y, wb.Z + wb.ZLen));
                water.IsWater = true; water.WaterRoom = wb.Room;
                room.Geometry.Add(water);
            }

            FrameImported(level);
            _document.NotifyChanged();
            _document.NotifyScenesChanged();   // refresh the scene picker with the imported scene's name
            UpdateStatus();

            // Multi-room dungeons: offer per-room visibility toggles (D14) — only meaningful with geometry.
            if (importGeom && level.Scene.Rooms.Count > 1)
            {
                _roomsForm?.Close();
                _roomsForm = new ImportedRoomsForm(level, () => { foreach (var vp in AllViewports()) vp.RequestRedraw(); })
                { Owner = this };
                _roomsForm.Location = new Point(Right - 220, Top + 80);
                _roomsForm.Show(this);
            }
            string scopeNote = dlg.Scope switch
            {
                ImportScope.GeometryOnly => "Imported geometry, textures and lighting only (no actors). " +
                                            "Geometry is a read-only backdrop.",
                ImportScope.ActorsOnly   => "Imported actors, transitions, exits, paths and cutscene only " +
                                            "(no backdrop geometry). Actors are editable.",
                _                        => "Geometry is a read-only backdrop; actors are editable.",
            };
            string crossNote = dlg.CrossGame ? $" [cross-game: {(mm ? "MM" : "OoT")} scene]" : "";
            MessageBox.Show(
                $"Imported {level.Scene.Name}{crossNote}: " +
                $"{(importGeom ? level.TriangleCount : 0)} triangles, {(importActors ? level.ActorCount : 0)} actors, " +
                $"{level.Scene.Rooms.Count} room(s), {level.Scene.Setups.Count} alternate setup(s).\n\n" +
                scopeNote + " The original ROM is never modified.",
                "Import from ROM", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed:\n{ex.Message}", "Import from ROM",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Copies the imported scene's environment (lighting, fog, skybox) into the editor's
    // scene settings so the backdrop and brushes render under the real scene's lighting (D4).
    private void ApplyImportedEnvironment(Editor.ImportedLevel level)
    {
        var st = _document.Scene.Settings;
        st.SkyboxId = level.Scene.SkyboxId;
        st.DrawConfig = level.Scene.DrawConfig;
        // OoT skybox ids: 1 = normal blue sky, 2 = cloudy; 0/other = enclosed (no sky, e.g. dungeons).
        st.Sky = level.Scene.SkyboxId switch
        {
            1 => Editor.SkyMode.Day,
            2 => Editor.SkyMode.Cloudy,
            _ => Editor.SkyMode.None,
        };

        // Inherit the scene's name, music, and Link's start position so an imported level matches
        // the original's configuration (sky/lighting follow below).
        if (!string.IsNullOrWhiteSpace(level.Scene.Name)) _document.Scene.Name = level.Scene.Name;
        st.MusicSeq = level.Scene.MusicSeq;
        st.MusicCrossGame = false;
        st.NightSfx = level.Scene.NightSfx;
        if (level.Scene.Spawns.Count > 0)
        {
            var sp = level.Scene.Spawns[0];
            st.SpawnPos = new OpenTK.Mathematics.Vector3(sp.X, sp.Y, sp.Z);
            st.SpawnYaw = sp.RY;
        }

        // Keep every lighting environment so a multi-env scene survives a recompile (entry 0 is the
        // primary mirrored into Settings above/below; the rest are preserved verbatim).
        _document.Scene.Environments = [.. level.Scene.Lights];

        if (level.Scene.Lights.Count == 0) return;
        var l = level.Scene.Lights[0];
        st.Ambient   = Editor.RgbColor.From(l.AmbR, l.AmbG, l.AmbB);
        st.Light1Col = Editor.RgbColor.From(l.L1r, l.L1g, l.L1b);
        st.Light2Col = Editor.RgbColor.From(l.L2r, l.L2g, l.L2b);
        st.Light1DirX = l.L1x; st.Light1DirY = l.L1y; st.Light1DirZ = l.L1z;
        st.Light2DirX = l.L2x; st.Light2DirY = l.L2y; st.Light2DirZ = l.L2z;
        st.FogColor  = Editor.RgbColor.From(l.FogR, l.FogG, l.FogB);
        st.FogNear = l.FogNear; st.FogFar = l.FogFar;
    }

    // Frames every viewport on the imported geometry so it sits centred under the camera/crosshair
    // instead of far off-screen. Coordinates are left untouched (the geometry keeps its true ROM
    // world position), so re-exporting an unchanged import stays byte-for-byte faithful — the
    // editor just looks at where the level actually is.
    private void FrameImported(Editor.ImportedLevel level)
    {
        OpenTK.Mathematics.Vector3 mn = new(1e9f), mx = new(-1e9f);
        foreach (var mesh in level.RoomMeshes)
            foreach (var t in mesh)
                foreach (var p in new[] { t.P0, t.P1, t.P2 })
                { mn = OpenTK.Mathematics.Vector3.ComponentMin(mn, p); mx = OpenTK.Mathematics.Vector3.ComponentMax(mx, p); }
        if (mn.X > mx.X) return;   // no geometry to frame

        var center = (mn + mx) * 0.5f;
        float radius = (mx - mn).Length * 0.5f;

        // 3D: back off proportional to the level's size.
        var cam3 = _vp3D.Viewport.ActiveCamera3D;
        if (cam3 != null)
        {
            cam3.Position = center + new OpenTK.Mathematics.Vector3(0, radius * 0.4f, radius * 1.2f + 200f);
            cam3.Yaw = -90f; cam3.Pitch = -20f;
        }

        // 2D: pan each ortho view to the level centre, and give ALL THREE panes one SHARED zoom so a
        // square reads as a perfect cube across the Top/Front/Side views (a per-pane fit would scale
        // each by its own width and desync them). Fit the level into ~80% of the smallest pane so it
        // stays fully visible everywhere.
        float extent = MathF.Max(mx.X - mn.X, MathF.Max(mx.Y - mn.Y, mx.Z - mn.Z));
        int minDim = int.MaxValue;
        foreach (var panel in new[] { _vpTop, _vpFront, _vpSide })
            if (panel.Viewport.ActiveCamera2D != null)
                minDim = Math.Min(minDim, Math.Max(1, Math.Min(panel.Viewport.Width, panel.Viewport.Height)));
        float sharedZoom = (extent > 1f && minDim != int.MaxValue)
            ? OpenTK.Mathematics.MathHelper.Clamp(extent / (minDim * 0.8f), 0.05f, 100f)
            : 0.25f;
        foreach (var panel in new[] { _vpTop, _vpFront, _vpSide })
        {
            var cam = panel.Viewport.ActiveCamera2D;
            if (cam == null) continue;
            (cam.PanX, cam.PanY) = cam.Axis switch
            {
                Rendering.ViewAxis.Top   => (center.X, -center.Z),
                Rendering.ViewAxis.Front => (center.X,  center.Y),
                Rendering.ViewAxis.Side  => (center.Z,  center.Y),
                _                        => (cam.PanX, cam.PanY),
            };
            cam.Zoom = sharedZoom;
        }
        RedrawAll();
    }

    // Exports a textured + shaded render of the whole level (isometric and/or top-down, with or
    // without actors) by rendering the live 3D scene off-screen at a high resolution from a framed
    // overview camera. Re-uses the editor's real GL pipeline so the output matches the 3D view.
    private void ExportLevelRender()
    {
        // World-space bounds of everything: brushwork, imported backdrop geometry, and actors.
        OpenTK.Mathematics.Vector3 mn = new(1e9f), mx = new(-1e9f);
        void Inc(OpenTK.Mathematics.Vector3 p)
        { mn = OpenTK.Mathematics.Vector3.ComponentMin(mn, p); mx = OpenTK.Mathematics.Vector3.ComponentMax(mx, p); }

        foreach (var s in _document.Solids) { var (a, b) = s.GetAABB(); Inc(a); Inc(b); }
        if (_document.Imported != null)
            foreach (var mesh in _document.Imported.RoomMeshes)
                foreach (var t in mesh) { Inc(t.P0); Inc(t.P1); Inc(t.P2); }
        foreach (var act in _document.AllActors)
            Inc(new OpenTK.Mathematics.Vector3(act.XPos, act.YPos, act.ZPos));

        if (mn.X > mx.X)
        {
            MessageBox.Show("There's nothing in the level to render yet.", "Export Level Render",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new LevelRenderDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var center = (mn + mx) * 0.5f;
        float radius = MathF.Max(1f, (mx - mn).Length * 0.5f);

        string stem = new string((_document.Scene.Name is { Length: > 0 } nm ? nm : "level")
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        using var save = new SaveFileDialog
        {
            Title = "Export Level Render",
            Filter = "PNG image (*.png)|*.png",
            FileName = stem + "_render.png",
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;

        string dir = Path.GetDirectoryName(save.FileName) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(save.FileName);
        var bg = Color.FromArgb(20, 20, 24);
        int res = dlg.Resolution;
        var saved = new List<string>();

        void Render(bool topDown, string suffix)
        {
            var cam = topDown
                ? CamFor(center, radius, yaw: -90f, pitch: -89f, fov: 38f)
                : CamFor(center, radius, yaw: -45f, pitch: -32f, fov: 34f);
            using var bmp = _vp3D.Viewport.RenderToImage(cam, res, res, dlg.IncludeActors, bg);
            if (bmp == null) return;
            string path = Path.Combine(dir, baseName + suffix + ".png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            saved.Add(path);
        }

        try
        {
            switch (dlg.View)
            {
                case RenderView.Isometric: Render(topDown: false, ""); break;
                case RenderView.TopDown:   Render(topDown: true, ""); break;
                case RenderView.Both:
                    Render(topDown: false, "_iso");
                    Render(topDown: true, "_top");
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Render failed:\n{ex.Message}", "Export Level Render",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        RedrawAll();   // the offscreen pass shared the GL context — repaint the live viewports

        MessageBox.Show(saved.Count == 0
                ? "The render could not be produced (the 3D view may not be initialised yet)."
                : "Saved:\n" + string.Join("\n", saved),
            "Export Level Render", MessageBoxButtons.OK,
            saved.Count == 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    // Builds a perspective camera framed on a bounding sphere (center, radius) at the given angles.
    private static Rendering.Camera3D CamFor(OpenTK.Mathematics.Vector3 center, float radius,
                                             float yaw, float pitch, float fov)
    {
        float yawR = OpenTK.Mathematics.MathHelper.DegreesToRadians(yaw);
        float pitchR = OpenTK.Mathematics.MathHelper.DegreesToRadians(pitch);
        var front = OpenTK.Mathematics.Vector3.Normalize(new OpenTK.Mathematics.Vector3(
            MathF.Cos(pitchR) * MathF.Cos(yawR), MathF.Sin(pitchR), MathF.Cos(pitchR) * MathF.Sin(yawR)));
        float dist = radius / MathF.Tan(OpenTK.Mathematics.MathHelper.DegreesToRadians(fov * 0.5f)) * 1.15f
                     + radius * 0.2f;
        // Tight clip planes around the level → depth precision that avoids coplanar z-fighting speckle.
        return new Rendering.Camera3D
        {
            Fov = fov, Yaw = yaw, Pitch = pitch, Position = center - front * dist,
            Near = MathF.Max(4f, dist - radius * 1.3f), Far = dist + radius * 1.6f,
        };
    }

    // Build ▸ Export as .o2r: a plain vanilla-SoH level mod (overrides a chosen scene), optionally added to an
    // existing .o2r as a multi-level pack. No fork required — regular SoH loads the scene resources by path.
    private void OpenExportO2RDialog()
    {
        using var dlg = new ExportO2RDialog(_document.Scene, _config.IsMMBased, n => _textureLib.Find(n)?.Image);
        dlg.ShowDialog(this);
    }

    private void OpenPlaytestDialog()
    {
        bool mm = _config.IsMMBased;
        if (!Editor.EditorSettings.IsEngineConfigured(mm))
        {
            string name = mm ? "2Ship (Majora's Mask)" : "Ship of Harkinian (Ocarina of Time)";
            if (MessageBox.Show(this,
                    $"No {name} engine build is configured for play-testing.\n\nConfigure it now?",
                    "Playtest", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                OpenOptionsDialog(OptionsTab.Playtest);
            return;
        }
        using var dlg = new PlaytestDialog(_document.Scene, Editor.EditorSettings.EngineExe(mm), mm,
            texResolver: n => _textureLib.Find(n)?.Image, allScenes: _document.Scenes);
        dlg.ShowDialog(this);
    }

    // N64/Project64 playtest — same config popout as SoH/2Ship (append-vs-overwrite + inventory), but the
    // Launch delegate drives Project64 instead of packing an O2R. SCENE_TEST01 (0x65) is the OoT overwrite
    // target; MM uses its own spare slot / entrance redirect internally (cfg.Append drives it).
    private void OpenN64PlaytestDialog()
    {
        bool mm = _config.IsMMBased;
        using var dlg = new PlaytestDialog(_document.Scene, null, mm,
            texResolver: n => _textureLib.Find(n)?.Image, allScenes: _document.Scenes,
            n64Launch: cfg =>
            {
                // OoT N64: Append targets the disposable SCENE_TEST01 (0x65) — a cutscene-free dev scene that
                // destroys no real content (the OoT analogue of MM's spare slot); Replace overwrites the
                // chosen real scene (warped to via its own entrance). MM ignores this slot entirely
                // (MmInjectScene picks Termina 0x2D / spare 0x0E from cfg.Append).
                int slot = mm ? 0x2D : (cfg.Append ? 0x65 : cfg.TargetSceneId);
                Project64Playtest.Launch(this, _config, _document.Scene, slot, cfg,
                                         texResolver: n => _textureLib.Find(n)?.Image);
            });
        dlg.ShowDialog(this);
    }

    private void OpenOptionsDialog(OptionsTab tab = OptionsTab.General)
    {
        var placement = new List<(ushort, string)> { (EntityTool.EditorDummyLinkId, "Dummy Link (editor-only scale)") };
        placement.AddRange(_actorDb.All.Where(a => a.Id != 0).Select(a => ((ushort)a.Id, a.Name)));
        using var dlg = new OptionsDialog(_config.IsOoTBased, _gridSize, tab, placement);
        dlg.DefaultActorChanged += () => _hierarchyPanel.RefreshActorCombo();
        dlg.DiscordChanged += UpdateDiscordPresence;
        dlg.SourcesChanged += () =>
        {
            // Music list rebuilds itself on the next properties refresh; textures need reloading.
            StartCrossGameTextureLoad();
            EnsureModelSource();   // a newly-set game ROM enables actor models in fresh scenes
            _propertiesPanel?.ForceRefresh();
        };
        dlg.AutoSaveChanged += RestartAutoSaveTimer;
        dlg.GridChanged += g =>
        {
            _gridSize = g;
            foreach (var vp in AllViewports()) { vp.GridSize = g; vp.RequestRedraw(); }
            UpdateGridButton();
            UpdateStatus();
        };
        dlg.ViewChanged += () => { foreach (var vp in AllViewports()) vp.RequestRedraw(); UpdateStatus(); };
        dlg.ShowDialog(this);
    }

    private EntityConfigDialog? _entityDlg;
    private ZActor? _entityDlgActor;   // the actor the open config pop-out is bound to (for selection-follow sync)

    private void OpenEntityConfig(ZActor actor)
    {
        _entityDlgActor = actor;          // set BEFORE re-selecting so SyncOpenEntityConfig doesn't re-fire
        // Preserve a multi-selection: if this actor is already part of one, keep the others selected so the
        // config edits them all as a group (Hammer). Only collapse to a single selection when opening a fresh
        // (unselected) actor.
        if (!actor.IsSelected) { _document.ClearSelection(); actor.IsSelected = true; }
        _document.RecordUndo();           // one undo point for the whole edit session
        // #2: modeless + reopen-in-place — double-clicking another actor while the window is open refreshes
        // it to that actor, and it never blocks interacting with the 2D/3D views.
        Point? loc = _entityDlg is { IsDisposed: false } ? _entityDlg.Location : null;
        _entityDlg?.Close();
        var dlg = new EntityConfigDialog(actor, _actorDb, _config.IsOoTBased, _document);
        dlg.Changed += () => { _propertiesPanel.ForceRefresh(); foreach (var vp in AllViewports()) vp.RequestRedraw(); UpdateStatus(); };
        // Hammer "Mark": clicking a connected actor in the I/O list follows the wire — select + reopen on it.
        dlg.GoToActor += a => OpenEntityConfig(a);
        dlg.FormClosed += (_, _) =>
        {
            if (_entityDlg == dlg) { _entityDlg = null; _entityDlgActor = null; }
            _propertiesPanel.ForceRefresh();
            foreach (var vp in AllViewports()) vp.RequestRedraw();
        };
        if (loc is { } l) { dlg.StartPosition = FormStartPosition.Manual; dlg.Location = l; }
        _entityDlg = dlg;
        dlg.Show(this);
    }

    private BrushPropertiesDialog? _brushDlg;

    // Double-click a brush (or 2D right-click → Properties on a selected brush): open the Brush Properties
    // pop-out — a floating copy of the docked inspector, so the author gets the FULL brush property set (warp
    // + destination, floor property, floor hazard, wall type, material, conveyor, water, raw words), not just
    // the warp fields. Modeless + reopen-in-place, matching the actor config window.
    private void OpenSolidProperties(Editor.Solid solid)
    {
        // Preserve a multi-selection so Brush Properties edits them all as a group (Hammer); only collapse to a
        // single selection when opening a fresh (unselected) brush.
        if (!solid.IsSelected) { _document.ClearSelection(); solid.IsSelected = true; }
        _document.RecordUndo();
        if (_brushDlg is { IsDisposed: false })   // already open → just refocus it on the newly-clicked brush
        {
            _document.NotifyChanged();
            _brushDlg.Activate();
            return;
        }
        var dlg = new BrushPropertiesDialog(_document, _actorDb, _config.IsOoTBased);
        dlg.FormClosed += (_, _) =>
        {
            if (_brushDlg == dlg) _brushDlg = null;
            _propertiesPanel.ForceRefresh();
            foreach (var vp in AllViewports()) vp.RequestRedraw();
        };
        _brushDlg = dlg;
        dlg.Show(this);
    }

    private void ResetCameras2D()
    {
        foreach (var vp in new[] { _vpTop, _vpFront, _vpSide })
        {
            var cam = vp.Viewport.ActiveCamera2D;
            if (cam == null) continue;
            cam.PanX = 0; cam.PanY = 0; cam.Zoom = 0.25f;
        }
    }

    private static ToolStripMenuItem Menu(string text, params ToolStripItem[] items)
    {
        var m = new ToolStripMenuItem(text) { ForeColor = Color.FromArgb(220, 220, 220) };
        m.DropDownItems.AddRange(items);
        return m;
    }

    private static ToolStripMenuItem Item(string text, Keys keys, EventHandler handler)
    {
        var m = new ToolStripMenuItem(text, null, handler);
        bool hasModifier = (keys & (Keys.Control | Keys.Alt | Keys.Shift)) != 0;
        bool isFKey      = keys >= Keys.F1 && keys <= Keys.F24;
        if (keys != Keys.None && (hasModifier || isFKey))
            m.ShortcutKeys = keys;
        return m;
    }

    // A checkable menu item that reflects/toggles a boolean view option.
    private static ToolStripMenuItem CheckItem(string text, bool initial, Action<bool> onToggle)
    {
        var m = new ToolStripMenuItem(text) { CheckOnClick = true, Checked = initial,
                                              ForeColor = Color.FromArgb(220, 220, 220) };
        m.CheckedChanged += (_, _) => onToggle(m.Checked);
        return m;
    }

    // ── Toolbar builder ───────────────────────────────────────────────────

    private ToolStrip BuildToolStrip()
    {
        // Hammer-style vertical tool column docked on the left edge.
        var strip = new ToolStrip
        {
            BackColor = Color.FromArgb(45, 45, 48),
            GripStyle = ToolStripGripStyle.Hidden,
            Dock = DockStyle.Left,
            LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow,
            ImageScalingSize = new Size(IconSize, IconSize),
            Padding = new Padding(2, 4, 2, 4),
            // Fixed width that fully houses a 32px icon button (AutoSize on a vertical strip can
            // under-size and clip the icons' right edge); no overflow arrow.
            AutoSize = false,
            Width = IconSize + 14,   // button (IconSize+4) + margins + padding, with a little safety
            CanOverflow = false,
        };

        _btnSelect  = ToolBtn("Select [Q]", "Select and move brushes/actors  (Q or Shift+S)", true,
            (_, _) => SetActiveTool(_selectTool), "select");
        _btnMagnify = ToolBtn("Magnify [Z]", "Zoom 2D views — click in / Shift-click out / drag up-down  (Shift+Z)", false,
            (_, _) => SetActiveTool(_magnifyTool), "magnify");
        _btnCamera  = ToolBtn("Camera [C]", "Place the 3D camera from a 2D view — click an eye point, drag to aim  (Shift+C)", false,
            (_, _) => SetActiveTool(_cameraTool), "camera");
        _btnEntity  = ToolBtn("Entity [E]", "Place actors in 2D views or on surfaces in 3D  (E or Shift+E)", false,
            (_, _) => SetActiveTool(_entityTool), "entity");
        _btnBrush   = ToolBtn("Block [B]", "Draw box brushes in 2D views  (B or Shift+B)", false,
            (_, _) => SetActiveTool(_brushTool), "brush");
        _btnTexture = ToolBtn("Texture [T]", "Paint the active texture onto faces in the 3D view  (T or Shift+A; Shift-click clears)", false,
            (_, _) => SetActiveTool(_textureTool), "texture");
        _btnDecal   = ToolBtn("Decal [D]", "Stamp the active texture as a decal/overlay on a surface  (D)", false,
            (_, _) => SetActiveTool(_decalTool), "decal");
        _btnShade   = ToolBtn("Shade [G]", "Spray vertex shade onto faces in the 3D view  (G or Shift+G)", false,
            (_, _) => SetActiveTool(_shadeTool), "shade");
        _btnClip    = ToolBtn("Clip [X]", "Slice selected brushes with a plane  (X cycles keep front/back/both; or Shift+X)", false,
            (_, _) => SetActiveTool(_clipTool), "clip");
        _btnVertex  = ToolBtn("Vertex [V]", "Drag brush vertices in 2D views  (V or Shift+V)", false,
            (_, _) => SetActiveTool(_vertexTool), "vertex");
        _btnPath    = ToolBtn("Path [P]", "Edit scene paths (moving-platform / NPC tracks) in 2D views: click to add a waypoint, drag to move, Delete to remove, Enter for a new path  (P)", false,
            (_, _) => SetActiveTool(_pathTool), "path");

        // Colour/opacity swatch (icon-sized, like the tool buttons): left-click picks the shade
        // colour (rainbow), right-click cycles opacity. The colour/opacity show as a swatch image.
        _btnShadeColor = new ToolStripButton
        {
            DisplayStyle = ToolStripItemDisplayStyle.Image, ImageScaling = ToolStripItemImageScaling.None,
            ToolTipText = "Shade colour (left-click) / opacity (right-click)", AutoToolTip = false,
            Margin = new Padding(1), AutoSize = false, Size = ToolBtnSize,
        };
        UpdateShadeSwatch();
        _btnShadeColor.MouseUp += (_, me) =>
        {
            if (me.Button == MouseButtons.Left)
            {
                using var cd = new ColorDialog { FullOpen = true, Color = ToColor(_shadeTool.PaintColor) };
                if (cd.ShowDialog(this) == DialogResult.OK)
                    _shadeTool.PaintColor = new OpenTK.Mathematics.Vector3(cd.Color.R / 255f, cd.Color.G / 255f, cd.Color.B / 255f);
            }
            else if (me.Button == MouseButtons.Right)
            {
                float[] steps = [0.25f, 0.5f, 0.75f, 1.0f];
                int idx = Array.FindIndex(steps, s => s >= _shadeTool.Opacity - 0.01f);
                _shadeTool.Opacity = steps[(idx + 1) % steps.Length];
            }
            UpdateShadeSwatch();
        };

        // Grid size: an icon-sized button (the value lives in the status bar). Left-click grows,
        // right-click shrinks; both wrap around (so it never sticks at 1024 or 1).
        _btnGrid = new ToolStripButton
        {
            DisplayStyle = ToolStripItemDisplayStyle.Image, ImageScaling = ToolStripItemImageScaling.None,
            ToolTipText = $"Grid size: {_gridSize}  (left-click grows, right-click shrinks; [ ] keys too)", AutoToolTip = false,
            Margin = new Padding(1), AutoSize = false, Size = ToolBtnSize,
        };
        UpdateGridButton();
        _btnGrid.MouseUp += (_, ev) => CycleGrid(ev.Button == MouseButtons.Right ? -1 : +1);

        // Order matches Valve Hammer's tool column for the tools that coincide; Zelda-64-specific
        // tools (vertex shade + grid) follow after a divider.
        ToolStripSeparator Sep() => new();
        strip.Items.AddRange([
            _btnSelect, _btnMagnify, _btnCamera, Sep(),   // pointer / navigation
            _btnEntity, _btnBrush, Sep(),                 // place / block
            _btnTexture, _btnDecal, Sep(),                // texture application / decals
            _btnClip, _btnVertex, _btnPath, Sep(),        // clip / vertex / path edit
            // ── Zelda 64-specific tools below ──
            _btnShade, _btnShadeColor, Sep(),
            _btnGrid,
        ]);
        return strip;
    }

    private static Color ToColor(OpenTK.Mathematics.Vector3 c) =>
        Color.FromArgb((int)(Math.Clamp(c.X, 0, 1) * 255), (int)(Math.Clamp(c.Y, 0, 1) * 255), (int)(Math.Clamp(c.Z, 0, 1) * 255));

    private void UpdateShadeSwatch()
    {
        if (_btnShadeColor == null) return;
        var c = ToColor(_shadeTool.PaintColor);
        int pct = (int)Math.Round(_shadeTool.Opacity * 100);
        // Colour-swatch tile with an opacity bar along the bottom.
        int sz = IconSize;
        var bmp = new Bitmap(sz, sz);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(c);
            using var pen = new Pen(Color.FromArgb(90, 90, 95));
            g.DrawRectangle(pen, 0, 0, sz - 1, sz - 1);
            int barW = (int)Math.Round(_shadeTool.Opacity * (sz - 2));
            g.FillRectangle(Brushes.White, 1, sz - 4, barW, 3);
        }
        _btnShadeColor.Image?.Dispose();
        _btnShadeColor.Image = bmp;
        _btnShadeColor.ToolTipText = $"Shade colour ({pct}%) — left-click colour / right-click opacity";
    }

    // Grid-size button: a small grid glyph with the current size, value also in the status bar.
    private void UpdateGridButton()
    {
        if (_btnGrid == null) return;
        int sz = IconSize;
        var bmp = new Bitmap(sz, sz);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var pen = new Pen(Color.FromArgb(90, 90, 95));
            for (int i = sz / 5; i < sz; i += sz / 5) { g.DrawLine(pen, i, 2, i, sz - 2); g.DrawLine(pen, 2, i, sz - 2, i); }
            string t = _gridSize.ToString();
            using var f = new Font("Segoe UI", t.Length >= 4 ? sz * 0.24f : sz * 0.30f, FontStyle.Bold);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            float bh = sz * 0.42f;
            g.FillRectangle(new SolidBrush(Color.FromArgb(210, 30, 30, 33)), 2, (sz - bh) / 2, sz - 4, bh);
            g.DrawString(t, f, Brushes.Gainsboro, new RectangleF(0, 0, sz, sz), sf);
        }
        _btnGrid.Image?.Dispose();
        _btnGrid.Image = bmp;
        _btnGrid.ToolTipText = $"Grid size: {_gridSize}  ([ shrinks, ] grows)";
    }

    // Hammer-sized tool buttons.
    private const int IconSize = 32;

    private static ToolStripButton ToolBtn(string text, string tip, bool check, EventHandler click, string? icon = null)
    {
        // Vertical Hammer-style column: icon-only buttons (a bundled icon, a drawn pictograph for
        // the tools without one, else a letter glyph) with the label + shortcut in the tooltip.
        var img = IconFor(icon ?? "", text);
        var b = new ToolStripButton(text)
        {
            ToolTipText  = tip,
            AutoToolTip  = false,
            ForeColor    = Color.FromArgb(200, 200, 200),
            Image        = img,
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ImageScaling = ToolStripItemImageScaling.None,
            CheckOnClick = false,
            Checked      = check,
            Margin       = new Padding(1),
            AutoSize     = false,
            Size         = new Size(IconSize + 4, IconSize + 4),
        };
        b.Click += click;
        return b;
    }

    private static readonly Size ToolBtnSize = new(IconSize + 4, IconSize + 4);

    // Resolves a tool's icon at IconSize: a bundled PNG (scaled), else a drawn pictograph, else a letter.
    private static readonly Dictionary<string, Image> _iconFor = [];
    private static Image IconFor(string name, string label)
    {
        string key = name.Length > 0 ? name : "?" + label;
        if (_iconFor.TryGetValue(key, out var cached)) return cached;
        Image img = ScaleTo(LoadIcon(name), IconSize) ?? DrawnIcon(name, IconSize) ?? GlyphIcon(label, IconSize);
        _iconFor[key] = img;
        return img;
    }

    private static Image? ScaleTo(Image? src, int size)
    {
        if (src == null) return null;
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode  = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, size, size);
        return bmp;
    }

    // Recognisable pictographs for the tools that ship without a bundled icon.
    private static Image? DrawnIcon(string name, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.Gainsboro, 2f);
        float s = size / 30f;   // designed on a 30px grid
        float S(float v) => v * s;
        switch (name)
        {
            case "magnify":   // magnifying glass: lens + handle + a "+" for zoom
                g.DrawEllipse(pen, S(5), S(5), S(14), S(14));
                g.DrawLine(pen, S(18), S(18), S(26), S(26));
                g.DrawLine(pen, S(9), S(12), S(15), S(12));
                g.DrawLine(pen, S(12), S(9), S(12), S(15));
                return bmp;
            case "camera":    // camera body + lens + viewfinder bump
                g.DrawRectangle(pen, S(4), S(10), S(22), S(15));
                g.FillRectangle(Brushes.Gainsboro, S(9), S(6), S(7), S(4));
                g.DrawEllipse(pen, S(11), S(13), S(9), S(9));
                return bmp;
            case "decal":     // a surface with a smaller "sticker" placed on it
                g.DrawRectangle(pen, S(4), S(4), S(22), S(22));
                using (var b = new SolidBrush(Color.FromArgb(120, 180, 255)))
                    g.FillRectangle(b, S(11), S(11), S(11), S(11));
                g.DrawRectangle(pen, S(11), S(11), S(11), S(11));
                return bmp;
            case "path":      // waypoint track: a zigzag line with node dots
                using (var op = new Pen(Color.FromArgb(255, 165, 40), 2f))
                    g.DrawLines(op, [new PointF(S(4), S(23)), new PointF(S(12), S(9)), new PointF(S(19), S(20)), new PointF(S(26), S(7))]);
                foreach (var (px, py) in new[] { (4f, 23f), (12f, 9f), (19f, 20f), (26f, 7f) })
                    g.FillEllipse(Brushes.Orange, S(px) - 2.5f, S(py) - 2.5f, 5f, 5f);
                return bmp;
            default:
                return null;
        }
    }

    // Letter-glyph fallback (for any tool with neither a bundled nor drawn icon).
    private static Image GlyphIcon(string label, int size)
    {
        char c = string.IsNullOrEmpty(label) ? '?' : char.ToUpperInvariant(label[0]);
        var bmp = new Bitmap(size, size);
        using var gfx = Graphics.FromImage(bmp);
        gfx.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        using var f = new Font("Segoe UI", size * 0.45f, FontStyle.Bold);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        gfx.DrawString(c.ToString(), f, Brushes.Gainsboro, new RectangleF(0, 0, size, size), sf);
        return bmp;
    }

    // Loads a bundled tool icon (Assets/tool_<name>.png) from embedded resources.
    private static readonly Dictionary<string, Image?> _iconCache = [];
    private static Image? LoadIcon(string name)
    {
        if (name.Length == 0) return null;
        if (_iconCache.TryGetValue(name, out var cached)) return cached;
        Image? img = null;
        try
        {
            using var s = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream($"MegatonHammer.Assets.tool_{name}.png");
            if (s != null) img = Image.FromStream(s);
        }
        catch { }
        _iconCache[name] = img;
        return img;
    }
}
