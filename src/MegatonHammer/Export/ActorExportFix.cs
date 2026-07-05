namespace MegatonHammer.Export;

/// <summary>
/// Export-time fix-ups that make a placed actor actually SPAWN in vanilla gameplay regardless of how the
/// user configured it or what kind of scene it's in. Applied by both exporters (N64 <see cref="RoomExporter"/>
/// and O2R <see cref="Otr.OtrSceneWriter"/>) at the point the actor variable + room object list are written.
///
/// The recurring trap: several actors pick their object from a small <c>sObjectIds[]</c> table keyed off a
/// param bit, and <c>Actor_Kill()</c> themselves if that object isn't loaded. When the object is a KEEP
/// (gameplay_dangeon_keep / gameplay_field_keep), it's only resident in the matching scene TYPE — so the
/// default variant silently fails to spawn in the "wrong" kind of scene. We steer them onto a normal object
/// that loads anywhere, and make sure that object is in the room's dependency list.
/// </summary>
public static class ActorExportFix
{
    // Obj_Tsubo (OoT pot). sObjectIds = { OBJECT_GAMEPLAY_DANGEON_KEEP, OBJECT_TSUBO }, bit 8. Default (bit8=0)
    // needs the dungeon keep → dies in non-dungeon scenes. Force OBJECT_TSUBO (loads anywhere).
    private const ushort ObjTsuboOoT = 0x0111;
    public const ushort ObjectTsubo = 0x012C;

    // En_Kusa (OoT cuttable grass). sObjectIds = { OBJECT_GAMEPLAY_FIELD_KEEP, OBJECT_KUSA, OBJECT_KUSA }, keyed
    // by (params & 3). z_en_kusa.c Actor_Kills if the object isn't loaded. Type 0 uses the FIELD keep → dies in
    // DUNGEON scenes (e.g. a Deku Tree recreation); types 1/2 use OBJECT_KUSA (which the editor otherwise never
    // adds). Draw table { gFieldBushDL, object_kusa_DL_000140, object_kusa_DL_000140 }: type 2 is the OBJECT_KUSA
    // twin of the type-0 tuft, so in a dungeon we remap 0→2 (same look) and always list OBJECT_KUSA. The drop
    // (hearts/rupees) lives in bits 8-11, untouched by the type remap.
    private const ushort EnKusaOoT = 0x0125;
    public const ushort ObjectKusa = 0x012B;

    /// <summary>The variable to actually WRITE for this actor so it spawns (object-bank fix), else unchanged.</summary>
    public static ushort Variable(bool mm, ushort actorId, ushort variable, bool isDungeon)
    {
        if (mm) return variable;
        if (actorId == ObjTsuboOoT) return (ushort)(variable | 0x0100);                 // pot → OBJECT_TSUBO (any scene)
        if (actorId == EnKusaOoT && isDungeon && (variable & 3) == 0)                    // field-keep grass in a dungeon
            return (ushort)((variable & ~0x0003) | 0x0002);                              //   → type 2 (OBJECT_KUSA twin)
        return variable;
    }

    /// <summary>An extra object id this actor needs in the room's object list beyond the resolver's default,
    /// or 0 for none. (Unconditional — a harmless spare object slot at worst.)</summary>
    public static ushort ExtraRoomObject(bool mm, ushort actorId)
    {
        if (mm) return 0;
        if (actorId == ObjTsuboOoT) return ObjectTsubo;   // pot always rides OBJECT_TSUBO
        if (actorId == EnKusaOoT) return ObjectKusa;      // grass types 1/2 (and dungeon-remapped type 0) need OBJECT_KUSA
        return 0;
    }
}
