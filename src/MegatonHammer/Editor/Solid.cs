using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

/// <summary>How a brush's visible geometry is blended, mapped to a vanilla N64/Fast3D render mode.</summary>
public enum BrushBlend
{
    /// <summary>Normal solid geometry (poly_opa).</summary>
    Opaque = 0,
    /// <summary>Alpha-blended (poly_xlu, G_RM_AA_ZB_XLU_SURF) — glass, water, ghosts, coloured haze.</summary>
    Translucent = 1,
    /// <summary>Additive (poly_xlu, blender B = G_BL_1) — light shafts/god rays, glow, fire, energy.</summary>
    Additive = 2,
}

public sealed class Solid
{
    public List<Plane3D>   Planes     { get; } = [];
    public List<SolidFace> Faces      { get; private set; } = [];
    public bool            IsSelected { get; set; }

    /// <summary>Hammer group id (0 = ungrouped). Clicking any member selects the whole group.</summary>
    public int GroupId    { get; set; }
    /// <summary>Hammer visgroup id (0 = none/always-visible). Hidden when its visgroup is toggled off.</summary>
    public int VisGroupId { get; set; }

    /// <summary>When true this brush is an exit-trigger volume (a warp zone), not visible geometry:
    /// drawn as a translucent marker, and on export its collision polygons carry the exit index.</summary>
    public bool IsTrigger { get; set; }

    /// <summary>For a trigger brush: the destination entrance index (gEntranceTable) to warp to,
    /// or -1 for "void out". Mapped to/from the scene's exit list on import/export.</summary>
    public int ExitEntrance { get; set; } = -1;

    /// <summary>When true this brush is a water volume (Hammer func_water): not solid collision —
    /// exported as a collision-header WaterBox (its top face is the water surface), not as polygons.</summary>
    public bool IsWater { get; set; }

    /// <summary>The room a water box belongs to (0x3F = all rooms). Stored in the WaterBox properties.</summary>
    public int WaterRoom { get; set; } = 0x3F;

    /// <summary>Vanilla render technique for this brush's visible geometry. Opaque = the normal poly_opa path.
    /// Translucent/Additive route the brush's faces into the room's poly_XLU display list with the matching
    /// N64 render mode (alpha-blend for Translucent — glass/water/ghosts; additive for Additive — light
    /// shafts/glow/fire), so it commits to OoT/MM on N64 AND SoH/2Ship. <see cref="Opacity"/> is baked into
    /// the vertex alpha. Compatible with texture scroll for effects like the Chamber of Sages water.</summary>
    public BrushBlend Blend { get; set; } = BrushBlend.Opaque;

    /// <summary>Alpha 0 (invisible) … 255 (opaque) for a Translucent/Additive brush; ignored when Opaque.</summary>
    public byte Opacity { get; set; } = 255;

    /// <summary>True when this brush draws through the translucent (poly_xlu) path rather than poly_opa.</summary>
    public bool IsXlu => Blend != BrushBlend.Opaque;

    /// <summary>Solidity: when true this brush is NON-SOLID — it still renders (visible geometry) but emits
    /// NO collision polygons, so the player/actors pass through it (Hammer's "non-solid" / a fake wall you
    /// can walk through). Vanilla-faithful: the collision builders simply skip its faces. Default solid.</summary>
    public bool NoCollision { get; set; }

    /// <summary>OoT/MM collision SurfaceType words for this brush's polygons (data[0], data[1]) — the
    /// floor type/property (void, damage…), wall type (climb/ledge), conveyor, sound, camera, etc.
    /// Default 0 = plain floor/wall. Authored via the brush's Surface Type property; exported into the
    /// collision surface-type table so the brush actually behaves as that surface in-game. (Exit-trigger
    /// brushes additionally OR their exit index into data[0] bits 8-12.)</summary>
    public uint SurfaceData0 { get; set; }
    public uint SurfaceData1 { get; set; }

