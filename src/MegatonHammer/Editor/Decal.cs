using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

/// <summary>
/// A Hammer-style decal / overlay: a small textured quad stuck onto a surface, placed as its own entity
/// (a position + a surface normal) and independently sized/rotated — NOT a whole-face retexture. It floats
/// a hair off the surface in the editor and is BAKED at compile time into a thin projected polygon on the
/// surface(s) beneath it. Held per-room alongside brushes and actors.
/// </summary>
public sealed class Decal
{
    /// <summary>Centre of the decal in world space (a hair off the surface it sits on).</summary>
    public Vector3 Position { get; set; }
    /// <summary>Outward normal of the surface the decal is stuck to (the direction it faces).</summary>
    public Vector3 Normal { get; set; } = Vector3.UnitY;
    /// <summary>Half-extents along the decal's local U/V axes, world units (so the quad is 2*Size across).</summary>
    public float SizeU { get; set; } = 48f;
    public float SizeV { get; set; } = 48f;
    /// <summary>Rotation of the decal about its normal, degrees.</summary>
    public float Rotation { get; set; }
    /// <summary>The library texture painted on the decal (null = unset / nothing to bake).</summary>
    public string? TextureName { get; set; }

    public bool IsSelected { get; set; }
    public int GroupId { get; set; }
    public int VisGroupId { get; set; }

    /// <summary>The decal's local U/V axes in world space (derived from the normal + rotation), for
    /// building its quad corners and for the 2D footprint. U is horizontal-ish, V the other in-plane axis.</summary>
    public (Vector3 u, Vector3 v) Axes()
    {
        var n = Normal.LengthSquared > 1e-6f ? Vector3.Normalize(Normal) : Vector3.UnitY;
        // Base in-plane axes: pick a reference not parallel to the normal.
        Vector3 up = MathF.Abs(n.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 u = Vector3.Normalize(Vector3.Cross(up, n));
        Vector3 v = Vector3.Normalize(Vector3.Cross(n, u));
        // Rotate the axes about the normal.
        float a = MathHelper.DegreesToRadians(Rotation);
        float c = MathF.Cos(a), s = MathF.Sin(a);
        return (u * c + v * s, -u * s + v * c);
    }

    /// <summary>The four world-space corners of the decal quad (BL, BR, TR, TL), floated slightly off
    /// the surface along the normal so it doesn't z-fight the wall it sits on.</summary>
    public Vector3[] Corners(float lift = 0.6f)
    {
        var (u, v) = Axes();
        var n = Normal.LengthSquared > 1e-6f ? Vector3.Normalize(Normal) : Vector3.UnitY;
        Vector3 c0 = Position + n * lift;
        Vector3 su = u * SizeU, sv = v * SizeV;
        return [c0 - su - sv, c0 + su - sv, c0 + su + sv, c0 - su + sv];
    }

    public Decal Clone() => new()
    {
        Position = Position, Normal = Normal, SizeU = SizeU, SizeV = SizeV, Rotation = Rotation,
        TextureName = TextureName, IsSelected = IsSelected, GroupId = GroupId, VisGroupId = VisGroupId,
    };
}
