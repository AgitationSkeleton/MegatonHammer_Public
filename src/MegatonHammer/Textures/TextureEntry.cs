using System.Drawing;
using System.Drawing.Imaging;

namespace MegatonHammer.Textures;

/// <summary>
/// One texture available in the library. The pixel data is loaded lazily — from a
/// file on disk or a procedural generator — the first time <see cref="Image"/> is read.
/// </summary>
public sealed class TextureEntry
{
    public string  Name      { get; }
    public string? FilePath  { get; }   // null for built-in/procedural textures
    public string  TypeLabel { get; }   // N64 format (rgba16, ci8, …) or "Procedural"
    public string  Category  { get; }   // primary folder (for sort); see also Folders

    private List<string>? _folders;
    /// <summary>Browser folders this texture belongs to (a shared texture can be in many).</summary>
    public IReadOnlyList<string> Folders => _folders ?? [Category];
    public void SetFolders(IEnumerable<string> folders)
    {
        _folders = folders.Distinct().ToList();
        if (_folders.Count == 0) _folders.Add(Category);
    }

    /// <summary>Adds one browser folder (used when a duplicate is merged onto this survivor so it still
    /// appears under every scene the dropped copies belonged to).</summary>
    public void AddFolder(string folder)
    {
        _folders ??= [Category];
        if (!_folders.Contains(folder)) _folders.Add(folder);
    }

    /// <summary>Number of faces currently using this texture (drives the "by use" sort).</summary>
    public int UsageCount { get; set; }

    private Bitmap? _image;
    private readonly Func<Bitmap>? _generator;

    /// <summary>Optional per-category tint: given a texture's <see cref="Category"/> (a scene name), returns
    /// the RGB multiplier (0..1) of that scene's baked vertex colouring, or null for no tint. Set by the app
    /// so the browser previews each level's textures with its in-game hue (e.g. Lost Woods' blue-green cast)
    /// instead of flat grayscale. Cached per category by the provider so it's computed once.</summary>
    public static Func<string, (float r, float g, float b)?>? CategoryTint;

    public TextureEntry(string name, string? filePath, string typeLabel, string category,
                        Func<Bitmap>? generator = null)
    {
        Name      = name;
        FilePath  = filePath;
        TypeLabel = typeLabel;
        Category  = category;
        _generator = generator;
    }

    /// <summary>Lazily-decoded 32-bpp ARGB bitmap, cached for reuse by the grid and GL upload.</summary>
    public Bitmap Image => _image ??= ApplyCategoryTint(LoadOrGenerate());

    /// <summary>Drop the cached bitmap so it re-decodes (e.g. when the per-level tint is toggled).</summary>
    public void InvalidateImage() { _image?.Dispose(); _image = null; }

    /// <summary>Decodes the RAW (pre-tint) 32-bpp ARGB pixels for duplicate detection, then frees the
    /// temp bitmap (does NOT populate the <see cref="Image"/> cache). Returns null if undecodable — such
    /// an entry is never treated as a duplicate. Pre-tint so two pixel-identical source textures collapse
    /// regardless of which scene's preview tint they'd carry (mirrors the existing shared-texture model).</summary>
    internal byte[]? DecodeRawBytes(out int w, out int h)
    {
        w = h = 0;
        Bitmap bmp;
        try { bmp = LoadOrGenerate(); }   // already 32-bpp ARGB
        catch { return null; }
        try
        {
            w = bmp.Width; h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int len = w * h * 4;
                var buf = new byte[len];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, len);
                return buf;
            }
            finally { bmp.UnlockBits(data); }
        }
        catch { return null; }
        finally { bmp.Dispose(); }
    }

    // Multiply the decoded texture by its scene's baked-colour tint, so a grayscale wall previews with the
    // level's in-game hue. Applied in place (the bitmap is freshly decoded and owned here).
    private Bitmap ApplyCategoryTint(Bitmap bmp)
    {
        if (CategoryTint?.Invoke(Category) is not { } t) return bmp;
        if (t.r > 0.99f && t.g > 0.99f && t.b > 0.99f) return bmp;   // white = no-op
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int n = bmp.Width * bmp.Height;
            var px = new byte[n * 4];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
            for (int i = 0; i < px.Length; i += 4)   // BGRA in memory
            {
                px[i + 0] = (byte)(px[i + 0] * t.b);
                px[i + 1] = (byte)(px[i + 1] * t.g);
                px[i + 2] = (byte)(px[i + 2] * t.r);
            }
            System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    private Bitmap LoadOrGenerate()
    {
        try
        {
            if (_generator != null) return To32Bpp(_generator());

            if (FilePath != null && File.Exists(FilePath))
            {
                // Copy through a stream so the source file is not left locked.
                using var fs  = File.OpenRead(FilePath);
                using var raw = new Bitmap(fs);
                return To32Bpp(raw, maxDim: 256);
            }
        }
        catch { /* fall through to placeholder */ }

        return TextureFactory.Missing();
    }

    // Returns a 32-bpp ARGB copy, optionally downscaled so the largest side ≤ maxDim.
    private static Bitmap To32Bpp(Bitmap src, int maxDim = 0)
    {
        int w = src.Width, h = src.Height;
        if (maxDim > 0 && (w > maxDim || h > maxDim))
        {
            float k = maxDim / (float)Math.Max(w, h);
            w = Math.Max(1, (int)(w * k));
            h = Math.Max(1, (int)(h * k));
        }

        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.PixelOffsetMode    = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.InterpolationMode  = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.DrawImage(src, 0, 0, w, h);
        return dst;
    }
}
