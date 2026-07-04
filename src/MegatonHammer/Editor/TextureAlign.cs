using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

/// <summary>
/// Shared texture-mapping geometry used by the Face Edit sheet and the Texture tool's right-click
/// apply: folding one face's mapping across a shared edge so the texture is continuous around the
/// corner (Hammer "Align ▸ to adjacent"), and flood-filling the continuous coplanar surface a face
/// belongs to (so a multi-brush wall/floor can be selected and aligned as one).
/// </summary>
public static class TextureAlign
{
    private static float Sane(float s) => MathF.Abs(s) < 1e-3f ? 64f : s;

    /// <summary>
    /// Makes <paramref name="dst"/>'s texture mapping continuous with <paramref name="src"/>: copies
    /// src's scale/rotation/alignment, folds src's U/V axes about the shared edge into dst's plane, and
    /// offsets dst's shift so the UV matches src's at the shared edge (the texture "wraps the corner").
    /// Returns false (and inherits src's mapping verbatim) when the faces share no edge.
    /// </summary>
    public static bool TryAlignAcrossSeam(SolidFace src, SolidFace dst)
    {
        dst.AlignToFace = src.AlignToFace;
        dst.TexRotation = src.TexRotation;
        dst.TexScaleS = src.TexScaleS;
        dst.TexScaleT = src.TexScaleT;

        var (su, sv) = src.TextureAxes();
        float sS = Sane(src.TexScaleS), sT = Sane(src.TexScaleT);

        if (!SharedEdge(src, dst, out var anchor, out var edgeEnd))
        {
            // No shared edge → there's no seam to fold across. Reproduce the reference's CONFIG (scale —
            // already copied — plus rotation and shift) in the DESTINATION's OWN plane, so the texture
            // tiles correctly on it. Copying the source's world axes verbatim would leave them lying in the
            // SOURCE's plane; on a differently-angled face (e.g. applying a flat-wall face onto a sloped
            // beam) those out-of-plane axes shear/stretch the texture into streaks.
            dst.SetRotation(src.CurrentRotationDegrees());
            dst.TexShiftS = src.TexShiftS; dst.TexShiftT = src.TexShiftT;
            return false;
        }

        // Rotate src's axes about the shared edge by the dihedral angle (src plane → dst plane); the
        // edge lies in both planes so both normals are perpendicular to it.
        Vector3 e = Vector3.Normalize(edgeEnd - anchor);
        Vector3 ns = src.Plane.Normal, nd = dst.Plane.Normal;
        float ang = MathF.Atan2(Vector3.Dot(Vector3.Cross(ns, nd), e), Vector3.Dot(ns, nd));
        Vector3 ud = RotAbout(su, e, ang), vd = RotAbout(sv, e, ang);
        dst.SetAxes(ud, vd);

        // Offset so the UV at the shared anchor vertex matches src's UV there (seam continuity).
        float srcU = Vector3.Dot(anchor, su) / sS + src.TexShiftS;
        float srcV = Vector3.Dot(anchor, sv) / sT + src.TexShiftT;
        float dS = Sane(dst.TexScaleS), dT = Sane(dst.TexScaleT);
        dst.TexShiftS = srcU - Vector3.Dot(anchor, ud) / dS;
        dst.TexShiftT = srcV - Vector3.Dot(anchor, vd) / dT;
        return true;
    }

