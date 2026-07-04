using System.Drawing;
using System.Drawing.Imaging;
using MegatonHammer.Rom;

namespace MegatonHammer.Textures;

/// <summary>
/// Supplies the real OoT open-water surface texture so WATERBOX brushes can render as proper translucent,
/// water — not the editor's flat "WATER" tool swatch. The texture is <c>gLakeHyliaWaterTex</c> (RGBA16,
/// 32x32, at offset 0x25A0 inside <c>object_spot06_objects</c>), the same texture OoT scrolls over Lake
/// Hylia. Extracted once from the loaded ROM and cached; if no ROM is available (self-tests, O2R-only
/// projects) a procedural blue ripple stands in so water still reads as water.
/// </summary>
public static class WaterTexture
{
    private const int Dim = 32;   // 32x32 RGBA16 for every candidate below

    // Real water-surface textures to extract, per game, in preference order (all RGBA16 32x32):
    //   OoT: gLakeHyliaWaterTex  (object_spot06_objects @ 0x25A0)
    //   MM : gWoodfallSceneryPurifiedWaterTex (object_mtoride @ 0x9120), else the hot-spring water
    //        (object_oyu @ 0x160) — a clean blue MM water surface.
    private static (string obj, int off)[] Candidates(bool mm) => mm
        ? [("object_mtoride", 0x9120), ("object_oyu", 0x160)]
        : [("object_spot06_objects", 0x25A0)];

    private static Bitmap? _cached;
    private static RomImage? _rom;
    private static bool _triedRom;

    /// <summary>Point the provider at the loaded ROM so water uses the real texture. Call once after a ROM
    /// loads; switching ROMs (or passing null) drops the cache so the next resolve re-extracts.</summary>
    public static void SetRom(RomImage? rom)
    {
        if (ReferenceEquals(rom, _rom)) return;
        _rom = rom;
        _triedRom = false;
        _cached?.Dispose();
        _cached = null;
    }

    /// <summary>The 32x32 water-surface texture (real from the ROM when available, else procedural). Cached.</summary>
    public static Bitmap Resolve()
    {
        if (_cached != null) return _cached;

        if (_rom != null && !_triedRom)
        {
            _triedRom = true;
            try
            {
                var table = ObjectTable.Build(_rom);
                foreach (var (obj, off) in Candidates(_rom.Game == RomGame.MM))
                {
                    var bytes = table.GetObjectBytes(_rom, obj);
                    if (bytes == null || off + Dim * Dim * 2 > bytes.Length) continue;
                    var data = new byte[Dim * Dim * 2];
                    Array.Copy(bytes, off, data, 0, data.Length);
                    _cached = N64TextureDecoder.Decode(N64TexType.RGBA16bpp, data, Dim, Dim);
                    return _cached;
                }
            }
            catch { /* fall through to procedural */ }
        }

        return _cached ??= Procedural();
    }

    /// <summary>True when the real ROM texture is in use (vs. the procedural fallback). For diagnostics.</summary>
    public static bool HaveRealTexture => _rom != null;

    // A tileable blue caustic water texture, used when no real ROM water texture is available (e.g. an MM
    // project, where the OoT object_spot06 water doesn't exist, or an O2R-only project with no raw ROM).
    // Layered sines (periods that divide 32) give soft rippling highlights and tile seamlessly.
    private static Bitmap Procedural()
    {
        var bmp = new Bitmap(Dim, Dim, PixelFormat.Format32bppArgb);
        for (int y = 0; y < Dim; y++)
            for (int x = 0; x < Dim; x++)
            {
                double fx = x / (double)Dim * Math.PI * 2.0, fy = y / (double)Dim * Math.PI * 2.0;
                double c = Math.Sin(fx * 2.0 + Math.Cos(fy)) * Math.Sin(fy * 2.0 + Math.Cos(fx));
                double caustic = Math.Pow(Math.Max(0.0, c), 2.0);   // 0..1 bright veins on a blue base
                int r = (int)(24 + caustic * 90);
                int g = (int)(96 + caustic * 110);
                int b = (int)(150 + caustic * 90);
                bmp.SetPixel(x, y, Color.FromArgb(255, Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255)));
            }
        return bmp;
    }
}
