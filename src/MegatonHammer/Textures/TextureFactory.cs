using System.Drawing;
using System.Drawing.Imaging;

namespace MegatonHammer.Textures;

/// <summary>
/// Generates the built-in procedural sample textures so the library is never empty
/// on first run. Each is a small tileable 64×64 pattern.
/// </summary>
public static class TextureFactory
{
    private const int N = 64;

    public sealed record Sample(string Name, string Category, Func<Bitmap> Make);

    public static IReadOnlyList<Sample> Builtins =>
    [
        new("Stone",     "Terrain", () => Noise(Color.FromArgb(120,120,125), 28)),
        new("Dirt",      "Terrain", () => Noise(Color.FromArgb(110, 82, 52), 26)),
        new("Grass",     "Terrain", () => Noise(Color.FromArgb( 70,135, 58), 34)),
        new("Sand",      "Terrain", () => Noise(Color.FromArgb(196,176,120), 22)),
        new("Cobble",    "Terrain", () => Cells(Color.FromArgb(96,96,104), Color.FromArgb(60,60,66), 16)),
        new("Brick",     "Surface", () => Bricks(Color.FromArgb(150, 64, 50), Color.FromArgb(60,40,36))),
        new("Wood",      "Surface", () => Stripes(Color.FromArgb(120, 84, 44), Color.FromArgb(95, 64, 32), false)),
        new("Plank",     "Surface", () => Stripes(Color.FromArgb(140,100, 56), Color.FromArgb(70, 48, 24), true)),
        new("Metal",     "Surface", () => Stripes(Color.FromArgb(120,124,130), Color.FromArgb(150,154,160), false)),
        new("Water",     "Special", () => Gradient(Color.FromArgb(40,90,170), Color.FromArgb(80,150,210))),
        new("Lava",      "Special", () => Noise(Color.FromArgb(200, 70, 20), 60)),
        new("Ice",       "Special", () => Gradient(Color.FromArgb(170,205,230), Color.FromArgb(210,235,250))),

        // Dev / blockout textures — Valve-style measured grid (checkerboard + fine & bold grid lines), for
        // greyboxing layouts before real textures. Grid squares read scale at a glance like Hammer's dev/ set.
        new("Dev Orange", "Dev", () => DevGrid(Color.FromArgb(206,126,52), Color.FromArgb(188,110,42), Color.FromArgb(40,26,14))),
        new("Dev Gray",   "Dev", () => DevGrid(Color.FromArgb(132,132,138), Color.FromArgb(112,112,118), Color.FromArgb(44,44,50))),
        new("Dev White",  "Dev", () => DevGrid(Color.FromArgb(226,226,230), Color.FromArgb(204,204,210), Color.FromArgb(90,90,96))),
        new("Dev Blue",   "Dev", () => DevGrid(Color.FromArgb( 58,112,190), Color.FromArgb( 46, 96,170), Color.FromArgb(16,28,58))),
        new("Dev Green",  "Dev", () => DevGrid(Color.FromArgb( 72,150, 82), Color.FromArgb( 58,132, 68), Color.FromArgb(18,46,24))),
        new("Dev Red",    "Dev", () => DevGrid(Color.FromArgb(192, 72, 62), Color.FromArgb(172, 56, 48), Color.FromArgb(54,18,14))),

        // Tool textures (see SpecialTextures): visible in the editor, but NoRender variants
        // are skipped on export and the clip variants drive collision flags instead of mesh.
        new(SpecialTextures.NoDraw,          "Tool", () => Label(Color.FromArgb(220,200,40),  Color.Black, "NODRAW")),
        new(SpecialTextures.Clip,            "Tool", () => Label(Color.FromArgb(60,150,220),   Color.White, "CLIP")),
        new(SpecialTextures.BlockProjectile, "Tool", () => Label(Color.FromArgb(200,70,70),    Color.White, "BLOCK\nSHOT")),
        new(SpecialTextures.Water,           "Tool", () => Label(Color.FromArgb(40,110,180),   Color.White, "WATER")),
        // #7: invisible collision-only void floors (Hammer trigger-style) — drop a thin brush over a pit.
        new(SpecialTextures.VoidOut,         "Tool", () => Label(Color.FromArgb(120,30,140),   Color.White, "VOID\nOUT")),
        new(SpecialTextures.VoidRespawn,     "Tool", () => Label(Color.FromArgb(90,40,120),    Color.White, "VOID\nRESPN")),
        // Invisible climbable collision (place behind a ladder/vine model, or on its own) + warp trigger.
        new(SpecialTextures.Ladder,          "Tool", () => Label(Color.FromArgb(150,95,40),    Color.White, "LADDER")),
        new(SpecialTextures.Vines,           "Tool", () => Label(Color.FromArgb(60,130,50),    Color.White, "VINES")),
        new(SpecialTextures.Crawlspace,      "Tool", () => Label(Color.FromArgb(110,80,60),    Color.White, "CRAWL\nSPACE")),
        new(SpecialTextures.Warp,            "Tool", () => Label(Color.FromArgb(180,120,30),   Color.Black, "WARP")),
    ];

