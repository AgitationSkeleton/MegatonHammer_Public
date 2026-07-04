using System.Drawing;
using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

/// <summary>
/// Imported Wavefront-OBJ mesh geometry brought into a room as first-class, exportable level geometry
/// (textured triangles with per-vertex UVs and a material), distinct from the editor's brush solids.
/// Carries the SharpOcarina/Blender OBJ group conventions (#nocollision / #nomesh / #door) so the
/// exporter can honour them. A Blender or SharpOcarina scene OBJ becomes a playable textured level.
/// </summary>
public sealed class ObjMesh
{
    public struct Tri
    {
        public Vector3 P0, P1, P2;
        public Vector2 UV0, UV1, UV2;   // 0..1 texture coords (V already OpenGL-oriented)
        public string Material;
        public bool NoCollision;        // group tagged #nocollision — drawn but not solid
        public bool NoMesh;             // group tagged #nomesh — collision only, not drawn
        public bool Door;               // group tagged #door / tag_door — a door placement hint
    }

    public List<Tri> Tris { get; } = [];

    /// <summary>Material name → its decoded texture bitmap (from the .mtl map_Kd), or null if untextured.</summary>
    public Dictionary<string, Bitmap?> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Tris.Count == 0;

    /// <summary>World-space AABB of the mesh, for camera framing.</summary>
    public (Vector3 min, Vector3 max) Bounds()
    {
        if (Tris.Count == 0) return (Vector3.Zero, Vector3.Zero);
        Vector3 mn = new(1e9f), mx = new(-1e9f);
        foreach (var t in Tris)
            foreach (var p in new[] { t.P0, t.P1, t.P2 })
            { mn = Vector3.ComponentMin(mn, p); mx = Vector3.ComponentMax(mx, p); }
        return (mn, mx);
    }
}
