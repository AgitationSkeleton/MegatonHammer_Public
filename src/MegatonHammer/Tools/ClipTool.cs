using MegatonHammer.Editor;
using MegatonHammer.Forms;
using MegatonHammer.Rendering;
using OpenTK.Mathematics;

namespace MegatonHammer.Tools;

/// <summary>
/// Hammer-style clip tool: drag a line in a 2D view to define a cutting plane
/// (perpendicular to the view) and split the selected brushes. X cycles which side(s)
/// are kept: front, back, or both.
/// </summary>
public sealed class ClipTool : ITool
{
    public enum Keep { Front, Back, Both }

    private readonly MapDocument _doc;

    private bool        _dragging;   // mouse is down, dragging the line
    private bool        _hasLine;    // a clip line is set and awaiting Enter (Hammer-style deferred apply)
    private GLViewport? _dragVp;
    private ViewAxis    _lineAxis;
    private float       _h1, _v1, _h2, _v2;

    public Keep KeepMode { get; private set; } = Keep.Front;

    public string Name => "Clip";

    public ClipTool(MapDocument doc) { _doc = doc; }

    /// <summary>Cycle which side(s) the pending clip keeps (front → back → both). Doesn't apply.</summary>
    public void CycleKeep() => KeepMode = (Keep)(((int)KeepMode + 1) % 3);

    /// <summary>Whether a clip line is currently shown in <paramref name="vp"/> (live drag or pending).</summary>
    public bool IsActiveDragViewport(GLViewport vp) => (_dragging || _hasLine) && vp == _dragVp;
    public (float h1, float v1, float h2, float v2) GetLine() => (_h1, _v1, _h2, _v2);

