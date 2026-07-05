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
    // by (params & 3). z_en_kusa.c Init Actor_Kills if the object isn't loaded. Type 0 uses the FIELD keep, which
    // is ONLY resident in true field scenes — so type-0 grass silently dies in dungeons AND in our "append as a
    // new scene" playtest slots (neither loads the field keep). Its draw uses gFieldBushDL, hardcoded to the
    // keep's segment 0x05, so we can't just add the keep as a room object (wrong segment). Type 2 is the
    // OBJECT_KUSA twin of the type-0 tuft — identical model AND identical drop (z_en_kusa.c EnKusa_DropCollectible
    // shares `case 0: case 2:`, both `(params>>8)&0xF` random) — and OBJECT_KUSA loads in any scene. So we remap
    // 0→2 UNCONDITIONALLY (was dungeon-only, which left arena/append grass spawning only intermittently) and
    // always list OBJECT_KUSA. The drop bits [8..11] are preserved (only bits 0-1 change).
    private const ushort EnKusaOoT = 0x0125;
    public const ushort ObjectKusa = 0x012B;

    // En_Item00 (OoT freestanding collectible). A PLACED item (no 0x8000 spawn flag) keeps despawnTimer=-1, and
    // EnItem00_Draw's RECOVERY_HEART branch (type 0x03) draws via GetItem_Draw(GID_RECOVERY_HEART) out of
    // OBJECT_GI_HEART — NOT gameplay_keep. If that object isn't loaded, Object_IsLoaded fails and the draw
    // early-returns: the heart is collectible (Update is independent) but INVISIBLE. Every other ITEM00 type
    // draws from the resident gameplay_keep, so only the recovery heart needs an extra object. (z_en_item00.c)
    private const ushort EnItem00OoT = 0x0015;
    private const ushort Item00RecoveryHeart = 0x03;
    public const ushort ObjectGiHeart = 0x00B7;

    /// <summary>The variable to actually WRITE for this actor so it spawns (object-bank fix), else unchanged.</summary>
    public static ushort Variable(bool mm, ushort actorId, ushort variable, bool isDungeon)
    {
        if (mm) return variable;
        if (actorId == ObjTsuboOoT) return (ushort)(variable | 0x0100);                 // pot → OBJECT_TSUBO (any scene)
        if (actorId == EnKusaOoT && (variable & 3) == 0)                                 // field-keep grass (type 0)
            return (ushort)((variable & ~0x0003) | 0x0002);                              //   → type 2 (OBJECT_KUSA twin, any scene)
        return variable;
    }

    /// <summary>An extra object id this actor needs in the room's object list beyond the resolver's default,
    /// or 0 for none. (A harmless spare object slot at worst.) Some are variable-dependent (En_Item00 heart).</summary>
    public static ushort ExtraRoomObject(bool mm, ushort actorId, ushort variable)
    {
        if (mm) return 0;
        if (actorId == ObjTsuboOoT) return ObjectTsubo;   // pot always rides OBJECT_TSUBO
        if (actorId == EnKusaOoT) return ObjectKusa;      // grass types 1/2 (and dungeon-remapped type 0) need OBJECT_KUSA
        if (actorId == EnItem00OoT && (variable & 0xFF) == Item00RecoveryHeart) return ObjectGiHeart;  // placed heart → GI_HEART
        return 0;
    }
}
