using MegatonHammer.Editor;
using MegatonHammer.Forms;
using MegatonHammer.Rendering;
using OpenTK.Mathematics;

namespace MegatonHammer.Tools;

/// <summary>
/// Vertex manipulation (Hammer-style). Shows the selected brush's vertices as handles in the 2D views.
/// Click a vertex to select it, Ctrl-click to add/remove, drag an empty box to marquee-select, and drag
/// any selected vertex to move the whole selection within the view plane. Click an edge midpoint to grab
/// both its endpoints (edge drag). Insert (I) splits the selected edge by adding a midpoint vertex.
/// The brush is rebuilt as the convex hull of its vertices each edit, so it stays a valid solid — which
/// means inward (concave) pulls are clamped to the hull; outward pulls and edge splits add geometry.
/// </summary>
public sealed class VertexTool : ITool
{
    private const float HandlePixels = 7f;

    private readonly MapDocument _doc;

    private Solid?         _editSolid;            // the solid whose point set we're editing
    private readonly List<Vector3> _points = []; // editable vertex set (hulled into the solid on each edit)
    private readonly HashSet<int>  _selected = [];

    // Drag state
    private bool        _dragging;
    private GLViewport? _dragVp;
    private float       _dragStartH, _dragStartV;
    private List<Vector3> _dragOrigin = [];       // _points snapshot at grab time

    // Marquee state
    private bool  _marquee;
    private float _mqH0, _mqV0, _mqH1, _mqV1;
    private bool  _mqAdditive;

    public string Name => "Vertex";

    public VertexTool(MapDocument doc) { _doc = doc; }

    // ── Render accessors ────────────────────────────────────────────────────
    public List<(float h, float v)>? GetHandles(GLViewport vp)
    {
        if (vp.ViewportType == ViewportType.Perspective3D) return null;
        if (!Sync(vp)) return null;
        var (hDir, vDir, _) = Bases(vp.ActiveCamera2D!.Axis);
        return _points.Select(p => (Vector3.Dot(p, hDir), Vector3.Dot(p, vDir))).ToList();
    }
    public List<(float h, float v)>? GetSelectedHandles(GLViewport vp)
    {
        if (vp.ViewportType == ViewportType.Perspective3D || _editSolid != _doc.SelectedSolid) return null;
        var (hDir, vDir, _) = Bases(vp.ActiveCamera2D!.Axis);
        return _selected.Where(i => i < _points.Count)
                        .Select(i => (Vector3.Dot(_points[i], hDir), Vector3.Dot(_points[i], vDir))).ToList();
    }
    public bool TryGetMarquee(GLViewport vp, out float h1, out float v1, out float h2, out float v2)
    {
        h1 = _mqH0; v1 = _mqV0; h2 = _mqH1; v2 = _mqV1;
        return _marquee && vp == _dragVp;
    }

    // Re-seed the editable point set when the selected solid changes.
    private bool Sync(GLViewport vp)
    {
        var solid = _doc.SelectedSolid;
        if (solid == null) { _editSolid = null; _points.Clear(); _selected.Clear(); return false; }
        if (solid != _editSolid)
        {
            _editSolid = solid;
            _points.Clear(); _points.AddRange(solid.GetUniqueVertices());
            _selected.Clear();
        }
        return true;
    }

    public void OnMouseDown(GLViewport vp, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || vp.ViewportType == ViewportType.Perspective3D) return;
        if (!Sync(vp)) return;

        var cam = vp.ActiveCamera2D!;
        var (mh, mv) = ScreenToOrtho(e.X, e.Y, vp.Width, vp.Height, cam);
        var (hDir, vDir, _) = Bases(cam.Axis);
        float r = HandlePixels * cam.Zoom * 1.6f;
        bool additive = (Control.ModifierKeys & (Keys.Control | Keys.Shift)) != 0;

        // 1) Vertex handle?
        int hit = -1;
        for (int i = 0; i < _points.Count; i++)
        {
            float h = Vector3.Dot(_points[i], hDir), v = Vector3.Dot(_points[i], vDir);
            if (MathF.Abs(mh - h) <= r && MathF.Abs(mv - v) <= r) { hit = i; break; }
        }
        if (hit >= 0)
        {
            if (additive) { if (!_selected.Add(hit)) _selected.Remove(hit); }
            else if (!_selected.Contains(hit)) { _selected.Clear(); _selected.Add(hit); }
            if (_selected.Contains(hit)) BeginDrag(vp, mh, mv);
            vp.Invalidate();
            return;
        }

        // 2) Edge midpoint? Grab both endpoints (edge drag).
        foreach (var (a, b) in Edges())
        {
            var mid = (_points[a] + _points[b]) * 0.5f;
            float h = Vector3.Dot(mid, hDir), v = Vector3.Dot(mid, vDir);
            if (MathF.Abs(mh - h) <= r && MathF.Abs(mv - v) <= r)
            {
                if (!additive) _selected.Clear();
                _selected.Add(a); _selected.Add(b);
                BeginDrag(vp, mh, mv);
                vp.Invalidate();
                return;
            }
        }

