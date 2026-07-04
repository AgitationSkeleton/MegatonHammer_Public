using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

/// <summary>How the 3D view renders this scene's sky.</summary>
public enum SkyMode
{
    None,    // enclosed interior / dungeon — no sky (e.g. Forest Temple interior)
    Day,     // normal blue overworld sky (e.g. Hyrule Field) — the default
    Cloudy,  // overcast grey sky
}

/// <summary>A brush-authored animated texture: the painted texture's name and its UV scroll (units/sec).</summary>
public sealed record TextureScroll(string Name, float U, float V);

/// <summary>A 0–255 RGB colour (kept separate from System.Drawing for the editor core).</summary>
public struct RgbColor
{
    public byte R, G, B;
    public RgbColor(byte r, byte g, byte b) { R = r; G = g; B = b; }
    public static RgbColor From(int r, int g, int b) => new((byte)r, (byte)g, (byte)b);
}

/// <summary>
/// Scene-wide configuration surfaced in the editor and consumed by SceneExporter.
/// Defaults mirror the previously hard-coded export values (indoor lighting).
/// </summary>
public sealed class SceneSettings
{
    // ── Identity ──────────────────────────────────────────────────────────
    /// <summary>Area name shown on the title card when entering (OoT: a 144x24 IA8 texture
    /// generated from this text; MM: on-screen message text). Empty = no title card. (D15)</summary>
    public string AreaName { get; set; } = "";

    // ── Skybox / lighting ─────────────────────────────────────────────────
    public byte SkyboxId       { get; set; } = 1;     // 1 = standard sky
    /// <summary>Scene-table draw config: selects the engine's per-frame scene animation (texture
    /// scroll / material animation / the Deku Tree death morph etc.). 0 = default.</summary>
    public byte DrawConfig     { get; set; }
    public bool IndoorLighting { get; set; } = true;  // use per-scene env lighting
    public bool Cloudy         { get; set; }
    /// <summary>Dungeon scene (vs overworld/field). Loads the dungeon keep object (gameplay_dangeon_keep,
    /// 0x0003) instead of field keep so dungeon-keep actors render — dungeon pots/Obj_Tsubo (whose default
    /// object is the dungeon keep), small keys, dungeon doors, etc. (#14, #11). Also marks the area as a
    /// dungeon for the pause minimap.</summary>
    public bool Dungeon        { get; set; }
    /// <summary>Door theme — which dungeon's door model En_Door / Door_Shutter render in the editor (and,
    /// for vanilla, in-game). 0 = default wooden; 1 Deku Tree, 2 Dodongo, 5 Fire, 6 Water, 7 Spirit,
    /// 8 Shadow, 10 Gerudo Training (see ActorModelResolver.DoorModel). Boss doors are detected by params.</summary>
    public byte DoorStyle      { get; set; }
    /// <summary>Boss door emblem (z_door_shutter.c D_809982D4): which temple's boss-door texture the
    /// SHUTTER_BOSS door shows. 0 Default, 1 Fire, 2 Water, 3 Shadow, 4 Ganon's Castle, 5 Forest, 6 Spirit.
    /// In OoT this is derived from the scene, so it's a scene-wide setting (all boss doors here match).</summary>
    public byte BossDoorTheme  { get; set; }

    /// <summary>Brush-authored animated textures: a painted texture's name → its UV scroll in units per
    /// second. Faces using that texture scroll in the 3D view, and (MM) export an AnimatedMaterial so they
    /// scroll in-game too. (OoT in-game scroll needs a draw-config function and isn't emitted yet.)</summary>
    public List<TextureScroll> TextureScrolls { get; set; } = [];
    /// <summary>3D-view sky rendering for this scene (blue day sky by default; None for interiors).</summary>
    public SkyMode Sky         { get; set; } = SkyMode.Day;

    // ── Audio ─────────────────────────────────────────────────────────────
    public byte MusicSeq       { get; set; }          // background sequence id
    public bool MusicCrossGame { get; set; }          // MusicSeq belongs to the OTHER game's audiobank
    public byte NightSfx       { get; set; }          // ambient night SFX id

    // ── MM playtest starting event flags (weekEventReg), set on boot by the 2Ship fork. Each value is
    //    PACK_WEEKEVENTREG_FLAG(byteIndex, bitMask) = (byteIndex<<8)|bitMask. Lets a scene start in a
    //    given world-state (e.g. a temple already cleared). MM only; never compiled into scene data. ──
    public List<int> StartWeekEvents { get; set; } = [];

    // ── MM weekEventReg cross-cycle persistence (decomp: sPersistentCycleWeekEventRegs in z_sram_NES.c).
    //    On a Song-of-Time cycle reset MM clears MOST weekEventReg flags; only those whose 2-bit slot in
    //    that table is non-zero survive. These flags are OR'd into the engine's own persistence table by
    //    the 2Ship fork so authored world-state (a sidequest step, a door opened) SURVIVES the reset.
    //    Each value is PACK_WEEKEVENTREG_FLAG(byteIndex,bitMask). MM only. ──
    public List<int> PersistentWeekEvents { get; set; } = [];

