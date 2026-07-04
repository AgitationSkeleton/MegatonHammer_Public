using System.Text.RegularExpressions;

namespace MegatonHammer.Rom;

/// <summary>
/// Resolves OoT object files: maps an object name (object_sk2) → object id (from the
/// decomp object_table.h) → its VROM range (from gObjectTable, located in the ROM).
/// </summary>
public sealed partial class ObjectTable
{
    private readonly Dictionary<string, int> _nameToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (uint start, uint end)> _idToVrom = [];

    public int Count => _idToVrom.Count;

    [GeneratedRegex(@"/\*\s*0x([0-9A-Fa-f]+)\s*\*/\s*DEFINE_OBJECT\w*\((\w+)")]
    private static partial Regex ObjLineRegex();

    public static ObjectTable Build(RomImage rom)
    {
        var t = new ObjectTable();
        t.LoadNames(rom.Game == RomGame.MM);
        t.Locate(rom);
        return t;
    }

    /// <summary>Name→id only (from the decomp), no ROM needed — for resolving object ids on export.</summary>
    public static ObjectTable BuildNamesOnly(bool mm = false)
    {
        var t = new ObjectTable();
        t.LoadNames(mm);
        return t;
    }

    /// <summary>Object name → VROM range, or null if unresolved.</summary>
    public (uint start, uint end)? Resolve(string objectName)
    {
        if (_nameToId.TryGetValue(objectName, out int id) && _idToVrom.TryGetValue(id, out var v))
            return v;
        return null;
    }

    public int? IdOf(string objectName) => _nameToId.TryGetValue(objectName, out int id) ? id : null;

    /// <summary>Object id → VROM range, or null if unresolved.</summary>
    public (uint start, uint end)? ResolveId(int objectId) =>
        _idToVrom.TryGetValue(objectId, out var v) ? v : null;

    /// <summary>Decompressed bytes of an object file (by name), or null if unresolved.</summary>
    public byte[]? GetObjectBytes(RomImage rom, string objectName)
    {
        var v = Resolve(objectName);
        return v == null ? null : BytesAt(rom, v.Value.start);
    }

    /// <summary>Decompressed bytes of an object file (by id), or null if unresolved.</summary>
    public byte[]? GetObjectBytes(RomImage rom, int objectId)
    {
        var v = ResolveId(objectId);
        return v == null ? null : BytesAt(rom, v.Value.start);
    }

    private static byte[]? BytesAt(RomImage rom, uint vromStart)
    {
        foreach (var f in rom.Files)
            if (f.Exists && f.VromStart == vromStart)
                try { return rom.GetFile(f.Index); } catch { return null; }
        return null;
    }

