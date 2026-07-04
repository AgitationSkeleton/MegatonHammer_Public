using MegatonHammer.Editor;
using MegatonHammer.Forms;

namespace MegatonHammer.Tools;

/// <summary>
/// Texture / Face Edit tool (Valve Hammer style). In the 3D view: left-click a face to select
/// it and lift its mapping into the Face Edit dialog; Ctrl-click adds/removes faces from the
/// selection (multi-face editing); Shift-click selects the whole continuous coplanar surface (a
/// multi-brush wall/floor) so it can be aligned as one; right-click applies the current texture to
/// a face and auto-aligns it to an adjacent face already wearing that texture.
/// </summary>
public sealed class TextureTool : ITool
{
    private readonly MapDocument _doc;

    public string Name => "Texture";

    /// <summary>The current/active texture, set by the texture browser; the fallback "current material"
    /// for right-click apply when the Face Edit sheet isn't supplying one.</summary>
    public string? ActiveTexture { get; set; }

    /// <summary>Supplies the effective "current material" for right-click apply — the Face Edit sheet's
    /// shown texture when it's open, else <see cref="ActiveTexture"/>. Set by the host (MainForm) so the
    /// right-click paint uses whatever texture the user currently sees, fixing apply-while-sheet-open.</summary>
    public Func<string?>? CurrentMaterial { get; set; }

    /// <summary>Raised after a face's texture changes, so the browser can refresh usage.</summary>
    public event Action? FacePainted;

    /// <summary>Raised when the face selection changes, so the Face Edit dialog can refresh.</summary>
    public event Action? FaceSelectionChanged;

    /// <summary>Raised on Alt-click "lift" with the sampled face's texture, so the host can make it the
    /// global active texture (Hammer's eyedropper).</summary>
    public event Action<string?>? TextureLifted;

    /// <summary>Raised whenever a face is clicked (selected) with this tool, carrying that face's texture
    /// name — so an open Replace Textures dialog can fill its "Find texture" field from the clicked face (#1).</summary>
    public event Action<string?>? FaceClickedTexture;

    public TextureTool(MapDocument doc) { _doc = doc; }

    public void OnMouseDown(GLViewport vp, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (vp.ViewportType != ViewportType.Perspective3D) return;
        var cam = vp.ActiveCamera3D;
        if (cam == null) return;

        var ray = Picking.RayFromScreen(cam, e.X, e.Y, vp.Width, vp.Height);
        if (!Picking.PickFace(_doc.Scene, ray, out var hit))
        {
            // Click on empty space (no face under the cursor) deselects all faces — Hammer "click away to
            // deselect". A plain click only; Ctrl/Shift/Alt keep the current face selection.
            if ((Control.ModifierKeys & (Keys.Control | Keys.Shift | Keys.Alt)) == 0 && _doc.SelectedFaces.Any())
            {
                _doc.ClearFaceSelection();
                FaceSelectionChanged?.Invoke();
                _doc.NotifyChanged();
                vp.Invalidate();
            }
            return;
        }

        var mods = Control.ModifierKeys;
        if (mods.HasFlag(Keys.Alt))
        {
            // Hammer's eyedropper "lift": sample this face's texture + mapping. Make it the active
            // texture and select the face so the Face Edit sheet loads its scale/shift/rotation.
            ActiveTexture = hit.Face.TextureName;
            _doc.ClearFaceSelection();
            hit.Face.FaceSelected = true;
            TextureLifted?.Invoke(hit.Face.TextureName);
            FaceClickedTexture?.Invoke(hit.Face.TextureName);
            FaceSelectionChanged?.Invoke();
            _doc.NotifyChanged();
        }
        else if (mods.HasFlag(Keys.Shift))
        {
            // Select the continuous coplanar surface this face belongs to — every face across connected
            // brushes that shares its plane (a wall fills horizontally, a floor/ceiling across its
            // expanse). By default only faces of the SAME texture are grabbed (one painted band); the
            // Options ▸ Face Editing toggle switches to all adjacent faces regardless of texture.
            _doc.ClearFaceSelection();
            foreach (var f in TextureAlign.CoplanarRun(_doc.Solids, hit.Face, EditorSettings.ShiftSelectSameTextureOnly))
                f.FaceSelected = true;
            FaceClickedTexture?.Invoke(hit.Face.TextureName);
            FaceSelectionChanged?.Invoke();
            _doc.NotifyChanged();
        }
        else if (mods.HasFlag(Keys.Control))
        {
            hit.Face.FaceSelected = !hit.Face.FaceSelected;   // toggle in the multi-face set
            if (hit.Face.FaceSelected) FaceClickedTexture?.Invoke(hit.Face.TextureName);
            FaceSelectionChanged?.Invoke();
            _doc.NotifyChanged();
        }
        else
        {
            _doc.ClearFaceSelection();                        // select just this face + lift
            hit.Face.FaceSelected = true;
            FaceClickedTexture?.Invoke(hit.Face.TextureName);
            FaceSelectionChanged?.Invoke();
            _doc.NotifyChanged();
        }
        vp.Invalidate();
    }

    /// <summary>Applies the current material to the face under the cursor (Hammer's right-click "apply
    /// current" — works on any face whether or not its brush is selected) and aligns its mapping to a
    /// REFERENCE face. The reference is the face you left-clicked (the one selected / shown in the Face
    /// Edit sheet): the painted face inherits its scale / shift / rotation and is folded continuous across
    /// the edge they share. With no face selected it falls back to an adjacent face wearing the same
    /// texture. The material is the sheet's shown texture when it's open, else the active texture.
    /// Returns true if a face was painted.</summary>
    public bool ApplyAt(GLViewport vp, int sx, int sy)
    {
        if (vp.ViewportType != ViewportType.Perspective3D) return false;
        string? tex = CurrentMaterial?.Invoke() ?? ActiveTexture;
        if (string.IsNullOrEmpty(tex)) return false;
        var cam = vp.ActiveCamera3D;
        if (cam == null) return false;
        var ray = Picking.RayFromScreen(cam, sx, sy, vp.Width, vp.Height);
        if (!Picking.PickFace(_doc.Scene, ray, out var hit)) return false;
        _doc.RecordUndo();
        var face = hit.Face;
        // Align to the face the user explicitly picked as the reference — the selected face shown in the
        // sheet — NOT just any same-texture neighbour (every face here is the same texture, so that would
        // grab an arbitrary one like the top). Fall back to a same-texture neighbour only when nothing's
        // selected. TryAlignAcrossSeam folds across the edge the two faces share (or copies the reference's
        // mapping verbatim when they don't touch), so the result reflects the clicked reference's look.
        var reference = _doc.SelectedFaces.LastOrDefault(f => !ReferenceEquals(f, face))
                        ?? TextureAlign.AdjacentWithTexture(_doc.Solids, face, tex);
        face.TextureName = tex;
        if (reference != null) TextureAlign.TryAlignAcrossSeam(reference, face);
        _doc.NotifyChanged();
        FacePainted?.Invoke();
        return true;
    }

    public void OnMouseMove(GLViewport vp, MouseEventArgs e) { }
    public void OnMouseUp(GLViewport vp, MouseEventArgs e)   { }
    public void OnKeyDown(GLViewport vp, KeyEventArgs e)     { }
}
