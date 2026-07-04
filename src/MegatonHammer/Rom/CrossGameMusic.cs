namespace MegatonHammer.Rom;

/// <summary>
/// Cross-game music injection for the N64 (PJ64) path: plays a sequence extracted from the OTHER game
/// (via <see cref="AudioSeqExtractor"/>) in the target game. To avoid relocating the audioseq blob (which
/// would need the audioseq base constant patched), we OVERWRITE a host sequence slot IN PLACE: pick a
/// target seq whose vanilla size is >= the source's, write the source over it, and shrink its size entry.
/// The host slot's own soundfont is kept, so the track plays through the target's instruments — matching
/// the "restrict to tracks whose instruments exist in both games" approach (no custom soundfont needed).
///
/// Works on the DECOMPRESSED ROM image (files at VROM==offset), the same buffer MmInjectScene / RomBuilder
/// operate on. The engine DMAs each sequence on demand from audioseqBase + seqTable[id].addr, so an in-place
/// overwrite at that location is exactly what plays.
/// </summary>
public static class CrossGameMusic
{
    private const int OotNumSeqs = 0x6E, MmNumSeqs = 0x80;

    /// <summary>Locates the sequence index table's ENTRIES start in a decompressed ROM by signature —
    /// version-robust (retail, gc-eu-mq-dbg, MM). The AudioTable header is {u16 numEntries, ...}=0x10 bytes,
    /// then 16-byte entries; entry 0 = {u32 addr=0, u32 size>0} and following entries stay in bounds (or are
    /// small pointer aliases). Returns the entries offset, or -1.</summary>
    public static int FindSeqTable(byte[] dec, RomGame game, int audioseqLen)
    {
        int numSeqs = game == RomGame.MM ? MmNumSeqs : OotNumSeqs;
        int span = 0x10 + numSeqs * 0x10;
        for (int p = 0; p + span <= dec.Length; p += 4)
        {
            if (U16(dec, p) != numSeqs) continue;          // AudioTable header numEntries
            int e0 = p + 0x10;                             // entries start (past the 16-byte header)
            if (U32(dec, e0) != 0) continue;               // entry 0 addr == 0
            int s0 = (int)U32(dec, e0 + 4);
            if (s0 <= 16 || s0 > audioseqLen) continue;    // entry 0 size sane
            int valid = 1;
            for (int i = 1; i < 24; i++)
            {
                int a = (int)U32(dec, e0 + i * 0x10), s = (int)U32(dec, e0 + i * 0x10 + 4);
                bool ptr   = s == 0 && a > 0 && a < numSeqs;
                bool real  = s > 0 && a >= 0 && (long)a + s <= audioseqLen;
                bool empty = a == 0 && s == 0;
                if (ptr || real || empty) valid++;
                else { valid = -1; break; }
            }
            if (valid >= 12) return e0;                    // enough consecutive valid entries → the table
        }
        return -1;
    }

    /// <summary>Overwrites a host seq slot in <paramref name="dec"/> with <paramref name="sourceSeq"/>,
    /// keeping that slot's font. <paramref name="audioseqRomStart"/>/<paramref name="audioseqLen"/> locate
    /// the (decompressed) audioseq blob. Prefers <paramref name="preferredSlot"/> (same id as the source)
    /// when it fits, else the smallest slot that does. Returns the host seq id to set as the scene music, or
    /// -1 if the source is too big for any slot.</summary>
    public static int InjectInPlace(byte[] dec, RomGame targetGame, int audioseqRomStart, int audioseqLen,
                                    byte[] sourceSeq, int preferredSlot)
    {
        int tableVrom = FindSeqTable(dec, targetGame, audioseqLen);
        if (tableVrom < 0) return -1;                       // seq table not located → don't touch the ROM
        int numSeqs   = targetGame == RomGame.MM ? MmNumSeqs : OotNumSeqs;
        int need = sourceSeq.Length;
        if (need <= 0) return -1;

        bool Fits(int slot)
        {
            if (slot < 0 || slot >= numSeqs) return false;
            var (a, s) = Entry(dec, tableVrom, slot);
            return s >= need && a >= 0 && (long)a + need <= audioseqLen;   // size-0 pointer aliases excluded (s < need)
        }

        int host = Fits(preferredSlot) ? preferredSlot : -1;
        if (host < 0)
        {
            int bestSize = int.MaxValue;   // smallest slot that still fits → clobbers the least-used-looking track
            for (int i = 0; i < numSeqs; i++)
            {
                var (_, s) = Entry(dec, tableVrom, i);
                if (Fits(i) && s < bestSize) { bestSize = s; host = i; }
            }
        }
        if (host < 0) return -1;

        var (hAddr, _) = Entry(dec, tableVrom, host);
        System.Array.Copy(sourceSeq, 0, dec, audioseqRomStart + hAddr, need);
        W32(dec, tableVrom + host * 0x10 + 4, (uint)need);   // seqTable[host].size = source size
        return host;
    }

    private static (int addr, int size) Entry(byte[] d, int tableVrom, int slot)
        => ((int)U32(d, tableVrom + slot * 0x10), (int)U32(d, tableVrom + slot * 0x10 + 4));

    private static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    private static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    private static void W32(byte[] d, int o, uint v) { d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v; }
}
