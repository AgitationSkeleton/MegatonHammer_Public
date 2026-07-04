using System.Drawing;

namespace MegatonHammer.Rom;

/// <summary>
/// Decoder for OoT/MM prerendered room backgrounds (ROOM_SHAPE_TYPE_IMAGE, mesh-header type 1).
/// Scenes like the Castle Town market, Temple of Time exterior and house interiors draw their
/// scenery as a full-screen JFIF (baseline JPEG) image rather than 3D geometry; only the floor and
/// a few props are real display lists. This pulls the JFIF out of the scene/room file and decodes
/// it to a <see cref="Bitmap"/> via the standard JPEG path (the N64 JFIF is a conformant baseline
/// JPEG, just stored without an explicit length — we read to the End-Of-Image marker).
/// </summary>
public sealed class PrerenderBackground
{
    public int Width;
    public int Height;
    public byte[] Jfif = [];     // raw JFIF/JPEG bytes (FFD8..FFD9)

    /// <summary>Parses a room's type-1 mesh header and returns its background(s), or an empty list
    /// when the room isn't prerendered. <paramref name="seg"/> resolves a segmented pointer to file
    /// bytes (segment 2 = scene, 3 = room).</summary>
    public static List<PrerenderBackground> FromRoom(byte[] roomFile, int meshHeaderOff, Func<int, byte[]> seg)
    {
        var result = new List<PrerenderBackground>();
        var d = roomFile;
        if (meshHeaderOff < 0 || meshHeaderOff + 0x1C > d.Length || d[meshHeaderOff] != 1) return result;

        byte amountType = d[meshHeaderOff + 1];
        if (amountType == 1)
        {
            // RoomShapeImageSingle: source@0x08, width@0x14, height@0x16, fmt@0x18, siz@0x19.
            AddBg(result, d, meshHeaderOff + 0x08, meshHeaderOff + 0x14, meshHeaderOff + 0x16, seg);
        }
        else if (amountType == 2)
        {
            // RoomShapeImageMulti: numBackgrounds@0x08, backgrounds ptr@0x0C → array of
            // RoomShapeImageMultiBgEntry (0x1C each): source@0x04, width@0x10, height@0x12.
            int num = d[meshHeaderOff + 0x08];
            int listSeg = SegNum(d, meshHeaderOff + 0x0C, out int listOff);
            var src = seg(listSeg);
            for (int i = 0; i < num; i++)
            {
                int e = listOff + i * 0x1C;
                if (e + 0x1C > src.Length) break;
                AddBg(result, src, e + 0x04, e + 0x10, e + 0x12, seg);
            }
        }
        return result;
    }

    private static void AddBg(List<PrerenderBackground> outList, byte[] hdr, int srcPtrOff, int wOff, int hOff, Func<int, byte[]> seg)
    {
        if (srcPtrOff + 4 > hdr.Length || hOff + 2 > hdr.Length) return;
        int srcSeg = SegNum(hdr, srcPtrOff, out int srcOff);
        int w = U16(hdr, wOff), h = U16(hdr, hOff);
        var file = seg(srcSeg);
        var jfif = ExtractJfif(file, srcOff);
        if (jfif != null) outList.Add(new PrerenderBackground { Width = w, Height = h, Jfif = jfif });
    }

    /// <summary>Copies a JFIF/JPEG blob starting at <paramref name="off"/> up to and including its
    /// End-Of-Image (FF D9) marker. Returns null if the data isn't a JPEG.</summary>
    private static byte[]? ExtractJfif(byte[] file, int off)
    {
        if (off < 0 || off + 4 > file.Length) return null;
        if (file[off] != 0xFF || file[off + 1] != 0xD8) return null;   // SOI
        for (int p = off + 2; p + 1 < file.Length; p++)
        {
            if (file[p] == 0xFF && file[p + 1] == 0xD9)
            {
                int len = p + 2 - off;
                var blob = new byte[len];
                Array.Copy(file, off, blob, 0, len);
                return blob;
            }
        }
        return null;
    }

    /// <summary>Decodes the JFIF to a 32-bpp bitmap, or null if decoding fails.</summary>
    public Bitmap? Decode()
    {
        try
        {
            using var ms = new MemoryStream(Jfif);
            using var raw = new Bitmap(ms);
            return new Bitmap(raw);   // detach from the stream
        }
        catch { return null; }
    }

    private static int SegNum(byte[] d, int o, out int off)
    {
        uint v = U32(d, o);
        off = (int)(v & 0x00FFFFFF);
        return (int)(v >> 24);
    }
    private static uint U32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    private static ushort U16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);
}
