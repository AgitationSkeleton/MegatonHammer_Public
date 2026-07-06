using System.Text.Json;

namespace MegatonHammer.Editor;

/// <summary>
/// Persistent editor preferences (saved to %AppData%\MegatonHammer\settings.json). Holds the
/// cross-game source paths: a second-game ROM or O2R that lets an OoT project pull in MM music
/// and textures (and vice-versa). Loaded once at startup, saved whenever a value changes.
/// </summary>
public static class EditorSettings
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MegatonHammer");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private sealed class Data
    {
        // Cross-game asset sources for BOTH games, kept independently so each is remembered
        // regardless of which game the current project targets. The editor borrows from whichever
        // is the OPPOSITE of the project's game.
        public string? OotRomPath { get; set; }   // Ocarina of Time .z64/.n64
        public string? OotO2RPath { get; set; }   // …or its extracted .o2r
        public string? MmRomPath  { get; set; }   // Majora's Mask .z64/.n64
        public string? MmO2RPath  { get; set; }   // …or its extracted .o2r
        public bool EnableCrossGameMusic { get; set; }
        public bool EnableCrossGameTextures { get; set; }
        // Auto-save / crash recovery.
        public bool AutoSaveEnabled { get; set; } = true;
        public int AutoSaveIntervalMinutes { get; set; } = 5;
        public int AutoSaveBackupCount { get; set; } = 10;

        // Playtest engine forks + N64 emulator (configured once, remembered across sessions).
        public string? SohExePath { get; set; }       // Ship of Harkinian build (soh.exe) — OoT
        public string? TwoShipExePath { get; set; }    // 2Ship build (2ship.exe) — MM
        public string? Project64Path { get; set; }     // Project64 emulator for vanilla N64 playtests
        public bool EngineSetupPrompted { get; set; }     // we offered to set up the SoH/2Ship fork once
        public bool Project64SetupPrompted { get; set; }  // …and Project64 once
        // #12b: on launch, scan known locations for the base ROMs + engine/emulator forks and fill any
        // unset paths (ROMs validated by MD5). On by default; configurable in Options.
        public bool AutoDetectAssetsOnStartup { get; set; } = true;
        // N64 playtest: boot straight into the injected level (skip logo/title/map-select). Default on.
        public bool PlaytestN64AutoBoot { get; set; } = true;
        // Playtest age (OoT only): remembered across sessions so it isn't re-set every launch. false = child.
        public bool PlaytestAdult { get; set; }
        // Playtest scene mode (append-as-new vs replace-existing), remembered across sessions for ALL engines
        // and both games. null = never chosen (use the per-context default: replace on N64, append elsewhere).
        public bool? PlaytestAppend { get; set; }
        // Actor properties simplification. When true (default), the panel shows a BASIC field set with a
        // "Show Advanced Options" toggle hiding the technical logic (flags etc.). When false, the panel always
        // shows EVERY field — the classic pre-simplification layout. Global, persistent.
        public bool SimplifiedActorProperties { get; set; } = true;
        // The "Show Advanced Options" expand state (persistent), used only when SimplifiedActorProperties is on.
        public bool ShowAdvancedActorOptions { get; set; }
        // N64 debug controls (off by default): the PJ64 fork enables the debug ROM's L+R+Z map-select
        // (OoT: pokes gIsCtrlr2Valid; MM: triggers the ovl_select gamestate) + an L+D-pad no-clip. Never
        // brings the debug inventory (that's the separate inventory mode).
        public bool PlaytestN64DebugControls { get; set; } = false;

        // Default placement entity for the Entity tool, per game (0xFFFF = the editor-only dummy Link).
        public int DefaultActorOoT { get; set; } = 0xFFFF;
        public int DefaultActorMM  { get; set; } = 0xFFFF;

        // Editor view defaults, restored on launch (mirror the live View-menu toggles).
        public int  GridSize { get; set; } = 64;
        public bool SnapToGrid { get; set; } = true;
        public bool ShowSky { get; set; } = true;
        public bool ShowGrid3D { get; set; } = false;
        public bool ShowPrerenderedBackground { get; set; } = false;
        public int LastReplaceSceneOoT { get; set; } = 0x51;   // Playtest → Replace scene, remembered per game
        public int LastReplaceSceneMm { get; set; } = -1;
        public bool DiscordRpcEnabled { get; set; } = true;    // Discord Rich Presence (on by default)
        public bool DiscordShowMap { get; set; } = true;       // show "Editing: <map>"
        public bool DiscordShowGame { get; set; } = true;      // show "For <game>"
        public string DiscordAppId { get; set; } = "1523530435831922882";  // the "Megaton Hammer" Discord app (assets: mh/oot/mm)
        public bool ShowEntities3D { get; set; } = true;
        public bool ShowEntities2D { get; set; } = true;
        public bool TrilinearFilter { get; set; } = true;
        public bool TextureLock { get; set; } = true;
        // Shift-click on a face with the Texture tool: when true (default) it grabs only the connected
        // coplanar faces that share the clicked face's TEXTURE; when false, the whole coplanar surface
        // regardless of texture. Lets you select one painted band of a multi-texture wall to align it.
        public bool ShiftSelectSameTextureOnly { get; set; } = true;
        // Compile/export: drop brush render faces that are fully buried against a neighbouring brush (a
        // face entirely covered by an oppositely-facing solid — like Hammer/vbsp not emitting faces that
        // border solid instead of empty space). Render-only, like nodraw; collision is untouched. OFF by
        // default — it can change exported geometry, so opt-in until it's well-exercised.
        public bool CullUnseenFaces { get; set; } = false;
        // Colour-code the File ▸ Open Recent entries by their target game (OoT/SoH = blue, MM/2Ship = purple)
        // so you can tell a project's game at a glance. On by default.
        public bool ColorCodeRecentByGame { get; set; } = true;
        // #5: tint grayscale (i8/ia16) ROM textures by the prim colour the display list set, so MM
        // foliage / Lost Woods textures show their in-game colour instead of flat grey. Default on.
        public bool TintGrayscaleTextures { get; set; } = true;
        // Robustness extension: when a grayscale texture's display list set NO prim tint (prim white/
        // absent) but DID set an env colour (G_SETENVCOLOR), some vanilla surfaces are modulated by env
        // instead of prim. Applying it un-greys those too. OFF by default — env is also used for fog/
        // 2nd-cycle blends, so this can over-tint; it's a per-user A/B opt-in, never changes the prim path.
        public bool TintGrayscaleWithEnv { get; set; } = false;
        // Per-level texture preview: tint each level's textures in the browser by that level's baked
        // vertex-colour hue (computed once per scene + cached on disk), so a grayscale wall previews with
        // its in-game cast (e.g. Lost Woods' blue-green) instead of flat grayscale. Default on.
        public bool PerLevelTextureTint { get; set; } = true;
        // #10b: show the real in-game item sprite next to each playtest-inventory entry. Default on.
        public bool InventorySprites { get; set; } = true;
        // Playtest logging: how many per-launch logs to keep (oldest pruned on launch). 0 = logging off,
        // -1 = keep all. Default 50. And whether to use one fixed file per engine (overwrite) instead of
        // a timestamped file per launch. Default: timestamped (full history).
        public int PlaytestLogMax { get; set; } = 50;
        public bool PlaytestLogOneFile { get; set; } = false;

        // MM flow-of-time default for new rooms/scenes. MM maps overwhelmingly want a frozen clock while
        // editing (the Moon, Lost Woods intro, most dungeons disable it), so this defaults to false
        // (time frozen). It's remembered so MM devs working on consecutive maps don't have to re-set it:
        // toggling "Freeze time of day" on an MM project writes the choice back here for the next map.
        public bool MmFlowOfTime { get; set; } = false;

        // Ghost reference overlay: opacity (0..1) and whether it's shown. UI preferences only — the ghost
        // LEVEL data itself is transient (never serialized), but these slider/toggle positions persist.
        public float GhostOpacity { get; set; } = 0.40f;
        public bool GhostVisible { get; set; } = true;
        // See through the ghost: draw it without writing depth so your brushes and actors are never
        // occluded by the ghost's walls (an X-ray trace reference). On by default.
        public bool GhostXray { get; set; } = true;

        // Texture browser: hide non-area object/effect texture categories (object_*, g_pn_*, vr_*,
        // *_static, do_action…) so the list shows level/area textures. On by default.
        public bool HideObjectTextures { get; set; } = true;

        // 2D views draw each actor as its model's projected WIREFRAME (true, default) rather than a plain
        // bounding box (false) — so an entity's real footprint is what you align.
        public bool Actor2DWireframe { get; set; } = true;

        // Last path entered on the startup game-select screen, keyed by GameMode name
        // (so each of OoT / MM / SoH / 2Ship / Custom-OoT / Custom-MM is remembered separately).
        public Dictionary<string, string> LastGamePaths { get; set; } = new();

        // The game mode last chosen on the startup/close-project splash, so the splash reopens on it.
        public string? LastGameMode { get; set; }

        // Recently opened/saved projects, most-recent first — File ▸ Open Recent + taskbar jump list.
        public List<string> RecentFiles { get; set; } = new();

        // Keep the per-user .mhproj file association registered (on by default). Cleared if the user
        // turns it off, so it isn't silently re-added on the next launch.
        public bool AssociateProjectFiles { get; set; } = true;

        // Playtest starting inventory, remembered per game across restarts. Current = last-used
        // (keyed "oot"/"mm"); Presets = user-saved named configs (keyed "oot:Name"/"mm:Name").
        public Dictionary<string, string> InventoryCurrent { get; set; } = new();
        public Dictionary<string, string> InventoryPresets { get; set; } = new();

        // Lighting method for exported room geometry: 1 = "Fullbright" (textures show their true colour, no
        // lighting baked — pre-#9 behaviour) — 2 = "Shaded" (bake the scene's environment lighting by face
        // normal; indoor rooms read dark like SoH/2Ship). Default 2 (Shaded).
        public int LightingMethod { get; set; } = 2;
    }

    private static Data _d = new();

    /// <summary>Exported-geometry lighting: 1 = full-bright (old), 2 = baked env lighting (#9).</summary>
    public static int LightingMethod { get => _d.LightingMethod; set { _d.LightingMethod = value; Save(); } }

    // ── Cross-game sources (per game, both remembered) ──────────────────────
    public static string? OotRomPath { get => _d.OotRomPath; set { _d.OotRomPath = value; Save(); } }
    public static string? OotO2RPath { get => _d.OotO2RPath; set { _d.OotO2RPath = value; Save(); } }
    public static string? MmRomPath  { get => _d.MmRomPath;  set { _d.MmRomPath  = value; Save(); } }
    public static string? MmO2RPath  { get => _d.MmO2RPath;  set { _d.MmO2RPath  = value; Save(); } }

    /// <summary>The OPPOSITE game's ROM, relative to a project that is/ isn't OoT.</summary>
    public static string? OppositeRomPath(bool nativeIsOoT) => nativeIsOoT ? _d.MmRomPath : _d.OotRomPath;
    /// <summary>The OPPOSITE game's O2R, relative to a project that is/ isn't OoT.</summary>
    public static string? OppositeO2RPath(bool nativeIsOoT) => nativeIsOoT ? _d.MmO2RPath : _d.OotO2RPath;

    public static bool EnableCrossGameMusic { get => _d.EnableCrossGameMusic; set { _d.EnableCrossGameMusic = value; Save(); } }
    public static bool ColorCodeRecentByGame { get => _d.ColorCodeRecentByGame; set { _d.ColorCodeRecentByGame = value; Save(); } }
    public static bool EnableCrossGameTextures { get => _d.EnableCrossGameTextures; set { _d.EnableCrossGameTextures = value; Save(); } }

    /// <summary>Periodically write a recovery backup of the project (on by default).</summary>
    public static bool AutoSaveEnabled { get => _d.AutoSaveEnabled; set { _d.AutoSaveEnabled = value; Save(); } }
    /// <summary>Minutes between auto-saves (clamped 1–60).</summary>
    public static int AutoSaveIntervalMinutes { get => Math.Clamp(_d.AutoSaveIntervalMinutes, 1, 60); set { _d.AutoSaveIntervalMinutes = value; Save(); } }
    /// <summary>How many rolling auto-save backups to keep (clamped 1–100).</summary>
    public static int AutoSaveBackupCount { get => Math.Clamp(_d.AutoSaveBackupCount, 1, 100); set { _d.AutoSaveBackupCount = value; Save(); } }

    // ── Playtest engines / emulator ─────────────────────────────────────────
    /// <summary>Ship of Harkinian build executable (OoT engine fork) for play-testing.</summary>
    public static string? SohExePath { get => _d.SohExePath; set { _d.SohExePath = value; Save(); } }
    /// <summary>2Ship build executable (MM engine fork) for play-testing.</summary>
    public static string? TwoShipExePath { get => _d.TwoShipExePath; set { _d.TwoShipExePath = value; Save(); } }
    /// <summary>Project64 emulator used for vanilla N64 play-tests.</summary>
    public static string? Project64Path { get => _d.Project64Path; set { _d.Project64Path = value; Save(); } }
    /// <summary>True once we've offered to configure the SoH/2Ship fork (so we only nag once).</summary>
    public static bool EngineSetupPrompted { get => _d.EngineSetupPrompted; set { _d.EngineSetupPrompted = value; Save(); } }
    /// <summary>True once we've offered to configure Project64 (so we only nag once).</summary>
    public static bool Project64SetupPrompted { get => _d.Project64SetupPrompted; set { _d.Project64SetupPrompted = value; Save(); } }
    /// <summary>N64 playtest: boot straight into the injected level (default true). Toggled from the playtest menu.</summary>
    public static bool PlaytestN64AutoBoot { get => _d.PlaytestN64AutoBoot; set { _d.PlaytestN64AutoBoot = value; Save(); } }
    public static bool PlaytestAdult { get => _d.PlaytestAdult; set { _d.PlaytestAdult = value; Save(); } }
    /// <summary>Playtest scene mode remembered across sessions (all engines, both games). null = never chosen.</summary>
    public static bool? PlaytestAppend { get => _d.PlaytestAppend; set { _d.PlaytestAppend = value; Save(); } }
    /// <summary>When true (default) the actor properties show a simplified basic set + a "Show Advanced Options"
    /// toggle; when false they always show every field (classic layout).</summary>
    public static bool SimplifiedActorProperties { get => _d.SimplifiedActorProperties; set { _d.SimplifiedActorProperties = value; Save(); } }
    /// <summary>The persistent "Show Advanced Options" expand state (only used when simplified is on).</summary>
    public static bool ShowAdvancedActorOptions { get => _d.ShowAdvancedActorOptions; set { _d.ShowAdvancedActorOptions = value; Save(); } }
    /// <summary>Enable the N64 debug ROM's L+R+Z map-select + L+D-pad no-clip in playtest (off by default).</summary>
    public static bool PlaytestN64DebugControls { get => _d.PlaytestN64DebugControls; set { _d.PlaytestN64DebugControls = value; Save(); } }
    /// <summary>#12b: auto-detect base ROMs + forks at startup (configurable, on by default).</summary>
    public static bool AutoDetectAssetsOnStartup { get => _d.AutoDetectAssetsOnStartup; set { _d.AutoDetectAssetsOnStartup = value; Save(); } }

    /// <summary>The engine fork exe for the given game (MM → 2Ship, OoT → SoH).</summary>
    public static string? EngineExe(bool mm) => mm ? _d.TwoShipExePath : _d.SohExePath;
    public static void SetEngineExe(bool mm, string? path) { if (mm) TwoShipExePath = path; else SohExePath = path; }
    /// <summary>True when the engine fork exe for the given game is set and present on disk.</summary>
    public static bool IsEngineConfigured(bool mm)
    { var p = EngineExe(mm); return !string.IsNullOrWhiteSpace(p) && File.Exists(p); }

    /// <summary>True when an SoH/2Ship game archive (oot.otr / mm.o2r, generated by the engine on
    /// first launch) is present in <paramref name="dir"/>. The mod O2R we pack is useless without it.</summary>
    public static bool GameArchiveInDir(string? dir, bool mm)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        // SoH/2Ship name the imported game archive after the game (modern builds use .o2r;
        // older SoH used .otr). The engine's own assets (soh.o2r / 2ship.o2r) are separate.
        string[] names = mm
            ? ["mm.o2r", "mm.otr"]
            : ["oot.o2r", "oot.otr", "oot-mq.o2r", "oot-mq.otr"];
        return names.Any(n => File.Exists(Path.Combine(dir!, n)));
    }

    /// <summary>Default Entity-tool placement actor id for the given game (0xFFFF = editor-only dummy Link).</summary>
    public static int DefaultActor(bool mm) => mm ? _d.DefaultActorMM : _d.DefaultActorOoT;
    public static void SetDefaultActor(bool mm, int id) { if (mm) _d.DefaultActorMM = id; else _d.DefaultActorOoT = id; Save(); }

    // ── Editor view defaults ────────────────────────────────────────────────
    public static int  GridSize { get => Math.Clamp(_d.GridSize, 1, 1024); set { _d.GridSize = value; Save(); } }
    public static bool SnapToGrid { get => _d.SnapToGrid; set { _d.SnapToGrid = value; Save(); } }
    public static bool TextureLock { get => _d.TextureLock; set { _d.TextureLock = value; Save(); } }
    public static bool ShiftSelectSameTextureOnly { get => _d.ShiftSelectSameTextureOnly; set { _d.ShiftSelectSameTextureOnly = value; Save(); } }
    public static bool CullUnseenFaces { get => _d.CullUnseenFaces; set { _d.CullUnseenFaces = value; Save(); } }
    /// <summary>#5: tint grayscale ROM textures by their display-list prim colour (default on).</summary>
    public static bool TintGrayscaleTextures { get => _d.TintGrayscaleTextures; set { _d.TintGrayscaleTextures = value; Save(); } }
    /// <summary>Robustness opt-in: also tint grayscale textures by env colour when no prim tint exists (default off).</summary>
    public static bool TintGrayscaleWithEnv { get => _d.TintGrayscaleWithEnv; set { _d.TintGrayscaleWithEnv = value; Save(); } }
    public static bool PerLevelTextureTint { get => _d.PerLevelTextureTint; set { _d.PerLevelTextureTint = value; Save(); } }
    /// <summary>Max per-launch playtest logs to keep (0 = logging off, -1 = no limit). Default 50.</summary>
    public static int PlaytestLogMax { get => _d.PlaytestLogMax; set { _d.PlaytestLogMax = value; Save(); } }
    /// <summary>One fixed log file per engine (overwrite) instead of a timestamped file per launch. Default off.</summary>
    public static bool PlaytestLogOneFile { get => _d.PlaytestLogOneFile; set { _d.PlaytestLogOneFile = value; Save(); } }
    /// <summary>#10b: show real in-game item sprites in the playtest-inventory dialog (default on).</summary>
    public static bool InventorySprites { get => _d.InventorySprites; set { _d.InventorySprites = value; Save(); } }
    public static bool ShowSky { get => _d.ShowSky; set { _d.ShowSky = value; Save(); } }
    public static bool ShowGrid3D { get => _d.ShowGrid3D; set { _d.ShowGrid3D = value; Save(); } }
    public static bool ShowPrerenderedBackground { get => _d.ShowPrerenderedBackground; set { _d.ShowPrerenderedBackground = value; Save(); } }
    public static bool DiscordRpcEnabled { get => _d.DiscordRpcEnabled; set { _d.DiscordRpcEnabled = value; Save(); } }
    public static bool DiscordShowMap { get => _d.DiscordShowMap; set { _d.DiscordShowMap = value; Save(); } }
    public static bool DiscordShowGame { get => _d.DiscordShowGame; set { _d.DiscordShowGame = value; Save(); } }
    public static string DiscordAppId { get => _d.DiscordAppId ?? ""; set { _d.DiscordAppId = value ?? ""; Save(); } }
    public static int LastReplaceSceneOoT { get => _d.LastReplaceSceneOoT; set { _d.LastReplaceSceneOoT = value; Save(); } }
    public static int LastReplaceSceneMm { get => _d.LastReplaceSceneMm; set { _d.LastReplaceSceneMm = value; Save(); } }
    public static bool ShowEntities3D { get => _d.ShowEntities3D; set { _d.ShowEntities3D = value; Save(); } }
    public static bool ShowEntities2D { get => _d.ShowEntities2D; set { _d.ShowEntities2D = value; Save(); } }
    /// <summary>Trilinear (mipmap) filtering for world textures; off → crisp N64-style sampling.</summary>
    public static bool TrilinearFilter { get => _d.TrilinearFilter; set { _d.TrilinearFilter = value; Save(); } }

    /// <summary>Ghost reference-overlay opacity (0..1) and visibility. Persisted preferences; the ghost's
    /// level data is transient (never saved with the project).</summary>
    public static float GhostOpacity { get => _d.GhostOpacity; set { _d.GhostOpacity = Math.Clamp(value, 0.02f, 1f); Save(); } }
    public static bool GhostVisible { get => _d.GhostVisible; set { _d.GhostVisible = value; Save(); } }
    /// <summary>Draw the ghost without depth-writing so brushes/actors always show through its walls (X-ray).</summary>
    public static bool GhostXray { get => _d.GhostXray; set { _d.GhostXray = value; Save(); } }
    /// <summary>Texture browser hides non-area object/effect texture categories (object_/g_pn_/vr_/…).</summary>
    public static bool HideObjectTextures { get => _d.HideObjectTextures; set { _d.HideObjectTextures = value; Save(); } }

    /// <summary>2D views: draw each actor as its model wireframe (true) rather than a bounding box (false).</summary>
    public static bool Actor2DWireframe { get => _d.Actor2DWireframe; set { _d.Actor2DWireframe = value; Save(); } }

    /// <summary>MM flow-of-time default for new rooms (false = clock frozen, the MM editing default).
    /// Persisted so consecutive MM maps inherit the dev's last choice.</summary>
    public static bool MmFlowOfTime { get => _d.MmFlowOfTime; set { _d.MmFlowOfTime = value; Save(); } }

    // ── Last-used startup paths ─────────────────────────────────────────────
    /// <summary>The path last entered for this game mode on the startup screen, or null.</summary>
    public static string? GetLastGamePath(GameMode mode)
        => _d.LastGamePaths.TryGetValue(mode.ToString(), out var p) && !string.IsNullOrWhiteSpace(p) ? p : null;
    public static void SetLastGamePath(GameMode mode, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _d.LastGamePaths[mode.ToString()] = path!;
        Save();
    }

    /// <summary>The game mode last chosen on the splash, so it reopens on it (null = none yet).</summary>
    public static string? LastGameMode { get => _d.LastGameMode; set { _d.LastGameMode = value; Save(); } }

    // ── Recent projects ─────────────────────────────────────────────────────
    private const int MaxRecentFiles = 10;

    /// <summary>Recently opened/saved project paths, most-recent first.</summary>
    public static IReadOnlyList<string> RecentFiles => _d.RecentFiles;

    /// <summary>Records a project as the most-recently used (de-duped, capped, persisted).</summary>
    public static void AddRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full;
        try { full = Path.GetFullPath(path); } catch { full = path; }
        _d.RecentFiles.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        _d.RecentFiles.Insert(0, full);
        if (_d.RecentFiles.Count > MaxRecentFiles)
            _d.RecentFiles.RemoveRange(MaxRecentFiles, _d.RecentFiles.Count - MaxRecentFiles);
        Save();
    }

    /// <summary>Drops a single entry (e.g. the file was deleted/moved).</summary>
    public static void RemoveRecentFile(string path)
    {
        if (_d.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0) Save();
    }

    /// <summary>Empties the recent-projects list.</summary>
    public static void ClearRecentFiles()
    {
        if (_d.RecentFiles.Count == 0) return;
        _d.RecentFiles.Clear();
        Save();
    }

    /// <summary>Whether to keep the .mhproj file association registered for this user.</summary>
    public static bool AssociateProjectFiles { get => _d.AssociateProjectFiles; set { _d.AssociateProjectFiles = value; Save(); } }

    // ── Playtest inventory (per game, persisted) ────────────────────────────
    private static string Game(bool mm) => mm ? "mm" : "oot";

    /// <summary>The last-used playtest inventory JSON for the game, or null (caller uses Default).</summary>
    public static string? GetInventoryJson(bool mm)
        => _d.InventoryCurrent.TryGetValue(Game(mm), out var j) && !string.IsNullOrWhiteSpace(j) ? j : null;
    public static void SetInventoryJson(bool mm, string json) { _d.InventoryCurrent[Game(mm)] = json; Save(); }

    /// <summary>Names of the user-saved inventory presets for the game (sorted).</summary>
    public static IReadOnlyList<string> GetInventoryPresetNames(bool mm)
    {
        string prefix = Game(mm) + ":";
        return _d.InventoryPresets.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k[prefix.Length..]).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static string? GetInventoryPreset(bool mm, string name)
        => _d.InventoryPresets.TryGetValue($"{Game(mm)}:{name}", out var j) ? j : null;
    public static void SaveInventoryPreset(bool mm, string name, string json) { _d.InventoryPresets[$"{Game(mm)}:{name}"] = json; Save(); }
    public static void DeleteInventoryPreset(bool mm, string name) { if (_d.InventoryPresets.Remove($"{Game(mm)}:{name}")) Save(); }

    /// <summary>True when the opposite game (relative to the current project) has a usable
    /// ROM or O2R configured on disk.</summary>
    public static bool HasOppositeSource(bool nativeIsOoT)
    {
        var rom = OppositeRomPath(nativeIsOoT);
        var o2r = OppositeO2RPath(nativeIsOoT);
        return (!string.IsNullOrWhiteSpace(rom) && File.Exists(rom)) ||
               (!string.IsNullOrWhiteSpace(o2r) && File.Exists(o2r));
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _d = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();
        }
        catch { _d = new Data(); }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_d, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* preferences are best-effort */ }
    }
}
