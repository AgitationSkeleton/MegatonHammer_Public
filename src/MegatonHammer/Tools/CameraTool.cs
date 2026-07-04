using MegatonHammer.Editor;
using MegatonHammer.Forms;
using MegatonHammer.Rendering;
using OpenTK.Mathematics;

namespace MegatonHammer.Tools;

/// <summary>
/// Hammer's Camera tool. In a 2D view it places and drags persistent camera gizmos (eye + look
/// point); the active camera drives the live 3D view. Click empty space to drop a new camera and
/// drag to aim it; grab a camera's eye or look handle to move/re-aim it; PgUp/PgDn cycles the active
/// camera, Delete removes it. (A single click with no existing camera still just frames the 3D view.)
/// </summary>
public sealed class CameraTool : ITool
{
    public string Name => "Camera";

    private readonly MapDocument     _doc;
    private readonly Func<Camera3D?> _get3DCam;
    private readonly Action          _redrawAll;

    private const float GrabPixels = 8f;

    private int  _dragCam = -1;     // index of the camera being dragged (-1 = none)
    private bool _dragLook;         // true = dragging the look handle, false = the eye handle

    public CameraTool(MapDocument doc, Func<Camera3D?> get3DCam, Action redrawAll)
    {
        _doc = doc;
        _get3DCam = get3DCam;
        _redrawAll = redrawAll;
    }

    public void OnMouseDown(GLViewport vp, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || vp.ViewportType == ViewportType.Perspective3D) return;
        if (vp.ActiveCamera2D is not { } cam2D) return;
        var (oh, ov) = ScreenToOrtho(cam2D, e.X, e.Y, vp.Width, vp.Height);
        float r = GrabPixels * cam2D.Zoom * 1.6f;

        // Grab an existing camera's eye or look handle.
        for (int i = 0; i < _doc.Cameras.Count; i++)
        {
            var (eh, ev) = OrthoPos(_doc.Cameras[i].Eye, cam2D.Axis);
            var (lh, lv) = OrthoPos(_doc.Cameras[i].Look, cam2D.Axis);
            if (Near(oh, ov, lh, lv, r)) { _dragCam = i; _dragLook = true;  _doc.ActiveCameraIndex = i; Activate(); _redrawAll(); return; }
            if (Near(oh, ov, eh, ev, r)) { _dragCam = i; _dragLook = false; _doc.ActiveCameraIndex = i; Activate(); _redrawAll(); return; }
        }

