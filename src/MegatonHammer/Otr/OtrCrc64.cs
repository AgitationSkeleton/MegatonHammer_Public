namespace MegatonHammer.Otr;

/// <summary>
/// CRC-64 hash used by libultraship to identify resources by their virtual archive path
/// (e.g. a display list references its textures/vertices by hash, not by path string).
/// Must match libultraship StrHash64.cpp exactly: ECMA-182 polynomial 0x42F0E1EBA9EA3693,
/// MSB-first, init 0xFFFFFFFFFFFFFFFF, and — importantly — NO final XOR (the path-hashing
/// <c>CRC64(const char*)</c> returns the raw register, unlike <c>update_crc64</c>).
/// </summary>
public static class OtrCrc64
{
    private const ulong Poly = 0x42F0E1EBA9EA3693UL;
    private const ulong Init = 0xFFFFFFFFFFFFFFFFUL;

    private static readonly ulong[] Table = BuildTable();

    private static ulong[] BuildTable()
    {
        var t = new ulong[256];
        for (int n = 0; n < 256; n++)
        {
            ulong c = (ulong)n << 56;
            for (int k = 0; k < 8; k++)
                c = (c & 0x8000000000000000UL) != 0 ? (c << 1) ^ Poly : c << 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>Hashes an ASCII resource path the same way libultraship's CRC64(const char*) does.</summary>
    public static ulong Hash(string path)
    {
        ulong crc = Init;
        foreach (char ch in path)
            crc = Table[(byte)((crc >> 56) ^ (byte)ch)] ^ (crc << 8);
        return crc;   // no final complement (matches CRC64(), not update_crc64())
    }
}
