namespace MegatonHammer.Textures;

/// <summary>
/// Tool textures with engine-level meaning (Valve-Hammer style). They are visible in the
/// editor as distinctive swatches but behave specially on export: NODRAW/CLIP faces are
/// not drawn in-game, and the clip variants drive collision surface flags instead of
/// geometry. Classification is by reserved texture name (case-insensitive).
/// </summary>
[Flags]
public enum SpecialKind
{
    None            = 0,
    NoRender        = 1 << 0,   // omitted from exported display lists (no in-game mesh)
    PlayerClip      = 1 << 1,   // collision wall that blocks the player
    ProjectileBlock = 1 << 2,   // collision that blocks projectiles (OoT "ignore" flags)
    WaterSurface    = 1 << 3,   // a water brush's surface face → drives a collision waterbox
    VoidOut         = 1 << 4,   // #7 floor that instantly reloads the scene (FloorProperty 12)
    VoidRespawn     = 1 << 5,   // #7 floor that respawns Link at the last safe spot (FloorProperty 5)
    Climbable       = 1 << 6,   // invisible collision wall Link can climb (ladder/vine wall-type flags)
    Warp            = 1 << 7,   // invisible trigger volume: walking in fires the brush's ExitEntrance
}

public static class SpecialTextures
{
    // Reserved tool-texture names.
    public const string NoDraw          = "NODRAW";
    public const string Clip            = "CLIP";
    public const string BlockProjectile = "BLOCKPROJECTILE";
    public const string Water           = "WATERBOX";
    public const string VoidOut         = "VOIDOUT";        // #7 instant scene reload
    public const string VoidRespawn     = "VOIDRESPAWN";    // #7 soft void → last safe spot
    public const string Ladder          = "LADDER";         // invisible ladder collision (climb side, wall type 2)
    public const string Vines           = "VINES";          // invisible vine/front-climb collision (wall type 4)
    public const string Crawlspace      = "CRAWLSPACE";     // invisible child crawlspace (wall type 5)
    public const string Warp            = "WARP";           // invisible warp trigger (brush's ExitEntrance)

    public static readonly string[] Names =
        [NoDraw, Clip, BlockProjectile, Water, VoidOut, VoidRespawn, Ladder, Vines, Crawlspace, Warp];

    public static SpecialKind Classify(string? textureName) => textureName?.ToUpperInvariant() switch
    {
        NoDraw          => SpecialKind.NoRender,
        Clip            => SpecialKind.NoRender | SpecialKind.PlayerClip,
        BlockProjectile => SpecialKind.NoRender | SpecialKind.ProjectileBlock,
        Water           => SpecialKind.WaterSurface,
        // The void tool textures are NOT rendered (they mark a surface's collision behaviour, like CLIP) —
        // texture the floor normally AND drop a thin void brush over it, or use these on a hidden trigger
        // floor. void-out FloorProperty 12 / soft-void 5 verified in z_player.c (4498-4514).
        // NOTE: "burning areas" are deliberately NOT a tool texture — OoT lava damage is the scene's hot
        // environment (Goron-Tunic-gated) or a damage actor, not a floor surface type; a damaging-lava
        // texture would be a lie. (The lava MATERIAL is only a footstep sound.)
        VoidOut         => SpecialKind.NoRender | SpecialKind.VoidOut,
        VoidRespawn     => SpecialKind.NoRender | SpecialKind.VoidRespawn,
        // Climbable/warp tool textures: invisible collision-only surfaces. LADDER/VINES/CRAWLSPACE drive the
        // wall-type climb flags (see SurfaceBits); WARP marks a trigger volume (CollisionBuilder gives it the
        // brush's exit index). Place these behind a ladder actor's model, or on their own for an invisible climb.
        Ladder          => SpecialKind.NoRender | SpecialKind.Climbable,
        Vines           => SpecialKind.NoRender | SpecialKind.Climbable,
        Crawlspace      => SpecialKind.NoRender | SpecialKind.Climbable,
        Warp            => SpecialKind.NoRender | SpecialKind.Warp,
        _               => SpecialKind.None,
    };

    /// <summary>The collision surface-type bits (data0, data1) for a special-surface face, or null if it
    /// carries no surface behaviour. data0 FloorProperty = bits [29:26]. Verified against z64bgcheck.h /
    /// z_player.c (FLOOR_PROPERTY_12 = void-out, FLOOR_PROPERTY_5 = soft void → respawn).</summary>
    public static (uint data0, uint data1)? SurfaceBits(string? textureName) => textureName?.ToUpperInvariant() switch
    {
        VoidOut     => (12u << 26, 0u),   // FloorProperty 12 = void-out
        VoidRespawn => (5u << 26, 0u),    // FloorProperty 5 = soft void
        // Climbable wall types → data0 bits 21-25 (wall type index → wall-flags table {…,3(t2),5(t3),8(t4),…}).
        // t2 ladder side-climb (WALL_FLAG_1), t4 vines/front-climb (WALL_FLAG_3), t5 crawlspace (WALL_FLAG_4).
        // VERIFIED z_bgcheck.c sWallFlags + z_player.c climb check. By reserved name so LADDER vs VINES differ.
        Ladder      => (2u << 21, 0u),
        Vines       => (4u << 21, 0u),
        Crawlspace  => (5u << 21, 0u),
        _           => ((uint, uint)?)null,
    };

    /// <summary>True if a face with this texture must be omitted from the rendered game mesh.</summary>
    public static bool IsNoRender(string? textureName) => Classify(textureName).HasFlag(SpecialKind.NoRender);

    public static bool IsSpecial(string? textureName) => Classify(textureName) != SpecialKind.None;
}
