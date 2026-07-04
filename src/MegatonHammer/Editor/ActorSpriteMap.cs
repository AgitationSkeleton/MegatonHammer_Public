namespace MegatonHammer.Editor;

/// <summary>
/// Curated mapping from an OoT actor id to an item/inventory icon (index into icon_item_static), used
/// to represent actors that have NO 3D model in the ROM (effects, triggers, managers, item-givers).
/// The mapping only takes effect when an actor resolves no model, so liberal entries are harmless.
/// Grow the table as more unmodeled actors are identified; unmapped ones use the generic fallback.
/// Companion to <see cref="MegatonHammer.Rom.ItemIconSource"/>.
/// </summary>
public static class ActorSpriteMap
{
    // Item icon indices = ITEM ids, in icon_item_static order.
    private const int DekuStick = 0, DekuNut = 1, Bomb = 2, Bow = 3, FireArrow = 4, DinsFire = 5,
        Slingshot = 6, FairyOcarina = 7, OcarinaOfTime = 8, Bombchu = 9, Hookshot = 10, Longshot = 11,
        IceArrow = 12, FaroresWind = 13, Boomerang = 14, LensOfTruth = 15, MagicBean = 16, Hammer = 17,
        LightArrow = 18, NayrusLove = 19, EmptyBottle = 20, BottledFairy = 24;   // icon_item_static: ITEM_FAIRY=0x18

    /// <summary>Fallback icon for an unmodeled actor with no specific item mapping.</summary>
    public const int GenericIcon = LensOfTruth;

    private static readonly Dictionary<ushort, int> Map = new()
    {
        [0x0018] = BottledFairy, // En_Elf        — fairy (Navi / recovery fairy): the bottled-fairy icon
        [0x0010] = Bomb,         // En_Bom        — bomb
        [0x004C] = Bomb,         // En_Bombf      — bomb flower
        [0x00DA] = Bombchu,      // En_Bom_Chu    — bombchu
        [0x00F1] = FairyOcarina, // Item_Ocarina
        [0x010A] = FireArrow,    // Arrow_Fire
        [0x010B] = IceArrow,     // Arrow_Ice
        [0x010C] = LightArrow,   // Arrow_Light
        [0x0127] = Bomb,         // Obj_Bombiwa   — bombable rock
        [0x0159] = Bombchu,      // Bg_Jya_Bombchuiwa — bombchu wall
        // Projectiles / magic / collectables with no static model → the matching inventory icon,
        // so a logic/effect actor reads as what it does (the user's "matching sprite" request).
        [0x0016] = Bow,          // En_Arrow
        [0x0032] = Boomerang,    // En_Boom
        [0x009E] = FaroresWind,  // Farore's Wind
        [0x009F] = DinsFire,     // Din's Fire
        [0x00F4] = NayrusLove,   // Nayru's Love
        [0x010F] = EmptyBottle,  // GI collectables (shields / bottles / etc.)
        [0x0112] = EmptyBottle,  // Invisible Collectable
        // Overlay-embedded EFFECT actors with no editor model → the item that reads as their purpose.
        // Ocarina-song visual effects (light shafts, screen wipes/storm from playing a song) → ocarina.
        [0x017E] = FairyOcarina,  // Oceff_Spot  — song-spot light effect (Sun's Song, etc.)
        [0x018A] = FairyOcarina,  // Oceff_Wipe  — song screen-wipe
        [0x018B] = FairyOcarina,  // Oceff_Storm — Song of Storms effect
        [0x0198] = FairyOcarina,  // Oceff_Wipe2
        [0x0199] = FairyOcarina,  // Oceff_Wipe3
        [0x01CB] = FairyOcarina,  // Oceff_Wipe4
        // Navi message hint triggers (invisible in-game) → the fairy icon (Navi delivers the message).
        [0x011B] = BottledFairy,  // Elf_Msg  — point message trigger
        [0x0173] = BottledFairy,  // Elf_Msg2 — area message trigger
        [0x004F] = BottledFairy,  // En_OE2   — Blue Navi lock-on target spot (invisible; empty Draw)
        // … grows: add `[actorId] = item,` as more unmodeled actors are curated.
    };

