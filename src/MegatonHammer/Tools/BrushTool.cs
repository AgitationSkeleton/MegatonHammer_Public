using MegatonHammer.Editor;
using MegatonHammer.Forms;
using MegatonHammer.Rendering;
using OpenTK.Mathematics;

namespace MegatonHammer.Tools;

/// <summary>
/// Block / brush tool, matching Valve Hammer's flow: drag a box in a 2D view to DEFINE it, then it
/// waits as a preview (shown in every view) — nothing is created yet. Press Enter to commit the box
/// to a real brush, Esc to discard, or just drag again to redefine it.
/// </summary>
public sealed class BrushTool : ITool
{
    private readonly MapDocument _doc;

    private bool _dragging;        // actively dragging out the box
    private bool _hasBox;          // a box is defined and awaiting confirmation (Enter)
    private bool _resizing;        // dragging a resize handle of the pending box
    private int _grabCol, _grabRow;   // grabbed handle: 0=lo, 1=mid, 2=hi (per ortho axis)
    private ViewAxis _resizeAxis;     // the view the handle is being dragged in
    private Vector3 _worldStart, _worldEnd;
    private ViewAxis _dragAxis;
    private Vector3 _min, _max;    // the pending box bounds (valid while _dragging || _hasBox)

    // Last committed brush's bounds. A new brush reuses the LAST brush's off-plane (3rd-axis) extent + the
    // same position on that axis, so consecutive brushes keep a CONSISTENT height/depth (Hammer:
    // CToolBlock::OnMouseMove2D → Selection::GetLastValidBounds + axThird). Null until the first commit.
    private Vector3? _lastMin, _lastMax;
    private GLViewport? _dragVp;
    private bool _moving;          // dragging the whole pending box (clicked inside it)
    private Vector3 _moveAnchor, _moveBoxMin, _moveBoxMax;

    private const float HandlePixels = 7f;

    public string Name => "Brush";

    /// <summary>Supplies the active paint texture, applied to every face of a newly-created brush.</summary>
    public Func<string?>? ActiveTextureProvider { get; set; }

    /// <summary>A box is being dragged or is waiting for Enter (used to draw the preview).</summary>
    public bool HasPendingBox => _dragging || _hasBox;
    public bool IsDragging => _dragging;

    public BrushTool(MapDocument doc) { _doc = doc; }

    public void OnMouseDown(GLViewport vp, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || vp.ViewportType == ViewportType.Perspective3D) return;
        var cam = vp.ActiveCamera2D!;

        // A pending box can be fine-tuned by dragging its resize handles (Hammer-style) before Enter.
        if (_hasBox && TryHitHandle(vp, e.X, e.Y, out int col, out int row))
        {
            _resizing = true; _grabCol = col; _grabRow = row; _resizeAxis = cam.Axis; _dragVp = vp;
            return;
        }

        // Clicking INSIDE a pending box (not on a handle) moves the whole box, Hammer-style,
        // rather than starting a new one.
        if (_hasBox && PointInBox(vp, e.X, e.Y))
        {
            int gm = GridSnap.ActiveStep(vp.GridSize, cam.Zoom);
            _moving = true; _dragVp = vp;
            _moveAnchor = SnapVec(ScreenToWorld(e.X, e.Y, vp.Width, vp.Height, cam), gm);
            _moveBoxMin = _min; _moveBoxMax = _max;
            return;
        }

