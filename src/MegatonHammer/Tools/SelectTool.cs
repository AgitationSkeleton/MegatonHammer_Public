using MegatonHammer.Editor;
using MegatonHammer.Forms;
using MegatonHammer.Rendering;
using OpenTK.Mathematics;

namespace MegatonHammer.Tools;

/// <summary>
/// Selection + transform tool. Picks brushes/actors in the 2D views and, when a
/// brush selection exists, exposes 8 resize handles (4 corners + 4 edge midpoints)
/// that stretch the selection along one axis (edges) or two axes (corners), Hammer-style.
/// </summary>
public sealed class SelectTool : ITool
{
    /// <summary>Hammer's selection handle modes, cycled by clicking the selected object.</summary>
    public enum SelectMode { Scale, Rotate, Skew }

    private readonly MapDocument _doc;

    public string Name => "Select";

    /// <summary>#1: raised with the clicked face's texture when a brush face is picked in the 3D view, so an
    /// open Replace Textures dialog can fill its "Find texture" field even with the (default) Select tool.</summary>
    public event Action<string?>? FaceClickedTexture;

    private const float HandlePixels = 7f;   // grab radius / draw size in screen pixels

    private SelectMode _mode = SelectMode.Scale;
    /// <summary>Current handle mode (Scale → Rotate → Skew, cycled by re-clicking the selection).</summary>
    public SelectMode Mode => _mode;
    /// <summary>Reset to Scale mode — e.g. after a fresh paste, so the new selection is ready to manipulate
    /// (and a click cycles to Rotate), matching Hammer.</summary>
    public void ResetToScale() => _mode = SelectMode.Scale;

    // ── Active resize-drag state ──────────────────────────────────────────
    private bool        _resizing, _rotating, _skewing;
    private GLViewport? _dragVp;
    private int         _grabCol, _grabRow;          // 0=min,1=mid,2=max
    private float       _anchorH, _anchorV;          // fixed opposite-handle ortho coords
    private float       _grabOrigH, _grabOrigV;      // grabbed handle ortho coords at grab time
    private Vector3     _origMin, _origMax;          // selection world AABB at grab time
    private float       _centerH, _centerV;          // selection centre (rotate pivot)
    private float       _rotStartAngle;
    private readonly Dictionary<Solid, Plane3D[]> _snapshots = [];
    // Original per-face texture axes AND shift (by plane index) captured at drag start, so a rotate/skew
    // can map the ORIGINALS by the absolute transform each frame (texture lock, drift-free like the
    // planes). The shift is needed too: texture lock must also re-offset so the texture stays pinned. #24
    private readonly Dictionary<Solid, Dictionary<int, (Vector3 u, Vector3 v, float shiftS, float shiftT)>> _texSnap = [];
    // Selected actors' pose at rotate-grab time (position + binary-angle rotations), so rotation can
    // spin entities about the selection pivot like brushes (Hammer rotates entity angles too).
    private readonly Dictionary<ZActor, (Vector3 pos, short rx, short ry, short rz)> _rotActors = [];

    // ── Active body-move-drag state ───────────────────────────────────────
    private bool  _moving;
    private float _moveStartH, _moveStartV;
    private readonly Dictionary<Solid, Plane3D[]> _moveSnap = [];
    // Baseline per-face texture SHIFT (by plane index) at move start. Each drag frame does RestorePlanes +
    // Translate(cumulativeDelta) for drift-free geometry, but ComputeFaces CARRIES the (already-shifted)
    // texture offset and Translate re-applies the texture-lock adjustment on top of it → the offset COMPOUNDS
    // every frame and the texture visibly slides ("shifts constantly"), obvious on centred/edge-aligned faces.
    // Restore this baseline before each Translate so the lock offset is applied exactly once from the origin.
    private readonly Dictionary<Solid, Dictionary<int, (float s, float t)>> _moveTexSnap = [];
    private readonly Dictionary<ZActor, Vector3>  _moveActors = [];   // actor → position at grab time
    private bool    _moving3D;          // dragging actor(s) on the ground plane in the 3D view
    private Vector3 _move3DAnchor;      // world point under the cursor at grab (on the drag plane)
    private float   _move3DY;           // height of the drag plane
    private bool    _cloneDrag;         // Shift-drag a selected object → clone it (Hammer); set at mousedown
    private bool    _cloneDone;         // the clone has been spawned for the current drag

    private bool _undoRecorded;   // captured once per drag, on the first real change
    private bool _pendingCycle;   // a click (no drag) on the already-selected body cycles the mode
    private bool _moved;          // the current drag has moved enough to count as a drag, not a click

    // ── Marquee (rubber-band) box select in 2D ────────────────────────────
    private bool  _marquee;                                   // dragging an empty-space selection box
    private float _marqStartH, _marqStartV, _marqCurH, _marqCurV;
    private bool  _marqAdditive;                              // Ctrl/Shift: add to the existing selection

    // ── Active decal move/resize (2D) ─────────────────────────────────────
    private Decal?  _dragDecal;        // decal being moved (body) or resized (corner) in a 2D view
    private bool    _decalResize;      // grabbed a corner → resize its U/V extents; else move the body
    private ViewAxis _decalAxis;
    private float   _decalStartH, _decalStartV;   // ortho grab point (move delta)
    private Vector3 _decalStartPos;               // decal position at grab

    // ── Active decal transform via the Hammer selection handles (2D, decals-only selection) ──
    private bool _decalHandleScale, _decalHandleRotate;   // dragging a handle to scale / rotate selected decals
    // Snapshot of each selected decal at grab time (size + rotation + position), so a scale/rotate maps the
    // ORIGINALS by the absolute transform each frame (drift-free, like the brush snapshots).
    private readonly Dictionary<Decal, (float su, float sv, float rot, Vector3 pos)> _decalSnap = [];

    // ── Active decal move in the 3D view ──────────────────────────────────
    private Decal?  _dragDecal3D;         // decal being slid along its own plane in the 3D view
    private Vector3 _decal3DGrabOffset;   // (rayHit - Position) at grab, so the decal follows the cursor
    private Vector3 _decal3DPlanePoint;   // a fixed point on the decal's plane (its position at grab)

    public SelectTool(MapDocument doc) { _doc = doc; }

    public bool IsResizing => _resizing || _rotating || _skewing;

    // ── Input ──────────────────────────────────────────────────────────────

