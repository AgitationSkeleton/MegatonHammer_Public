using MegatonHammer.Editor;
using MegatonHammer.Export;
using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Verifies the ROM-injection pipeline against the OoT gc-eu-mq-dbg DEBUG ROM (ZELOOTMA) — the
/// ROM the PJ64 playtest hook targets (its gSaveContext/map-select addresses are debug-only) and
/// the one the user wants playtests to use. Retail-decompressed ROMs hang in early boot under the
/// headless interpreter; the already-decompressed debug ROM boots cleanly, so this re-targets the
/// dungeon injection at it. Reports each pipeline step (identify, dmadata, scene table, inject) and
/// writes a playable ROM. Run: MegatonHammer --testdebugrom [romPath]
/// </summary>
public static class DebugRomTest
{
    private static readonly string DefaultRom = Editor.AppPaths.Rom(@"ZELOOTMA.Z64");
    private static readonly string OutDir = System.IO.Path.Combine(Editor.AppPaths.BaseDir, @"out\dungeon");

    public static void Run(string[] args)
    {
        string romPath = args.Length >= 2 ? args[1] : DefaultRom;
        Console.WriteLine($"== Debug-ROM injection test ==\n  ROM: {romPath}");
        if (!File.Exists(romPath)) { Console.WriteLine("  FAIL ROM not found"); return; }

        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) { pass++; Console.WriteLine($"  PASS {what}"); } else { fail++; Console.WriteLine($"  FAIL {what}"); } }

        // 1. Identify + dmadata.
        var rom = new RomImage(romPath);
        Console.WriteLine($"  internal name : \"{rom.InternalName}\"");
        Console.WriteLine($"  game          : {rom.Game}");
        Console.WriteLine($"  dma table off : 0x{rom.DmaTableOffset:X} ({rom.Files.Count} files)");
        Check(rom.Game == RomGame.OoT, "identified as OoT (cart code ZL fallback)");
        Check(rom.DmaTableOffset > 0, "dmadata table located");
        Check(rom.Files.Count > 1000, $"file table parsed ({rom.Files.Count} files)");

        // 2. Decompress/flatten (auto-trims the +overdump to the max file boundary).
        var flat = RomBuilder.Decompress(rom);
        Console.WriteLine($"  flat size     : {flat.Data.Length / 1024 / 1024} MB ({flat.Data.Length} bytes)");
        Console.WriteLine($"  spare dma     : {RomBuilder.SpareDmaSlots(flat)} slots");
        Check(flat.Data.Length is > 0x3000000 and <= 0x4000000, "flat image ~64MB (overdump trimmed)");
        Check(RomBuilder.SpareDmaSlots(flat) >= 5, "enough spare dmadata slots for scene+rooms");

        // 3. Scene table.
        var loc = SceneTableLocator.Find(flat.Data, flat.Files);
        Console.WriteLine($"  scene table   : off=0x{loc.Offset:X} count={loc.Count}");
        Check(loc.Offset > 0, "gSceneTable located by fingerprint");
        if (loc.Offset > 0)
        {
            int slot = loc.Offset + 0x52 * SceneTableLocator.EntrySize;
            uint vs = U32(flat.Data, slot), ve = U32(flat.Data, slot + 4);
            Console.WriteLine($"  scene 0x52 cur: vrom 0x{vs:X8}..0x{ve:X8} (Kakariko in retail layout)");
            Check(vs > 0 && ve > vs, "scene slot 0x52 has a valid current entry");
        }

        // 3b. Round-trip ONLY (Decompress -> checksum -> pad, NO injection). Isolates whether the
        // decompression itself produces a non-booting ROM vs the scene injection/append/repoint.
        {
            var rt = RomBuilder.Decompress(rom);
            int target = 0x2000000; while (target < rt.Data.Length) target <<= 1;
            var padded = new byte[target];
            Array.Copy(rt.Data, padded, rt.Data.Length);
            for (int i = rt.Data.Length; i < target; i++) padded[i] = 0xFF;
            OotCrc.Update(padded);
            Directory.CreateDirectory(OutDir);
            string rtPath = Path.Combine(OutDir, "mh_dbg_roundtrip.z64");
            File.WriteAllBytes(rtPath, padded);
            Console.WriteLine($"  round-trip ROM: {rtPath} ({padded.Length / 1024 / 1024} MB, no injection)");
        }

        // 3c. MINIMAL scene isolation: one room, one floor, a spawn + a light, NO actors / door /
        // transition / second room. If this loads but the full dungeon hangs, an actor or the
        // transition is the culprit; if this also hangs, the base scene (collision/mesh/spawn) is.
        try
        {
            var min = new ZScene("MH Minimal");
            while (min.Rooms.Count < 1) min.AddRoom();
            min.Settings.SpawnPos = new OpenTK.Mathematics.Vector3(0, 10, 0);
            min.Settings.SpawnRoom = 0;
            min.Settings.AreaName = "MH Minimal";
            min.Rooms[0].Geometry.Add(Solid.CreateBox(new(-300, -10, -300), new(300, 0, 300)));
            var (mScene, mRooms) = SceneExporter.BuildBinaries(min, texResolver: null, objResolver: ActorObjectResolver.Build(mm: false));
            var mr = RomInjector.InjectDebug(rom, mScene, mRooms, 0x52);
            string mPath = Path.Combine(OutDir, "mh_dbg_minimal.z64");
            File.WriteAllBytes(mPath, mr.Rom);
            Console.WriteLine($"  minimal ROM   : {mPath} (1 room, floor+spawn+light, no actors)");
        }
        catch (Exception ex) { Console.WriteLine($"  minimal build threw: {ex.Message}"); }

        // 3d. CHEST-ONLY isolation: minimal scene + a single object-dependent actor (En_Box chest,
        // object_box) in one room. No door, no transition actor, no second room. If this loads, the
        // actor+object path works and the door/transition/multi-room is the remaining issue.
        try
        {
            var cs = new ZScene("MH Chest");
            while (cs.Rooms.Count < 1) cs.AddRoom();
            cs.Settings.SpawnPos = new OpenTK.Mathematics.Vector3(0, 10, 0);
            cs.Settings.SpawnRoom = 0; cs.Settings.AreaName = "MH Chest";
            cs.Rooms[0].Geometry.Add(Solid.CreateBox(new(-300, -10, -300), new(300, 0, 300)));
            cs.Rooms[0].Actors.Add(new ZActor { Number = 0x000A, Variable = 0x0001, XPos = 100, YPos = 0, ZPos = 0 });
            var (csScene, csRooms) = SceneExporter.BuildBinaries(cs, texResolver: null, objResolver: ActorObjectResolver.Build(mm: false));
            var cr = RomInjector.InjectDebug(rom, csScene, csRooms, 0x52);
            File.WriteAllBytes(Path.Combine(OutDir, "mh_dbg_chest.z64"), cr.Rom);
            Console.WriteLine($"  chest ROM     : mh_dbg_chest.z64 (1 room, floor + 1 chest)");
        }
        catch (Exception ex) { Console.WriteLine($"  chest build threw: {ex.Message}"); }

        // 3e. TWO-ROOM isolation: 2 rooms (chest in room 0, Stalfos in room 1), NO door, NO transition
        // actor. The game loads only the spawn room at init, so this tests multi-room scene structure
        // + the Stalfos load without the door/transition mechanism.
        try
        {
            var ts = new ZScene("MH 2Room");
            while (ts.Rooms.Count < 2) ts.AddRoom();
            ts.Settings.SpawnPos = new OpenTK.Mathematics.Vector3(0, 10, 0);
            ts.Settings.SpawnRoom = 0; ts.Settings.AreaName = "MH 2Room";
            ts.Rooms[0].Geometry.Add(Solid.CreateBox(new(-300, -10, -300), new(300, 0, 300)));
            ts.Rooms[0].Actors.Add(new ZActor { Number = 0x000A, Variable = 0x0001, XPos = 100, YPos = 0, ZPos = 0 });
            ts.Rooms[1].Geometry.Add(Solid.CreateBox(new(-300, -10, 320), new(300, 0, 900)));
            ts.Rooms[1].Actors.Add(new ZActor { Number = 0x0002, Variable = 0x0000, XPos = 0, YPos = 0, ZPos = 700 });
            var (tsScene, tsRooms) = SceneExporter.BuildBinaries(ts, texResolver: null, objResolver: ActorObjectResolver.Build(mm: false));
            var tr = RomInjector.InjectDebug(rom, tsScene, tsRooms, 0x52);
            File.WriteAllBytes(Path.Combine(OutDir, "mh_dbg_2room.z64"), tr.Rom);
            Console.WriteLine($"  2room ROM     : mh_dbg_2room.z64 (2 rooms, chest + Stalfos, no door)");
        }
        catch (Exception ex) { Console.WriteLine($"  2room build threw: {ex.Message}"); }

        // 3f. BIG-FLOOR isolation: ONE room with a floor spanning the same large Z extent as the
        // 2-room scene combined (-300..900). If this hangs, the collision extent/BgCheck is the issue;
        // if it loads, the hang is multi-room handling (room count), not the collision.
        try
        {
            var bf = new ZScene("MH BigFloor");
            while (bf.Rooms.Count < 1) bf.AddRoom();
            bf.Settings.SpawnPos = new OpenTK.Mathematics.Vector3(0, 10, 0);
            bf.Settings.SpawnRoom = 0; bf.Settings.AreaName = "MH BigFloor";
            bf.Rooms[0].Geometry.Add(Solid.CreateBox(new(-300, -10, -300), new(300, 0, 900)));
            var (bfScene, bfRooms) = SceneExporter.BuildBinaries(bf, texResolver: null, objResolver: ActorObjectResolver.Build(mm: false));
            var bfr = RomInjector.InjectDebug(rom, bfScene, bfRooms, 0x52);
            File.WriteAllBytes(Path.Combine(OutDir, "mh_dbg_bigfloor.z64"), bfr.Rom);
            Console.WriteLine($"  bigfloor ROM  : mh_dbg_bigfloor.z64 (1 room, large floor -300..900)");
        }
        catch (Exception ex) { Console.WriteLine($"  bigfloor build threw: {ex.Message}"); }

        // 3g. TWO-BOX isolation: ONE room with TWO separate floor boxes (same 24-poly collision as the
        // 2-room scene, but a single room). If this hangs, the cause is the collision (two disjoint
        // boxes / poly count); if it loads, the cause is the room count (2 rooms), not the collision.
        try
        {
            var tb = new ZScene("MH 2Box");
            while (tb.Rooms.Count < 1) tb.AddRoom();
            tb.Settings.SpawnPos = new OpenTK.Mathematics.Vector3(0, 10, 0);
            tb.Settings.SpawnRoom = 0; tb.Settings.AreaName = "MH 2Box";
            tb.Rooms[0].Geometry.Add(Solid.CreateBox(new(-300, -10, -300), new(300, 0, 300)));
            tb.Rooms[0].Geometry.Add(Solid.CreateBox(new(-300, -10, 320), new(300, 0, 900)));
            var (tbScene, tbRooms) = SceneExporter.BuildBinaries(tb, texResolver: null, objResolver: ActorObjectResolver.Build(mm: false));
            var tbr = RomInjector.InjectDebug(rom, tbScene, tbRooms, 0x52);
            File.WriteAllBytes(Path.Combine(OutDir, "mh_dbg_2box.z64"), tbr.Rom);
            Console.WriteLine($"  2box ROM      : mh_dbg_2box.z64 (1 room, two disjoint floor boxes)");
        }
        catch (Exception ex) { Console.WriteLine($"  2box build threw: {ex.Message}"); }

        // 3h. STALFOS-IN-SPAWN isolation: 1 room, floor + Stalfos in the SPAWN room (so it actually
        // loads at init). If this hangs, En_Test itself is the problem; if it loads, En_Test is fine.
        try
        {
            var sf = new ZScene("MH Stalfos");
            while (sf.Rooms.Count < 1) sf.AddRoom();
            sf.Settings.SpawnPos = new OpenTK.Mathematics.Vector3(0, 10, 0);
            sf.Settings.SpawnRoom = 0; sf.Settings.AreaName = "MH Stalfos";
            sf.Rooms[0].Geometry.Add(Solid.CreateBox(new(-300, -10, -300), new(300, 0, 300)));
            sf.Rooms[0].Actors.Add(new ZActor { Number = 0x0002, Variable = 0x0000, XPos = 0, YPos = 0, ZPos = 150 });
            var (sfScene, sfRooms) = SceneExporter.BuildBinaries(sf, texResolver: null, objResolver: ActorObjectResolver.Build(mm: false));
            File.WriteAllBytes(Path.Combine(OutDir, "mh_dbg_stalfos.z64"), RomInjector.InjectDebug(rom, sfScene, sfRooms, 0x52).Rom);
            Console.WriteLine($"  stalfos ROM   : mh_dbg_stalfos.z64 (1 room, floor + Stalfos in spawn room)");
        }
        catch (Exception ex) { Console.WriteLine($"  stalfos build threw: {ex.Message}"); }

        // 3i. TWO-ROOM-EMPTY isolation: 2 rooms, room 0 = chest (spawn), room 1 = bare floor (no actors,
        // no object list). If this hangs, it's pure room-count; if it loads, room 1's content matters.
        try
        {
            var re = new ZScene("MH 2RoomEmpty");
            while (re.Rooms.Count < 2) re.AddRoom();
            re.Settings.SpawnPos = new OpenTK.Mathematics.Vector3(0, 10, 0);
            re.Settings.SpawnRoom = 0; re.Settings.AreaName = "MH 2RoomEmpty";
            re.Rooms[0].Geometry.Add(Solid.CreateBox(new(-300, -10, -300), new(300, 0, 300)));
            re.Rooms[0].Actors.Add(new ZActor { Number = 0x000A, Variable = 0x0001, XPos = 100, YPos = 0, ZPos = 0 });
            re.Rooms[1].Geometry.Add(Solid.CreateBox(new(-300, -10, 320), new(300, 0, 900)));
            var (reScene, reRooms) = SceneExporter.BuildBinaries(re, texResolver: null, objResolver: ActorObjectResolver.Build(mm: false));
            File.WriteAllBytes(Path.Combine(OutDir, "mh_dbg_2roomempty.z64"), RomInjector.InjectDebug(rom, reScene, reRooms, 0x52).Rom);
            Console.WriteLine($"  2roomempty ROM: mh_dbg_2roomempty.z64 (2 rooms, room 1 bare)");
        }
        catch (Exception ex) { Console.WriteLine($"  2roomempty build threw: {ex.Message}"); }

        // 3j. NON-SPOT TEST-SCENE target (slot 0x65 = SCENE_TEST01, map-select "118:Test Map"). Test01
        // has NO entrance cutscene (fixes the Link paralysis) and is NOT a "Spot" overworld scene
        // (Kakariko is) — Spot scenes use a smaller BgCheck budget + special room handling, a prime
        // suspect for the multi-room hang. These two probe whether the Test slot fixes both.
        try
        {
            var c1 = new ZScene("MH T1 Chest");
            while (c1.Rooms.Count < 1) c1.AddRoom();
            c1.Settings.SpawnPos = new OpenTK.Mathematics.Vector3(0, 10, 0); c1.Settings.SpawnRoom = 0; c1.Settings.AreaName = "MH T1 Chest";
            c1.Rooms[0].Geometry.Add(Solid.CreateBox(new(-300, -10, -300), new(300, 0, 300)));
            c1.Rooms[0].Actors.Add(new ZActor { Number = 0x000A, Variable = 0x0001, XPos = 100, YPos = 0, ZPos = 0 });
            var (c1s, c1r) = SceneExporter.BuildBinaries(c1, texResolver: null, objResolver: ActorObjectResolver.Build(mm: false));
            File.WriteAllBytes(Path.Combine(OutDir, "mh_test01_chest.z64"), RomInjector.InjectDebug(rom, c1s, c1r, 0x65).Rom);

            // Thick-floor + higher-spawn variant: floor 100 units thick (y -100..0), Link spawns at
            // y=50, chest sits up at y=20. Tests whether the thin (10u) floor at exactly y=0 is why
            // Link is pinned / actors fall through.
            var ck = new ZScene("MH Thick");
            while (ck.Rooms.Count < 1) ck.AddRoom();
            ck.Settings.SpawnPos = new OpenTK.Mathematics.Vector3(0, 50, 0); ck.Settings.SpawnRoom = 0; ck.Settings.AreaName = "MH Thick";
            ck.Rooms[0].Geometry.Add(Solid.CreateBox(new(-400, -100, -400), new(400, 0, 400)));
            ck.Rooms[0].Actors.Add(new ZActor { Number = 0x000A, Variable = 0x0001, XPos = 100, YPos = 20, ZPos = 0 });
            var (cks, ckr) = SceneExporter.BuildBinaries(ck, texResolver: null, objResolver: ActorObjectResolver.Build(mm: false));
            File.WriteAllBytes(Path.Combine(OutDir, "mh_test01_thick.z64"), RomInjector.InjectDebug(rom, cks, ckr, 0x65).Rom);

            // Disambiguate: was it floor THICKNESS or SPAWN HEIGHT? Two controlled variants.
            ZScene MkFloor(string nm, float spawnY, float floorBottom) {
                var s = new ZScene(nm); while (s.Rooms.Count < 1) s.AddRoom();
                s.Settings.SpawnPos = new OpenTK.Mathematics.Vector3(0, spawnY, 0); s.Settings.SpawnRoom = 0; s.Settings.AreaName = nm;
                s.Rooms[0].Geometry.Add(Solid.CreateBox(new(-400, floorBottom, -400), new(400, 0, 400)));
                return s;
            }
            var thinHigh = MkFloor("MH ThinHigh", 50, -10);   // thin floor, high spawn
            var thickLow = MkFloor("MH ThickLow", 10, -100);  // thick floor, low spawn
            var (ths, thr) = SceneExporter.BuildBinaries(thinHigh, null, ActorObjectResolver.Build(false));
            var (tls, tlr) = SceneExporter.BuildBinaries(thickLow, null, ActorObjectResolver.Build(false));
            File.WriteAllBytes(Path.Combine(OutDir, "mh_test01_thinhigh.z64"), RomInjector.InjectDebug(rom, ths, thr, 0x65).Rom);
            File.WriteAllBytes(Path.Combine(OutDir, "mh_test01_thicklow.z64"), RomInjector.InjectDebug(rom, tls, tlr, 0x65).Rom);

            var t2 = new ZScene("MH T1 2Room");
            while (t2.Rooms.Count < 2) t2.AddRoom();
            t2.Settings.SpawnPos = new OpenTK.Mathematics.Vector3(0, 10, 0); t2.Settings.SpawnRoom = 0; t2.Settings.AreaName = "MH T1 2Room";
            t2.Rooms[0].Geometry.Add(Solid.CreateBox(new(-300, -10, -300), new(300, 0, 300)));
            t2.Rooms[0].Actors.Add(new ZActor { Number = 0x000A, Variable = 0x0001, XPos = 100, YPos = 0, ZPos = 0 });
            t2.Rooms[1].Geometry.Add(Solid.CreateBox(new(-300, -10, 320), new(300, 0, 900)));
            var (t2s, t2r) = SceneExporter.BuildBinaries(t2, texResolver: null, objResolver: ActorObjectResolver.Build(mm: false));
            File.WriteAllBytes(Path.Combine(OutDir, "mh_test01_2room.z64"), RomInjector.InjectDebug(rom, t2s, t2r, 0x65).Rom);
            Console.WriteLine($"  TEST01 ROMs   : mh_test01_chest.z64 + mh_test01_2room.z64 (slot 0x65, map-select \"118:Test Map\")");
        }
        catch (Exception ex) { Console.WriteLine($"  test01 build threw: {ex.Message}"); }

        // 4. Build the dungeon + inject.
        var scene = BuildDungeon();
        var (sceneBytes, rooms) = SceneExporter.BuildBinaries(scene, texResolver: null, objResolver: ActorObjectResolver.Build(mm: false));
        Console.WriteLine($"  dungeon       : scene {sceneBytes.Length}B, {rooms.Count} rooms");

        // 4a. APPEND-ONLY isolation: append the scene+room files (+ dmadata entries) but pass
        // targetSceneId=-1 so the scene-table slot 0x52 is NOT repointed (Kakariko stays intact). If
        // this boots but the repointed one hangs, the game loads slot 0x52 at boot (our scene content
        // is the fault); if this also hangs, the append/dmadata mechanism itself breaks boot.
        try
        {
            var ao = RomInjector.Inject(rom, sceneBytes, rooms, -1, null);
            string aoPath = Path.Combine(OutDir, "mh_dbg_appendonly.z64");
            File.WriteAllBytes(aoPath, ao.Rom);
            Console.WriteLine($"  append-only ROM: {aoPath} ({ao.Rom.Length / 1024 / 1024} MB, no repoint) — {ao.Message}");
        }
        catch (Exception ex) { Console.WriteLine($"  append-only inject threw: {ex.Message}"); }
        try
        {
            // Debug-ROM path: write to free space, NO dmadata entries (gc-eu-mq-dbg DmaMgr_Init crashes
            // if the dma table grows — see RomInjector.InjectDebug).
            var result = RomInjector.InjectDebug(rom, sceneBytes, rooms, 0x52);
            Console.WriteLine($"  inject        : {result.Message}");
            Console.WriteLine($"  out ROM size  : {result.Rom.Length / 1024 / 1024} MB");
            Check(result.Repointed, "scene table slot 0x52 repointed to injected scene");
            Check(result.Rom.Length == 0x4000000, "output ROM is exactly 64MB (matches debug ROM)");

            Directory.CreateDirectory(OutDir);
            string outRom = Path.Combine(OutDir, "mh_testdungeon_dbg.z64");
            File.WriteAllBytes(outRom, result.Rom);
            Console.WriteLine($"\n  wrote: {outRom}");
        }
        catch (Exception ex) { Check(false, $"inject threw: {ex.Message}"); }

        Console.WriteLine($"\n==== {(fail == 0 ? "ALL PASS" : $"{fail} FAILED")} ({pass} passed) ====");
    }

    // Same 2-room dungeon as DungeonTest, inlined to keep this test self-contained.
    private static ZScene BuildDungeon()
    {
        var scene = new ZScene("MH Test Dungeon");
        while (scene.Rooms.Count < 2) scene.AddRoom();
        scene.Settings.SpawnPos = new OpenTK.Mathematics.Vector3(0, 10, 0);
        scene.Settings.SpawnRoom = 0;
        scene.Settings.AreaName = "MH Test Dungeon";
        var r0 = scene.Rooms[0];
        r0.Geometry.Add(Solid.CreateBox(new(-300, -10, -300), new(300, 0, 300)));
        r0.Actors.Add(new ZActor { Number = 0x000A, Variable = 0x0001, XPos = 100, YPos = 0, ZPos = 0 });
        r0.Actors.Add(new ZActor { Number = 0x0009, Variable = 0x0000, XPos = 0, YPos = 0, ZPos = 290 });
        r0.Actors.Add(new ZActor { Number = 0x002E, IsTransition = true, FrontRoom = 0, BackRoom = 1, XPos = 0, YPos = 0, ZPos = 300 });
        var r1 = scene.Rooms[1];
        r1.Geometry.Add(Solid.CreateBox(new(-300, -10, 320), new(300, 0, 900)));
        r1.Actors.Add(new ZActor { Number = 0x0002, Variable = 0x0000, XPos = 0, YPos = 0, ZPos = 700 });
        return scene;
    }

    private static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
}