        int g = GridSnap.ActiveStep(vp.GridSize, cam.Zoom);
        _worldStart = SnapVec(ScreenToWorld(e.X, e.Y, vp.Width, vp.Height, cam), g);
        _worldEnd   = _worldStart;
        _dragAxis   = cam.Axis;
        _dragging   = true;
        _hasBox     = false;
        _dragVp     = vp;
        ComputeBox(g);
        InvalidateAll(vp);
    }

    public void OnMouseMove(GLViewport vp, MouseEventArgs e)
    {
        if (_resizing && vp == _dragVp) { ResizeTo(vp, e.X, e.Y); return; }
        if (_moving   && vp == _dragVp) { MoveTo(vp, e.X, e.Y);   return; }
        if (!_dragging || vp != _dragVp) return;
        int g = GridSnap.ActiveStep(vp.GridSize, vp.ActiveCamera2D!.Zoom);
        _worldEnd = SnapVec(ScreenToWorld(e.X, e.Y, vp.Width, vp.Height, vp.ActiveCamera2D!), g);
        ComputeBox(g);
        InvalidateAll(vp);
    }

    public void OnMouseUp(GLViewport vp, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_resizing) { _resizing = false; InvalidateAll(vp); return; }
        if (_moving)   { _moving   = false; InvalidateAll(vp); return; }
        if (!_dragging) return;
        _dragging = false;
        // The box now waits for Enter (Hammer's confirmation step). Drop a degenerate drag.
        _hasBox = (_max.X - _min.X) >= 1f && (_max.Y - _min.Y) >= 1f && (_max.Z - _min.Z) >= 1f;
        InvalidateAll(vp);
    }

    // ── Resize handles (Hammer adjusts the box before committing) ───────────

    /// <summary>The 8 resize-handle centres (ortho space) for a view, or null when no box is pending.</summary>
    public List<(float h, float v)>? GetHandles(GLViewport vp)
    {
        if (!_hasBox || _dragging || vp.ViewportType == ViewportType.Perspective3D || vp.ActiveCamera2D == null)
            return null;
        var (loH, loV, hiH, hiV) = BoxRect(vp.ActiveCamera2D.Axis);
        float mH = (loH + hiH) * 0.5f, mV = (loV + hiV) * 0.5f;
        return
        [
            (loH, loV), (mH, loV), (hiH, loV),
            (loH, mV),             (hiH, mV),
            (loH, hiV), (mH, hiV), (hiH, hiV),
        ];
    }

    private bool TryHitHandle(GLViewport vp, int sx, int sy, out int col, out int row)
    {
        col = row = 1;
        var cam = vp.ActiveCamera2D;
        if (cam == null) return false;
        var mw = ScreenToWorld(sx, sy, vp.Width, vp.Height, cam);
        float mh = OrthoH(mw, cam.Axis), mv = OrthoV(mw, cam.Axis);
        var (loH, loV, hiH, hiV) = BoxRect(cam.Axis);
        float[] hs = [loH, (loH + hiH) * 0.5f, hiH];
        float[] vs = [loV, (loV + hiV) * 0.5f, hiV];
        float r = HandlePixels * cam.Zoom * 1.6f;
        for (int c = 0; c < 3; c++)
            for (int rr = 0; rr < 3; rr++)
            {
                if (c == 1 && rr == 1) continue;     // centre is not a handle
                if (MathF.Abs(mh - hs[c]) <= r && MathF.Abs(mv - vs[rr]) <= r) { col = c; row = rr; return true; }
            }
        return false;
    }

    // True when the screen point falls within the pending box's footprint in this view's plane.
    private bool PointInBox(GLViewport vp, int sx, int sy)
    {
        var cam = vp.ActiveCamera2D;
        if (cam == null) return false;
        var mw = ScreenToWorld(sx, sy, vp.Width, vp.Height, cam);
        float mh = OrthoH(mw, cam.Axis), mv = OrthoV(mw, cam.Axis);
        var (loH, loV, hiH, hiV) = BoxRect(cam.Axis);
        return mh >= loH && mh <= hiH && mv >= loV && mv <= hiV;
    }

    // Translates the whole pending box by the snapped drag delta (off-plane axis preserved).
    private void MoveTo(GLViewport vp, int sx, int sy)
    {
        int g = GridSnap.ActiveStep(vp.GridSize, vp.ActiveCamera2D!.Zoom);
        var cur = SnapVec(ScreenToWorld(sx, sy, vp.Width, vp.Height, vp.ActiveCamera2D!), g);
        var delta = cur - _moveAnchor;
        _min = _moveBoxMin + delta;
        _max = _moveBoxMax + delta;
        InvalidateAll(vp);
    }

    private void ResizeTo(GLViewport vp, int sx, int sy)
    {
        int g = GridSnap.ActiveStep(vp.GridSize, vp.ActiveCamera2D!.Zoom);
        var mw = SnapVec(ScreenToWorld(sx, sy, vp.Width, vp.Height, vp.ActiveCamera2D!), g);
        float mh = OrthoH(mw, _resizeAxis), mv = OrthoV(mw, _resizeAxis);
        var (loH, loV, hiH, hiV) = BoxRect(_resizeAxis);

        if (_grabCol == 0) loH = mh; else if (_grabCol == 2) hiH = mh;
        if (_grabRow == 0) loV = mv; else if (_grabRow == 2) hiV = mv;

        // Keep lo ≤ hi (flip the grabbed side if dragged past the anchor).
        if (loH > hiH) { (loH, hiH) = (hiH, loH); _grabCol = 2 - _grabCol; }
        if (loV > hiV) { (loV, hiV) = (hiV, loV); _grabRow = 2 - _grabRow; }
        SetBoxFromRect(_resizeAxis, loH, loV, hiH, hiV);
        InvalidateAll(vp);
    }

    // Box ↔ ortho-rect for a view's plane (the off-plane axis is preserved).
    private (float loH, float loV, float hiH, float hiV) BoxRect(ViewAxis axis)
    {
        float h1 = OrthoH(_min, axis), v1 = OrthoV(_min, axis), h2 = OrthoH(_max, axis), v2 = OrthoV(_max, axis);
        return (MathF.Min(h1, h2), MathF.Min(v1, v2), MathF.Max(h1, h2), MathF.Max(v1, v2));
    }

    private void SetBoxFromRect(ViewAxis axis, float loH, float loV, float hiH, float hiV)
    {
        switch (axis)
        {
            case ViewAxis.Top:   _min.X = loH; _max.X = hiH; _min.Z = -hiV; _max.Z = -loV; break;  // v = -Z
            case ViewAxis.Front: _min.X = loH; _max.X = hiH; _min.Y = loV;  _max.Y = hiV;  break;
            case ViewAxis.Side:  _min.Z = loH; _max.Z = hiH; _min.Y = loV;  _max.Y = hiV;  break;
        }
    }

    /// <summary>Hammer Block-tool primitive shapes (the convex ones that map to a single brush).</summary>
    public enum BlockShape { Block, Wedge, Cylinder, Spike, Sphere }
    /// <summary>Shape the next committed brush is built as. Set from the Tools ▸ Block Shape menu.</summary>
    public BlockShape Shape { get; set; } = BlockShape.Block;
    /// <summary>Sides for round shapes (cylinder/spike/sphere).</summary>
    public int Sides { get; set; } = 12;

    /// <summary>Commit the pending box to a real brush (Hammer's Enter). No-op if none.</summary>
    public void Commit()
    {
        if (!_hasBox) return;
        _doc.RecordUndo();
        var box = BuildShape(Shape, _min, _max, Sides);
        // New brushes adopt the active paint texture on all six faces (Hammer behaviour).
        var tex = ActiveTextureProvider?.Invoke();
        if (!string.IsNullOrEmpty(tex))
            foreach (var f in box.Faces) f.TextureName = tex;
        // Hammer selects the brush it just created, so its dimensions show and it's ready to edit.
        _doc.ClearSelection();
        box.IsSelected = true;
        _doc.Add(box);
        _lastMin = _min; _lastMax = _max;   // next brush reuses this brush's off-axis extent (Hammer)
        _hasBox = false;
    }

    // Builds the chosen primitive inside the bounding box. The round/sloped shapes are produced as the
    // convex hull of a generated point set (Solid.RebuildFromPoints), so they're always valid brushes.
    private static Solid BuildShape(BlockShape shape, Vector3 min, Vector3 max, int sides)
    {
        if (shape == BlockShape.Block) return Solid.CreateBox(min, max);

        float cx = (min.X + max.X) * 0.5f, cz = (min.Z + max.Z) * 0.5f;
        float rx = (max.X - min.X) * 0.5f, rz = (max.Z - min.Z) * 0.5f;
        int n = Math.Clamp(sides, 3, 64);
        var pts = new List<Vector3>();

        switch (shape)
        {
            case BlockShape.Wedge:   // triangular prism / ramp rising along +X, full Z depth
                pts.Add(new(min.X, min.Y, min.Z)); pts.Add(new(max.X, min.Y, min.Z));
                pts.Add(new(max.X, min.Y, max.Z)); pts.Add(new(min.X, min.Y, max.Z));
                pts.Add(new(max.X, max.Y, min.Z)); pts.Add(new(max.X, max.Y, max.Z));
                break;

            case BlockShape.Cylinder:   // n-gon prism inscribed in the footprint ellipse
                for (int k = 0; k < n; k++)
                {
                    float a = MathF.Tau * k / n;
                    float px = cx + rx * MathF.Cos(a), pz = cz + rz * MathF.Sin(a);
                    pts.Add(new(px, min.Y, pz)); pts.Add(new(px, max.Y, pz));
                }
                break;

            case BlockShape.Spike:      // n-gon pyramid: base ring + apex at top centre
                for (int k = 0; k < n; k++)
                {
                    float a = MathF.Tau * k / n;
                    pts.Add(new(cx + rx * MathF.Cos(a), min.Y, cz + rz * MathF.Sin(a)));
                }
                pts.Add(new(cx, max.Y, cz));
                break;

            case BlockShape.Sphere:     // ellipsoid sampled in lat/long rings, hulled to a polyhedron
                float ry = (max.Y - min.Y) * 0.5f, cy = (min.Y + max.Y) * 0.5f;
                int rings = Math.Clamp(n / 2, 2, 16);
                for (int r = 1; r < rings; r++)
                {
                    float lat = MathF.PI * (r / (float)rings - 0.5f);
                    float y = cy + ry * MathF.Sin(lat), cr = MathF.Cos(lat);
                    for (int k = 0; k < n; k++)
                    {
                        float a = MathF.Tau * k / n;
                        pts.Add(new(cx + rx * cr * MathF.Cos(a), y, cz + rz * cr * MathF.Sin(a)));
                    }
                }
                pts.Add(new(cx, min.Y, cz)); pts.Add(new(cx, max.Y, cz));   // poles
                break;
        }

        var s = new Solid();
        s.RebuildFromPoints(pts);
        return s;
    }

    /// <summary>Discard the pending box without creating anything (Esc).</summary>
    public void Cancel()
    {
        var vp = _dragVp;
        _dragging = _hasBox = _moving = _resizing = false;
        if (vp != null) InvalidateAll(vp);
    }

    public void OnKeyDown(GLViewport vp, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { Commit(); e.Handled = true; InvalidateAll(vp); }
        else if (e.KeyCode == Keys.Escape) { Cancel(); e.Handled = true; }
    }

    // ── Preview accessors (used by the viewports) ──────────────────────────

    /// <summary>The pending box projected into a 2D view's plane, for the rubber-band preview.</summary>
    public (float h1, float v1, float h2, float v2) GetBox2D(ViewAxis axis) =>
        (OrthoH(_min, axis), OrthoV(_min, axis), OrthoH(_max, axis), OrthoV(_max, axis));

    /// <summary>The pending box bounds, for the 3D wireframe preview.</summary>
    public (Vector3 min, Vector3 max) GetBox3D() => (_min, _max);

    // Back-compat for the old rubber-band call site (active drag viewport only).
    public bool IsActiveDragViewport(GLViewport vp) => _dragging && vp == _dragVp;
    public (float h1, float v1, float h2, float v2) GetRubberBand(GLViewport vp) =>
        vp.ActiveCamera2D == null ? (0, 0, 0, 0) : GetBox2D(vp.ActiveCamera2D.Axis);

    // ── Box construction ───────────────────────────────────────────────────

    private void ComputeBox(int gridSize)
    {
        var ws = _worldStart; var we = _worldEnd; var axis = _dragAxis;
        // Off-plane (3rd-axis) extent — the dimension the 2D drag can't set. Hammer reuses the LAST brush's
        // extent on that axis (Selection::GetLastValidBounds + axThird), so CONSECUTIVE brushes keep a
        // consistent height/depth AND the same position on that axis (e.g. a row of floor brushes all stay
        // the same height). Only the very FIRST brush (no prior) falls back to the drawn footprint's larger
        // side, so a lone square still makes a cube rather than an arbitrary slab.
        float Span(float a, float b) => MathF.Abs(a - b);
        (float lo, float hi) OffAxis(float lastLo, float lastHi)
        {
            if (_lastMin is not null && _lastMax is not null) return (lastLo, lastHi);
            float footprint = axis switch
            {
                ViewAxis.Top   => MathF.Max(Span(ws.X, we.X), Span(ws.Z, we.Z)),
                ViewAxis.Front => MathF.Max(Span(ws.X, we.X), Span(ws.Y, we.Y)),
                _              => MathF.Max(Span(ws.Z, we.Z), Span(ws.Y, we.Y)),
            };
            float d = MathF.Max(gridSize, MathF.Round(footprint / gridSize) * gridSize);
            return (-d * 0.5f, d * 0.5f);
        }

        switch (axis)
        {
            case ViewAxis.Top:   // off-axis = Y (height)
            {
                var (lo, hi) = OffAxis(_lastMin?.Y ?? 0, _lastMax?.Y ?? 0);
                _min = new(MathF.Min(ws.X, we.X), lo, MathF.Min(ws.Z, we.Z));
                _max = new(MathF.Max(ws.X, we.X), hi, MathF.Max(ws.Z, we.Z));
                break;
            }
            case ViewAxis.Front: // off-axis = Z (depth)
            {
                var (lo, hi) = OffAxis(_lastMin?.Z ?? 0, _lastMax?.Z ?? 0);
                _min = new(MathF.Min(ws.X, we.X), MathF.Min(ws.Y, we.Y), lo);
                _max = new(MathF.Max(ws.X, we.X), MathF.Max(ws.Y, we.Y), hi);
                break;
            }
            case ViewAxis.Side:  // off-axis = X
            {
                var (lo, hi) = OffAxis(_lastMin?.X ?? 0, _lastMax?.X ?? 0);
                _min = new(lo, MathF.Min(ws.Y, we.Y), MathF.Min(ws.Z, we.Z));
                _max = new(hi, MathF.Max(ws.Y, we.Y), MathF.Max(ws.Z, we.Z));
                break;
            }
        }
    }

    // The render timer redraws every viewport each frame, so a single nudge is enough.
    private static void InvalidateAll(GLViewport vp) => vp.Invalidate();

    private static float OrthoH(Vector3 w, ViewAxis axis) => axis switch
    {
        ViewAxis.Top => w.X, ViewAxis.Front => w.X, ViewAxis.Side => w.Z, _ => 0
    };
    private static float OrthoV(Vector3 w, ViewAxis axis) => axis switch
    {
        ViewAxis.Top => -w.Z, ViewAxis.Front => w.Y, ViewAxis.Side => w.Y, _ => 0
    };

    private static Vector3 ScreenToWorld(int sx, int sy, int w, int h, Camera2D cam)
    {
        float oh = cam.PanX + (sx - w * 0.5f) * cam.Zoom;
        float ov = cam.PanY - (sy - h * 0.5f) * cam.Zoom;
        return cam.Axis switch
        {
            ViewAxis.Top   => new(oh, 0, -ov),
            ViewAxis.Front => new(oh, ov, 0),
            ViewAxis.Side  => new(0, ov, oh),
            _              => Vector3.Zero
        };
    }

    private static Vector3 SnapVec(Vector3 v, int g)
    {
        if (g < 1) return v;
        float gf = g;
        return new(MathF.Round(v.X / gf) * gf, MathF.Round(v.Y / gf) * gf, MathF.Round(v.Z / gf) * gf);
    }
}