    /// <summary>
    /// Every face that belongs to the same continuous flat surface as <paramref name="seed"/> — coplanar
    /// (same plane, same facing) and reachable from it through shared edges across connected brushes.
    /// A vertical surface flood-fills horizontally (a wall built from several brushes); a horizontal one
    /// fills across the floor/ceiling. Includes the seed itself. When <paramref name="sameTextureOnly"/>
    /// is set, the fill only crosses into faces wearing the seed's texture (so one painted band of a
    /// multi-texture wall is grabbed on its own).
    /// </summary>
    public static List<SolidFace> CoplanarRun(IReadOnlyList<Solid> solids, SolidFace seed, bool sameTextureOnly = false)
    {
        var coplanar = new List<SolidFace>();
        foreach (var s in solids)
            foreach (var f in s.Faces)
                if (Vector3.Dot(f.Plane.Normal, seed.Plane.Normal) > 0.9995f &&
                    MathF.Abs(f.Plane.Distance - seed.Plane.Distance) < 0.6f &&
                    (!sameTextureOnly || ReferenceEquals(f, seed) ||
                     string.Equals(f.TextureName, seed.TextureName, StringComparison.OrdinalIgnoreCase)))
                    coplanar.Add(f);

        var result = new HashSet<SolidFace> { seed };
        var stack = new Stack<SolidFace>();
        stack.Push(seed);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            foreach (var f in coplanar)
                if (!result.Contains(f) && FacesTouch(cur, f))
                { result.Add(f); stack.Push(f); }
        }
        return result.ToList();
    }

    /// <summary>The adjacent face (sharing an edge with <paramref name="face"/>) whose texture is
    /// <paramref name="texture"/> — the neighbour to align a freshly-painted face against. Null if none.</summary>
    public static SolidFace? AdjacentWithTexture(IReadOnlyList<Solid> solids, SolidFace face, string? texture)
    {
        if (string.IsNullOrEmpty(texture)) return null;
        foreach (var s in solids)
            foreach (var f in s.Faces)
                if (!ReferenceEquals(f, face) &&
                    string.Equals(f.TextureName, texture, StringComparison.OrdinalIgnoreCase) &&
                    SharedEdge(face, f, out _, out _))
                    return f;
        return null;
    }

    // Two faces touch if any pair of their edges is collinear and overlaps — robust to brushes of
    // different sizes abutting (a big brush next to several small ones), not just vertex-coincident grids.
    private static bool FacesTouch(SolidFace a, SolidFace b)
    {
        int na = a.Vertices.Count, nb = b.Vertices.Count;
        for (int i = 0; i < na; i++)
        {
            Vector3 a0 = a.Vertices[i], a1 = a.Vertices[(i + 1) % na];
            for (int j = 0; j < nb; j++)
            {
                Vector3 b0 = b.Vertices[j], b1 = b.Vertices[(j + 1) % nb];
                if (EdgesOverlap(a0, a1, b0, b1)) return true;
            }
        }
        return false;
    }

    // True if segments [a0,a1] and [b0,b1] are collinear (same supporting line) and their 1-D extents
    // overlap by more than a point.
    private static bool EdgesOverlap(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1)
    {
        Vector3 da = a1 - a0;
        float len = da.Length;
        if (len < 1e-4f) return false;
        Vector3 dir = da / len;
        // b endpoints must lie on a's line.
        if (Vector3.Cross(dir, b0 - a0).LengthSquared > 0.5f) return false;
        if (Vector3.Cross(dir, b1 - a0).LengthSquared > 0.5f) return false;
        // Parametric extents along dir.
        float ta0 = 0f, ta1 = len;
        float tb0 = Vector3.Dot(b0 - a0, dir), tb1 = Vector3.Dot(b1 - a0, dir);
        float lo = MathF.Max(MathF.Min(ta0, ta1), MathF.Min(tb0, tb1));
        float hi = MathF.Min(MathF.Max(ta0, ta1), MathF.Max(tb0, tb1));
        return hi - lo > 0.5f;   // more than a touching corner
    }

    // Two vertices shared (within tolerance) by both faces → their common edge; anchor = one endpoint.
    private static bool SharedEdge(SolidFace a, SolidFace b, out Vector3 anchor, out Vector3 edgeEnd)
    {
        anchor = edgeEnd = default;
        var shared = new List<Vector3>();
        foreach (var va in a.Vertices)
            if (b.Vertices.Any(vb => (vb - va).LengthSquared < 0.25f) &&
                !shared.Any(s => (s - va).LengthSquared < 0.25f))
                shared.Add(va);
        if (shared.Count < 2) return false;
        anchor = shared[0]; edgeEnd = shared[1];
        return true;
    }

    private static Vector3 RotAbout(Vector3 v, Vector3 axis, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return v * c + Vector3.Cross(axis, v) * s + axis * Vector3.Dot(axis, v) * (1f - c);
    }
}
