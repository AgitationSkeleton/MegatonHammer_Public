using MegatonHammer.Editor;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MegatonHammer.Rendering;

public sealed class ActorRenderer : IDisposable
{
    private static readonly string Vert = @"
#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uMVP;
void main() { gl_Position = uMVP * vec4(aPos, 1.0); }";

    private static readonly string Frag = @"
#version 330 core
out vec4 fragColor;
uniform vec4 uColor;
void main() { fragColor = uColor; }";

    private readonly Shader _shader;
    private readonly int    _vao, _vbo;
    private bool _disposed;

    private static readonly Vector4 DefaultColor  = new(1.00f, 0.85f, 0.00f, 1f); // gold
    private static readonly Vector4 SelectedColor = new(1.00f, 0.28f, 0.00f, 1f); // red-orange
    private static readonly Vector4 ObsoleteColor = new(0.95f, 0.20f, 0.85f, 1f); // magenta — unknown entity

    private const float Cross2D = 16f;  // world units arm length in 2D views
    private const float Cross3D = 20f;  // world units arm length in 3D view

    public ActorRenderer()
    {
        _shader = new Shader(Vert, Frag);

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);
    }

    // ── 3D view ───────────────────────────────────────────────────────────
    // Actors show as their real model (or a billboard) — no per-actor origin cross by default. The SELECTED
    // actor gets a selection box (its model footprint) plus an origin reticule (a 3-axis cross at the exact
    // placed coordinate), so its precise position reads over/inside the model.

    public void Render3D(IEnumerable<ZActor> actors, Camera3D cam, int w, int h,
                         Editor.ActorModelResolver? resolver, bool adult)
    {
        var mvp = cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h);
        _shader.Use();
        _shader.SetMatrix4("uMVP", mvp);
        GL.DepthFunc(DepthFunction.Always);   // selection box always on top

        foreach (var actor in actors)
        {
            if (!actor.IsSelected) continue;
            _shader.SetVector4("uColor", Color(actor));
            var (mn, mx) = Editor.Picking.ActorBounds(actor, resolver, adult);
            DrawWireAabb3D(mn, mx);
            DrawCross3D(actor.Position, Cross3D);   // origin reticule (selected only)
        }

        GL.DepthFunc(DepthFunction.Less);
    }

    // Origin reticule: a 3-axis cross centred on the actor's exact placed position.
    private void DrawCross3D(Vector3 c, float s) => DrawLines(
    [
        c.X - s, c.Y, c.Z,  c.X + s, c.Y, c.Z,
        c.X, c.Y - s, c.Z,  c.X, c.Y + s, c.Z,
        c.X, c.Y, c.Z - s,  c.X, c.Y, c.Z + s,
    ]);

    // ── 2D views ──────────────────────────────────────────────────────────
    // Each actor is drawn as its model's projected WIREFRAME (EditorSettings.Actor2DWireframe, the default) so
    // its true footprint is what you align — or a bounding box when wireframes are off / it has no model. A
    // facing line shows rotation; the SELECTED actor also gets an origin reticule at its exact coordinate.

    // Per-actor cache of the world-space wireframe edges, keyed by a hash of the actor's transform so it's
    // rebuilt only when the actor moves/rotates (or its id/params change).
    private readonly Dictionary<ZActor, (int sig, float[] edges)> _wireCache = new();

    private float[]? WireEdges(ZActor a, Editor.ActorModelResolver resolver, bool adult)
    {
        int sig = System.HashCode.Combine(a.XPos, a.YPos, a.ZPos, a.YRot, a.Number, a.Variable, a.XRot, a.ZRot);
        if (_wireCache.TryGetValue(a, out var c) && c.sig == sig) return c.edges.Length > 0 ? c.edges : null;
        var e = resolver.WorldModelEdges(a, adult) ?? System.Array.Empty<float>();
        if (_wireCache.Count > 4000) _wireCache.Clear();   // bound the cache (deleted actors never re-hit)
        _wireCache[a] = (sig, e);
        return e.Length > 0 ? e : null;
    }

    public void Render2D(IEnumerable<ZActor> actors, Camera2D cam, int w, int h,
                         Editor.ActorModelResolver? resolver, bool adult)
    {
        var mvp = cam.GetProjectionMatrix(w, h);
        _shader.Use();
        _shader.SetMatrix4("uMVP", mvp);
        bool wireMode = Editor.EditorSettings.Actor2DWireframe;

        foreach (var actor in actors)
        {
            _shader.SetVector4("uColor", Color(actor));
            var (mn, mx) = Editor.Picking.ActorBounds(actor, resolver, adult);
            var (sh, sv, eh, ev) = OrthoRect(mn, mx, cam.Axis);

            // Wireframe (default): the model's edges projected onto this view's plane. Falls back to the
            // footprint box when wireframes are off or the actor has no model (billboard/box entities).
            float[]? edges = wireMode && resolver != null ? WireEdges(actor, resolver, adult) : null;
            if (edges != null) DrawWorldEdges2D(edges, cam.Axis);
            else DrawLines(
            [
                sh, sv, 0f,  eh, sv, 0f,   eh, sv, 0f,  eh, ev, 0f,
                eh, ev, 0f,  sh, ev, 0f,   sh, ev, 0f,  sh, sv, 0f,
            ]);

            // #29: facing indicator — a line from the footprint centre in the actor's in-plane yaw, so its
            // rotation is visible (and visibly changes when you click-cycle to Rotate and drag in 2D).
            // Uses the same per-view rotation field the rotate tool edits: Top→Y, Front→Z, Side→X.
            float ang = (cam.Axis switch
            {
                ViewAxis.Front => actor.ZRot,
                ViewAxis.Side  => actor.XRot,
                _              => actor.YRot,
            }) * (MathF.PI / 32768f);
            float cch = (sh + eh) * 0.5f, ccv = (sv + ev) * 0.5f;
            float len = MathF.Max(MathF.Max(eh - sh, ev - sv) * 0.55f, 18f);
            DrawLines([cch, ccv, 0f, cch + MathF.Sin(ang) * len, ccv + MathF.Cos(ang) * len, 0f]);

            // Origin reticule (selected only): a 2-axis cross at the exact placed coordinate.
            if (actor.IsSelected)
            {
                var (ph, pv) = WorldToOrtho(actor.Position, cam.Axis);
                DrawLines([ph - Cross2D, pv, 0f, ph + Cross2D, pv, 0f,  ph, pv - Cross2D, 0f, ph, pv + Cross2D, 0f]);
            }
        }
    }

    // Draws a static world-space wireframe (flat x,y,z point pairs) dimmed + blended onto a 2D view plane —
    // the 2D ghost reference overlay (trace room outlines in the top-down/front/side panes).
    public void RenderWorldWire2D(float[] worldEdges, Camera2D cam, int w, int h, Vector4 color)
    {
        if (worldEdges.Length == 0) return;
        _shader.Use();
        _shader.SetMatrix4("uMVP", cam.GetProjectionMatrix(w, h));
        _shader.SetVector4("uColor", color);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        DrawWorldEdges2D(worldEdges, cam.Axis);
        GL.Disable(EnableCap.Blend);
    }

    private float[] _projScratch = System.Array.Empty<float>();

    // Draws world-space wireframe edges (flat x,y,z pairs) projected onto a 2D view plane. Projects into a
    // reused scratch buffer so a per-frame wireframe of many actors doesn't churn the GC.
    private void DrawWorldEdges2D(float[] world, ViewAxis axis)
    {
        if (_projScratch.Length < world.Length) _projScratch = new float[world.Length];
        for (int i = 0; i < world.Length; i += 3)
        {
            var (hh, vv) = WorldToOrtho(new Vector3(world[i], world[i + 1], world[i + 2]), axis);
            _projScratch[i] = hh; _projScratch[i + 1] = vv; _projScratch[i + 2] = 0f;
        }
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, world.Length * sizeof(float), _projScratch, BufferUsageHint.StreamDraw);
        GL.DrawArrays(PrimitiveType.Lines, 0, world.Length / 3);
        GL.BindVertexArray(0);
    }

    // World AABB → ortho-plane rectangle (min/max of the 8 projected corners).
    private static (float sh, float sv, float eh, float ev) OrthoRect(Vector3 mn, Vector3 mx, ViewAxis axis)
    {
        float sh = float.MaxValue, sv = float.MaxValue, eh = float.MinValue, ev = float.MinValue;
        foreach (float x in new[] { mn.X, mx.X })
        foreach (float y in new[] { mn.Y, mx.Y })
        foreach (float z in new[] { mn.Z, mx.Z })
        {
            var (hh, vv) = WorldToOrtho(new Vector3(x, y, z), axis);
            sh = MathF.Min(sh, hh); eh = MathF.Max(eh, hh);
            sv = MathF.Min(sv, vv); ev = MathF.Max(ev, vv);
        }
        return (sh, sv, eh, ev);
    }

    // Wireframe AABB (12 edges) for a selected modelled actor.
    private void DrawWireAabb3D(Vector3 mn, Vector3 mx)
    {
        float[] v =
        [
            mn.X,mn.Y,mn.Z, mx.X,mn.Y,mn.Z,  mx.X,mn.Y,mn.Z, mx.X,mn.Y,mx.Z,
            mx.X,mn.Y,mx.Z, mn.X,mn.Y,mx.Z,  mn.X,mn.Y,mx.Z, mn.X,mn.Y,mn.Z,
            mn.X,mx.Y,mn.Z, mx.X,mx.Y,mn.Z,  mx.X,mx.Y,mn.Z, mx.X,mx.Y,mx.Z,
            mx.X,mx.Y,mx.Z, mn.X,mx.Y,mx.Z,  mn.X,mx.Y,mx.Z, mn.X,mx.Y,mn.Z,
            mn.X,mn.Y,mn.Z, mn.X,mx.Y,mn.Z,  mx.X,mn.Y,mn.Z, mx.X,mx.Y,mn.Z,
            mx.X,mn.Y,mx.Z, mx.X,mx.Y,mx.Z,  mn.X,mn.Y,mx.Z, mn.X,mx.Y,mx.Z,
        ];
        DrawLines(v);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static Vector4 Color(ZActor a)
        => a.IsSelected ? SelectedColor : a.IsObsolete ? ObsoleteColor : DefaultColor;

    // Wireframe box around an obsolete-entity marker (12 edges).
    private void DrawBox3D(Vector3 c, float s)
    {
        float[] v =
        [
            // bottom square
            c.X-s,c.Y-s,c.Z-s, c.X+s,c.Y-s,c.Z-s,  c.X+s,c.Y-s,c.Z-s, c.X+s,c.Y-s,c.Z+s,
            c.X+s,c.Y-s,c.Z+s, c.X-s,c.Y-s,c.Z+s,  c.X-s,c.Y-s,c.Z+s, c.X-s,c.Y-s,c.Z-s,
            // top square
            c.X-s,c.Y+s,c.Z-s, c.X+s,c.Y+s,c.Z-s,  c.X+s,c.Y+s,c.Z-s, c.X+s,c.Y+s,c.Z+s,
            c.X+s,c.Y+s,c.Z+s, c.X-s,c.Y+s,c.Z+s,  c.X-s,c.Y+s,c.Z+s, c.X-s,c.Y+s,c.Z-s,
            // verticals
            c.X-s,c.Y-s,c.Z-s, c.X-s,c.Y+s,c.Z-s,  c.X+s,c.Y-s,c.Z-s, c.X+s,c.Y+s,c.Z-s,
            c.X+s,c.Y-s,c.Z+s, c.X+s,c.Y+s,c.Z+s,  c.X-s,c.Y-s,c.Z+s, c.X-s,c.Y+s,c.Z+s,
        ];
        DrawLines(v);
    }

    private static (float h, float v) WorldToOrtho(Vector3 world, ViewAxis axis) => axis switch
    {
        ViewAxis.Top   => (world.X, -world.Z),
        ViewAxis.Front => (world.X,  world.Y),
        ViewAxis.Side  => (world.Z,  world.Y),
        _              => (0, 0)
    };

    private void DrawLines(float[] pts)
    {
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, pts.Length * sizeof(float), pts, BufferUsageHint.StreamDraw);
        GL.DrawArrays(PrimitiveType.Lines, 0, pts.Length / 3);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        _shader.Dispose();
        _disposed = true;
    }
}
