using MegatonHammer.Editor;

namespace MegatonHammer.Textures;

public enum TextureSort { Name, Type, Usage }

/// <summary>
/// In-memory catalogue of textures. Seeds a set of built-in procedural samples and can
/// additionally scan a folder of image files (e.g. extracted decomp/SoH assets).
/// </summary>
public sealed class TextureLibrary
{
    private readonly List<TextureEntry> _entries = [];
    private readonly Dictionary<string, TextureEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TextureEntry> Entries => _entries;
    public string? RootFolder { get; private set; }

    /// <summary>Category-name marker for textures borrowed from the other game (e.g. "⟨MM⟩ ").
    /// Categories beginning with this group below a divider in the area picker.</summary>
    public const string CrossGameMarker = "⟨";

    /// <summary>Drops any cross-game (other-game) textures previously added, so the source can be
    /// switched or disabled without restarting.</summary>
    public void RemoveCrossGameTextures()
    {
        _entries.RemoveAll(e =>
        {
            bool x = e.Category.StartsWith(CrossGameMarker, StringComparison.Ordinal)
                     || e.Folders.Any(f => f.StartsWith(CrossGameMarker, StringComparison.Ordinal));
            if (x) _byName.Remove(e.Name);
            return x;
        });
    }

    private static readonly string[] ImageExts = [".png", ".bmp", ".jpg", ".jpeg"];

    // N64 texture format suffixes commonly used by decomp asset names.
    private static readonly string[] N64Formats =
        ["rgba32", "rgba16", "rgb5a1", "ci8", "ci4", "ia16", "ia8", "ia4", "i8", "i4"];

    public TextureLibrary()
    {
        AddBuiltins();
    }

    public TextureEntry? Find(string? name)
        => name != null && _byName.TryGetValue(name, out var e) ? e : null;

    private void AddBuiltins()
    {
        foreach (var s in TextureFactory.Builtins)
            Register(new TextureEntry(s.Name, null, "Procedural", s.Category, s.Make));
    }

