namespace MegatonHammer.Rom;

/// <summary>
/// Decoder for Majora's Mask "yar" (Yaz0 ARchive) files. A yar is a TOC of block offsets followed by
/// a series of Yaz0-compressed blocks; decompressing each block and concatenating them yields the
/// flat ".unarchive" blob the game's texture offsets index into. Used to read MM's icon_item_static
/// (item icons), whose layout after unarchiving matches OoT's (icon i at i * 0x1000, RGBA32 32x32).
/// Format per the decomp's tools/decompress_yars.py.
/// </summary>
public static class MmArchive
{
    private static uint U32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

    /// <summary>Block boundaries (relative to the first-block offset) from the TOC.</summary>
    private static (int feo, List<int> bounds)? ReadToc(byte[] arc)
    {
        if (arc.Length < 8) return null;
        int feo = (int)U32(arc, 0);                 // offset of the first block = TOC size
        if (feo <= 4 || feo > arc.Length) return null;
        var bounds = new List<int> { 0 };           // block 0 starts at feo + 0
        for (int o = 4; o < feo - 3; o += 4)
            bounds.Add((int)U32(arc, o));            // cumulative block-end offsets, relative to feo
        return (feo, bounds);
    }

    /// <summary>Total size of the unarchived blob (sum of decompressed Yaz0 block sizes), or 0.</summary>
    public static int UnarchivedSize(byte[] arc)
    {
        try { return Unarchive(arc)?.Length ?? 0; } catch { return 0; }
    }

    /// <summary>Decompresses every block and concatenates them into the flat unarchive blob.</summary>
    public static byte[]? Unarchive(byte[] arc)
    {
        var toc = ReadToc(arc);
        if (toc is not { } t) return null;
        using var ms = new MemoryStream();
        for (int i = 0; i + 1 < t.bounds.Count; i++)
        {
            int start = t.feo + t.bounds[i], end = t.feo + t.bounds[i + 1];
            if (start < 0 || end > arc.Length || end <= start) continue;
            if (!Yaz0.IsYaz0(arc, start)) continue;
            byte[] block = Yaz0.Decompress(arc, start);
            ms.Write(block, 0, block.Length);
        }
        return ms.Length > 0 ? ms.ToArray() : null;
    }
}