    public void OnMouseDown(GLViewport vp, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _moved = false; _pendingCycle = false; _cloneDrag = false; _cloneDone = false;
        bool shiftDown = (Control.ModifierKeys & Keys.Shift) != 0;

        // ── 3D viewport: ray-pick an actor (priority) or brush ──────────────
        if (vp.ViewportType == ViewportType.Perspective3D)
        {
            var cam3 = vp.ActiveCamera3D;
            if (cam3 == null) return;
            var ray = Picking.RayFromScreen(cam3, e.X, e.Y, vp.Width, vp.Height);

            bool add3d = (Control.ModifierKeys & (Keys.Control | Keys.Shift)) != 0;
            // Pick by the actor's model footprint (its world AABB), not a fixed origin handle.
            var actor = Picking.PickActor(_doc.PickableActors, ray, vp.Resolver, adult: true);
            if (actor != null)
            {
                if (add3d) actor.IsSelected = !actor.IsSelected;
                else if (!actor.IsSelected) { _doc.ClearSelection(); actor.IsSelected = true; _doc.SelectGroup(actor.GroupId); }
                // Dragging a selected actor in 3D moves it along its ground plane (Hammer entity move).
                if (actor.IsSelected) Begin3DMove(vp, cam3, e.X, e.Y, actor.YPos);
                _doc.NotifyChanged();
                return;
            }

            bool haveFace = Picking.PickFace(_doc.Scene, ray, out var hit);
            // Decal (sticker overlay) pick — a decal floats on top of its wall, so it wins when the ray hits it
            // nearer than the surface behind. Lets you click the sticker in the 3D view and drag to slide it
            // along its plane; resize via the corner handles in the face-on 2D view (same as a brush).
            bool haveDecal = Picking.PickDecal(_doc.VisibleDecals, ray, out var pd3, out var pdPoint, out var pdDist);
            if (haveDecal && (!haveFace || pdDist <= hit.Distance + 0.5f))
            {
                if (add3d) pd3.IsSelected = !pd3.IsSelected;
                else if (!pd3.IsSelected) { _doc.ClearSelection(); pd3.IsSelected = true; }
                if (pd3.IsSelected)
                {
                    _dragDecal3D = pd3; _decal3DGrabOffset = pdPoint - pd3.Position;
                    _decal3DPlanePoint = pd3.Position; _dragVp = vp; _undoRecorded = false;
                }
                _doc.NotifyChanged();
                return;
            }

            if (haveFace)
            {
                FaceClickedTexture?.Invoke(hit.Face.TextureName);   // #1: feed the Replace Textures "Find" field
                if (add3d) hit.Solid.IsSelected = !hit.Solid.IsSelected;
                else { _doc.ClearSelection(); hit.Solid.IsSelected = true; _doc.SelectGroup(hit.Solid.GroupId); }
                // Dragging a selected brush in 3D moves the whole selection on the grabbed face's plane
                // height (Hammer 3D brush move), consistent with the actor 3D move above.
                if (hit.Solid.IsSelected) Begin3DMove(vp, cam3, e.X, e.Y, hit.Point.Y);
            }
            else if (!add3d) _doc.ClearSelection();
            _doc.NotifyChanged();
            return;
        }

        var cam = vp.ActiveCamera2D!;
        var (oh, ov) = ScreenToOrtho(e.X, e.Y, vp.Width, vp.Height, cam);

        // ── Try grabbing a handle on the current selection (mode-specific) ──
        if (TryGetSelectionRect(cam.Axis, vp.Resolver, out var rect) &&
            TryHitHandle(rect, oh, ov, cam.Zoom, out _grabCol, out _grabRow))
        {
            // A decals-ONLY selection routes the handles to the decal transform (resize its U/V extents in
            // Scale/Skew, spin its facing in Rotate), so a sticker overlay has the same handle interactivity
            // as a brush. Mixed selections keep the brush/actor transform (decals ride along on body moves).
            if (DecalsOnlySelection())
            {
                if (_mode == SelectMode.Rotate) BeginDecalRotate(vp, rect, oh, ov);
                else                            BeginDecalScale(vp, rect);
                return;
            }
            switch (_mode)
            {
                case SelectMode.Rotate: BeginRotate(vp, rect, oh, ov); break;
                case SelectMode.Skew:   BeginSkew(vp, rect); break;
                default:                BeginResize(vp, rect); break;
            }
            return;
        }

        // ── Decal pick (2D): decals sit on top of the geometry — grab a corner to resize its U/V extents,
        // grab the body to move it. Works in whichever view the decal is face-on to. ──
        if (TryPickDecal2D(cam, oh, ov, out var pd, out bool pdResize))
        {
            bool addD = (Control.ModifierKeys & (Keys.Control | Keys.Shift)) != 0;
            bool wasSelected = pd.IsSelected;
            if (addD) pd.IsSelected = !pd.IsSelected;
            else if (!pd.IsSelected) { _doc.ClearSelection(); pd.IsSelected = true; _mode = SelectMode.Scale; }
            if (pd.IsSelected)
            {
                _dragDecal = pd; _decalResize = pdResize; _decalAxis = cam.Axis; _dragVp = vp;
                _decalStartH = oh; _decalStartV = ov; _decalStartPos = pd.Position; _undoRecorded = false;
                // Clicking (no drag) the body of an already-selected decal cycles the handle mode, like a brush.
                _pendingCycle = !pdResize && wasSelected && !addD;
            }
            _doc.NotifyChanged();
            return;
        }

        // ── Otherwise perform normal picking ──────────────────────────────
        // Actor hit-test (priority): pick by the actor's projected model footprint (its world AABB),
        // choosing the tightest box under the cursor. A minimum pixel size keeps tiny/no-model actors
        // clickable when zoomed out.
        var res = vp.Resolver;
        float minHalf = MathF.Max(Picking.DefaultActorHalf, 8f * cam.Zoom);
        ZActor? hitActor = null;
        float   bestArea = float.MaxValue;
        foreach (var actor in _doc.PickableActors)
        {
            var (mn, mx) = Picking.ActorBounds(actor, res, true);
            var (sh, sv, eh, ev) = Project2DAABB(mn, mx, cam.Axis);
            // Pad to the minimum clickable footprint around the actor's projected centre.
            var (ch, cv) = OrthoPos(actor.Position, cam.Axis);
            if (eh - sh < 2 * minHalf) { sh = MathF.Min(sh, ch - minHalf); eh = MathF.Max(eh, ch + minHalf); }
            if (ev - sv < 2 * minHalf) { sv = MathF.Min(sv, cv - minHalf); ev = MathF.Max(ev, cv + minHalf); }
            if (oh >= sh && oh <= eh && ov >= sv && ov <= ev)
            {
                float area = (eh - sh) * (ev - sv);
                if (area < bestArea) { bestArea = area; hitActor = actor; }
            }
        }
        // Ctrl or Shift = additive/toggle selection (Hammer multi-select); plain click replaces.
        bool additive = (Control.ModifierKeys & (Keys.Control | Keys.Shift)) != 0;

        if (hitActor != null)
        {
            // Shift-drag of an already-selected actor clones it (Hammer); otherwise Ctrl/Shift toggle.
            bool cloneIntent = shiftDown && hitActor.IsSelected && !hitActor.IsEditorOnly;
            _cloneDrag = cloneIntent;
            if (cloneIntent) { /* keep current selection; the clone is spawned once the drag begins */ }
            else if (additive) hitActor.IsSelected = !hitActor.IsSelected;
            else if (!hitActor.IsSelected) { _doc.ClearSelection(); hitActor.IsSelected = true; _doc.SelectGroup(hitActor.GroupId); _mode = SelectMode.Scale; }
            // Clicking an already-selected actor (no drag) cycles Scale → Rotate → Skew, like a brush,
            // so entities can be rotated with the handle. A drag instead moves it (Hammer entity move).
            else _pendingCycle = true;
            if (hitActor.IsSelected) BeginMove(vp, oh, ov);
            _doc.NotifyChanged();
            return;
        }

        // Brush AABB hit-test — clicking the body selects and arms a move-drag.
        // #1: among all brushes whose 2D box contains the click, prefer the SMALLEST (inner-most) — Hammer's
        // depth priority — so a small brush overlapping a larger one is easy to grab, not the big one behind.
        Solid? hitSolid = null;
        float bestBrushArea = float.MaxValue;
        Solid? hitSelected = null;
        float bestSelArea = float.MaxValue;
        foreach (var solid in _doc.Solids)
        {
            var (mn, mx) = solid.GetAABB();
            var (sh, sv, eh, ev) = Project2DAABB(mn, mx, cam.Axis);
            if (oh >= sh && oh <= eh && ov >= sv && ov <= ev)
            {
                float area = MathF.Max(eh - sh, 0.001f) * MathF.Max(ev - sv, 0.001f);
                if (area < bestBrushArea) { bestBrushArea = area; hitSolid = solid; }
                if (solid.IsSelected && area < bestSelArea) { bestSelArea = area; hitSelected = solid; }
            }
        }
        // Hammer priority: a plain click that lands anywhere on an ALREADY-SELECTED brush grabs THAT brush, so
        // you drag your current selection instead of accidentally snatching a smaller overlapping brush behind
        // it. (Additive/shift-toggle still targets the smallest brush so you can pick a specific overlapper.)
        if (hitSelected != null && !additive) hitSolid = hitSelected;

        if (hitSolid != null)
        {
            if (shiftDown && hitSolid.IsSelected)
            {
                // Shift-drag of an already-selected brush clones it (Hammer); selection unchanged.
                _mode = SelectMode.Scale;
                _cloneDrag = true;
                BeginMove(vp, oh, ov);
            }
            else if (additive)
            {
                // Toggle this brush in/out of the selection without disturbing the rest.
                hitSolid.IsSelected = !hitSolid.IsSelected;
                _mode = SelectMode.Scale;
                if (hitSolid.IsSelected) BeginMove(vp, oh, ov);
            }
            else if (!hitSolid.IsSelected)
            {
                // Selecting a new brush starts in Scale mode (Hammer).
                _doc.ClearSelection(); hitSolid.IsSelected = true; _doc.SelectGroup(hitSolid.GroupId); _mode = SelectMode.Scale;
                BeginMove(vp, oh, ov);
            }
            else
            {
                // Clicking the already-selected brush (without dragging) cycles the handle mode.
                _pendingCycle = true;
                BeginMove(vp, oh, ov);
            }
        }
        else
        {
            // Empty space: deselect IMMEDIATELY on press (Hammer clears on mouse-down, and our own 3D view
            // already does — the 2D view deferring it to release is what felt "sticky"), then arm a marquee
            // so a drag still box-selects. Ctrl/Shift keep the current selection for an additive box.
            if (!additive) { _doc.ClearSelection(); _mode = SelectMode.Scale; }
            _marquee = true; _dragVp = vp;
            _marqStartH = _marqCurH = oh; _marqStartV = _marqCurV = ov;
            _marqAdditive = additive;
        }
        _doc.NotifyChanged();
    }

