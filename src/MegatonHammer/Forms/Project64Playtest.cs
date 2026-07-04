using System.Diagnostics;
using System.Linq;
using MegatonHammer.Editor;
using MegatonHammer.Export;
using MegatonHammer.Rom;

namespace MegatonHammer.Forms;

/// <summary>
/// Vanilla (N64) playtest: injects the editor's scene into a copy of the configured OoT
/// ROM (ideally the MQ debug ROM, which has a built-in map-select), writes a temp .z64,
/// and launches Project64 on it. The injected ROM repoints the chosen scene slot, so the
/// level is reached via the debug map-select or by entering that area in normal play.
/// </summary>
public static class Project64Playtest
{
    private static readonly string[] CommonPaths =
    [
        // The bundled Megaton Hammer PJ64 4.0 fork: has the playtest hook + MH_INTERP interpreter
        // support (avoids the recompiler's EmulationStarting crash). Preferred over stock installs.
        @"D:\Copilot_OOT\WorkFolders\MegatonHammer\pj64run\Project64.exe",
        @"C:\Program Files\Project64 3.0\Project64.exe",
        @"C:\Program Files\Project64\Project64.exe",
        @"C:\Program Files (x86)\Project64 2.4\Project64.exe",
        @"C:\Program Files (x86)\Project64\Project64.exe",
    ];

