using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MegatonHammer.Textures;

/// <summary>
/// SoH/libultraship texture resource format (the <c>textureType</c> field stored in
/// O2R/OTR texture resources, offset 64).
/// </summary>
public enum N64TexType
{
    Error              = 0,
    RGBA32bpp          = 1,
    RGBA16bpp          = 2,
    Palette4bpp        = 3,  // CI4 — palette not bundled; decoded as grayscale
    Palette8bpp        = 4,  // CI8 — palette not bundled; decoded as grayscale
    Grayscale4bpp      = 5,  // I4
    Grayscale8bpp      = 6,  // I8
    GrayscaleAlpha4bpp = 7,  // IA4
    GrayscaleAlpha8bpp = 8,  // IA8
    GrayscaleAlpha16bpp= 9,  // IA16
}

/// <summary>
/// Decodes raw N64 texel data (as stored in SoH texture resources) into 32-bpp ARGB
/// bitmaps. CI (palette) formats are approximated as grayscale since the TLUT is not
/// part of a standalone texture resource.
/// </summary>
public static class N64TextureDecoder
{
    public static string FormatName(N64TexType t) => t switch
    {
        N64TexType.RGBA32bpp           => "rgba32",
        N64TexType.RGBA16bpp           => "rgba16",
        N64TexType.Palette4bpp         => "ci4",
        N64TexType.Palette8bpp         => "ci8",
        N64TexType.Grayscale4bpp       => "i4",
        N64TexType.Grayscale8bpp       => "i8",
        N64TexType.GrayscaleAlpha4bpp  => "ia4",
        N64TexType.GrayscaleAlpha8bpp  => "ia8",
        N64TexType.GrayscaleAlpha16bpp => "ia16",
        _                              => "unknown",
    };

    /// <summary>Decodes <paramref name="data"/> into an ARGB bitmap of size w×h.
    /// For CI formats, <paramref name="palette"/> is the RGBA16 TLUT (applied if non-null).</summary>
    public static Bitmap Decode(N64TexType type, byte[] data, int w, int h, byte[]? palette = null)
    {
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return TextureFactory.Missing();

        var argb = new int[w * h];   // 0xAARRGGBB
        try
        {
            switch (type)
            {
                case N64TexType.RGBA32bpp:           DecodeRGBA32(data, argb); break;
                case N64TexType.RGBA16bpp:           DecodeRGBA16(data, argb); break;
                case N64TexType.Grayscale8bpp:       DecodeI8(data, argb);     break;
                case N64TexType.Grayscale4bpp:       DecodeI4(data, argb);     break;
                case N64TexType.GrayscaleAlpha16bpp: DecodeIA16(data, argb);   break;
                case N64TexType.GrayscaleAlpha8bpp:  DecodeIA8(data, argb);    break;
                case N64TexType.GrayscaleAlpha4bpp:  DecodeIA4(data, argb);    break;
                case N64TexType.Palette8bpp:
                    if (palette != null) DecodeCI8(data, argb, palette); else DecodeCI8Gray(data, argb);
                    break;
                case N64TexType.Palette4bpp:
                    if (palette != null) DecodeCI4(data, argb, palette); else DecodeCI4Gray(data, argb);
                    break;
                default:                             return TextureFactory.Missing();
            }
        }
        catch { return TextureFactory.Missing(); }

        return FromArgb(argb, w, h);
    }

    // RGBA16 (5551) palette colour at index i.
    private static int PalColor(byte[] pal, int i)
    {
        int o = i * 2;
        if (o + 1 >= pal.Length) return Pack(255, 0, 0, 0);
        ushort p = (ushort)((pal[o] << 8) | pal[o + 1]);
        int r = (p >> 11) & 0x1F, g = (p >> 6) & 0x1F, b = (p >> 1) & 0x1F, a = p & 1;
        return Pack((byte)(a == 1 ? 255 : 0), Expand5(r), Expand5(g), Expand5(b));
    }

    private static void DecodeCI8(byte[] d, int[] argb, byte[] pal)
    {
        for (int i = 0; i < argb.Length; i++)
            argb[i] = PalColor(pal, i < d.Length ? d[i] : 0);
    }

