using MegatonHammer.Rom;

namespace MegatonHammer.Editor;

/// <summary>
/// Resolves a placed actor to its real 3D model (object-space triangles + draw scale) by
/// chaining actor id → object (ActorObjectTable) → object bytes (ObjectTable) → triangles
/// (ObjectModelReader). Unknown/obsolete actors resolve to the Eyeball Frog gag model (D7);
/// the player resolves to Link's object. Decoded models are cached per object name. (D5)
/// </summary>
public sealed class ActorModelResolver
{
    private readonly RomImage _rom;
    private readonly ObjectTable _objects;
    private readonly ActorObjectTable _actorObjects;
    private ActorOverlayTable? _overlays;   // lazy: located on first overlay-mesh request (full-file scan)
    private ActorOverlayTable Overlays => _overlays ??= ActorOverlayTable.Build(_rom);

    // MM actors that are invisible / effect-only in-game (a light ray at scale 0 until it reflects light, a
    // one-off wave effect) — auto-detect otherwise renders their object as a room-filling giant beam. Force
    // them to the billboard-sprite path instead. (Bg_Ikana_Bombwall/Shutter are real floor/door scenery.)
    private static readonly HashSet<ushort> MmForceSprite = new()
    {
        0x0062, // Mir_Ray  — reflectable light ray (OoT, broken)
        0x01D0, // Mir_Ray2 — reflectable light ray (ICHAIN scale 0; beam len = reflectIntensity)
        0x0230, // Mir_Ray3 — mirror-shield reflection + glow
        0x0256, // Bg_Ikana_Ray — large light ray (Stone Tower)
        0x024F, // Eff_Kamejima_Wave — turtle-awakening wave effect
    };

    // OoT actors that are invisible / draw nothing in-game (empty Draw), so auto-detecting their borrowed
    // object renders a spurious crumpled mesh. Force them to the billboard-sprite path (ActorSpriteMap).
    private static readonly HashSet<ushort> OotForceSprite = new()
    {
        0x004F, // En_OE2 — "Blue Navi Lock-on Target Spot": empty Draw, uses object_oE2 but never draws it
                //          (the REAL Nabooru NPC is En_Nb 0x00C3, handled separately).
    };
    private readonly ActorScaleTable _actorScales;
    private readonly ActorIdleAnimTable _idleAnims;
    private readonly ActorRenderDb _renderDb;

    private readonly Dictionary<string, Model?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public sealed record Model(IReadOnlyList<MeshTri> Tris, float Scale, bool IgnoreYaw = false,
                               OpenTK.Mathematics.Vector3 BaseRotationDeg = default);

    private const ushort ActorPlayer = 0x0000;

    // Environment-colour overrides for actors whose cloth colour is set in C draw code (not in the
    // object data), keyed by actor id. The game calls gDPSetEnvColor with this before drawing, and
    // the cloth limbs' combiners tint with it — so seeding it reproduces the in-game colour. Values
    // are the game's own constants (0..1). Skin/metal limbs use other combiner inputs and stay put.
    private static readonly Dictionary<ushort, OpenTK.Mathematics.Vector3> EnvColorOverride = new()
    {
        [0x0000] = new(30 / 255f, 105 / 255f, 27 / 255f),   // Player — Kokiri tunic (sTunicColors)
        [0x0033] = new(1f, 0f, 0f),                         // Dark Link — En_Torch2 sets env red (the red eyes)
        [0x0197] = new(140 / 255f, 0, 0),                   // En_GeldB — Gerudo warrior body (z_en_geldb.c)
    };

    // SharpOcarina custom_* model names that are really display lists inside a real ROM object —
    // the editor maps them to that object + the hint's DL offsets so the actor renders instead of
    // falling back to a billboard. Doors (En_Door/En_Shutter) draw from gameplay_keep.
    // Per-actor eye/mouth face textures (offsets in the actor's object), so face NPCs show eyes and a
    // mouth instead of a blank face. The actor's draw code binds seg 8 = sEyeTextures[0] (open eye) and
    // seg 9 = sMouthTextures[0] (neutral mouth); these are those textures' offsets, extracted from the
    // decomp. Only the player got these before. -1 = the actor has eyes but no mouth array.
    private static readonly Dictionary<ushort, (int eye, int mouth)> OotFaceTex = new()
    {
        [0x000B] = (0x17930, 0x19130), [0x0048] = (0x4CC0, -1), [0x004D] = (0x30C8, 0x3508),
        [0x0096] = (0x38A8, -1), [0x009A] = (0x1D28, -1), [0x00A1] = (0xE3B8, 0xE838),
        [0x00A8] = (0x8080, 0x8C80), [0x00A9] = (0x7210, -1), [0x00C3] = (0xB428, -1),
        [0x00C9] = (0x2F48, 0x3588), [0x00CA] = (0xCE80, -1), [0x00CC] = (0x86D8, -1),
        [0x00D2] = (0xF20, -1), [0x00D9] = (0x2570, 0x2970), [0x00DC] = (0xA438, -1),
        [0x00E7] = (0x1B18, 0x1F18), [0x0124] = (0x3E40, -1), [0x0138] = (0x708, -1),
        [0x013C] = (0x8C8, -1), [0x016D] = (0x4FF0, -1), [0x0179] = (0x30C8, 0x3508),
        [0x01C5] = (0x2570, 0x2970),
    };
    private static readonly Dictionary<ushort, (int eye, int mouth)> MmFaceTex = new()
    {
        [0x0019] = (0x8E30, -1), [0x0022] = (0x59A0, -1), [0x0054] = (0x1D28, -1),
        [0x008A] = (0x28E8, -1), [0x009F] = (0x3078, -1), [0x00BD] = (0x6498, -1),
        [0x00FA] = (0x6398, -1), [0x011D] = (0x5AC8, -1), [0x0138] = (0x10438, -1),
        [0x0141] = (0x7AA8, -1), [0x014A] = (0xF70, -1), [0x0152] = (0x2AF0, 0x46F0),
        [0x0155] = (0x14C8, -1), [0x0159] = (0xDC0, 0x9C0), [0x0168] = (0x1140, -1),
        [0x0176] = (0x6050, -1), [0x017A] = (0x1680, -1), [0x0187] = (0x18FA0, -1),
        [0x0188] = (0xB0B8, -1), [0x01A0] = (0x1680, -1), [0x01A4] = (0xFFC8, 0x127C8),
        [0x01BA] = (0x55A0, -1), [0x01C3] = (0x5E18, -1), [0x01C4] = (0x5BC0, -1),
        [0x01FC] = (0x103D0, -1), [0x0215] = (0x164D0, 0x17CD0), [0x021D] = (0x93B8, -1),
        [0x021E] = (0x53E8, -1), [0x021F] = (0xFFC8, 0x127C8), [0x0241] = (0x10918, -1),
        [0x0242] = (0x10438, -1), [0x0248] = (0x5F20, 0x6720), [0x0251] = (0x11138, -1),
        [0x0252] = (0x13C38, 0x135F8), [0x0299] = (-1, 0x9DF0), [0x02A2] = (0x7350, -1),
        [0x02A3] = (0x6050, -1),
    };

