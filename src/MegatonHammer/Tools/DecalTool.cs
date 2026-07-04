using MegatonHammer.Editor;
using MegatonHammer.Forms;

namespace MegatonHammer.Tools;

/// <summary>
/// Decal / overlay tool (Hammer's "Apply Decals"). Left-click a surface in the 3D view to drop a DECAL
/// ENTITY there: a small textured quad stuck to the surface (with a position marker + its own size), not a
/// whole-face retexture. The decal is then selectable/movable/scalable like a brush (Select tool, 2D views)
/// and is BAKED onto the surface as a projected polygon at compile time. Shift-click a decal removes it.
/// </summary>
public sealed class DecalTool : ITool
{
    private readonly MapDocument _doc;

    public string Name => "Decal";

    /// <summary>Texture the newly-placed decal uses; mirrors the texture browser selection.</summary>
    public string? ActiveTexture { get; set; }

    /// <summary>Raised after a decal is placed, so the browser can refresh usage counts.</summary>
    public event Action? Applied;

    public DecalTool(MapDocument doc) { _doc = doc; }

    public void OnMouseDown(GLViewport vp, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || vp.ViewportType != ViewportType.Perspective3D) return;
        var cam = vp.ActiveCamera3D;
        if (cam == null) return;

        var ray = Picking.RayFromScreen(cam, e.X, e.Y, vp.Width, vp.Height);
        if (!Picking.PickFace(_doc.Scene, ray, out var hit)) return;

        // Shift-click on an existing decal near the hit removes it (Hammer's clear).
        if ((Control.ModifierKeys & Keys.Shift) != 0)
        {
            var near = _doc.AllDecals
                .Where(d => (d.Position - hit.Point).Length < MathF.Max(d.SizeU, d.SizeV) + 8f)
                .OrderBy(d => (d.Position - hit.Point).LengthSquared).FirstOrDefault();
            if (near != null) { _doc.RecordUndo(); _doc.RemoveDecal(near); _doc.NotifyChanged(); vp.Invalidate(); }
            return;
        }

        _doc.RecordUndo();
        _doc.ClearSelection();
        var decal = new Decal
        {
            Position = hit.Point, Normal = hit.Face.Plane.Normal,
            TextureName = ActiveTexture, IsSelected = true,
        };
        _doc.AddDecal(decal);
        _doc.NotifyChanged();
        Applied?.Invoke();
        vp.Invalidate();
    }

    public void OnMouseMove(GLViewport vp, MouseEventArgs e) { }
    public void OnMouseUp(GLViewport vp, MouseEventArgs e)   { }
    public void OnKeyDown(GLViewport vp, KeyEventArgs e)     { }
}
