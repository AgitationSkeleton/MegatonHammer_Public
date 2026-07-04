using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

/// <summary>
/// In-app clipboard for brushes and actors (Hammer's Copy/Cut/Paste). Holds deep copies, so the
/// originals can change or be deleted without affecting what will be pasted. <see cref="Center"/>
/// is the combined selection centre, used by Paste Special as the rotate origin / re-centre anchor.
/// </summary>
public static class EditClipboard
{
    public static List<Solid>  Solids { get; private set; } = [];
    public static List<ZActor> Actors { get; private set; } = [];
    public static Vector3 Center { get; private set; }

    public static bool HasContent => Solids.Count > 0 || Actors.Count > 0;

    /// <summary>Copies the document's current selection into the clipboard (deep copies).</summary>
    public static void CopyFrom(MapDocument doc)
    {
        Solids = doc.Solids.Where(s => s.IsSelected).Select(s => s.Clone()).ToList();
        Actors = doc.AllActors.Where(a => a.IsSelected).Select(a => a.Clone()).ToList();
        Center = ComputeCenter();
    }

    /// <summary>Fresh deep copies of the clipboard contents (so multiple pastes don't alias).</summary>
    public static (List<Solid> solids, List<ZActor> actors) Instantiate()
        => (Solids.Select(s => s.Clone()).ToList(), Actors.Select(a => a.Clone()).ToList());

    private static Vector3 ComputeCenter()
    {
        bool any = false;
        Vector3 mn = Vector3.Zero, mx = Vector3.Zero;
        void Acc(Vector3 p) { if (!any) { mn = mx = p; any = true; } else { mn = Vector3.ComponentMin(mn, p); mx = Vector3.ComponentMax(mx, p); } }

        foreach (var s in Solids) { var (a, b) = s.GetAABB(); Acc(a); Acc(b); }
        foreach (var a in Actors) Acc(a.Position);
        return any ? (mn + mx) * 0.5f : Vector3.Zero;
    }
}
