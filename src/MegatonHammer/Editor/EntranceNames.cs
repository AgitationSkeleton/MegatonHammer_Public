using System.Globalization;
using System.Text.RegularExpressions;

namespace MegatonHammer.Editor;

/// <summary>
/// Human-readable names for OoT gEntranceTable indices, parsed from the decomp/SoH entrance_table.h
/// (row order = entrance index). Used by the warp-brush properties UI so an author sees "Water Temple
/// (ENTR_WATER_TEMPLE_ENTRANCE)" instead of a bare hex index, and can pick a destination from a list.
///
/// Only OoT is covered — MM uses a different entrance layout, so <see cref="Available"/> is false there and
/// callers fall back to a raw index field. Loaded lazily from the source tree (same approach as the scene
/// name tables); if the header can't be found the table is simply empty (raw-index fallback still works).
/// </summary>
public static class EntranceNames
{
    /// <summary>One gEntranceTable row.</summary>
    public sealed record Entry(int Index, string EntranceMacro, string SceneMacro, string SceneName, bool IsLeader)
    {
        /// <summary>e.g. "Water Temple  (ENTR_WATER_TEMPLE_ENTRANCE)".</summary>
        public string Label => $"{SceneName}  ({EntranceMacro})";
        /// <summary>Combo text — scene name first so type-ahead matches the destination, hex index at the end.</summary>
        public string ComboText => $"{SceneName}  ({EntranceMacro})  [0x{Index:X4}]";
    }

    private static readonly string[] CandidatePaths =
    {
        @"D:\Copilot_OOT\WorkFolders\MegatonHammer\SoH\soh\include\tables\entrance_table.h",
        @"D:\Copilot_OOT\READ_ONLY_SourceCodes\Shipwright-develop\soh\include\tables\entrance_table.h",
        @"D:\Copilot_OOT\READ_ONLY_SourceCodes\dawn_and_dusk_soh-develop\soh\include\tables\entrance_table.h",
    };

    private static readonly Regex DefRe = new(
        @"DEFINE_ENTRANCE\(\s*(ENTR_\w+)\s*,\s*(SCENE_\w+)\s*,", RegexOptions.Compiled);

    private static readonly Lazy<IReadOnlyList<Entry>> _all = new(Load);
    private static readonly Lazy<Dictionary<int, Entry>> _byIndex =
        new(() => _all.Value.ToDictionary(e => e.Index));

    /// <summary>All entrance rows in table order.</summary>
    public static IReadOnlyList<Entry> All => _all.Value;

    /// <summary>Only the referenceable group-leader entrances (the first of each 4-layer group) — the
    /// clean set for a destination picker (variants share the leader's index via a runtime offset).</summary>
    public static IReadOnlyList<Entry> Leaders => _all.Value.Where(e => e.IsLeader).ToList();

    /// <summary>True when the table loaded (OoT source present) — otherwise callers use a raw index field.</summary>
    public static bool Available => _all.Value.Count > 0;

    /// <summary>Friendly label for an entrance index, or a bare hex fallback when unknown.</summary>
    public static string Label(int index) =>
        _byIndex.Value.TryGetValue(index, out var e) ? e.Label : $"Entrance 0x{index:X4}";

    /// <summary>The row for an index, or null.</summary>
    public static Entry? Get(int index) => _byIndex.Value.GetValueOrDefault(index);

    private static IReadOnlyList<Entry> Load()
    {
        string? path = CandidatePaths.FirstOrDefault(File.Exists);
        if (path == null) return Array.Empty<Entry>();
        var list = new List<Entry>();
        try
        {
            int idx = 0;
            bool groupStart = true;   // first DEFINE after a blank line leads a new scene-layer group
            foreach (var raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                var m = DefRe.Match(line);
                if (!m.Success)
                {
                    // A blank line (outside the header comment) separates layer groups; the next entry leads.
                    if (line.Length == 0) groupStart = true;
                    continue;
                }
                string entr = m.Groups[1].Value, scene = m.Groups[2].Value;
                list.Add(new Entry(idx, entr, scene, PrettyScene(scene), groupStart));
                groupStart = false;
                idx++;
            }
        }
        catch { return list; }
        return list;
    }

    // "SCENE_WATER_TEMPLE" -> "Water Temple"; "SCENE_SPOT00" -> "Spot00".
    private static string PrettyScene(string macro)
    {
        string s = macro.StartsWith("SCENE_", StringComparison.Ordinal) ? macro[6..] : macro;
        var parts = s.Split('_', StringSplitOptions.RemoveEmptyEntries)
                     .Select(w => w.Length == 0 ? w
                         : char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..].ToLower(CultureInfo.InvariantCulture));
        return string.Join(' ', parts);
    }
}
