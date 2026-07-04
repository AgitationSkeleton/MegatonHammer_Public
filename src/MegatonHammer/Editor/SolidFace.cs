using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

public sealed class SolidFace
{
    public Plane3D            Plane    { get; }
    public IReadOnlyList<Vector3> Vertices { get; }
    public Vector3            Color    { get; set; } = new(0.50f, 0.50f, 0.52f);

    // Optional per-vertex shade colours (one per face vertex), painted by the shade tool.
    // Null = unpainted (use the scene light / face Color). Stored sparsely so most faces
    // cost nothing; reset if the vertex count no longer matches (geometry was re-cut).
    public Vector3[]?         VertexColors { get; set; }

    /// <summary>Shade colour for face vertex <paramref name="i"/> — painted value or fallback.</summary>
    public Vector3 ColorAt(int i, Vector3 fallback)
        => VertexColors != null && VertexColors.Length == Vertices.Count ? VertexColors[i] : fallback;

    /// <summary>Whether this face has any painted vertex colours.</summary>
    public bool IsPainted => VertexColors != null && VertexColors.Length == Vertices.Count;

    // Name of the applied texture in the TextureLibrary, or null for an untextured
    // (flat-coloured) face. Set by the texture-paint tool.
    public string?           TextureName { get; set; }

    // ── Texture mapping (Hammer Face-Edit model) ──────────────────────────
    // World units one texture tile spans along S / T (Hammer "scale"); shift offsets the
    // mapping in tile units; rotation spins the texture axes in the face plane (degrees);
    // AlignToFace maps in the face's own tangent space vs the world axes (planar).
    public float  TexScaleS   { get; set; } = 64f;
    public float  TexScaleT   { get; set; } = 64f;
    public float  TexShiftS   { get; set; }
    public float  TexShiftT   { get; set; }
    public float  TexRotation { get; set; }
    public bool   AlignToFace { get; set; }

    /// <summary>Back-compat: old single-scale accessor mirrors the S/T scales.</summary>
    public float TextureScale
    {
        get => TexScaleS;
        set { TexScaleS = value; TexScaleT = value; }
    }

    // ── Explicit texture U/V world axes (SDK2013 CMapFace model) ─────────────
    // The stored source of truth for the projection direction. (0,0,0) = not yet initialized → lazily
    // derived from the face normal + TexRotation (so a face that never had explicit axes renders
    // identically to before). Storing them — rather than re-deriving from the normal every time — is
    // what makes Hammer "texture lock" work: when the brush rotates / flips, these rotate WITH it
    // (see Solid.TransformAbout / SelectTool rotate / Solid.Flip), so the texture sticks to the surface
    // instead of re-projecting off the new normal (the #4/#24 bug).
    public Vector3 UAxis;
    public Vector3 VAxis;

    /// <summary>Initialize the explicit axes from the normal + rotation/alignment if they're unset.</summary>
    public void EnsureAxes()
    {
        if (UAxis != Vector3.Zero || VAxis != Vector3.Zero) return;
        (UAxis, VAxis) = DerivedAxes();
    }