    public static Solid CreateBox(Vector3 min, Vector3 max)
    {
        var s = new Solid();
        s.Planes.AddRange([
            new(new Vector3( 1,  0,  0),  max.X),
            new(new Vector3(-1,  0,  0), -min.X),
            new(new Vector3( 0,  1,  0),  max.Y),
            new(new Vector3( 0, -1,  0), -min.Y),
            new(new Vector3( 0,  0,  1),  max.Z),
            new(new Vector3( 0,  0, -1), -min.Z),
        ]);
        s.ComputeFaces();
        return s;
    }

    public void ComputeFaces()
    {
        // Snapshot existing per-plane attributes so the full texture mapping (name, scale, shift,
        // rotation, alignment) and colour survive a recompute (a transform calls ComputeFaces — without
        // this the face's texture shift/rotation would reset to 0 on every move/scale/rotate).
        // Keyed by the plane index that made the face.
        var carry = new Dictionary<int, SolidFace>();
        foreach (var f in Faces)
            if (f.PlaneIndex >= 0)
                carry[f.PlaneIndex] = f;

        Faces.Clear();
        var allVerts = ComputeVertices();

        for (int pi = 0; pi < Planes.Count; pi++)
        {
            var plane = Planes[pi];
            var fv = allVerts.Where(v => MathF.Abs(plane.Evaluate(v)) < 0.5f).ToList();
            if (fv.Count < 3) continue;
            SortFaceVerts(plane, fv);
            var face = new SolidFace(plane, fv) { PlaneIndex = pi };
            if (carry.TryGetValue(pi, out var o))
            {
                face.TextureName = o.TextureName;
                face.TexScaleS = o.TexScaleS; face.TexScaleT = o.TexScaleT;
                face.TexShiftS = o.TexShiftS; face.TexShiftT = o.TexShiftT;
                face.TexRotation = o.TexRotation; face.AlignToFace = o.AlignToFace;
                face.UAxis = o.UAxis; face.VAxis = o.VAxis;   // carry explicit texture axes (texture lock)
                face.Color = o.Color;
                // Carry the spray/shade paint too, else any recompute (transform, a covering brush, an export
                // rebuild during playtest) silently wipes it. But ONLY when the face keeps its vertex count: a
                // CLIP (Solid.Split) reshapes the crossed faces (a quad becomes a tri/pentagon), and a stale
                // grid/VertexColors sized for the old quad no longer matches the new geometry — carrying it
                // caused the slice of a painted brush to render wrong / fail. On a plain transform the count is
                // unchanged, so paint survives as before; on a clip the trimmed face just starts unpainted.
                if (o.Vertices.Count == face.Vertices.Count)
                {
                    face.ShadePaint = o.ShadePaint;
                    face.VertexColors = o.VertexColors;
                }
            }
            Faces.Add(face);
        }
    }

    private List<Vector3> ComputeVertices()
    {
        var results = new List<Vector3>();
        int n = Planes.Count;
        for (int i = 0; i < n - 2; i++)
        for (int j = i + 1; j < n - 1; j++)
        for (int k = j + 1; k < n; k++)
        {
            if (!Intersect3Planes(Planes[i], Planes[j], Planes[k], out var pt)) continue;
            if (Planes.All(p => p.Contains(pt)))
                results.Add(pt);
        }
        // Deduplicate within tolerance
        var unique = new List<Vector3>();
        foreach (var v in results)
            if (!unique.Any(u => (u - v).LengthSquared < 0.25f))
                unique.Add(v);
        return unique;
    }

    private static bool Intersect3Planes(Plane3D p1, Plane3D p2, Plane3D p3, out Vector3 pt)
    {
        var n1 = p1.Normal; var n2 = p2.Normal; var n3 = p3.Normal;
        float denom = Vector3.Dot(n1, Vector3.Cross(n2, n3));
        if (MathF.Abs(denom) < 1e-6f) { pt = default; return false; }
        pt = (p1.Distance * Vector3.Cross(n2, n3)
            + p2.Distance * Vector3.Cross(n3, n1)
            + p3.Distance * Vector3.Cross(n1, n2)) / denom;
        return true;
    }