    public void OnMouseMove(GLViewport vp, MouseEventArgs e)
    {
        if (vp != _dragVp) return;
        if (_moving3D) { Apply3DMove(vp, e.X, e.Y); return; }
        // 3D decal slide: intersect the cursor ray with the decal's (fixed) plane and keep the grab offset.
        if (_dragDecal3D != null && vp.ViewportType == ViewportType.Perspective3D)
        {
            var cam3 = vp.ActiveCamera3D;
            if (cam3 == null) return;
            var ray = Picking.RayFromScreen(cam3, e.X, e.Y, vp.Width, vp.Height);
            var n = _dragDecal3D.Normal.LengthSquared > 1e-6f ? Vector3.Normalize(_dragDecal3D.Normal) : Vector3.UnitY;
            float denom = Vector3.Dot(ray.Direction, n);
            if (MathF.Abs(denom) > 1e-5f)
            {
                float t = Vector3.Dot(_decal3DPlanePoint - ray.Origin, n) / denom;
                if (t > 0f)
                {
                    RecordUndoOnce();
                    var pos = ray.Origin + ray.Direction * t - _decal3DGrabOffset;
                    if ((Control.ModifierKeys & Keys.Alt) == 0) pos = SnapVec(pos, GridSnap.ActiveStep(vp.GridSize, 1f));
                    _dragDecal3D.Position = pos;
                    ProjectDecalToSurface(_dragDecal3D);   // conform to the brush surface it now sits over (Hammer overlay)
                    _moved = true; _doc.NotifyChanged(); vp.Invalidate();
                }
            }
            return;
        }
        var cam = vp.ActiveCamera2D!;
        var (oh, ov) = ScreenToOrtho(e.X, e.Y, vp.Width, vp.Height, cam);
        bool freeSnap = (Control.ModifierKeys & Keys.Alt) != 0;   // Alt = ignore the grid (Hammer)
        float grid = GridSnap.ActiveStep(vp.GridSize, cam.Zoom);

        // Decal handle transforms (Hammer selection handles on a decals-only selection).
        if (_decalHandleScale)  { ApplyDecalScale(vp, oh, ov); return; }
        if (_decalHandleRotate) { ApplyDecalRotate(vp, oh, ov); return; }

        // Decal drag: resize its U/V extents (corner grab) or move its position (body grab). Both snap to the
        // grid like a brush (Alt frees them); a move also re-projects onto the surface it's over.
        if (_dragDecal != null)
        {
            RecordUndoOnce();
            if (_decalResize)
            {
                var world = OrthoToWorld(oh, ov, _decalAxis, _dragDecal.Position);
                var (u, v) = _dragDecal.Axes();
                float su = MathF.Abs(Vector3.Dot(world - _dragDecal.Position, u));
                float sv = MathF.Abs(Vector3.Dot(world - _dragDecal.Position, v));
                if (!freeSnap) { su = Snap(su, grid); sv = Snap(sv, grid); }
                _dragDecal.SizeU = MathF.Max(4f, su);
                _dragDecal.SizeV = MathF.Max(4f, sv);
            }
            else
            {
                var pos = _decalStartPos + OrthoToWorldDelta(oh - _decalStartH, ov - _decalStartV, _decalAxis);
                if (!freeSnap) pos = SnapVec(pos, grid);
                _dragDecal.Position = pos;
                ProjectDecalToSurface(_dragDecal);   // stick to the brush surface beneath (Hammer overlay projection)
            }
            _moved = true; _doc.NotifyChanged(); vp.Invalidate();
            return;
        }

        if (_marquee)
        {
            _marqCurH = oh; _marqCurV = ov;
            if (!_moved && (MathF.Abs(oh - _marqStartH) > 2f * cam.Zoom || MathF.Abs(ov - _marqStartV) > 2f * cam.Zoom))
                _moved = true;
            vp.Invalidate();
            return;
        }

        // Past a small threshold the gesture is a drag (move), not a click → no mode cycle.
        if (!_moved && (MathF.Abs(oh - _moveStartH) > 2f * cam.Zoom || MathF.Abs(ov - _moveStartV) > 2f * cam.Zoom))
        {
            _moved = true; _pendingCycle = false;
            // Hammer Shift-drag: the moment the drag starts, leave the originals in place and drag clones.
            if (_cloneDrag && !_cloneDone && _moving) { _cloneDone = true; CloneForDrag(); }
        }

        if (_rotating) { ApplyRotate(vp, oh, ov); return; }
        if (_skewing)  { ApplySkew(vp, oh, ov); return; }

        if (_resizing)
        {
            // Snap the moving edge to the visible grid (same effective step the grid draws); Alt = free.
            float g = GridSnap.ActiveStep(vp.GridSize, cam.Zoom);
            bool free = (Control.ModifierKeys & Keys.Alt) != 0;
            float mouseH = free ? oh : Snap(oh, g);
            float mouseV = free ? ov : Snap(ov, g);

            float scaleH = ComputeFactor(_grabCol, mouseH, _anchorH, _grabOrigH, ExtentH());
            float scaleV = ComputeFactor(_grabRow, mouseV, _anchorV, _grabOrigV, ExtentV());
            var (scale, pivot) = ToWorldScale(cam.Axis, scaleH, scaleV);

            RecordUndoOnce();
            foreach (var (solid, snap) in _snapshots)
            {
                solid.RestorePlanes(snap);
                solid.ScaleAbout(pivot, scale);
            }
            _doc.NotifyChanged();
            vp.Invalidate();
        }
        else if (_moving)
        {
            // Movement snaps to the grid; hold Ctrl (Hammer) or Alt to move freely.
            float dh = oh - _moveStartH, dv = ov - _moveStartV;
            bool free = (Control.ModifierKeys & Keys.Alt) != 0;
            if (!free) { float g = GridSnap.ActiveStep(vp.GridSize, cam.Zoom); dh = Snap(dh, g); dv = Snap(dv, g); }

            var (hDir, vDir, _) = Bases(cam.Axis);
            Vector3 delta = dh * hDir + dv * vDir;
            if (delta.LengthSquared > 0f) RecordUndoOnce();
            foreach (var (solid, snap) in _moveSnap)
            {
                solid.RestorePlanes(snap);
                RestoreMoveTex(solid);          // reset texture shift to baseline so the lock doesn't compound
                solid.Translate(delta);
            }
            foreach (var (actor, start) in _moveActors)
                actor.Position = start + delta;
            _doc.NotifyChanged();
            vp.Invalidate();
        }
    }

