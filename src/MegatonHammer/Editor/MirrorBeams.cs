using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

/// <summary>
/// The vanilla OoT/MM Mirror-Shield light beams (<c>ovl_Mir_Ray</c> <c>sMirRayData</c>). Mir_Ray's beam
/// geometry is COMPILED into the overlay and indexed by the actor's params — it ignores the actor's placed
/// position — so a custom level can only reuse one of these fixed-world-position beams, it can't invent a new
/// one. The editor uses this table to (a) name the beam slots in the Mir_Ray property dropdown and (b) draw
/// the beam volume as a viewport gizmo, so the author can build a standable floor inside a beam and aim a Sun
/// Switch at the reflection. Coordinates are identical on OoT and MM (MM's Mir_Ray kept OoT's Spirit Temple
/// table). Values verified against oot-master z_mir_ray.c sMirRayData[] (source, pool, radii, params byte).
/// </summary>
public static class MirrorBeams
{
    // ACTOR_MIR_RAY has a DIFFERENT id per game: 0x00B7 on OoT (0x0062 there is Bg_Menkuri_Eye!), 0x0062 on MM
    // (0x00B7 is unused there). Always resolve it with the document's game flag.
    public const ushort ActorIdOoT = 0x00B7, ActorIdMm = 0x0062;
    public static ushort ActorId(bool isOoT) => isOoT ? ActorIdOoT : ActorIdMm;
    public static bool Is(ushort id, bool isOoT) => id == ActorId(isOoT);

    /// <param name="Params">The sMirRayData params byte: bit0 adirectional, bit1 has beam collider,
    /// bit2 from-mirror, bit3 light-from-mirror.</param>
    public sealed record Beam(int Index, string Name, Vector3 Source, Vector3 Pool,
                              int SourceRad, int PoolRad, int Params)
    {
        /// <summary>A vertical sunbeam the PLAYER stands in and reflects (as opposed to a mirror-relay point):
        /// the slots worth building a puzzle around. True when it isn't a from-mirror beam (params bit2 clear)
        /// and has an actual span (source != pool).</summary>
        public bool PlayerReflectable => (Params & 4) == 0 && Source != Pool;
    }

    // From z_mir_ray.c sMirRayData[] (the hex literals in MM decode to these same decimals).
    public static readonly IReadOnlyList<Beam> All =
    [
        new(0, "Spirit — Bombchu room downlight",        new(-1160,  686,  -880), new( -920,  480,  -889), 30,  50, 0x02),
        new(1, "Spirit — Sun-block room downlight",      new(-1856, 1092,  -190), new(-1703,  841,  -186), 30,  70, 0x02),
        new(2, "Spirit — Single-Cobra room downlight",   new( 1367,  738,  -860), new( 1091,  476,  -860), 30,  85, 0x00),
        new(3, "Spirit — Armos room downlight",          new( 2200, 1103,  -220), new( 2040,  843,  -220), 30,  60, 0x01),
        new(4, "Spirit — Top room downlight",            new( -560, 2169,  -310), new( -560, 1743,  -310), 30,  70, 0x00),
        new(5, "Spirit — Top room ceiling mirror",       new(   60, 1802, -1090), new(   60,  973, -1090), 30,  70, 0x0D),
        new(6, "Spirit — Single-Cobra relay",            new( 1140,  480,  -860), new( 1140,  480,  -860), 30,  30, 0x0E),
        new(7, "Spirit — Top room cobra 1",              new( -560, 1743,  -310), new( -560, 1743,  -310), 30,  30, 0x0C),
        new(8, "Spirit — Top room cobra 2",              new(   60, 1743,  -310), new(   60, 1743,  -310), 30,  30, 0x0C),
        new(9, "Ganon's Castle — Spirit Trial downlight",new(-1174,  448,  1194), new(-1174,  148,  1194), 50, 100, 0x03),
    ];

    public static string[] Names { get; } = All.Select(b => b.Name).ToArray();

    public static Beam? TryGet(int index) => index >= 0 && index < All.Count ? All[index] : null;
}
