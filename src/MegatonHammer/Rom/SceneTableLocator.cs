namespace MegatonHammer.Rom;

/// <summary>
/// Locates OoT/MM <c>gSceneTable</c> inside a decompressed ROM image without version
/// constants. The table is a run of 0x14-byte entries whose first word
/// (sceneFile.vromStart) matches a real dmadata file boundary.
/// </summary>
public static class SceneTableLocator
{
    public const int EntrySize   = 0x14;   // OoT gSceneTable entry (sceneFile + titleFile + config)
    public const int MmEntrySize = 0x10;   // MM gSceneTable entry (sceneFile + titleTextId + drawConfig, no titleFile)
    private const int MaxNull  = 12;   // max consecutive unused {0,0} slots within a table

    // Title-card presence for OoT scenes 0x00..0x0D (1 = has title card). This sequence
    // (from the decomp scene table) is a unique fingerprint for gSceneTable: scenes 0-9
    // have titles, 0x0A (Ganon's Tower) has none, 0x0B-0x0D have titles.
    private static readonly int[] OoTTitleMask =
        [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1];

    // Offset = table start; Count = number of slots (scene id range, incl. NULL slots).
    public readonly record struct Result(int Offset, int Count);

    /// <summary>
    /// Locates MM's gSceneTable. MM entries are 0x10 bytes: a single RomFile (sceneFile) plus
    /// titleTextId (u16) + unk + drawConfig + unk. We find the longest run of valid sceneFile
    /// entries (start &lt; end, start is a dmadata file boundary, plausible size) at 0x10 stride,
    /// tolerating UNSET {0…} slots — MM's ~115-entry table is by far the longest such run.
    /// </summary>
    public static Result FindMM(byte[] flat, IEnumerable<RomFile> files) => FindMM(flat, files, null);

    /// <summary>
    /// MM locator. When <paramref name="rom"/> is given, candidate tables are confirmed by
    /// loading a few of their scene files and checking they start with a real scene header —
    /// this rejects the false-positive 0x10-stride file tables that the value heuristic alone
    /// would match.
    /// </summary>
    public static Result FindMM(byte[] flat, IEnumerable<RomFile> files, RomImage? rom)
    {
        const int Stride = 0x10;
        var starts = new HashSet<uint>();
        var fileByVrom = new Dictionary<uint, int>();
        foreach (var f in files) if (f.Exists) { starts.Add(f.VromStart); fileByVrom[f.VromStart] = f.Index; }

        int len = flat.Length, bestOff = -1, bestCount = 0;
        for (int p = 0; p + Stride <= len; p += 4)
        {
            if (!IsMmSceneFile(flat, p, starts)) continue;
            int valid = 0, idx = 0;
            for (int q = p; q + Stride <= len; q += Stride, idx++)
            {
                if (IsMmSceneFile(flat, q, starts)) { valid++; }
                else if (U32(flat, q) == 0 && U32(flat, q + 4) == 0) { }
                else break;
            }
            // Confirm by content: the entries must point at actual scene headers.
            if (valid > bestCount && (rom == null || EntriesAreScenes(flat, p, idx, fileByVrom, rom)))
            { bestCount = valid; bestOff = p; }
            if (idx > 4) p = p + (idx - 1) * Stride;   // skip past this run
        }
        return bestCount >= 30 ? new Result(bestOff, FullSpan(flat, bestOff, starts)) : new Result(-1, 0);
    }

    // True if several of the run's sceneFile entries point at files that begin with a valid
    // scene header (first command 0x00-0x1F and an end marker 0x14 within the first 0x300).
    private static bool EntriesAreScenes(byte[] d, int off, int slots, Dictionary<uint, int> fileByVrom, RomImage rom)
    {
        int sampled = 0, ok = 0;
        for (int i = 0; i < slots && sampled < 6; i++)
        {
            uint s = U32(d, off + i * 0x10);
            if (s == 0 || !fileByVrom.TryGetValue(s, out int fi)) continue;
            sampled++;
            byte[] sd; try { sd = rom.GetFile(fi); } catch { continue; }
            if (IsSceneHeader(sd)) ok++;
        }
        return ok >= 3;
    }

