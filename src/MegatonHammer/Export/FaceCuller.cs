using MegatonHammer.Editor;
using OpenTK.Mathematics;

namespace MegatonHammer.Export;

/// <summary>
/// Compile-time "cull unseen faces": decides whether a brush render face is entirely BURIED against a
/// neighbouring brush and can be dropped from the exported display list — the same idea as Hammer/vbsp
/// only emitting faces that border empty space, not solid (and like a nodraw texture: render is skipped,
/// collision is untouched). Conservative by construction: a face is culled ONLY when the area just in
/// front of it is inside another solid AND the whole face polygon lies within that solid, so a face that
/// is even partly visible is never removed (no holes). Off by default (<see cref="EditorSettings.CullUnseenFaces"/>).
/// </summary>
public static class FaceCuller
{
    // How far in front of a face (along its normal) we probe for solid: small, so even a thin neighbour
    // brush still registers, but clear of floating-point slop at the shared plane.
    private const float FrontProbe = 0.5f;
    // The probe must be at least this far inside the neighbour to count as "buried" (clears plane slop).
    private const float InsideMargin = 0.1f;
    // A face vertex up to this far OUTSIDE the neighbour still counts as covered (verts sitting exactly on
    // the shared face plane read as ~0; abutting brushes are grid-aligned so real overhangs are >> this).
    private const float CoverSlop = 0.5f;

    /// <summary>True if <paramref name="face"/> (of <paramref name="owner"/>) is fully hidden behind one of
    /// the other solids — safe to skip in the render display list.</summary>
    public static bool IsObscured(SolidFace face, Solid owner, IReadOnlyList<Solid> solids)
    {
        var verts = face.Vertices;
        if (verts.Count < 3) return false;

        Vector3 centre = Centroid(verts);
        Vector3 front = centre + face.Plane.Normal * FrontProbe;   // a point just off the visible side
        var (fmin, fmax) = FaceBounds(verts);

        foreach (var other in solids)
        {
            if (ReferenceEquals(other, owner)) continue;
            var (omin, omax) = other.GetAABB();
            // Cheap reject: the face can't be inside a solid it doesn't even overlap (pad by the slop).
            if (fmin.X > omax.X + CoverSlop || fmax.X < omin.X - CoverSlop ||
                fmin.Y > omax.Y + CoverSlop || fmax.Y < omin.Y - CoverSlop ||
                fmin.Z > omax.Z + CoverSlop || fmax.Z < omin.Z - CoverSlop) continue;

            // The visible side must be inside this solid (so the face points INTO it, not away — an
            // outward-facing face coincident with the solid's surface is NOT buried).
            if (!Inside(front, other, -InsideMargin)) continue;
            // ...and the whole face must be within the solid (every vertex inside, with surface slop).
            bool all = true;
            foreach (var v in verts) if (!Inside(v, other, CoverSlop)) { all = false; break; }
            if (all) return true;
        }
        return false;
    }

    // A point is inside a convex brush when it's on the inner (negative) side of every face plane. The
    // margin shifts the boundary: negative = must be strictly inside; positive = on-or-near the surface ok.
    private static bool Inside(Vector3 p, Solid solid, float margin)
    {
        foreach (var g in solid.Faces)
            if (Vector3.Dot(g.Plane.Normal, p) - g.Plane.Distance > margin) return false;
        return true;
    }

    private static Vector3 Centroid(IReadOnlyList<Vector3> v)
    {
        Vector3 s = Vector3.Zero;
        foreach (var p in v) s += p;
        return s / v.Count;
    }

    private static (Vector3 min, Vector3 max) FaceBounds(IReadOnlyList<Vector3> v)
    {
        Vector3 mn = v[0], mx = v[0];
        foreach (var p in v) { mn = Vector3.ComponentMin(mn, p); mx = Vector3.ComponentMax(mx, p); }
        return (mn, mx);
    }
}