        // 3) Empty space → marquee select.
        _marquee = true; _dragVp = vp; _mqH0 = _mqH1 = mh; _mqV0 = _mqV1 = mv; _mqAdditive = additive;
        if (!additive) _selected.Clear();
        vp.Invalidate();
    }

    public void OnMouseMove(GLViewport vp, MouseEventArgs e)
    {
        if (vp != _dragVp) return;
        var cam = vp.ActiveCamera2D!;
        var (mh, mv) = ScreenToOrtho(e.X, e.Y, vp.Width, vp.Height, cam);

        if (_marquee) { _mqH1 = mh; _mqV1 = mv; vp.Invalidate(); return; }
        if (!_dragging || _editSolid == null) return;

        var (hDir, vDir, _) = Bases(cam.Axis);
        float g = Editor.GridSnap.ActiveStep(vp.GridSize, cam.Zoom);
        float dh = Snap(mh - _dragStartH, g), dv = Snap(mv - _dragStartV, g);
        var delta = dh * hDir + dv * vDir;

        for (int i = 0; i < _points.Count; i++)
            _points[i] = _selected.Contains(i) ? _dragOrigin[i] + delta : _dragOrigin[i];

        _editSolid.RebuildFromPoints(_points);
        _doc.NotifyChanged();
        vp.Invalidate();
    }

    public void OnMouseUp(GLViewport vp, MouseEventArgs e)
    {
        if (_marquee)
        {
            FinishMarquee(vp);
            _marquee = false; _dragVp = null; vp.Invalidate(); return;
        }
        if (!_dragging) return;
        _dragging = false; _dragVp = null; _dragOrigin = [];
        _doc.NotifyChanged();
        vp.Invalidate();
    }

    public void OnKeyDown(GLViewport vp, KeyEventArgs e)
    {
        // Insert: split the selected edge by adding a midpoint vertex (then drag it out to add geometry).
        if (e.KeyCode == Keys.Insert && _editSolid != null && _selected.Count == 2)
        {
            var sel = _selected.ToList();
            var mid = (_points[sel[0]] + _points[sel[1]]) * 0.5f;
            _doc.RecordUndo();
            _points.Add(mid);
            _selected.Clear(); _selected.Add(_points.Count - 1);
            _editSolid.RebuildFromPoints(_points);
            _doc.NotifyChanged();
            vp.Invalidate();
            e.Handled = true;
        }
    }

    private void BeginDrag(GLViewport vp, float mh, float mv)
    {
        _doc.RecordUndo();
        _dragging = true; _dragVp = vp; _dragStartH = mh; _dragStartV = mv;
        _dragOrigin = new List<Vector3>(_points);
    }

    private void FinishMarquee(GLViewport vp)
    {
        var cam = vp.ActiveCamera2D!;
        var (hDir, vDir, _) = Bases(cam.Axis);
        float loH = MathF.Min(_mqH0, _mqH1), hiH = MathF.Max(_mqH0, _mqH1);
        float loV = MathF.Min(_mqV0, _mqV1), hiV = MathF.Max(_mqV0, _mqV1);
        // A click (no real box) just clears (already cleared on down if non-additive).
        if (MathF.Abs(hiH - loH) < 1e-3f && MathF.Abs(hiV - loV) < 1e-3f) return;
        for (int i = 0; i < _points.Count; i++)
        {
            float h = Vector3.Dot(_points[i], hDir), v = Vector3.Dot(_points[i], vDir);
            if (h >= loH && h <= hiH && v >= loV && v <= hiV) _selected.Add(i);
        }
    }

    // Unique edges of the current solid, as index pairs into _points (nearest-match).
    private IEnumerable<(int a, int b)> Edges()
    {
        if (_editSolid == null) yield break;
        var seen = new HashSet<(int, int)>();
        foreach (var f in _editSolid.Faces)
        {
            var vs = f.Vertices;
            for (int k = 0; k < vs.Count; k++)
            {
                int a = NearestPoint(vs[k]), b = NearestPoint(vs[(k + 1) % vs.Count]);
                if (a < 0 || b < 0 || a == b) continue;
                var key = a < b ? (a, b) : (b, a);
                if (seen.Add(key)) yield return key;
            }
        }
    }

    private int NearestPoint(Vector3 p)
    {
        int best = -1; float bestD = 4f; // within 2 units
        for (int i = 0; i < _points.Count; i++)
        {
            float d = (_points[i] - p).LengthSquared;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
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

    private static float Snap(float x, float g) => g < 1f ? x : MathF.Round(x / g) * g;
}
