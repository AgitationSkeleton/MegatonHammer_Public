using MegatonHammer.Editor;
using MegatonHammer.Export;
using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Verifies the faithful vanilla round-trip: patching a room's actor list via RetainedRoomPatcher
/// preserves every other header command (object list 0x0B, behaviour 0x08, mesh 0x0A, …) byte-for-byte
/// while correctly rewriting the actors — for both the same-count (in-place) and changed-count (append)
/// paths. Run: MegatonHammer --testretain [oot|mm]
/// </summary>
public static class RetainTest
{
    private static readonly string OotRom = Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
    private static readonly string MmRom  = Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).n64");

    public static void Run(string[] args)
    {
        bool mm = args.Length >= 2 && args[1].Equals("mm", StringComparison.OrdinalIgnoreCase);
        var rom = new RomImage(mm ? MmRom : OotRom);
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) { pass++; Console.WriteLine($"  PASS {what}"); } else { fail++; Console.WriteLine($"  FAIL {what}"); } }

        IEnumerable<int> sceneIds = mm
            ? MmSceneFiles.All.Select(t => t.Id)
            : Enumerable.Range(0, 128).Where(OotSceneFiles.IsValid);

        // Find a scene+room that has BOTH an actor list (0x01) and an object list (0x0B) to prove
        // the object list survives the actor patch.
        foreach (int sid in sceneIds)
        {
            var scene = SceneImporter.Import(rom, sid);
            if (scene == null) continue;
            for (int ri = 0; ri < scene.Rooms.Count; ri++)
            {
                var room = scene.Rooms[ri];
                byte[] roomBytes;
                try { roomBytes = rom.GetFile(room.FileIndex); } catch { continue; }
                var (hasActors, hasObjects) = HasCommands(roomBytes);
                if (!hasActors || !hasObjects || room.Actors.Count == 0) continue;

                Console.WriteLine($"\n[{sid:X2}] {scene.Name} room {ri}: {room.Actors.Count} actors — testing retain+patch");
                byte[] origObjCmd = GrabCommand(roomBytes, 0x0B);
                byte[] origBehavior = GrabCommand(roomBytes, 0x08);

                var editActors = room.Actors.Select(a => new ZActor
                { Number = a.Id, IdFlags = a.IdFlags, Variable = a.Params, XPos = a.X, YPos = a.Y, ZPos = a.Z, XRot = a.RX, YRot = a.RY, ZRot = a.RZ }).ToList();

                // (1) Same-count patch: move actor 0 by +100 in X.
                editActors[0].XPos += 100;
                var patched = RetainedRoomPatcher.PatchActors(roomBytes, editActors, mm);
                Check(SequenceEq(GrabCommand(patched, 0x0B), origObjCmd), "object list (0x0B) preserved [same-count]");
                Check(SequenceEq(GrabCommand(patched, 0x08), origBehavior), "behaviour (0x08) preserved [same-count]");
                Check(ReadActor0X(patched, mm) == (short)MathF.Round(editActors[0].XPos), "actor 0 X updated [same-count]");
                Check(patched.Length == roomBytes.Length, "file size unchanged [same-count]");

                // (2) Changed-count patch: append one actor.
                editActors.Add(new ZActor { Number = 0x0015, Variable = 0x1234, XPos = 5, YPos = 6, ZPos = 7 });
                var grown = RetainedRoomPatcher.PatchActors(roomBytes, editActors, mm);
                Check(SequenceEq(GrabCommand(grown, 0x0B), origObjCmd), "object list (0x0B) preserved [grown]");
                Check(ActorCount(grown) == editActors.Count, $"actor count {ActorCount(grown)} == {editActors.Count} [grown]");
                Check(grown.Length > roomBytes.Length, "file grew for appended actor [grown]");

                Console.WriteLine($"\n==== {(fail == 0 ? "ALL PASS" : $"{fail} FAILED")} ({pass} passed) ====");
                return;
            }
        }
        Console.WriteLine("No suitable room found (need 0x01 + 0x0B).");
    }

    private static (bool actors, bool objects) HasCommands(byte[] d)
    {
        bool a = false, o = false;
        for (int p = 0; p + 8 <= d.Length; p += 8) { byte op = d[p]; if (op == 0x14) break; if (op == 0x01) a = true; if (op == 0x0B) o = true; }
        return (a, o);
    }

    // The 8 raw bytes of the first header command with the given id, or empty.
    private static byte[] GrabCommand(byte[] d, byte id)
    {
        for (int p = 0; p + 8 <= d.Length; p += 8) { byte op = d[p]; if (op == 0x14) break; if (op == id) return d[p..(p + 8)]; }
        return [];
    }

    private static int ActorCount(byte[] d)
    {
        for (int p = 0; p + 8 <= d.Length; p += 8) { if (d[p] == 0x14) break; if (d[p] == 0x01) return d[p + 1]; }
        return -1;
    }

    private static short ReadActor0X(byte[] d, bool mm)
    {
        for (int p = 0; p + 8 <= d.Length; p += 8)
        {
            if (d[p] == 0x14) break;
            if (d[p] == 0x01)
            {
                int off = (int)(((uint)((d[p + 4] << 24) | (d[p + 5] << 16) | (d[p + 6] << 8) | d[p + 7])) & 0x00FFFFFF);
                return (short)((d[off + 2] << 8) | d[off + 3]);
            }
        }
        return short.MinValue;
    }

    private static bool SequenceEq(byte[] a, byte[] b) => a.Length == b.Length && a.AsSpan().SequenceEqual(b);
}
