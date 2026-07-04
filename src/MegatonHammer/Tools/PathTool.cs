using MegatonHammer.Editor;
using MegatonHammer.Forms;
using MegatonHammer.Rendering;
using OpenTK.Mathematics;

namespace MegatonHammer.Tools;

/// <summary>
/// Hammer func_tracktrain-style path editor. In a 2D view: click empty space to drop a waypoint
/// (extending the active path, or starting a new one); click a waypoint to select and drag it;
/// Delete removes the selected waypoint (and the path when its last point goes); Enter begins a
/// fresh path; Escape deselects. Paths are scene-level (the 0x0D waypoint lists that moving
/// platforms and time/action-driven NPCs follow).
/// </summary>
public sealed class PathTool : ITool
{
    private const float HandlePixels = 7f;

    private readonly MapDocument _doc;
    private bool        _dragging;
    private GLViewport? _dragVp;

    /// <summary>The path + waypoint currently being edited (−1 = none), read by the renderer to
    /// highlight the active waypoint.</summary>
    public int ActivePath = -1, ActivePoint = -1;

    public string Name => "Path";

    public PathTool(MapDocument doc) { _doc = doc; }

    private List<ZPath> Paths => _doc.Scene.Paths;

    /// <summary>#6: select a specific waypoint (used when it's double-clicked while another tool is active,
    /// so the host can switch to the Path tool and have that node ready to drag/edit).</summary>
    public void SelectNode(int pathIdx, int pointIdx)
    {
        if (pathIdx >= 0 && pathIdx < Paths.Count) { ActivePath = pathIdx; ActivePoint = pointIdx; }
    }

    public void OnMouseDown(GLViewport vp, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || vp.ViewportType == ViewportType.Perspective3D) return;
        var cam = vp.ActiveCamera2D!;
        var (mh, mv) = ScreenToOrtho(e.X, e.Y, vp.Width, vp.Height, cam);
        var (hDir, vDir, dDir) = Bases(cam.Axis);
        float r = HandlePixels * cam.Zoom * 1.6f;

        // Click on an existing waypoint → select + start dragging it.
        for (int pi = 0; pi < Paths.Count; pi++)
        {
            var pts = Paths[pi].Points;
            for (int i = 0; i < pts.Count; i++)
            {
                float h = Vector3.Dot(pts[i], hDir), v = Vector3.Dot(pts[i], vDir);
                if (MathF.Abs(mh - h) <= r && MathF.Abs(mv - v) <= r)
                {
                    _doc.RecordUndo();
                    ActivePath = pi; ActivePoint = i; _dragging = true; _dragVp = vp;
                    vp.Invalidate();
                    return;
                }
            }
        }

        // Empty space → append a waypoint to the active path (creating one if needed).
        _doc.RecordUndo();
        if (ActivePath < 0 || ActivePath >= Paths.Count)
        {
            Paths.Add(new ZPath { Name = $"Path {Paths.Count}" });
            ActivePath = Paths.Count - 1;
        }
        var path = Paths[ActivePath];
        float g = GridSnap.ActiveStep(vp.GridSize, cam.Zoom);
        float depth = path.Points.Count > 0 ? Vector3.Dot(path.Points[^1], dDir) : 0f;   // stay on the run's plane
        path.Points.Add(Snap(mh, g) * hDir + Snap(mv, g) * vDir + depth * dDir);
        ActivePoint = path.Points.Count - 1;
        _doc.NotifyChanged();
        vp.Invalidate();
    }

    public void OnMouseMove(GLViewport vp, MouseEventArgs e)
    {
        if (!_dragging || vp != _dragVp || ActivePath < 0 || ActivePath >= Paths.Count) return;
        var path = Paths[ActivePath];
        if (ActivePoint < 0 || ActivePoint >= path.Points.Count) return;
        var cam = vp.ActiveCamera2D!;
        var (hDir, vDir, dDir) = Bases(cam.Axis);
        var (mh, mv) = ScreenToOrtho(e.X, e.Y, vp.Width, vp.Height, cam);
        float g = GridSnap.ActiveStep(vp.GridSize, cam.Zoom);
        float depth = Vector3.Dot(path.Points[ActivePoint], dDir);
        path.Points[ActivePoint] = Snap(mh, g) * hDir + Snap(mv, g) * vDir + depth * dDir;
        _doc.NotifyChanged();
        vp.Invalidate();
    }

    public void OnMouseUp(GLViewport vp, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false; _dragVp = null;
        vp.Invalidate();
    }

    public void OnKeyDown(GLViewport vp, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Return)
        {
            ActivePath = -1; ActivePoint = -1; e.Handled = true;   // next click starts a new path
        }
        else if (e.KeyCode == Keys.Escape)
        {
            ActivePath = -1; ActivePoint = -1; vp.Invalidate(); e.Handled = true;
        }
        else if (e.KeyCode == Keys.Delete && ActivePath >= 0 && ActivePath < Paths.Count)
        {
            _doc.RecordUndo();
            var path = Paths[ActivePath];
            if (ActivePoint >= 0 && ActivePoint < path.Points.Count) path.Points.RemoveAt(ActivePoint);
            if (path.Points.Count == 0) { Paths.RemoveAt(ActivePath); ActivePath = -1; ActivePoint = -1; }
            else ActivePoint = Math.Clamp(ActivePoint, 0, path.Points.Count - 1);
            _doc.NotifyChanged(); vp.Invalidate(); e.Handled = true;
        }
        else if (e.KeyCode == Keys.L && ActivePath >= 0 && ActivePath < Paths.Count)
        {
            // L toggles the active path's loop/closed flag (Hammer track loop).
            _doc.RecordUndo();
            Paths[ActivePath].Closed = !Paths[ActivePath].Closed;
            _doc.NotifyChanged(); vp.Invalidate(); e.Handled = true;
        }
    }

    // Double-click a waypoint → edit that path's properties (name / loop / MM header fields).
    public void OnDoubleClick(GLViewport vp, MouseEventArgs e)
    {
        if (vp.ViewportType == ViewportType.Perspective3D || vp.ActiveCamera2D is not { } cam) return;
        var (mh, mv) = ScreenToOrtho(e.X, e.Y, vp.Width, vp.Height, cam);
        var (hDir, vDir, _) = Bases(cam.Axis);
        float r = HandlePixels * cam.Zoom * 1.6f;
        for (int pi = 0; pi < Paths.Count; pi++)
            foreach (var p in Paths[pi].Points)
                if (MathF.Abs(mh - Vector3.Dot(p, hDir)) <= r && MathF.Abs(mv - Vector3.Dot(p, vDir)) <= r)
                {
                    ActivePath = pi;
                    _doc.RecordUndo();
                    using var dlg = new PathPropertiesDialog(Paths[pi]);
                    if (dlg.ShowDialog(vp.FindForm()) == DialogResult.OK) _doc.NotifyChanged();
                    vp.Invalidate();
                    return;
                }
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
