namespace MegatonHammer.Rom;

public enum RomGame { Unknown, OoT, MM }

/// <summary>One file in the ROM's dmadata table (decompressed on demand).</summary>
public sealed class RomFile
{
    public int  Index;
    public uint VromStart, VromEnd, RomStart, RomEnd;
    public bool Compressed => RomEnd != 0 && RomEnd != 0xFFFFFFFF;
    public bool Exists     => RomStart != 0xFFFFFFFF && VromEnd > VromStart;
    public int  Size => (int)(VromEnd - VromStart);
}

/// <summary>
/// Loads a Zelda 64 ROM (.z64/.n64/.v64), normalises byte order, identifies the game,
/// locates the dmadata file table, and decompresses files on demand.
/// </summary>
public sealed class RomImage
{
    public byte[] Data { get; }              // big-endian (z64) image
    public RomGame Game { get; }
    public string InternalName { get; }
    public IReadOnlyList<RomFile> Files => _files;
    public int DmaTableOffset { get; private set; } = -1;

    private readonly List<RomFile> _files = [];
    private readonly Dictionary<int, byte[]> _cache = [];

    public RomImage(string path)
    {
        Data = ToBigEndian(File.ReadAllBytes(path));
        InternalName = System.Text.Encoding.ASCII.GetString(Data, 0x20, 20).TrimEnd('\0', ' ');
        Game = Identify(InternalName);
        // The OoT/MM *debug* ROMs share the internal name "THE LEGEND OF DEBUG", so the name
        // heuristic returns Unknown for them. Fall back to the header cartridge code at 0x3C-0x3D:
        // "ZL" = Ocarina of Time (incl. gc-eu-mq-dbg), "Z2" = Majora's Mask (incl. its debug build).
        if (Game == RomGame.Unknown && Data.Length > 0x3E)
        {
            string cart = System.Text.Encoding.ASCII.GetString(Data, 0x3C, 2);
            if (cart == "ZL") Game = RomGame.OoT;
            else if (cart == "Z2" || cart == "ZS") Game = RomGame.MM;
        }
        ParseDmaData();
    }

    /// <summary>Returns the decompressed bytes for file <paramref name="index"/>.</summary>
    public byte[] GetFile(int index)
    {
        if (_cache.TryGetValue(index, out var cached)) return cached;

        var f = _files[index];
        byte[] result;
        if (!f.Exists)
        {
            result = [];
        }
        else if (f.Compressed)
        {
            try { result = Yaz0.Decompress(Data, (int)f.RomStart); }
            catch { result = []; }
        }
        else
        {
            int len = Math.Min(f.Size, Data.Length - (int)f.RomStart);
            result = new byte[Math.Max(0, len)];
            Array.Copy(Data, (int)f.RomStart, result, 0, result.Length);
        }

        _cache[index] = result;
        return result;
    }

    /// <summary>Drops decompressed-file caches to bound memory after a scan.</summary>
    public void ClearCache() => _cache.Clear();

    // ── Byte order ─────────────────────────────────────────────────────────

    private static byte[] ToBigEndian(byte[] raw)
    {
        if (raw.Length < 4) return raw;
        // z64 big-endian
        if (raw[0] == 0x80 && raw[1] == 0x37) return raw;
        // n64 little-endian (swap every 4 bytes)
        if (raw[0] == 0x40 && raw[1] == 0x12)
        {
            for (int i = 0; i + 3 < raw.Length; i += 4)
                (raw[i], raw[i + 1], raw[i + 2], raw[i + 3]) = (raw[i + 3], raw[i + 2], raw[i + 1], raw[i]);
            return raw;
        }
        // v64 byteswapped halfwords (swap every 2 bytes)
        if (raw[0] == 0x37 && raw[1] == 0x80)
        {
            for (int i = 0; i + 1 < raw.Length; i += 2)
                (raw[i], raw[i + 1]) = (raw[i + 1], raw[i]);
            return raw;
        }
        return raw;
    }

    private static RomGame Identify(string name)
    {
        string n = name.ToUpperInvariant();
        if (n.Contains("MAJORA")) return RomGame.MM;
        if (n.Contains("ZELDA") || n.Contains("OOT") || n.Contains("OCARINA")) return RomGame.OoT;
        return RomGame.Unknown;
    }

    // ── dmadata ────────────────────────────────────────────────────────────

    private uint U32(int o) =>
        (uint)((Data[o] << 24) | (Data[o + 1] << 16) | (Data[o + 2] << 8) | Data[o + 3]);

    private void ParseDmaData()
    {
        int tableOff = FindDmaData();
        DmaTableOffset = tableOff;
        if (tableOff < 0) return;

        for (int o = tableOff; o + 16 <= Data.Length; o += 16)
        {
            uint vs = U32(o), ve = U32(o + 4), rs = U32(o + 8), re = U32(o + 12);
            // Terminator: an all-zero entry after the first file.
            if (_files.Count > 0 && vs == 0 && ve == 0 && rs == 0 && re == 0) break;
            // Sanity bound: stop if the entry is clearly past the table.
            if (ve < vs || vs > Data.Length * 4u) break;
            _files.Add(new RomFile { Index = _files.Count, VromStart = vs, VromEnd = ve, RomStart = rs, RomEnd = re });
            if (_files.Count > 4096) break;
        }
    }

    // Locates dmadata by its signature: entry 0 is the makerom file
    // {vrom 0..firstSize, rom 0..0}, and entry 1 begins where entry 0 ends.
    private int FindDmaData()
    {
        for (int o = 0; o + 32 <= Data.Length; o += 16)
        {
            if (U32(o) != 0) continue;                       // entry0.vromStart == 0
            uint firstSize = U32(o + 4);                     // entry0.vromEnd
            if (firstSize == 0 || firstSize > 0x100000) continue;
            if (U32(o + 8) != 0) continue;                   // entry0.romStart == 0
            if (U32(o + 12) != 0) continue;                  // entry0.romEnd   == 0 (uncompressed)
            // entry1.vromStart must continue from entry0.vromEnd
            if (U32(o + 16) != firstSize) continue;
            if (U32(o + 20) <= firstSize) continue;          // entry1.vromEnd > vromStart
            if (U32(o + 24) != firstSize) continue;          // entry1.romStart == its vromStart (uncompressed boot)
            return o;
        }
        return -1;
    }
}
