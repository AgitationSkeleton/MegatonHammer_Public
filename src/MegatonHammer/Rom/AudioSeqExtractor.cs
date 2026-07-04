namespace MegatonHammer.Rom;

/// <summary>
/// Extracts a music sequence's raw N64 binary from a Zelda 64 ROM's audioseq, for cross-game music
/// (play OoT's music in MM and vice-versa). Approach validated against the MM (MMR) and OoT (OoTR)
/// randomizers: the audioseq blob is dmadata file index 4 in both games, and a 16-byte-per-entry index
/// table maps a sequence id to its {offset within audioseq, size}. A size-0 entry with a small address
/// is a POINTER that aliases another slot (shared/looping tracks); we follow it.
///
/// Font: the sequence plays through a soundfont. For cross-game we reuse the TARGET game's font of the
/// same id (restricting to tracks whose instruments exist in both games), so only the sequence binary is
/// extracted here; the seq→font id comes from <see cref="SeqFontId"/>.
/// </summary>
public static class AudioSeqExtractor
{
    private const int AudioseqDmaIndex = 4;      // both OoT and MM
    private const int OotSeqTableVrom  = 0xB89AE0;  // retail NTSC 1.0 gSequenceTable
    private const int MmSeqTableVrom   = 0xC77B80;  // retail US audioseq index table

    /// <summary>Extracts sequence <paramref name="seqId"/>'s raw binary from the ROM's audioseq (following
    /// pointer aliases). Returns null if the sequence is empty/unavailable or the tables can't be read.</summary>
    public static byte[]? Extract(RomImage rom, int seqId)
    {
        int tableVrom = rom.Game == RomGame.MM ? MmSeqTableVrom : OotSeqTableVrom;
        byte[] audioseq;
        try { audioseq = rom.GetFile(AudioseqDmaIndex); } catch { return null; }
        if (audioseq.Length == 0) return null;

        var (addr, size) = ReadEntry(rom, tableVrom, seqId);
        for (int guard = 0; size == 0 && addr > 0 && addr < 128 && guard < 8; guard++)   // pointer alias
            (addr, size) = ReadEntry(rom, tableVrom, addr);

        if (size <= 0 || addr < 0 || (long)addr + size > audioseq.Length) return null;
        var data = new byte[size];
        Array.Copy(audioseq, addr, data, 0, size);
        return data;
    }

    private static (int addr, int size) ReadEntry(RomImage rom, int tableVrom, int seqId)
    {
        var (data, off) = ReadVrom(rom, tableVrom + seqId * 0x10, 8);
        if (data == null) return (0, 0);
        return ((int)U32(data, off), (int)U32(data, off + 4));
    }

    // Reads at least <paramref name="len"/> bytes at ROM VROM address <paramref name="vrom"/> from whichever
    // dmadata file contains it (decompressed). Returns the file buffer + the offset of vrom within it.
    private static (byte[]? data, int off) ReadVrom(RomImage rom, int vrom, int len)
    {
        foreach (var f in rom.Files)
        {
            if (!f.Exists || vrom < f.VromStart || vrom + len > f.VromEnd) continue;
            byte[] fileData;
            try { fileData = rom.GetFile(f.Index); } catch { continue; }
            int off = (int)(vrom - f.VromStart);
            if (off >= 0 && off + len <= fileData.Length) return (fileData, off);
        }
        return (null, 0);
    }

    private static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
}