    public void OnMouseUp(GLViewport vp, MouseEventArgs e)
    {
        if (_decalHandleScale || _decalHandleRotate)
        {
            _decalHandleScale = _decalHandleRotate = false; _dragVp = null; _moved = false;
            _undoRecorded = false; _decalSnap.Clear();
            _doc.NotifyChanged(); vp.Invalidate();
            return;
        }
        if (_dragDecal != null)
        {
            // A click (no drag) on the already-selected decal body cycles Scale ↔ Rotate (decals don't skew).
            if (_pendingCycle && !_moved)
                _mode = _mode == SelectMode.Scale ? SelectMode.Rotate : SelectMode.Scale;
            _dragDecal = null; _dragVp = null; _moved = false; _pendingCycle = false;
            _doc.NotifyChanged(); vp.Invalidate();
            return;
        }
        if (_dragDecal3D != null)
        {
            _dragDecal3D = null; _dragVp = null; _moved = false; _undoRecorded = false;
            _doc.NotifyChanged(); vp.Invalidate();
            return;
        }
        if (_marquee)
        {
            FinishMarquee(vp);
            _marquee = false; _dragVp = null; _moved = false;
            _doc.NotifyChanged(); vp.Invalidate();
            return;
        }
        if (!_resizing && !_moving && !_rotating && !_skewing && !_moving3D) return;

        // A click (no drag) on the already-selected brush cycles Scale → Rotate → Skew.
        if (_pendingCycle && !_moved)
            _mode = _mode switch
            {
                SelectMode.Scale  => SelectMode.Rotate,
                SelectMode.Rotate => SelectMode.Skew,
                _                 => SelectMode.Scale,
            };

        // If the Player Start was dragged/rotated, write its new placement back to the scene spawn settings.
        if (_doc.SpawnMarkerDragging) { _doc.SyncSpawnFromMarker(); _doc.SpawnMarkerDragging = false; }

        _resizing = false;
        _moving   = false;
        _rotating = false;
        _skewing  = false;
        _moving3D = false;
        _dragVp   = null;
        _undoRecorded = false;
        _pendingCycle = false;
        _moved        = false;
        _snapshots.Clear();
        _moveSnap.Clear();
        _moveActors.Clear();
        _rotActors.Clear();
        _doc.NotifyChanged();
        vp.Invalidate();
    }

    /// <summary>Marquee rect for the rubber-band overlay, in the given view's ortho coords. Only the
    /// viewport the drag started in returns true.</summary>
    public bool TryGetMarquee(GLViewport vp, out float h1, out float v1, out float h2, out float v2)
    {
        h1 = _marqStartH; v1 = _marqStartV; h2 = _marqCurH; v2 = _marqCurV;
        return _marquee && _moved && vp == _dragVp;
    }

    // Select every brush + actor whose projected AABB intersects the marquee rect (Hammer touch-select).
    private void FinishMarquee(GLViewport vp)
    {
        var cam = vp.ActiveCamera2D; if (cam == null) return;
        // #6: any marquee outcome (deselect on plain click, or a fresh box-select) is a NEW selection,
        // so the transform handles must return to Scale/Stretch — never carry Rotate/Skew across.
        _mode = SelectMode.Scale;
        if (!_moved) { if (!_marqAdditive) _doc.ClearSelection(); return; }   // plain click = deselect

        float loH = MathF.Min(_marqStartH, _marqCurH), hiH = MathF.Max(_marqStartH, _marqCurH);
        float loV = MathF.Min(_marqStartV, _marqCurV), hiV = MathF.Max(_marqStartV, _marqCurV);
        if (!_marqAdditive) _doc.ClearSelection();

        foreach (var s in _doc.Solids)
        {
            var (mn, mx) = s.GetAABB();
            var (sh, sv, eh, ev) = Project2DAABB(mn, mx, cam.Axis);
            if (eh >= loH && sh <= hiH && ev >= loV && sv <= hiV) s.IsSelected = true;
        }
        var res = vp.Resolver;
        foreach (var a in _doc.PickableActors)
        {
            var (mn, mx) = Picking.ActorBounds(a, res, true);
            var (sh, sv, eh, ev) = Project2DAABB(mn, mx, cam.Axis);
            if (eh >= loH && sh <= hiH && ev >= loV && sv <= hiV) a.IsSelected = true;
        }
    }

    private void RecordUndoOnce()
    {
        if (_undoRecorded) return;
        _doc.RecordUndo();
        _undoRecorded = true;
    }

    // Hammer Shift-drag clone: duplicate the brushes/actors being moved, leave the originals where
    // they are, and retarget the drag onto the new copies (so the drag carries the clones away).
    private void CloneForDrag()
    {
        RecordUndoOnce();   // record BEFORE adding clones so one undo removes them

        var newSnap = new Dictionary<Solid, Plane3D[]>();
        foreach (var s in _moveSnap.Keys.ToList())
        {
            s.IsSelected = false;
            var c = s.Clone(); c.IsSelected = true;
            _doc.AddSolid(c);
            newSnap[c] = c.SnapshotPlanes();
        }
        _moveSnap.Clear();
        foreach (var kv in newSnap) _moveSnap[kv.Key] = kv.Value;
        SnapshotMoveTex();   // re-baseline texture shifts for the freshly-cloned brushes

        var newActors = new Dictionary<ZActor, Vector3>();
        foreach (var a in _moveActors.Keys.ToList())
        {
            if (a.IsEditorOnly) { newActors[a] = a.Position; continue; }   // the Player Start is a singleton — never clone it
            a.IsSelected = false;
            var c = a.Clone(); c.IsSelected = true;
            _doc.AddActor(c);
            newActors[c] = c.Position;
        }
        _moveActors.Clear();
        foreach (var kv in newActors) _moveActors[kv.Key] = kv.Value;
    }

    // Capture each moving brush's baseline texture shift so the per-frame texture lock doesn't compound.
    private void SnapshotMoveTex()
    {
        _moveTexSnap.Clear();
        foreach (var s in _moveSnap.Keys)
        {
            var d = new Dictionary<int, (float, float)>();
            foreach (var f in s.Faces) if (f.PlaneIndex >= 0) d[f.PlaneIndex] = (f.TexShiftS, f.TexShiftT);
            _moveTexSnap[s] = d;
        }
    }

    // Reset a brush's texture shift to its move-start baseline (call AFTER RestorePlanes, BEFORE Translate).
    private void RestoreMoveTex(Solid solid)
    {
        if (!_moveTexSnap.TryGetValue(solid, out var baseTex)) return;
        foreach (var f in solid.Faces)
            if (baseTex.TryGetValue(f.PlaneIndex, out var t)) { f.TexShiftS = t.s; f.TexShiftT = t.t; }
    }