    // ── Name → id from the decomp object_table.h (OoT or MM) ───────────────
    private void LoadNames(bool mm = false)
    {
        string root = mm ? "mm-main" : "oot-master";
        string path = System.IO.Path.Combine(MegatonHammer.Editor.AppPaths.Sources ?? MegatonHammer.Editor.AppPaths.BaseDir, $@"{root}\include\tables\object_table.h");
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            var m = ObjLineRegex().Match(line);
            if (m.Success)
                _nameToId[m.Groups[2].Value] = Convert.ToInt32(m.Groups[1].Value, 16);
        }
    }

    // ── gObjectTable (id → VROM) located by signature in the ROM ──────────
    // gObjectTable is an array of {u32 vromStart, u32 vromEnd} where each non-null start is a
    // real dmadata file start (object 0 + some ids are null/reserved, objects are NOT vrom-
    // contiguous). We find the run that maximizes the count of valid object entries, then read
    // the whole table from there up to the highest object id known from object_table.h.
    // gObjectTable is an array of {u32 vromStart, u32 vromEnd}, indexed by object id. id 0 is a
    // reserved/unset slot; id 1 onward are real objects scattered (NOT vrom-ordered) through the ROM,
    // interspersed with reserved/zero-size slots. The table sits inside the (decompressed) code file.
    //
    // We locate it by scoring every 4-byte-aligned candidate base: a base qualifies if ids 1..4 are
    // all real dmadata file starts (the keep objects), and is scored by how many of the whole id
    // range resolve to file starts. Crucially we also require the player's object_link_boy id to
    // resolve to a LARGE file — that single check rejects the various decoy runs (the dmadata, file
    // lists, other resource tables) that score well on raw entry count but map the link id to a tiny
    // file or garbage. The real table both scores highest and resolves the link object large.
    private void Locate(RomImage rom)
    {
        var starts = new HashSet<uint>();
        foreach (var f in rom.Files) if (f.Exists) starts.Add(f.VromStart);
        int maxId = _nameToId.Count > 0 ? _nameToId.Values.Max() + 4 : 0x300;
        int linkId = _nameToId.TryGetValue("object_link_boy", out int li) ? li : -1;

        // Pass 1: note which vroms hold a skeletal (character) object, and keep the large files (the
        // gObjectTable lives in the big code file). object_link_boy is always a skeletal object, so
        // requiring its slot to land on one rejects decoy tables whose link slot is a non-skeletal file.
        var skelVroms = new HashSet<uint>();
        var candFiles = new List<byte[]>();
        foreach (var f in rom.Files)
        {
            if (!f.Exists || f.Size < 0x8000) continue;
            byte[] d0; try { d0 = rom.GetFile(f.Index); } catch { continue; }
            try { if (ObjectModelReader.FindSkeleton(d0) >= 0) skelVroms.Add(f.VromStart); } catch { }
            if (f.Size >= 0x40000) candFiles.Add(d0);
        }

        // Pass 2: enumerate contiguous runs of valid entries (each run considered once, from its
        // start) in the large files. A run's first valid entry is object id 1 (gameplay_keep), so its
        // base (id 0) is runStart-8. Prefer runs whose object_link_boy slot lands on a real skeletal
        // object, then the longest run. The link-skel check is what rejects MM's decoy run (a long
        // contiguous block of small files whose link slot is a tiny non-skeletal file).
        bool dbg = Environment.GetEnvironmentVariable("MH_DEBUG_OBJTABLE") == "1";
        var cands = dbg ? new List<(long key, byte[] d, int b, int valid, uint linkV, bool ls)>() : null;
        byte[]? bestFile = null;
        int bestBase = -1; long bestKey = -1;
        foreach (var d in candFiles)
        {
            int n = d.Length, p = 0;
            while (p + 8 <= n)
            {
                if (!IsEntry(d, p, starts)) { p += 8; continue; }
                int q = p, valid = 0, nulls = 0;
                uint prevS = 0xFFFFFFFF, prevE = 0xFFFFFFFF;
                while (q + 8 <= n)
                {
                    uint s = U32(d, q), e = U32(d, q + 4);
                    if (s < e && e - s <= 0x200000 && starts.Contains(s))
                    {
                        if (s == prevS && e == prevE) break;   // dmadata duplicate (vrom==rom) → not the table
                        valid++; nulls = 0; prevS = s; prevE = e;
                    }
                    else if (s == 0 && e == 0) { if (++nulls > 30) break; prevS = prevE = 0xFFFFFFFF; }
                    else break;
                    q += 8;
                }
                int b = p - 8;   // run start is id 1, so id 0 is the entry before it
                if (b >= 0 && valid >= 40)
                {
                    bool linkSkel = false; uint linkV = 0;
                    if (linkId >= 0 && b + (linkId + 1) * 8 <= n)
                    {
                        int lq = b + linkId * 8;
                        uint s = U32(d, lq), e = U32(d, lq + 4);
                        if (s < e && e - s <= 0x200000 && starts.Contains(s)) { linkV = s; linkSkel = skelVroms.Contains(s); }
                    }
                    long key = ((long)(linkSkel ? 1 : 0) << 40) | (uint)valid;
                    cands?.Add((key, d, b, valid, linkV, linkSkel));
                    if (key > bestKey) { bestKey = key; bestBase = b; bestFile = d; }
                }
                p = q;   // continue after this run (q advanced past p)
            }
        }
        if (dbg && cands != null)
            foreach (var c in cands.OrderByDescending(c => c.key).Take(8))
                Console.Error.WriteLine($"[objtable] base@0x{c.b:X} valid={c.valid} id1=0x{U32(c.d, c.b + 8):X8} link=0x{c.linkV:X8} skel={c.ls}");
        if (bestFile == null) return;

        for (int id = 0; id <= maxId; id++)
        {
            int q = bestBase + id * 8;
            if (q + 8 > bestFile.Length) break;
            uint s = U32(bestFile, q), e = U32(bestFile, q + 4);
            if (s < e && e - s <= 0x200000 && starts.Contains(s)) _idToVrom[id] = (s, e);
            // null/invalid slots are reserved ids — skip and keep going (table is sparse).
        }
    }

    private static bool IsEntry(byte[] d, int o, HashSet<uint> starts)
    {
        uint s = U32(d, o), e = U32(d, o + 4);
        return s < e && e - s <= 0x200000 && starts.Contains(s);
    }

    private static uint U32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
}
