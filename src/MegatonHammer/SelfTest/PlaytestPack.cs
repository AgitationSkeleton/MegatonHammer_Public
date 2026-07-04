using MegatonHammer.Editor;
using MegatonHammer.Export;
using MegatonHammer.Rom;
using MegatonHammer.Textures;

namespace MegatonHammer.SelfTest;

/// <summary>Headless end-to-end inventory-injection helper: builds the Test Temple scene, attaches a
/// custom starting inventory (Default OoT, or a preset), and packs it into the engine's mods folder
/// as the playtest O2R — so the engine's boot hook applies it and logs the result. Verifies the
/// editor→O2R→engine path without the GUI. Run: MegatonHammer --packplaytest [engineDir] [preset]</summary>
public static class PlaytestPack
{
    public static void Run(string[] args)
    {
        // MM/2Ship mode: pack an MM-format O2R for the 2Ship fork instead of OoT/SoH.
        bool mm = args.Contains("mm", StringComparer.OrdinalIgnoreCase)
               || args.Contains("2ship", StringComparer.OrdinalIgnoreCase);
        string defaultEngine = mm
            ? System.IO.Path.Combine(Editor.AppPaths.BaseDir, @"2Ship\x64\Release")
            : System.IO.Path.Combine(Editor.AppPaths.BaseDir, @"SoH\x64\Release");
        string engineDir = args.Length >= 2 && !IsFlag(args[1]) ? args[1] : defaultEngine;
        string presetName = args.Length >= 3 && !IsFlag(args[2]) ? args[2] : "Default";

        // An explicit .mhproj path among the args packs THAT project (for diagnosing a specific scene,
        // e.g. the chest); otherwise build the known-good Test Temple.
        string? projArg = args.FirstOrDefault(a => a.EndsWith(".mhproj", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        var doc = new MapDocument();
        if (projArg != null)
        {
            ProjectSerializer.Load(doc, projArg);
            Console.WriteLine($"packing project: {projArg}");
        }
        else
        {
            string tempDir = Path.Combine(Path.GetTempPath(), mm ? "mh_packplaytest_mm" : "mh_packplaytest");
            Directory.CreateDirectory(tempDir);
            TestTempleBuilder.Build(tempDir, mm);
            ProjectSerializer.Load(doc, Path.Combine(tempDir, "Test_Temple.mhproj"));
        }

        bool append = args.Contains("append", StringComparer.OrdinalIgnoreCase);
        var inv = PlaytestInventory.Preset(presetName, oot: !mm);
        var cfg = new PlaytestConfig
        {
            // append → reserved SCENE_MH_APPEND (OoT 0x6E / MM informational); else replace a real slot.
            TargetSceneId = append ? (mm ? 0x71 : 0x6E) : (mm ? 0x08 : 0x52),
            Append = append,
            Adult = false,
            Inventory = "custom",
            InventoryPayload = inv.ToPayloadJson(),
        };

        // Texture resolver: load the ROM's textures so the brush textures (rom_XXXX) export as OTEX.
        var texResolver = BuildRomTexResolver(mm);

        string o2r = Path.Combine(engineDir, "mods", "mh_playtest.o2r");
        O2RPacker.PackOtr(doc.Scene, o2r, cfg, mm: mm, texResolver: texResolver);

        long size = File.Exists(o2r) ? new FileInfo(o2r).Length : -1;
        Console.WriteLine($"packed {o2r} ({size} bytes); game={(mm ? "mm" : "oot")}; textures={(texResolver != null ? "resolved" : "none")}");
        Console.WriteLine($"inventory preset='{presetName}' payload={cfg.InventoryPayload}");
        Console.WriteLine("Launch the engine; its mh_playtest_boot.log should show '[mh_inv] applied: …'.");
    }

    // True for non-positional arg tokens (mode flags), so they don't get mistaken for engineDir/preset.
    private static bool IsFlag(string a) =>
        a.Equals("append", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("mm", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("2ship", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("n64", StringComparison.OrdinalIgnoreCase) ||
        a.EndsWith(".mhproj", StringComparison.OrdinalIgnoreCase);

    private static readonly string OotRomPath = Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
    private static readonly string MmRomPath  = Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).z64");
    // gc-eu-mq-dbg "THE LEGEND OF DEBUG" — boots with map-select and takes the no-dma InjectDebug path.
    private static readonly string DebugRomPath = Editor.AppPaths.Rom(@"ZELOOTD.z64");

    // MM EU debug build (uncompressed, like the OoT debug ROM) — for the MM no-dma InjectDebug path.
    private static readonly string MmDebugRomPath =
        Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (Europe) (En,Fr,De,Es) (Debug Version).n64");

    /// <summary>Builds a brush-texture resolver (rom_{file}_{offset} → bitmap) from the OoT or MM ROM.
    /// Returns null (untextured) if the ROM is missing or the asset scan fails — the exporters and the
    /// engine gfx path both handle a null/missing texture gracefully.</summary>
    public static Func<string, System.Drawing.Bitmap?>? BuildRomTexResolver(bool mm)
    {
        string romPath = mm ? MmRomPath : OotRomPath;
        if (!File.Exists(romPath)) { Console.WriteLine($"[tex] ROM not found: {romPath}"); return null; }
        try
        {
            var rom = new RomImage(romPath);
            var lib = new TextureLibrary();
            var src = new RomTextureSource(rom);
            var map = RomAssetIndex.BuildMap(rom);
            var (allTex, allFolders) = SceneTextureMapper.Build(rom, src.Scan(), map);
            lib.AddRomTextures(allTex, src, map.FileScene, allFolders);
            Console.WriteLine($"[tex] {(mm ? "MM" : "OoT")} ROM: {allTex.Count} textures indexed");
            return n => lib.Find(n)?.Image;
        }
        catch (Exception ex) { Console.WriteLine($"[tex] {(mm ? "MM" : "OoT")} scan failed ({ex.GetType().Name}: {ex.Message}); untextured"); return null; }
    }

    /// <summary>Headless N64/PJ64 parity pack: builds the Test Temple as raw N64 scene+room binaries
    /// (textured DLs, collision, actors, doors, lighting — same scene info the SoH/2Ship O2R carries),
    /// injects it into the debug ROM, writes mh_playtest.z64, and reports texture/scene parity stats.
    /// Run: MegatonHammer --packplaytestn64 [outDir]</summary>
    public static void RunN64(string[] args)
    {
        string outDir = args.Length >= 2 && !IsFlag(args[1]) ? args[1]
            : Path.Combine(Path.GetTempPath(), "MegatonHammer");
        Directory.CreateDirectory(outDir);

        // An explicit .mhproj packs THAT project (for auditing a real scene); else the Test Temple.
        string? projArg = args.FirstOrDefault(a => a.EndsWith(".mhproj", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        var doc = new MapDocument();
        if (projArg != null) { ProjectSerializer.Load(doc, projArg); Console.WriteLine($"packing project: {projArg}"); }
        else
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "mh_packplaytest_n64");
            Directory.CreateDirectory(tempDir);
            TestTempleBuilder.Build(tempDir);
            ProjectSerializer.Load(doc, Path.Combine(tempDir, "Test_Temple.mhproj"));
        }

        bool mm = args.Any(a => a.Equals("mm", StringComparison.OrdinalIgnoreCase));
        var texResolver = BuildRomTexResolver(mm: mm);
        var (sceneBytes, roomBytes) = Export.SceneExporter.BuildBinaries(
            doc.Scene, texResolver, Export.ActorObjectResolver.Build(mm: mm));

        // Parity report: per-room textured-DL stats (proves textures are actually emitted, not just packed).
        int totalTexBytes = 0, totalTexCount = 0;
        for (int i = 0; i < doc.Scene.Rooms.Count; i++)
        {
            var dl = Export.DisplayListBuilder.Build(doc.Scene.Rooms[i], 0x03, 0, texResolver);
            int texCount = dl.TextureData.Length > 0 ? 1 : 0;   // contiguous texture block per room
            totalTexBytes += dl.TextureData.Length; totalTexCount += dl.TextureData.Length > 0 ? 1 : 0;
        }
        int transitions = doc.Scene.Rooms.SelectMany(r => r.Actors).Count(a => a.IsTransitionActor);
        int actors = doc.Scene.Rooms.Sum(r => r.Actors.Count);

        Console.WriteLine($"N64 scene: {sceneBytes.Length} bytes, {roomBytes.Count} room(s)");
        Console.WriteLine($"  textures: {totalTexBytes} bytes of RGBA16 across {totalTexCount} room texture block(s)");
        Console.WriteLine($"  scene info: collision+lighting+skybox, {actors} actor(s), {transitions} transition/door actor(s)");
        Console.WriteLine($"  resolver: {(texResolver != null ? "ROM textures" : "untextured")}");

        string romPath = mm ? MmDebugRomPath : DebugRomPath;
        if (File.Exists(romPath))
        {
            var baseRom = new RomImage(romPath);
            // OoT: SCENE_TEST01 map-select slot (0x65). MM: KAKUSIANA grotto (0x07) — a simple single-room
            // scene reachable via ENTRANCE(GROTTOS,0)=0x1400 (map-select "KAKUSIANA 0"), so the hook can warp.
            int slot = mm ? 0x07 : 0x65;
            // Authored dialogue (OoT N64): overwrite each bank message's textId text in place.
            var msgs = mm ? null
                : doc.Scene.Messages.Select(m => (m.Id, Export.MessageEncoder.EncodeOoT(m.Text))).ToList();
            var result = Rom.RomInjector.InjectDebug(baseRom, sceneBytes, roomBytes, slot, mm: mm, messages: msgs);
            byte[] romBytes = result.Rom;

            // Mirror the GUI N64 path so the headless audit exercises the real inventory pipeline: apply
            // the Default inventory as SaveContext pokes (mh_n64_save.txt) + write the hook params, and for
            // OoT apply the gc-eu-mq-dbg auto-boot detour. entrance: OoT SCENE_TEST01_0=0x0094, MM grotto.
            string pdir = Path.Combine(Path.GetTempPath(), "MegatonHammer");
            Directory.CreateDirectory(pdir);
            var inv = PlaytestInventory.Default(oot: !mm);
            var pokes = mm ? N64SavePokes.ComputeMM(inv, "custom", out _) : N64SavePokes.ComputeOoT(inv, "custom", out _);
            File.WriteAllText(Path.Combine(pdir, "mh_n64_save.txt"), N64SavePokes.Format(pokes, oot: !mm));
            int dbg = EditorSettings.PlaytestN64DebugControls ? 1 : 0;
            if (mm)
            {
                File.WriteAllText(Path.Combine(pdir, "mh_n64_playtest.txt"), $"inventory=1\ndebug={dbg}\n");
                Console.WriteLine($"  MM hook params: inventory=1 (custom Default) debug={dbg}; {pokes.Count} save pokes");
            }
            else
            {
                int entrance = 0x0094;   // ENTR_TEST01_0 (slot 0x65 SCENE_TEST01)
                bool recog = Rom.OotDebugAutoBoot.IsRecognized(baseRom.Data);
                bool patched = recog && Rom.OotDebugAutoBoot.Patch(romBytes, entrance);
                Console.WriteLine($"  OoT auto-boot: recognized={recog} patched={patched}" +
                                  (patched ? $" (entrance 0x{entrance:X4})" : " — fork will warp via params when gameplay is reached"));
                File.WriteAllText(Path.Combine(pdir, "mh_n64_playtest.txt"),
                    $"entrance={entrance}\nage=1\ninventory=1\nscene={slot}\ntimeOfDay=32768\ndebug={dbg}\n");
                Console.WriteLine($"  OoT hook params: entrance=0x{entrance:X4} inventory=1 (custom Default) debug={dbg}; {pokes.Count} save pokes");
            }
            string romOut = Path.Combine(outDir, mm ? "mh_playtest_mm.z64" : "mh_playtest.z64");
            File.WriteAllBytes(romOut, romBytes);
            Console.WriteLine($"injected -> {romOut} ({romBytes.Length} bytes, debug/no-dma, slot 0x{slot:X2})");
            Console.WriteLine($"  scene table @ 0x{result.SceneTableOffset:X} (located={result.SceneTableOffset >= 0}), " +
                              $"repointed={result.Repointed}, sceneVrom=0x{result.SceneVrom:X8}");
            Console.WriteLine(result.Repointed
                ? $"[n64{(mm ? "-mm" : "")}] PASS — scene table located and slot 0x{slot:X2} repointed."
                : $"[n64{(mm ? "-mm" : "")}] FAIL — scene table not located / slot not repointed.");
        }
        else Console.WriteLine($"[n64] debug ROM not found ({romPath}); skipped ROM injection (binaries built OK).");
    }

    /// <summary>
    /// End-to-end test of the editor->PJ64 playtest + LOGGING pipeline: loads a real .mhproj, runs the
    /// actual Project64Playtest.Launch (which injects, writes the fork params/save pokes, launches PJ64,
    /// and links the playtest log to BOTH the fork log and PJ64's own trace), with MH_MAXFRAMES so PJ64
    /// self-exits. Then prints the produced playtest log and asserts it captured every expected section.
    /// Run: MegatonHammer --testplaytestlog [oot|mm] [path\to.mhproj]
    /// </summary>
    public static void TestPlaytestLog(string[] args)
    {
        bool mm = args.Any(a => a.Equals("mm", StringComparison.OrdinalIgnoreCase));
        string proj = args.FirstOrDefault(a => a.EndsWith(".mhproj", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
                      ?? (mm ? System.IO.Path.Combine(Editor.AppPaths.BaseDir, @"megaton_mhprojs\mmtestwoods.mhproj")
                            : System.IO.Path.Combine(Editor.AppPaths.BaseDir, @"megaton_mhprojs\test.mhproj"));
        if (!File.Exists(proj)) { Console.WriteLine($"[playtestlog] project not found: {proj}"); return; }

        string rom = mm ? Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).z64")
                        : Editor.AppPaths.Rom(@"ZELOOTMA.Z64");
        var config = new GameConfig
        {
            Mode = mm ? GameMode.MajorasMask : GameMode.OcarinaOfTime,
            RomPath = File.Exists(rom) ? rom : null,
        };

        var doc = new MapDocument();
        ProjectSerializer.Load(doc, proj);
        Console.WriteLine($"[playtestlog] loaded {proj} ({(mm ? "MM" : "OoT")}); rooms={doc.Scene.Rooms.Count}");

        // Make sure logging is enabled + ensure the engine self-exits so the link thread finalizes the log.
        if (EditorSettings.PlaytestLogMax == 0) EditorSettings.PlaytestLogMax = 50;
        Environment.SetEnvironmentVariable("MH_MAXFRAMES", "360");   // inherited by the launched PJ64
        var texResolver = BuildRomTexResolver(mm: mm);

        using var owner = new Form { Visible = false, ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
        _ = owner.Handle;   // realize the handle for use as a dialog owner (no dialogs expected on good ROMs)

        // Report which textures the N64 builder flags as cutout (alpha-tested transparency).
        DisplayListBuilder.LastCutoutTextures.Clear();
        _ = Export.SceneExporter.BuildBinaries(doc.Scene, texResolver, Export.ActorObjectResolver.Build(mm: mm), mm: mm);
        var cut = DisplayListBuilder.LastCutoutTextures;
        Console.WriteLine($"[playtestlog] cutout (alpha-tested) textures: {(cut.Count == 0 ? "(none detected)" : string.Join(", ", cut))}");

        int slot = mm ? 0x2D : 0x65;
        Console.WriteLine($"[playtestlog] launching real Project64Playtest.Launch (slot 0x{slot:X2}) ...");
        Forms.Project64Playtest.Launch(owner, config, doc.Scene, slot, playtest: null, texResolver: texResolver);

        // Launch returns immediately (PJ64 runs on its own; the link thread tails until PJ64 exits at
        // MH_MAXFRAMES). Wait for the engine to run + exit + the link thread to finalize the log.
        string logFile = Path.Combine(PlaytestLog.LogDir, "playtest_latest.log");
        Console.WriteLine($"[playtestlog] waiting for PJ64 to run {360} frames + log finalize ...");
        for (int i = 0; i < 60; i++)
        {
            System.Threading.Thread.Sleep(1000);
            if (File.Exists(logFile) && File.ReadAllText(logFile).Contains("PLAYTEST LOG ENDED")) break;
        }
        Environment.SetEnvironmentVariable("MH_MAXFRAMES", null);

        if (!File.Exists(logFile)) { Console.WriteLine("[playtestlog] FAIL — no playtest log produced"); return; }
        string log = File.ReadAllText(logFile);

        Console.WriteLine("\n================= PRODUCED PLAYTEST LOG =================");
        Console.WriteLine(log.Length > 9000 ? log.Substring(0, 4500) + "\n   ... [middle elided] ...\n" + log.Substring(log.Length - 4000) : log);
        Console.WriteLine("================= END PLAYTEST LOG =================\n");

        // Assert the log captured every part of the injection + playtest pipeline.
        (string label, string needle)[] checks =
        {
            ("config dump",        "PLAYTEST CONFIG"),
            ("inventory dump",     "INVENTORY"),
            ("scene settings",     "SCENE SETTINGS"),
            ("injection manifest", "INJECTION MANIFEST"),
            ("rdb status",         "Project64.rdb"),
            ("fork params file",   "fork params"),
            ("engine link",        "ENGINE LINK"),
            ("fork log tail",      "[mh_n64_playtest]"),
            ("PJ64 trace tail",    "[Project64]"),
            ("clean stop",         "PLAYTEST LOG ENDED"),
        };
        Console.WriteLine("[playtestlog] section coverage:");
        int pass = 0;
        foreach (var (label, needle) in checks)
        {
            bool ok = log.Contains(needle, StringComparison.OrdinalIgnoreCase);
            if (ok) pass++;
            Console.WriteLine($"   [{(ok ? "x" : " ")}] {label,-20} ({needle})");
        }
        bool boot = log.Contains("alive frame", StringComparison.OrdinalIgnoreCase) || log.Contains("gameMode", StringComparison.OrdinalIgnoreCase);
        bool crash = log.Contains("!! [", StringComparison.Ordinal);
        Console.WriteLine($"   engine booted (frames/gameMode seen): {boot};  crash markers present: {crash}");
        Console.WriteLine($"[playtestlog] {(mm ? "MM" : "OoT")} RESULT: {pass}/{checks.Length} sections; log = {logFile}");
    }
}