    private void BeginMove(GLViewport vp, float oh, float ov)
    {
        _moving = true;
        _dragVp = vp;
        _undoRecorded = false;
        _moveStartH = oh; _moveStartV = ov;
        _moveSnap.Clear();
        foreach (var s in _doc.Solids)
            if (s.IsSelected) _moveSnap[s] = s.SnapshotPlanes();
        SnapshotMoveTex();
        _moveActors.Clear();
        foreach (var a in _doc.PickableActors)
            if (a.IsSelected) _moveActors[a] = a.Position;
        _doc.SpawnMarkerDragging = _moveActors.Keys.Any(a => a.IsSpawn);
    }

    // Begins a 3D drag of the whole selection (brushes and/or actors) on the horizontal plane Y=planeY,
    // matching MH's actor 3D move. planeY is the grabbed actor's height or the grabbed face's hit point.
    private void Begin3DMove(GLViewport vp, Camera3D cam3, int sx, int sy, float planeY)
    {
        _moving3D = true;
        _dragVp = vp;
        _undoRecorded = false;
        _move3DY = planeY;
        var ray = Picking.RayFromScreen(cam3, sx, sy, vp.Width, vp.Height);
        _move3DAnchor = RayPlaneXZ(ray, _move3DY);
        _moveActors.Clear();
        foreach (var a in _doc.PickableActors)
            if (a.IsSelected) _moveActors[a] = a.Position;
        _moveSnap.Clear();
        foreach (var s in _doc.Solids)
            if (s.IsSelected) _moveSnap[s] = s.SnapshotPlanes();
        SnapshotMoveTex();
        _doc.SpawnMarkerDragging = _moveActors.Keys.Any(a => a.IsSpawn);
    }

    private void Apply3DMove(GLViewport vp, int sx, int sy)
    {
        var cam3 = vp.ActiveCamera3D;
        if (cam3 == null) return;
        var ray = Picking.RayFromScreen(cam3, sx, sy, vp.Width, vp.Height);
        var pt = RayPlaneXZ(ray, _move3DY);
        var delta = pt - _move3DAnchor;
        delta.Y = 0f;
        // Snap to grid unless Alt (free); Ctrl already suspends snapping globally via GridSnap.
        if ((Control.ModifierKeys & Keys.Alt) == 0)
        {
            float g = GridSnap.ActiveStep(vp.GridSize, 1f);
            delta.X = Snap(delta.X, g);
            delta.Z = Snap(delta.Z, g);
        }
        RecordUndoOnce();
        foreach (var (solid, snap) in _moveSnap)
        {
            solid.RestorePlanes(snap);
            RestoreMoveTex(solid);          // reset texture shift to baseline so the lock doesn't compound
            solid.Translate(delta);
        }
        foreach (var (actor, start) in _moveActors)
            actor.Position = start + delta;
        _doc.NotifyChanged();
        vp.Invalidate();
    }

    // Intersects a ray with the horizontal plane Y = planeY (returns the ray origin if parallel).
    private static Vector3 RayPlaneXZ(Ray ray, float planeY)
    {
        float dy = ray.Direction.Y;
        if (MathF.Abs(dy) < 1e-6f) return ray.Origin;
        float t = (planeY - ray.Origin.Y) / dy;
        if (t < 0) t = 0;
        return ray.Origin + ray.Direction * t;
    }

    private static (Vector3 h, Vector3 v, Vector3 d) Bases(ViewAxis axis) => axis switch
    {
        ViewAxis.Top   => (new(1, 0, 0), new(0, 0, -1), new(0, 1, 0)),
        ViewAxis.Front => (new(1, 0, 0), new(0, 1, 0),  new(0, 0, 1)),
        ViewAxis.Side  => (new(0, 0, 1), new(0, 1, 0),  new(1, 0, 0)),
        _              => (new(1, 0, 0), new(0, 1, 0),  new(0, 0, 1)),
    };