    private static void DecodeCI4(byte[] d, int[] argb, byte[] pal)
    {
        for (int i = 0; i < argb.Length; i++)
        {
            int o = i / 2;
            int idx = o >= d.Length ? 0 : ((i & 1) == 0 ? (d[o] >> 4) : (d[o] & 0xF));
            argb[i] = PalColor(pal, idx);
        }
    }

    // ── Format decoders (write 0xAARRGGBB into argb) ──────────────────────────

    private static void DecodeRGBA32(byte[] d, int[] argb)
    {
        for (int i = 0; i < argb.Length; i++)
        {
            int o = i * 4;
            if (o + 3 >= d.Length) break;
            argb[i] = Pack(d[o + 3], d[o], d[o + 1], d[o + 2]);
        }
    }

    private static void DecodeRGBA16(byte[] d, int[] argb)
    {
        for (int i = 0; i < argb.Length; i++)
        {
            int o = i * 2;
            if (o + 1 >= d.Length) break;
            ushort p = (ushort)((d[o] << 8) | d[o + 1]);
            int r = (p >> 11) & 0x1F, g = (p >> 6) & 0x1F, b = (p >> 1) & 0x1F, a = p & 1;
            argb[i] = Pack((byte)(a == 1 ? 255 : 0), Expand5(r), Expand5(g), Expand5(b));
        }
    }

    private static void DecodeI8(byte[] d, int[] argb)
    {
        for (int i = 0; i < argb.Length; i++)
        {
            byte v = i < d.Length ? d[i] : (byte)0;
            argb[i] = Pack(255, v, v, v);
        }
    }

    private static void DecodeI4(byte[] d, int[] argb)
    {
        for (int i = 0; i < argb.Length; i++)
        {
            int o = i / 2;
            if (o >= d.Length) break;
            int n = (i & 1) == 0 ? (d[o] >> 4) : (d[o] & 0xF);
            byte v = (byte)((n << 4) | n);
            argb[i] = Pack(255, v, v, v);
        }
    }

    private static void DecodeIA16(byte[] d, int[] argb)
    {
        for (int i = 0; i < argb.Length; i++)
        {
            int o = i * 2;
            if (o + 1 >= d.Length) break;
            byte v = d[o], a = d[o + 1];
            argb[i] = Pack(a, v, v, v);
        }
    }

    private static void DecodeIA8(byte[] d, int[] argb)
    {
        for (int i = 0; i < argb.Length; i++)
        {
            byte b = i < d.Length ? d[i] : (byte)0;
            int iv = b >> 4, av = b & 0xF;
            byte v = (byte)((iv << 4) | iv), a = (byte)((av << 4) | av);
            argb[i] = Pack(a, v, v, v);
        }
    }

    private static void DecodeIA4(byte[] d, int[] argb)
    {
        for (int i = 0; i < argb.Length; i++)
        {
            int o = i / 2;
            if (o >= d.Length) break;
            int n = (i & 1) == 0 ? (d[o] >> 4) : (d[o] & 0xF);
            int i3 = n >> 1, a1 = n & 1;
            byte v = (byte)((i3 << 5) | (i3 << 2) | (i3 >> 1));
            argb[i] = Pack((byte)(a1 == 1 ? 255 : 0), v, v, v);
        }
    }

    // CI formats: palette (TLUT) isn't part of a standalone resource → grayscale preview.
    private static void DecodeCI8Gray(byte[] d, int[] argb)
    {
        for (int i = 0; i < argb.Length; i++)
        {
            byte v = i < d.Length ? d[i] : (byte)0;
            argb[i] = Pack(255, v, v, v);
        }
    }

    private static void DecodeCI4Gray(byte[] d, int[] argb)
    {
        for (int i = 0; i < argb.Length; i++)
        {
            int o = i / 2;
            if (o >= d.Length) break;
            int n = (i & 1) == 0 ? (d[o] >> 4) : (d[o] & 0xF);
            byte v = (byte)(n * 17);   // 0..15 → 0..255
            argb[i] = Pack(255, v, v, v);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));
    private static int Pack(byte a, byte r, byte g, byte b) => (a << 24) | (r << 16) | (g << 8) | b;

    private static Bitmap FromArgb(int[] argb, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, w, h);
        var bits = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(argb, 0, bits.Scan0, argb.Length);
        bmp.UnlockBits(bits);
        return bmp;
    }
}