    /// <summary>The face's natural (zero-rotation) U/V axes from its normal — the frame the Face-Edit
    /// rotation is measured against. World-aligned (snapped to the dominant world axes) or face-tangent.</summary>
    private (Vector3 u, Vector3 v) BaseAxes()
    {
        Vector3 n = Plane.Normal;
        if (AlignToFace)
        {
            // Tangent space: U perpendicular to the normal (and to world-up where possible).
            Vector3 up = MathF.Abs(n.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
            Vector3 u = Vector3.Normalize(Vector3.Cross(up, n));
            return (u, Vector3.Normalize(Vector3.Cross(n, u)));
        }
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        return (ax >= ay && ax >= az) ? (Vector3.UnitZ, Vector3.UnitY)   // X-facing
             : (ay >= ax && ay >= az) ? (Vector3.UnitX, Vector3.UnitZ)   // Y-facing
                                      : (Vector3.UnitX, Vector3.UnitY);  // Z-facing
    }

    /// <summary>World/face-aligned U/V axes derived from the face normal, rotated by TexRotation —
    /// the original (pre-explicit-axis) mapping. Used to seed the explicit axes and on reset.</summary>
    private (Vector3 u, Vector3 v) DerivedAxes()
    {
        var (u, v) = BaseAxes();
        if (MathF.Abs(TexRotation) > 1e-3f)
        {
            float r = MathHelper.DegreesToRadians(TexRotation);
            float c = MathF.Cos(r), s = MathF.Sin(r);
            (u, v) = (u * c + v * s, v * c - u * s);
        }
        return (u, v);
    }

    /// <summary>The texture's ACTUAL rotation (degrees) — the angle of the stored U axis relative to the
    /// face's natural base axes. Unlike the <see cref="TexRotation"/> field this is read back from the real
    /// axes, so it stays correct after a brush rotate / flip / seam-align folded the mapping (which moved
    /// the axes without touching the scalar). This is what the Face-Edit sheet should display.</summary>
    public float CurrentRotationDegrees()
    {
        EnsureAxes();
        var (bu, bv) = BaseAxes();
        Vector3 u = UAxis.LengthSquared > 1e-9f ? Vector3.Normalize(UAxis) : bu;
        // u = bu·cos(r) + bv·sin(r)  (DerivedAxes' convention) → r = atan2(u·bv, u·bu).
        return MathHelper.RadiansToDegrees(MathF.Atan2(Vector3.Dot(u, bv), Vector3.Dot(u, bu)));
    }

    /// <summary>Sets the texture rotation to an ABSOLUTE angle relative to the face's base axes (the
    /// Face-Edit rotation field), recomputing the U/V axes. Keeps <see cref="TexRotation"/> truthful.</summary>
    public void SetRotation(float degrees)
    {
        TexRotation = degrees;
        var (bu, bv) = BaseAxes();
        float r = MathHelper.DegreesToRadians(degrees);
        float c = MathF.Cos(r), s = MathF.Sin(r);
        UAxis = Vector3.Normalize(bu * c + bv * s);
        VAxis = Vector3.Normalize(bv * c - bu * s);
    }

    /// <summary>The face's texture U/V axes (explicit, lazily seeded). Per-vertex texture coords use these.</summary>
    public (Vector3 u, Vector3 v) TextureAxes()
    {
        EnsureAxes();
        return (UAxis, VAxis);
    }

    /// <summary>Re-derive the axes from the current normal + TexRotation/alignment (Face-Edit "reset"
    /// and AlignToFace toggle): drops any accumulated texture-lock rotation.</summary>
    public void ResetAxes()
    {
        UAxis = VAxis = Vector3.Zero;
        EnsureAxes();
    }

    /// <summary>SDK2013 RotateTextureAxes: rotate the stored axes by <paramref name="degrees"/> CCW
    /// about the texture normal (cross(V,U)), keeping them in the face plane.</summary>
    public void RotateTextureAxes(float degrees)
    {
        EnsureAxes();
        Vector3 axis = Vector3.Cross(VAxis, UAxis);
        if (axis.LengthSquared < 1e-9f) axis = Plane.Normal;
        axis = Vector3.Normalize(axis);
        UAxis = RotateAbout(UAxis, axis, degrees);
        VAxis = RotateAbout(VAxis, axis, degrees);
    }

    /// <summary>Texture lock through a brush rotate/skew: apply the geometry's linear map to the axes
    /// and renormalize, so the mapping rotates with the surface (SDK2013 DoTransform transforms UAxis/VAxis).</summary>
    public void TransformAxes(Func<Vector3, Vector3> linearMap)
    {
        EnsureAxes();
        UAxis = SafeNormalize(linearMap(UAxis));
        VAxis = SafeNormalize(linearMap(VAxis));
    }

    /// <summary>Directly set the (normalized) texture axes — used by the rotate drag to map a snapshot
    /// of the original axes by the current absolute rotation each frame (drift-free, like the planes).</summary>
    public void SetAxes(Vector3 u, Vector3 v)
    {
        UAxis = SafeNormalize(u);
        VAxis = SafeNormalize(v);
        // Keep the scalar rotation truthful so the Face-Edit sheet shows the real angle after a
        // texture-lock rotate or a seam-align fold moved the axes directly.
        TexRotation = CurrentRotationDegrees();
    }

    /// <summary>Mirror the axes on one world axis (0=X,1=Y,2=Z) for a brush Flip.</summary>
    public void FlipAxes(int axis)
    {
        EnsureAxes();
        UAxis = MirrorComponent(UAxis, axis);
        VAxis = MirrorComponent(VAxis, axis);
    }

    /// <summary>Texture lock through a brush Flip (mirror about <paramref name="center"/> on <paramref
    /// name="axis"/>): mirror the U/V axes AND compensate the shift for the mirror's translation part, so the
    /// texture stays welded to the geometry (SDK2013 DoTransform for a flip). Mirroring the axes alone leaves
    /// the texture SLID by 2·center·axisComponent/scale tiles — a visible offset when the flip centre isn't
    /// the texture origin (i.e. almost always, since you flip about the brush/selection centre). The shift
    /// uses the PRE-mirror axis component; run this instead of <see cref="FlipAxes"/> when a centre is known.</summary>
    public void FlipTextureLock(int axis, float center)
    {
        EnsureAxes();
        float sS = MathF.Abs(TexScaleS) < 1e-3f ? 64f : TexScaleS;
        float sT = MathF.Abs(TexScaleT) < 1e-3f ? 64f : TexScaleT;
        float uc = axis == 0 ? UAxis.X : axis == 1 ? UAxis.Y : UAxis.Z;   // pre-mirror component
        float vc = axis == 0 ? VAxis.X : axis == 1 ? VAxis.Y : VAxis.Z;
        TexShiftS += 2f * center * uc / sS;
        TexShiftT += 2f * center * vc / sT;
        UAxis = MirrorComponent(UAxis, axis);
        VAxis = MirrorComponent(VAxis, axis);
        TexRotation = CurrentRotationDegrees();   // keep the scalar truthful after the axes moved
    }

    private static Vector3 MirrorComponent(Vector3 v, int axis)
        => axis == 0 ? new(-v.X, v.Y, v.Z) : axis == 1 ? new(v.X, -v.Y, v.Z) : new(v.X, v.Y, -v.Z);

    private static Vector3 SafeNormalize(Vector3 v)
        => v.LengthSquared < 1e-12f ? v : Vector3.Normalize(v);

    private static Vector3 RotateAbout(Vector3 v, Vector3 axis, float degrees)
    {
        float r = MathHelper.DegreesToRadians(degrees);
        float c = MathF.Cos(r), s = MathF.Sin(r);
        // Rodrigues' rotation formula.
        return v * c + Vector3.Cross(axis, v) * s + axis * Vector3.Dot(axis, v) * (1f - c);
    }

    /// <summary>Texture coordinate (in tile units) for a world point on this face.</summary>
    public Vector2 UVAt(Vector3 p)
    {
        var (u, v) = TextureAxes();
        float sS = MathF.Abs(TexScaleS) < 1e-3f ? 64f : TexScaleS;
        float sT = MathF.Abs(TexScaleT) < 1e-3f ? 64f : TexScaleT;
        // Use only the FRACTIONAL part of the shift. The texture-lock translate accumulates the shift
        // (it can reach thousands of tiles), which the editor renders fine (GL wraps float UVs) but on
        // export overflowed the s16 S/T coords → every vertex clamped to the same texel = corrupted
        // textures. For a tiling texture the integer tile offset is invisible, so the fractional shift is
        // visually identical AND keeps the exported coords in range.
        return new(Vector3.Dot(p, u) / sS + Frac(TexShiftS), Vector3.Dot(p, v) / sT + Frac(TexShiftT));
    }

    private static float Frac(float x) => x - MathF.Floor(x);

    /// <summary>Reduce the texture shift to the fractional part that <see cref="UVAt"/> actually renders, so
    /// the value shown/edited in the Face Edit sheet matches what's drawn (and nudging it stays continuous).
    /// The integer tile offset is invisible for a tiling texture. Hammer does the same in NormalizeTextureShifts;
    /// call after any op that can leave a large shift (Justify / Fit) so the two representations don't diverge.</summary>
    public void NormalizeShift() { TexShiftS = Frac(TexShiftS); TexShiftT = Frac(TexShiftT); }

    /// <summary>Per-cell shade paint over a QUAD face: a parametric grid of vertex colours so the spray tool
    /// can shade a LOCAL patch. A flat quad has only 4 corners, which the GPU/exporter interpolate across the
    /// WHOLE face — hence the old "paints the whole face". Stored parametrically (grid indices, not world
    /// positions) so it stays attached when the brush moves/resizes. Null = not painted (use VertexColors).</summary>
    public ShadeGrid? ShadePaint { get; set; }

    public sealed class ShadeGrid
    {
        public int Nu, Nv;               // (Nu+1) * (Nv+1) grid nodes
        public Vector3[] Colors = [];    // row-major: node (i,j) at index j*(Nu+1)+i
        public int Index(int i, int j) => j * (Nu + 1) + i;
    }

    /// <summary>World position of shade-grid node (i,j) — bilinear over the quad's 4 corners (requires 4 verts).</summary>
    public Vector3 ShadeGridPos(int i, int j, int nu, int nv)
    {
        var q = Vertices;
        float s = (float)i / nu, t = (float)j / nv;
        var bottom = Vector3.Lerp(q[0], q[1], s);
        var top    = Vector3.Lerp(q[3], q[2], s);
        return Vector3.Lerp(bottom, top, t);
    }

    /// <summary>Selected in the Face Edit tool (runtime only; multi-face texture editing).</summary>
    public bool FaceSelected { get; set; }

    // Index of the clip plane that produced this face, used to carry face attributes
    // across geometry recomputes (e.g. when a brush is scaled).
    public int               PlaneIndex { get; set; } = -1;

    public SolidFace(Plane3D plane, IReadOnlyList<Vector3> vertices)
    {
        Plane    = plane;
        Vertices = vertices;
    }
}
