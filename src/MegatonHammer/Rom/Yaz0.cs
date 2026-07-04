namespace MegatonHammer.Rom;

/// <summary>Decompressor for the Nintendo Yaz0 run-length format used by Zelda 64 ROMs.</summary>
public static class Yaz0
{
    public static bool IsYaz0(byte[] data, int offset = 0) =>
        data.Length >= offset + 4 &&
        data[offset] == (byte)'Y' && data[offset + 1] == (byte)'a' &&
        data[offset + 2] == (byte)'z' && data[offset + 3] == (byte)'0';

    /// <summary>
    /// Decompresses a Yaz0 block beginning at <paramref name="srcOff"/>. The 16-byte
    /// header carries the decompressed size; the body is groups of one control byte
    /// followed by literals/back-references.
    /// </summary>
    public static byte[] Decompress(byte[] src, int srcOff = 0)
    {
        if (!IsYaz0(src, srcOff)) throw new InvalidDataException("Not a Yaz0 stream.");

        int decompSize = (src[srcOff + 4] << 24) | (src[srcOff + 5] << 16) |
                         (src[srcOff + 6] << 8)  |  src[srcOff + 7];
        var dst = new byte[decompSize];

        int s = srcOff + 16;   // body follows the 16-byte header
        int d = 0;
        byte group = 0;
        int bitsLeft = 0;

        while (d < decompSize)
        {
            if (bitsLeft == 0)
            {
                group = src[s++];
                bitsLeft = 8;
            }

            if ((group & 0x80) != 0)
            {
                dst[d++] = src[s++];               // literal
            }
            else
            {
                byte b1 = src[s++];
                byte b2 = src[s++];
                int dist = (((b1 & 0x0F) << 8) | b2) + 1;
                int count = (b1 >> 4);
                count = count == 0 ? src[s++] + 0x12 : count + 2;

                int copyFrom = d - dist;
                for (int i = 0; i < count && d < decompSize; i++)
                    dst[d++] = dst[copyFrom++];
            }

            group <<= 1;
            bitsLeft--;
        }
        return dst;
    }
}
