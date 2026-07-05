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

    // The Windows clipboard is the cross-instance bridge: Copy mirrors the selection there as marked JSON, and
    // Paste pulls it back — so copying in one Megaton Hammer window and pasting in another works (the in-process
    // lists alone are per-process). The marker keeps us from trying to paste arbitrary text from other apps.
    private const string Marker = "MegatonHammerClipboard/v1\n";

    public static bool HasContent => Solids.Count > 0 || Actors.Count > 0 || SystemClipboardHasPayload();

    /// <summary>Copies the document's current selection into the clipboard (deep copies), and mirrors it to the
    /// Windows clipboard so another editor window can paste it.</summary>
    public static void CopyFrom(MapDocument doc)
    {
        Solids = doc.Solids.Where(s => s.IsSelected).Select(s => s.Clone()).ToList();
        Actors = doc.AllActors.Where(a => a.IsSelected).Select(a => a.Clone()).ToList();
        Center = ComputeCenter();
        WriteSystemClipboard();
    }

    /// <summary>Fresh deep copies of the clipboard contents (so multiple pastes don't alias). Pulls the latest
    /// cross-instance copy from the Windows clipboard first, so a copy from another window wins.</summary>
    public static (List<Solid> solids, List<ZActor> actors) Instantiate()
    {
        SyncFromSystemClipboard();
        return (Solids.Select(s => s.Clone()).ToList(), Actors.Select(a => a.Clone()).ToList());
    }

    // Mirror the current selection to the Windows clipboard as marked JSON (best-effort; the clipboard can be
    // momentarily locked by another process — the in-process copy still works if this fails).
    private static void WriteSystemClipboard()
    {
        try
        {
            string json = ProjectSerializer.SerializeSelection(Solids, Actors);
            System.Windows.Forms.Clipboard.SetText(Marker + json);
        }
        catch { /* clipboard busy/unavailable — same-window paste is unaffected */ }
    }

    // If the Windows clipboard holds a Megaton Hammer payload (from THIS or ANOTHER window), adopt it — it is by
    // definition the most recent copy across all windows. Otherwise keep the in-process contents.
    private static void SyncFromSystemClipboard()
    {
        try
        {
            if (!System.Windows.Forms.Clipboard.ContainsText()) return;
            string t = System.Windows.Forms.Clipboard.GetText();
            if (!t.StartsWith(Marker, StringComparison.Ordinal)) return;
            var (solids, actors) = ProjectSerializer.DeserializeSelection(t[Marker.Length..]);
            if (solids.Count == 0 && actors.Count == 0) return;
            Solids = solids; Actors = actors; Center = ComputeCenter();
        }
        catch { /* keep in-process contents on any clipboard/parse failure */ }
    }

    private static bool SystemClipboardHasPayload()
    {
        try { return System.Windows.Forms.Clipboard.ContainsText()
                     && System.Windows.Forms.Clipboard.GetText().StartsWith(Marker, StringComparison.Ordinal); }
        catch { return false; }
    }

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
