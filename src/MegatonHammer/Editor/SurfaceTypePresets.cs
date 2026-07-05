namespace MegatonHammer.Editor;

/// <summary>
/// Curated OoT/MM collision SurfaceType presets — named (data[0], data[1]) word pairs the editor can
/// assign to a brush so it behaves as that surface in-game. The bit positions are from the decomp
/// (SurfaceType_GetFloorProperty = data0>>26&0xF, GetWallType = data0>>21&0x1F, GetFloorType =
/// data0>>13&0x1F); the listed presets use values confirmed against the player/bgcheck code. Anything
/// not covered can be authored with the raw data0/data1 hex fields.
/// </summary>
public static class SurfaceTypePresets
{
    public readonly record struct Preset(string Name, uint Data0, uint Data1);

    private const int FloorPropShift = 26;   // data0 >> 26 & 0xF
    private const int WallTypeShift   = 21;   // data0 >> 21 & 0x1F
    private const int FloorTypeShift  = 13;   // data0 >> 13 & 0x1F

    public static readonly Preset[] All =
    {
        new("Normal floor / wall",       0, 0),
        new("Void-out floor (fall = reload)",  (uint)(12 << FloorPropShift), 0),   // FLOOR_PROPERTY_12 = void out
        new("Respawn floor (soft void)",       (uint)(5  << FloorPropShift), 0),   // FLOOR_PROPERTY_5  = respawn
        new("Wall: ladder — climb (side)",     (uint)(2  << WallTypeShift),  0),   // WALL_TYPE_2 (FLAG_0|FLAG_1) climbable
        new("Wall: vines — climb (front)",     (uint)(4  << WallTypeShift),  0),   // WALL_TYPE_4 (FLAG_3) climbable
        new("Wall: ledge grab + hang",         (uint)(7  << WallTypeShift),  0),   // WALL_TYPE_7 (FLAG_6) grabbable
        new("Wall: crawlspace (child)",        (uint)(5  << WallTypeShift),  0),   // WALL_TYPE_5 (FLAG_4) crawlspace
        // Hurt / hazard floors — the FloorType field (data0>>13). Values verified in z_player.c (SoH):
        // func_80838144 → types 2/3 tick 4 HP contact damage (every 120 / 60 frames; Goron Tunic resists);
        // func_8083816C → types 4/7 set Link on fire (Goron Tunic protects; 7 also sinks). (The old presets
        // mislabelled type 7 as "sand" and type 9 as lava — neither is correct.)
        new("Damaging floor — slow (type 2)",   (uint)(2 << FloorTypeShift), 0),
        new("Damaging floor — fast (type 3)",   (uint)(3 << FloorTypeShift), 0),
        new("Lava — catch fire (type 4)",       (uint)(4 << FloorTypeShift), 0),
        new("Lava — deep sink + fire (type 7)", (uint)(7 << FloorTypeShift), 0),
    };

    /// <summary>The preset whose words match (d0,d1), or -1 ("Custom" / raw).</summary>
    public static int IndexOf(uint d0, uint d1)
    {
        for (int i = 0; i < All.Length; i++) if (All[i].Data0 == d0 && All[i].Data1 == d1) return i;
        return -1;
    }
}
