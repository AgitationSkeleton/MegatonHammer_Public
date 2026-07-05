using MegatonHammer.Rendering;
using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

/// <summary>A world-space ray with origin and (unit) direction.</summary>
public readonly record struct Ray(Vector3 Origin, Vector3 Direction);

/// <summary>Result of picking a face in the 3D view.</summary>
public readonly record struct FaceHit(Solid Solid, SolidFace Face, Vector3 Point, float Distance);

/// <summary>
/// Screen-to-world raycasting helpers shared by the 3D entity-placement and
/// face-painting tools.
/// </summary>
public static class Picking
{
    /// <summary>
    /// Builds a world-space ray from a 3D viewport pixel by unprojecting the near
    /// and far plane points through the inverse view-projection matrix.
    /// </summary>
    public static Ray RayFromScreen(Camera3D cam, int sx, int sy, int w, int h)
    {
        if (w <= 0 || h <= 0) return new Ray(cam.Position, -Vector3.UnitZ);

        // Normalised device coordinates (NDC): x,y in [-1,1]; y flipped (screen-down → up)
        float ndcX = 2f * sx / w - 1f;
        float ndcY = 1f - 2f * sy / h;

        var vp     = cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h);
        var invVp  = Matrix4.Invert(vp);

        var nearH = new Vector4(ndcX, ndcY, -1f, 1f) * invVp;
        var farH  = new Vector4(ndcX, ndcY,  1f, 1f) * invVp;

        if (MathF.Abs(nearH.W) < 1e-9f || MathF.Abs(farH.W) < 1e-9f)
            return new Ray(cam.Position, -Vector3.UnitZ);

