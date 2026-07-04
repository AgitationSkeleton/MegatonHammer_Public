using MegatonHammer.Editor;
using MegatonHammer.Forms;
using MegatonHammer.Rendering;
using OpenTK.Mathematics;

namespace MegatonHammer.Tools;

public sealed class EntityTool : ITool
{
    private readonly MapDocument _doc;

    public string Name => "Entity";

    // The actor ID that will be placed on the next click; updated by HierarchyPanel.
    // Defaults to a treasure chest (En_Box 0x000A), not Player/Link 0x0000 — Player is the movable
    // Player Start marker, not a placeable actor.
    public ushort ActiveActorId { get; set; } = 0x000A;

    public EntityTool(MapDocument doc) { _doc = doc; }

    public void OnMouseDown(GLViewport vp, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        Vector3 world;
        if (vp.ViewportType == ViewportType.Perspective3D)
        {
            // Hammer (CToolEntity::OnLMouseDown3D) ray-casts to the surface under the cursor and places the
            // entity at the hit, grid-snapped. We do the same: hit point on the clicked face (origin sits
            // on the surface, like Hammer's ALIGN_BOTTOM), then grid-snap it when snapping is on (Ctrl
            // suspends). Fall back to the ground plane / a fixed distance if nothing is hit.
            var cam3 = vp.ActiveCamera3D;
            if (cam3 == null) return;
            var ray = Picking.RayFromScreen(cam3, e.X, e.Y, vp.Width, vp.Height);
            if (!Picking.PickPoint(_doc.Scene, ray, out world))
                world = ray.Origin + ray.Direction * 256f;
            if (Editor.GridSnap.SnappingActive) world = SnapVec(world, vp.GridSize);
        }
        else
        {
            world = ScreenToWorld(e.X, e.Y, vp.Width, vp.Height, vp.ActiveCamera2D!);
            // 2D placement snaps to the visible grid (honours the snap toggle + Ctrl-suspend).
            int g = Editor.GridSnap.ActiveStep(vp.GridSize, vp.ActiveCamera2D!.Zoom);
            world = SnapVec(world, g);
        }

        _doc.RecordUndo();
        bool dummy = ActiveActorId == EditorDummyLinkId;
        var actor = new ZActor
        {
            // The editor-only dummy Link renders as Link (0x0000) for scale but is never compiled.
            Number      = dummy ? (ushort)0x0000 : ActiveActorId,
            IsEditorOnly = dummy,
            DisplayName = dummy ? "Dummy Link (editor-only scale)" : "Unknown",
            XPos   = world.X,
            YPos   = world.Y,
            ZPos   = world.Z,
        };
        if (!dummy) SeedPlacementDefault(actor);
        _doc.AddActor(actor);
    }

    /// <summary>Sentinel "actor id" for the editor-only insertable dummy Link (scale reference). Never a
    /// real OoT/MM actor id; placing it creates an IsEditorOnly Link that the exporters skip.</summary>
    public const ushort EditorDummyLinkId = 0xFFFF;

    // Seed a freshly-placed actor's params so it FUNCTIONS out of the box, instead of the raw 0 default that
    // leaves some actors inert (a Beamos blind, a proximity trigger zero-range, an object that won't spawn).
    // Only the "broken at 0" field is filled — everything else stays at 0/editable. Most actors work fine at
    // 0 (their type 0 is a valid variant) and are intentionally NOT listed here. Verified vs the decomp.
    private void SeedPlacementDefault(ZActor a)
    {
        bool oot = !_doc.IsMM;
        ushort chestId = oot ? (ushort)0x000A : (ushort)0x0006;
        // Chest: give a UNIQUE treasure flag (else every chest shares one — opening one marks them all opened).
        if (a.Number == chestId)
            a.Variable = (ushort)((a.Variable & ~0x1F) | (NextFreeChestFlag(chestId) & 0x1F));
        if (!oot) return;   // the rest are OoT-specific
        switch (a.Number)
        {
            case 0x008A:   // En_Vm Beamos — sightRange = (params>>8)*40; 0 = permanently blind (z_en_vm.c:148)
                if ((a.Variable >> 8) == 0) a.Variable = (ushort)((a.Variable & 0x00FF) | 0x0F00);   // 600u
                break;
            case 0x00B8:   // Bg_Spot09_Obj — param 0 = "don't spawn"; default to the visible tent (type 3)
                if (a.Variable == 0) a.Variable = 0x0003;
                break;
            case 0x0185:   // En_Wonder_Talk2 — proximity range = (rot.z ones digit)*40, gated rot.z>0; 0 = never
                if (a.ZRot == 0) a.ZRot = 10;   // 400u trigger range (z_en_wonder_talk2.c:49-58)
                break;
            case 0x01AF:   // En_Wf Wolfos — switch-flag byte 0xFF = "none"; only the White variant sets it (z_en_wf.c:223)
                if ((a.Variable >> 8) == 0) a.Variable = (ushort)((a.Variable & 0x00FF) | 0xFF00);
                break;
            case 0x00A7:   // En_Encount1 spawner — params<=0 self-kills; seed 1 alive / 1 total (z_en_encount1.c:34)
                if (a.Variable == 0) a.Variable = (1 << 6) | 1;
                break;
        }
    }

    // The lowest treasure flag (0..31) not already used by another chest of the same id in the scene, so a
    // newly-placed chest doesn't collide with an existing one (shared flag = opening one opens both). Falls
    // back to 0 when all 32 are taken (a 32-chest room is beyond the vanilla flag budget anyway).
    private int NextFreeChestFlag(ushort chestId)
    {
        var used = new bool[32];
        foreach (var a in _doc.AllActors)
            if (a.Number == chestId) used[a.Variable & 0x1F] = true;
        for (int f = 0; f < 32; f++) if (!used[f]) return f;
        return 0;
    }

    public void OnMouseMove(GLViewport vp, MouseEventArgs e) { }
    public void OnMouseUp(GLViewport vp, MouseEventArgs e)   { }
    public void OnKeyDown(GLViewport vp, KeyEventArgs e)     { }

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
        return new(MathF.Round(v.X / gf) * gf,
                   MathF.Round(v.Y / gf) * gf,
                   MathF.Round(v.Z / gf) * gf);
    }
}
