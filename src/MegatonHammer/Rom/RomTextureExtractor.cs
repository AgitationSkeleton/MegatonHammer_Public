using System.Drawing;
using MegatonHammer.Textures;

namespace MegatonHammer.Rom;

/// <summary>A texture located inside a ROM file by display-list scanning.
/// PaletteOffset is the offset of the TLUT for CI textures (-1 if none); PaletteFileIndex is
/// the file the palette lives in (-1 = same file as the texture — the common case).</summary>
public readonly record struct RomTexInfo(int FileIndex, int Offset, N64TexType Type, int Width, int Height,
                                         int PaletteOffset = -1, int PaletteFileIndex = -1,
                                         bool ClampS = false, bool ClampT = false,
                                         bool MirrorS = false, bool MirrorT = false,
                                         // #5: the G_SETPRIMCOLOR RGBA active when this texture was bound
                                         // (0 = none). Grayscale i/ia formats are modulated by it in-game,
                                         // so applying it un-greys MM foliage/Lost-Woods textures.
                                         uint Prim = 0,
                                         // The G_SETENVCOLOR RGBA active when bound (0 = none). Used only as
                                         // a fallback tint for grayscale textures with no prim (opt-in).
                                         uint Env = 0);

/// <summary>
/// Extracts textures from a Zelda 64 ROM by scanning every decompressed file's
/// F3DEX2 display lists for a G_SETTIMG (texture image) command paired with a nearby
/// G_SETTILESIZE (dimensions). Segment-relative addresses are resolved within the
/// owning file, which holds for self-contained scene/object/keep files.
/// </summary>
public sealed class RomTextureSource : IDisposable
{
    // F3DEX2 opcodes
    private const byte G_SETTIMG     = 0xFD;
    private const byte G_SETPRIMCOLOR = 0xFA;
    private const byte G_SETENVCOLOR = 0xFB;
    private const byte G_SETTILE     = 0xF5;
    private const byte G_SETTILESIZE = 0xF2;
    private const byte G_LOADBLOCK   = 0xF3;
    private const byte G_LOADTILE    = 0xF4;
    private const byte G_LOADTLUT    = 0xF0;
    private const byte G_ENDDL       = 0xDF;

    public RomImage Rom { get; }

    public RomTextureSource(RomImage rom) { Rom = rom; }

    // ── Scan (background-thread friendly) ─────────────────────────────────────

    public List<RomTexInfo> Scan()
    {
        var found = new List<RomTexInfo>();
        var seen  = new HashSet<long>();

        foreach (var file in Rom.Files)
        {
            if (!file.Exists) continue;
            byte[] data = Rom.GetFile(file.Index);
            ScanFile(file.Index, data, found, seen);
        }

        // Keep memory bounded: drop the decompressed file cache; lazy decode
        // re-decompresses only the files whose textures are actually shown.
        Rom.ClearCache();
        return found;
    }