    private static void SortFaceVerts(Plane3D plane, List<Vector3> verts)
    {
        if (verts.Count < 3) return;
        var center = verts.Aggregate(Vector3.Zero, (a, b) => a + b) / verts.Count;
        var ref0   = Vector3.Normalize(verts[0] - center);
        var ref1   = Vector3.Normalize(Vector3.Cross(ref0, plane.Normal));
        verts.Sort((a, b) =>
        {
            var da = a - center; var db = b - center;
            float angA = MathF.Atan2(Vector3.Dot(da, ref1), Vector3.Dot(da, ref0));
            float angB = MathF.Atan2(Vector3.Dot(db, ref1), Vector3.Dot(db, ref0));
            return angA.CompareTo(angB);
        });
    }

    public (Vector3 min, Vector3 max) GetAABB()
    {
        if (Faces.Count == 0) return (Vector3.Zero, Vector3.Zero);
        var vs = Faces.SelectMany(f => f.Vertices).ToList();
        return (
            new Vector3(vs.Min(v => v.X), vs.Min(v => v.Y), vs.Min(v => v.Z)),
            new Vector3(vs.Max(v => v.X), vs.Max(v => v.Y), vs.Max(v => v.Z)));
    }

    /// <summary>
    /// Non-uniformly scales the solid about <paramref name="pivot"/> by per-axis
    /// factors in <paramref name="scale"/>. Each clip plane is transformed so the
    /// result remains a valid convex solid, then faces are recomputed.
    /// A scale factor of 1 on an axis leaves that axis unchanged.
    /// </summary>
    public void ScaleAbout(Vector3 pivot, Vector3 scale)
    {
        if (scale.X == 0f || scale.Y == 0f || scale.Z == 0f) return;

        // Plane3D is a struct stored in a List, so rewrite each entry by index.
        for (int i = 0; i < Planes.Count; i++)
        {
            var plane = Planes[i];
            var n = plane.Normal;
            // A point x on the plane satisfies n·x = d. Under x' = pivot + S(x-pivot),
            // the transformed plane has normal n' = S^-1 n (S diagonal) and passes
            // through the transformed points. Distance is re-derived from a point on
            // the plane (the closest point to origin: d*n).
            var nPrime = new Vector3(n.X / scale.X, n.Y / scale.Y, n.Z / scale.Z);
            float len = nPrime.Length;
            if (len < 1e-9f) continue;
            nPrime /= len;

            var pointOnPlane = n * plane.Distance;                 // n·p = d, p = d·n
            var moved = pivot + Mul(scale, pointOnPlane - pivot);  // transform that point
            plane.Normal   = nPrime;
            plane.Distance = Vector3.Dot(nPrime, moved);
            Planes[i] = plane;
        }

        ComputeFaces();
    }

