namespace MegatonHammer.Export;

/// <summary>
/// Export-time fix-ups that make a placed actor actually SPAWN in vanilla gameplay regardless of how the
/// user configured it or what kind of scene it's in. Applied by both exporters (N64 <see cref="RoomExporter"/>
/// and O2R <see cref="Otr.OtrSceneWriter"/>) at the point the actor variable + room object list are written.
/// </summary>
public static class ActorExportFix
{
    // Obj_Tsubo (OoT breakable pot). z_obj_tsubo.c: sObjectIds[(params>>8)&1] = { OBJECT_GAMEPLAY_DANGEON_KEEP,
    // OBJECT_TSUBO }. A DEFAULT pot (bit 8 = 0) needs the dungeon keep, which only loads in DUNGEON scenes — so
    // in a non-dungeon scene Object_GetIndex fails and the pot Actor_Kill()s itself (never spawns). OBJECT_TSUBO
    // (0x012C) is a normal object that loads as a room dependency in ANY scene, so we force bit 8 = 1 and add
    // OBJECT_TSUBO to the room's object list. Both pot models are near-identical clay pots.
    private const ushort ObjTsuboOoT = 0x0111;
    public const ushort ObjectTsubo = 0x012C;

    /// <summary>The variable to actually WRITE for this actor so it spawns (pot object-bank fix), else unchanged.</summary>
    public static ushort Variable(bool mm, ushort actorId, ushort variable)
        => (!mm && actorId == ObjTsuboOoT) ? (ushort)(variable | 0x0100) : variable;

    /// <summary>An extra object id this actor needs in the room's object list beyond the resolver's default,
    /// or 0 for none. (The pot needs OBJECT_TSUBO once we've forced it onto that object.)</summary>
    public static ushort ExtraRoomObject(bool mm, ushort actorId)
        => (!mm && actorId == ObjTsuboOoT) ? ObjectTsubo : (ushort)0;
}