    public static void Launch(IWin32Window owner, GameConfig config, ZScene scene, int sceneSlot,
                              Export.PlaytestConfig? playtest = null,
                              Func<string, System.Drawing.Bitmap?>? texResolver = null)
    {
        DiagnosticLog.Begin("Playtest — Project64 (N64)");
        // The N64 base ROM: the project's own config.RomPath if set, else the base ROM configured in
        // Options / found by auto-detect (EditorSettings.{Oot,Mm}RomPath). The two were disconnected —
        // auto-detect filled the Options ROM but the playtest only looked at config.RomPath, so a freshly
        // auto-detected ROM read as "not configured". Fall back so the configured/detected ROM is used.
        string? romPath = config.RomPath;
        // For the N64 OoT playtest, actively prefer the gc-eu-mq-dbg DEBUG ROM ("THE LEGEND OF DEBUG").
        // It is a different ROM from the retail OoT that OotRomPath/config.RomPath usually hold (that one
        // feeds SoH asset extraction) — and only the debug build supports the seamless auto-boot + the
        // no-dmadata inject path. Without this, auto-detect's first OoT match (retail) was used, the debug
        // layout check failed, auto-boot never fired, and the level "never launched in the mq debug rom".
        if (config.IsOoTBased && RomFingerprint.FindOotDebugRom() is { } dbgRom)
        {
            romPath = dbgRom;
            DiagnosticLog.Step($"using gc-eu-mq-dbg debug ROM for N64 OoT playtest: {dbgRom}");
        }
        if (!IsRom(romPath))
        {
            romPath = config.IsOoTBased ? EditorSettings.OotRomPath : EditorSettings.MmRomPath;
            if (IsRom(romPath)) DiagnosticLog.Step($"using base ROM from Options: {romPath}");
        }
        DiagnosticLog.Step($"base ROM: {romPath ?? "(none)"}");
        if (!IsRom(romPath))
        {
            // #12a: the N64 base ROM is game-specific — OoT (gc-eu-mq-dbg debug ROM) vs MM (US retail).
            string game = config.IsOoTBased ? "Ocarina of Time (gc-eu-mq-dbg debug)" : "Majora's Mask (US retail)";
            DiagnosticLog.Fail($"no valid base {(config.IsOoTBased ? "OoT" : "MM")} ROM configured");
            MessageBox.Show($"Configure a base {game} ROM (Options ▸ Cross-Game, or let auto-detect find it) before an N64 playtest.",
                "Playtest (N64)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string? emu = FindEmulator(owner);
        if (emu == null) { DiagnosticLog.Step("user cancelled emulator browse"); return; }
        DiagnosticLog.Step($"emulator: {emu}");
        EnsureRdb(emu);   // a 0-byte Project64.rdb crashes OoT/MM at boot — restore the bundled DB

        PlaytestLog? plog = null;
        try
        {
            var baseRom = new RomImage(romPath!);
            DiagnosticLog.Step($"base ROM game: {baseRom.Game}");
            bool mm = baseRom.Game == RomGame.MM;

            // Per-playtest session log: the COMPLETE launch state (every scene/room setting incl. defaults,
            // playtest config, full inventory + mode, the injection manifest via mirrored DiagnosticLog
            // steps) + a live link to the PJ64 fork's log. Fixed location: see PlaytestLog.LogDir.
            plog = PlaytestLog.Begin(mm ? "PJ64-MM" : "PJ64-OoT");
            plog.Section("PLAYTEST CONFIG");
            plog.Kv("base ROM", romPath);
            plog.Kv("emulator", emu);
            plog.Kv("game", mm ? "MM (US-retail inject)" : "OoT (gc-eu-mq-dbg inject)");
            plog.DumpObject("PLAYTEST CONFIG (raw)", playtest);
            plog.DumpInventory(playtest?.Inventory ?? "debug",
                               PlaytestInventory.FromJson(EditorSettings.GetInventoryJson(mm)), !mm);
            plog.DumpScene(scene, scene.Rooms.Select(r => (object)r.Settings), scene.Settings);

            // #12b: validate the base ROM by MD5 against the expected debug/retail build. A mismatch is a
            // warning, not a hard stop (an off-spec ROM may still inject), but the user is told up-front so
            // a wrong/region/byteswapped ROM doesn't surface later as a baffling in-game failure.
            var (romOk, romDetail) = RomFingerprint.CheckRom(romPath, oot: !mm);
            DiagnosticLog.Step($"ROM fingerprint: {romDetail}");
            if (!romOk && MessageBox.Show(
                    $"The configured {(mm ? "MM" : "OoT")} ROM isn't the expected playtest build:\n\n{romDetail}\n\n" +
                    $"Expected: {(mm ? "Majora's Mask (USA) retail" : "Ocarina of Time gc-eu-mq-dbg debug")}.\n\n" +
                    "Continue anyway?", "Playtest (N64) — ROM check",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            { DiagnosticLog.Step("user cancelled on ROM-fingerprint mismatch"); return; }
            if (baseRom.Game != RomGame.OoT && !mm)
            {
                DiagnosticLog.Fail("N64 injection only supports OoT and MM ROMs");
                MessageBox.Show("N64 injection supports Ocarina of Time and Majora's Mask ROMs only.",
                    "Playtest (N64)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Faithful round-trip: when playtesting a VANILLA-imported scene with no added brush geometry,
            // re-emit the original scene/room bytes (collision surface types, object list 0x0B, keep object,
            // prerender cameras, MM setups all preserved) instead of the lossy template rebuild — the same
            // path the Inject-to-ROM dialog uses. (The MM playtest injects ONE room into Termina Field, so
            // retain there is limited to single-room imported scenes; multi-room MM keeps the rebuild path.)
            bool addedGeometry = scene.Rooms.Any(r => r.Geometry.Count > 0);
            var retained = !addedGeometry ? Export.RetainedSceneBuilder.TryBuild(scene) : null;
            if (mm && retained is { } rr && rr.Rooms.Count != 1) retained = null;
            byte[] sceneBytes; List<byte[]> roomBytes;
            if (retained is { } ret)
            {
                sceneBytes = ret.Scene; roomBytes = ret.Rooms;
                DiagnosticLog.Ok($"faithful vanilla round-trip (retain): scene {sceneBytes.Length} bytes, {roomBytes.Count} room(s)");
            }
            else
            {
                DiagnosticLog.Step($"building scene binaries ({(mm ? "MM" : "OoT")}, rooms={scene.Rooms.Count}, rebuild)");
                (sceneBytes, roomBytes) = SceneExporter.BuildBinaries(scene, texResolver, Export.ActorObjectResolver.Build(mm: mm), mm: mm);
                DiagnosticLog.Ok($"scene {sceneBytes.Length} bytes, {roomBytes.Count} room blob(s)");
            }

            string dir = Path.Combine(Path.GetTempPath(), "MegatonHammer");
            Directory.CreateDirectory(dir);
            string romOut = Path.Combine(dir, "mh_playtest.z64");

            // Custom/empty inventory for the PJ64 fork's N64 save-poke — behavioural parity with the OTR
            // MhApplyCustomInventory. The editor computes the flat SaveContext poke list here (testable C#,
            // verified field-for-field against SoH's [mh_inv] applied: output) and writes it as
            // mh_n64_save.txt; the fork applies it blindly after the debug save runs (OoT and MM — the fork's
            // MM path applies these to kMM_SaveContext a few frames into gameplay). Deleted for debug mode so a
            // stale file can't apply last run's loadout.
            string savePokePath = Path.Combine(dir, "mh_n64_save.txt");
            try
            {
                string invMode = playtest?.Inventory ?? "debug";
                if (invMode == "custom" || invMode == "empty")
                {
                    var pinv2 = PlaytestInventory.FromJson(EditorSettings.GetInventoryJson(mm));
                    List<Rom.N64SavePokes.Poke> pokes; string aggr;
                    if (mm)
                    {
                        pokes = Rom.N64SavePokes.ComputeMM(pinv2, invMode, out var sm);
                        aggr = $"equips=0x{sm.EquipsEquipment:X} upgrades=0x{sm.Upgrades:X} quest=0x{sm.QuestItems:X} tatl={sm.HasTatl}";
                    }
                    else
                    {
                        pokes = Rom.N64SavePokes.ComputeOoT(pinv2, invMode, out var s);
                        aggr = $"equip=0x{s.InvEquipment:X} upgrades=0x{s.Upgrades:X} quest=0x{s.QuestItems:X}";
                    }
                    File.WriteAllText(savePokePath, Rom.N64SavePokes.Format(pokes, oot: !mm));
                    DiagnosticLog.Step($"N64 save pokes: {pokes.Count} (mode={invMode} {aggr} hearts={pinv2.Hearts})");
                }
                else if (File.Exists(savePokePath)) File.Delete(savePokePath);
            }
            catch (Exception ex) { DiagnosticLog.Step($"save-poke write skipped: {ex.Message}"); }

            if (mm)
            {
                // MM N64 playtest: decompress US-retail MM, inject into the Termina Field playtest slot,
                // apply the boot/menu fix (full HUD/pause/Tatl), and optionally auto-boot straight in.
                if (roomBytes.Count > 1)
                    DiagnosticLog.Step($"note: MM playtest injects room 0 only ({roomBytes.Count} rooms in scene)");
                bool autoBoot = EditorSettings.PlaytestN64AutoBoot;
                // Native music passes through; a CROSS-GAME (OoT) track is extracted from the OoT ROM and
                // injected into MM's audioseq (BuildPlaytestRom picks a host slot + plays through its font).
                byte music = scene.Settings.MusicCrossGame ? (byte)0 : scene.Settings.MusicSeq;
                byte[] crossSeq = null; int crossSrcId = -1;
                if (scene.Settings.MusicCrossGame)
                {
                    string ootRom = EditorSettings.OotRomPath;
                    if (!string.IsNullOrEmpty(ootRom) && File.Exists(ootRom))
                    {
                        try
                        {
                            crossSeq = Rom.AudioSeqExtractor.Extract(new Rom.RomImage(ootRom), scene.Settings.MusicSeq);
                            crossSrcId = scene.Settings.MusicSeq;
                            if (crossSeq != null) DiagnosticLog.Ok($"cross-game music: extracted OoT seq 0x{scene.Settings.MusicSeq:X2} ({crossSeq.Length}B)");
                            else DiagnosticLog.Step($"cross-game music: OoT seq 0x{scene.Settings.MusicSeq:X2} not extractable");
                        }
                        catch (Exception ex) { DiagnosticLog.Step($"cross-game music extract failed: {ex.Message}"); }
                    }
                    else DiagnosticLog.Step("cross-game music: no OoT ROM configured");
                }
                // Append mode (parity with SoH/2Ship SCENE_MH_APPEND): clone Termina's scene into a spare slot
                // + redirect its entrance, so the level plays without destroying Termina Field's real data.
                bool mmAppend = playtest?.Append ?? false;
                byte[] rom = SelfTest.MmInjectScene.BuildPlaytestRom(romPath!, sceneBytes, roomBytes[0], autoBoot, music, crossSeq, crossSrcId, mmAppend);
                File.WriteAllBytes(romOut, rom);
                DiagnosticLog.Ok($"MM ROM ({rom.Length} bytes, {(mmAppend ? $"APPEND spare slot 0x{SelfTest.MmInjectScene.AppendSlotId:X2}" : $"overwrite slot 0x{SelfTest.MmInjectScene.TargetSceneId:X2}")}, auto-boot {(autoBoot ? "ON" : "off")})");
                DiagnosticLog.Step($"wrote {romOut}");

                // MM auto-boots via MmInjectScene (no fork warp), but the fork still needs the inventory
                // mode + debug-controls flag to apply the custom inventory + debug controls once it finds
                // the live PlayState. Write a minimal params file (no entrance -> no double-warp).
                int mmInv = playtest?.Inventory switch { "empty" => 2, "custom" => 1, _ => 0 };
                int mmDbg = EditorSettings.PlaytestN64DebugControls ? 1 : 0;
                File.WriteAllText(Path.Combine(dir, "mh_n64_playtest.txt"), $"inventory={mmInv}\ndebug={mmDbg}\n");
                DiagnosticLog.Step($"MM N64 params: inventory={mmInv} debug={mmDbg}");

                LogManifest(plog, emu, dir, romOut);   // verbatim params/save-poke files + rdb status
                var psiMm = new ProcessStartInfo { FileName = emu, Arguments = $"\"{romOut}\"", UseShellExecute = false };
                psiMm.Environment["MH_INTERP"] = "1";
                var procMm = Process.Start(psiMm);
                DiagnosticLog.Ok("Project64 launched (MM, interpreter core)");
                // Tail BOTH the fork's playtest log and PJ64's own emulation trace (filtered).
                plog!.LinkEngine(procMm, [Path.Combine(dir, "mh_n64_playtest.log"), Pj64TraceLog(emu)]);
                DiagnosticLog.Ok($"playtest log: {plog.Path}");
                return;   // no post-launch popup — the emulator window is the feedback
            }

            // The gc-eu-mq-dbg DEBUG ROM ("THE LEGEND OF DEBUG") needs the no-dmadata injection path:
            // its DmaMgr_Init walks a fixed-size filename array in lockstep with the dma table, so a
            // grown table crashes the boot. InjectDebug writes to free space and adds no dma entries.
            // Detect the gc-eu-mq-dbg debug ROM by LAYOUT, not name: that ROM is internally "THE LEGEND OF
            // ZELDA" (not "...DEBUG"), so the old name check missed it → it took the retail inject path (which
            // crashes the debug ROM's DmaMgr_Init) and never auto-booted. The layout check is authoritative.
            bool isDebug = baseRom.InternalName.ToUpperInvariant().Contains("DEBUG")
                           || Rom.OotDebugAutoBoot.IsRecognized(baseRom.Data);
            bool waterScroll = Export.DisplayListBuilder.SceneHasWater(scene);   // OoT: drawConfig SDC_CALM_WATER

            // Cross-game music (OoT target): a MM track chosen in the editor is extracted from the MM ROM and
            // injected into OoT's audioseq; RomInjector points this scene's 0x15 (SetSoundSettings) at the host
            // slot so it plays through an OoT font (tracks are restricted to shared-instrument sequences).
            byte[] ootCrossSeq = null; int ootCrossSrcId = -1;
            if (scene.Settings.MusicCrossGame)
            {
                string mmRom = EditorSettings.MmRomPath;
                if (!string.IsNullOrEmpty(mmRom) && File.Exists(mmRom))
                {
                    try
                    {
                        ootCrossSeq = Rom.AudioSeqExtractor.Extract(new Rom.RomImage(mmRom), scene.Settings.MusicSeq);
                        ootCrossSrcId = scene.Settings.MusicSeq;
                        if (ootCrossSeq != null) DiagnosticLog.Ok($"cross-game music: extracted MM seq 0x{scene.Settings.MusicSeq:X2} ({ootCrossSeq.Length}B)");
                        else DiagnosticLog.Step($"cross-game music: MM seq 0x{scene.Settings.MusicSeq:X2} not extractable");
                    }
                    catch (Exception ex) { DiagnosticLog.Step($"cross-game music extract failed: {ex.Message}"); }
                }
                else DiagnosticLog.Step("cross-game music: no MM ROM configured");
            }

            var result = isDebug
                ? RomInjector.InjectDebug(baseRom, sceneBytes, roomBytes, sceneSlot, waterScroll: waterScroll,
                                          crossGameSeq: ootCrossSeq, crossGameSrcSeqId: ootCrossSrcId)
                : RomInjector.Inject(baseRom, sceneBytes, roomBytes, sceneSlot, scene.Settings.AreaName, waterScroll,
                                     crossGameSeq: ootCrossSeq, crossGameSrcSeqId: ootCrossSrcId);
            if (ootCrossSeq != null) DiagnosticLog.Step(result.Message);
            // OoT scene mode: Append = SCENE_TEST01 (0x65) disposable dev slot (no real scene touched);
            // Replace = the chosen real scene overwritten in this disposable playtest ROM.
            bool ootAppend = (playtest?.Append ?? true) || sceneSlot == 0x65;
            DiagnosticLog.Step(ootAppend
                ? $"OoT scene mode: APPEND -> SCENE_TEST01 (slot 0x{sceneSlot:X2}); no real scene overwritten"
                : $"OoT scene mode: REPLACE -> {Rom.OotSceneNames.Pretty(sceneSlot)} (slot 0x{sceneSlot:X2})");
            DiagnosticLog.Ok($"injected ROM ({result.Rom.Length} bytes, {(isDebug ? "debug/no-dma" : "retail/append")} path)");

            // Auto-boot straight into the level (gc-eu-mq-dbg only): detour ConsoleLogo_Main to set up the
            // debug save + target entrance + jump to Gameplay_Init, skipping logo/title/map-select. No-op on
            // an unrecognized debug ROM. Honors the "Boot straight into level" toggle.
            bool ootAutoBoot = isDebug && EditorSettings.PlaytestN64AutoBoot;
            if (ootAutoBoot)
            {
                int entrance = SlotToEntrance(sceneSlot);
                int age = playtest is { Adult: false } ? 1 : 0;   // gSaveContext linkAge: 0=adult, 1=child
                if (OotDebugAutoBoot.Patch(result.Rom, entrance, age))
                    DiagnosticLog.Ok($"OoT auto-boot patched (entrance 0x{entrance:X4}, age={(age == 0 ? "adult" : "child")})");
                else { ootAutoBoot = false; DiagnosticLog.Step("OoT auto-boot skipped (unrecognized debug ROM layout)"); }
            }

            File.WriteAllBytes(romOut, result.Rom);
            DiagnosticLog.Step($"wrote {romOut}");

            // Drop the params the bundled PJ64 fork reads to auto-warp to this scene (with the chosen
            // Link age) once any gameplay is reached. Inventory: "debug" via the map-select boot path
            // (which runs the game's debug-save init), "empty" via the opening demo.
            WriteN64Params(dir, sceneSlot, playtest, scene.Settings.PlaytestTimeOfDay);

            // Launch with MH_INTERP=1 so the bundled PJ64 fork uses the interpreter core (video stays
            // on). The dynamic recompiler raises "EmulationStarting: Exception caught" (N64System.cpp:740)
            // on some setups; the interpreter avoids that crash. UseShellExecute=false to pass the env var.
            LogManifest(plog, emu, dir, romOut);   // verbatim params/save-poke files + rdb status
            var psi = new ProcessStartInfo { FileName = emu, Arguments = $"\"{romOut}\"", UseShellExecute = false };
            psi.Environment["MH_INTERP"] = "1";
            var procOot = Process.Start(psi);
            DiagnosticLog.Ok($"Project64 launched (interpreter core; scene injected at slot 0x{sceneSlot:X2})");
            // Tail BOTH the fork's playtest log and PJ64's own emulation trace (filtered).
            plog!.LinkEngine(procOot, [Path.Combine(dir, "mh_n64_playtest.log"), Pj64TraceLog(emu)]);
            DiagnosticLog.Ok($"playtest log: {plog.Path}");
            // No popup — seamless like the MM path. If auto-boot couldn't run (non-debug ROM), the
            // map-select hint goes to the diagnostic log instead of interrupting with a dialog.
            if (!ootAutoBoot)
                DiagnosticLog.Step($"auto-boot off — in the debug MAP SELECT pick scene slot 0x{sceneSlot:X2}" +
                                   (sceneSlot == 0x65 ? " (entry \"118\", Test Map)" : ""));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("N64 playtest failed", ex);
            plog?.Stop($"N64 playtest failed: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show($"N64 playtest failed:\n{ex.Message}\n\nDiagnostic trail:\n{DiagnosticLog.RecentTrail()}\n\nFull log: {DiagnosticLog.Path}" +
                (plog != null ? $"\nPlaytest log: {plog.Path}" : ""),
                "Playtest (N64)", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Writes %TEMP%\MegatonHammer\mh_n64_playtest.txt for the fork: target entrance (so the warp
    // lands in this scene), Link age (0 adult / 1 child), and an inventory hint.
    private static void WriteN64Params(string dir, int sceneSlot, Export.PlaytestConfig? cfg, ushort timeOfDay = 0x8000)
    {
        int entrance = SlotToEntrance(sceneSlot);
        int age = cfg is { Adult: false } ? 1 : 0;                 // gSaveContext linkAge: 0=adult, 1=child
        int inv = cfg?.Inventory switch { "empty" => 2, "custom" => 1, _ => 0 }; // 0=debug
        int dbg = EditorSettings.PlaytestN64DebugControls ? 1 : 0;
        // timeOfDay: one u16 over a 24h day from midnight (0x8000=noon) shared with OoT dayTime / MM time.
        string txt = $"entrance={entrance}\nage={age}\ninventory={inv}\nscene={sceneSlot}\ntimeOfDay={timeOfDay}\ndebug={dbg}\n";
        File.WriteAllText(Path.Combine(dir, "mh_n64_playtest.txt"), txt);
        DiagnosticLog.Step($"N64 params: entrance=0x{entrance:X4} age={(age == 0 ? "adult" : "child")} inventory={inv} timeOfDay=0x{timeOfDay:X4}");
    }

    // First entrance index for the scene slot the editor repoints, so the auto-boot hook warps into it.
    // APPEND mode targets SCENE_TEST01 (0x65) — a non-Spot, cutscene-free dev scene that loads custom
    // multi-room levels cleanly and destroys no real scene. REPLACE mode targets a chosen real scene; its
    // first (leader) entrance is resolved from the decomp entrance table (EntranceNames) via the scene's
    // SCENE_ macro. Falls back to ENTR_TEST01_0 (0x0094) if the table isn't loaded or the scene has no entrance.
    private static int SlotToEntrance(int slot)
    {
        if (slot == 0x65) return 0x0094;                       // SCENE_TEST01 -> ENTR_TEST01_0 (append target)
        string? macro = Rom.OotSceneNames.SceneMacro(slot);    // e.g. "SCENE_KOKIRI_FOREST"
        if (macro != null && Editor.EntranceNames.Available)
        {
            var e = Editor.EntranceNames.Leaders.FirstOrDefault(x => x.SceneMacro == macro);
            if (e != null) return e.Index;
        }
        return 0x0094;   // safe fallback (dev test scene)
    }

    private static string? FindEmulator(IWin32Window owner)
    {
        // Use the path configured in Options, then well-known install locations, then ask.
        var saved = EditorSettings.Project64Path;
        if (IsExe(saved)) return saved;
        foreach (var p in CommonPaths)
            if (File.Exists(p)) { EditorSettings.Project64Path = p; return p; }

        using var ofd = new OpenFileDialog
        {
            Title = "Locate Project64.exe",
            Filter = "Project64 (Project64.exe)|Project64.exe|Executables (*.exe)|*.exe",
        };
        if (ofd.ShowDialog(owner) == DialogResult.OK) { EditorSettings.Project64Path = ofd.FileName; return ofd.FileName; }
        return null;
    }

    // A 0-byte/missing Project64.rdb makes PJ64 boot N64 ROMs on bare defaults (no per-game CIC / save type /
    // RDRAM / core compatibility), which crashes OoT and MM during early boot (BadVAddr 0xFFFFFFFF in their OS
    // exception handler). The bundled good database lives in the repo's forks/pj64/Config; restore it next to
    // the chosen Project64.exe whenever its own copy is empty/missing. Idempotent and best-effort.
    private static void EnsureRdb(string emu)
    {
        try
        {
            string? emuDir = Path.GetDirectoryName(emu);
            if (emuDir == null) return;
            string cfgDir = Path.Combine(emuDir, "Config");
            string rdb = Path.Combine(cfgDir, "Project64.rdb");
            if (File.Exists(rdb) && new FileInfo(rdb).Length > 0) return;   // already good

            // Bundle = <repoRoot>\forks\pj64\Config. For the editor's own pj64run, repoRoot is emuDir's parent.
            // Also try walking up from the app base dir so it works regardless of where the editor runs from.
            string?[] candidates =
            {
                Directory.GetParent(emuDir)?.FullName is { } up ? Path.Combine(up, "forks", "pj64", "Config") : null,
                FindUp(AppContext.BaseDirectory, Path.Combine("forks", "pj64", "Config")),
            };
            string? bundle = candidates.FirstOrDefault(b => b != null && Directory.Exists(b)
                                                            && new FileInfo(Path.Combine(b, "Project64.rdb")).Length > 0);
            if (bundle == null) { DiagnosticLog.Step("PJ64 rdb empty but no bundled DB found to restore"); return; }

            Directory.CreateDirectory(cfgDir);
            foreach (var n in new[] { "Project64.rdb", "Project64.rdx", "Video.rdb", "Audio.rdb" })
            {
                string s = Path.Combine(bundle, n), d = Path.Combine(cfgDir, n);
                if (File.Exists(s) && (!File.Exists(d) || new FileInfo(d).Length == 0))
                    File.Copy(s, d, overwrite: true);
            }
            DiagnosticLog.Ok("restored PJ64 ROM database (Project64.rdb was empty — would crash N64 boot)");
        }
        catch (Exception ex) { DiagnosticLog.Step($"PJ64 rdb restore skipped: {ex.Message}"); }
    }

    // Walk up from <start> looking for a directory containing <relative>; returns the full path or null.
    private static string? FindUp(string start, string relative)
    {
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
        {
            string cand = Path.Combine(d.FullName, relative);
            if (Directory.Exists(cand)) return cand;
        }
        return null;
    }

    // PJ64 writes its own verbose trace to <emuDir>\Logs\Project64.log (boot, plugin init, emulation
    // errors, "Fatal Error: Stopping emulation"). The playtest log tails it (filtered to the relevant
    // lines) so emulation-level diagnostics are captured alongside the MH fork's log. Best-effort path.
    private static string Pj64TraceLog(string emu)
    {
        string? d = Path.GetDirectoryName(emu);
        return d != null ? Path.Combine(d, "Logs", "Project64.log") : "";
    }

    // Records the exact files the PJ64 fork reads (params + save pokes) verbatim into the playtest log,
    // plus the injected ROM size and the Project64.rdb status — so a "it crashed playing X.mhproj" report
    // has the complete injection manifest in one place.
    private static void LogManifest(PlaytestLog? plog, string emu, string dir, string romOut)
    {
        if (plog == null) return;
        plog.Section("INJECTION MANIFEST (what the fork + emulator receive)");
        try { plog.Kv("injected ROM", $"{romOut} ({new FileInfo(romOut).Length} bytes)"); } catch { }
        try
        {
            string? cfgDir = Path.GetDirectoryName(emu);
            string rdb = cfgDir != null ? Path.Combine(cfgDir, "Config", "Project64.rdb") : "";
            plog.Kv("Project64.rdb", File.Exists(rdb) ? $"{new FileInfo(rdb).Length} bytes (populated=" +
                (new FileInfo(rdb).Length > 0 ? "yes)" : "NO — would crash N64 boot)") : "(missing)");
        }
        catch { }
        DumpFileInto(plog, "mh_n64_playtest.txt (fork params)", Path.Combine(dir, "mh_n64_playtest.txt"));
        DumpFileInto(plog, "mh_n64_save.txt (inventory save pokes)", Path.Combine(dir, "mh_n64_save.txt"));
    }

    private static void DumpFileInto(PlaytestLog plog, string label, string path)
    {
        plog.Section(label);
        try
        {
            if (!File.Exists(path)) { plog.Line("(absent — debug/default mode)"); return; }
            foreach (var l in File.ReadAllLines(path)) plog.Line("  " + l);
        }
        catch (Exception ex) { plog.Line($"(unreadable: {ex.Message})"); }
    }

    private static bool IsExe(string? p) => !string.IsNullOrWhiteSpace(p) && File.Exists(p);

    private static bool IsRom(string? p) =>
        !string.IsNullOrWhiteSpace(p) && File.Exists(p) &&
        Path.GetExtension(p).ToLowerInvariant() is ".z64" or ".n64" or ".v64";
}
