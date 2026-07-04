using MegatonHammer.Forms;

namespace MegatonHammer.Tools;

/// <summary>
/// Hammer's Magnify tool: in a 2D view, left-click zooms in toward the cursor, Shift+click zooms
/// out, and dragging vertically zooms continuously about the press point (drag up = in, down =
/// out). Purely a navigation aid — it never touches the document. The 3D view ignores it.
/// </summary>
public sealed class MagnifyTool : ITool
{
    public string Name => "Magnify";

    private bool _dragging;
    private int  _anchorX, _anchorY, _lastY;

    public void OnMouseDown(GLViewport vp, MouseEventArgs e)
    {
        if (vp.ViewportType == ViewportType.Perspective3D || vp.ActiveCamera2D is not { } cam) return;

        if (e.Button == MouseButtons.Left)
        {
            // A plain click is a discrete zoom step; a drag (tracked in OnMouseMove) is continuous.
            _dragging = true; _anchorX = e.X; _anchorY = e.Y; _lastY = e.Y;
            bool zoomOut = (Control.ModifierKeys & Keys.Shift) != 0;
            cam.ZoomAt(e.X, e.Y, vp.Width, vp.Height, zoomOut ? 1f / 1.25f : 1.25f);
            vp.Invalidate();
        }
    }

    public void OnMouseMove(GLViewport vp, MouseEventArgs e)
    {
        if (!_dragging || vp.ActiveCamera2D is not { } cam) return;
        int dy = e.Y - _lastY;
        if (dy == 0) return;
        _lastY = e.Y;
        // ~0.5% per pixel; drag up (dy<0) zooms in. Anchor on the press point like Hammer.
        float factor = MathF.Pow(1.005f, -dy);
        cam.ZoomAt(_anchorX, _anchorY, vp.Width, vp.Height, factor);
        vp.Invalidate();
    }

    public void OnMouseUp(GLViewport vp, MouseEventArgs e) => _dragging = false;

    public void OnKeyDown(GLViewport vp, KeyEventArgs e) { }
}