        var nearP = nearH.Xyz / nearH.W;
        var farP  = farH.Xyz  / farH.W;
        var dir   = Vector3.Normalize(farP - nearP);
        return new Ray(nearP, dir);
    }

    /// <summary>
    /// Picks the closest solid face hit by the ray across all rooms in the scene.
    /// Returns false if nothing is hit.
    /// </summary>
    /// <summary>Ray-picks the nearest decal quad (sticker overlay) under the ray, so decals can be
    /// clicked/selected in the 3D view like a brush. Tests both triangles of each decal's world quad.</summary>
    public static bool PickDecal(IEnumerable<Decal> decals, Ray ray, out Decal hit, out Vector3 point, out float dist)
    {
        hit = null!; point = default; dist = float.MaxValue; bool found = false;
        foreach (var d in decals)
        {
            var c = d.Corners();   // BL, BR, TR, TL
            if ((RayTriangle(ray, c[0], c[1], c[2], out float t) || RayTriangle(ray, c[0], c[2], c[3], out t)) && t < dist)
            { dist = t; hit = d; point = ray.Origin + ray.Direction * t; found = true; }
        }
        return found;
    }

    public static bool PickFace(ZScene scene, Ray ray, out FaceHit hit)
    {
        hit = default;
        bool found = false;
        float best = float.MaxValue;

        foreach (var room in scene.Rooms)
        foreach (var solid in room.Geometry)
        foreach (var face in solid.Faces)
        {
            var v = face.Vertices;
            if (v.Count < 3) continue;
            for (int i = 1; i < v.Count - 1; i++)
            {
                if (RayTriangle(ray, v[0], v[i], v[i + 1], out float t) && t < best)
                {
                    best  = t;
                    hit   = new FaceHit(solid, face, ray.Origin + ray.Direction * t, t);
                    found = true;
                }
            }
        }
        return found;
    }

    /// <summary>
    /// Nearest actor within a small angular cone of the ray, or null. The cone widens with
    /// distance (a constant screen-space click radius). Used when no model resolver is available.
    /// </summary>
    public static ZActor? PickActor(IEnumerable<ZActor> actors, Ray ray)
    {
        ZActor? best = null;
        float bestT = float.MaxValue;
        foreach (var a in actors)
        {
            var to = a.Position - ray.Origin;
            float t = Vector3.Dot(to, ray.Direction);
            if (t < 0f) continue;                          // behind the camera
            float perp = (to - ray.Direction * t).Length;
            float gate = 0.05f * t + 10f;                  // widen with distance
            if (perp < gate && t < bestT) { bestT = t; best = a; }
        }
        return best;
    }

    /// <summary>Default world half-extent for an actor that has no model — a small "point-entity"
    /// box around the origin so it stays pickable (Hammer draws an FGD-sized box for these).</summary>
    public const float DefaultActorHalf = 18f;

    /// <summary>World-space selection AABB of an actor: its drawn model's bounds when it has one,
    /// otherwise a small default box around the origin. Mirrors Hammer selecting entities by their
    /// model footprint rather than a fixed origin handle.</summary>
    public static (Vector3 min, Vector3 max) ActorBounds(ZActor a, ActorModelResolver? resolver, bool adult)
    {
        if (resolver?.ModelWorldBounds(a, adult) is { } b) return b;
        var p = a.Position;
        var h = new Vector3(DefaultActorHalf);
        return (p - h, p + h);
    }

    /// <summary>True when the actor resolves to a real 3D model (so the editor draws the model and
    /// suppresses the origin marker), false when it falls back to a billboard/marker.</summary>
    public static bool ActorHasModel(ZActor a, ActorModelResolver? resolver, bool adult)
        => resolver?.ModelWorldBounds(a, adult) != null;

    /// <summary>
    /// Nearest actor whose world AABB (model footprint, or default box) the ray enters — actors are
    /// picked by their model in the 3D view, not by a fixed origin handle.
    /// </summary>
    public static ZActor? PickActor(IEnumerable<ZActor> actors, Ray ray, ActorModelResolver? resolver, bool adult)
    {
        ZActor? best = null;
        float bestT = float.MaxValue;
        foreach (var a in actors)
        {
            var (mn, mx) = ActorBounds(a, resolver, adult);
            if (RayAabb(ray, mn, mx, out float t) && t < bestT) { bestT = t; best = a; }
        }
        return best;
    }

    /// <summary>Slab-test ray/AABB intersection. On hit, <paramref name="t"/> is the entry distance
    /// (0 if the origin is inside the box).</summary>
    public static bool RayAabb(Ray ray, Vector3 mn, Vector3 mx, out float t)
    {
        t = 0f;
        float tmin = 0f, tmax = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float o = ray.Origin[i], d = ray.Direction[i];
            float lo = mn[i], hi = mx[i];
            if (MathF.Abs(d) < 1e-9f) { if (o < lo || o > hi) return false; continue; }
            float inv = 1f / d;
            float t1 = (lo - o) * inv, t2 = (hi - o) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = MathF.Max(tmin, t1);
            tmax = MathF.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        t = tmin;
        return tmax >= 0f;
    }

    /// <summary>
    /// Picks a world point under the cursor: the nearest face hit if any, otherwise
    /// the intersection with the horizontal plane Y=<paramref name="groundY"/>.
    /// </summary>
    public static bool PickPoint(ZScene scene, Ray ray, out Vector3 point, float groundY = 0f)
    {
        if (PickFace(scene, ray, out var hit))
        {
            point = hit.Point;
            return true;
        }
        // Fall back to the ground plane.
        if (MathF.Abs(ray.Direction.Y) > 1e-6f)
        {
            float t = (groundY - ray.Origin.Y) / ray.Direction.Y;
            if (t > 0f)
            {
                point = ray.Origin + ray.Direction * t;
                return true;
            }
        }
        point = default;
        return false;
    }

    /// <summary>
    /// Möller–Trumbore ray/triangle intersection. On hit, <paramref name="t"/> is the
    /// positive distance along the ray. Double-sided (back faces count).
    /// </summary>
    public static bool RayTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        const float eps = 1e-6f;
        var e1 = b - a;
        var e2 = c - a;
        var p  = Vector3.Cross(ray.Direction, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < eps) return false;     // parallel

        float inv = 1f / det;
        var tv = ray.Origin - a;
        float u = Vector3.Dot(tv, p) * inv;
        if (u < 0f || u > 1f) return false;

        var q = Vector3.Cross(tv, e1);
        float v = Vector3.Dot(ray.Direction, q) * inv;
        if (v < 0f || u + v > 1f) return false;

        float dist = Vector3.Dot(e2, q) * inv;
        if (dist <= eps) return false;              // behind origin
        t = dist;
        return true;
    }
}