    private static void ScanFile(int fileIndex, byte[] d, List<RomTexInfo> outList, HashSet<long> seen)
    {
        int n = d.Length & ~7;
        int lastSetTimg = -1;   // in-file offset of the most recent G_SETTIMG target
        int lastPalette = -1;   // palette offset committed by the most recent G_LOADTLUT
        uint lastPrim = 0;      // #5: RGBA of the most recent G_SETPRIMCOLOR (modulates grayscale texels)
        uint lastEnv = 0;       // RGBA of the most recent G_SETENVCOLOR (fallback grayscale modulator)

        // A G_SETTIMG consumed by a following G_LOADTLUT is a TLUT (palette), NOT a texture. Such a
        // region decoded as an RGBA16 image is just the palette's colours — rainbow-confetti garbage.
        // Collect every palette offset so emitted textures pointing at one can be dropped. A SETTIMG
        // is seen before the LOADTLUT that reveals it as a palette, so candidates are buffered here
        // and filtered against paletteOffsets after the whole file is walked.
        var paletteOffsets = new HashSet<int>();
        var candidates = new List<RomTexInfo>();

        for (int o = 0; o + 8 <= n; o += 8)
        {
            // Track the active prim colour (G_SETPRIMCOLOR 0xFA): its RGBA is in the command's 2nd word.
            if (d[o] == G_SETPRIMCOLOR) { lastPrim = U32(d, o + 4); continue; }
            // Track the active env colour (G_SETENVCOLOR 0xFB), same 2nd-word RGBA layout.
            if (d[o] == G_SETENVCOLOR) { lastEnv = U32(d, o + 4); continue; }
            // Track TLUT loads: G_LOADTLUT commits the previous SETTIMG as the palette.
            if (d[o] == G_LOADTLUT) { if (lastSetTimg >= 0) { lastPalette = lastSetTimg; paletteOffsets.Add(lastSetTimg); } continue; }
            if (d[o] != G_SETTIMG) continue;

            uint addr = U32(d, o + 4);
            int seg = (int)(addr >> 24);
            int off = (int)(addr & 0x00FFFFFF);
            if (seg == 0 || seg > 0x0F) continue;             // not a segmented pointer
            if (off < 0 || off >= d.Length) continue;
            lastSetTimg = off;                                // candidate palette for a following LOADTLUT

            // gsDPLoadTextureBlock forces the SETTIMG siz to 16/32b for the block
            // load; the real fmt/siz come from the render-tile G_SETTILE (tile 0),
            // and the dimensions from the paired G_SETTILESIZE. Scan a short window.
            int fmt = -1, siz = -1, tw = 0, th = 0;
            bool haveTile = false, haveSize = false, haveLoad = false;
            int limit = Math.Min(n, o + 8 + 16 * 8);
            for (int p = o + 8; p + 8 <= limit; p += 8)
            {
                byte op = d[p];
                if (op == G_SETTIMG || op == G_ENDDL) break;   // next texture / end of DL

                if (op == G_LOADBLOCK || op == G_LOADTILE) haveLoad = true;   // real texture load present
                else if (op == G_SETTILE)
                {
                    int tile = (int)((U32(d, p + 4) >> 24) & 7);
                    if (tile == 0)                              // render tile = real format
                    {
                        uint w0 = U32(d, p);
                        fmt = (int)((w0 >> 21) & 7);
                        siz = (int)((w0 >> 19) & 3);
                        haveTile = true;
                    }
                }
                else if (op == G_SETTILESIZE)
                {
                    uint w0 = U32(d, p), w1 = U32(d, p + 4);
                    int uls = (int)((w0 >> 12) & 0xFFF), ult = (int)(w0 & 0xFFF);
                    int lrs = (int)((w1 >> 12) & 0xFFF), lrt = (int)(w1 & 0xFFF);
                    tw = ((lrs - uls) >> 2) + 1;
                    th = ((lrt - ult) >> 2) + 1;
                    haveSize = true;
                }
                if (haveTile && haveSize && haveLoad) break;
            }
            // Require a load command (G_LOADBLOCK/G_LOADTILE): a real texture setup always has one, but
            // random non-DL data that coincidentally has SETTIMG+SETTILE+SETTILESIZE bytes (read as
            // rainbow-noise "textures") almost never does.
            if (!haveTile || !haveSize || !haveLoad) continue;

            var type = MapType(fmt, siz);
            if (type == null) continue;
            if (tw < 4 || th < 4 || tw > 256 || th > 256) continue;
            if (tw * th > 0x10000) continue;                  // ≤ 256×256 texels

            int bytes = TexByteSize(type.Value, tw, th);
            if (bytes <= 0 || off + bytes > d.Length) continue;

            // For CI (palette) textures, use the most recently-loaded TLUT (palettes are
            // often loaded once and reused), falling back to a backward scan. The TLUT lives in THIS
            // file (it's an in-file G_SETTIMG offset), so credit the palette to this file too —
            // otherwise Decode sees PaletteFileIndex < 0 and falls back to grayscale, which left the
            // vast majority of in-file CI textures (e.g. most MM scene textures) needlessly desaturated.
            int pal = -1, palFile = -1;
            if (type is N64TexType.Palette4bpp or N64TexType.Palette8bpp)
            {
                pal = lastPalette >= 0 ? lastPalette : FindPalette(d, o);
                if (pal >= 0)
                {
                    int colors = type == N64TexType.Palette4bpp ? 16 : 256;
                    if (pal + colors * 2 <= d.Length) palFile = fileIndex;   // TLUT fits in this file
                    else pal = -1;                                            // bogus offset — keep grayscale
                }
            }

            // Attach the active prim + env colours only to grayscale formats (the ones modulated in-game).
            bool gray = type.Value is N64TexType.Grayscale4bpp or N64TexType.Grayscale8bpp
                                   or N64TexType.GrayscaleAlpha4bpp or N64TexType.GrayscaleAlpha8bpp
                                   or N64TexType.GrayscaleAlpha16bpp;
            uint prim = gray ? lastPrim : 0;
            uint env  = gray ? lastEnv  : 0;
            candidates.Add(new RomTexInfo(fileIndex, off, type.Value, tw, th, pal, palFile, Prim: prim, Env: env));
        }

        // Emit candidates that aren't actually palettes (TLUTs misread as RGBA16 textures).
        foreach (var c in candidates)
        {
            if (paletteOffsets.Contains(c.Offset)) continue;   // this region is a TLUT, not a texture
            long key = ((long)fileIndex << 40) ^ ((long)c.Offset << 12) ^ ((long)c.Type * 977 + c.Width * 31 + c.Height);
            if (!seen.Add(key)) continue;
            outList.Add(c);
        }
    }