    private static bool IsSceneHeader(byte[] sd)
    {
        if (sd.Length < 16) return false;
        if (sd[0] > 0x1F) return false;                       // first command must be a scene cmd
        for (int p = 0; p + 8 <= sd.Length && p < 0x300; p += 8)
        {
            byte c = sd[p];
            if (c == 0x14) return true;                       // reached the end marker
            if (c > 0x1F) return false;                       // not a scene command → not a header
        }
        return false;
    }

    private static int FullSpan(byte[] d, int off, HashSet<uint> starts)
    {
        int lastReal = -1, nulls = 0, idx = 0;
        for (int q = off; q + 0x10 <= d.Length; q += 0x10, idx++)
        {
            if (IsMmSceneFile(d, q, starts)) { lastReal = idx; nulls = 0; }
            else if (U32(d, q) == 0 && U32(d, q + 4) == 0) { if (++nulls > MaxNull * 4) break; }
            else break;
        }
        return lastReal + 1;
    }

    private static bool IsMmSceneFile(byte[] d, int o, HashSet<uint> starts)
    {
        if (o + 0x10 > d.Length) return false;
        uint s = U32(d, o), e = U32(d, o + 4);
        if (s >= e || e - s < 0x800 || e - s > 0x400000) return false;
        if (!starts.Contains(s)) return false;
        uint titleId = (uint)((d[o + 8] << 8) | d[o + 9]);
        byte drawCfg = d[o + 0x0B];
        return titleId < 0x2000 && drawCfg < 0x40;   // plausible title-text id + draw config
    }

    public static Result Find(byte[] flat, IEnumerable<RomFile> files)
    {
        var starts = new HashSet<uint>();
        foreach (var f in files) if (f.Exists) starts.Add(f.VromStart);

        int len = flat.Length;
        for (int p = 0; p + OoTTitleMask.Length * EntrySize <= len; p += 4)
        {
            if (!MatchesFingerprint(flat, p, len, starts)) continue;

            // Confirmed table start; measure how many slots it spans (incl. NULL slots).
            int lastReal = -1, nulls = 0, idx = 0;
            for (int q = p; q + EntrySize <= len; q += EntrySize, idx++)
            {
                if (IsSceneFile(flat, q, len, starts)) { lastReal = idx; nulls = 0; }
                else if (IsNull(flat, q)) { if (++nulls > MaxNull) break; }
                else break;
            }
            return new Result(p, lastReal + 1);
        }
        return new Result(-1, 0);
    }

    // Requires sceneFile validity for the fingerprint span and the exact title-presence
    // pattern, which together uniquely identify gSceneTable.
    private static bool MatchesFingerprint(byte[] d, int p, int len, HashSet<uint> starts)
    {
        for (int i = 0; i < OoTTitleMask.Length; i++)
        {
            int o = p + i * EntrySize;
            if (!IsSceneFile(d, o, len, starts)) return false;
            bool hasTitle = !(U32(d, o + 8) == 0 && U32(d, o + 12) == 0);
            if (hasTitle != (OoTTitleMask[i] == 1)) return false;
        }
        return true;
    }

    // Validates a 0x14-byte SceneTableEntry: a real sceneFile RomFile followed by a
    // title RomFile that is NULL or another real file. The paired second RomFile over
    // many consecutive 0x14-stride entries is what distinguishes gSceneTable from other
    // file-pointer tables (the trailing config bytes are NOT zero, so they aren't tested).
    // Note: validates entry VALUES against the dmadata file-start set, NOT the buffer
    // length — the table holds ROM-wide VROM addresses, so it can be scanned inside the
    // code file (where those addresses far exceed the file's own length) as well as a
    // flat ROM image.
    private static bool IsSceneFile(byte[] d, int o, int len, HashSet<uint> starts)
    {
        uint s = U32(d, o), e = U32(d, o + 4);
        if (s >= e || e - s < 0x800 || e - s > 0x400000) return false;
        if (!starts.Contains(s)) return false;

        uint ts = U32(d, o + 8), te = U32(d, o + 12);
        return (ts == 0 && te == 0) ||
               (ts < te && te - ts <= 0x80000 && starts.Contains(ts));
    }

    private static bool IsNull(byte[] d, int o) =>
        U32(d, o) == 0 && U32(d, o + 4) == 0 && U32(d, o + 8) == 0 && U32(d, o + 12) == 0;

    private static uint U32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
}