    public void OnMouseDown(GLViewport vp, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || vp.ViewportType == ViewportType.Perspective3D) return;
        var (h, v) = ScreenToOrtho(e.X, e.Y, vp.Width, vp.Height, vp.ActiveCamera2D!);
        // Hammer parity: clicking ON the pending clip line cycles which side is kept (front → back → both);
        // the preview highlight (kept = white, discarded = grey) follows. Clicking elsewhere starts a new line.
        if (_hasLine && vp == _dragVp && vp.ActiveCamera2D!.Axis == _lineAxis
            && DistToLine(h, v) <= 8f * vp.ActiveCamera2D!.Zoom)   // within ~8px of the line
        {
            CycleKeep();
            vp.RequestRedrawAll();   // the kept-side highlight flips in the 3D preview too
            return;
        }
        _h1 = _h2 = Snap(vp, h); _v1 = _v2 = Snap(vp, v);
        _dragging = true; _hasLine = false; _dragVp = vp;
        _lineAxis = vp.ActiveCamera2D!.Axis;   // set now so the 3D preview can build the cut plane live during the drag
        vp.Invalidate();
    }

    // Distance (in ortho world units) from a point to the pending clip line segment — for click-to-cycle.
    private float DistToLine(float h, float v)
    {
        var p = new Vector2(h, v); var a = new Vector2(_h1, _v1); var b = new Vector2(_h2, _v2);
        var ab = b - a; float len2 = ab.LengthSquared;
        if (len2 < 1e-3f) return (p - a).Length;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return (p - (a + t * ab)).Length;
    }

    public void OnMouseMove(GLViewport vp, MouseEventArgs e)
    {
        if (!_dragging || vp != _dragVp) return;
        var (h, v) = ScreenToOrtho(e.X, e.Y, vp.Width, vp.Height, vp.ActiveCamera2D!);
        _h2 = Snap(vp, h); _v2 = Snap(vp, v);
        vp.RequestRedrawAll();   // refresh the 3D view's live cut preview too, not just this 2D pane
    }

    public void OnMouseUp(GLViewport vp, MouseEventArgs e)
    {
        if (!_dragging || e.Button != MouseButtons.Left) return;
        _dragging = false;
        // Hammer: the drag only DEFINES the plane — it doesn't cut yet. Keep the line shown so the
        // user can cycle the kept side (X) and commit with Enter (or start a new line).
        var dh = _h2 - _h1; var dv = _v2 - _v1;
        _hasLine = dh * dh + dv * dv >= 1f;
        _lineAxis = vp.ActiveCamera2D!.Axis;
        vp.RequestRedrawAll();
    }

    // Hammer previews the clip while the line is pending: the kept half of each selected brush is drawn
    // bright (white) and the discarded half greyed. Returns 2D segments (in the pending line's view axis)
    // ready for DrawConnections2D, or null when there's nothing to preview.
    private static readonly Vector4 KeptCol = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 DiscardCol = new(0.40f, 0.40f, 0.40f, 1f);

    /// <summary>True while a clip line is being drawn or is pending and long enough to define a plane.</summary>
    public bool HasPreview => (_dragging || _hasLine) && LineLengthSq >= 1f;
    private float LineLengthSq { get { float dh = _h2 - _h1, dv = _v2 - _v1; return dh * dh + dv * dv; } }
    private Plane3D CurrentPlane() => BuildPlane(_lineAxis, new Vector2(_h1, _v1), new Vector2(_h2, _v2));

    // Split every target brush by the pending plane and hand each half + its keep/discard colour to `emit`
    // (kept unless the keep mode discards that side). Shared by the 2D and 3D previews.
    private void ForEachPreviewHalf(Action<Solid, Vector4> emit)
    {
        var plane = CurrentPlane();
        foreach (var s in TargetSolids())
        {
            var (front, back) = s.Split(plane);
            if (front != null) emit(front, KeepMode != Keep.Back  ? KeptCol : DiscardCol);
            if (back  != null) emit(back,  KeepMode != Keep.Front ? KeptCol : DiscardCol);
        }
    }

    public IReadOnlyList<(float h1, float v1, float h2, float v2, Vector4 col)>? PreviewSegments()
    {
        if (!_hasLine || LineLengthSq < 1f) return null;
        var (hDir, vDir, _) = Bases(_lineAxis);
        var segs = new List<(float, float, float, float, Vector4)>();
        ForEachPreviewHalf((s, col) =>
        {
            foreach (var f in s.Faces)
            {
                var vs = f.Vertices;
                for (int i = 0; i < vs.Count; i++)
                {
                    var a = vs[i]; var b = vs[(i + 1) % vs.Count];
                    segs.Add((Vector3.Dot(a, hDir), Vector3.Dot(a, vDir),
                              Vector3.Dot(b, hDir), Vector3.Dot(b, vDir), col));
                }
            }
        });
        return segs.Count > 0 ? segs : null;
    }

    /// <summary>The kept/discarded brush halves as WORLD-3D edges, for the 3D view's cut preview — Hammer
    /// shows where the cut will land (kept half bright, discarded greyed) as you draw the clip line. Works
    /// live during the drag and while the line is pending.</summary>
    public IReadOnlyList<(Vector3 a, Vector3 b, Vector4 col)>? PreviewSegments3D()
    {
        if (!HasPreview) return null;
        var lines = new List<(Vector3, Vector3, Vector4)>();
        ForEachPreviewHalf((s, col) =>
        {
            foreach (var f in s.Faces)
            {
                var vs = f.Vertices;
                for (int i = 0; i < vs.Count; i++)
                    lines.Add((vs[i], vs[(i + 1) % vs.Count], col));
            }
        });
        return lines.Count > 0 ? lines : null;
    }

    /// <summary>Commit the pending clip (Hammer: Enter). No-op if no line is set.</summary>
    public void Apply()
    {
        if (!_hasLine) return;
        PerformClip(_lineAxis);
        _hasLine = false;
        _dragVp?.RequestRedrawAll();   // clear the 3D cut preview too once the clip commits
    }

    /// <summary>Discard the pending clip line without cutting (Esc).</summary>
    public void Cancel()
    {
        var vp = _dragVp;
        _dragging = _hasLine = false; _dragVp = null;
        vp?.RequestRedrawAll();   // clear the 3D cut preview on cancel
    }

    public void OnKeyDown(GLViewport vp, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { Apply(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { Cancel(); e.Handled = true; }
    }

    private void PerformClip(ViewAxis axis)
    {
        var p1 = new Vector2(_h1, _v1);
        var p2 = new Vector2(_h2, _v2);
        if ((p2 - p1).LengthSquared < 1f) return;        // no line drawn

        var plane = BuildPlane(axis, p1, p2);
        // Only cut brushes the plane actually passes THROUGH (both halves valid); a brush entirely on
        // one side is left intact. This makes a slice across a scene affect just the crossed brushes.
        var targets = TargetSolids().Where(s => { var (f, b) = s.Split(plane); return f != null && b != null; }).ToList();
        if (targets.Count == 0) return;

        _doc.RecordUndo();
        foreach (var s in targets)
        {
            var (front, back) = s.Split(plane);
            var keep = new List<Solid>();
            if (KeepMode != Keep.Back  && front != null) keep.Add(front);
            if (KeepMode != Keep.Front && back  != null) keep.Add(back);
            foreach (var k in keep) k.IsSelected = true;
            _doc.ReplaceSolid(s, keep);
        }
    }

    // The brushes the clip acts on: the current selection, or — when nothing is selected — every solid
    // (so a freshly-drawn slice cuts whatever it crosses, rather than silently doing nothing, which read
    // as "the slice tool doesn't work"). Hammer requires a selection; this is the more forgiving default.
    private IEnumerable<Solid> TargetSolids()
    {
        var sel = _doc.Solids.Where(s => s.IsSelected).ToList();
        return sel.Count > 0 ? sel : _doc.Solids;
    }

    // Builds the cutting plane: it contains the drawn line and extends along the view's
    // depth axis, so its normal lies in the view plane perpendicular to the line.
    private static Plane3D BuildPlane(ViewAxis axis, Vector2 a, Vector2 b)
    {
        var (hDir, vDir, dDir) = Bases(axis);
        Vector3 p1 = a.X * hDir + a.Y * vDir;
        Vector3 line = (b.X - a.X) * hDir + (b.Y - a.Y) * vDir;
        Vector3 n = Vector3.Normalize(Vector3.Cross(line, dDir));
        return new Plane3D(n, Vector3.Dot(n, p1));
    }

    private static (Vector3 h, Vector3 v, Vector3 d) Bases(ViewAxis axis) => axis switch
    {
        ViewAxis.Top   => (new(1, 0, 0), new(0, 0, -1), new(0, 1, 0)),
        ViewAxis.Front => (new(1, 0, 0), new(0, 1, 0),  new(0, 0, 1)),
        ViewAxis.Side  => (new(0, 0, 1), new(0, 1, 0),  new(1, 0, 0)),
        _              => (new(1, 0, 0), new(0, 1, 0),  new(0, 0, 1)),
    };

    private static (float h, float v) ScreenToOrtho(int sx, int sy, int w, int h, Camera2D cam)
        => (cam.PanX + (sx - w * 0.5f) * cam.Zoom, cam.PanY - (sy - h * 0.5f) * cam.Zoom);

    // Snap to the SAME adaptive grid every other tool uses: GridSnap.ActiveStep coarsens the step as you
    // zoom out so you snap to the grid lines actually on screen (and respects the Snap-to-grid toggle / Ctrl
    // suspend). Previously the clip line snapped to the raw grid size, so when zoomed out to see the whole
    // level its 64u snap points were sub-pixel and unreachable — the "limited range" of where you could cut.
    private static float Snap(GLViewport vp, float x)
    {
        float zoom = vp.ActiveCamera2D?.Zoom ?? 1f;
        return Editor.GridSnap.Snap(x, Editor.GridSnap.ActiveStep(vp.GridSize, zoom));
    }
}
