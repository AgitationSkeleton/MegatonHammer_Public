namespace MegatonHammer.Rom;

/// <summary>
/// Produces a fully-decompressed copy of a Zelda 64 ROM: every dmadata file is written
/// uncompressed at its VROM address (so ROM==VROM throughout), and the dmadata table is
/// rewritten to mark every entry uncompressed (romStart=vromStart, romEnd=0). This is the
/// standard MM-romhack base used by tools like MMR.
///
/// Why we need it: the MM retail ROM Yaz0-compresses <c>code</c> and all scene/asset files,
/// so <c>gSceneTable</c> (US retail: vrom <c>0xC5A1E0</c>) and the scene/room files are not
/// present as plain bytes in the cartridge image. On the decompressed image they are, so the
/// editor can read the scene table directly and inject an uncompressed scene/room <i>in place</i>
/// at the target slot's VROM — no Yaz0 <i>encoder</i> required. A decompressed ROM boots in
/// emulators exactly like the original (the game's DmaMgr treats romEnd==0 as a direct copy).
/// </summary>
public static class MmRomDecompressor
{
    /// <summary>Returns a decompressed, checksum-fixed big-endian (z64) image of <paramref name="rom"/>.</summary>
    public static byte[] Decompress(RomImage rom)
    {
        if (rom.DmaTableOffset < 0)
            throw new InvalidOperationException("dmadata table not located; cannot decompress");

        // Output spans the full VROM address space (files placed at VromStart). Pad to 0x1000.
        long maxV = 0;
        foreach (var f in rom.Files) if (f.Exists) maxV = Math.Max(maxV, f.VromEnd);
        int size = (int)((maxV + 0xFFF) & ~0xFFFL);
        var outp = new byte[size];

        // Lay each file down uncompressed at its VROM address.
        foreach (var f in rom.Files)
        {
            if (!f.Exists) continue;
            byte[] data = rom.GetFile(f.Index);
            int n = Math.Min(data.Length, size - (int)f.VromStart);
            if (n > 0) Array.Copy(data, 0, outp, (int)f.VromStart, n);
        }

        // Rewrite dmadata in place (it now lives uncompressed at its own VROM in outp): every
        // existing file becomes {vromStart, vromEnd, romStart=vromStart, romEnd=0}; non-existent
        // slots keep their 0xFFFFFFFF markers so the game still skips them.
        int t = rom.DmaTableOffset;
        for (int i = 0; i < rom.Files.Count; i++)
        {
            var f = rom.Files[i];
            int e = t + i * 16;
            WriteU32(outp, e + 0, f.VromStart);
            WriteU32(outp, e + 4, f.VromEnd);
            if (f.Exists) { WriteU32(outp, e + 8, f.VromStart); WriteU32(outp, e + 12, 0); }
            else          { WriteU32(outp, e + 8, f.RomStart);  WriteU32(outp, e + 12, f.RomEnd); }
        }

        OotCrc.Update(outp);   // CIC-6105 (shared by OoT & MM)
        return outp;
    }

    private static void WriteU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }
}