    // ── Majora's Mask ──────────────────────────────────────────────────────
    // MM icon_item_static order (index = offset / 0x1000 in the unarchived blob).
    private const int MmOcarinaOfTime = 0, MmBow = 1, MmFireArrow = 2, MmIceArrow = 3, MmLightArrow = 4,
        MmFairyOcarina = 5, MmBomb = 6, MmBombchu = 7, MmDekuStick = 8, MmDekuNut = 9, MmMagicBeans = 10,
        MmSlingshot = 11, MmPowderKeg = 12, MmPictoBox = 13, MmLensOfTruth = 14, MmHookshot = 15,
        MmGreatFairysSword = 16, MmLongshot = 17, MmEmptyBottle = 18, MmBottledFairy = 22,
        MmMoonsTear = 41, MmDekuMask = 50, MmGoronMask = 51, MmZoraMask = 52, MmFierceDeityMask = 53;

    private const int MmGenericIcon = MmLensOfTruth;

    private static readonly Dictionary<ushort, int> MmMap = new()
    {
        [0x0009] = MmPowderKeg,        // En_Bom (Powder Keg)
        [0x000F] = MmBow,              // Arrow
        [0x0010] = MmBottledFairy,     // Healing Fairy and Tatl
        [0x0034] = MmDekuNut,          // Deku Nut Effect
        [0x003D] = MmHookshot,         // Hookshot
        [0x006A] = MmBombchu,          // Bombchu
        [0x007D] = MmFireArrow,        // Arrow_Fire
        [0x007E] = MmIceArrow,         // Arrow_Ice
        [0x007F] = MmLightArrow,       // Arrow_Light
        [0x0097] = MmFairyOcarina,     // Ocarina Song Spot
        [0x00DA] = MmDekuNut,          // Deku Nut Projectile
        [0x0165] = MmGreatFairysSword, // Great Fairy's Mask and Sword
        [0x017C] = MmMoonsTear,        // The Moon
        [0x01B0] = MmBottledFairy,     // Stray Fairy
        [0x01B1] = MmBottledFairy,     // Stray Fairy Bubble
        [0x01DF] = MmFairyOcarina,     // Monkey Instrument Prompt
        // Light rays / reflections (invisible until activated) → the light-arrow icon (a light beam).
        [0x0062] = MmLightArrow,       // Mir_Ray  — reflectable light ray
        [0x01D0] = MmLightArrow,       // Mir_Ray2 — reflectable light ray
        [0x0230] = MmLightArrow,       // Mir_Ray3 — mirror-shield reflection + glow
        [0x0256] = MmLightArrow,       // Bg_Ikana_Ray — large light ray (Stone Tower)
        // Ocarina-song visual effects → ocarina.
        [0x00CC] = MmFairyOcarina,     // Oceff_Spot
        [0x00D6] = MmFairyOcarina,     // Oceff_Wipe
        [0x00D7] = MmFairyOcarina,     // Oceff_Storm
        [0x00DF] = MmFairyOcarina,     // Oceff_Wipe2
        [0x00E0] = MmFairyOcarina,     // Oceff_Wipe3
        [0x00F6] = MmFairyOcarina,     // Oceff_Wipe4
        [0x0249] = MmFairyOcarina,     // Oceff_Wipe5
        [0x024B] = MmFairyOcarina,     // Oceff_Wipe6
        [0x024E] = MmFairyOcarina,     // Oceff_Wipe7
        // … grows: add `[actorId] = MmIcon,` for more unmodeled MM actors.
    };

    /// <summary>The icon index to draw for an unmodeled actor: its curated item, else the generic.</summary>
    public static int IconFor(ushort actorId, bool mm = false) => mm
        ? (MmMap.TryGetValue(actorId, out var mi) ? mi : MmGenericIcon)
        : (Map.TryGetValue(actorId, out var i) ? i : GenericIcon);
}
