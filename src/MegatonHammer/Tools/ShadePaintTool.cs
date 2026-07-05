using MegatonHammer.Editor;
using MegatonHammer.Forms;
using OpenTK.Mathematics;

namespace MegatonHammer.Tools;

/// <summary>
/// Vertex-shade paint tool (Blender-style): in the 3D view, drag over brush faces to
/// "spray" the current colour into their vertices with a radial falloff. OoT lighting is
/// per-vertex colour, so painted shade exports directly into the vertex data. Strokes are
/// reversible — the whole drag is one undo step, snapshotting each touched face's colours.
/// </summary>
public sealed class ShadePaintTool : ITool
{
    private readonly MapDocument _doc;

    public string Name => "Shade";

    /// <summary>Colour sprayed onto vertices (default black, as requested).</summary>
    public Vector3 PaintColor { get; set; } = Vector3.Zero;
    /// <summary>Per-application blend strength 0..1.</summary>
    public float Opacity { get; set; } = 0.5f;
    /// <summary>Falloff radius in world units around the cursor hit point. Smaller = more local spray; the
    /// default is deliberately tight so a stroke shades a patch rather than the whole (small) face.</summary>
    public float Radius { get; set; } = 48f;
    /// <summary>Erase mode: instead of spraying <see cref="PaintColor"/>, blend touched vertices back toward
    /// the face's unpainted base colour, removing shade. A fully-reverted face drops its paint entirely.</summary>
    public bool Erase { get; set; }

    private bool _painting;
    private bool _undoRecorded;
    private readonly HashSet<SolidFace> _stroked = [];

    public ShadePaintTool(MapDocument doc) { _doc = doc; }

    public void OnMouseDown(GLViewport vp, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || vp.ViewportType != ViewportType.Perspective3D) return;
        _painting = true;
        _undoRecorded = false;
        _stroked.Clear();
        Paint(vp, e);
    }

    public void OnMouseMove(GLViewport vp, MouseEventArgs e)
    {
        if (_painting) Paint(vp, e);
    }

    public void OnMouseUp(GLViewport vp, MouseEventArgs e)
    {
        _painting = false;
        _stroked.Clear();
    }

    public void OnKeyDown(GLViewport vp, KeyEventArgs e) { }

    private void Paint(GLViewport vp, MouseEventArgs e)
    {
        var cam = vp.ActiveCamera3D;
        if (cam == null) return;
        var ray = Picking.RayFromScreen(cam, e.X, e.Y, vp.Width, vp.Height);
        if (!Picking.PickFace(_doc.Scene, ray, out var hit)) return;

        if (!_undoRecorded) { _doc.RecordUndo(); _undoRecorded = true; }

        var face = hit.Face;
        // Erase blends toward the face's flat base colour (removing shade); paint blends toward PaintColor.
        Vector3 target = Erase ? face.Color : PaintColor;
        bool changed;
        // Quad faces get a dense parametric shade GRID so the spray shades a LOCAL patch instead of tinting
        // the whole face via corner interpolation. Non-quads fall back to per-corner painting.
        if (face.Vertices.Count == 4)
        {
            var g = EnsureGrid(face);
            changed = false;
            for (int j = 0; j <= g.Nv; j++)
                for (int i = 0; i <= g.Nu; i++)
                {
                    float dist = (face.ShadeGridPos(i, j, g.Nu, g.Nv) - hit.Point).Length;
                    if (dist > Radius) continue;
                    float w = Math.Clamp(Opacity * (1f - dist / Radius), 0f, 1f);
                    int k = g.Index(i, j);
                    g.Colors[k] = Vector3.Lerp(g.Colors[k], target, w);
                    changed = true;
                }
            // Fully-erased grid (every node back at base) → drop the paint so the face is truly unpainted.
            if (Erase && changed && g.Colors.All(c => (c - face.Color).LengthSquared < 1e-6f))
                face.ShadePaint = null;
        }
        else
        {
            EnsurePainted(face);
            changed = false;
            for (int i = 0; i < face.Vertices.Count; i++)
            {
                float dist = (face.Vertices[i] - hit.Point).Length;
                if (dist > Radius) continue;
                float w = Math.Clamp(Opacity * (1f - dist / Radius), 0f, 1f);
                face.VertexColors![i] = Vector3.Lerp(face.VertexColors[i], target, w);
                changed = true;
            }
            if (Erase && changed && face.VertexColors!.All(c => (c - face.Color).LengthSquared < 1e-6f))
                face.VertexColors = null;
        }
        if (changed)
        {
            _stroked.Add(face);
            _doc.NotifyChanged();
            vp.Invalidate();
        }
    }

    /// <summary>True if any solid is currently selected — lets the dialog scope "remove all paint" to the
    /// selection rather than the whole scene.</summary>
    public bool HasSelection => _doc.Scene.Rooms.Any(r => r.Geometry.Any(s => s.IsSelected));

    /// <summary>Strips all sprayed shade from faces: the current selection if any solids are selected,
    /// otherwise every solid in the scene. One undo step. Returns the number of faces cleared.</summary>
    public int ClearPaint(bool selectionOnly)
    {
        var solids = _doc.Scene.Rooms.SelectMany(r => r.Geometry).ToList();
        if (selectionOnly) { var sel = solids.Where(s => s.IsSelected).ToList(); if (sel.Count > 0) solids = sel; }
        var painted = solids.SelectMany(s => s.Faces).Where(f => f.ShadePaint != null || f.VertexColors != null).ToList();
        if (painted.Count == 0) return 0;
        _doc.RecordUndo();                                          // snapshot BEFORE clearing
        foreach (var f in painted) { f.ShadePaint = null; f.VertexColors = null; }
        _doc.NotifyChanged();
        return painted.Count;
    }

    // Build (once) a parametric shade grid over a quad face, dense enough that the smallest brush shades a
    // local patch. ~24-unit cells, clamped 2..16 per axis to bound the exported vertex count.
    private static SolidFace.ShadeGrid EnsureGrid(SolidFace face)
    {
        if (face.ShadePaint is { } g && g.Colors.Length == (g.Nu + 1) * (g.Nv + 1)) return g;
        var q = face.Vertices;
        float edgeU = ((q[1] - q[0]).Length + (q[2] - q[3]).Length) * 0.5f;
        float edgeV = ((q[3] - q[0]).Length + (q[2] - q[1]).Length) * 0.5f;
        int nu = Math.Clamp((int)MathF.Round(edgeU / 24f), 2, 16);
        int nv = Math.Clamp((int)MathF.Round(edgeV / 24f), 2, 16);
        var grid = new SolidFace.ShadeGrid { Nu = nu, Nv = nv, Colors = new Vector3[(nu + 1) * (nv + 1)] };
        for (int i = 0; i < grid.Colors.Length; i++) grid.Colors[i] = face.Color;
        face.ShadePaint = grid;
        return grid;
    }

    // Initialise a face's per-vertex colour array (from its flat colour) on first paint (non-quad fallback).
    private static void EnsurePainted(SolidFace face)
    {
        if (face.VertexColors != null && face.VertexColors.Length == face.Vertices.Count) return;
        var arr = new Vector3[face.Vertices.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = face.Color;
        face.VertexColors = arr;
    }
}
