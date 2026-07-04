namespace MegatonHammer.Editor;

/// <summary>One row in the scene-music dropdown. <see cref="Opposite"/> marks a song from the
/// other game (needs the cross-game source); <see cref="IsDivider"/> rows are non-selectable
/// separators between the two games' lists.</summary>
public sealed record MusicEntry(byte Id, string Name, bool Opposite, bool IsDivider)
{
    public static MusicEntry Divider(string label) => new(0, label, false, true);
    public override string ToString() => IsDivider ? Name : $"{Id:X2}  {Name}{(Opposite ? "  (cross-game)" : "")}";
}

/// <summary>
/// Builds the combined OoT+MM music list for the scene-music picker, and tracks which songs can't
/// be borrowed across games because their instruments don't exist in the other game's audiobank.
/// </summary>
public static class CrossGameAudio
{
    // Curated soundfont-incompatible songs (commented out of the cross-game list "for now", per the
    // request). Conservative and easily extended — keyed by each song's OWN game/sequence id.
    // OoT songs whose instruments aren't in MM's audiobank:
    private static readonly HashSet<byte> OotSongsBadInMm = [0x26 /* Jabu-Jabu's Belly */];
    // MM songs whose instruments aren't in OoT's audiobank:
    private static readonly HashSet<byte> MmSongsBadInOot =
    [
        0x04, 0x06,        // Majora's Mask Theme (unique synth/choir voices)
        0x0A,              // Happy Mask Salesman's Theme
        0x69, 0x6A, 0x6B,  // Battle: Majora's Wrath / Incarnation / Mask
    ];

    /// <summary>True if a song from <paramref name="songIsOoT"/>'s game can play under the other
    /// game's audiobank without missing instruments.</summary>
    public static bool IsCrossGameCompatible(bool songIsOoT, byte id) =>
        songIsOoT ? !OotSongsBadInMm.Contains(id) : !MmSongsBadInOot.Contains(id);

    /// <summary>
    /// The scene-music dropdown rows: the native game's songs, then — when cross-game music is
    /// enabled and an opposite-game source is configured — a divider and the other game's
    /// soundfont-compatible songs.
    /// </summary>
    public static List<MusicEntry> BuildList(bool nativeIsOoT)
    {
        var list = new List<MusicEntry>();
        foreach (var (id, name) in SequenceNames.Load(nativeIsOoT))
            list.Add(new MusicEntry(id, name, Opposite: false, IsDivider: false));

        if (EditorSettings.EnableCrossGameMusic && EditorSettings.HasOppositeSource(nativeIsOoT))
        {
            bool oppIsOoT = !nativeIsOoT;
            string label = oppIsOoT ? "──────  Ocarina of Time  ──────" : "──────  Majora's Mask  ──────";
            list.Add(MusicEntry.Divider(label));
            foreach (var (id, name) in SequenceNames.Load(oppIsOoT))
                if (IsCrossGameCompatible(oppIsOoT, id))
                    list.Add(new MusicEntry(id, name, Opposite: true, IsDivider: false));
        }
        return list;
    }
}
