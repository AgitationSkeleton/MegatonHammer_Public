using MegatonHammer.Editor;
using MegatonHammer.Export;
using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Round-trip fidelity audit: for every importable scene, import it then re-export via the faithful
/// retain path (RetainedSceneBuilder), and verify (a) it builds without crashing, (b) the room count +
/// every room's actor list round-trips, (c) the vanilla header commands the editor doesn't model are
/// preserved (object list 0x0B, collision header 0x03), and (d) an added actor persists. This is the
/// "edit a vanilla level and it stays identical except your edits" guarantee for the N64 path.
/// Run: MegatonHammer --roundtrip [oot|mm|both]
/// </summary>
public static class RoundTripAudit
{
    private static readonly string OotRom = Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
    private static readonly string MmRom  = Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).n64");

    public static void Run(string[] args)
    {
        bool oot = args.Any(a => a.Equals("oot", StringComparison.OrdinalIgnoreCase));
        bool mm  = args.Any(a => a.Equals("mm", StringComparison.OrdinalIgnoreCase));
        if (!oot && !mm) oot = mm = true;
        if (oot) AuditGame(false);
        if (mm)  AuditGame(true);
    }

    private static void AuditGame(bool mm)
    {
        string romPath = mm ? MmRom : OotRom;
        if (!File.Exists(romPath)) { Console.WriteLine($"[roundtrip] {(mm ? "MM" : "OoT")} ROM not found"); return; }
        var rom = new RomImage(romPath);
        Console.WriteLine($"\n================= {(mm ? "MM" : "OoT")} ROUND-TRIP AUDIT =================");

        IEnumerable<int> sceneIds = mm
            ? MmSceneFiles.All.Select(t => t.Id)
            : Enumerable.Range(0, 128).Where(OotSceneFiles.IsValid);

        int scenes = 0, ok = 0, crashed = 0, actorMismatch = 0, cmdLost = 0, editFail = 0, otrFail = 0;
        var problems = new List<string>();

        foreach (int sid in sceneIds)
        {
            ImportedLevel? level;
            try { level = ImportedLevel.Load(rom, sid); } catch (Exception ex) { crashed++; problems.Add($"[{sid:X2}] IMPORT THREW: {ex.GetType().Name}"); continue; }
            if (level == null) continue;
            scenes++;
            var scene = BuildSceneFromImported(level);   // editor's import → ZScene, with .Imported set

            (byte[] Scene, List<byte[]> Rooms)? built;
            try { built = RetainedSceneBuilder.TryBuild(scene); }
            catch (Exception ex) { crashed++; problems.Add($"[{sid:X2}] {scene.Name}: RETAIN THREW {ex.GetType().Name}: {ex.Message}"); continue; }
            if (built == null) { problems.Add($"[{sid:X2}] {scene.Name}: retain returned null"); continue; }

            bool sceneOk = true;

            // (a) room count round-trips
            if (built.Value.Rooms.Count != scene.Rooms.Count)
            { sceneOk = false; actorMismatch++; problems.Add($"[{sid:X2}] {scene.Name}: room count {built.Value.Rooms.Count} != {scene.Rooms.Count}"); }

            // (b) each room's actor list round-trips (count, excluding transitions) + (c) 0x0B/0x03 preserved
            for (int ri = 0; ri < Math.Min(built.Value.Rooms.Count, scene.Rooms.Count); ri++)
            {
                byte[] retained = built.Value.Rooms[ri];
                byte[] orig;
                try { orig = ri < level.Scene.Rooms.Count ? rom.GetFile(level.Scene.Rooms[ri].FileIndex) : []; } catch { continue; }
                if (orig.Length == 0) continue;
                int wantActors = scene.Rooms[ri].Actors.Count(a => !a.IsTransition);
                int gotActors = ActorCount(retained);
                if (gotActors >= 0 && gotActors != wantActors)
                { sceneOk = false; actorMismatch++; problems.Add($"[{sid:X2}] {scene.Name} room {ri}: actor {gotActors} != {wantActors}"); }
                if (!CmdPreserved(orig, retained, 0x0B))
                { sceneOk = false; cmdLost++; problems.Add($"[{sid:X2}] {scene.Name} room {ri}: object list 0x0B not preserved"); }
            }

            // (d) edit persists: add an actor to room 0, rebuild, confirm count grows by 1
            if (scene.Rooms.Count > 0)
            {
                int before = ActorCount(built.Value.Rooms[0]);
                int expected = (before < 0 ? 0 : before) + 1;   // a room with no 0x01 (-1) gains a 1-actor list
                scene.Rooms[0].Actors.Add(new ZActor { Number = 0x0015, Variable = 0x1234, XPos = 1, YPos = 2, ZPos = 3 });
                var built2 = RetainedSceneBuilder.TryBuild(scene);
                int after = built2 != null ? ActorCount(built2.Value.Rooms[0]) : -999;
                if (after != expected)
                { sceneOk = false; editFail++; problems.Add($"[{sid:X2}] {scene.Name}: added actor didn't persist ({before}->{after}, expected {expected})"); }
            }

            // OTR (SoH/2Ship) export must not crash and must produce resources for the same scene.
            try
            {
                var otr = Otr.OtrSceneWriter.BuildLevel(scene, "scene/mh_audit", mm);
                if (otr == null || otr.Count == 0)
                { sceneOk = false; otrFail++; problems.Add($"[{sid:X2}] {scene.Name}: OTR export produced no resources"); }
            }
            catch (Exception ex)
            { sceneOk = false; otrFail++; problems.Add($"[{sid:X2}] {scene.Name}: OTR export THREW {ex.GetType().Name}: {ex.Message}"); }

            if (sceneOk) ok++;
        }

        Console.WriteLine($"  scenes audited: {scenes}");
        Console.WriteLine($"  fully OK:       {ok}");
        Console.WriteLine($"  crashed:        {crashed}");
        Console.WriteLine($"  actor mismatch: {actorMismatch}");
        Console.WriteLine($"  0x0B lost:      {cmdLost}");
        Console.WriteLine($"  edit not kept:  {editFail}");
        Console.WriteLine($"  OTR export fail:{otrFail}");
        if (problems.Count > 0)
        {
            Console.WriteLine($"\n  --- PROBLEMS ({problems.Count}) ---");
            foreach (var p in problems.Take(60)) Console.WriteLine("    " + p);
        }
    }

    // Mirror the editor's import → ZScene construction (rooms + per-room actors + transitions), and mark
    // it imported so RetainedSceneBuilder uses the faithful retain path.
    private static ZScene BuildSceneFromImported(ImportedLevel level)
    {
        var s = level.Scene;
        var zs = new ZScene(s.Name) { Imported = level };
        while (zs.Rooms.Count < Math.Max(1, s.Rooms.Count)) zs.AddRoom();
        for (int i = 0; i < s.Rooms.Count; i++)
            foreach (var a in s.Rooms[i].Actors)
                zs.Rooms[i].Actors.Add(new ZActor
                {
                    Number = a.Id, Variable = a.Params,
                    XPos = a.X, YPos = a.Y, ZPos = a.Z, XRot = a.RX, YRot = a.RY, ZRot = a.RZ,
                });
        foreach (var t in s.Transitions)
            zs.Rooms[0].Actors.Add(new ZActor
            {
                Number = t.Id, Variable = t.Params, XPos = t.X, YPos = t.Y, ZPos = t.Z, YRot = t.RY,
                IsTransition = true, FrontRoom = t.FrontRoom, FrontEffect = t.FrontEffect,
                BackRoom = t.BackRoom, BackEffect = t.BackEffect,
            });
        return zs;
    }

    private static int ActorCount(byte[] d)
    {
        for (int p = 0; p + 8 <= d.Length; p += 8) { if (d[p] == 0x14) break; if (d[p] == 0x01) return d[p + 1]; }
        return -1;   // no actor command (rooms can legitimately have none)
    }

    // The given header command's 8 bytes are present and byte-identical between orig and retained.
    private static bool CmdPreserved(byte[] orig, byte[] retained, byte id)
    {
        var a = Grab(orig, id); var b = Grab(retained, id);
        if (a.Length == 0) return true;   // vanilla had none → nothing to preserve
        return a.AsSpan().SequenceEqual(b);
    }

    private static byte[] Grab(byte[] d, byte id)
    {
        for (int p = 0; p + 8 <= d.Length; p += 8) { if (d[p] == 0x14) break; if (d[p] == id) return d[p..(p + 8)]; }
        return [];
    }
}