    // ── Playtest time-of-day (normalized gamestate). One u16 over a 24h day from midnight:
    //    0x0000 = midnight, 0x4000 = 6:00, 0x8000 = noon, 0xC000 = 18:00. The SAME convention drives
    //    OoT dayTime/skyboxTime (SoH, N64) AND MM save.time (2Ship), so every playtest engine starts at
    //    the identical time. Default noon = brightest/flattest light, best for inspecting geometry. ──
    public ushort PlaytestTimeOfDay { get; set; } = 0x8000;

    // ── Wind (scene cmd 0x05): direction + speed driving grass/particle drift ─
    public sbyte WindX     { get; set; }
    public sbyte WindY     { get; set; }
    public sbyte WindZ     { get; set; }
    public byte  WindSpeed { get; set; }

    // ── Collision subdivision (BG check grid) ─────────────────────────────
    public byte SubdivX        { get; set; } = 16;
    public byte SubdivY        { get; set; } = 4;
    public byte SubdivZ        { get; set; } = 16;

    // ── Spawn / entrance ──────────────────────────────────────────────────
    public Vector3 SpawnPos    { get; set; } = Vector3.Zero;
    public short   SpawnYaw    { get; set; }          // binary angle
    public int     SpawnRoom   { get; set; }          // room index Link spawns in

    // ── Environment (single lighting entry) ───────────────────────────────
    // Defaults match Hyrule Field's daytime lighting (env setting 9 from OoT's spot00), so a
    // from-blank scene looks like the overworld in-game: directional day light + the real fog far
    // (the old default fog-far of 0x03FF ≈ 1023u fogged the whole level to washed-out dark).
    public RgbColor Ambient    { get; set; } = RgbColor.From(95, 80, 80);
    public sbyte Light1DirX = 73,  Light1DirY = 73,  Light1DirZ = 73;
    public RgbColor Light1Col  { get; set; } = RgbColor.From(145, 145, 130);
    public sbyte Light2DirX = -73, Light2DirY = -73, Light2DirZ = -73;
    public RgbColor Light2Col  { get; set; } = RgbColor.From(40, 40, 80);
    public RgbColor FogColor   { get; set; } = RgbColor.From(95, 95, 85);
    public ushort FogNear      { get; set; } = 0x07DA;
    public ushort FogFar       { get; set; } = 0x3200;

    /// <summary>#9: the vertex shade the editor bakes onto a surface with the given world normal — the
    /// SAME environment-lighting formula the 3D view's shader uses (ambient + the two directional lights,
    /// no fog). The exporters bake this into room geometry so the in-game level matches the editor view:
    /// indoor scenes (low ambient, no sun) read DARK where unlit instead of fullbright white. Outdoor
    /// scenes keep their bright ambient + sun, so they stay lit. (OoT room DLs use vertex colours, not
    /// runtime normals, so the lighting MUST be baked here to appear in-game.)</summary>
    public Vector3 BakedShade(Vector3 normal)
    {
        Vector3 Rgb(RgbColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f);
        var n   = normal.LengthSquared > 1e-6f ? Vector3.Normalize(normal) : new Vector3(0, 1, 0);
        var l1d = Vector3.Normalize(new Vector3(Light1DirX, Light1DirY, Light1DirZ));
        var l2d = Vector3.Normalize(new Vector3(Light2DirX, Light2DirY, Light2DirZ));
        var light = Rgb(Ambient)
                  + Rgb(Light1Col) * MathF.Max(0f, Vector3.Dot(n, l1d))
                  + Rgb(Light2Col) * MathF.Max(0f, Vector3.Dot(n, l2d));
        return Vector3.Clamp(light, Vector3.Zero, Vector3.One);
    }

    /// <summary>Deep copy (all fields are value types or immutable strings) — used by scene setups.</summary>
    public SceneSettings Clone() => (SceneSettings)MemberwiseClone();

    /// <summary>
    /// Defaults for a brand-new scene in the given game. OoT keeps the property defaults above
    /// (Hyrule Field daytime). MM starts from Termina Field — the natural "blank overworld" reference:
    /// blue day sky, the Termina Field theme (sequence 0x02), and a bright outdoor daytime light rig.
    /// </summary>
    public static SceneSettings DefaultFor(bool mm)
    {
        var s = new SceneSettings();
        if (!mm) return s;

        // ── Termina Field (MM SPOT00) ──
        s.SkyboxId       = 1;            // standard overworld sky
        s.Sky            = SkyMode.Day;  // blue day sky
        s.Cloudy         = false;
        s.IndoorLighting = true;         // use the per-scene env rig below
        s.MusicSeq       = 0x02;         // NA_BGM_TERMINA_FIELD (MM SongNames.xml key 02)
        s.MusicCrossGame = false;
        // Bright outdoor daytime rig (close to OoT's field day light — both are sunlit open fields).
        s.Ambient   = RgbColor.From(100, 90, 80);
        s.Light1DirX = 73;  s.Light1DirY = 73;  s.Light1DirZ = 73;
        s.Light1Col = RgbColor.From(150, 150, 135);
        s.Light2DirX = -73; s.Light2DirY = -73; s.Light2DirZ = -73;
        s.Light2Col = RgbColor.From(45, 45, 80);
        s.FogColor  = RgbColor.From(100, 100, 90);
        s.FogNear   = 0x07D0;
        s.FogFar    = 0x3200;
        return s;
    }
}