    /// <summary>
    /// Recursively scans <paramref name="folder"/> for image files and adds them to the
    /// library (built-ins are kept). Returns the number of files added.
    /// </summary>
    public int LoadFolder(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        RootFolder = folder;

        int added = 0;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories); }
        catch { return 0; }

        foreach (var path in files)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(ImageExts, ext) < 0) continue;

            string name = MakeUniqueName(Path.GetFileNameWithoutExtension(path));
            string type = DetectFormat(name) ?? ext.TrimStart('.');
            string cat  = Path.GetFileName(Path.GetDirectoryName(path)) ?? "Imported";

            Register(new TextureEntry(StripFormatSuffix(name), path, type, cat));
            added++;
        }
        return added;
    }

    /// <summary>Number of textures sourced from an O2R archive (vs. built-ins/files).</summary>
    public int O2RCount { get; private set; }

    /// <summary>
    /// Adds texture entries discovered in an O2R archive. Each decodes lazily via the
    /// shared <paramref name="source"/> the first time its image is shown or painted.
    /// </summary>
    public void AddO2RTextures(IReadOnlyList<O2RTexInfo> infos, O2RTextureSource source,
                               Func<string, string?>? friendly = null)
    {
        foreach (var info in infos)
        {
            string leaf = info.EntryName;
            int slash = leaf.LastIndexOf('/');
            string folder = slash > 0 ? leaf[..slash] : "o2r";
            string name = slash >= 0 ? leaf[(slash + 1)..] : leaf;
            name = MakeUniqueName(name);

            // Categorise by the scene's friendly name (like the vanilla path) when the folder is a
            // known scene; otherwise fall back to the folder's own leaf name.
            int fslash = folder.LastIndexOf('/');
            string folderLeaf = fslash >= 0 ? folder[(fslash + 1)..] : folder;
            string cat = friendly?.Invoke(folderLeaf) ?? folderLeaf;

            string type = N64TextureDecoder.FormatName(info.Type);
            var captured = info;
            Register(new TextureEntry(name, null, type, cat, () => source.Decode(captured)));
            O2RCount++;
        }
    }

    /// <summary>Number of textures sourced from a raw ROM scan.</summary>
    public int RomCount { get; private set; }

    /// <summary>
    /// Adds texture entries discovered by scanning a ROM's display lists. Each decodes
    /// lazily via the shared <paramref name="source"/> on first display/paint.
    /// </summary>
    public void AddRomTextures(IReadOnlyList<Rom.RomTexInfo> infos, Rom.RomTextureSource source,
                               IReadOnlyDictionary<int, string>? categories = null,
                               IReadOnlyList<HashSet<string>>? folderSets = null,
                               string? categoryPrefix = null)
    {
        for (int i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            string name = MakeUniqueName($"{(categoryPrefix != null ? "x" : "")}rom_{info.FileIndex:D4}_{info.Offset:X6}");
            string type = N64TextureDecoder.FormatName(info.Type);
            string cat  = categories != null && categories.TryGetValue(info.FileIndex, out var c)
                ? c : Rom.RomAssetIndex.Common;
            if (categoryPrefix != null) cat = categoryPrefix + cat;
            var captured = info;
            var srcRef = source;
            var entry = new TextureEntry(name, null, type, cat, () => srcRef.Decode(captured));
            if (folderSets != null && i < folderSets.Count && folderSets[i].Count > 0)
            {
                var set = categoryPrefix == null
                    ? folderSets[i]
                    : [.. folderSets[i].Select(f => categoryPrefix + f)];
                entry.SetFolders(set);
            }
            Register(entry);
            RomCount++;
        }
    }

    /// <summary>Distinct category folders, scenes first (alphabetical), Common/keep buckets last.</summary>
    public List<string> CategoryFolders()
    {
        return _entries.SelectMany(e => e.Folders).Distinct()
            .OrderBy(c => c == Rom.RomAssetIndex.Common || c.StartsWith("Common") || c.StartsWith("Keep") ? 1 : 0)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Entries in one folder (or all), filtered by search and sorted.</summary>
    public List<TextureEntry> QueryCategory(string? category, string? search, TextureSort sort)
    {
        IEnumerable<TextureEntry> q = _entries;
        bool searching = !string.IsNullOrWhiteSpace(search);
        // A search is GLOBAL (like Hammer's texture filter): it ignores the selected folder so you can find
        // a texture from anywhere. Texture names are content hashes (rom_1488_007218), so we also match the
        // folder/category (scene) name.
        if (!searching && !string.IsNullOrEmpty(category))
            q = q.Where(e => e.Folders.Contains(category));
        else if (searching)
        {
            const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
            string s = search!;
            q = q.Where(e => e.Name.Contains(s, OIC)
                          || e.Category.Contains(s, OIC)
                          || e.Folders.Any(fo => fo.Contains(s, OIC)));
        }
        return Sort(q, sort);
    }

    /// <summary>
    /// Audits the catalogue and collapses textures whose decoded pixels are byte-for-byte IDENTICAL
    /// into a single survivor — merging the duplicates' browser folders onto it and ALIASING the dropped
    /// names so any face or saved project still referencing a dropped name keeps resolving (Find returns
    /// the survivor). This is purely a catalogue/memory cleanup: the in-game look of every face is
    /// unchanged (a face just points at a pixel-identical twin). Returns the number of entries removed.
    /// Cross-game overlay textures (toggled separately) and special tool textures (WATERBOX/NODRAW/…) are
    /// never dropped. Decodes each candidate once for hashing; a small hash-colliding subset is re-decoded
    /// for an exact byte compare so a hash collision can never merge two different textures.
    /// </summary>
    public int DedupeIdentical() => ApplyDedupe(PlanDedupe());

    /// <summary>One survivor↔duplicate pairing produced by <see cref="PlanDedupe"/>.</summary>
    public readonly record struct DedupeMerge(TextureEntry Survivor, TextureEntry Duplicate);

    /// <summary>
    /// Computes which entries are byte-identical duplicates WITHOUT mutating the catalogue, so it can run
    /// off the UI thread during a texture load (decoding is the costly part). Feed the result to
    /// <see cref="ApplyDedupe"/> on the owning thread. Reads a snapshot so a concurrent add is harmless.
    /// </summary>
    public IReadOnlyList<DedupeMerge> PlanDedupe()
    {
        var snapshot = _entries.ToArray();

        // Pass 1 (cheap): bucket candidates by (w, h, content-hash). Skip cross-game overlays and undecodables.
        var buckets = new Dictionary<(int w, int h, ulong hash), List<TextureEntry>>();
        foreach (var e in snapshot)
        {
            if (IsCrossGame(e)) continue;
            var bytes = e.DecodeRawBytes(out int w, out int h);
            if (bytes == null) continue;
            var key = (w, h, Fnv1a(bytes));
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
            list.Add(e);
        }

        var merges = new List<DedupeMerge>();
        foreach (var bucket in buckets.Values)
        {
            if (bucket.Count < 2) continue;
            // Confirm exact byte-identity (guards the astronomically-rare hash collision); re-decode only
            // this small colliding subset.
            foreach (var group in GroupByExactBytes(bucket))
            {
                if (group.Count < 2) continue;
                var survivor = PickSurvivor(group);
                foreach (var dup in group)
                    if (!ReferenceEquals(dup, survivor)) merges.Add(new DedupeMerge(survivor, dup));
            }
        }
        return merges;
    }

    /// <summary>Applies a <see cref="PlanDedupe"/> result on the owning thread: merges each duplicate's
    /// folders onto its survivor, aliases the dropped name so existing references still resolve, and drops
    /// the duplicate entry. Returns the number removed.</summary>
    public int ApplyDedupe(IReadOnlyList<DedupeMerge> merges)
    {
        int removed = 0;
        var present = new HashSet<TextureEntry>(_entries);
        foreach (var (survivor, dup) in merges)
        {
            if (!present.Contains(dup) || !present.Contains(survivor)) continue;
            foreach (var f in dup.Folders) survivor.AddFolder(f);
            _byName[dup.Name] = survivor;   // alias dropped name → survivor (existing refs resolve)
            _entries.Remove(dup);
            present.Remove(dup);
            dup.InvalidateImage();
            removed++;
        }
        return removed;
    }

    private static bool IsCrossGame(TextureEntry e)
        => e.Category.StartsWith(CrossGameMarker, StringComparison.Ordinal)
           || e.Folders.Any(f => f.StartsWith(CrossGameMarker, StringComparison.Ordinal));

    private static ulong Fnv1a(byte[] data)
    {
        ulong h = 1469598103934665603UL;
        foreach (var b in data) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    // Splits a hash-bucket into groups of TRULY byte-identical textures (full pixel compare).
    private static List<List<TextureEntry>> GroupByExactBytes(List<TextureEntry> bucket)
    {
        var groups = new List<(byte[] bytes, List<TextureEntry> members)>();
        foreach (var e in bucket)
        {
            var bytes = e.DecodeRawBytes(out _, out _);
            if (bytes == null) continue;
            bool placed = false;
            foreach (var (gb, members) in groups)
                if (gb.AsSpan().SequenceEqual(bytes)) { members.Add(e); placed = true; break; }
            if (!placed) groups.Add((bytes, [e]));
        }
        return groups.Select(g => g.members).ToList();
    }

    // Which entry to keep: never lose a tool texture, then prefer built-in named samples, then friendly
    // file/O2R names over generated hash names (rom_1488_007218), then the one already in the most folders.
    private static TextureEntry PickSurvivor(List<TextureEntry> group)
        => group.OrderByDescending(SurvivorScore).ThenByDescending(e => e.Folders.Count).First();

    private static int SurvivorScore(TextureEntry e)
    {
        if (SpecialTextures.IsSpecial(e.Name)) return 4;
        if (e.TypeLabel == "Procedural") return 3;
        bool generated = e.Name.StartsWith("rom_", StringComparison.Ordinal)
                      || e.Name.StartsWith("xrom_", StringComparison.Ordinal);
        return generated ? 0 : 2;
    }

    /// <summary>Recomputes per-texture usage counts from the document's faces.</summary>
    public void RecountUsage(MapDocument doc)
    {
        foreach (var e in _entries) e.UsageCount = 0;
        foreach (var solid in doc.Solids)
            foreach (var face in solid.Faces)
            {
                var entry = Find(face.TextureName);
                if (entry != null) entry.UsageCount++;
            }
    }

    /// <summary>Returns entries filtered by a search term and ordered by the chosen key.</summary>
    public List<TextureEntry> Query(string? search, TextureSort sort)
    {
        IEnumerable<TextureEntry> q = _entries;
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(e => e.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                          || e.Category.Contains(search, StringComparison.OrdinalIgnoreCase));
        return Sort(q, sort);
    }

    private static List<TextureEntry> Sort(IEnumerable<TextureEntry> q, TextureSort sort) => sort switch
    {
        TextureSort.Usage => q.OrderByDescending(e => e.UsageCount)
                              .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        TextureSort.Type  => q.OrderBy(e => e.TypeLabel, StringComparer.OrdinalIgnoreCase)
                              .ThenBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
                              .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        // Name sort: in the "All" view, surface the OoT dev-test scenes first, then Hyrule Field,
        // then the rest alphabetically (a single-category view has a uniform rank, so it's unaffected).
        _                 => q.OrderBy(e => CategoryRank(e.Category))
                              .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
    };

    // OoT dev/test scenes (Pretty names from OotSceneNames) — pinned to the top of the All view.
    private static readonly HashSet<string> DevTestCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Test01", "Besitu", "Depth Test", "Syotes", "Syotes2", "Sutaru", "Hairal Niwa2", "Sasatest", "Testroom",
    };

    private static int CategoryRank(string category) =>
        DevTestCategories.Contains(category) ? 0
        : category.Equals("Hyrule Field", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    // ── Helpers ───────────────────────────────────────────────────────────

    private void Register(TextureEntry e)
    {
        _entries.Add(e);
        _byName[e.Name] = e;
    }

    private string MakeUniqueName(string baseName)
    {
        if (!_byName.ContainsKey(baseName)) return baseName;
        for (int i = 2; ; i++)
        {
            string candidate = $"{baseName}#{i}";
            if (!_byName.ContainsKey(candidate)) return candidate;
        }
    }

    private static string? DetectFormat(string fileName)
    {
        foreach (var fmt in N64Formats)
            if (fileName.EndsWith("." + fmt, StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith("_" + fmt, StringComparison.OrdinalIgnoreCase))
                return fmt;
        return null;
    }

    private static string StripFormatSuffix(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot > 0 && DetectFormat(name) != null) return name[..dot];
        return name;
    }
}
