using System.Linq;

namespace MegatonHammer.Editor;

public sealed class ZScene
{
    public string     Name  { get; set; }
    public List<ZRoom> Rooms { get; } = [];
    public SceneSettings Settings { get; set; } = new();

    /// <summary>All scene lighting environments (command 0x0F). Empty = use the single Settings env.
    /// When populated (e.g. on import), entry 0 is the primary that Settings mirrors/edits, and the
    /// rest are preserved verbatim so a multi-environment scene survives a recompile.</summary>
    public List<Rom.EnvLight> Environments { get; set; } = [];

    /// <summary>Scene paths (command 0x0D) — waypoint polylines that moving platforms and
    /// time/action-driven NPCs follow by index. Editable like a Hammer func_tracktrain track.</summary>
    public List<ZPath> Paths { get; set; } = [];

    /// <summary>Positional (point) lights — scene cmd 0x0C. Glowing light sources inserted into the
    /// scene's light context (torches, lanterns…), on top of the ambient env lighting (0x0F).</summary>
    public List<ScenePointLight> PointLights { get; set; } = [];

    /// <summary>Authored dialogue (the Message Bank): in-game text an actor's Message field references
    /// by textId. Exported per target (N64 message-table append / SoH+2Ship mh/messages resource).</summary>
    public List<MhMessage> Messages { get; set; } = [];

    /// <summary>The bank message providing <paramref name="textId"/>, or null.</summary>
    public MhMessage? Message(int textId) => Messages.FirstOrDefault(m => m.Id == textId);

    /// <summary>Lowest unused textId for an actor Message field: base + the largest value in
    /// [0, maxValue] not already used by a bank entry (top-down keeps new ids clear of low vanilla ids).</summary>
    public int NextFreeMessageId(int textIdBase, int maxValue)
    {
        for (int v = maxValue; v >= 0; v--)
            if (Messages.All(m => m.Id != textIdBase + v)) return textIdBase + v;
        return textIdBase;   // bank full for this field; reuse base
    }

    /// <summary>Raw cutscene script (command 0x17), retained verbatim from import and re-emitted on
    /// export (its internal segment-2 pointers are relocated). Null = no cutscene.</summary>
    public byte[]? CutsceneData { get; set; }
    public int CutsceneOrigOff { get; set; }

    /// <summary>Scene setups / alternate headers (command 0x18) — OoT age and MM time-of-day
    /// variants. Empty = a single-header scene. When non-empty, <see cref="ActiveSetup"/> is the
    /// variant currently mirrored into Settings/Environments and each room's Actors.</summary>
    public List<ZSetup> Setups { get; set; } = [];
    public int ActiveSetup { get; set; }

    /// <summary>Snapshots the live scene data (settings, environments, per-room actors) into the
    /// active setup, so it isn't lost on save / switch / export.</summary>
    public void CommitActiveSetup()
    {
        if (Setups.Count == 0 || ActiveSetup < 0 || ActiveSetup >= Setups.Count) return;
        var su = Setups[ActiveSetup];
        su.Settings = Settings.Clone();
        su.Environments = [.. Environments];
        su.RoomActors = Rooms.Select(r => r.Actors.Select(a => a.Clone()).ToList()).ToList();
    }

    /// <summary>Loads a setup's data into the live scene (settings, environments, per-room actors).</summary>
    public void LoadSetup(int index)
    {
        if (index < 0 || index >= Setups.Count) return;
        var su = Setups[index];
        Settings = su.Settings.Clone();
        Environments = [.. su.Environments];
        for (int r = 0; r < Rooms.Count; r++)
        {
            Rooms[r].Actors.Clear();
            if (r < su.RoomActors.Count) Rooms[r].Actors.AddRange(su.RoomActors[r].Select(a => a.Clone()));
        }
        ActiveSetup = index;
    }

    /// <summary>A ROM level loaded read-only for viewing/reference in this scene (null = none).</summary>
    public ImportedLevel? Imported { get; set; }

    /// <summary>An imported OBJ/external mesh shown as an untextured reference backdrop (null = none).</summary>
    public List<Rom.MeshTri>? ReferenceMesh { get; set; }

    public ZRoom? ActiveRoom
    {
        get => Rooms.FirstOrDefault(r => r.IsActive) ?? Rooms.FirstOrDefault();
        set
        {
            foreach (var r in Rooms) r.IsActive = false;
            if (value != null) value.IsActive = true;
        }
    }

    public ZScene(string name = "New Scene")
    {
        Name = name;
        AddRoom();  // start with one room
    }

    public ZRoom AddRoom()
    {
        var room = new ZRoom($"Room {Rooms.Count}", Rooms.Count);
        if (Rooms.Count == 0) room.IsActive = true;
        Rooms.Add(room);
        return room;
    }

    public bool RemoveRoom(ZRoom room)
    {
        if (Rooms.Count <= 1) return false;   // always keep at least 1
        bool wasActive = room.IsActive;
        Rooms.Remove(room);
        if (wasActive) Rooms[0].IsActive = true;
        return true;
    }

    // Rename so room indices stay semantic even after removals
    public void RenumberRooms()
    {
        for (int i = 0; i < Rooms.Count; i++)
            Rooms[i].Name = $"Room {i}";
    }
}

/// <summary>A positional (point) light for scene cmd 0x0C. Exports as a 14-byte LightInfo
/// (type + LightPoint: s16 x/y/z, u8 color[3], u8 drawGlow, s16 radius).</summary>
public sealed class ScenePointLight
{
    public float X, Y, Z;
    public byte R = 255, G = 255, B = 255;
    public short Radius = 200;
    public bool Glow = true;   // type 2 (glow) vs type 0 (no glow)
}