    // SharpOcarina custom_* aliases → real-object display lists (the hint's own custom mesh doesn't
    // exist in the retail ROM). The wooden door's panels are gDoorLeftDL/gDoorRightDL in gameplay_keep.
    private static readonly Dictionary<string, (string obj, int[] dls)> CustomObjectAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["custom_woodendoor"] = ("object_gameplay_keep", new[] { 0xECB8, 0xEE00 }),
    };

    // Actors whose model is specific display lists inside a real object that the actor→object table /
    // render-DB don't capture (so they fell back to a lens billboard). Keyed by actor id → (object, DLs).
    private static readonly Dictionary<ushort, (string obj, int[] dls)> OotActorDlOverride = new()
    {
        // (En_Door 0x0009 / Door_Shutter 0x002E door models are resolved dynamically by DoorModel() so
        // they can follow the scene's door theme — see DoorStyle below.)
        // En_Blkobj = the Water Temple Dark Link illusion room: the room shell + the central tree
        // (gIllusionRoomNormalDL + gIllusionRoomTreeDL in object_blkobj). Previously this id was
        // proxied to Dark Link, which hid the room; now it renders the room itself.
        [0x0136] = ("object_blkobj", new[] { 0x14E0, 0x7EB0 }),
        // Bg_Toki_Hikari 0x006A = "Temple of Time windows": shares object_toki_objects with Bg_Toki_Swd
        // (the Master Sword pedestal), so auto-detect grabbed the LARGER sword mesh. Force the actual window
        // DL the actor draws (z_bg_toki_hikari.c: adult gSPDisplayList object_toki_objects_DL_008190).
        [0x006A] = ("object_toki_objects", new[] { 0x8190 }),
    };

    // Per-actor draw-scale overrides (object-space → world). Most actors are ~0.01; Bg/scenery actors
    // whose model is already world-sized draw near 1.0, so the default shrinks them to a dot.
    private static readonly Dictionary<ushort, float> OotActorScale = new()
    {
        [0x0136] = 1.0f,    // En_Blkobj illusion room — world-sized Bg geometry
        [0x006A] = 1.0f,    // Bg_Toki_Hikari ToT windows — world-sized Bg geometry (drawn at scale 1, no Actor_SetScale)
        [0x008E] = 0.01f,   // En_Floormas (Floormaster): full size. The scale parser otherwise picks up the
                            // SetupSplit Actor_SetScale(0.004f) and renders the tiny split-hand size.
    };

    // MM actors whose real Actor_SetScale uses a NON-LITERAL the parser can't evaluate (a variable / array),
    // so the table would fall to the 0.01 default. Hand-carried from the decomp for in-game-accurate size.
    private static readonly Dictionary<ushort, float> MmActorScale = new()
    {
        [0x0188] = 0.008f,  // En_Trt (Magic Hag / potion-shop witch "Koume"): Actor_SetScale(&this->actor,
                            // sActorScale=0.008f). The old parser instead grabbed the displayed wares'
                            // this->items[i]->actor.scale=0.2f and drew her ~25x oversized.
    };

    // Obj_Tokeidai (MM clock-tower actor, id 0x019C) sizes itself per type = (params & 0xF000) >> 12.
    // The same overlay is the giant Clock Town tower, the Termina-field gears, AND the little wall clocks
    // hung in shops; their scales span 0.01..1.0. A flat table entry (the parser picked the first
    // Actor_SetScale = 0.15) blew the wall clocks up into a roomfilling clock face. Mirror z_obj_tokeidai.c.
    private static float TokeidaiScale(ushort param) => ((param & 0xF000) >> 12) switch
    {
        4 or 5 or 6 => 0.15f,   // EXTERIOR_GEAR / TOWER_CLOCK / COUNTERWEIGHT (Termina Field)
        8 => 1.0f,              // TOWER_WALLS_TERMINA_FIELD
        9 => 0.02f,             // WALL_CLOCK (shop wall clock)
        10 => 0.01f,            // SMALL_WALL_CLOCK
        _ => 0.1f,              // CLOCK_TOWN variants + staircase: InitChain ICHAIN_VEC3F_DIV1000(scale,100)
    };

    // En_Fall (the Moon) loads its model object by EN_FALL_TYPE = (params & 0xF80) >> 7 (z_en_fall.c).
    // The crash-effect types (fire ball / rising debris / fire ring) are particle effects, not the moon
    // sphere, and ReadBestModel would draw them as the moon — return null so they stay billboards.
    private static string? EnFallObject(ushort param) => ((param & 0xF80) >> 7) switch
    {
        5 or 6 or 12 => "object_lodmoon",   // LODMOON_NO_LERP / LODMOON / LODMOON_INVERTED_STONE_TOWER
        8 => "object_moonston",             // MOONS_TEAR (the falling-star item)
        10 => "object_fall2",               // STOPPED_MOON_OPEN_MOUTH
        3 or 4 or 11 => null,               // CRASH_FIRE_BALL / RISING_DEBRIS / FIRE_RING — effects, not a model
        _ => "object_fall",                 // TERMINA_FIELD_MOON / CLOCK_TOWER_MOON / etc. (the Majora moon)
    };

    // Explicit display lists to render for an object whose skeleton the auto-detector can't use
    // (e.g. the chest's flex/curve skeleton renders flat) — draw its static parts directly instead.
    private static readonly Dictionary<string, int[]> ObjectDlOverride = new(StringComparer.OrdinalIgnoreCase)
    {
        // Treasure chest: front + side/lid DLs (gTreasureChestChestFrontDL, ...ChestSideAndLidDL).
        ["object_box"] = new[] { 0x6F0, 0x10C0 },
    };

    // Per-DL transform for ObjectDlOverride models whose DLs are drawn at skeleton limbs (not the origin).
    // The chest (gTreasureChestSkel + closed anim gTreasureChestAnim_000128 frame 0, from --diagchestskel):
    // EnBox_PostLimbDraw draws the body face DL at limb 1 and the side/lid DL at limb 3, each at that limb's
    // posed world matrix (vert → R·v + jointPos). Limb 1 = jointPos(0,2733,1982) rot(0,-90°,0); limb 3 =
    // jointPos(0,2700,0) rot 0. Reproduce those so the lid sits closed on top instead of flat on the floor.
    private static readonly Dictionary<(string, int), Func<OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3>> DlTransform = new()
    {
        // Body face DL already sits on the floor in actor space (no transform). The lid DL is the open lid,
        // authored hanging below the origin (Y[-1742,0]) and rotated 90° from the body. Flip it curve-up and
        // align to the body (Rx 180° then Ry 90° → (x,y,z)↦(-z,-y,-x)), then seat it on the body's top rim.
        [("object_box", 0x10C0)] = v => new OpenTK.Mathematics.Vector3(-v.Z, -v.Y, -v.X) + new OpenTK.Mathematics.Vector3(0, 2530, 1962),
        // Dodongo / Spirit metal bars live in the door's own object; push them to the door's front face (+Z)
        // like the generic gameplay_keep bars, so they read as a grille over the panel, not embedded/behind.
        [("object_ddan_objects", 0x1F0)] = v => v + new OpenTK.Mathematics.Vector3(0, 0, 40f),
        [("object_jya_door", 0x1F0)]     = v => v + new OpenTK.Mathematics.Vector3(0, 0, 40f),
    };

    // Explicit frame-0 idle animation per object, for skeletons the auto-detector can't pose (their
    // bind pose collapses). Queen Gohma's gGohmaStandAnim.
    private static readonly Dictionary<string, int> ObjectAnimOffset = new(StringComparer.OrdinalIgnoreCase)
    {
        ["object_goma"] = 0xAE8,       // gGohmaStandAnim (Queen Gohma)
        // Creatures whose idle animation the heuristic missed (rendered as a bind-pose tangle). Frame 0
        // of these un-tangles the skeleton into a recognisable pose. Extracted from the decomp.
        ["object_bl"]   = 0xA4,        // En_Bili (Biri) — gBiriDefaultAnim
        ["object_brob"] = 0x1750,      // En_Brob (Flobbery Muscle Block) — object_brob_Anim_001750 idle/rest
        ["object_dekubaba"] = 0x2B8,   // Deku Baba — gDekuBabaFastChompAnim
        ["object_fd"]   = 0x115E4,     // Volvagia — gVolvagiaHeadEmergeAnim
        ["object_fish"] = 0x453C,      // Fishing — gFishingOwnerAnim
        ["object_sb"]   = 0x194,       // En_Sb (Shell Blade)
        ["object_st"]   = 0x304,       // En_Sw (Skullwalltula / Gold Skulltula) — object_st_Anim_000304 idle
        ["object_vm"]   = 0x68,        // En_Vm (Beamos)
        // Epona / En_Horse: a Skin-system skeleton (not standard SkelAnime); its bind pose collapses into
        // a crumpled tangle. gEponaIdleAnim frame 0 stands her up. Same object in OoT and MM.
        ["object_horse"] = 0x6D50,     // gEponaIdleAnim (Epona standing idle)
        // MM creatures/bosses whose idle the heuristic missed.
        ["object_bee"]      = 0x5C,    // En_Bee — gBeeFlyingAnim
        ["object_boss02"]   = 0x9C78,  // Twinmold — gTwinmoldHeadFlyAnim
        ["object_boss04"]   = 0x4C,    // Wart — gWartIdleAnim
        ["object_drs"]      = 0x1C,    // En_Drs (Wedding Dress Mannequin)
        ["object_ds2n"]     = 0x8038,  // En_Ds2n (Potion Shop Proprietor)
        ["object_po_fusen"] = 0x40,    // En_Po_Fusen (Poe Balloon)
        // Composite-NPC body objects with an embedded standing/idle animation (used by En_Ossan).
        ["object_ossan"]    = 0x338,   // gObjectOssanAnim_000338 (bearded shopkeeper)
        ["object_ds2"]      = 0x2E4,   // object_ds2_Anim_0002E4 (potion-shop granny)
        // Kaepora Gaebora perches at rest: use the perch animation with the perching skeleton (SkelOverride).
        ["object_owl"]      = 0xC8A0,  // gOwlPerchAnim
        // MM Gekko (En_Pametfrog) inits in a boxing stance; gGekkoStandingIdleAnim is its upright rest.
        ["object_bigslime"] = 0x4680,  // gGekkoStandingIdleAnim (paired with gGekkoSkel below)
    };

    // CROSS-OBJECT idle animation: the actor's skeleton is in one object but its idle animation lives in a
    // DIFFERENT object it also bank-loads. En_Ossan's shopkeepers: the Kokiri clerk uses gKm1Skel (object_km1)
    // posed by object_masterkokiri_Anim_0004A8; the Goron/Zora clerks likewise from object_mastergolon/zoora.
    // Keyed by the SKELETON object → (anim object, anim offset). Without this those clerks render at bind pose.
    private static readonly Dictionary<string, (string animObj, int off)> ObjectAnimSource = new(StringComparer.OrdinalIgnoreCase)
    {
        ["object_km1"] = ("object_masterkokiri", 0x4A8),   // En_Ossan Kokiri-shop clerk
    };

    // Actors whose object holds MORE THAN ONE skeleton, where the resting/spawn form isn't the one
    // FindSkeleton scores highest. object → the skeleton header offset to use. Pair with its idle anim in
    // ObjectAnimOffset so frame 0 poses the RIGHT skeleton (the two skeletons have different limb layouts).
    private static readonly Dictionary<string, int> SkelOverride = new(StringComparer.OrdinalIgnoreCase)
    {
        ["object_owl"] = 0x100B0,       // gOwlPerchingSkel (perched) instead of gOwlFlyingSkel (wings splayed)
        ["object_bigslime"] = 0xDF98,   // gGekkoSkel (the Gekko), not the slime skeleton, in the shared object
    };

    // MM-specific overrides, checked BEFORE the shared dicts when !_oot. Needed where an object NAME exists in
    // BOTH games with different offsets: MM object_owl (Kaepora's MM cousin) perches at rest like the OoT owl,
    // but its perching skeleton/anim sit at different offsets (gOwlPerchingSkel 0x105C0 / gOwlPerchAnim 0xCDB0).
    private static readonly Dictionary<string, int> MmSkelOverride = new(StringComparer.OrdinalIgnoreCase)
    {
        ["object_owl"] = 0x105C0,       // MM gOwlPerchingSkel (not gOwlFlyingSkel)
    };
    private static readonly Dictionary<string, int> MmObjectAnimOffset = new(StringComparer.OrdinalIgnoreCase)
    {
        ["object_owl"] = 0xCDB0,        // MM gOwlPerchAnim
    };

    // Composite NPCs: one actor id picks its body skeleton from DIFFERENT objects based on its spawn
    // variable, loading them dynamically — so the decomp ActorObjectTable (which keys off the InitVars
    // OBJECT_GAMEPLAY_KEEP) skips them and the editor drew a lens-of-truth billboard. Map (id, var) to
    // the body object the game would load. See the composite-entities note for the eventual editor UI.
    private static readonly string[] OssanObjects =
    {
        "object_km1",   // 0 Kokiri shop
        "object_ds2",   // 1 Kakariko potion
        "object_rs",    // 2 Bombchu shop (running man)
        "object_ds2",   // 3 Market potion
        "object_ossan", // 4 Bazaar
        "object_ossan", // 5 Adult (market)
        "object_ossan", // 6 Talon→Ingo
        "object_zo",    // 7 Zora
        "object_of1d_map", // 8 Goron
        "object_ossan", // 9 Ingo
        "object_os",    // 10 Happy Mask salesman
    };

    /// <summary>For a composite NPC, the body object the game loads for this spawn variable, else null.</summary>
    // Freestanding items (En_Item00, id 0x0015) whose vanilla draw is a genuine 3D gameplay_keep model —
    // render that model so the editor shows the spinning item exactly as in-game (the user's "represented
    // by its model, same way as a heart piece"). Keyed by Item00Type = params low byte (z64actor.h ITEM00_*).
    // Returns (object, display-list offset(s) in gameplay_keep, object→world scale = the actor's SetScale).
    // The other Item00 types (bombs/nuts/sticks/arrows/seeds/magic/key/recovery-heart/bombchu) are 2D
    // billboard "drops" in vanilla, so their item-icon billboard already matches how they look in-game.
    private static (string obj, int[] dls, float scale)? FreestandingItemModel(ushort var)
    {
        int t = var & 0xFF;
        return t switch
        {
            0x00 or 0x01 or 0x02 => ("object_gameplay_keep", new[] { 0x45150 }, 0.015f),          // gRupeeDL (green/blue/red)
            0x13                 => ("object_gameplay_keep", new[] { 0x45150 }, 0.045f),          // orange / huge (200) rupee
            0x14                 => ("object_gameplay_keep", new[] { 0x45150 }, 0.030f),          // purple (50) rupee
            0x03                 => ("object_gi_heart", new[] { 0x00E0 }, 0.020f),                // recovery heart (gGiRecoveryHeartDL)
            0x06                 => ("object_gameplay_keep", new[] { 0x3B030 }, 0.020f),          // gHeartPieceInteriorDL (was 0x3B860, wrong)
            0x07                 => ("object_gameplay_keep", new[] { 0x3C3D0, 0x3C508 }, 0.020f), // heart container: ext + int
            _ => null,
        };
    }

    // #4 MM: variant actors whose object changes by params. En_Ossan (0x02A) shop man / part-timer;
    // En_Sekihi (0x15C) the graves + soaring pedestal (static-DL props, no skeleton/pose needed).
    private static readonly string[] MmSekihiObjects =
        { "object_sekihil", "object_sekihig", "object_sekihin", "object_sekihiz", "object_zog" };

    // #4 MM En_Test2 (0x158) — lens-of-truth props; params (0..12) picks the object (sObjectIds). The
    // hakugin variants share one object (different DLs) — the object's main model is a fine preview.
    private static readonly string[] MmTest2Objects =
    {
        "object_dekucity_ana_obj", "object_sichitai_obj", "object_yukimura_obj", "object_hakugin_obj",
        "object_hakugin_obj", "object_hakugin_obj", "object_meganeana_obj", "object_haka_obj",
        "object_haka_obj", "object_hakugin_obj", "object_hakugin_obj", "object_hakugin_obj", "object_hakugin_obj",
    };

    // Obj_Switch (0x012A) display lists in gameplay_dangeon_keep, by type=(params&7)/subtype=(params>>4&7).
    // z_obj_switch.c: FLOOR draws floorSwitchDLists[subtype], EYE eyeDlists[subtype] with a gold/silver eye
    // texture on seg 8, CRYSTAL the core/diamond opaque + translucent shell. Offsets verified vs the XML.
    private static (int[] dls, int seg8) ObjSwitchModel(ushort var)
    {
        int type = var & 7, sub = (var >> 4) & 7;
        return type switch
        {
            0 => (new[] { new[] { 0x5800, 0x6170, 0x5D50, 0x5D50 }[Math.Clamp(sub, 0, 3)] }, -1),  // FLOOR (1/3/2/2)
            1 => (new[] { 0x5AD0 }, -1),                                                            // FLOOR_RUSTY
            2 => (new[] { sub == 1 ? 0x6810 : 0x6610 }, sub == 1 ? 0xB0A0 : 0xA8A0),                // EYE (silver/gold)
            3 or 4 => sub == 1 ? (new[] { 0x7340, 0x7488 }, -1)                                     // CRYSTAL diamond
                               : (new[] { 0x6D10, 0x6E60 }, -1),                                    // CRYSTAL core
            _ => (new[] { 0x6D10, 0x6E60 }, -1),
        };
    }

    // MM Obj_Switch (0x0093) display lists in the MM gameplay_dangeon_keep (offsets GC_US, verified vs XML).
    // z_obj_switch.c (2ship): type=(params&7)/subtype=(params>>4&7). FLOOR/FLOOR_LARGE draw sFloorSwitchDL
    // [subtype], EYE sEyeSwitchDL[subtype] + a gold/silver eye texture on seg 8, CRYSTAL base+core+diamond.
    private static (int[] dls, int seg8) ObjSwitchModelMm(ushort var)
    {
        int type = var & 7, sub = (var >> 4) & 7;
        return type switch
        {
            0 or 5 => (new[] { new[] { 0x1B508, 0x1B9F8, 0x1B788, 0x1B788, 0x1B508 }[Math.Clamp(sub, 0, 4)] }, -1),  // FLOOR / FLOOR_LARGE
            1 => (new[] { 0x7E00 }, -1),                                                    // FLOOR_RUSTY
            2 => (new[] { sub == 1 ? 0x85F0 : 0x83F0 }, sub == 1 ? 0xB6C0 : 0xAEC0),        // EYE gold/silver (+seg8 tex)
            3 or 4 => (new[] { 0x1C058, 0x1BEE0, 0x1BFB8 }, -1),                            // CRYSTAL base + core + diamond
            _ => (new[] { 0x1C058, 0x1BEE0, 0x1BFB8 }, -1),
        };
    }

    // MM Obj_Switch per-type Actor_SetScale (z_obj_switch.c sScale), indexed by type=(params&7).
    private static float ObjSwitchScaleMm(ushort var) =>
        new[] { 0.123f, 0.123f, 0.1f, 0.118f, 0.118f, 0.248f }[Math.Clamp(var & 7, 0, 5)];

    // Per-actor texture-segment bindings: many actors bind extra textures to segments 8-D in their C draw
    // code (gSPSegment), which the DL then SETTIMGs — without these the affected limbs render untextured.
    // Returns a 16-entry array (index = segment; -1 = unbound) of object-file offsets, or null when none.
    // The bulk of these come from OotActorSegTex (auto-extracted from the decomp draw code); a few whose
    // binding depends on params (variant tektite, etc.) are resolved here first.
    private static int[]? SegTexFor(bool oot, ushort id, ushort var)
    {
        if (oot)
        {
            switch (id)
            {
                case 0x001B:   // En_Tite tektite: BLUE(0) vs the red/desert set (z_en_tite.c draw)
                {
                    var t = NewSegTex();
                    if ((var & 0xFF) == 0) { t[8] = 0x1300; t[9] = 0x1700; t[0xA] = 0x1900; }
                    else                   { t[8] = 0x1B00; t[9] = 0x1F00; t[0xA] = 0x2100; }
                    return t;
                }
                case 0x014D:   // En_Owl Kaepora Gaebora: seg 8 = the open-eye texture (eyeTextures[0])
                {
                    var t = NewSegTex(); t[8] = 0x89A8; return t;
                }
            }
            // Bg_Mori_* (Forest Temple): geometry in object_mori_objects binds seg8 to the separately
            // bank-loaded object_mori_tex at base 0 → seg8 offset = the DL's raw offset (file via SegTexFileFor).
            if (MoriTexActors.Contains(id)) return Seg((8, 0));
            if (OotActorSegTex.TryGetValue(id, out var seg)) return seg;
        }
        else if (MmActorSegTex.TryGetValue(id, out var seg)) return seg;
        return null;
    }

    // Forest Temple room actors whose geometry object (object_mori_objects) textures via the separate
    // object_mori_tex bound to seg8 (Bg_Mori_Hineri/Bigst/Elevator/Kaitenkabe/Rakkatenjo/Hashigo/Hashira4).
    private static readonly HashSet<ushort> MoriTexActors = new()
    { 0x0068, 0x0086, 0x0087, 0x0088, 0x0089, 0x00E2, 0x00E3 };

    private int _moriTexFileIndex = -2;   // -2 = not yet resolved
    private int[]? SegTexFileFor(ushort id)
    {
        if (!_oot || !MoriTexActors.Contains(id)) return null;
        if (_moriTexFileIndex == -2)
            _moriTexFileIndex = _objects.Resolve("object_mori_tex") is { } v ? FileIndexOf(v.start) : -1;
        if (_moriTexFileIndex < 0) return null;
        var a = new int[16]; Array.Fill(a, -1); a[8] = _moriTexFileIndex; return a;
    }

    private static int[] NewSegTex() { var a = new int[16]; Array.Fill(a, -1); return a; }

    // Static seg-8..D texture bindings extracted from the decomp draw code (actor id → 16-entry offset
    // array, into the actor's OWN object). The unconditional single-offset cases; variant/animated ones
    // (tektite, NPC eyes/mouths handled by FaceTexFor) are elsewhere. Keep-object binds are omitted (this
    // resolves into the actor object, not a keep).
    // COMPREHENSIVE seg-8..D texture bindings: scanned EVERY OoT overlay's draw code + resolved each
    // gSPSegment(SEGMENTED_TO_VIRTUAL(eyeTextures[i]...)) array's first element vs the object XML texture
    // Offset (NOT TlutOffset), into the actor's OWN object. The broad fix for missing NPC faces/eyes/mouths.
    // (scratchpad/facetex_extract.py, all ids). gameplay_keep-bound + Gfx-array (lightswitch) actors excluded
    // — they need version-probed / cross-object handling. Special non-face binds kept at top.
    private static readonly Dictionary<ushort, int[]> OotActorSegTex = new()
    {
        [0x008D] = Seg((0x9, 0x173D0), (0xA, 0x17BD0)),  // Bg_Hidan_Fwbig (object_hidan_objects)
        [0x00C4] = Seg((0x8, 0x51DB0)),  // Boss_Mo Morpha (object_mo)
        [0x000B] = Seg((0x8, 0x17930), (0x9, 0x17930), (0xA, 0x19130)),  // Bg_Dy_Yoseizo (object_dy_obj)
        [0x000C] = Seg((0x8, 0x15D20)),  // Bg_Hidan_Firewall (object_hidan_objects)
        [0x0014] = Seg((0x8, 0x9F80)),  // En_Horse (object_horse)
        [0x0034] = Seg((0x8, 0xE08)),  // En_Bili (object_bl)
        [0x0035] = Seg((0x8, 0xC68)),  // En_Tp (object_tp)
        [0x0043] = Seg((0x8, 0x12120)),  // Bg_Hidan_Rock (object_hidan_objects)
        [0x0048] = Seg((0x8, 0x58C0), (0x9, 0x58C0)),  // En_Xc Sheik (object_xc)
        [0x005A] = Seg((0x8, 0x7698)),  // En_Jj (object_jj)
        [0x006B] = Seg((0x8, 0xAF0)),  // En_Yukabyun (object_yukabyun)
        [0x0084] = Seg((0x8, 0x7F80), (0x9, 0x6DC0)),  // En_Ta Talon (object_ta)
        [0x0085] = Seg((0x8, 0x3B40)),  // En_Tk Dampe (object_tk)
        [0x0096] = Seg((0x9, 0x38A8)),  // Boss_Fd Volvagia (object_fd)
        [0x0098] = Seg((0x8, 0x8080), (0x9, 0x8C80), (0xA, 0x7FC0)),  // En_Du Darunia (object_du)
        // En_Zl3 adult Zelda binds seg8=eye, seg9=eye (both gZelda2EyeOpenTex@0x30C8), segA=mouth
        // (gZelda2MouthSeriousTex@0x3508). Mouth-on-segA (not seg9) is why the eye/mouth fallback left it blank.
        [0x0179] = Seg((0x8, 0x30C8), (0x9, 0x30C8), (0xA, 0x3508)),  // En_Zl3 adult Zelda (object_zl2)
        [0x009A] = Seg((0x8, 0x1D28)),  // En_Horse_Link_Child (object_horse_link_child)
        [0x009C] = Seg((0x8, 0x96B0)),  // Bg_Spot02_Objects (object_spot02_objects)
        [0x00A2] = Seg((0x9, 0x2B08)),  // Boss_Fd2 Volvagia (object_fd2)
        [0x00BA] = Seg((0x8, 0x96F8)),  // Boss_Va Barinade (object_bv)
        [0x00C3] = Seg((0x8, 0xD8E8), (0x9, 0xD8E8)),  // En_Nb Nabooru (object_nb)
        [0x00CB] = Seg((0x8, 0x3590), (0x9, 0x34D0)),  // En_In Ingo (object_in)
        [0x00CC] = Seg((0x8, 0x86D8)),  // En_Tr Twinrova (object_tr)
        [0x00D9] = Seg((0x8, 0x2570), (0x9, 0x2970)),  // En_Ma2 child Malon (object_ma2)
        [0x00DE] = Seg((0x8, 0x24F0)),  // En_Ba Jabu tentacle (object_bxa)
        [0x00DF] = Seg((0x8, 0x24F0)),  // En_Bx Jabu tentacle (object_bxa)
        [0x00E7] = Seg((0x8, 0x1B18), (0x9, 0x1F18)),  // En_Ma1 child Malon (object_ma1)
        [0x00E8] = Seg((0x8, 0x9A20)),  // Boss_Ganon (object_ganon)
        [0x00ED] = Seg((0x8, 0x59A0), (0x9, 0x59A0)),  // En_Fr (object_fr)
        [0x00FE] = Seg((0x8, 0x9250)),  // Fishing (object_fish)
        [0x0122] = Seg((0x8, 0x3B40)),  // En_Po_Relay (object_tk)
        [0x0124] = Seg((0x8, 0x3E40)),  // En_Diving_Game Zora (object_zo)
        [0x0138] = Seg((0x8, 0x708)),  // En_Ge1 Gerudo (object_ge1)
        [0x013C] = Seg((0x8, 0x8C8)),  // En_Niw_Lady cucco lady (object_ane)
        [0x013D] = Seg((0x8, 0xCE80), (0x9, 0xDE80)),  // En_Gm Goron (object_of1d_map)
        [0x0146] = Seg((0x8, 0x2F48), (0x9, 0x2F48), (0xA, 0x3588)),  // En_Sa Saria (object_sa)
        [0x014B] = Seg((0x8, 0x4110)),  // En_Bom_Bowl_Man (object_bg)
        [0x0152] = Seg((0x8, 0xCE80), (0x9, 0xDE80)),  // En_Go Goron (object_of1d_map)
        [0x0153] = Seg((0x8, 0x5F20), (0x9, 0x6720)),  // En_Fu Windmill man (object_fu)
        [0x0156] = Seg((0x8, 0xD00), (0x9, 0x1500)),  // Bg_Jya_Megami (object_jya_obj)
        [0x0162] = Seg((0x8, 0xE30)),  // En_Mm Running Man (object_mm)
        [0x0164] = Seg((0x8, 0x1470)),  // En_Kz King Zora (object_kz)
        [0x0167] = Seg((0x8, 0x408)),  // En_Ani (object_ani)
        [0x016C] = Seg((0x8, 0x2130)),  // En_Cs Graveyard kid (object_cs)
        [0x016D] = Seg((0x8, 0x4FF0)),  // En_Md Mido (object_md)
        [0x017C] = Seg((0x8, 0x970)),  // En_Takara_Man (object_ts)
        [0x0186] = Seg((0x8, 0x4F78)),  // En_Ge2 Gerudo (object_gla)
        [0x0188] = Seg((0x8, 0x7E0)),  // En_Ssh Skulltula-House man (object_ssh)
        [0x0197] = Seg((0x8, 0x5FE8)),  // En_GeldB Gerudo (object_geldb)
        [0x019A] = Seg((0x8, 0x4178)),  // En_Niw_Girl Cucco girl (object_gr)
        [0x01A2] = Seg((0x8, 0x30A0)),  // En_Dnt_Jiji Deku elder (object_dns)
        [0x01A4] = Seg((0xA, 0x5FC)),  // En_Guest (object_boj)
        [0x01AE] = Seg((0x8, 0xDA80), (0x9, 0xDE80)),  // En_Go2 Goron (object_of1d_map)
        [0x01AF] = Seg((0x8, 0x7B68)),  // En_Wf Wolfos (object_wf)
        [0x01C5] = Seg((0x8, 0x2570), (0x9, 0x2970)),  // En_Ma3 adult Malon (object_ma2)
        [0x01CE] = Seg((0x8, 0x3E40)),  // En_Zo Zora (object_zo)
        [0x01D0] = Seg((0x8, 0x5FE8)),  // En_Ge3 Gerudo (object_geldb)
        [0x01D3] = Seg((0x8, 0x2AF0), (0x9, 0x2AF0), (0xA, 0x46F0)),  // En_Zl4 adult Zelda (object_zl4)
        [0x01D4] = Seg((0x8, 0xE30)),  // En_Mm2 Running Man (object_mm)
    };
    // MM NPC face/body textures bound to seg 8/9/A via gSPSegment(Lib_SegmentedToVirtual(eyeTextures[i]...)),
    // first frame. COMPREHENSIVE extraction: scanned EVERY MM overlay's draw code + resolved the array's
    // first element vs the GC_US object XML texture Offset (NOT TlutOffset), into the actor's own object.
    // This is the broad fix for the whole "missing MM face" class (scratchpad/facetex_extract_mm.py, all ids).
    private static readonly Dictionary<ushort, int[]> MmActorSegTex = new()
    {
        [0x002A] = Seg((0x8, 0x5BC0)),  // En_Ossan (object_fsn)
        [0x0054] = Seg((0x8, 0x1D28)),  // En_Horse_Link_Child (object_horse_link_child)
        [0x0067] = Seg((0x8, 0x35E0), (0x9, 0x3520)),  // En_In (object_in)
        [0x0069] = Seg((0x8, 0xF20)),  // En_Ru (object_ru2)
        [0x0079] = Seg((0x8, 0x9250)),  // En_Fishing (object_fish)
        [0x008A] = Seg((0x8, 0x28E8)),  // En_Dns (object_dns)
        [0x009F] = Seg((0x8, 0x3078)),  // En_Ge1 (object_ge1)
        [0x00A4] = Seg((0x8, 0x54A8)),  // En_Gm (object_in2)
        [0x00AF] = Seg((0x8, 0x8EB8)),  // En_Owl (object_owl)
        [0x00BD] = Seg((0x8, 0x6498)),  // En_Ani (object_ani)
        [0x00F8] = Seg((0x8, 0x50A0)),  // En_Zo (object_zo)
        [0x00FA] = Seg((0x8, 0x6398)),  // En_Ge3 (object_geldb)
        [0x0110] = Seg((0x8, 0x3CA0)),  // Bg_Fire_Wall (object_fwall)
        [0x0117] = Seg((0xA, 0x658)),  // En_Aob_01 (object_aob)
        [0x011C] = Seg((0x8, 0xC520), (0x9, 0xE620)),  // En_Bom_Bowl_Man (object_cs)
        [0x011D] = Seg((0x8, 0x5AC8), (0x9, 0x5AC8)),  // En_Syateki_Man (object_shn)
        [0x0124] = Seg((0x8, 0x49F0)),  // En_Bji_01 (object_bji)
        [0x012A] = Seg((0x8, 0x3A0)),  // Boss_02 Twinmold (object_boss02)
        [0x012F] = Seg((0x8, 0x42330)),  // Boss_07 (object_boss07)
        [0x0135] = Seg((0x8, 0x50A0)),  // En_Sob1 (object_zo)
        [0x0138] = Seg((0x8, 0x10438)),  // En_Go (object_of1d_map)
        [0x0141] = Seg((0x8, 0x7AA8)),  // En_Syateki_Wf (object_wf)
        [0x0147] = Seg((0x8, 0x59A0), (0x9, 0x59A0)),  // En_Fg (object_fr)
        [0x0152] = Seg((0x8, 0x2AF0), (0x9, 0x2AF0), (0xA, 0x46F0)),  // Dm_Zl (object_zl4)
        [0x0168] = Seg((0x8, 0x1140)),  // En_Dnh (object_tro)
        [0x017A] = Seg((0x8, 0x1680)),  // En_Look_Nuts (object_dnk)
        [0x017D] = Seg((0x8, 0x2950)),  // En_Mm3 (object_mm)
        [0x0187] = Seg((0x8, 0x18FA0), (0x9, 0x18FA0)),  // En_Tru (object_tru)
        [0x0188] = Seg((0x8, 0xB0B8), (0x9, 0xB0B8)),  // En_Trt (object_trt)
        [0x018D] = Seg((0x8, 0xF918), (0x9, 0x16018)),  // En_Az (object_az)
        [0x019A] = Seg((0x8, 0xD2D8), (0x9, 0xD2D8)),  // Dm_Char08 Great Turtle (object_kamejima)
        [0x019E] = Seg((0x8, 0x15020)),  // En_Mnk (object_mnk)
        [0x01A0] = Seg((0x8, 0x1680)),  // En_Guard_Nuts (object_dnk)
        [0x01B7] = Seg((0x8, 0xB0B8), (0x9, 0xB0B8)),  // En_Trt2 (object_trt)
        [0x01C2] = Seg((0x8, 0x73B8), (0x9, 0x73B8)),  // En_Tsn (object_tsn)
        [0x01C3] = Seg((0x8, 0x5E18), (0x9, 0x5E18)),  // En_Ds2n (object_ds2n)
        [0x01C4] = Seg((0x8, 0x5BC0), (0x9, 0x5BC0)),  // En_Fsn (object_fsn)
        [0x01CA] = Seg((0x8, 0x4390)),  // En_Tk (object_tk)
        [0x01D4] = Seg((0x8, 0x1AA0)),  // En_Snowwd (object_snowwd)
        [0x01D5] = Seg((0x8, 0x2950)),  // En_Pm (object_mm)
        [0x01DB] = Seg((0x8, 0x5A80)),  // En_Giant (object_giant)
        [0x01F5] = Seg((0x8, 0x3430)),  // En_Zoraegg (object_zoraegg)
        [0x01F7] = Seg((0x8, 0x9260)),  // En_Gg (object_gg)
        [0x01FA] = Seg((0x8, 0x9260)),  // En_Gg2 (object_gg)
        [0x01FC] = Seg((0x8, 0x103D0)),  // En_Dnp (object_dnq)
        [0x01FD] = Seg((0x8, 0x107B0)),  // En_Dai (object_dai)
        [0x0201] = Seg((0x8, 0xF720)),  // En_Gk (object_gk)
        [0x0214] = Seg((0x8, 0x18FA0), (0x9, 0x18FA0)),  // En_Tru_Mt (object_tru)
        [0x0215] = Seg((0x8, 0x164D0), (0x9, 0x17CD0)),  // Obj_Um (object_um)
        [0x0220] = Seg((0x8, 0x11AD8), (0x9, 0x14AD8)),  // En_Ma_Yto (object_ma2)
        [0x0228] = Seg((0x8, 0x50A0)),  // En_Zot (object_zo)
        [0x0234] = Seg((0x8, 0x8AE8)),  // En_Toto (object_zm)
        [0x0236] = Seg((0x8, 0x92A0)),  // En_Baba (object_bba)
        [0x023A] = Seg((0x8, 0x10438)),  // En_Geg (object_of1d_map)
        [0x0244] = Seg((0xA, 0x62B0)),  // En_Ja (object_boj)
        [0x0251] = Seg((0x8, 0x11138)),  // En_Hgo (object_harfgibud)
        [0x0253] = Seg((0x8, 0x6D70), (0x9, 0x8D70)),  // En_Ah (object_ah)
        [0x0260] = Seg((0x8, 0x50A0)),  // En_Zow (object_zo)
        [0x0263] = Seg((0x8, 0x6428)),  // En_Tab (object_tab)
        [0x026F] = Seg((0x8, 0x7350), (0x9, 0x7750)),  // En_Dt (object_dt)
        [0x0276] = Seg((0x8, 0x107B0)),  // En_Ig (object_dai)
        [0x0277] = Seg((0x8, 0x10438)),  // En_Rg (object_of1d_map)
        [0x027B] = Seg((0x9, 0xBC50)),  // En_Rz (object_rz)
        [0x027E] = Seg((0x8, 0xC520), (0x9, 0xE620)),  // En_Bomjima (object_cs)
        [0x027F] = Seg((0x8, 0xC520), (0x9, 0xE620)),  // En_Bomjimb (object_cs)
        [0x0280] = Seg((0x8, 0xC520), (0x9, 0xE620)),  // En_Bombers (object_cs)
        [0x0281] = Seg((0x8, 0xC520)),  // En_Bombers2 (object_cs)
        [0x0292] = Seg((0x8, 0x73B8), (0x9, 0x73B8)),  // En_Jgame_Tsn (object_tsn)
        [0x0299] = Seg((0x9, 0x9DF0)),  // En_And (object_and)
        [0x029F] = Seg((0x8, 0x6D70), (0x9, 0x8D70)),  // Dm_Ah (object_ah)
        [0x02A2] = Seg((0x8, 0x7350), (0x9, 0x7750)),  // En_Ending_Hero (object_dt)
        [0x02B1] = Seg((0x8, 0x5458)),  // En_Rsn (object_rsn)
    };

    private static int[] Seg(params (int seg, int off)[] binds)
    {
        var a = new int[16]; Array.Fill(a, -1);
        foreach (var (seg, off) in binds) a[seg] = off;
        return a;
    }

    private string? CompositeObject(ushort id, ushort var)
    {
        if (!_oot)
        {
            return id switch
            {
                0x002A => (var & 0xFF) == 0 ? "object_fsn" : "object_ani",   // MM En_Ossan: shop man / part-timer
                0x015C => MmSekihiObjects[Math.Clamp(var & 0xF, 0, MmSekihiObjects.Length - 1)],  // MM En_Sekihi graves
                0x0158 => MmTest2Objects[Math.Clamp(var, 0, MmTest2Objects.Length - 1)],          // MM En_Test2 lens props
                _ => null,
            };
        }
        switch (id)
        {
            case 0x003D:   // En_Ossan — every shopkeeper, params = OSSAN_TYPE index
                int t = var & 0xFF;
                return OssanObjects[Math.Clamp(t, 0, OssanObjects.Length - 1)];
            case 0x0163:   // En_Ko — Kokiri children; type (params & 0xFF) picks a boy (km1) or girl (kw1) body
                return EnKoBodies[Math.Clamp(var & 0xFF, 0, EnKoBodies.Length - 1)];
            case 0x016E:   // En_Hy — generic townsfolk; type (params & 0x7F) picks the body skeleton object
                return EnHyBodies[Math.Clamp(var & 0x7F, 0, EnHyBodies.Length - 1)];
            case 0x01A3:   // En_Dnt_Nomal — Lost Woods scrub minigame: stage scrub (dnk) vs target (hintnuts)
                return (var & 0xFF) == 0 ? "object_dnk" : "object_hintnuts";
        }
        return null;
    }

    // A door attachment drawn at a transformed sub-matrix relative to the door (a key-lock / boss-key
    // lock + the four chain segments). `src` is the object the DL lives in ("keep5" = gameplay_dangeon_keep
    // for the small-key lock, "object_bdoor" for the boss-key lock); `xform` maps the DL's object-space
    // vertices into the door's object space (so the shared model scale then places it correctly).
    private readonly record struct DoorAttach(string src, int dl, Func<OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3> xform);

    // Build the chains+lock of a locked door exactly as Actor_DrawDoorLock does at frame 10 (fully closed):
    // four chain segments rotated around Z into a cross, then the central lock — all translated to
    // (0, yShift, 500) and scaled by (0.01,0.01,0.025) off the door matrix (z_actor.c sDoorLocksInfo).
    private static List<DoorAttach> BuildLock(string src, int chainDl, int lockDl,
                                              float chainAngle, float yShift, float chainsScale, float chainsRotZInit,
                                              bool actorLocal = false, float xShift = 0f, bool back = false)
    {
        // Lock side (editor preview): vanilla always draws the lock on the door's local +Z. `back` flips it to
        // the −Z face so the editor shows which side a LockBack door will present; the export rotates the door
        // 180° so the vanilla +Z lock lands on that same world side (see LockBack).
        float front = back ? -1f : 1f;
        // Actor_DrawDoorLock at frame 10 (fully closed): Matrix_Translate(0, yShift, 500) in the ACTOR matrix
        // (which already holds the actor's scale), then four RotateZ copies of the chain DL (the frame-10 chain
        // slide chainsTranslateX/Y evaluates to 0), then the lock DL at Scale(frame*0.1)=1.0. Two conventions:
        //   actorLocal (En_Door): the door renders at its true actor scale, so bake in actor-local units
        //     (vert + translate) and let the uniform model scale carry it to world — physically exact per decomp.
        //   else (Door_Shutter): that door's model is pre-scaled to ~world (editor model scale 1.0), so the lock
        //     is pre-scaled the same way — the historical calibration that already matches it. Do not disturb.
        // xShift re-centres the lock when the panel origin isn't the doorway centre (En_Door's leaf renders off
        // the hinge to +X, so the lock at X=0 would sit at the edge); it's in the same units as Place's output.
        // actorLocal also pre-applies the inverse of En_Door's model base rotation (90° about X — its DL is
        // modelled lying flat and stood up): the lock is authored in game convention (Y up, +Z front), so
        // apply RotX(-90): (x,y,z)->(x,z,-y). ModelWorldBounds/the renderer then re-apply +90°X, landing the
        // lock upright at the door's front in step with the panel.
        OpenTK.Mathematics.Vector3 Place(OpenTK.Mathematics.Vector3 v) => actorLocal
            ? new OpenTK.Mathematics.Vector3(v.X + xShift, front * -(v.Z + 500f), v.Y + yShift)
            : new OpenTK.Mathematics.Vector3((v.X + 0f) * 0.01f + xShift, (v.Y + yShift) * 0.01f, front * (v.Z + 500f) * 0.025f);
        var list = new List<DoorAttach>();
        float rotZ = chainsRotZInit;
        for (int i = 0; i < 4; i++)
        {
            float rz = rotZ, cs = chainsScale;
            list.Add(new DoorAttach(src, chainDl, p =>
            {
                var v = p * cs;                                                       // Scale(chainsScale)
                float c = MathF.Cos(rz), s = MathF.Sin(rz);                           // RotateZ(rz)
                return Place(new OpenTK.Mathematics.Vector3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z));
            }));
            rotZ += (i % 2 == 1) ? 2f * chainAngle : (MathF.PI - 2f * chainAngle);
        }
        list.Add(new DoorAttach(src, lockDl, Place));                                 // lock: Scale(frame*0.1)=1.0 at frame 10
        return list;
    }

    // The CLOSED door model for En_Door / Door_Shutter so a doorway shows the modelled+textured door
    // instead of a black frame. Returns the door's own object + DLs, plus `keep` = extra DLs read from
    // gameplay_keep (segment 4) and overlaid at the same origin — the criss-cross metal-bar LATTICE
    // (gDoorMetalBarsDL) that sits over a dungeon shutter (this is the "stained-glass"-looking grille on
    // the Forest Temple door) — plus `attach` = a key/boss-key lock + chains for a locked shutter.
    // Every offset verified from z_en_door.c sDoorInfo / z_door_shutter.c sObjectInfo+sGfxInfo + object XMLs.
    private (string obj, int[] dls, int[] keep, List<DoorAttach> attach)? DoorModel(ushort id, ushort var)
    {
        int[] none = System.Array.Empty<int>();
        var noAtt = new List<DoorAttach>();
        _doorSeg8 = -1;
        _doorLeafShift = OpenTK.Mathematics.Vector3.Zero;
        // The metal-bar grille (sShutterInfo `b` = gDoorMetalBarsDL / gDodongoBarsDL / gJabuWebDoorDL …) is
        // the barrier that bars the doorway: z_door_shutter.c draws it over the opening for the "lock behind
        // Link" door types (front-clear / front-switch / back-locked), and translates it up out of view for
        // a normal door. So a normal closed door shows the solid panel; a BARRED door shows the grille.
        if (_oot)
        {
            // En_Door (hinged/knob door): on console the Object_GetIndex bug makes EVERY scene's knob
            // door render as the default gameplay_keep wooden door (z_en_door.c: "behavior same as
            // console"), so always the wooden panels — no themed En_Door, no lattice on knob doors.
            if (id == 0x0009)
            {
                // WYSIWYG position: in-game the door leaf is skeleton limb 3 of gDoorSkel, and limb 1 carries a
                // joint offset of (-2700, 0, 0) — so the leaf renders CENTRED on the actor origin. The editor
                // drew gDoorLeftDL/RightDL raw at the origin, i.e. ~+27 world off (the leaf DL's own +X extent),
                // so a door placed to line up in the editor spawned shifted in-game. Apply the limb offset.
                _doorLeafShift = new OpenTK.Mathematics.Vector3(-2700f, 0f, 0f);
                // DOOR_LOCKED (type 1) draws the small-key lock+chains (Actor_DrawDoorLock DOORLOCK_NORMAL,
                // z_en_door.c) — same rig as a KEY_LOCKED shutter. Show it so a locked knob door reads as locked.
                var enDoorAttach = ((var >> 7) & 7) == 1
                    // En_Door renders at its true actor scale (0.01), so bake the lock in actor-local space
                    // (physically exact per Actor_DrawDoorLock). The lock sits at the actor origin (translate X=0),
                    // which is the leaf's centre now that the leaf is limb-offset onto it. DOORLOCK_NORMAL params.
                    ? BuildLock("keep5", 0x11F0, 0x1100, 0.54f, 5000f, 1.0f, 0.0f, actorLocal: true, xShift: 0f, back: _doorLockBack)
                    : noAtt;
                return ("object_gameplay_keep", new[] { _kDoorLeft, _kDoorRight }, none, enDoorAttach);   // gDoorLeftDL/gDoorRightDL
            }

            if (id == 0x002E)   // Door_Shutter — sliding dungeon door (solid panel, grille, or boss door)
            {
                int doorType = (var >> 6) & 0xF;   // z_door_shutter.c: (params>>6)&0xF
                if (doorType == 5)   // SHUTTER_BOSS — boss door + boss-key lock + chains (all object_bdoor)
                {
                    _doorSeg8 = BossTexOffset(_bossTheme);   // bind seg 8 to the temple emblem (else white)
                    return ("object_bdoor", new[] { 0x10C0 }, none,
                            BuildLock("object_bdoor", 0x1530, 0x1400, 0.644f, 8000f, 1.0f, 0.0f, back: _doorLockBack));   // DOORLOCK_BOSS
                }
                // The solid door panel (sShutterInfo `a`) for the current style. Each style's panel DL lives
                // in its dungeon object, except the generic/Forest door which is gDungeonDoorDL in gameplay_keep.
                (string obj, int[] dls, int[] keep) Panel() => _doorStyle switch
                {
                    1 => ("object_ydan_objects",     new[] { 0x67A0 },        none),  // Deku Tree (gDTDungeonDoor1DL)
                    2 => ("object_ddan_objects",     new[] { 0xC0 },          none),  // Dodongo (gDodongoDoorDL)
                    3 => ("object_bdan_objects",     new[] { 0x590 },         none),  // Jabu (gJabuDoorSection1DL)
                    5 => ("object_hidan_objects",    new[] { 0x10CB0 },       none),  // Fire Temple (gFireTempleDoorFrontDL)
                    6 => ("object_mizu_objects",     new[] { 0x5D90 },        none),  // Water Temple
                    7 => ("object_jya_door",         new[] { 0x100 },         none),  // Spirit (gSpiritDoorDL)
                    8 => ("object_haka_door",        new[] { 0x2620 },        none),  // Shadow Temple / Well
                    10 => ("object_menkuri_objects", new[] { 0x10D0 },        none),  // Gerudo Training Ground (gGTGDoorDL)
                    _ => ("object_gameplay_keep",    new[] { _kDungeonDoor }, none),  // generic dungeon door (Forest etc.)
                };

                // SHUTTER_FRONT_CLEAR(1)/FRONT_SWITCH(2)/BACK_LOCKED(3)/FRONT_SWITCH_BACK_CLEAR(7): the doorway
                // is barred. z_door_shutter.c draws the solid panel (sShutterInfo `a`) AND the metal bars (`b`)
                // OVER it — so show BOTH (previously only the bars rendered, leaving the doorway empty behind).
                // Jabu (style 3) is special: its web grille IS the door, with no separate panel.
                if (doorType is 1 or 2 or 3 or 7)
                {
                    if (_doorStyle == 3)
                        return ("object_bdan_objects", new[] { 0x6460 }, none, noAtt);          // gJabuWebDoorDL only
                    var (po, pd, pk) = Panel();
                    // Dodongo/Spirit carry their bars in their OWN object → draw panel + bars from one object.
                    if (_doorStyle == 2)
                        return ("object_ddan_objects", new[] { pd[0], 0x1F0 }, pk, noAtt);      // panel + gDodongoBarsDL
                    if (_doorStyle == 7)
                        return ("object_jya_door",     new[] { pd[0], 0x1F0 }, pk, noAtt);      // panel + gJyaDoorMetalBarsDL
                    // Everyone else uses gDoorMetalBarsDL in gameplay_keep → overlay it via the `keep` channel
                    // (the panel may be in a different dungeon object, e.g. Deku/Fire/Water/Shadow/GTG).
                    return (po, pd, new[] { _kDoorBars }, noAtt);                               // panel + gDoorMetalBarsDL
                }
                // Closed (unbarred) door: just the solid panel, plus the small-key lock+chains for KEY_LOCKED.
                var (obj, dls, keep) = Panel();
                // SHUTTER_KEY_LOCKED (0xB): the small-key lock + chains attach to the standard-size doors.
                // Spirit uses the wider DOORLOCK_NORMAL_SPIRIT rig; Jabu (its own web bars) has no lock variant.
                var attach = doorType == 0xB && _doorStyle != 3
                    ? (_doorStyle == 7
                        ? BuildLock("keep5", 0x11F0, 0x1100, 0.64f,  8000f, 1.75f, 0.1f, back: _doorLockBack)   // DOORLOCK_NORMAL_SPIRIT
                        : BuildLock("keep5", 0x11F0, 0x1100, 0.54f,  5000f, 1.0f,  0.0f, back: _doorLockBack))  // DOORLOCK_NORMAL
                    : noAtt;
                return (obj, dls, keep, attach);
            }
        }
        else   // MM
        {
            if (id == 0x0005)   // MM En_Door — default wooden panels (gDoorLeftDL/Right in gameplay_keep)
                return ("object_gameplay_keep", new[] { 0x20BB8, 0x20D00 }, none, noAtt);
            if (id == 0x001E)   // MM Door_Shutter — boss door else the generic sliding door
                return ((var >> 6) & 0xF) == 5
                    ? ("object_bdoor", new[] { 0xC0 }, none, noAtt)            // MM boss door
                    : ("object_gameplay_keep", new[] { 0x77990 }, none, noAtt); // generic MM sliding door
        }
        return null;
    }

    // Humanoid NPCs whose skeleton has NO animation in its own object — their idle/standing animation
    // lives in a SEPARATE animation object (like the player's link_animetion). Resolve that object,
    // read frame 0, and pose the skeleton with it so they stand instead of collapsing to a tangle.
    private static readonly Dictionary<ushort, (string animObj, int animOff)> ExternalPose = new()
    {
        [0x004D] = ("object_zl2_anime1", 0x3BC),   // Adult Zelda (Cutscene) — gZelda2Anime1Anim_0003BC
        [0x0179] = ("object_zl2_anime1", 0x3BC),   // Adult Zelda
        [0x01CC] = ("object_zl2_anime1", 0x3BC),   // Zelda (Ganon escape)
        [0x013C] = ("object_os_anime", 0x7D0),     // Cucco Lady (En_Niw_Lady) — gObjOsAnim_07D0
        [0x01A4] = ("object_os_anime", 0x92C),     // En_Hy townsfolk (Man in Purple Pants) — gObjOsAnim_092C (ENHY_ANIM_0 standing idle)
        [0x0163] = ("object_os_anime", 0x7FFC),    // En_Ko (Kokiri kids/shopkeeper, object_km1) — gKokiriStandingHandOnChestAnim
    };

    // #4: composite NPC body objects whose skeleton has NO embedded idle anim — posed from a standing-idle
    // frame in a shared animation object, keyed by the RESOLVED body object (the variant resolver below
    // picks the object per spawn value, so a per-actor-id pose doesn't fit). En_Hy's bodies all use
    // object_os_anime (offsets from sAnimationInfo, one standing idle per body skeleton).
    private static readonly Dictionary<string, (string animObj, int animOff)> ExternalPoseByObject =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["object_aob"] = ("object_os_anime", 0x092C),   // Dog Lady
        ["object_cob"] = ("object_os_anime", 0x4F28),   // Bombchu Bowling Lady
        ["object_ahg"] = ("object_os_anime", 0x0228),   // Old man / woman
        ["object_boj"] = ("object_os_anime", 0x4CF4),   // Hylian man / carpenter
        ["object_bba"] = ("object_os_anime", 0x1E7C),   // Old woman
        ["object_cne"] = ("object_os_anime", 0x4E90),   // Young woman
        ["object_bji"] = ("object_os_anime", 0x265C),   // Poe collector
        ["object_bob"] = ("object_os_anime", 0x28DC),   // Carpenter
    };

    // #4: En_Hy (0x016E) townsfolk — type = params & 0x7F selects the body skeleton object (sModelInfo →
    // sSkelInfo). 21 variants; the body object renders posed by ExternalPoseByObject above. Index = type.
    private static readonly string[] EnHyBodies =
    {
        "object_aob", "object_cob", "object_ahg", "object_boj", "object_ahg", "object_boj", "object_bba",
        "object_bji", "object_cne", "object_boj", "object_boj", "object_cne", "object_boj", "object_ahg",
        "object_boj", "object_bji", "object_boj", "object_ahg", "object_bob", "object_bji", "object_ahg",
    };

    // #4: En_Ko (0x0163) Kokiri children — type = params & 0xFF picks a boy (object_km1) or girl (object_kw1)
    // body (per z_en_ko.c sModelInfo). 13 variants; posed by ExternalPose[0x0163] (object_os_anime).
    private static readonly string[] EnKoBodies =
    {
        "object_km1", "object_kw1", "object_km1", "object_km1", "object_km1", "object_kw1", "object_kw1",
        "object_km1", "object_km1", "object_kw1", "object_kw1", "object_km1", "object_kw1",
    };

    // #4: En_Hy head — the per-type head display-list OFFSET in the SAME (body) object, drawn at the head
    // limb (15) so each townsperson gets its own face/hair instead of the skeleton's default head (the game
    // does this swap in EnHy_OverrideLimbDraw). Offsets from z_en_hy.c sHeadInfo / the object asset XMLs.
    private const int EnHyHeadLimb = 15;

    // Actors that draw a HAIR/HEAD display list at a limb via a Post/OverrideLimbDraw callback (the skeleton's
    // own limb DL there is bald) → (reader limb index, default-variant DL offset in the actor's object). Without
    // replaying it the actor renders bald. limb = the SkelAnime limbIndex of the head (last real limb; the
    // Gerudos + En_Hy share HEAD=15). Extend per actor (find its *_LIMB_HEAD + the hairstyle/head DL).
    private (int limb, int dl)? HeadDlFor(ushort id)
    {
        if (!_oot)
            return id switch
            {
                0x009F => (15, 0xBB08),   // MM En_Ge1 (Gerudo White) — gGerudoWhiteHairstyleBobDL @ object_ge1
                _ => null,
            };
        return id switch
        {
            0x0138 => (15, 0xBB08),   // OoT En_Ge1 (Gerudo) — gGerudoWhiteHairstyleBobDL @ object_ge1
            _ => null,
        };
    }
    private static readonly int[] EnHyHeadDls =
    {
        0x3C88, 0x1300, 0x30F0, 0x52E0, 0x5508, 0x5728, 0x2948, 0x2560, 0x1300, 0x26F0, 0x26F0,
        0x2860, 0x5738, 0x5728, 0x59B0, 0x3F68, 0x26F0, 0x5508, 0x3B78, 0x2560, 0x30F0,
    };

    // Frame 0 of Link's idle animation (gPlayerAnim_link_normal_wait, link_animetion @0x1C3030),
    // flattened to [rootPos, limb0rot … limb20rot] (22 Vec3s). The player's animations live in the
    // external link_animetion file, not object_link_boy, so without this the skeleton would render
    // as the meaningless bind-pose tangle (all bones extend along +X). Shared by adult and child
    // Link — link animations are age-independent (only limb lengths differ). Binang units.
    private static readonly short[] PlayerIdlePose =
    {
        -57, 3377, 0,   0, 0, 0,   -16384, 730, -16384,   0, 0, 0,
        2332, 3070, -4374,   0, 0, 8198,   2115, 1887, -20292,   -1632, -2986, -4648,
        0, 0, 8327,   -1967, -1551, -20002,   16384, -2295, 16384,   -24, -431, 13532,
        0, 0, 19902,   0, 0, 0,   2353, -4705, 30867,   0, 0, -6675,
        -36, -503, -16171,   -793, 4973, -30158,   0, 0, -22217,   3940, 2803, -16884,
        -16384, 27240, -730,   0, 0, 0,
    };

    private readonly bool _oot;

    /// <summary>The scene's door theme (a DoorStyle id 0..N), so En_Door panels and Door_Shutter doors
    /// render the right dungeon door (e.g. Fire/Water/Shadow themed) instead of always the default wooden
    /// door. Set by the editor per scene; 0 = default. Cache is invalidated when this changes.</summary>
    public int DoorStyle
    {
        get => _doorStyle;
        set { if (value != _doorStyle) { _doorStyle = value; _cache.Clear(); } }
    }
    private int _doorStyle;

    /// <summary>The boss door's per-temple emblem (z_door_shutter.c D_809982D4 / unk_168): 0 Default,
    /// 1 Fire, 2 Water, 3 Shadow, 4 Ganon's Castle, 5 Forest, 6 Spirit. Binds segment 8 so the boss door
    /// + boss-key lock show that temple's texture instead of rendering untextured white.</summary>
    public int BossDoorTheme
    {
        get => _bossTheme;
        set { if (value != _bossTheme) { _bossTheme = value; _cache.Clear(); } }
    }
    private int _bossTheme;

    // Per-door leaf offset (skeleton limb translation) applied to a door's aliasDls so the leaf sits where
    // it does in-game. Set by DoorModel; zero for doors whose DL is already at the actor origin.
    private OpenTK.Mathematics.Vector3 _doorLeafShift;

    // The current door actor's LockBack flag (set before DoorModel), so its lock previews on the −Z side.
    private bool _doorLockBack;

    // Offset of gBossDoor*Tex in object_bdoor for each theme index (object_bdoor.xml).
    private static int BossTexOffset(int theme) => theme switch
    {
        1 => 0x35C0,  // Fire        2 => 0x55C0,  // Water       3 => 0x45C0,  // Shadow
        4 => 0x0,     // Ganon       5 => 0x25C0,  // Forest      6 => 0x15C0,  // Spirit
        _ => 0x65C0,  // Default
    };
    private int _doorSeg8 = -1;   // seg-8 texture base captured by DoorModel for the current boss door

    public ActorModelResolver(RomImage rom, int sceneKeepObjectId = -1)
    {
        _rom = rom;
        bool oot = rom.Game != RomGame.MM;
        _oot = oot;
        _objects = ObjectTable.Build(rom);
        _actorObjects = ActorObjectTable.Build(mm: !oot);
        _actorScales = ActorScaleTable.Build(mm: !oot);
        _idleAnims = ActorIdleAnimTable.Build(mm: !oot);
        _renderDb = ActorRenderDb.Load(isOoT: oot);

        // gameplay_keep is bound to segment 4 while actors draw, so their display lists can reference
        // its shared textures. It's object id 1 (not resolvable by name). Resolve its file once so
        // ObjectModelReader can decode those.
        int keepId = _objects.IdOf("object_gameplay_keep") ?? 1;
        _keepFileIndex = _objects.ResolveId(keepId) is { } kv ? FileIndexOf(kv.start) : -1;

        // The scene's own keep object (gameplay_field_keep / gameplay_dangeon_keep) is bound to
        // segment 5; field props — grass, rocks, bushes, gossip stones — reference their textures
        // there. Without it those actors render untextured (grey). The scene supplies its keep id.
        _keep5FileIndex = sceneKeepObjectId > 0 && _objects.ResolveId(sceneKeepObjectId) is { } sv
            ? FileIndexOf(sv.start) : -1;

        // The four door display lists in gameplay_keep are at version-SPECIFIC offsets (gameplay_keep is
        // rebuilt per ROM revision, unlike the themed door objects which are byte-identical everywhere).
        // Probe the loaded keep and pick whichever candidate actually begins with the DL's opcode, so the
        // editor renders doors correctly on both the user's retail NTSC-1.0 ROM and the gc debug ROM.
        // (retail NTSC-1.0, gc-debug, expected first opcode) — found via opcode-signature match vs the decomp.
        if (oot)
        {
            var kb = _objects.GetObjectBytes(_rom, keepId);
            int Pick(int retail, int debug, byte op)
            {
                if (kb == null) return retail;
                if (retail >= 0 && retail < kb.Length && kb[retail] == op) return retail;
                if (debug  >= 0 && debug  < kb.Length && kb[debug]  == op) return debug;
                return retail;
            }
            _kDoorLeft    = Pick(0xF158,  0xECB8,  0xD7);   // gDoorLeftDL
            _kDoorRight   = Pick(0xF2A0,  0xEE00,  0xD7);   // gDoorRightDL
            _kDungeonDoor = Pick(0x4F510, 0x49FE0, 0xE7);   // gDungeonDoorDL
            _kDoorBars    = Pick(0x50600, 0x4B0D0, 0xE7);   // gDoorMetalBarsDL (the criss-cross lattice grille)
        }
    }

    // gameplay_keep door DL offsets resolved for the loaded ROM revision (see constructor).
    private readonly int _kDoorLeft, _kDoorRight, _kDungeonDoor, _kDoorBars;

    private readonly int _keepFileIndex;
    private readonly int _keep5FileIndex;

    /// <summary>Per-actor eye/mouth texture offsets for the current game (-1 = none).</summary>
    private (int eye, int mouth) FaceTexFor(ushort id) =>
        (_oot ? OotFaceTex : MmFaceTex).GetValueOrDefault(id, (-1, -1));

    // Env (modulate) colour for an actor, variable-aware. The Gold Skulltula (En_Sw 0x0095) shares
    // object_st with the plain Skullwalltula and is distinguished only by params bits [15:13] (>0 =
    // gold), so a flat id table can't tell them apart — tint the gold variant gold.
    private OpenTK.Mathematics.Vector3? EnvColorFor(ZActor a)
    {
        if (!_oot)
        {
            // MM crystal switch (Obj_Switch type 3/4) — untextured/code-coloured; tint blue like the OoT one.
            if (a.Number == 0x0093 && (a.Variable & 7) is 3 or 4) return new(0.40f, 0.72f, 1.00f);
            return null;
        }
        if (a.Number == 0x0095 && ((a.Variable >> 13) & 7) > 0) return new(1f, 0.82f, 0.15f);  // Gold Skulltula
        // Jabu tentacles (En_Bx 0x00DF blocking, En_Ba 0x00DE inside) both bind their colour as a seg-8
        // texture indexed by params (object_bxa 0x24F0/0x27F0/0x29F0), combined with a grayscale detail — so
        // the single-texture reader shows them grey. Tint per variant so the colour preset reads visually.
        if (a.Number == 0x00DF) return TentacleTint(a.Variable & 0x7F);
        if (a.Number == 0x00DE) return TentacleTint(a.Variable & 0xFF);
        // Crystal switch (Obj_Switch type 3/4) is an untextured, code-coloured crystal (blue) — the DL has
        // no colour, so tint the preview blue instead of stark white so it reads as a crystal switch.
        if (a.Number == 0x012A && (a.Variable & 7) is 3 or 4) return new(0.40f, 0.72f, 1.00f);
        // #9: free-standing rupees share one grayscale model (gRupeeDL) and are coloured per type in-game
        // (sRupeeTex green/blue/red/pink/orange). Tint the editor preview to match the chosen Item00Type so
        // the colour dropdown reads back visually. Values approximate the rupee texture hues.
        if (a.Number == 0x0015)
        {
            switch (a.Variable & 0xFF)
            {
                case 0x00: case 0x08: return new(0.30f, 0.85f, 0.30f);   // green
                case 0x01: case 0x09: return new(0.30f, 0.55f, 1.00f);   // blue
                case 0x02: case 0x0A: return new(1.00f, 0.35f, 0.30f);   // red
                case 0x14: case 0x0B: return new(0.80f, 0.35f, 1.00f);   // purple (50)
                case 0x13:            return new(1.00f, 0.65f, 0.15f);   // orange / huge (200)
            }
        }
        // Carpenters (En_Daiku 0x0133 / En_Daiku_Kakariko 0x01BC) tint their tunic via gDPSetEnvColor by
        // (params & 3) — z_en_daiku.c. En_Ge3 Gerudo (0x01D0) sets its body env red (140,0,0) per-limb like
        // its En_GeldB twin. The reader applies these only to env-combine (cloth) tris, so skin is untouched.
        if (a.Number == 0x0133 || a.Number == 0x01BC) return DaikuTunic(a.Variable & 3);
        if (a.Number == 0x01D0) return new(140 / 255f, 0f, 0f);   // En_Ge3 Gerudo warrior body (red)
        // Kokiri children (En_Ko 0x0163) all wear a green tunic tinted in C code (z_en_ko.c sModelInfo):
        // boys RGB(0,130,70), girls RGB(70,190,60), selected by the Kokiri type (params & 0xFF).
        if (a.Number == 0x0163) return KokiriTunic(a.Variable & 0xFF);
        // En_Ossan shopkeeper: only the Kokiri Shop keeper (OSSAN_TYPE_KOKIRI == 0) tints his tunic green
        // (0,130,70) — the Goron/Zora/market keepers use their own textures (z_en_ossan.c).
        if (a.Number == 0x003D && (a.Variable & 0xFF) == 0) return new(0 / 255f, 130 / 255f, 70 / 255f);
        // En_Hy Hylian townsfolk: tunic env colour (seg8) selected by (params & 0x7F) from z_en_hy.c
        // sModelInfo. White (identity) entries return null so their baked texture shows unchanged.
        if (a.Number == 0x016E) return HyTunic(a.Variable & 0x7F);
        return EnvColorOverride.TryGetValue(a.Number, out var ec) ? ec : (OpenTK.Mathematics.Vector3?)null;
    }

    private static OpenTK.Mathematics.Vector3 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f);

    // En_Hy townsfolk tunic (seg8) env colour by (params & 0x7F), verbatim from z_en_hy.c sModelInfo.
    // Types whose seg8 is white (0,1,2,6,16,18) tint nothing → null (baked texture shows as-is).
    private static OpenTK.Mathematics.Vector3? HyTunic(int type) => type switch
    {
        4 => Rgb(0, 0, 0), 5 => Rgb(50, 80, 0), 7 => Rgb(0, 50, 160), 8 => Rgb(160, 180, 255),
        9 => Rgb(220, 0, 80), 10 => Rgb(0, 130, 220), 11 => Rgb(70, 160, 230), 12 => Rgb(150, 60, 90),
        13 => Rgb(200, 180, 255), 14 => Rgb(140, 255, 110), 15 => Rgb(130, 70, 20), 17 => Rgb(90, 100, 20),
        19 => Rgb(160, 0, 100), 20 => Rgb(160, 230, 0),
        _ => null,   // 0,1,2,3,6,16,18 — white/identity seg8: no tint
    };

    // En_Daiku / En_Daiku_Kakariko tunic env colour by (params & 3), verbatim from z_en_daiku.c.
    private static OpenTK.Mathematics.Vector3 DaikuTunic(int type) => type switch
    {
        0 => new(170 / 255f, 10 / 255f, 70 / 255f),
        1 => new(170 / 255f, 200 / 255f, 255 / 255f),
        2 => new(0 / 255f, 230 / 255f, 70 / 255f),
        _ => new(200 / 255f, 0 / 255f, 150 / 255f),
    };

    // En_Ko Kokiri children: girls (types 1, 5, 6, 9, 10) wear a brighter green tunic than the boys.
    // Colours from z_en_ko.c sModelInfo tunic entries. Boots differ too but the tunic dominates the read.
    private static OpenTK.Mathematics.Vector3 KokiriTunic(int type) => type switch
    {
        1 or 5 or 6 or 9 or 10 => new(70 / 255f, 190 / 255f, 60 / 255f),   // girls
        _                      => new(0 / 255f, 130 / 255f, 70 / 255f),    // boys
    };

    // Jabu-tentacle colour by variant index (object_bxa seg-8 texture index / En_Bx & En_Ba params). Presets:
    // 0 reddish brown, 1 green, 2 grayish blue w/ red, 3 corrupt/dead, 4 dark brownish, 5 blackish gray.
    private static OpenTK.Mathematics.Vector3 TentacleTint(int idx) => idx switch
    {
        0 => new(0.62f, 0.40f, 0.30f),
        1 => new(0.38f, 0.62f, 0.38f),
        2 => new(0.50f, 0.54f, 0.64f),
        4 => new(0.40f, 0.30f, 0.22f),
        5 => new(0.30f, 0.30f, 0.32f),
        _ => new(0.45f, 0.32f, 0.32f),   // 3 (En_Bx corrupt / En_Ba dead) and any out-of-range
    };

    // Some actors place NO model of their own but dynamically spawn another actor at their exact
    // position when the room loads — so the static scene shows an empty spot where the game shows an
    // enemy/boss. Represent the spawner by the actor it spawns, so the editor matches what's played.
    private static readonly Dictionary<ushort, ushort> OotSpawnProxy = new()
    {
        // (En_Blkobj 0x0136 now renders the illusion room itself via OotActorDlOverride, not Dark Link.)
    };
    private static readonly Dictionary<ushort, ushort> MmSpawnProxy = new();

    /// <summary>If <paramref name="actor"/> is a spawner that places another actor at its own
    /// position, returns that spawned actor's id; else the actor's own id.</summary>
    private ushort EffectiveId(ushort id) =>
        (_oot ? OotSpawnProxy : MmSpawnProxy).GetValueOrDefault(id, id);

    /// <summary>Model for an actor, or null if no model could be resolved (caller draws a marker
    /// or, for obsolete actors, the Eyeball Frog billboard).</summary>
    public Model? Resolve(ZActor actor, bool adult)
    {
        // Obsolete/unknown actors are drawn as the Eyeball Frog billboard, not a model.
        if (actor.IsObsolete) return null;

        // Resolve a spawner (e.g. the Dark Link arena) as the actor it spawns, keeping the position.
        if (EffectiveId(actor.Number) is var eff && eff != actor.Number)
            actor = new ZActor { Number = eff, Variable = actor.Variable,
                                 XPos = actor.XPos, YPos = actor.YPos, ZPos = actor.ZPos,
                                 XRot = actor.XRot, YRot = actor.YRot, ZRot = actor.ZRot };

        var hint = _renderDb.Resolve(actor.Number, actor.Variable);

        // Player / spawn point → Link's object. The spawn marker always uses the tallest standing
        // form (Adult Link, object_link_boy) as a size reference, per the editor's convention.
        // Otherwise the actor→object table, falling back to the render DB's object — which covers
        // actors drawn from a keep object (e.g. grottos use gameplay_field_keep) that the
        // actor→object table deliberately skips.
        // Object name: the decomp actor→object table (always a real ROM object) wins; otherwise the
        // render-DB hint's object — which covers keep-drawn actors the table skips. The hint's object
        // is preferred over the table only when it's a real object the ROM actually has (SharpOcarina's
        // XML often points at its own custom_* objects that don't exist in the retail ROM).
        string? tableObj = _actorObjects.ObjectFor(actor.Number);
        string? hintObj  = hint?.ObjectName;
        bool hintObjReal = hintObj != null && _objects.Resolve(hintObj) != null;

        // Dark Link (OoT En_Torch2, 0x0033) reuses the Player struct + Link's skeleton, and its
        // animations — like Link's — live in the external link_animetion file, so the editor found no
        // posable idle and drew its OWN object_torch2 skeleton as a bind-pose tangle ("folded in on
        // itself"). Keep its correct object_torch2 model, but pose it with Link's idle frame so it
        // stands up in the same rig as Adult Link.
        bool isPlayer = actor.Number == ActorPlayer;
        bool darkLink = _oot && actor.Number == 0x0033;
        bool kafei = !_oot && actor.Number == 0x0159;   // En_Test3 — reuses child-Link player anims on a player-layout skeleton
        bool poseAsLink = isPlayer || darkLink || kafei;   // pose with Link's idle joint table

        // En_Item00 freestanding item: render its actual gameplay_keep model (rupee / heart piece /
        // heart container) chosen by Item00Type, so the editor matches the spinning in-game item.
        var fsItem = (_oot && actor.Number == 0x0015) ? FreestandingItemModel(actor.Variable) : null;

        string? objName;
        bool useHintDls;
        int[]? aliasDls = null;   // explicit DLs when a custom_* alias maps into a real object
        bool sharedAliasDls = false;   // run aliasDls on ONE reader (material DL's texture carries to geometry)
        int[]? keepOverlayDls = null;   // extra DLs read from gameplay_keep + overlaid (e.g. a shutter's bar lattice)
        List<DoorAttach>? doorAttach = null;   // transformed lock/chain pieces attached to a locked door
        int doorSeg8 = -1;   // boss door: seg-8 temple emblem texture base (in the door object)
        IReadOnlyList<OpenTK.Mathematics.Vector3>? seg0CStack = null;   // synthetic body-segment matrix stack (Like-Like)
        _doorLockBack = actor.LockBack;   // preview the lock on the door's back face when set (see DoorModel)
        if (isPlayer) { objName = "object_link_boy"; useHintDls = false; }
        else if (fsItem is { } fs) { objName = fs.obj; useHintDls = false; aliasDls = fs.dls; }
        // #4: En_Wood02 (OoT 0x0077) — tree/bush/leaf, one object but the display list is chosen by type
        // (params & 0xFF). Render the matching DL from object_wood02 so each tree/bush variant shows.
        else if (_oot && actor.Number == 0x0077)
        {
            int t = actor.Variable & 0xFF;
            int dl = t <= 0x04 ? 0x78D0 : t <= 0x09 ? 0x7CA0 : t == 0x0A ? 0x80D0 : t <= 0x10 ? 0x0090 : t <= 0x17 ? 0x0340 : 0x0700;
            objName = "object_wood02"; useHintDls = false; aliasDls = new[] { dl };
        }
        // En_Fall (MM 0x017C) is the Moon. It statically declares gameplay_keep but dynamically loads its
        // model object by EN_FALL_TYPE = (params & 0xF80) >> 7 (z_en_fall.c EnFall_Init): LODMOON for the
        // sky moon, object_fall for the Termina-field / clock-tower moon, object_moonston for the Moon's
        // Tear, object_fall2 for the open-mouth moon. Map the type to the real object so the editor draws
        // the moon model (auto-detected by ReadBestModel) instead of the Moon's-Tear billboard icon.
        else if (!_oot && actor.Number == 0x017C && EnFallObject(actor.Variable) is { } moonObj && _objects.Resolve(moonObj) != null)
        { objName = moonObj; useHintDls = false; }
        // Bg_Ladder (MM 0x0163): the general placeable wooden ladder. size = params & 0xFF picks the rung
        // count → DL from object_ladder (offsets from object_ladder.xml: 12-rung 0xA0, 16 0x2D0, 20 0x500,
        // 24 0x730). It installs its own climbable dynapoly collision in-game, so the model IS the ladder.
        else if (!_oot && actor.Number == 0x0163)
        {
            int size = actor.Variable & 0xFF;
            int off = size switch { 1 => 0x2D0, 2 => 0x500, 3 => 0x730, _ => 0xA0 };
            objName = "object_ladder"; useHintDls = false; aliasDls = new[] { off };
        }
        // Doors (En_Door / Door_Shutter) — the closed, themed door model so the doorway isn't a black frame.
        else if (DoorModel(actor.Number, actor.Variable) is { } door) { objName = door.obj; useHintDls = false; aliasDls = door.dls; keepOverlayDls = door.keep.Length > 0 ? door.keep : null; doorAttach = door.attach.Count > 0 ? door.attach : null; doorSeg8 = _doorSeg8; }
        // Obj_Switch (0x012A): the model is chosen by type=(params&7) + subtype=(params>>4&7) — floor
        // (plain/rusty), eye, or crystal switch. All live in gameplay_dangeon_keep (offsets verified vs the
        // object XML). Previously only the EYE type was handled; the floor/crystal switches (all over Jabu
        // and every dungeon) fell through to the generic hint and rendered wrong/invisible.
        else if (_oot && actor.Number == 0x012A)
        {
            var sw = ObjSwitchModel(actor.Variable);
            objName = "gameplay_dangeon_keep"; useHintDls = false;
            aliasDls = sw.dls;
            if (sw.seg8 >= 0) doorSeg8 = sw.seg8;
        }
        // En_Ishi (0x014E): a small throwable rock (gFieldKakeraDL) or a large silver rock (gSilverRockDL),
        // chosen by params&1 (z_en_ishi.c sDrawFuncs), both in gameplay_field_keep. Scale 0.1 / 0.4.
        else if (_oot && actor.Number == 0x014E)
        {
            objName = "gameplay_field_keep"; useHintDls = false;
            aliasDls = new[] { (actor.Variable & 1) == 1 ? 0xA3B8 : 0xA880 };
        }
        // Bg_Hidan_Kowarerukabe (0x00CF): the Fire Temple bomb-breakable — cracked stone floor / bombable
        // wall / large bombable wall by params&0xFF (z_bg_hidan_kowarerukabe.c sBreakableWallDLists), in
        // object_hidan_objects. Scale 0.1.
        else if (_oot && actor.Number == 0x00CF)
        {
            objName = "object_hidan_objects"; useHintDls = false;
            aliasDls = new[] { (actor.Variable & 0xFF) switch { 1 => 0xC038, 2 => 0xB900, _ => 0xB9C0 } };
        }
        // Bg_Bdan_Objects (0x00C8): Jabu-Jabu mechanisms — params&0xFF picks the rotating spike platform (0),
        // the elevator platform (1), or the water surface (2), in object_bdan_objects (z_bg_bdan_objects.c
        // sDLists / BgBdanObjects_Draw). Scale 0.1.
        else if (_oot && actor.Number == 0x00C8)
        {
            objName = "object_bdan_objects"; useHintDls = false;
            aliasDls = new[] { (actor.Variable & 0xFF) switch { 1 => 0x4BE8, 2 => 0x38E8, _ => 0x8618 } };
        }
        // Bg_Spot09_Obj (0x00B8): Gerudo Valley — the bridge (intact/broken/child/repaired) or the carpenters'
        // tent, by params (z_bg_spot09_obj.c sDLists), in object_spot09_obj. The bridges are world-scale (1.0);
        // the tent is 0.1 (func_808B1BA0).
        else if (_oot && actor.Number == 0x00B8)
        {
            objName = "object_spot09_obj"; useHintDls = false;
            aliasDls = new[] { new[] { 0x100, 0x3970, 0x1120, 0x7D40, 0x6210 }[Math.Clamp(actor.Variable & 0xFF, 0, 4)] };
        }
        // Bg_Mizu_Bwall (0x01BA): a Water Temple breakable wall — one of 5 wall pieces by params&0xF
        // (z_bg_mizu_bwall.c sDLists), in object_mizu_objects. Scale 0.1.
        else if (_oot && actor.Number == 0x01BA)
        {
            objName = "object_mizu_objects"; useHintDls = false;
            aliasDls = new[] { new[] { 0x1A30, 0x2390, 0x1CD0, 0x2090, 0x1770 }[Math.Clamp(actor.Variable & 0xF, 0, 4)] };
        }
        // En_Gs (0x01B9): the Gossip Stone draws gGossipStoneMaterialDL (sets the texture) then gGossipStoneDL
        // (geometry) as SEPARATE display lists — auto-detect ran only the geometry → fully untextured (0/30).
        // Render both on one reader so the texture binds. object_gs, world scale (identity draw).
        else if (_oot && actor.Number == 0x01B9)
        {
            objName = "object_gs"; useHintDls = false;
            aliasDls = new[] { 0x950, 0x9D0 };   // gGossipStoneMaterialDL, gGossipStoneDL
            sharedAliasDls = true;
        }
        // MM En_Gs (0x00EF): same gossip stone, same object_gs DL layout (material 0x950 + geometry 0x9D0).
        else if (!_oot && actor.Number == 0x00EF)
        {
            objName = "object_gs"; useHintDls = false;
            aliasDls = new[] { 0x950, 0x9D0 };
            sharedAliasDls = true;
        }
        // Bg_Po_Event (0x0093): the Forest Temple pushable painting-BLOCKS (Amy/AmyBeth) and wall PAINTINGS
        // (Joelle/Beth/Amy), by type=(params>>8)&0xF (z_bg_po_event.c displayLists[]), in object_po_sisters.
        // Auto-detect otherwise grabbed the Poe Sisters' BODY (the object's largest DL) → the "giant Poe".
        // Drawn at an identity matrix = world scale (ICHAIN scale 1000/1000 = 1.0).
        else if (_oot && actor.Number == 0x0093)
        {
            objName = "object_po_sisters"; useHintDls = false;
            aliasDls = new[] { new[] { 0x75A0, 0x79E0, 0x6830, 0x6D60, 0x7230 }[Math.Clamp((actor.Variable >> 8) & 0xF, 0, 4)] };
        }
        // Bg_Haka_Megane (0x00AE): the Lens-of-Truth fake walls/floors/holes (normally invisible). params
        // picks the piece; params<3 come from object_hakach_objects (Bottom of the Well), the rest from
        // object_haka_objects (Shadow Temple) — the actor bank-loads whichever by params (z_bg_haka_megane.c
        // Init). Drawn here so the hidden geometry is visible in the editor. Scale 0.1.
        else if (_oot && actor.Number == 0x00AE)
        {
            int p = actor.Variable & 0xFF;
            useHintDls = false;
            if (p < 3)
            {
                objName = "object_hakach_objects";
                aliasDls = new[] { new[] { 0x1060, 0x1920, 0x03F0 }[p] };   // fake walls/floors / three floors / hole trap
            }
            else
            {
                objName = "object_haka_objects";
                int[] haka = { 0x40F0, 0x43B0, 0x1120, 0x45A0, 0x47F0, 0x18F0, 0x49B0, 0x3CF0, 0x4B70, 0x2ED0 };
                aliasDls = new[] { haka[Math.Clamp(p - 3, 0, haka.Length - 1)] };
            }
        }
        // Obj_Hsblock (0x012D): a hookshot POST (a climbable stone post) or a hookshot TARGET, by params&3
        // (z_obj_hsblock.c sDLists = {post,post,target}), in object_d_hsblock. Scale 0.1.
        else if (_oot && actor.Number == 0x012D)
        {
            objName = "object_d_hsblock"; useHintDls = false;
            aliasDls = new[] { (actor.Variable & 3) == 2 ? 0x470 : 0x210 };
        }
        // Bg_Bdan_Switch (0x00E6): Jabu blue/yellow buttons by type=params&0xFF. z_bg_bdan_switch.c: BLUE(0)
        // draws gJabuBlueFloorSwitchDL; every yellow type shares gJabuYellowFloorSwitchDL but the TALL types
        // (YELLOW_TALL_1=3 / YELLOW_TALL_2=4) are drawn NARROWER and ~4× TALLER (unk_1D4≈0.0538 X/Z,
        // unk_1D0≈0.205 Y, from unk_1C8=2.0), stretching the flat button DL into a tall column. The flat
        // types are ~uniform 0.106. Y-stretch for the tall type is baked below.
        else if (_oot && actor.Number == 0x00E6)
        {
            objName = "object_bdan_objects"; useHintDls = false;
            aliasDls = new[] { (actor.Variable & 0xFF) == 0 ? 0x5A20 : 0x61A0 };   // BLUE : YELLOW (shared DL)
        }
        // MM Obj_Switch (0x0093): the MM floor/rusty/eye/crystal switch, chosen by type/subtype, all in the
        // MM gameplay_dangeon_keep (parallel to the OoT switch above, different offsets/object).
        else if (!_oot && actor.Number == 0x0093)
        {
            var sw = ObjSwitchModelMm(actor.Variable);
            objName = "gameplay_dangeon_keep"; useHintDls = false;
            aliasDls = sw.dls;
            if (sw.seg8 >= 0) doorSeg8 = sw.seg8;
        }
        // MM Bg_Ikana_Bombwall (0x255): Stone Tower bombable tan floor — object_ikana_obj DL by (params>>8)&1
        // (D_80BD52E0), scale 1.0. Borrows the shared OBJECT_IKANA_OBJ, so auto-detect grabbed a wrong big DL.
        else if (!_oot && actor.Number == 0x0255)
        {
            objName = "object_ikana_obj"; useHintDls = false;
            aliasDls = new[] { ((actor.Variable >> 8) & 1) == 1 ? 0x0048 : 0x0378 };
        }
        // MM Bg_Ikana_Shutter (0x257): Stone Tower metal shutter — object_ikana_obj DL 0xCE8, scale 0.1.
        else if (!_oot && actor.Number == 0x0257)
        {
            objName = "object_ikana_obj"; useHintDls = false; aliasDls = new[] { 0x0CE8 };
        }
        // (Bg_Inibs_Movebg 0x227 Twinmold arena sand: sOpaDLists 0x62D8/0x1DC0 are a ~28000u arena-wide plane
        //  with animated segment-bound sand textures the static reader can't decode — it renders as one huge
        //  untextured triangle, worse than the cap-box. Left to auto-detect+cap until animated-tex support.)
        // Like-Like (En_Rr 0x00DD): one DL (gLikeLikeDL @ 0x470 in object_rr) drawn through 4 body-segment
        // matrices bound to segment 0x0C. z_en_rr.c EnRr_Draw stacks them cumulatively at Y += height+1000
        // (height 0 at rest), so at rest the segments sit at Y = 1000/2000/3000/4000 above the base ring;
        // without them the tube collapses flat. Supply that rest stack so it stands upright.
        else if (_oot && actor.Number == 0x00DD)
        {
            objName = "object_rr"; useHintDls = false; aliasDls = new[] { 0x470 };
            seg0CStack = new[] { new OpenTK.Mathematics.Vector3(0, 1000, 0), new OpenTK.Mathematics.Vector3(0, 2000, 0),
                                 new OpenTK.Mathematics.Vector3(0, 3000, 0), new OpenTK.Mathematics.Vector3(0, 4000, 0) };
        }
        // Jabu blocking tentacle (En_Bx 0x00DF): 4 body-segment balls drawn via object_bxa_DL_0022F0 through
        // segment-0x0C matrices (z_en_bx.c: segment i at world Y +140*(i+1), per-segment scale ~0.015). The
        // model scale (0.015) is carried on the Model, so pre-divide the translations by it. Colour is a seg-8
        // texture chosen by params&0x7F (D_809D2560 → object_bxa 0x24F0/0x27F0/0x29F0; idx>2 = vanilla OOB).
        else if (_oot && actor.Number == 0x00DF)
        {
            objName = "object_bxa"; useHintDls = false; aliasDls = new[] { 0x22F0 };
            int ci = Math.Clamp(actor.Variable & 0x7F, 0, 2);
            doorSeg8 = ci == 0 ? 0x24F0 : ci == 1 ? 0x27F0 : 0x29F0;
            const float s = 0.015f;
            seg0CStack = new[] { new OpenTK.Mathematics.Vector3(0, 140f / s, 0), new OpenTK.Mathematics.Vector3(0, 280f / s, 0),
                                 new OpenTK.Mathematics.Vector3(0, 420f / s, 0), new OpenTK.Mathematics.Vector3(0, 560f / s, 0) };
        }
        // Deku Tree cobweb (Bg_Ydan_Sp 0x000F): the floor web (horizontal drop-through) or wall web (vertical,
        // burnable) from object_ydan_objects, chosen by (params>>12)&0xF — 0=WEB_FLOOR (gDTWebFloorDL @ 0x61B0),
        // 1=WEB_WALL (gDTWebWallDL @ 0x5F40). z_bg_ydan_sp.c: sInitChain scales 100/1000 = 0.1 (below).
        else if (_oot && actor.Number == 0x000F)
        {
            int webType = (actor.Variable >> 12) & 0xF;
            objName = "object_ydan_objects"; useHintDls = false;
            aliasDls = new[] { webType == 1 ? 0x5F40 : 0x61B0 };
        }
        else if (_oot && OotActorDlOverride.TryGetValue(actor.Number, out var ado)) { objName = ado.obj; useHintDls = false; aliasDls = ado.dls; }
        else if (CompositeObject(actor.Number, actor.Variable) is { } compObj) { objName = compObj; useHintDls = false; }
        else if (hintObj != null && CustomObjectAlias.TryGetValue(hintObj, out var alias))
        {
            // A SharpOcarina custom_* model that's really DLs inside a real object (e.g. the door's
            // panels in gameplay_keep) — render those display lists directly.
            objName = alias.obj; useHintDls = false; aliasDls = alias.dls;
        }
        else if (tableObj != null)
        {
            objName = tableObj;
            // The hint's display-list offsets only apply if it targets the SAME object we resolve to;
            // otherwise they're offsets into a custom_* model and would decode garbage (e.g. chests:
            // table says object_box, the XML hint says custom_chest). Then we auto-detect the object's
            // own model instead.
            useHintDls = hintObj != null && hintObj.Equals(tableObj, StringComparison.OrdinalIgnoreCase);
        }
        else if (hintObjReal) { objName = hintObj; useHintDls = true; }   // keep-actor with a real hint object
        else { objName = hintObj; useHintDls = hint != null; }            // last resort (may be custom/unresolvable)

        if (objName == null) return null;
        // Scale and ignore-yaw are actor behaviour (roughly object-independent), so honour the hint
        // even when its DL offsets don't apply; the offsets themselves are gated by useHintDls.
        var dlOffsets = useHintDls ? hint!.DlOffsets : [];
        int dlCount = useHintDls ? hint!.DlCount : 1;
        // The render-DB scale is calibrated for SharpOcarina's hint object. When we override the object
        // (Player → object_link_boy instead of custom_adultlink), that scale is wrong — Link's hint
        // scale 0.0048 is for custom_adultlink and renders object_link_boy tiny. Use the scale that
        // sizes object_link_boy to a real adult Link (matches Dark Link's object_torch2 at 0.015).
        // Dark Link (object_torch2) is adult-Link-sized; force the full Link scale so the Water Temple
        // arena (En_Blkobj proxied to Dark Link) shows him at proper size, not the generic 0.01.
        // #7: scale priority — special-cased actors first, then SharpOcarina-custom DL coords keep the
        // hint scale, but a REAL decomp object uses the actor's own Actor_SetScale value (ground truth).
        // That value sizes world-scaled Bg/scenery actors correctly (e.g. Bg_Gjyo_Bridge), which the old
        // flat 0.01 default shrank to a dot, while leaving the ~0.01 humanoids unchanged.
        float scale;
        if (isPlayer || darkLink) scale = 0.015f;
        // #6: En_Box chest size follows its type — small chest types (SMALL/6/ROOM_CLEAR_SMALL/
        // SWITCH_FLAG_FALL_SMALL = 5/6/7/8) are Actor_SetScale(0.005), big types 0.01, so render them at
        // the matching half size instead of all big.
        else if (_oot && actor.Number == 0x000A)
        {
            int boxType = (actor.Variable >> 12) & 0xF;
            scale = (boxType is 5 or 6 or 7 or 8) ? 0.005f : 0.01f;
        }
        else if (fsItem is { } fsc) scale = fsc.scale;
        else if (_oot && actor.Number == 0x012A) scale = 0.1f;   // Obj_Switch: ICHAIN scale 100/1000 (eye/crystal)
        else if (_oot && actor.Number == 0x00DF) scale = 0.015f;   // En_Bx tentacle (segment matrices pre-divided by this)
        else if (_oot && actor.Number == 0x000F) scale = 0.1f;     // Bg_Ydan_Sp cobweb (sInitChain scale 100/1000)
        else if (_oot && actor.Number == 0x014E) scale = (actor.Variable & 1) == 1 ? 0.4f : 0.1f;   // En_Ishi rock small/large
        else if (_oot && actor.Number == 0x00CF) scale = 0.1f;   // Bg_Hidan_Kowarerukabe (Actor_SetScale 0.1)
        else if (_oot && actor.Number == 0x00C8) scale = 0.1f;   // Bg_Bdan_Objects Jabu (sInitChain scale 100)
        else if (_oot && actor.Number == 0x00E6) scale = (actor.Variable & 0xFF) is 3 or 4 ? 0.05375f : 0.106f;   // Bg_Bdan_Switch: tall vs flat X/Z scale
        else if (_oot && actor.Number == 0x012D) scale = 0.1f;   // Obj_Hsblock hookshot post/target (sInitChain 100)
        else if (_oot && actor.Number == 0x01BA) scale = 0.1f;   // Bg_Mizu_Bwall Water Temple wall (sInitChain 100)
        else if (_oot && actor.Number == 0x00B8) scale = (actor.Variable & 0xFF) == 3 ? 0.1f : 1.0f;   // Bg_Spot09_Obj: tent 0.1, bridges world-scale
        else if (_oot && actor.Number == 0x00AE) scale = 0.1f;   // Bg_Haka_Megane Lens fake walls (sInitChain 100)
        else if (_oot && actor.Number == 0x0093) scale = 1.0f;   // Bg_Po_Event painting-blocks/paintings (ICHAIN scale 1000/1000, identity-matrix draw)
        else if (_oot && actor.Number == 0x01B9) scale = 0.1f;   // En_Gs Gossip Stone (ICHAIN scale 100/1000)
        else if (!_oot && actor.Number == 0x00EF) scale = 0.1f;  // MM En_Gs Gossip Stone (ICHAIN scale 100/1000)
        else if (!_oot && actor.Number == 0x0093) scale = ObjSwitchScaleMm(actor.Variable);   // MM Obj_Switch per-type scale
        else if (!_oot && actor.Number == 0x0163) scale = 0.1f;   // MM Bg_Ladder (ICHAIN scale 100/1000)
        else if (!_oot && actor.Number == 0x0255) scale = 1.0f;    // Bg_Ikana_Bombwall floor (ICHAIN 1000/1000)
        else if (!_oot && actor.Number == 0x0257) scale = 0.1f;    // Bg_Ikana_Shutter (ICHAIN 100/1000)
        else if (!_oot && actor.Number == 0x019C) scale = TokeidaiScale(actor.Variable);   // clock tower / wall clocks: per-type size
        else if (!_oot && actor.Number == 0x017C) scale = 0.16f;   // En_Fall moon: this->scale default (EN_FALL_SCALE 0 -> 0.16f)
        else if (_oot && OotActorScale.TryGetValue(actor.Number, out var so)) scale = so;
        else if (!_oot && MmActorScale.TryGetValue(actor.Number, out var ms)) scale = ms;
        else if (aliasDls != null || useHintDls) scale = hint?.Scale ?? 0.01f;   // custom/SharpOcarina coords
        else scale = _actorScales.ScaleFor(actor.Number) ?? hint?.Scale ?? 0.01f; // real object → decomp scale
        bool ignoreYaw = hint?.IgnoreYaw ?? false;

        // Include the spawn variable: composite/variant actors (En_Hy head, rupee colour, …) pick their
        // model/head/tint from it, so two actors of the same id but different params must cache separately.
        string key = $"{objName}|{string.Join(',', dlOffsets)}|{dlCount}|{ignoreYaw}|{actor.Number:X4}|s{scale}|v{actor.Variable:X4}"
                   + (_doorLockBack ? "|lb" : "")
                   + (fsItem is { } fk ? $"|fs{string.Join(',', fk.dls)}@{fk.scale}" : "");
        if (_cache.TryGetValue(key, out var cached)) return cached;

        // Actors whose in-game model is invisible/effect-only (a light ray that's scale-0 until it reflects
        // light, a wave effect) auto-detect their object as a room-filling giant beam. Force them to NO model
        // so they fall to the billboard-sprite path (ActorSpriteMap) with a fitting icon, per "no real model
        // -> billboard". Mir_Ray/Ray2/Ray3 (ICHAIN scale 0, beam len = reflectIntensity), Bg_Ikana_Ray,
        // Eff_Kamejima_Wave. Bg_Ikana_Bombwall/Shutter are real scenery (floor/door) — NOT forced.
        if (!_oot && MmForceSprite.Contains(actor.Number)) { _cache[key] = null; return null; }
        if (_oot && OotForceSprite.Contains(actor.Number)) { _cache[key] = null; return null; }

        // Actors whose real geometry is embedded in their OVERLAY file (not a shared object): render that
        // mesh directly so they're faithful, instead of auto-detecting the borrowed object's body (the
        // giant-Ganondorf-for-the-floor bug). See TryOverlayModel / ActorOverlayTable.
        if (TryOverlayModel(actor) is { } ovlModel) { _cache[key] = ovlModel; return ovlModel; }

        Model? model = null;
        try
        {
            // gameplay_keep (the door alias target) isn't in the by-name object list — resolve it by
            // its object id (segment-4 keep) instead.
            (uint start, uint end)? v;
            byte[]? bytes;
            if (objName.Equals("object_gameplay_keep", StringComparison.OrdinalIgnoreCase))
            {
                int kid = _objects.IdOf("object_gameplay_keep") ?? 1;
                v = _objects.ResolveId(kid);
                bytes = _objects.GetObjectBytes(_rom, kid);
            }
            else if (objName.Equals("gameplay_dangeon_keep", StringComparison.OrdinalIgnoreCase))
            {
                int did = _objects.IdOf("gameplay_dangeon_keep") ?? 3;   // dungeon keep (seg 5); holds the eye switch
                v = _objects.ResolveId(did);
                bytes = _objects.GetObjectBytes(_rom, did);
            }
            else if (objName.Equals("gameplay_field_keep", StringComparison.OrdinalIgnoreCase))
            {
                int fid = _objects.IdOf("gameplay_field_keep") ?? 2;   // field keep (seg 5); holds the En_Ishi rocks
                v = _objects.ResolveId(fid);
                bytes = _objects.GetObjectBytes(_rom, fid);
            }
            else
            {
                v = _objects.Resolve(objName);
                bytes = _objects.GetObjectBytes(_rom, objName);
            }
            if (bytes != null && v != null)
            {
                int fileIdx = FileIndexOf(v.Value.start);
                // The eye switch / rocks (and the dungeon locks) live in the segment-5 keep and self-reference
                // segment 5 for their vertices. When THIS object IS that keep, seg-5 is itself, so bind it to
                // fileIdx — otherwise they'd only resolve in a scene whose keep5 is set.
                int keep5Eff = objName != null && (objName.Equals("gameplay_dangeon_keep", StringComparison.OrdinalIgnoreCase)
                                                   || objName.Equals("gameplay_field_keep", StringComparison.OrdinalIgnoreCase))
                    ? fileIdx : _keep5FileIndex;

                // External idle pose: this NPC's animation lives in a separate object. Resolve it, read
                // frame 0 against this object's skeleton (limb count), and use it as the pose override.
                short[]? extPose = null;
                // Per-actor external pose, else (#4 composite NPCs) a pose keyed by the resolved body object.
                // ExternalPoseByObject is OoT-only (it references object_os_anime).
                if (!ExternalPose.TryGetValue(actor.Number, out var ep) && objName != null && _oot)
                    ExternalPoseByObject.TryGetValue(objName, out ep);
                if (ep.animObj != null)
                {
                    var animBytes = _objects.GetObjectBytes(_rom, ep.animObj);
                    int skel = ObjectModelReader.FindSkeleton(bytes);
                    int limbCount = skel >= 0 && skel + 5 <= bytes.Length ? bytes[skel + 4] : 0;
                    if (animBytes != null && limbCount > 0)
                        extPose = ObjectModelReader.ReadAnimFrame0(animBytes, ep.animOff, limbCount);
                }

                // Read one display list, tolerating a malformed DL (a bad overlay must not nuke the whole
                // model — e.g. the door frame must still render even if its bar-lattice DL trips the reader).
                List<MeshTri> SafeDl(byte[] b, int fi, int off)
                {
                    try
                    {
                        var dlt = ObjectModelReader.ReadDList(b, fi, off, _keepFileIndex, keep5Eff, doorSeg8, seg0CStack).ToList();
                        // Per-DL transform (e.g. Dodongo/Spirit in-object metal bars → push to the front face).
                        if (objName != null && DlTransform.TryGetValue((objName, off), out var fn))
                            for (int k = 0; k < dlt.Count; k++) { var t = dlt[k]; t.P0 = fn(t.P0); t.P1 = fn(t.P1); t.P2 = fn(t.P2); dlt[k] = t; }
                        return dlt;
                    }
                    catch { return new List<MeshTri>(); }
                }

                List<MeshTri> tris;
                bool autoDetectPath = false;   // true only when the model came from ReadBestModel's guess
                if (aliasDls != null)
                {
                    // A custom_* alias backed by real display lists (in a real object). sharedAliasDls runs
                    // them on ONE reader so a leading material DL's texture state carries into the geometry.
                    tris = sharedAliasDls
                        ? ObjectModelReader.ReadDLs(bytes, fileIdx, aliasDls, _keepFileIndex, _keep5FileIndex, SegTexFor(_oot, actor.Number, actor.Variable))
                        : aliasDls.SelectMany(off => SafeDl(bytes, fileIdx, off)).ToList();
                    // Door leaf offset: place the leaf where its skeleton limb does (En_Door's leaf is limb 3,
                    // carried -2700 in X off the actor origin), so the editor door matches the in-game position.
                    if (_doorLeafShift != OpenTK.Mathematics.Vector3.Zero)
                        for (int k = 0; k < tris.Count; k++)
                        { var t = tris[k]; t.P0 += _doorLeafShift; t.P1 += _doorLeafShift; t.P2 += _doorLeafShift; tris[k] = t; }
                    // #4: the freestanding rupee is ONE grayscale model (gRupeeDL) that the game colours per
                    // type via a texture swap (sRupeeTex green/blue/red/pink/orange) — there's no colour in
                    // the DL, so the static reader produced a grey rupee. Force EnvColorFor's per-type hue
                    // onto the vertex shade (TEXEL×SHADE) so the rupee shows its real colour in the editor.
                    if (_oot && actor.Number == 0x0015 && EnvColorFor(actor) is { } rupeeTint)
                        tris = tris.Select(t => { t.C0 *= rupeeTint; t.C1 *= rupeeTint; t.C2 *= rupeeTint; return t; }).ToList();
                    // Crystal switch (OoT Obj_Switch 0x012A / MM 0x0093, type 3/4): untextured code-coloured
                    // crystal — tint blue so it reads.
                    if (((_oot && actor.Number == 0x012A) || (!_oot && actor.Number == 0x0093)) && EnvColorFor(actor) is { } crySwTint)
                        tris = tris.Select(t => { t.C0 *= crySwTint; t.C1 *= crySwTint; t.C2 *= crySwTint; return t; }).ToList();
                    // Bg_Bdan_Switch tall types (0x00E6, YELLOW_TALL_1/2 = 3/4): the game stretches the flat
                    // yellow-button DL ~4× in Y (unk_1D0/unk_1D4) into a tall column. The Model scale is uniform
                    // (the X/Z factor), so pre-stretch the vertices' Y so the tall button reads as a column.
                    if (_oot && actor.Number == 0x00E6 && (actor.Variable & 0xFF) is 3 or 4)
                    {
                        const float yStretch = 0.205f / 0.05375f;   // unk_1D0 / unk_1D4 for the tall switch
                        tris = tris.Select(t =>
                        {
                            t.P0 = new OpenTK.Mathematics.Vector3(t.P0.X, t.P0.Y * yStretch, t.P0.Z);
                            t.P1 = new OpenTK.Mathematics.Vector3(t.P1.X, t.P1.Y * yStretch, t.P1.Z);
                            t.P2 = new OpenTK.Mathematics.Vector3(t.P2.X, t.P2.Y * yStretch, t.P2.Z);
                            return t;
                        }).ToList();
                    }
                    // Overlay extra DLs that live in gameplay_keep at the same origin (a shutter's metal-bar
                    // lattice grille, whose door panel is in a different dungeon object). Read them from the
                    // keep's own bytes/file index so their segment-04 references resolve.
                    if (keepOverlayDls != null)
                    {
                        int kid = _objects.IdOf("object_gameplay_keep") ?? 1;
                        var keepBytes = _objects.GetObjectBytes(_rom, kid);
                        var keepV = _objects.ResolveId(kid);
                        if (keepBytes != null && keepV != null)
                        {
                            int keepFi = FileIndexOf(keepV.Value.start);
                            // The metal-bar grille bars the doorway on the side Link enters; push it to the
                            // door's FRONT face (+Z) so it reads as a grille OVER the panel instead of being
                            // embedded/coplanar (env-mapped metal hides it otherwise). Front face ≈ +1000 raw.
                            var barTris = keepOverlayDls.SelectMany(off => SafeDl(keepBytes, keepFi, off)).ToList();
                            for (int k = 0; k < barTris.Count; k++)
                            { var t = barTris[k]; var dz = new OpenTK.Mathematics.Vector3(0, 0, 20f);
                              t.P0 += dz; t.P1 += dz; t.P2 += dz; barTris[k] = t; }
                            tris.AddRange(barTris);
                        }
                    }
                    // Transformed lock/chain pieces of a locked door: read each DL from its source object
                    // (gameplay_dangeon_keep for the small-key lock, object_bdoor for the boss-key lock)
                    // and bake it through its sub-matrix so the lock+chains sit centred in front of the door.
                    if (doorAttach != null)
                    {
                        foreach (var grp in doorAttach.GroupBy(a => a.src))
                        {
                            byte[]? sb; int sfi;
                            if (grp.Key == "keep5")
                            {
                                int dkid = _objects.IdOf("gameplay_dangeon_keep") ?? 3;
                                sb = _objects.GetObjectBytes(_rom, dkid);
                                sfi = _objects.ResolveId(dkid) is { } dv ? FileIndexOf(dv.start) : -1;
                            }
                            else
                            {
                                sb = _objects.GetObjectBytes(_rom, grp.Key);
                                sfi = _objects.Resolve(grp.Key) is { } gv ? FileIndexOf(gv.start) : -1;
                            }
                            if (sb == null || sfi < 0) continue;
                            // The lock/chain DLs reference their OWN segment for vertices (the dangeon-keep
                            // lock uses segment 5 = itself), so resolve segment 5 to this source file index;
                            // segment 4 stays the shared gameplay_keep.
                            int kf5 = grp.Key == "keep5" ? sfi : _keep5FileIndex;
                            int aseg8 = grp.Key == "object_bdoor" ? doorSeg8 : -1;   // boss lock shares the temple emblem
                            List<MeshTri> ReadAttach(int off)
                            {
                                try { return ObjectModelReader.ReadDList(sb, sfi, off, _keepFileIndex, kf5, aseg8).ToList(); }
                                catch { return new List<MeshTri>(); }
                            }
                            foreach (var a in grp)
                                foreach (var t in ReadAttach(a.dl))
                                {
                                    var tt = t;
                                    tt.P0 = a.xform(t.P0); tt.P1 = a.xform(t.P1); tt.P2 = a.xform(t.P2);
                                    tris.Add(tt);
                                }
                        }
                    }
                }
                else if (ObjectDlOverride.TryGetValue(objName, out int[]? ovrDls))
                    // Hand-specified static display lists. Some carry a per-DL skeleton-limb transform (the
                    // chest body/lid limbs) so the lid sits closed on top instead of flat on the floor.
                    tris = ovrDls.SelectMany(off =>
                    {
                        var dlTris = ObjectModelReader.ReadDList(bytes, fileIdx, off, _keepFileIndex, _keep5FileIndex);
                        if (DlTransform.TryGetValue((objName, off), out var d))
                            for (int k = 0; k < dlTris.Count; k++)
                            { var t = dlTris[k]; t.P0 = d(t.P0); t.P1 = d(t.P1); t.P2 = d(t.P2); dlTris[k] = t; }
                        return dlTris;
                    }).ToList();
                else if (dlOffsets.Count > 1)
                    // Several explicit display lists (e.g. a chest's body + lid) — merge them all.
                    tris = dlOffsets.SelectMany(off => ObjectModelReader.ReadDList(bytes, fileIdx, off, _keepFileIndex, _keep5FileIndex)).ToList();
                else if (dlOffsets.Count == 1 && dlCount > 1)
                    // One offset, several sequential DLs (e.g. a tree's trunk + leaves).
                    tris = ObjectModelReader.ReadDListChain(bytes, fileIdx, dlOffsets[0], dlCount, _keepFileIndex, _keep5FileIndex);
                else
                {
                    // The player is posed with Link's real idle frame (PlayerIdlePose) since his
                    // animations live in the external link_animetion file; other actors use frame 0
                    // of the animation embedded in their own object.
                    autoDetectPath = true;
                    tris = ObjectModelReader.ReadBestModel(bytes, fileIdx,
                        dlOffsets.Count == 1 ? dlOffsets[0] : null,
                        // Link's animations live in the external link_animetion file (identical idle
                        // data in OoT and MM), so PlayerIdlePose poses both Adult Link and MM's Fierce
                        // Deity (object_link_boy) — their player skeletons share the limb layout.
                        poseOverride: extPose ?? (poseAsLink ? PlayerIdlePose : null),
                        keepFileIndex: _keepFileIndex,
                        // The env-colour table holds OoT actor ids / colours (Kokiri tunic, Gerudo
                        // body); MM's Fierce Deity carries its colours in its textures and uses none.
                        envOverride: EnvColorFor(actor),
                        // Player face seg 8/9 → eye/mouth texture in the link object: OoT Adult Link
                        // gLinkAdultEyesOpenTex@0x0000 / mouth@0x4000; MM Fierce Deity
                        // gLinkFierceDeityEyesTex@0x9708 / gLinkFierceDeityMouthTex@0xA908.
                        // Eye/mouth face textures: the player's are fixed; other face NPCs come from
                        // the extracted per-actor table so they show eyes + a mouth, not a blank face.
                        eyeOff: isPlayer ? (_oot ? 0x0000 : 0x9708) : FaceTexFor(actor.Number).eye,
                        mouthOff: isPlayer ? (_oot ? 0x4000 : 0xA908) : FaceTexFor(actor.Number).mouth,
                        keep5FileIndex: _keep5FileIndex,
                        // Explicit idle animation for skeletons the auto-pose collapses (Queen Gohma).
                        // Idle pose: a hand-curated pin first (special cases the name heuristic misses),
                        // else the generated idle/wait offset from the decomp object XMLs, else auto-detect.
                        animFrame0Offset: ObjectAnimSource.TryGetValue(objName, out var _animSrc) ? _animSrc.off
                                          : (!_oot && MmObjectAnimOffset.TryGetValue(objName, out var _mIdle)) ? _mIdle
                                          : ObjectAnimOffset.TryGetValue(objName, out var _idleOff) ? _idleOff
                                          : (_idleAnims.OffsetFor(objName) ?? -1),
                        // Cross-object idle anim: load the anim OBJECT's bytes so its frame 0 poses this skeleton.
                        animObj: ObjectAnimSource.TryGetValue(objName, out var _as2) ? _objects.GetObjectBytes(_rom, _as2.animObj) : null,
                        // Actors that draw a separate HAIR/HEAD display list at a limb via a Post/OverrideLimbDraw
                        // callback (the skeleton's own limb DL there is bald) — En_Hy townsfolk heads, the
                        // Gerudos' hairstyles, carpenters, etc. Without replaying it the actor renders bald.
                        headLimb: _oot && actor.Number == 0x016E ? EnHyHeadLimb
                                  : HeadDlFor(actor.Number) is { } hd ? hd.limb : -1,
                        headDl:   _oot && actor.Number == 0x016E ? EnHyHeadDls[Math.Clamp(actor.Variable & 0x7F, 0, EnHyHeadDls.Length - 1)]
                                  : HeadDlFor(actor.Number) is { } hd2 ? hd2.dl : -1,
                        // Textures the actor's C draw code binds to segments 8-D (tektite carapace, enemy eyes…).
                        segTex: SegTexFor(_oot, actor.Number, actor.Variable),
                        // Cross-object seg8-D texture files (Bg_Mori_* geometry in object_mori_objects binds
                        // seg8 to the separate object_mori_tex the actor bank-loads).
                        segTexFile: SegTexFileFor(actor.Number),
                        // Use a specific skeleton for actors with several (Kaepora Gaebora's perching skeleton).
                        skelOffset: objName == null ? -1
                                    : (!_oot && MmSkelOverride.TryGetValue(objName, out var _mso)) ? _mso
                                    : SkelOverride.TryGetValue(objName, out var _so) ? _so : -1);
                }
                // Gold Skulltula (En_Sw 0x0095, params bits [15:13] > 0) shares the Skullwalltula model and is
                // recoloured gold in-game by a colour filter, not a different texture — its combiner doesn't
                // sample ENV, so (like the rupee) force the gold tint onto the vertex shade so it reads as gold.
                // Same forced-shade tint for the Jabu tentacles (En_Bx/En_Ba), whose per-variant colour is a
                // seg-8 texture the single-texture reader can't surface — tint so the colour preset shows.
                if (_oot && (actor.Number == 0x0095 || actor.Number == 0x00DE || actor.Number == 0x00DF)
                    && EnvColorFor(actor) is { } swTint)
                    tris = tris.Select(t => { t.C0 *= swTint; t.C1 *= swTint; t.C2 *= swTint; return t; }).ToList();
                // Sanity-cap the AUTO-DETECT path only: a Bg_/Dm_/Eff_ scenery actor that shares a big
                // boss/NPC object (Bg_Ganon_Otyuka→OBJECT_GANON, Bg_Po_Event→OBJECT_PO_SISTERS) has its real
                // geometry embedded in its overlay, which the object reader can't see — so ReadBestModel grabs
                // the largest DL/skeleton in the shared object (the boss body) and renders a room-filling giant.
                // Pinned/override models are trusted; only the guess is capped. Over ~5000u of model extent is
                // never a normal actor (the legit clock-tower peaks at ~4800u), so treat it as a mis-resolution
                // and drop to the placeholder box rather than obscure the level with a giant wrong mesh.
                if (autoDetectPath && tris.Count > 0)
                {
                    var amn = new OpenTK.Mathematics.Vector3(1e9f);
                    var amx = new OpenTK.Mathematics.Vector3(-1e9f);
                    foreach (var t in tris)
                        foreach (var p in new[] { t.P0, t.P1, t.P2 })
                        { amn = OpenTK.Mathematics.Vector3.ComponentMin(amn, p); amx = OpenTK.Mathematics.Vector3.ComponentMax(amx, p); }
                    var asz = (amx - amn) * scale;
                    if (MathF.Max(asz.X, MathF.Max(asz.Y, asz.Z)) > 5000f)
                        tris = new List<MeshTri>();   // giant wrong-object guess → placeholder box
                }
                if (tris.Count > 0) model = new Model(tris, scale, ignoreYaw, ActorOrientation.For(actor.Number, !_oot));
            }
        }
        catch { model = null; }

        _cache[key] = model;
        return model;
    }

    private int FileIndexOf(uint vromStart)
    {
        foreach (var f in _rom.Files) if (f.Exists && f.VromStart == vromStart) return f.Index;
        return -1;
    }

    // ── Overlay-embedded meshes ─────────────────────────────────────────────
    // A handful of actors keep their drawn geometry in their OVERLAY file (not a shared object): the mesh
    // is unreadable via the object path, so auto-detect grabbed the borrowed object's body (giant Ganon).
    // These recipes read the overlay's real display lists (version-independent scan) and, where the actor's
    // Draw instances a unit DL with per-piece matrices, replay those transforms so the shape is faithful.
    private Model? TryOverlayModel(ZActor actor)
    {
        try
        {
            // OoT Bg_Ganon_Otyuka (0x0106): the collapsing boss-floor TILE. sPlatformTopDL (120×120 cap) +
            // sPlatformSideDL (120×60 wall) instanced at the 4 sSideCenters with the 4 sSideAngles. Multiple
            // actors tile the arena. (z_bg_ganon_otyuka.c BgGanonOtyuka_Draw; scale = ICHAIN DIV1000(1000)=1.)
            if (_oot && actor.Number == 0x0106)
                return BuildGanonOtyuka(actor);
            // En_Ganon_Organ (0x15E): the tower organ room — sRoomOrganAndFloorDL + sRoomStatuesDL drawn at
            // the origin (identity matrix, world scale). Overlay-embedded; borrows OBJECT_GANON only for
            // textures, so auto-detect otherwise renders Ganondorf's body. Static DLs at origin → simple merge.
            if (_oot && actor.Number == 0x015E)
                return BuildOverlayModelSimple(actor, 0x015E, 1.0f);
        }
        catch { }
        return null;
    }

    // General overlay-mesh recipe for actors whose Draw renders their overlay-embedded display lists at the
    // origin with no per-piece instancing (En_Ganon_Organ, …): merge every geometry DL, each textured by its
    // nearest preceding material DL. Actors that INSTANCE a unit DL (Bg_Ganon_Otyuka) need a bespoke builder.
    private Model? BuildOverlayModelSimple(ZActor actor, int actorId, float scale)
    {
        if (Overlays.For(actorId) is not { } e) return null;
        var bytes = Overlays.GetOverlayBytes(_rom, actorId);
        if (bytes == null) return null;
        int fileIdx = FileIndexOf(e.VromStart);
        var dls = ObjectModelReader.ScanOverlayGeometry(bytes, fileIdx, e.VramStart);
        var mats = ObjectModelReader.ScanOverlayMaterials(bytes, e.VramStart);
        var outTris = new List<MeshTri>();
        foreach (var (off, tris0) in dls)
        {
            int mat = -1; foreach (int m in mats) if (m < off) mat = m;
            var tris = mat >= 0 ? ObjectModelReader.ReadOverlayDLs(bytes, fileIdx, e.VramStart, mat, off) : tris0;
            outTris.AddRange(tris.Count > 0 ? tris : tris0);
        }
        if (outTris.Count == 0) return null;
        return new Model(outTris, scale, false, ActorOrientation.For(actor.Number, !_oot));
    }

    private Model? BuildGanonOtyuka(ZActor actor)
    {
        if (Overlays.For(0x0106) is not { } e) return null;
        var bytes = Overlays.GetOverlayBytes(_rom, 0x0106);
        if (bytes == null) return null;
        int fileIdx = FileIndexOf(e.VromStart);
        var dls = ObjectModelReader.ScanOverlayGeometry(bytes, fileIdx, e.VramStart);
        var mats = ObjectModelReader.ScanOverlayMaterials(bytes, e.VramStart);
        // Re-read each geometry DL WITH its nearest preceding material DL so the platform texture binds
        // (the actor's Draw runs sPlatformMaterialDL then the geometry as separate DLs).
        List<MeshTri> Textured(int geomOff, List<MeshTri> fallback)
        {
            int mat = -1;
            foreach (int m in mats) if (m < geomOff) mat = m;
            if (mat < 0) return fallback;
            var t = ObjectModelReader.ReadOverlayDLs(bytes, fileIdx, e.VramStart, mat, geomOff);
            return t.Count > 0 ? t : fallback;
        }
        // Exclude the flash XLU effect (large 720×300 quad); classify the rest by Y-extent: near-flat =
        // top/bottom cap (draw once at the tile centre), tall = the side wall (instance around the tile).
        var caps = new List<MeshTri>();
        List<MeshTri>? side = null;
        foreach (var (off, tris0) in dls)
        {
            var tris = Textured(off, tris0);
            float mny = 1e9f, mxy = -1e9f, mnx = 1e9f, mxx = -1e9f, mnz = 1e9f, mxz = -1e9f;
            foreach (var t in tris) foreach (var p in new[] { t.P0, t.P1, t.P2 })
            { mny = MathF.Min(mny, p.Y); mxy = MathF.Max(mxy, p.Y); mnx = MathF.Min(mnx, p.X);
              mxx = MathF.Max(mxx, p.X); mnz = MathF.Min(mnz, p.Z); mxz = MathF.Max(mxz, p.Z); }
            float sx = mxx - mnx, sy = mxy - mny, sz = mxz - mnz;
            if (sx > 400 || sz > 400) continue;         // the flash effect — not the platform
            if (sy < 10) caps.AddRange(tris);           // horizontal cap (top / bottom)
            else side ??= tris;                          // the vertical wall unit
        }
        var outTris = new List<MeshTri>(caps);
        if (side != null)
        {
            // sSideCenters / sSideAngles from z_bg_ganon_otyuka.c (geometry constants, version-independent).
            (OpenTK.Mathematics.Vector3 c, float a)[] sides =
            {
                (new(60, 0, 0),  MathF.PI / 2), (new(-60, 0, 0), -MathF.PI / 2),
                (new(0, 0, 60),  0f),           (new(0, 0, -60),  MathF.PI),
            };
            foreach (var (c, a) in sides)
            {
                float cs = MathF.Cos(a), sn = MathF.Sin(a);
                OpenTK.Mathematics.Vector3 Xf(OpenTK.Mathematics.Vector3 p) =>
                    new(c.X + (p.X * cs + p.Z * sn), c.Y + p.Y, c.Z + (-p.X * sn + p.Z * cs));
                foreach (var t in side)
                { var tt = t; tt.P0 = Xf(t.P0); tt.P1 = Xf(t.P1); tt.P2 = Xf(t.P2); outTris.Add(tt); }
            }
        }
        if (outTris.Count == 0) return null;
        // Actor_SetScale(1000/1000)=1.0 → the 120u pieces render at world scale (rooms are 1:1).
        return new Model(outTris, 1.0f, false, ActorOrientation.For(actor.Number, !_oot));
    }

    // Object-space (scaled + base-rotated, pre-yaw, pre-translate) bounds per resolved model.
    private readonly Dictionary<Model, (OpenTK.Mathematics.Vector3 min, OpenTK.Mathematics.Vector3 max)> _localBounds = new();

    /// <summary>World-space axis-aligned bounding box of an actor's drawn model (mirrors the exact
    /// scale → base-rotation → yaw → translate transform the renderer uses), or null when the actor
    /// has no model. Used for model-based selection (picking by the model footprint, Hammer-style)
    /// and to suppress the origin marker for modelled actors.</summary>
    public (OpenTK.Mathematics.Vector3 min, OpenTK.Mathematics.Vector3 max)? ModelWorldBounds(ZActor actor, bool adult)
    {
        var model = Resolve(actor, adult);
        if (model == null) return null;

        if (!_localBounds.TryGetValue(model, out var lb))
        {
            var br = model.BaseRotationDeg;
            bool hasBase = br != OpenTK.Mathematics.Vector3.Zero;
            var baseM = hasBase
                ? OpenTK.Mathematics.Matrix3.CreateRotationZ(OpenTK.Mathematics.MathHelper.DegreesToRadians(br.Z))
                  * OpenTK.Mathematics.Matrix3.CreateRotationY(OpenTK.Mathematics.MathHelper.DegreesToRadians(br.Y))
                  * OpenTK.Mathematics.Matrix3.CreateRotationX(OpenTK.Mathematics.MathHelper.DegreesToRadians(br.X))
                : OpenTK.Mathematics.Matrix3.Identity;
            var mn = new OpenTK.Mathematics.Vector3(1e9f);
            var mx = new OpenTK.Mathematics.Vector3(-1e9f);
            foreach (var t in model.Tris)
                foreach (var p in new[] { t.P0, t.P1, t.P2 })
                {
                    var q = (hasBase ? baseM * p : p) * model.Scale;
                    mn = OpenTK.Mathematics.Vector3.ComponentMin(mn, q);
                    mx = OpenTK.Mathematics.Vector3.ComponentMax(mx, q);
                }
            if (mn.X > mx.X) { mn = OpenTK.Mathematics.Vector3.Zero; mx = OpenTK.Mathematics.Vector3.Zero; }
            _localBounds[model] = lb = (mn, mx);
        }

        float ang = model.IgnoreYaw ? 0f : actor.YRot * (MathF.PI / 32768f);   // binary angle → radians
        float cs = MathF.Cos(ang), sn = MathF.Sin(ang);
        var dofs = ModelDrawOffset(actor, model);   // per-actor model draw offset (world-space, yaw-rotated)
        float ox = actor.XPos + dofs.X, oy = actor.YPos + dofs.Y, oz = actor.ZPos + dofs.Z;
        var wmn = new OpenTK.Mathematics.Vector3(1e9f);
        var wmx = new OpenTK.Mathematics.Vector3(-1e9f);
        foreach (float x in new[] { lb.min.X, lb.max.X })
            foreach (float y in new[] { lb.min.Y, lb.max.Y })
                foreach (float z in new[] { lb.min.Z, lb.max.Z })
                {
                    // Same Y-rotation + translation the renderer applies (ImportedMeshRenderer.Xform).
                    var w = new OpenTK.Mathematics.Vector3(
                        ox + (x * cs + z * sn), oy + y, oz + (-x * sn + z * cs));
                    wmn = OpenTK.Mathematics.Vector3.ComponentMin(wmn, w);
                    wmx = OpenTK.Mathematics.Vector3.ComponentMax(wmx, w);
                }
        return (wmn, wmx);
    }

    // ── Per-actor model draw offset (position audit) ────────────────────────
    // In-game an actor's MODEL is drawn at world.pos + a per-actor offset the editor otherwise misses:
    //   - shape.yOffset*scale.y is added to world.pos.y by the GENERIC Actor_Draw (z_actor.c:2772) for EVERY
    //     actor — zero for most; the entries below are the actors whose init sets a non-zero yOffset.
    //   - a few actors' own Draw apply a constant Matrix_Translate to the whole body before drawing.
    // `scaled` = multiply by the actor's model scale (true for shape.yOffset and scaled-frame translates).
    // Offsets are in the actor's LOCAL frame (rotate with yaw). Audited from the OoT decomp (multi-agent
    // sweep, verified). CONDITIONAL/variable-gated offsets (En_G_Switch rupee y+700, Bg_Ice_Turara hanging
    // y+1200) and negligible ones (En_Bdfire +11 billboard, Door_Warp1 +1) are intentionally omitted.
    private static readonly Dictionary<ushort, (OpenTK.Mathematics.Vector3 off, bool scaled)> OotDrawOffset = new()
    {
        [0x001B] = (new(0f, -200f, 0f), true),      // En_Tite (Tektite)     shape.yOffset -200
        [0x001C] = (new(0f, -27500f, 0f), true),    // En_Reeba              shape.yOffset -27500 (mostly buried)
        [0x0027] = (new(0f, 9200f, 0f), true),      // Boss_Dodongo          shape.yOffset 9200
        [0x0028] = (new(0f, 4000f, 0f), true),      // Boss_Gohma            shape.yOffset 4000 (ceiling)
        [0x019E] = (new(0f, 118f, 0f), true),       // Obj_Comb (beehive)    +118 (scaled frame)
        [0x01AC] = (new(0f, 0f, -560f), true),      // En_Tg                 -560 Z (body)
        [0x01AD] = (new(-1200f, 0f, -1400f), true), // En_Mu (market kids)   formation offset
        [0x01D2] = (new(0f, 80f, 0f), true),        // Obj_Hamishi           shape.yOffset 80
    };
    // MM draw offsets (multi-agent audit of all 573 MM actors, verified). shape.yOffset + scaled-frame body
    // translates (scaled=true); a couple of world-frame translates (scaled=false). Conditional/type-gated
    // (Door_Warp1 crystal, En_Karebaba dead, En_Encount3, Bg_Icicle stalactite, En_Zot cases, En_S_Goro
    // rolled-up), sub-part-only (En_Zod/En_Zos keyboard), ambiguous-huge (En_Door_Etc, En_Cha) and negligible
    // (En_Elfbub z1, En_Geg y14) findings are intentionally omitted.
    private static readonly Dictionary<ushort, (OpenTK.Mathematics.Vector3 off, bool scaled)> MmDrawOffset = new()
    {
        [0x003C] = (new(0f, 1500f, 0f), true),      // En_Bbfall
        [0x003E] = (new(0f, 1500f, 0f), true),      // En_Bb
        [0x0043] = (new(0f, 5500f, 0f), true),      // En_Death (Gomess)
        [0x014B] = (new(0f, 1500f, 0f), true),      // En_Pr
        [0x0155] = (new(0f, -3000f, 0f), true),     // En_Baguo
        [0x0180] = (new(0f, 500f, 0f), true),       // En_Pr2
        [0x0181] = (new(0f, 500f, 0f), true),       // En_Prz
        [0x0182] = (new(0f, 960f, 0f), true),       // En_Jso2
        [0x01D2] = (new(0f, 700f, 0f), true),       // En_Gamelupy
        [0x01F5] = (new(0f, 1100f, 0f), true),      // En_Zoraegg
        [0x0224] = (new(0f, 1000f, 0f), true),      // En_Zog (Zora chef)
        [0x025A] = (new(0f, 700f, 0f), true),       // En_Sc_Ruppe
        [0x025F] = (new(0f, 1600f, 0f), true),      // En_Hidden_Nuts
        [0x027C] = (new(0f, 700f, 0f), true),       // En_Scopecoin
        [0x028B] = (new(0f, 1100f, 0f), true),      // Obj_Milk_Bin
        [0x0290] = (new(0f, -60f, 0f), true),       // En_Recepgirl
        [0x00BD] = (new(0f, 0f, -1000f), true),     // En_Ani
        [0x00E4] = (new(0f, 118f, 0f), true),       // Obj_Comb (beehive)
        [0x012B] = (new(0f, -600f, 0f), true),      // Boss_03
        [0x012C] = (new(0f, 0f, 800f), true),       // Boss_04
        [0x01EA] = (new(-100f, 0f, 0f), true),      // En_Hakurock
        [0x0156] = (new(0f, 60f, 0f), false),       // Obj_Vspinyroll (world-frame +60)
        [0x0168] = (new(0f, 1100f, 0f), true),      // En_Dnh (Deku scrub variant)
        [0x0172] = (new(-55f, 0f, 0f), false),      // Bg_Spout_Fire (world-frame -55 X)
        // (skipped from the re-run: En_GirlA y24 / En_Dnk y18 / En_Dnq y14 negligible; En_Tite y-3000 is the
        //  MINUS_3 param type only, so it's variable-gated like the OoT conditional cases.)
    };

    /// <summary>World-space offset to add to the actor's position when drawing its MODEL (not the origin
    /// reticule), so the editor matches the in-game draw. Rotates with the actor's yaw. Zero for most actors.</summary>
    public OpenTK.Mathematics.Vector3 ModelDrawOffset(ZActor actor, Model model)
    {
        var tbl = _oot ? OotDrawOffset : MmDrawOffset;
        if (!tbl.TryGetValue(actor.Number, out var e)) return OpenTK.Mathematics.Vector3.Zero;
        var local = e.scaled ? e.off * model.Scale : e.off;
        float ang = model.IgnoreYaw ? 0f : actor.YRot * (MathF.PI / 32768f);
        float cs = MathF.Cos(ang), sn = MathF.Sin(ang);
        return new OpenTK.Mathematics.Vector3(local.X * cs + local.Z * sn, local.Y, -local.X * sn + local.Z * cs);
    }

    // Model-local edge endpoints (base rotation + scale already applied, yaw/translation NOT) per Model —
    // computed once and reused, since only the actor's yaw + position vary between placements.
    private readonly Dictionary<Model, float[]> _localEdges = new();

    /// <summary>The actor's model as WORLD-space wireframe edges (flat x,y,z; consecutive points pair into
    /// line segments) — the same transform ModelWorldBounds uses, so the wireframe sits exactly where the
    /// model renders. Null if the actor has no model (caller falls back to a box). Cache the result by the
    /// actor's transform; this rebuilds every vertex.</summary>
    public float[]? WorldModelEdges(ZActor actor, bool adult)
    {
        var model = Resolve(actor, adult);
        if (model == null || model.Tris.Count == 0) return null;

        if (!_localEdges.TryGetValue(model, out var loc))
        {
            var br = model.BaseRotationDeg;
            bool hasBase = br != OpenTK.Mathematics.Vector3.Zero;
            var baseM = hasBase
                ? OpenTK.Mathematics.Matrix3.CreateRotationZ(OpenTK.Mathematics.MathHelper.DegreesToRadians(br.Z))
                  * OpenTK.Mathematics.Matrix3.CreateRotationY(OpenTK.Mathematics.MathHelper.DegreesToRadians(br.Y))
                  * OpenTK.Mathematics.Matrix3.CreateRotationX(OpenTK.Mathematics.MathHelper.DegreesToRadians(br.X))
                : OpenTK.Mathematics.Matrix3.Identity;
            var pts = new List<float>(model.Tris.Count * 18);
            void Edge(OpenTK.Mathematics.Vector3 a, OpenTK.Mathematics.Vector3 b)
            { pts.Add(a.X); pts.Add(a.Y); pts.Add(a.Z); pts.Add(b.X); pts.Add(b.Y); pts.Add(b.Z); }
            foreach (var t in model.Tris)
            {
                var a = (hasBase ? baseM * t.P0 : t.P0) * model.Scale;
                var b = (hasBase ? baseM * t.P1 : t.P1) * model.Scale;
                var c = (hasBase ? baseM * t.P2 : t.P2) * model.Scale;
                Edge(a, b); Edge(b, c); Edge(c, a);
            }
            _localEdges[model] = loc = pts.ToArray();
        }

        float ang = model.IgnoreYaw ? 0f : actor.YRot * (MathF.PI / 32768f);
        float cs = MathF.Cos(ang), sn = MathF.Sin(ang);
        var dofs = ModelDrawOffset(actor, model);
        float ox = actor.XPos + dofs.X, oy = actor.YPos + dofs.Y, oz = actor.ZPos + dofs.Z;
        var outp = new float[loc.Length];
        for (int i = 0; i < loc.Length; i += 3)
        {
            float x = loc[i], y = loc[i + 1], z = loc[i + 2];
            outp[i]     = ox + (x * cs + z * sn);
            outp[i + 1] = oy + y;
            outp[i + 2] = oz + (-x * sn + z * cs);
        }
        return outp;
    }
}
