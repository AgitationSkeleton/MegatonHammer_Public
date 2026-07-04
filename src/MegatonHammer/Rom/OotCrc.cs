namespace MegatonHammer.Rom;

/// <summary>
/// Recomputes the N64 ROM checksum (the two words at 0x10/0x14) using the CIC-6105
/// algorithm that Ocarina of Time / Majora's Mask use. Standard Parasyte n64crc method.
/// </summary>
public static class OotCrc
{
    private const int Start = 0x1000;
    private const int Length = 0x100000;
    private const int HeaderSize = 0x40;
    private const uint Seed6105 = 0xDF26F436;

    public static void Update(byte[] rom)
    {
        if (rom.Length < Start + Length) return;

        uint t1 = Seed6105, t2 = Seed6105, t3 = Seed6105;
        uint t4 = Seed6105, t5 = Seed6105, t6 = Seed6105;

        for (int i = Start; i < Start + Length; i += 4)
        {
            uint d = U32(rom, i);
            if (unchecked(t6 + d) < t6) t4++;
            t6 += d;
            t3 ^= d;
            uint r = Rol(d, (int)(d & 0x1F));
            t5 += r;
            t2 = t2 > d ? t2 ^ r : t2 ^ (t6 ^ d);
            // CIC-6105 mixes in bytes of the bootcode at 0x40 + 0x0710 + (i & 0xFF).
            t1 += U32(rom, HeaderSize + 0x0710 + (i & 0xFF)) ^ d;
        }

        uint crc0 = t6 ^ t4 ^ t3;
        uint crc1 = t5 ^ t2 ^ t1;

        W32(rom, 0x10, crc0);
        W32(rom, 0x14, crc1);
    }

    private static uint Rol(uint i, int b) => b == 0 ? i : (i << b) | (i >> (32 - b));
    private static uint U32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    private static void W32(byte[] d, int o, uint v)
    {
        d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
    }
}
