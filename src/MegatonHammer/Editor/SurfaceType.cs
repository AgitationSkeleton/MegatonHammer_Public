namespace MegatonHammer.Editor;

/// <summary>
/// Friendly accessors for the OoT/MM collision SurfaceType word pair (data0, data1). Bit positions are
/// the exact decomp values (z_bgcheck.c SurfaceType_* accessors): floor property, wall type, floor type,
/// hookshot, soft/sinking, horse-block, conveyor, sound material — so the editor can present each as a
/// labelled control instead of a raw hex word, and round-trip identically. Same words feed both the N64
/// CollisionBuilder and the OTR OtrCollisionHeader, so OoT/MM/SoH/2Ship behave the same.
/// </summary>
public static class SurfaceType
{
    // ── data0 fields ──
    public static int  FloorProperty(uint d0) => (int)(d0 >> 26 & 0xF);   // 0 normal, 5 void/respawn, 12 void-out
    public static uint WithFloorProperty(uint d0, int v) => Set(d0, 26, 0xF, v);
    // Wall TYPE index (data0>>21 & 0x1F) → wall-FLAGS via the game's table sWallFlags/D_80119D90 =
    // {0,1,3,5,8,16,32,64}. The Player climbs when the resulting flags have WALL_FLAG_1 (type 2, ladder
    // side-climb), WALL_FLAG_2 (type 3), or WALL_FLAG_3 (type 4, vines/front-climb); crawlspace = flags
    // 0x30 (types 5/6, child only); grabbable ledge = WALL_FLAG_6 (type 7). VERIFIED z_bgcheck.c sWallFlags
    // + z_player.c climb check (MM 9754/OoT 7498). NOTE type 1 = WALL_FLAG_0 only = NOT climbable.
    public static int  WallType(uint d0) => (int)(d0 >> 21 & 0x1F);
    public static uint WithWallType(uint d0, int v) => Set(d0, 21, 0x1F, v);
    // Wall-type indices that make Link climb (produce WALL_FLAG_1/2/3). Used by the climbable tool textures.
    public const int WallTypeLadder = 2;   // side climb (WALL_FLAG_0|1)
    public const int WallTypeVines  = 4;   // front climb (WALL_FLAG_3) — also how ivy/vines climb
    public const int WallTypeCrawl  = 5;   // crawlspace (WALL_FLAG_4), child Link only
    public static int  FloorType(uint d0) => (int)(d0 >> 13 & 0x1F);      // sound/visual material group
    public static uint WithFloorType(uint d0, int v) => Set(d0, 13, 0x1F, v);
    public static bool Soft(uint d0) => (d0 >> 30 & 1) != 0;              // sinking sand / bog
    public static uint WithSoft(uint d0, bool v) => SetBit(d0, 30, v);
    public static bool HorseBlocked(uint d0) => (d0 >> 31 & 1) != 0;
    public static uint WithHorseBlocked(uint d0, bool v) => SetBit(d0, 31, v);

    // ── data1 fields ──
    public static int  Material(uint d1) => (int)(d1 & 0xF);              // footstep sound group
    public static uint WithMaterial(uint d1, int v) => Set(d1, 0, 0xF, v);
    public static bool Hookshot(uint d1) => (d1 >> 17 & 1) != 0;
    public static uint WithHookshot(uint d1, bool v) => SetBit(d1, 17, v);
    public static int  ConveyorSpeed(uint d1) => (int)(d1 >> 18 & 7);     // 0 = off
    public static uint WithConveyorSpeed(uint d1, int v) => Set(d1, 18, 7, v);
    public static int  ConveyorDirection(uint d1) => (int)(d1 >> 21 & 0x3F);  // 0..63 = 360/64°
    public static uint WithConveyorDirection(uint d1, int v) => Set(d1, 21, 0x3F, v);

    private static uint Set(uint word, int shift, uint mask, int v) =>
        (word & ~(mask << shift)) | (((uint)v & mask) << shift);
    private static uint SetBit(uint word, int shift, bool v) =>
        v ? word | (1u << shift) : word & ~(1u << shift);

    // ── Friendly option lists (only decomp-confirmed names are labelled; the rest stay numeric so the
    //    editor never mislabels a value it hasn't verified). ──
    public static readonly (int Value, string Label)[] FloorProperties =
    {
        (0,  "None (normal floor)"),
        (5,  "Void — respawn (soft, return to last safe spot)"),
        (12, "Void-out (instant scene reload)"),
        (6,  "Edge behaviour 6"), (7, "Edge behaviour 7"), (8, "Edge behaviour 8"),
        (9,  "Edge behaviour 9"), (11, "Edge behaviour 11"),
    };
    // Floor hazard = the FloorType field (data0 >> 13 & 0x1F). Values RE-verified in z_player.c (SoH that runs
    // the playtest): the floor that HURTS is type 2/3 — func_80838144 → 4 HP contact damage every 120/60 frames
    // AND Player_Action_80843CEC sets Link on FIRE (Goron Tunic resists both). Types 4/7 are `func_8083816C` =
    // deep-lava SINK only (gravity pulls Link down) — NO fire, NO damage on their own. Type 9 = fire animation,
    // no HP loss. Void-out is the SEPARATE FloorProperty field above. Type 0 = no hazard.
    // (For a "lava floor that damages", pick 2 or 3 — NOT 4.)
    public static readonly (int Value, string Label)[] FloorHazards =
    {
        (0, "None"),
        (2, "Fire / lava — burns you, slow (≈4 HP / 2s; Goron Tunic protects)"),
        (3, "Fire / lava — burns you, fast (≈4 HP / 1s; Goron Tunic protects)"),
        (9, "Fire — visual only (catch fire, no HP loss)"),
        (4, "Deep lava — sink only (no damage)"),
        (7, "Deep lava — sink faster (no damage)"),
    };
    // VERIFIED vs sWallFlags/D_80119D90 + z_player.c climb checks (was mislabelled: old "1=climbable" is
    // actually WALL_FLAG_0 = NOT climbable; ladder is type 2, vines type 4).
    public static readonly (int Value, string Label)[] WallTypes =
    {
        (0, "None (normal wall)"),
        (2, "Ladder — climb (side)"),
        (4, "Vines / ladder-top — climb (front)"),
        (3, "Climbable (alt / flag 2)"),
        (5, "Crawlspace (child)"),
        (6, "Crawlspace 2 (child)"),
        (7, "Ledge — grab & hang"),
        (1, "No-jump (basic, not climbable)"),
    };
    // Footstep sound material groups (data1 & 0xF) — names from the decomp surface-material SFX table.
    // Footstep sound material groups (data1 & 0xF), names from the decomp SURFACE_MATERIAL enum. NOTE: this
    // is the SOUND/surface material only — e.g. "Lava" sets lava footstep/splash, it does NOT deal damage
    // (vanilla lava damage comes from actors / hot-room timers, not a collision surface type).
    public static readonly (int Value, string Label)[] Materials =
    {
        (0, "Dirt"), (1, "Sand"), (2, "Stone"), (3, "Jabu (wet flesh)"), (4, "Water (shallow)"),
        (5, "Water (deep)"), (6, "Tall grass"), (7, "Lava / magma"), (8, "Grass"), (9, "Bridge (hollow wood)"),
        (10, "Wood"), (11, "Dirt (soft)"), (12, "Ice"), (13, "Carpet"),
    };
}
