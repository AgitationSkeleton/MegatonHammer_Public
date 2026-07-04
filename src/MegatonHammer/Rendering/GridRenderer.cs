using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MegatonHammer.Rendering;

public sealed class GridRenderer : IDisposable
{
    private static readonly string VertSrc = @"
#version 330 core
layout(location = 0) in vec3 aPos;
uniform mat4 uMVP;
void main() { gl_Position = uMVP * vec4(aPos, 1.0); }";

    private static readonly string FragSrc = @"
#version 330 core
out vec4 fragColor;
uniform vec4 uColor;
void main() { fragColor = uColor; }";

    private readonly Shader _shader;
    private int _vao, _vbo;
    private bool _disposed;

    public int GridSize = 64;

    public GridRenderer()
    {
        _shader = new Shader(VertSrc, FragSrc);

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);
    }

    public void DrawOrtho(Camera2D cam, int viewW, int viewH)
    {
        var verts = BuildOrthoGrid(cam, viewW, viewH);
        UploadVerts(verts);

        _shader.Use();
        var mvp = cam.GetProjectionMatrix(viewW, viewH);
        _shader.SetMatrix4("uMVP", mvp);

        // Crisp, Hammer-style 1px aliased grid lines (no smoothing/blending so they don't blur
        // or fade out as you zoom). Restored afterwards for the AA'd 3D wireframe pass.
        GL.Disable(EnableCap.LineSmooth);
        GL.Disable(EnableCap.Blend);
        GL.LineWidth(1f);

        GL.BindVertexArray(_vao);
        DrawSegments(verts, cam);
        GL.BindVertexArray(0);

        GL.Enable(EnableCap.LineSmooth);
    }

    private void DrawSegments(List<(Vector3 a, Vector3 b, Vector4 col)> segs, Camera2D cam)
    {
        // Batch by color to reduce uniform changes
        var groups = segs.GroupBy(s => s.col);
        foreach (var g in groups)
        {
            _shader.SetVector4("uColor", g.Key);
            var pts = new List<float>();
            foreach (var (a, b, _) in g)
            {
                pts.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z]);
            }
            var arr = pts.ToArray();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Lines, 0, arr.Length / 3);
        }
    }

    private List<(Vector3 a, Vector3 b, Vector4 col)> BuildOrthoGrid(Camera2D cam, int viewW, int viewH)
    {
        var segs = new List<(Vector3, Vector3, Vector4)>();

        float halfW = viewW * 0.5f * cam.Zoom;
        float halfH = viewH * 0.5f * cam.Zoom;
        float left   = cam.PanX - halfW;
        float right  = cam.PanX + halfW;
        float bottom = cam.PanY - halfH;
        float top    = cam.PanY + halfH;

        // Effective step = the finest grid the user can actually SEE at this zoom; the brush
        // tools snap to the same value, so drawing/scaling always conforms to the visible grid.
        // Two nested highlight tiers (every 8 cells, every 64 cells) give Hammer's "city-block"
        // hierarchy that batches together as you zoom, so the scale is always readable.
        int step = Editor.GridSnap.EffectiveStep(GridSize, cam.Zoom);
        // Guard against a pathological line count (e.g. a tiny step over a huge visible range),
        // which would flood the view with so many lines they merge into solid grey bands. Coarsen
        // the step until each axis stays under a sane cap.
        const int maxLines = 1024;
        while ((right - left) / step > maxLines || (top - bottom) / step > maxLines) step *= 2;

        // Orange "sector" lines are pinned to FIXED world coordinates — multiples of 1024, like
        // Hammer++'s 1024-unit city blocks — so they stay put as you zoom/pan instead of tracking
        // the view. Keep them aligned to the visible grid (never finer than `step`), and only
        // coarsen to 2048/4096/… when 1024 would pack too tightly at a very zoomed-out view.
        const int sectorBase = 1024;
        int sectorStep = sectorBase;
        while (sectorStep < step) sectorStep *= 2;                 // stay on the grid lattice
        while (sectorStep / cam.Zoom < 24f) sectorStep *= 2;       // keep on-screen spacing readable

        // Dim minor grid; orange world-block sectors; bold blue origin cross (drawn last, on top).
        static Vector4 Gray(int v) => new(v / 255f, v / 255f, v / 255f, 1f);
        var minorCol  = Gray(48);
        var sectorCol = new Vector4(0.46f, 0.34f, 0.60f, 1f);  // fixed 1024-unit sectors (muted violet —
                                                               // #2: was orange, read too much like the selection highlight)
        var originCol = new Vector4(0.25f, 0.50f, 1.00f, 1f);  // world-centre cross (blue)

        Vector4 Tier(int n) => (n % sectorStep == 0) ? sectorCol : minorCol;

        int iLeft   = (int)MathF.Floor(left   / step) * step;
        int iRight  = (int)MathF.Ceiling(right / step) * step;
        int iBottom = (int)MathF.Floor(bottom  / step) * step;
        int iTop    = (int)MathF.Ceiling(top    / step) * step;

        // Vertical lines (constant horizontal position) — skip the origin here; drawn last.
        for (int h = iLeft; h <= iRight; h += step)
        {
            if (h == 0) continue;
            (float ax, float ay) = ToScreen2D(h, bottom, cam.Axis);
            (float bx, float by) = ToScreen2D(h, top,    cam.Axis);
            segs.Add((new Vector3(ax, ay, 0), new Vector3(bx, by, 0), Tier(h)));
        }

        // Horizontal lines (constant vertical position) — skip the origin here; drawn last.
        for (int v = iBottom; v <= iTop; v += step)
        {
            if (v == 0) continue;
            (float ax, float ay) = ToScreen2D(left,  v, cam.Axis);
            (float bx, float by) = ToScreen2D(right, v, cam.Axis);
            segs.Add((new Vector3(ax, ay, 0), new Vector3(bx, by, 0), Tier(v)));
        }

        // World-origin cross, last so it draws over the grid (blue vertical + horizontal).
        if (left <= 0 && right >= 0)
        {
            (float ax, float ay) = ToScreen2D(0, bottom, cam.Axis);
            (float bx, float by) = ToScreen2D(0, top,    cam.Axis);
            segs.Add((new Vector3(ax, ay, 0), new Vector3(bx, by, 0), originCol));
        }
        if (bottom <= 0 && top >= 0)
        {
            (float ax, float ay) = ToScreen2D(left,  0, cam.Axis);
            (float bx, float by) = ToScreen2D(right, 0, cam.Axis);
            segs.Add((new Vector3(ax, ay, 0), new Vector3(bx, by, 0), originCol));
        }

        return segs;
    }

    // The grid loop already works in the camera's projection space (its ranges come straight from
    // PanX/PanY ± half-extent), so this is a pass-through for every axis. (It used to negate v for
    // the Top view — a second transform on top of the projection — which shifted the Top grid out
    // of the visible vertical range when panned in Z, leaving unfilled bands at the top/bottom.)
    private static (float x, float y) ToScreen2D(float h, float v, ViewAxis axis) => (h, v);

    private void UploadVerts(List<(Vector3, Vector3, Vector4)> segs)
    {
        // Actual upload happens in DrawSegments per color group
    }

    public void Draw3DGroundGrid(Camera3D cam, int viewW, int viewH, int extent = 4096)
    {
        var segs = new List<(Vector3 a, Vector3 b, Vector4 col)>();

        int step = GridSize;
        for (int x = -extent; x <= extent; x += step)
        {
            var col = x == 0
                ? new Vector4(0.7f, 0.15f, 0.15f, 1f)  // X axis red
                : (x % (step * 8) == 0
                    ? new Vector4(0.35f, 0.35f, 0.35f, 1f)
                    : new Vector4(0.2f,  0.2f,  0.2f,  1f));
            segs.Add((new Vector3(x, 0, -extent), new Vector3(x, 0, extent), col));
        }
        for (int z = -extent; z <= extent; z += step)
        {
            var col = z == 0
                ? new Vector4(0.15f, 0.15f, 0.7f, 1f)  // Z axis blue
                : (z % (step * 8) == 0
                    ? new Vector4(0.35f, 0.35f, 0.35f, 1f)
                    : new Vector4(0.2f,  0.2f,  0.2f,  1f));
            segs.Add((new Vector3(-extent, 0, z), new Vector3(extent, 0, z), col));
        }

        var view = cam.GetViewMatrix();
        var proj = cam.GetProjectionMatrix(viewW, viewH);
        var mvp  = view * proj;

        _shader.Use();
        GL.BindVertexArray(_vao);

        var groups = segs.GroupBy(s => s.col);
        foreach (var g in groups)
        {
            _shader.SetVector4("uColor", g.Key);
            _shader.SetMatrix4("uMVP", mvp);
            var pts = new List<float>();
            foreach (var (a, b, _) in g)
                pts.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z]);
            var arr = pts.ToArray();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Lines, 0, arr.Length / 3);
        }

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