    // ── Lazy decode (UI thread) ───────────────────────────────────────────────

    // ── Diagnostics: tally decode outcomes (magenta-placeholder investigation). ──
    public static int DiagDecodeOk, DiagDecodeOverrun, DiagDecodeException;
    public static void ResetDecodeDiag() { DiagDecodeOk = DiagDecodeOverrun = DiagDecodeException = 0; }

    public Bitmap Decode(RomTexInfo info)
    {
        try
        {
            byte[] file = Rom.GetFile(info.FileIndex);
            int w = info.Width, h = info.Height;
            int bytes = TexByteSize(info.Type, w, h);
            if (info.Offset + bytes > file.Length)
            {
                // The bound dimensions are a SETTILESIZE render span (the texture tiles), so reading
                // span-many texels overruns the file. Rather than a magenta "missing" placeholder,
                // clamp the height to the rows that actually fit and decode those — the texture's real
                // data is at the start; the rest is wrap. Far less alarming than a magenta checker.
                int rowBytes = Math.Max(1, TexByteSize(info.Type, w, 1));
                int avail = file.Length - info.Offset;
                h = avail / rowBytes;
                if (h < 1) { DiagDecodeOverrun++; return TextureFactory.Missing(); }
                DiagDecodeOverrun++;
                bytes = TexByteSize(info.Type, w, h);
            }
            var data = new byte[bytes];
            Array.Copy(file, info.Offset, data, 0, bytes);

            // Read the TLUT for CI textures (RGBA16 palette entries) — possibly from another file.
            byte[]? palette = null;
            if (info.PaletteOffset >= 0)
            {
                int colors = info.Type == N64TexType.Palette4bpp ? 16 : 256;
                int palBytes = colors * 2;
                // Resolve the palette's backing file. PaletteFileIndex < 0 means the TLUT lived in a
                // segment we couldn't resolve (e.g. a scrolling/overlay segment) — in that case the
                // offset is meaningless in the texture's own file, so DON'T read it (that produced
                // garbage colours, e.g. Zora's River walls). Leave the palette null so the decoder
                // falls back to grayscale, which is at least honest about the missing TLUT.
                byte[]? palFile = info.PaletteFileIndex >= 0
                    ? (info.PaletteFileIndex != info.FileIndex ? Rom.GetFile(info.PaletteFileIndex) : file)
                    : null;
                if (palFile != null && info.PaletteOffset + palBytes <= palFile.Length)
                {
                    palette = new byte[palBytes];
                    Array.Copy(palFile, info.PaletteOffset, palette, 0, palBytes);
                }
            }
            var bmp = N64TextureDecoder.Decode(info.Type, data, info.Width, info.Height, palette);
            // #5: grayscale i/ia textures are modulated in-game by the prim colour the DL set before
            // binding them — that's how MM's Lost Woods / foliage textures get their colour. Apply it so
            // the editor doesn't show them as flat grey. Opt-out via Editor settings.
            bool tintOn = Editor.EditorSettings.TintGrayscaleTextures;
            if (tintOn && !IsTintless(info.Prim))
                ApplyPrimTint(bmp, info.Prim);
            // Robustness fallback (opt-in): only when there's NO usable prim tint (prim white/absent) do we
            // consider the env colour, since some vanilla surfaces modulate the texel by env instead. This
            // never alters the prim path above (default behaviour is byte-identical: a white/0 prim was
            // already a no-op). Default-off because env also feeds fog / 2nd-cycle blends and can over-tint.
            else if (tintOn && Editor.EditorSettings.TintGrayscaleWithEnv && !IsTintless(info.Env))
                ApplyPrimTint(bmp, info.Env);
            DiagDecodeOk++;
            return bmp;
        }
        catch { DiagDecodeException++; return TextureFactory.Missing(); }
    }

