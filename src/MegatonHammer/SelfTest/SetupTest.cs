using MegatonHammer.Editor;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Headless proof that scene setups are a real, from-blank editor feature (not import-only data):
/// builds a blank document, adds a setup, edits the variant independently, switches between them,
/// round-trips through the project serializer, and collapses back to a single header. Run:
/// MegatonHammer --testsetups
/// </summary>
public static class SetupTest
{
    public static void Run()
    {
        var doc = new MapDocument();
        var scene = doc.Scene;

        // Blank slate: one room, one actor, some lighting/music.
        scene.Rooms[0].Actors.Add(new ZActor { Number = 0x0010, XPos = 100 });
        scene.Settings.MusicSeq = 10;

        // Author a second variant from scratch (promotes current to "Default", adds "Night").
        doc.AddSetup("Night");
        scene.Rooms[0].Actors[0].XPos = 999;        // edit only affects the active (Night) setup
        scene.Settings.MusicSeq = 20;
        scene.Rooms[0].Actors.Add(new ZActor { Number = 0x0033, XPos = 300 });   // extra actor at night

        // Switch back to Default → the original data returns.
        doc.SwitchSetup(0);
        bool defaultOk = scene.Rooms[0].Actors.Count == 1
                         && scene.Rooms[0].Actors[0].XPos == 100 && scene.Settings.MusicSeq == 10;

        // Switch to Night → the edited variant returns.
        doc.SwitchSetup(1);
        bool nightOk = scene.Rooms[0].Actors.Count == 2
                       && scene.Rooms[0].Actors[0].XPos == 999 && scene.Settings.MusicSeq == 20;

        // Project round-trip preserves both setups + their independent data.
        string json = ProjectSerializer.Serialize(doc);
        var doc2 = new MapDocument();
        ProjectSerializer.Deserialize(doc2, json);
        var s2 = doc2.Scene;
        bool persisted = s2.Setups.Count == 2 && s2.Setups[0].Name == "Default"
                         && s2.Setups[1].Name == "Night" && s2.ActiveSetup == 1;
        bool dataOk = s2.Setups.Count == 2
                      && s2.Setups[0].RoomActors[0][0].XPos == 100
                      && s2.Setups[1].RoomActors[0][0].XPos == 999
                      && s2.Setups[1].RoomActors[0].Count == 2;

        // Delete a setup → a lone survivor collapses back to a plain single-header scene.
        doc.RemoveSetup(1);
        bool collapsed = scene.Setups.Count == 0 && scene.Rooms[0].Actors[0].XPos == 100;

        Console.WriteLine($"[testsetups] switchToDefault={defaultOk} switchToNight={nightOk} " +
                          $"persisted={persisted} perSetupData={dataOk} collapseToSingle={collapsed}");

        // Export verification: a 2-setup scene compiles with a scene 0x18 and per-room 0x18 alt
        // headers, and each room header's actor count matches that setup's actors for the room.
        var ed = new MapDocument();
        var es = ed.Scene;
        es.Rooms[0].Actors.Add(new ZActor { Number = 0x0010, XPos = 50 });   // default: 1 actor
        ed.AddSetup("Night");
        es.Rooms[0].Actors.Add(new ZActor { Number = 0x0033, XPos = 80 });   // night: 2 actors
        ed.SwitchSetup(0);
        var (scn, rms) = Export.SceneExporter.BuildBinaries(es);
        bool sceneHas18 = HasCmd(scn, 0x18);
        bool roomHas18  = rms.Count == 1 && HasCmd(rms[0], 0x18);
        // Read the first 2 alt-header actor counts (the scene has 2 setups). The 0x18 list isn't
        // self-terminating, so only the known entries are meaningful here.
        var actorCounts = rms.Count == 1 ? RoomHeaderActorCounts(rms[0]) : [];
        bool altCounts = actorCounts.Count >= 2 && actorCounts[0] == 1 && actorCounts[1] == 2;
        Console.WriteLine($"[testsetups] export: scene0x18={sceneHas18} room0x18={roomHas18} altActorCounts=[{string.Join(',', actorCounts)}]");

        bool ok = defaultOk && nightOk && persisted && dataOk && collapsed && sceneHas18 && roomHas18 && altCounts;
        Console.WriteLine(ok ? "[testsetups] PASS" : "[testsetups] FAIL");
    }

    // True if a scene/room header (8-byte commands until 0x14) contains the given command.
    private static bool HasCmd(byte[] bin, byte cmd)
    {
        for (int p = 0; p + 8 <= bin.Length; p += 8) { if (bin[p] == cmd) return true; if (bin[p] == 0x14) break; }
        return false;
    }

    // Walks the primary header for its 0x18 alt-header list, then each header's 0x01 actor count.
    private static List<int> RoomHeaderActorCounts(byte[] bin)
    {
        int ActorCount(int hdrOff)
        {
            for (int p = hdrOff; p + 8 <= bin.Length; p += 8) { if (bin[p] == 0x01) return bin[p + 1]; if (bin[p] == 0x14) break; }
            return 0;
        }
        int U32(int o) => (bin[o] << 24) | (bin[o + 1] << 16) | (bin[o + 2] << 8) | bin[o + 3];
        int altOff = -1;
        for (int p = 0; p + 8 <= bin.Length; p += 8) { if (bin[p] == 0x18) { altOff = U32(p + 4) & 0xFFFFFF; break; } if (bin[p] == 0x14) break; }
        var counts = new List<int>();
        if (altOff < 0) { counts.Add(ActorCount(0)); return counts; }
        for (int i = 0; altOff + i * 4 + 4 <= bin.Length; i++)
        {
            uint ptr = (uint)U32(altOff + i * 4);
            if (i > 0 && ptr == 0) break;
            counts.Add(ActorCount(i == 0 ? 0 : (int)(ptr & 0xFFFFFF)));   // layer 0 NULL → primary header at 0
            if (i >= 7) break;
        }
        return counts;
    }
}