    // Solid swatch with centred label text (for tool textures).
    private static Bitmap Label(Color bg, Color fg, string text)
    {
        var bmp = new Bitmap(N, N, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(bg);
        // thin border so the swatch reads as a tool texture
        using (var pen = new Pen(Color.FromArgb(30, 30, 30)))
            g.DrawRectangle(pen, 0, 0, N - 1, N - 1);
        using var font = new Font(FontFamily.GenericSansSerif, 9f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(fg);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, new RectangleF(0, 0, N, N), fmt);
        return bmp;
    }

    // Deterministic PRNG keyed off the base colour so each pattern is stable per call.
    private static Bitmap Noise(Color baseCol, int amp)
    {
        var bmp = new Bitmap(N, N, PixelFormat.Format32bppArgb);
        var rng = new Random(baseCol.ToArgb());
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            int d = rng.Next(-amp, amp + 1);
            bmp.SetPixel(x, y, Shift(baseCol, d));
        }
        return bmp;
    }

    private static Bitmap Stripes(Color a, Color b, bool vertical)
    {
        var bmp = new Bitmap(N, N, PixelFormat.Format32bppArgb);
        var rng = new Random(a.ToArgb() ^ b.ToArgb());
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            int t = (vertical ? x : y) / 8;
            var c = (t % 2 == 0) ? a : b;
            bmp.SetPixel(x, y, Shift(c, rng.Next(-10, 11)));
        }
        return bmp;
    }

    private static Bitmap Bricks(Color brick, Color mortar)
    {
        var bmp = new Bitmap(N, N, PixelFormat.Format32bppArgb);
        var rng = new Random(brick.ToArgb());
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            int row = y / 16;
            int off = (row % 2) * 16;        // stagger alternate rows
            int bx  = (x + off) % 32;
            bool mortarPx = (y % 16) < 3 || bx < 3;
            var c = mortarPx ? mortar : Shift(brick, rng.Next(-14, 15));
            bmp.SetPixel(x, y, c);
        }
        return bmp;
    }

    private static Bitmap Cells(Color cell, Color grout, int size)
    {
        var bmp = new Bitmap(N, N, PixelFormat.Format32bppArgb);
        var rng = new Random(cell.ToArgb() * 31 + size);
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            bool g = (x % size) < 2 || (y % size) < 2;
            bmp.SetPixel(x, y, g ? grout : Shift(cell, rng.Next(-18, 19)));
        }
        return bmp;
    }

    // Valve-style "dev" / measure texture: a checkerboard of two shades of the base colour, overlaid with a
    // fine 8px subdivision grid and a bold 16px major grid. Tileable (grid lines land on the tile edges).
    private static Bitmap DevGrid(Color cellA, Color cellB, Color line)
    {
        const int major = 16, minor = 8;
        var bmp = new Bitmap(N, N, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        using var brA = new SolidBrush(cellA);
        using var brB = new SolidBrush(cellB);
        // checkerboard of major cells
        for (int cy = 0; cy < N / major; cy++)
        for (int cx = 0; cx < N / major; cx++)
            g.FillRectangle(((cx + cy) & 1) == 0 ? brA : brB, cx * major, cy * major, major, major);
        // fine subdivision lines (translucent so they read as guides)
        using (var finePen = new Pen(Color.FromArgb(70, line)))
            for (int i = minor; i < N; i += minor)
            { g.DrawLine(finePen, i, 0, i, N); g.DrawLine(finePen, 0, i, N, i); }
        // bold major grid lines
        using (var majorPen = new Pen(line))
            for (int i = 0; i < N; i += major)
            { g.DrawLine(majorPen, i, 0, i, N - 1); g.DrawLine(majorPen, 0, i, N - 1, i); }
        return bmp;
    }

    private static Bitmap Gradient(Color top, Color bottom)
    {
        var bmp = new Bitmap(N, N, PixelFormat.Format32bppArgb);
        for (int y = 0; y < N; y++)
        {
            float t = y / (float)(N - 1);
            var c = Color.FromArgb(
                (int)(top.R + (bottom.R - top.R) * t),
                (int)(top.G + (bottom.G - top.G) * t),
                (int)(top.B + (bottom.B - top.B) * t));
            for (int x = 0; x < N; x++) bmp.SetPixel(x, y, c);
        }
        return bmp;
    }

    /// <summary>Magenta/black checker used when a texture fails to load.</summary>
    public static Bitmap Missing()
    {
        var bmp = new Bitmap(N, N, PixelFormat.Format32bppArgb);
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            bool c = ((x / 8) + (y / 8)) % 2 == 0;
            bmp.SetPixel(x, y, c ? Color.Magenta : Color.Black);
        }
        return bmp;
    }

    private static Color Shift(Color c, int d) => Color.FromArgb(
        Math.Clamp(c.R + d, 0, 255),
        Math.Clamp(c.G + d, 0, 255),
        Math.Clamp(c.B + d, 0, 255));
}