    // A modulation colour that wouldn't change the texel: absent (0 = no command seen) or pure white
    // (identity multiply). Lets the prim path stay byte-identical while the env fallback only fires when
    // prim genuinely contributes nothing.
    private static bool IsTintless(uint c)
    {
        if (c == 0) return true;
        int r = (int)((c >> 24) & 0xFF), g = (int)((c >> 16) & 0xFF), b = (int)((c >> 8) & 0xFF);
        return r == 255 && g == 255 && b == 255;
    }

    // Multiply a decoded grayscale bitmap by the prim RGB (modulate TEXEL×PRIM, the common combiner for
    // i/ia textures), preserving alpha. White prim (0xFFFFFFFF) leaves it grey — a no-op.
    private static unsafe void ApplyPrimTint(Bitmap bmp, uint prim)
    {
        int pr = (int)((prim >> 24) & 0xFF), pg = (int)((prim >> 16) & 0xFF), pb = (int)((prim >> 8) & 0xFF);
        if (pr == 255 && pg == 255 && pb == 255) return;
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite,
                              System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            byte* p = (byte*)bd.Scan0;
            for (int y = 0; y < bd.Height; y++)
                for (int x = 0; x < bd.Width; x++)
                {
                    byte* px = p + y * bd.Stride + x * 4;   // BGRA
                    px[0] = (byte)(px[0] * pb / 255);
                    px[1] = (byte)(px[1] * pg / 255);
                    px[2] = (byte)(px[2] * pr / 255);
                }
        }
        finally { bmp.UnlockBits(bd); }
    }

    // Walks backward from a CI texture's G_SETTIMG to the G_LOADTLUT that preceded it,
    // then to the palette's own G_SETTIMG, returning the palette's in-file offset.
    private static int FindPalette(byte[] d, int ciSettimg)
    {
        int lo = Math.Max(0, ciSettimg - 48 * 8);
        for (int p = ciSettimg - 8; p >= lo; p -= 8)
        {
            if (d[p] == G_ENDDL) break;          // don't cross a DL boundary
            if (d[p] != G_LOADTLUT) continue;
            for (int q = p - 8; q >= Math.Max(0, p - 8 * 8); q -= 8)
            {
                if (d[q] != G_SETTIMG) continue;
                int off = (int)(U32(d, q + 4) & 0x00FFFFFF);
                return off >= 0 && off < d.Length ? off : -1;
            }
            return -1;
        }
        return -1;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static uint U32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

    private static N64TexType? MapType(int fmt, int siz) => (fmt, siz) switch
    {
        (0, 2) => N64TexType.RGBA16bpp,
        (0, 3) => N64TexType.RGBA32bpp,
        (2, 0) => N64TexType.Palette4bpp,
        (2, 1) => N64TexType.Palette8bpp,
        (3, 0) => N64TexType.GrayscaleAlpha4bpp,
        (3, 1) => N64TexType.GrayscaleAlpha8bpp,
        (3, 2) => N64TexType.GrayscaleAlpha16bpp,
        (4, 0) => N64TexType.Grayscale4bpp,
        (4, 1) => N64TexType.Grayscale8bpp,
        _      => null,
    };

    private static int TexByteSize(N64TexType t, int w, int h) => t switch
    {
        N64TexType.RGBA32bpp           => w * h * 4,
        N64TexType.RGBA16bpp           => w * h * 2,
        N64TexType.GrayscaleAlpha16bpp => w * h * 2,
        N64TexType.Grayscale8bpp       => w * h,
        N64TexType.GrayscaleAlpha8bpp  => w * h,
        N64TexType.Palette8bpp         => w * h,
        N64TexType.Grayscale4bpp       => w * h / 2,
        N64TexType.GrayscaleAlpha4bpp  => w * h / 2,
        N64TexType.Palette4bpp         => w * h / 2,
        _                              => 0,
    };

    public void Dispose() => Rom.ClearCache();
}
