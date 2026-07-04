using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using MegatonHammer.Otr;
using MegatonHammer.Textures;

namespace MegatonHammer.Rom;

/// <summary>
/// Generates the area "title card" nameplate texture that appears when entering a scene
/// (OoT shows a texture like "Kokiri Forest"; MM uses on-screen text instead — see
/// <see cref="ForMajorasMask"/>). Renders the user's text into the title-card dimensions
/// and N64 IA8 format, ready to inject or write as an OTEX resource. (D15)
/// </summary>
public static class NameTextureGenerator
{
    // OoT title cards are IA8, 144x24 (centered white text on transparent).
    public const int Width = 144;
    public const int Height = 24;

    /// <summary>Renders centered white title-card text to a 32-bpp bitmap (transparent bg).</summary>
    public static Bitmap Render(string text, int w = Width, int h = Height)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        // Fit the text to the card width by shrinking the font if needed.
        for (float size = 16f; size >= 7f; size -= 1f)
        {
            using var font = new Font(FontFamily.GenericSerif, size, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = g.MeasureString(text, font);
            if (measured.Width <= w - 4 || size <= 7f)
            {
                using var brush = new SolidBrush(Color.White);
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, font, brush, new RectangleF(0, 0, w, h), fmt);
                break;
            }
        }
        return bmp;
    }

    /// <summary>Converts a rendered nameplate to N64 IA8 bytes (4-bit intensity + 4-bit alpha).</summary>
    public static byte[] ToIA8(Bitmap bmp)
    {
        var data = new byte[bmp.Width * bmp.Height];
        int i = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                int intensity = (c.R + c.G + c.B) / 3;
                data[i++] = (byte)(((intensity >> 4) << 4) | (c.A >> 4));
            }
        return data;
    }

    /// <summary>Builds a complete OTEX (IA8) resource for the title card, for the OTR pipeline.</summary>
    public static byte[] BuildOtex(string text)
    {
        using var bmp = Render(text);
        byte[] ia8 = ToIA8(bmp);
        var w = new OtrResourceWriter(OtrResType.Texture);
        w.U32((uint)N64TexType.GrayscaleAlpha8bpp);   // SoH TextureType 8 = IA8
        w.U32((uint)bmp.Width);
        w.U32((uint)bmp.Height);
        w.U32((uint)ia8.Length);
        w.Bytes(ia8);
        return w.ToArray();
    }

    /// <summary>
    /// MM has no title-card texture — it prints the area name as on-screen text from a message
    /// entry. So for MM the "name" is stored as a string/message id rather than a texture; the
    /// caller writes this into the scene's message slot instead of generating an image.
    /// </summary>
    public static string ForMajorasMask(string text) => text;
}