        // Otherwise drop a new camera at the click and drag its look handle to aim it.
        var eye = MapToWorld(cam2D, new Vector3(0, 96, 0), e.X, e.Y, vp.Width, vp.Height);
        var (fh, fv) = cam2D.Axis switch
        {
            ViewAxis.Top   => (new Vector3(0, 0, -1), Vector3.Zero),   // look "north" by default
            _              => (new Vector3(1, 0, 0),  Vector3.Zero),
        };
        var cam = new MapDocument.EditorCamera { Eye = eye, Look = eye + fh * 128f };
        _doc.Cameras.Add(cam);
        _dragCam = _doc.Cameras.Count - 1; _dragLook = true;
        _doc.ActiveCameraIndex = _dragCam;
        Activate();
        _redrawAll();
    }

    public void OnMouseMove(GLViewport vp, MouseEventArgs e)
    {
        if (_dragCam < 0 || _dragCam >= _doc.Cameras.Count || vp.ActiveCamera2D is not { } cam2D) return;
        var ec = _doc.Cameras[_dragCam];
        // Keep the off-plane axis from the handle's current value (a Top drag sets X/Z, not height).
        var keep = _dragLook ? ec.Look : ec.Eye;
        var p = MapToWorld(cam2D, keep, e.X, e.Y, vp.Width, vp.Height);
        if (_dragLook) ec.Look = p; else ec.Eye = p;
        Activate();
        _redrawAll();
    }

    public void OnMouseUp(GLViewport vp, MouseEventArgs e) => _dragCam = -1;

    public void OnKeyDown(GLViewport vp, KeyEventArgs e)
    {
        if (_doc.Cameras.Count == 0) return;
        switch (e.KeyCode)
        {
            case Keys.PageDown: _doc.ActiveCameraIndex = (_doc.ActiveCameraIndex + 1) % _doc.Cameras.Count; Activate(); _redrawAll(); e.Handled = true; break;
            case Keys.PageUp:   _doc.ActiveCameraIndex = (_doc.ActiveCameraIndex - 1 + _doc.Cameras.Count) % _doc.Cameras.Count; Activate(); _redrawAll(); e.Handled = true; break;
            case Keys.Delete:
                if (_doc.ActiveCameraIndex >= 0 && _doc.ActiveCameraIndex < _doc.Cameras.Count)
                {
                    _doc.Cameras.RemoveAt(_doc.ActiveCameraIndex);
                    _doc.ActiveCameraIndex = Math.Min(_doc.ActiveCameraIndex, _doc.Cameras.Count - 1);
                    _redrawAll(); e.Handled = true;
                }
                break;
        }
    }

    /// <summary>Per-camera gizmo lines projected into the given 2D view (eye→look + which is active),
    /// for the viewport overlay.</summary>
    public IEnumerable<(float eh, float ev, float lh, float lv, bool active)> Gizmos2D(ViewAxis axis)
    {
        for (int i = 0; i < _doc.Cameras.Count; i++)
        {
            var (eh, ev) = OrthoPos(_doc.Cameras[i].Eye, axis);
            var (lh, lv) = OrthoPos(_doc.Cameras[i].Look, axis);
            yield return (eh, ev, lh, lv, i == _doc.ActiveCameraIndex);
        }
    }

    // Drive the live 3D view from the active camera gizmo.
    private void Activate()
    {
        if (_get3DCam() is not { } cam) return;
        if (_doc.ActiveCameraIndex < 0 || _doc.ActiveCameraIndex >= _doc.Cameras.Count) return;
        var ec = _doc.Cameras[_doc.ActiveCameraIndex];
        cam.Position = ec.Eye;
        var dir = ec.Look - ec.Eye;
        if (dir.LengthSquared < 1e-3f) return;
        cam.Yaw   = MathHelper.RadiansToDegrees(MathF.Atan2(dir.Z, dir.X));
        cam.Pitch = MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(dir.Y / dir.Length, -1f, 1f)));
    }

    private static bool Near(float ah, float av, float bh, float bv, float r)
        => MathF.Abs(ah - bh) <= r && MathF.Abs(av - bv) <= r;

    private static (float h, float v) ScreenToOrtho(Camera2D cam, int sx, int sy, int w, int h)
        => (cam.PanX + (sx - w * 0.5f) * cam.Zoom, cam.PanY - (sy - h * 0.5f) * cam.Zoom);

    private static (float h, float v) OrthoPos(Vector3 world, ViewAxis axis) => axis switch
    {
        ViewAxis.Top   => (world.X, -world.Z),
        ViewAxis.Front => (world.X,  world.Y),
        ViewAxis.Side  => (world.Z,  world.Y),
        _              => (world.X,  world.Y),
    };

    // Screen point in a 2D view → world position, keeping the off-plane axis from <paramref name="keep"/>.
    private static Vector3 MapToWorld(Camera2D cam2D, Vector3 keep, int sx, int sy, int w, int h)
    {
        float wh = cam2D.PanX + (sx - w * 0.5f) * cam2D.Zoom;
        float wv = cam2D.PanY - (sy - h * 0.5f) * cam2D.Zoom;
        return cam2D.Axis switch
        {
            ViewAxis.Top   => new Vector3(wh, keep.Y, -wv),
            ViewAxis.Front => new Vector3(wh, wv, keep.Z),
            ViewAxis.Side  => new Vector3(keep.X, wv, wh),
            _              => keep,
        };
    }
}
