using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

/// <summary>
/// Per-actor upright-orientation corrections for the editor's model preview. A handful of actors
/// (mostly fliers) store their mesh lying down in the skeleton REST pose — in-game the actor's
/// draw/animation code stands them up, which the static editor doesn't execute. These base
/// rotations (degrees, applied in object space before the placement yaw) approximate that pose.
///
/// Some actors also rotate themselves on spawn (e.g. En_Box adds 0x8000 / 180° to its facing in
/// Init), so the in-game chest faces 180° from the authored yaw; the editor mirrors that here so the
/// preview is WYSIWYG. Tables are PER-GAME — an id means a different actor in OoT vs MM.
///
/// The values are deliberately easy to tune by eye: import a scene, eyeball the actor, and adjust
/// the degrees here. Most actors need no entry (humanoid skeletons are authored upright).
/// </summary>
public static class ActorOrientation
{
    // OoT actorId → (pitchX, yawY, rollZ) degrees applied to the model.
    private static readonly Dictionary<ushort, Vector3> OotCorrections = new()
    {
        [0x01D] = new(-90f, 0f, 0f),   // En_Peehat (Peahat) — rest pose lies forward; tip it upright
        // En_Owl (Kaepora Gaebora): NO correction — the perching skeleton + gOwlPerchAnim frame 0 already
        // stands it upright (like the MM owl 0xAF, which needs none). The old -90f was a stale leftover from
        // when it used the flying skeleton's lying-down rest pose, and it tipped the perched owl into the floor.
        [0x009] = new(90f, 0f, 0f),    // En_Door — gameplay_keep door panels lie flat; stand them up
        // En_Box (treasure chest) 0x00A: NO correction. Init's `world.rot.y += 0x8000` (z_en_box.c) only
        // drives item-ejection PHYSICS (Math_SinS(world.rot.y) in the open code) — the chest MODEL is drawn
        // from shape.rot.y (standard Actor_Draw), which keeps the AUTHORED yaw. The old +180 rotated the
        // preview 180 from where the chest actually faces in-game (the "chests are backwards" report).
    };

    // MM actorId → correction (ids differ from OoT).
    private static readonly Dictionary<ushort, Vector3> MmCorrections = new()
    {
        [0x006] = new(0f, 180f, 0f),   // En_Box (treasure chest) — Init adds 0x8000 to shape.rot.y
    };

    /// <summary>Object-space rotation correction for an actor (zero = none).</summary>
    public static Vector3 For(ushort actorId, bool mm = false) =>
        (mm ? MmCorrections : OotCorrections).GetValueOrDefault(actorId);
}
