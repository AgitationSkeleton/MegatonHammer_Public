using MegatonHammer.Editor;
using MegatonHammer.Export;
using MegatonHammer.Rom;
using OpenTK.Mathematics;

namespace MegatonHammer.SelfTest;

/// <summary>
/// End-to-end "can it build a working dungeon" verification (blocker #3). Programmatically authors a
/// small 2-room dungeon exercising every load-bearing feature — spawn, textured geometry, an
/// object-dependent actor (chest), a door, an enemy, a void-out floor, a climbable wall, an exit
/// trigger, and a room→room transition — then exports it through the real ROM pipeline, structurally
/// verifies the output (object lists, surface-type table, room links, spawn, exit), injects a playable
/// .z64, and prints the exact steps to confirm it in-engine. Run: MegatonHammer --testdungeon
/// </summary>
public static class DungeonTest
{
    private const string OotRom = @"D:\Copilot_OOT\READ_ONLY_GameROMs\Legend of Zelda, The - Ocarina of Time (USA).z64";
    private const string OutDir = @"D:\Copilot_OOT\WorkFolders\MegatonHammer\out\dungeon";

    public static void Run()
    {
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) { pass++; Console.WriteLine($"  PASS {what}"); } else { fail++; Console.WriteLine($"  FAIL {what}"); } }

        var scene = BuildDungeon();
        var objRes = ActorObjectResolver.Build(mm: false);

        var (sceneBytes, rooms) = SceneExporter.BuildBinaries(scene, texResolver: null, objResolver: objRes);
        Check(rooms.Count == 2, $"scene built with 2 rooms (got {rooms.Count})");

        // Room 0 must list object_box (the chest's object) in its 0x0B list.
        ushort boxObj = objRes(0x000A) ?? 0;
        Check(boxObj > 0 && RoomLists(rooms[0]).objects.Contains(boxObj), "room 0 object list contains object_box (chest)");
        // Room 1 must list the Stalfos object (En_Test → object_sk2).
        ushort skObj = objRes(0x0002) ?? 0;
        Check(skObj > 0 && RoomLists(rooms[1]).objects.Contains(skObj), "room 1 object list contains the enemy's object");
        // Each room has its actor list.
        Check(RoomLists(rooms[0]).actorCount >= 2, "room 0 has its actors (chest + door)");
        Check(RoomLists(rooms[1]).actorCount >= 1, "room 1 has its actors (enemy)");

        // Scene: 2-room map list (0x04), a spawn (0x00), a transition list (0x0E), an exit list (0x13).
        var cmds = SceneCmds(sceneBytes);
        Check(cmds.TryGetValue(0x04, out var rl) && sceneBytes[rl + 1] == 2, "scene room list has 2 rooms");
        Check(cmds.ContainsKey(0x00), "scene has a spawn list (0x00)");
        Check(cmds.ContainsKey(0x0E), "scene has a transition-actor list (0x0E, the door between rooms)");
        Check(cmds.ContainsKey(0x13), "scene has an exit list (0x13, the dungeon exit)");

        // Collision surface-type table carries the void + climb words.
        Check(cmds.TryGetValue(0x03, out var colCmd), "scene has a collision header (0x03)");
        if (cmds.ContainsKey(0x03))
        {
            int colOff = Seg(sceneBytes, colCmd + 4);
            int stOff = Seg(sceneBytes, colOff + 0x1C);
            var words = new List<uint>();
            for (int i = 0; i < 8 && stOff + i * 8 + 4 <= sceneBytes.Length; i++) words.Add(U32(sceneBytes, stOff + i * 8));
            Check(words.Contains(0x30000000u), "collision table has the void-out floor word");
            Check(words.Contains(0x00200000u), "collision table has the climbable-wall word");
        }

        // Inject a playable ROM.
        bool injected = false;
        if (File.Exists(OotRom))
        {
            try
            {
                Directory.CreateDirectory(OutDir);
                var baseRom = new RomImage(OotRom);
                var result = RomInjector.Inject(baseRom, sceneBytes, rooms, 0x52, "MH Test Dungeon");
                string outRom = Path.Combine(OutDir, "mh_testdungeon.z64");
                RomSafety.GuardWrite(outRom);
                File.WriteAllBytes(outRom, result.Rom);
                injected = true;
                Console.WriteLine($"\n  wrote playable ROM: {outRom} ({result.Rom.Length / 1024 / 1024} MB) — {result.Message}");
            }
            catch (Exception ex) { Console.WriteLine($"  ROM inject skipped: {ex.Message}"); }
        }
        else Console.WriteLine("  (base OoT ROM not found — skipped .z64 injection)");

        Console.WriteLine($"\n==== {(fail == 0 ? "ALL STRUCTURAL CHECKS PASS" : $"{fail} FAILED")} ({pass} passed) ====");
        PrintRunSteps(injected);
    }

    private static ZScene BuildDungeon()
    {
        var scene = new ZScene("MH Test Dungeon");
        while (scene.Rooms.Count < 2) scene.AddRoom();
        scene.Settings.SpawnPos = new Vector3(0, 10, 0);
        scene.Settings.SpawnRoom = 0;
        scene.Settings.AreaName = "MH Test Dungeon";

        // ── Room 0: entry room — floor, 4 walls (one climbable), a chest, a door to room 1 ──
        var r0 = scene.Rooms[0];
        r0.Geometry.Add(Floor(-300, -300, 300, 300));                                   // floor
        r0.Geometry.Add(WallX(-300, 300, -300, climb: false));                          // back wall
        var climb = WallX(-300, 300, 300, climb: true); r0.Geometry.Add(climb);          // front wall (climbable)
        r0.Actors.Add(new ZActor { Number = 0x000A, Variable = 0x0001, XPos = 100, YPos = 0, ZPos = 0 });   // En_Box (chest)
        r0.Actors.Add(new ZActor { Number = 0x0009, Variable = 0x0000, XPos = 0, YPos = 0, ZPos = 290, YRot = 0 }); // En_Door
        // Room→room transition (the door plane): scene-level 0x0E, links room 0 ↔ room 1.
        r0.Actors.Add(new ZActor { Number = 0x002E, IsTransition = true, FrontRoom = 0, BackRoom = 1, FrontEffect = 0, BackEffect = 0, XPos = 0, YPos = 0, ZPos = 300 });

        // ── Room 1: a void pit, a climbable wall, an enemy, and the dungeon exit ──
        var r1 = scene.Rooms[1];
        r1.Geometry.Add(Floor(-300, 320, 300, 900));                                    // floor
        var pit = Floor(-100, 500, 100, 700); pit.SurfaceData0 = 0x30000000;             // void-out pit
        r1.Geometry.Add(pit);
        var climb2 = WallX(-300, 320, 900, climb: true); r1.Geometry.Add(climb2);         // far wall (climbable)
        r1.Actors.Add(new ZActor { Number = 0x0002, Variable = 0x0000, XPos = 0, YPos = 0, ZPos = 700 });   // En_Test (Stalfos enemy)
        var exit = Floor(-100, 820, 100, 900); exit.IsTrigger = true; exit.ExitEntrance = 0; // walk-into exit
        r1.Geometry.Add(exit);
        return scene;
    }

    // Floor brushes are 60 units thick (top at y=0): a razor-thin floor (<~30u, Link's downward
    // collision extent) pins Link and lets actors fall through — verified in-engine.
    private static Solid Floor(float x0, float z0, float x1, float z1)
        => Solid.CreateBox(new Vector3(MathF.Min(x0, x1), -60, MathF.Min(z0, z1)), new Vector3(MathF.Max(x0, x1), 0, MathF.Max(z0, z1)));
    private static Solid WallX(float x0, float x1, float z, bool climb)
    {
        var s = Solid.CreateBox(new Vector3(MathF.Min(x0, x1), 0, z - 10), new Vector3(MathF.Max(x0, x1), 200, z + 10));
        if (climb) s.SurfaceData0 = 0x00200000;   // WALL_TYPE_1: climbable / ledge grab
        return s;
    }

    // ── Structural readers ──
    private static (int actorCount, List<ushort> objects) RoomLists(byte[] room)
    {
        int actorCount = 0; var objs = new List<ushort>();
        for (int p = 0; p + 8 <= room.Length; p += 8)
        {
            if (room[p] == 0x14) break;
            if (room[p] == 0x01) actorCount = room[p + 1];
            if (room[p] == 0x0B)
            {
                int n = room[p + 1], off = Seg(room, p + 4);
                for (int i = 0; i < n && off + i * 2 + 2 <= room.Length; i++) objs.Add((ushort)((room[off + i * 2] << 8) | room[off + i * 2 + 1]));
            }
        }
        return (actorCount, objs);
    }
    private static Dictionary<byte, int> SceneCmds(byte[] s)
    {
        var d = new Dictionary<byte, int>();
        for (int p = 0; p + 8 <= s.Length; p += 8) { if (s[p] == 0x14) break; d[s[p]] = p; }
        return d;
    }
    private static int Seg(byte[] d, int o) => (int)(U32(d, o) & 0xFFFFFF);
    private static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

    private static void PrintRunSteps(bool injected)
    {
        Console.WriteLine(@"
── How to confirm the dungeon plays (the part I can't run for you) ──

A) Vanilla emulator (Project64 / a decompressed-ROM-capable core):
   1. " + (injected ? "Load out/dungeon/mh_testdungeon.z64." : "Build the ROM via the editor's Inject-into-ROM dialog.") + @"
   2. Enable expanded / decompressed ROM in the core settings.
   3. Warp to scene 0x52 (the dungeon was injected into that slot).
   4. Confirm: you spawn in room 0, the chest is openable (object loaded), the
      door leads to room 1, the Stalfos spawns (its object loaded), the climbable
      wall grabs, the void pit reloads you, and the exit warps out.

B) SoH (Ship of Harkinian):
   1. In the editor open this dungeon, then Playtest → SoH (append mode).
   2. The Megaton-Hammer-modified soh.exe boots straight into the level (no intro).
   3. Confirm the same gameplay points as above.

If any actor fails to spawn, its object is missing from the room 0x0B list — report
which actor and I'll extend the actor→object map (composite/multi-object actors).");
    }
}