    private static Vector3 Mul(Vector3 a, Vector3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

    /// <summary>
    /// Applies an affine transform (rotate / shear) about <paramref name="pivot"/> to the planes
    /// captured in <paramref name="snapshot"/>, re-deriving each plane from its baseline so there's
    /// no cumulative drift. <paramref name="mapPoint"/> is the linear map M applied to a point;
    /// <paramref name="mapNormalInvT"/> is (M⁻¹)ᵀ applied to a normal (equal to M for a rotation).
    /// Transforming planes (not vertices) keeps the face structure, so textures are preserved.
    /// </summary>
    public void TransformAbout(Plane3D[] snapshot, Func<Vector3, Vector3> mapPoint,
                               Func<Vector3, Vector3> mapNormalInvT, Vector3 pivot)
    {
        Planes.Clear();
        foreach (var pl in snapshot)
        {
            var nPrime = mapNormalInvT(pl.Normal);
            float len = nPrime.Length;
            if (len < 1e-9f) { Planes.Add(pl); continue; }
            nPrime /= len;
            var p0      = pl.Normal * pl.Distance;            // a point on the original plane
            var p0Prime = mapPoint(p0 - pivot) + pivot;       // transformed about the pivot
            Planes.Add(new Plane3D(nPrime, Vector3.Dot(nPrime, p0Prime)));
        }
        ComputeFaces();
    }

    /// <summary>Translates the solid by <paramref name="delta"/> (shifts every clip plane).</summary>
    /// <summary>Hammer "Texture Lock": when true (default), a brush's texture moves with it during a
    /// translate (UVs stay pinned to the surface); when false the texture stays fixed in world space.</summary>
    public static bool TextureLock = true;

    public void Translate(Vector3 delta)
    {
        for (int i = 0; i < Planes.Count; i++)
        {
            var p = Planes[i];
            p.Distance += Vector3.Dot(p.Normal, delta);
            Planes[i] = p;
        }
        ComputeFaces();
        if (TextureLock)
            foreach (var f in Faces)
            {
                var (u, v) = f.TextureAxes();
                float sS = MathF.Abs(f.TexScaleS) < 1e-3f ? 64f : f.TexScaleS;
                float sT = MathF.Abs(f.TexScaleT) < 1e-3f ? 64f : f.TexScaleT;
                f.TexShiftS -= Vector3.Dot(delta, u) / sS;
                f.TexShiftT -= Vector3.Dot(delta, v) / sT;
            }
    }

    /// <summary>Rotates the brush about <paramref name="pivot"/> by Euler degrees (X then Y then Z),
    /// for the Transform dialog. Each clip plane's normal is rotated and its distance adjusted so the
    /// plane passes through the rotated geometry.</summary>
    public void Rotate(Vector3 pivot, Vector3 degXYZ)
    {
        const float d2r = MathF.PI / 180f;
        float x = degXYZ.X * d2r, y = degXYZ.Y * d2r, z = degXYZ.Z * d2r;
        float sx = MathF.Sin(x), cx = MathF.Cos(x), sy = MathF.Sin(y), cy = MathF.Cos(y), sz = MathF.Sin(z), cz = MathF.Cos(z);
        var c0 = new Vector3(cz * cy, sz * cy, -sy);
        var c1 = new Vector3(cz * sy * sx - sz * cx, sz * sy * sx + cz * cx, cy * sx);
        var c2 = new Vector3(cz * sy * cx + sz * sx, sz * sy * cx - cz * sx, cy * cx);
        for (int i = 0; i < Planes.Count; i++)
        {
            var p = Planes[i];
            var nn = p.Normal.X * c0 + p.Normal.Y * c1 + p.Normal.Z * c2;
            p.Distance = p.Distance - Vector3.Dot(p.Normal, pivot) + Vector3.Dot(nn, pivot);
            p.Normal = nn;
            Planes[i] = p;
        }
        ComputeFaces();
    }

    /// <summary>Mirrors the brush across <paramref name="center"/> on one axis (0=X,1=Y,2=Z),
    /// matching Hammer's Flip. Reflecting each clip plane (negate the axis normal component and
    /// adjust its distance) keeps the solid convex with correct outward winding.</summary>
    public void Flip(int axis, float center)
    {
        for (int i = 0; i < Planes.Count; i++)
        {
            var p = Planes[i];
            float na = axis == 0 ? p.Normal.X : axis == 1 ? p.Normal.Y : p.Normal.Z;
            var n = p.Normal;
            if (axis == 0) n.X = -n.X; else if (axis == 1) n.Y = -n.Y; else n.Z = -n.Z;
            p.Normal = n;
            p.Distance -= 2f * center * na;
            Planes[i] = p;
        }
        ComputeFaces();
        // Texture lock: mirror each face's texture axes on the flipped world axis AND compensate the shift
        // for the mirror-about-centre, so the texture flips welded to the geometry instead of sliding off
        // (mirroring the axes alone left it offset by a fractional tile — the visible "flip breaks textures").
        if (TextureLock)
            foreach (var f in Faces) f.FlipTextureLock(axis, center);
    }

    /// <summary>Captures the current clip planes so a transform can be re-applied from
    /// a clean baseline each frame (avoids cumulative float drift during a drag).</summary>
    public Plane3D[] SnapshotPlanes() => [.. Planes];

    /// <summary>Restores planes captured by <see cref="SnapshotPlanes"/> and recomputes faces.</summary>
    public void RestorePlanes(Plane3D[] snapshot)
    {
        Planes.Clear();
        Planes.AddRange(snapshot);
        ComputeFaces();
    }

    /// <summary>Deep-copies this solid, preserving per-face texture/colour attributes.</summary>
    public Solid Clone()
    {
        var s = new Solid { IsSelected = IsSelected, IsTrigger = IsTrigger, ExitEntrance = ExitEntrance, IsWater = IsWater, WaterRoom = WaterRoom, NoCollision = NoCollision, SurfaceData0 = SurfaceData0, SurfaceData1 = SurfaceData1, GroupId = GroupId, VisGroupId = VisGroupId, Blend = Blend, Opacity = Opacity };
        s.Planes.AddRange(Planes);
        s.ComputeFaces();
        foreach (var f in s.Faces)
        {
            var src = Faces.FirstOrDefault(x => x.PlaneIndex == f.PlaneIndex);
            if (src == null) continue;
            // Carry the FULL Face-Edit texture mapping (was only name/scale/colour — so a pasted brush lost its
            // exact offsets, per-axis scale, rotation, alignment and explicit texture axes, re-projecting to
            // defaults). Copy everything so a clone/paste is texture-identical to its source.
            f.TextureName = src.TextureName;
            f.TexScaleS = src.TexScaleS; f.TexScaleT = src.TexScaleT;
            f.TexShiftS = src.TexShiftS; f.TexShiftT = src.TexShiftT;
            f.TexRotation = src.TexRotation; f.AlignToFace = src.AlignToFace;
            f.UAxis = src.UAxis; f.VAxis = src.VAxis;
            f.Color = src.Color;
            f.VertexColors = src.VertexColors is { } vc && vc.Length == f.Vertices.Count ? (Vector3[])vc.Clone() : null;
            f.ShadePaint = src.ShadePaint is { } sp
                ? new SolidFace.ShadeGrid { Nu = sp.Nu, Nv = sp.Nv, Colors = (Vector3[])sp.Colors.Clone() }
                : null;
        }
        return s;
    }

    /// <summary>
    /// Splits the solid by an infinite plane into front/back halves. Either may be null
    /// if that side is empty. The cut plane keeps the half-space where Evaluate ≤ 0.
    /// </summary>
    public (Solid? front, Solid? back) Split(Plane3D cut)
    {
        var front = Clone();
        int fi = front.Planes.Count;   // index the cut plane takes on the front half
        front.Planes.Add(cut);
        front.ComputeFaces();
        front.InheritCutFaceTexture(fi);

        var back = Clone();
        int bi = back.Planes.Count;
        back.Planes.Add(new Plane3D(-cut.Normal, -cut.Distance));
        back.ComputeFaces();
        back.InheritCutFaceTexture(bi);

        return (IsValidSolid(front) ? front : null, IsValidSolid(back) ? back : null);
    }

    /// <summary>After a clip, the freshly exposed cut face has no carried texture (its plane didn't exist
    /// before) and would render blank. Hammer textures the new face from the brush; we inherit from the
    /// existing textured face most parallel to the cut (its planar mapping matches best) so the slice keeps
    /// the brush's material instead of going blank. The mapping scalars carry; the axes are re-derived from
    /// the new face's own normal so the projection stays clean.</summary>
    private void InheritCutFaceTexture(int cutPlaneIndex)
    {
        var cutFace = Faces.FirstOrDefault(f => f.PlaneIndex == cutPlaneIndex);
        if (cutFace == null || !string.IsNullOrEmpty(cutFace.TextureName)) return;

        SolidFace? donor = null; float best = -1f;
        foreach (var f in Faces)
        {
            if (ReferenceEquals(f, cutFace) || string.IsNullOrEmpty(f.TextureName)) continue;
            float d = MathF.Abs(Vector3.Dot(f.Plane.Normal, cutFace.Plane.Normal));   // most parallel/anti-parallel
            if (d > best) { best = d; donor = f; }
        }
        if (donor == null) return;   // brush had no textured face — nothing to inherit

        cutFace.TextureName = donor.TextureName;
        cutFace.TexScaleS = donor.TexScaleS; cutFace.TexScaleT = donor.TexScaleT;
        cutFace.TexShiftS = donor.TexShiftS; cutFace.TexShiftT = donor.TexShiftT;
        cutFace.TexRotation = donor.TexRotation; cutFace.AlignToFace = donor.AlignToFace;
        cutFace.Color = donor.Color;
        cutFace.ResetAxes();   // derive axes from THIS face's normal, not the donor's world axes
    }

    /// <summary>Rebuilds the solid as the convex hull of <paramref name="points"/>.</summary>
    public void RebuildFromPoints(IReadOnlyList<Vector3> points)
    {
        var hull = ConvexHullPlanes(points);
        if (hull.Count < 4) return;     // degenerate; keep current geometry
        Planes.Clear();
        Planes.AddRange(hull);
        ComputeFaces();
    }

    public List<Vector3> GetUniqueVertices()
    {
        var result = new List<Vector3>();
        foreach (var v in Faces.SelectMany(f => f.Vertices))
            if (!result.Any(u => (u - v).LengthSquared < 0.25f))
                result.Add(v);
        return result;
    }

    // Brute-force convex hull (n is small): a triangle of points is a hull face when all
    // other points lie on one side. Normals are oriented outward (inside is Evaluate ≤ 0).
    private static List<Plane3D> ConvexHullPlanes(IReadOnlyList<Vector3> pts)
    {
        var planes = new List<Plane3D>();
        int n = pts.Count;
        for (int i = 0; i < n - 2; i++)
        for (int j = i + 1; j < n - 1; j++)
        for (int k = j + 1; k < n; k++)
        {
            var nrm = Vector3.Cross(pts[j] - pts[i], pts[k] - pts[i]);
            if (nrm.LengthSquared < 1e-6f) continue;
            nrm = Vector3.Normalize(nrm);
            float d = Vector3.Dot(nrm, pts[i]);

            int pos = 0, neg = 0;
            for (int m = 0; m < n; m++)
            {
                float e = Vector3.Dot(nrm, pts[m]) - d;
                if (e > 1e-3f) pos++; else if (e < -1e-3f) neg++;
            }
            if (pos > 0 && neg > 0) continue;          // not a supporting plane

            if (pos > 0) { nrm = -nrm; d = -d; }       // orient so interior is Evaluate ≤ 0
            if (!planes.Any(p => Vector3.Dot(p.Normal, nrm) > 0.9995f && MathF.Abs(p.Distance - d) < 0.5f))
                planes.Add(new Plane3D(nrm, d));
        }
        return planes;
    }

    private static bool IsValidSolid(Solid s)
    {
        if (s.Faces.Count < 4) return false;
        var (mn, mx) = s.GetAABB();
        return (mx.X - mn.X) > 0.5f && (mx.Y - mn.Y) > 0.5f && (mx.Z - mn.Z) > 0.5f;
    }
}
