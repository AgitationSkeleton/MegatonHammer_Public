namespace MegatonHammer.Rom;

/// <summary>A decompressed ROM working image plus its rebuilt dmadata file table.</summary>
public sealed class FlatRom
{
    public byte[] Data = [];
    public List<RomFile> Files = [];
    public int DmaOffset;                 // byte offset of the dmadata table in Data
    public int DmaFileIndex;              // index of the file containing the dmadata table
}

/// <summary>
/// Produces a fully-decompressed ROM image (every file stored uncompressed at its VROM
/// address, dmadata rewritten 1:1) and supports appending new files with fresh dmadata
/// entries. Decompressed ROMs run in emulators with the expanded-ROM setting.
/// </summary>
public static class RomBuilder
{
    public static FlatRom Decompress(RomImage rom)
    {
        if (rom.DmaTableOffset < 0) throw new InvalidDataException("dmadata table not found.");

        uint maxV = 0;
        foreach (var f in rom.Files) if (f.Exists && f.VromEnd > maxV) maxV = f.VromEnd;
        int size = (int)Align(maxV, 16);

        var data = new byte[size];
        foreach (var f in rom.Files)
        {
            if (!f.Exists) continue;
            var bytes = rom.GetFile(f.Index);
            int n = Math.Min(bytes.Length, size - (int)f.VromStart);
            if (n > 0) Array.Copy(bytes, 0, data, (int)f.VromStart, n);
        }

        // Identify which file holds the dmadata table.
        int dmaFileIdx = -1;
        foreach (var f in rom.Files)
            if (f.Exists && rom.DmaTableOffset >= f.VromStart && rom.DmaTableOffset < f.VromEnd) { dmaFileIdx = f.Index; break; }

        // Build the flattened file list (uncompressed: romStart = vromStart, romEnd = 0).
        var files = new List<RomFile>(rom.Files.Count);
        foreach (var f in rom.Files)
        {
            files.Add(f.Exists
                ? new RomFile { Index = f.Index, VromStart = f.VromStart, VromEnd = f.VromEnd, RomStart = f.VromStart, RomEnd = 0 }
                : new RomFile { Index = f.Index, VromStart = f.VromStart, VromEnd = f.VromEnd, RomStart = f.RomStart, RomEnd = f.RomEnd });
        }

        var flat = new FlatRom { Data = data, Files = files, DmaOffset = rom.DmaTableOffset, DmaFileIndex = dmaFileIdx };
        WriteDmaTable(flat);
        return flat;
    }

    /// <summary>
    /// Appends a new uncompressed file to the end of the image and registers a dmadata
    /// entry for it. Returns the file's VROM start (== physical offset). Throws if the
    /// dmadata table has no spare entry slot.
    /// </summary>
    public static uint AppendFile(FlatRom flat, byte[] content)
    {
        int spare = SpareDmaSlots(flat);
        if (spare <= 0) throw new InvalidOperationException("No spare dmadata slots to register a new file.");

        uint vrom = Align((uint)flat.Data.Length, 16);
        int newSize = (int)vrom + Align(content.Length, 16);

        var grown = new byte[newSize];
        Array.Copy(flat.Data, grown, flat.Data.Length);
        Array.Copy(content, 0, grown, (int)vrom, content.Length);
        flat.Data = grown;

        flat.Files.Add(new RomFile
        {
            Index = flat.Files.Count,
            VromStart = vrom, VromEnd = vrom + (uint)content.Length,
            RomStart = vrom, RomEnd = 0,
        });
        WriteDmaTable(flat);
        return vrom;
    }

    /// <summary>Spare 16-byte entry slots between the last entry and the end of the dmadata file.</summary>
    public static int SpareDmaSlots(FlatRom flat)
    {
        var dmaFile = flat.Files.FirstOrDefault(f => f.Index == flat.DmaFileIndex);
        if (dmaFile == null) return 0;
        int capacity = (int)((dmaFile.VromEnd - (uint)flat.DmaOffset) / 16);
        return capacity - flat.Files.Count;
    }

    /// <summary>
    /// Writes a file's bytes into free ROM space at <paramref name="cursor"/> WITHOUT registering a
    /// dmadata entry, growing the image if needed, and advances the cursor. Returns the file's VROM
    /// start (== physical offset, uncompressed). Used for debug-ROM injection: gc-eu-mq-dbg's
    /// DmaMgr_Init walks gDmaDataTable in lockstep with a fixed-size sDmaMgrFileNames[] array, so
    /// ADDING table entries overruns that array and crashes the debug build at boot. An uncompressed
    /// ROM, however, permits arbitrary DMA to regions not in the table (z_std_dma.c: the
    /// !sDmaMgrIsRomCompressed branch), so the scene/room files can live in unmapped free space and be
    /// reached purely via the scene-table + room-list VROM pointers — no new dmadata entries.
    /// </summary>
    public static uint WriteFileAt(FlatRom flat, byte[] content, ref uint cursor)
    {
        uint vrom = Align(cursor, 16);
        int end = (int)vrom + content.Length;
        if (end > flat.Data.Length)
        {
            var grown = new byte[Align(end, 16)];
            Array.Copy(flat.Data, grown, flat.Data.Length);
            flat.Data = grown;
        }
        Array.Copy(content, 0, flat.Data, (int)vrom, content.Length);
        cursor = vrom + (uint)content.Length;
        return vrom;
    }

    /// <summary>VROM offset just past the highest file in the table (start of free space).</summary>
    public static uint EndOfFiles(FlatRom flat)
    {
        uint maxV = 0;
        foreach (var f in flat.Files) if (f.VromEnd > maxV && f.VromEnd < 0x4000000u) maxV = f.VromEnd;
        return Align(maxV, 16);
    }

    /// <summary>Writes the dmadata table back into the image at its current offset.</summary>
    public static void WriteDmaTable(FlatRom flat)
    {
        int o = flat.DmaOffset;
        foreach (var f in flat.Files)
        {
            W32(flat.Data, o,      f.VromStart);
            W32(flat.Data, o + 4,  f.VromEnd);
            W32(flat.Data, o + 8,  f.RomStart);
            W32(flat.Data, o + 12, f.RomEnd);
            o += 16;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public static int Align(int v, int a) => (v + a - 1) & ~(a - 1);
    public static uint Align(uint v, uint a) => (v + a - 1) & ~(a - 1);

    private static void W32(byte[] d, int o, uint v)
    {
        d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
    }
}
