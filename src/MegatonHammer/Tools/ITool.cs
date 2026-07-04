using MegatonHammer.Forms;

namespace MegatonHammer.Tools;

public interface ITool
{
    string Name { get; }
    void OnMouseDown(GLViewport vp, MouseEventArgs e);
    void OnMouseMove(GLViewport vp, MouseEventArgs e);
    void OnMouseUp(GLViewport vp, MouseEventArgs e);
    void OnKeyDown(GLViewport vp, KeyEventArgs e);

    /// <summary>Double-click in a viewport, dispatched when the actor double-click test missed.
    /// Default: nothing (only tools that need it, e.g. the Path tool's properties dialog, override).</summary>
    void OnDoubleClick(GLViewport vp, MouseEventArgs e) { }
}