    public void OnKeyDown(GLViewport vp, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) _doc.DeleteSelected();
    }

    // ── Handle geometry exposed to the renderer ──────────────────────────

    /// <summary>
    /// Returns the 8 resize-handle centres in ortho space for the given viewport,
    /// or null when there is no brush selection (or this is the 3D view).
    /// </summary>
    public List<(float h, float v)>? GetHandles(GLViewport vp)
    {
        if (vp.ViewportType == ViewportType.Perspective3D) return null;
        if (!TryGetSelectionRect(vp.ActiveCamera2D!.Axis, vp.Resolver, out var rect)) return null;

        var (loH, loV, hiH, hiV) = rect;
        float mH = (loH + hiH) * 0.5f, mV = (loV + hiV) * 0.5f;
        return
        [
            (loH, loV), (mH, loV), (hiH, loV),
            (loH, mV),             (hiH, mV),
            (loH, hiV), (mH, hiV), (hiH, hiV),
        ];
    }

    // ── Resize helpers ───────────────────────────────────────────────────

    private void BeginResize(GLViewport vp, (float loH, float loV, float hiH, float hiV) rect)
    {
        _resizing = true;
        _dragVp   = vp;
        _undoRecorded = false;

        var (loH, loV, hiH, hiV) = rect;
        float mH = (loH + hiH) * 0.5f, mV = (loV + hiV) * 0.5f;

        _grabOrigH = _grabCol == 0 ? loH : _grabCol == 2 ? hiH : mH;
        _grabOrigV = _grabRow == 0 ? loV : _grabRow == 2 ? hiV : mV;

        // Anchor = opposite handle (fixed point of the scaling).
        int aCol = 2 - _grabCol, aRow = 2 - _grabRow;
        _anchorH = aCol == 0 ? loH : aCol == 2 ? hiH : mH;
        _anchorV = aRow == 0 ? loV : aRow == 2 ? hiV : mV;

        // Snapshot selected solids' geometry + record their combined world AABB.
        _snapshots.Clear();
        bool first = true;
        _origMin = _origMax = Vector3.Zero;
        foreach (var s in _doc.Solids)
        {
            if (!s.IsSelected) continue;
            _snapshots[s] = s.SnapshotPlanes();
            var (mn, mx) = s.GetAABB();
            if (first) { _origMin = mn; _origMax = mx; first = false; }
            else
            {
                _origMin = Vector3.ComponentMin(_origMin, mn);
                _origMax = Vector3.ComponentMax(_origMax, mx);
            }
        }
    }

    // ── Rotate (mode 2) ──────────────────────────────────────────────────

    private void BeginRotate(GLViewport vp, (float loH, float loV, float hiH, float hiV) rect, float oh, float ov)
    {
        _rotating = true;
        _dragVp   = vp;
        _undoRecorded = false;
        var (loH, loV, hiH, hiV) = rect;
        _centerH = (loH + hiH) * 0.5f;
        _centerV = (loV + hiV) * 0.5f;
        _rotStartAngle = MathF.Atan2(ov - _centerV, oh - _centerH);
        SnapshotSelected();
        _rotActors.Clear();
        foreach (var a in _doc.PickableActors)
            if (a.IsSelected) { _rotActors[a] = (a.Position, a.XRot, a.YRot, a.ZRot); if (a.IsSpawn) _doc.SpawnMarkerDragging = true; }
    }

    private void ApplyRotate(GLViewport vp, float oh, float ov)
    {
        var cam = vp.ActiveCamera2D!;
        float theta = MathF.Atan2(ov - _centerV, oh - _centerH) - _rotStartAngle;
        // Snap to 15° by default; hold Shift for free rotation (Hammer: Shift turns angle-snap off).
        bool free = (Control.ModifierKeys & Keys.Shift) != 0;
        if (!free) { float step = MathF.PI / 12f; theta = MathF.Round(theta / step) * step; }

        float cos = MathF.Cos(theta), sin = MathF.Sin(theta);
        var (hDir, vDir, dDir) = Bases(cam.Axis);
        Vector3 pivot = _centerH * hDir + _centerV * vDir;
        Vector3 Rot(Vector3 w) => RotateVec(w, hDir, vDir, dDir, cos, sin);

        RecordUndoOnce();
        foreach (var (solid, snap) in _snapshots)
        {
            solid.TransformAbout(snap, Rot, Rot, pivot);   // rotation is orthogonal: (M⁻¹)ᵀ = M
            LockTextureAxes(solid, Rot, pivot);            // #24: carry the texture mapping (axes + offset) through the rotation
        }

        // Rotate selected actors about the pivot too: spin their position in the view plane and add
        // the same angle to the binary-angle rotation about the view's depth axis (Top→Y/yaw — which
        // matches the model's render rotation exactly — Front→Z, Side→X).
        if (_rotActors.Count > 0)
        {
            // Binary-yaw delta = +theta (same sense as the body orbit + brushes). The rendered MODEL rotates
            // local +Z -> world (sin yaw, cos yaw); projected to the Top view (screen v = -Z) that spins the
            // model CCW as yaw grows — matching the cursor's CCW (+theta) drag. So +theta makes the whole
            // actor (model, not just the facing tick) track the cursor 1:1, consistent in-game (engine faces
            // (sin yaw, cos yaw) too). (The earlier -theta aligned only the small facing line, not the body.)
            short dRot = (short)(theta * (32768f / MathF.PI));
            foreach (var (a, snap) in _rotActors)
            {
                Vector3 np = pivot + RotateVec(snap.pos - pivot, hDir, vDir, dDir, cos, sin);
                a.XPos = np.X; a.YPos = np.Y; a.ZPos = np.Z;
                switch (cam.Axis)
                {
                    case ViewAxis.Top:   a.YRot = (short)(snap.ry + dRot); break;
                    case ViewAxis.Front: a.ZRot = (short)(snap.rz + dRot); break;
                    case ViewAxis.Side:  a.XRot = (short)(snap.rx + dRot); break;
                }
            }
        }
        _doc.NotifyChanged();
        vp.Invalidate();
    }

    // Rotates a world vector by (cos,sin) within the view's in-plane (h,v) basis; depth axis unchanged.
    private static Vector3 RotateVec(Vector3 w, Vector3 hDir, Vector3 vDir, Vector3 dDir, float cos, float sin)
    {
        float h = Vector3.Dot(w, hDir), v = Vector3.Dot(w, vDir), d = Vector3.Dot(w, dDir);
        return (h * cos - v * sin) * hDir + (h * sin + v * cos) * vDir + d * dDir;
    }

    // ── Skew / shear (mode 3) ────────────────────────────────────────────

    private void BeginSkew(GLViewport vp, (float loH, float loV, float hiH, float hiV) rect)
    {
        _skewing = true;
        _dragVp  = vp;
        _undoRecorded = false;
        var (loH, loV, hiH, hiV) = rect;
        float mH = (loH + hiH) * 0.5f, mV = (loV + hiV) * 0.5f;
        _grabOrigH = _grabCol == 0 ? loH : _grabCol == 2 ? hiH : mH;
        _grabOrigV = _grabRow == 0 ? loV : _grabRow == 2 ? hiV : mV;
        int aCol = 2 - _grabCol, aRow = 2 - _grabRow;
        _anchorH = aCol == 0 ? loH : aCol == 2 ? hiH : mH;
        _anchorV = aRow == 0 ? loV : aRow == 2 ? hiV : mV;
        SnapshotSelected();
    }

    private void ApplySkew(GLViewport vp, float oh, float ov)
    {
        var cam = vp.ActiveCamera2D!;
        var (hDir, vDir, dDir) = Bases(cam.Axis);
        float g = GridSnap.ActiveStep(vp.GridSize, cam.Zoom);
        bool free = (Control.ModifierKeys & Keys.Alt) != 0;   // Alt = no grid snap (Hammer, all modes)
        float mouseH = free ? oh : Snap(oh, g), mouseV = free ? ov : Snap(ov, g);

        // A horizontal edge handle (col=mid) shears H along V; a vertical edge handle shears V along H.
        // For a corner, pick the orientation matching the dominant drag axis.
        bool horizEdge = _grabCol == 1 && _grabRow != 1;
        bool vertEdge  = _grabRow == 1 && _grabCol != 1;
        bool shearHoriz = horizEdge || (!vertEdge &&
            MathF.Abs(mouseH - _grabOrigH) >= MathF.Abs(mouseV - _grabOrigV));

        Vector3 pivot = _anchorH * hDir + _anchorV * vDir;
        Func<Vector3, Vector3> mapPt, mapN;
        if (shearHoriz)
        {
            float denom = _grabOrigV - _anchorV;
            if (MathF.Abs(denom) < 1e-4f) return;
            float k = (mouseH - _grabOrigH) / denom;
            mapPt = w => ShearH(w, hDir, vDir, dDir, k);
            mapN  = w => ShearHNormal(w, hDir, vDir, dDir, k);
        }
        else
        {
            float denom = _grabOrigH - _anchorH;
            if (MathF.Abs(denom) < 1e-4f) return;
            float k = (mouseV - _grabOrigV) / denom;
            mapPt = w => ShearV(w, hDir, vDir, dDir, k);
            mapN  = w => ShearVNormal(w, hDir, vDir, dDir, k);
        }

        RecordUndoOnce();
        foreach (var (solid, snap) in _snapshots)
        {
            solid.TransformAbout(snap, mapPt, mapN, pivot);
            LockTextureAxes(solid, mapPt, pivot);   // carry the texture mapping (axes + offset) through the shear too
        }
        _doc.NotifyChanged();
        vp.Invalidate();
    }

    // Shear H by k along V (point: h' = h + k·v); normal uses (M⁻¹)ᵀ: v' = v − k·h.
    private static Vector3 ShearH(Vector3 w, Vector3 h, Vector3 v, Vector3 d, float k)
    { float a = Vector3.Dot(w, h), b = Vector3.Dot(w, v), c = Vector3.Dot(w, d); return (a + k * b) * h + b * v + c * d; }
    private static Vector3 ShearHNormal(Vector3 w, Vector3 h, Vector3 v, Vector3 d, float k)
    { float a = Vector3.Dot(w, h), b = Vector3.Dot(w, v), c = Vector3.Dot(w, d); return a * h + (b - k * a) * v + c * d; }
    // Shear V by k along H (point: v' = v + k·h); normal uses (M⁻¹)ᵀ: h' = h − k·v.
    private static Vector3 ShearV(Vector3 w, Vector3 h, Vector3 v, Vector3 d, float k)
    { float a = Vector3.Dot(w, h), b = Vector3.Dot(w, v), c = Vector3.Dot(w, d); return a * h + (b + k * a) * v + c * d; }
    private static Vector3 ShearVNormal(Vector3 w, Vector3 h, Vector3 v, Vector3 d, float k)
    { float a = Vector3.Dot(w, h), b = Vector3.Dot(w, v), c = Vector3.Dot(w, d); return (a - k * b) * h + b * v + c * d; }

    private void SnapshotSelected()
    {
        _snapshots.Clear();
        _texSnap.Clear();
        foreach (var s in _doc.Solids)
            if (s.IsSelected)
            {
                _snapshots[s] = s.SnapshotPlanes();
                var ax = new Dictionary<int, (Vector3 u, Vector3 v, float shiftS, float shiftT)>();
                foreach (var f in s.Faces)
                    if (f.PlaneIndex >= 0) { f.EnsureAxes(); ax[f.PlaneIndex] = (f.UAxis, f.VAxis, f.TexShiftS, f.TexShiftT); }
                _texSnap[s] = ax;
            }
    }

    // Texture lock for rotate/skew: after the solid's planes are transformed, set each face's texture
    // axes to the ORIGINAL axes mapped by the same linear transform, so the texture rotates with the
    // surface instead of re-projecting off the new normal (#4/#24). Drift-free (maps the snapshot).
    private void LockTextureAxes(Solid solid, Func<Vector3, Vector3> map, Vector3 pivot)
    {
        if (!Solid.TextureLock || !_texSnap.TryGetValue(solid, out var orig)) return;
        foreach (var f in solid.Faces)
            if (orig.TryGetValue(f.PlaneIndex, out var a))
            {
                f.SetAxes(map(a.u), map(a.v));
                // Texture lock must keep the texture PINNED to the surface, not merely rotate its axes:
                // also re-offset the shift so the pivot (the transform's one fixed point) keeps the UV it
                // had before. Without this the texture slides across the face as the brush rotates. The
                // pivot's UV = Dot(pivot, axis)/scale + shift; solving for the new shift that preserves it.
                float sS = MathF.Abs(f.TexScaleS) < 1e-3f ? 64f : f.TexScaleS;
                float sT = MathF.Abs(f.TexScaleT) < 1e-3f ? 64f : f.TexScaleT;
                f.TexShiftS = a.shiftS + (Vector3.Dot(pivot, a.u) - Vector3.Dot(pivot, f.UAxis)) / sS;
                f.TexShiftT = a.shiftT + (Vector3.Dot(pivot, a.v) - Vector3.Dot(pivot, f.VAxis)) / sT;
            }
    }

    private float ExtentH() => MathF.Abs(_grabOrigH - _anchorH);
    private float ExtentV() => MathF.Abs(_grabOrigV - _anchorV);

    // Absolute scale factor for one axis; clamps to keep a minimum extent and avoid flips.
    private static float ComputeFactor(int gridPos, float mouse, float anchor, float grabOrig, float extent)
    {
        if (gridPos == 1) return 1f;                 // mid handle: this axis not scaled
        float denom = grabOrig - anchor;
        if (MathF.Abs(denom) < 1e-4f) return 1f;
        float factor = (mouse - anchor) / denom;
        float minFactor = extent > 1e-4f ? 1f / extent : 0.01f;  // ≥1 world unit
        if (factor < minFactor) factor = minFactor;
        return factor;
    }

    // Maps ortho-space (h,v) scale factors to a world-space scale vector + pivot.
    private (Vector3 scale, Vector3 pivot) ToWorldScale(ViewAxis axis, float sH, float sV)
    {
        // Anchor ortho coords → world coords for the two in-plane axes.
        switch (axis)
        {
            case ViewAxis.Top:   // h=X, v=-Z
                return (new Vector3(sH, 1f, sV),
                        new Vector3(_anchorH, 0f, -_anchorV));
            case ViewAxis.Front: // h=X, v=Y
                return (new Vector3(sH, sV, 1f),
                        new Vector3(_anchorH, _anchorV, 0f));
            case ViewAxis.Side:  // h=Z, v=Y
                return (new Vector3(1f, sV, sH),
                        new Vector3(0f, _anchorV, _anchorH));
            default:
                return (Vector3.One, Vector3.Zero);
        }
    }

    private bool TryGetSelectionRect(ViewAxis axis, Editor.ActorModelResolver? resolver, out (float loH, float loV, float hiH, float hiV) rect)
    {
        rect = default;
        bool any = false;
        Vector3 mn = Vector3.Zero, mx = Vector3.Zero;
        void Acc(Vector3 a, Vector3 b)
        {
            if (!any) { mn = a; mx = b; any = true; }
            else { mn = Vector3.ComponentMin(mn, a); mx = Vector3.ComponentMax(mx, b); }
        }
        foreach (var s in _doc.Solids)
            if (s.IsSelected) { var (a, b) = s.GetAABB(); Acc(a, b); }
        // Selected actors contribute their MODEL footprint (its world AABB) so the selection box and
        // move/resize handles fit the model — like Hammer fitting handles to the entity's bounds —
        // instead of a fixed origin box. No-model actors fall back to a small default box.
        foreach (var actor in _doc.PickableActors)
            if (actor.IsSelected) { var (a, b) = Picking.ActorBounds(actor, resolver, true); Acc(a, b); }
        // Selected decals contribute their footprint so a decals-only selection gets the same handle box.
        foreach (var d in _doc.VisibleDecals)
            if (d.IsSelected)
                foreach (var c in d.Corners(0f)) Acc(c, c);
        if (!any) return false;

        var (sh, sv, eh, ev) = Project2DAABB(mn, mx, axis);
        rect = (sh, sv, eh, ev);
        return true;
    }

    private static bool TryHitHandle(
        (float loH, float loV, float hiH, float hiV) rect,
        float oh, float ov, float zoom, out int col, out int row)
    {
        col = row = 1;
        var (loH, loV, hiH, hiV) = rect;
        float mH = (loH + hiH) * 0.5f, mV = (loV + hiV) * 0.5f;
        float r = HandlePixels * zoom * 1.6f;   // grab radius in ortho units

        float[] hs = [loH, mH, hiH];
        float[] vs = [loV, mV, hiV];
        for (int c = 0; c < 3; c++)
        for (int rw = 0; rw < 3; rw++)
        {
            if (c == 1 && rw == 1) continue;     // skip centre
            if (MathF.Abs(oh - hs[c]) <= r && MathF.Abs(ov - vs[rw]) <= r)
            {
                col = c; row = rw;
                return true;
            }
        }
        return false;
    }

    // ── Coordinate helpers ───────────────────────────────────────────────

    private static (float h, float v) ScreenToOrtho(int sx, int sy, int w, int h, Camera2D cam)
        => (cam.PanX + (sx - w * 0.5f) * cam.Zoom,
            cam.PanY - (sy - h * 0.5f) * cam.Zoom);

    private static float Snap(float x, float g) => g < 1f ? x : MathF.Round(x / g) * g;

    private static (float h, float v) OrthoPos(Vector3 world, ViewAxis axis) => axis switch
    {
        ViewAxis.Top   => (world.X, -world.Z),
        ViewAxis.Front => (world.X,  world.Y),
        ViewAxis.Side  => (world.Z,  world.Y),
        _              => (0, 0)
    };

    // Reconstruct a world point from 2D ortho (h,v), taking the off-plane (depth) axis from a reference
    // point — inverse of OrthoPos. Used to resize a decal to the cursor on its own plane.
    private static Vector3 OrthoToWorld(float oh, float ov, ViewAxis axis, Vector3 depthRef) => axis switch
    {
        ViewAxis.Top   => new(oh, depthRef.Y, -ov),
        ViewAxis.Front => new(oh, ov, depthRef.Z),
        ViewAxis.Side  => new(depthRef.X, ov, oh),
        _              => depthRef,
    };

    // Map a 2D ortho delta (dh,dv) to a world delta on the view plane (depth axis unchanged).
    private static Vector3 OrthoToWorldDelta(float dh, float dv, ViewAxis axis) => axis switch
    {
        ViewAxis.Top   => new(dh, 0, -dv),
        ViewAxis.Front => new(dh, dv, 0),
        ViewAxis.Side  => new(0, dv, dh),
        _              => Vector3.Zero,
    };

    // Picks the decal under the 2D cursor: a corner (within the grab radius) → resize, else the smallest
    // footprint containing the cursor → move. Only visible-room decals are pickable.
    private bool TryPickDecal2D(Camera2D cam, float oh, float ov, out Decal decal, out bool resize)
    {
        decal = null!; resize = false;
        float grab = HandlePixels * cam.Zoom;
        Decal? corner = null; float bestCorner = grab;
        Decal? body = null;   float bestArea = float.MaxValue;
        foreach (var d in _doc.VisibleDecals)
        {
            float sh = float.MaxValue, sv = float.MaxValue, eh = float.MinValue, ev = float.MinValue;
            foreach (var c in d.Corners(0f))
            {
                var (ch, cv) = OrthoPos(c, cam.Axis);
                sh = MathF.Min(sh, ch); eh = MathF.Max(eh, ch); sv = MathF.Min(sv, cv); ev = MathF.Max(ev, cv);
                float dist = MathF.Sqrt((ch - oh) * (ch - oh) + (cv - ov) * (cv - ov));
                if (dist < bestCorner) { bestCorner = dist; corner = d; }
            }
            if (oh >= sh && oh <= eh && ov >= sv && ov <= ev)
            {
                float area = MathF.Max(eh - sh, 0.001f) * MathF.Max(ev - sv, 0.001f);
                if (area < bestArea) { bestArea = area; body = d; }
            }
        }
        if (corner != null) { decal = corner; resize = true; return true; }
        if (body   != null) { decal = body;   resize = false; return true; }
        return false;
    }

    // True when the selection is one-or-more decals and NOTHING else — so the transform handles drive the
    // decals (resize/rotate) instead of a brush/actor.
    private bool DecalsOnlySelection()
        => _doc.VisibleDecals.Any(d => d.IsSelected)
           && !_doc.Solids.Any(s => s.IsSelected)
           && !_doc.PickableActors.Any(a => a.IsSelected);

    private void SnapshotDecals()
    {
        _decalSnap.Clear();
        foreach (var d in _doc.VisibleDecals)
            if (d.IsSelected) _decalSnap[d] = (d.SizeU, d.SizeV, d.Rotation, d.Position);
    }

    // ── Decal handle scale (Scale/Skew mode) ─────────────────────────────
    private void BeginDecalScale(GLViewport vp, (float loH, float loV, float hiH, float hiV) rect)
    {
        _decalHandleScale = true; _dragVp = vp; _undoRecorded = false;
        var (loH, loV, hiH, hiV) = rect;
        float mH = (loH + hiH) * 0.5f, mV = (loV + hiV) * 0.5f;
        _grabOrigH = _grabCol == 0 ? loH : _grabCol == 2 ? hiH : mH;
        _grabOrigV = _grabRow == 0 ? loV : _grabRow == 2 ? hiV : mV;
        int aCol = 2 - _grabCol, aRow = 2 - _grabRow;   // anchor = opposite handle (fixed point)
        _anchorH = aCol == 0 ? loH : aCol == 2 ? hiH : mH;
        _anchorV = aRow == 0 ? loV : aRow == 2 ? hiV : mV;
        SnapshotDecals();
    }

    private void ApplyDecalScale(GLViewport vp, float oh, float ov)
    {
        var cam = vp.ActiveCamera2D!;
        float g = GridSnap.ActiveStep(vp.GridSize, cam.Zoom);
        bool free = (Control.ModifierKeys & Keys.Alt) != 0;
        float mouseH = free ? oh : Snap(oh, g), mouseV = free ? ov : Snap(ov, g);
        float sH = ComputeFactor(_grabCol, mouseH, _anchorH, _grabOrigH, ExtentH());
        float sV = ComputeFactor(_grabRow, mouseV, _anchorV, _grabOrigV, ExtentV());
        var (scale, pivot) = ToWorldScale(cam.Axis, sH, sV);

        RecordUndoOnce();
        foreach (var (d, snap) in _decalSnap)
        {
            // Scale position about the anchor in the view plane (a group scales outward from the fixed handle).
            var rel = snap.pos - pivot;
            d.Position = pivot + new Vector3(rel.X * scale.X, rel.Y * scale.Y, rel.Z * scale.Z);
            // For a face-on, unrotated decal, local U aligns with view-H and V with view-V, so the per-axis
            // factors map straight onto the half-extents (the common case; a rotated decal scales approximately).
            d.SizeU = MathF.Max(4f, snap.su * MathF.Abs(sH));
            d.SizeV = MathF.Max(4f, snap.sv * MathF.Abs(sV));
        }
        _moved = true; _doc.NotifyChanged(); vp.Invalidate();
    }

    // ── Decal handle rotate (Rotate mode) ────────────────────────────────
    private void BeginDecalRotate(GLViewport vp, (float loH, float loV, float hiH, float hiV) rect, float oh, float ov)
    {
        _decalHandleRotate = true; _dragVp = vp; _undoRecorded = false;
        var (loH, loV, hiH, hiV) = rect;
        _centerH = (loH + hiH) * 0.5f;
        _centerV = (loV + hiV) * 0.5f;
        _rotStartAngle = MathF.Atan2(ov - _centerV, oh - _centerH);
        SnapshotDecals();
    }

    private void ApplyDecalRotate(GLViewport vp, float oh, float ov)
    {
        var cam = vp.ActiveCamera2D!;
        float theta = MathF.Atan2(ov - _centerV, oh - _centerH) - _rotStartAngle;
        bool free = (Control.ModifierKeys & Keys.Shift) != 0;   // Shift = free (no 15° snap), Hammer
        if (!free) { float step = MathF.PI / 12f; theta = MathF.Round(theta / step) * step; }
        float cos = MathF.Cos(theta), sin = MathF.Sin(theta);
        var (hDir, vDir, dDir) = Bases(cam.Axis);
        Vector3 pivot = _centerH * hDir + _centerV * vDir;   // depth 0 — RotateVec preserves each decal's own depth
        float degDelta = theta * (180f / MathF.PI);

        RecordUndoOnce();
        foreach (var (d, snap) in _decalSnap)
        {
            d.Position = pivot + RotateVec(snap.pos - pivot, hDir, vDir, dDir, cos, sin);
            d.Rotation = snap.rot + degDelta;   // spin the decal's facing about its normal with the cursor
        }
        _doc.NotifyChanged(); vp.Invalidate();
    }

    // Hammer-style overlay projection: drop the decal onto the brush surface it now sits over. Cast from just
    // outside the decal along -normal; if a face is within reach, snap the decal onto it and adopt its normal
    // (so sliding across a corner re-orients the sticker to the new face). No hit (slid into open air) = leave it.
    private void ProjectDecalToSurface(Decal d)
    {
        var n = d.Normal.LengthSquared > 1e-6f ? Vector3.Normalize(d.Normal) : Vector3.UnitY;
        const float Probe = 128f;
        var ray = new Ray(d.Position + n * Probe, -n);
        if (Picking.PickFace(_doc.Scene, ray, out var hit) && hit.Distance <= Probe * 2f)
        {
            d.Position = hit.Point;
            var fn = hit.Face.Plane.Normal;
            if (fn.LengthSquared > 1e-6f) d.Normal = Vector3.Normalize(fn);
        }
    }

    private static Vector3 SnapVec(Vector3 v, float g) => new(Snap(v.X, g), Snap(v.Y, g), Snap(v.Z, g));

    private static (float sh, float sv, float eh, float ev) Project2DAABB(
        Vector3 mn, Vector3 mx, ViewAxis axis) => axis switch
    {
        ViewAxis.Top   => (mn.X, -mx.Z, mx.X, -mn.Z),
        ViewAxis.Front => (mn.X,  mn.Y,  mx.X,  mx.Y),
        ViewAxis.Side  => (mn.Z,  mn.Y,  mx.Z,  mx.Y),
        _              => (0, 0, 0, 0)
    };
}
