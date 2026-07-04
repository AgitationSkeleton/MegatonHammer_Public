using System.Drawing;
using System.IO.Compression;

namespace MegatonHammer.Textures;

/// <summary>Metadata for one texture resource discovered in an O2R archive.</summary>
public readonly record struct O2RTexInfo(string EntryName, N64TexType Type, int Width, int Height);

/// <summary>
/// Reads textures out of a SoH/2Ship O2R archive (a ZIP of resources). Each texture
/// resource is a 64-byte OTR header (magic 'OTEX' at offset 4) followed by
/// {textureType, width, height, dataSize} and the raw N64 texel data.
/// </summary>
public sealed class O2RTextureSource : IDisposable
{
    private const int HeaderSize = 64;          // OTR resource header
    private const int TexInfoSize = 16;         // type, width, height, dataSize
    private const int DataStart = HeaderSize + TexInfoSize;  // 80
    // 'OTEX' as little-endian uint32 → bytes X(0x58) E(0x45) T(0x54) O(0x4F).
    private static readonly byte[] Magic = [0x58, 0x45, 0x54, 0x4F];

    public string Path { get; }

    private ZipArchive? _decodeArchive;   // long-lived, UI-thread decode only
    private bool _disposed;

    // CI (palette) textures store only their indices; the TLUT is a separate resource named
    // "<prefix>TLUT_<offset>" alongside "<prefix>Tex_<offset>". Map both an exact group key
    // (folder + room/scene prefix) and a looser per-folder key to the first TLUT we find, so a
    // CI texture can be coloured at decode time instead of decoding to grayscale noise.
    private readonly Dictionary<string, string> _tlutByGroup  = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tlutByFolder = new(StringComparer.OrdinalIgnoreCase);

    public O2RTextureSource(string path) { Path = path; }

    /// <summary>
    /// Scans every entry's header and returns the texture resources. Safe to run on a
    /// background thread (uses its own short-lived archive handle).
    /// </summary>
    public List<O2RTexInfo> Scan()
    {
        var result = new List<O2RTexInfo>();
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(Path); }
        catch { return result; }

        using (zip)
        {
            var hdr = new byte[DataStart];
            foreach (var e in zip.Entries)
            {
                if (e.Length < DataStart) continue;
                try
                {
                    using var s = e.Open();
                    if (!ReadExactly(s, hdr, DataStart)) continue;
                }
                catch { continue; }

                if (hdr[4] != Magic[0] || hdr[5] != Magic[1] ||
                    hdr[6] != Magic[2] || hdr[7] != Magic[3]) continue;

                int type = BitConverter.ToInt32(hdr, 64);
                int w    = BitConverter.ToInt32(hdr, 68);
                int h    = BitConverter.ToInt32(hdr, 72);
                if (type <= 0 || type > 9 || w <= 0 || h <= 0 || w > 4096 || h > 4096) continue;

                result.Add(new O2RTexInfo(e.FullName, (N64TexType)type, w, h));

                // Index TLUT resources so CI textures in the same group can find their palette.
                if (e.FullName.Contains("TLUT", StringComparison.OrdinalIgnoreCase))
                {
                    var (folder, prefix) = Split(e.FullName);
                    _tlutByGroup.TryAdd(folder + "|" + prefix, e.FullName);
                    _tlutByFolder.TryAdd(folder, e.FullName);
                }
            }
        }
        return result;
    }

    // Splits an entry path into (folder, group-prefix). The prefix is the leaf name up to the
    // "Tex"/"TLUT" marker, so a texture and its TLUT in the same room/scene share a key.
    private static (string folder, string prefix) Split(string entry)
    {
        int slash = entry.LastIndexOf('/');
        string folder = slash >= 0 ? entry[..slash] : "";
        string leaf   = slash >= 0 ? entry[(slash + 1)..] : entry;
        int cut = leaf.IndexOf("TLUT", StringComparison.Ordinal);
        if (cut < 0) cut = leaf.IndexOf("Tex", StringComparison.Ordinal);
        return (folder, cut > 0 ? leaf[..cut] : leaf);
    }

    /// <summary>Decodes one texture on demand (call from the UI/GL thread).</summary>
    public Bitmap Decode(O2RTexInfo info)
    {
        try
        {
            var data = ReadResourceData(info.EntryName);
            if (data == null) return TextureFactory.Missing();

            // CI textures carry only palette indices — pair them with their TLUT resource so they
            // decode in colour instead of grayscale noise.
            byte[]? palette = info.Type is N64TexType.Palette4bpp or N64TexType.Palette8bpp
                ? FindTlut(info.EntryName) : null;

            return N64TextureDecoder.Decode(info.Type, data, info.Width, info.Height, palette);
        }
        catch { return TextureFactory.Missing(); }
    }

    // Returns the TLUT (palette) bytes for a CI texture, by group then by folder, or null.
    private byte[]? FindTlut(string entryName)
    {
        var (folder, prefix) = Split(entryName);
        string? tlut = _tlutByGroup.GetValueOrDefault(folder + "|" + prefix)
                       ?? _tlutByFolder.GetValueOrDefault(folder);
        return tlut == null ? null : ReadResourceData(tlut);
    }

    // Reads a resource's raw texel/palette data (the bytes after the 80-byte OTR header).
    private byte[]? ReadResourceData(string entryName)
    {
        _decodeArchive ??= ZipFile.OpenRead(Path);
        var entry = _decodeArchive.GetEntry(entryName);
        if (entry == null) return null;

        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        if (bytes.Length <= DataStart) return null;

        int dataSize = BitConverter.ToInt32(bytes, 76);
        int avail = Math.Min(dataSize, bytes.Length - DataStart);
        if (avail <= 0) return null;
        var data = new byte[avail];
        Array.Copy(bytes, DataStart, data, 0, avail);
        return data;
    }

    private static bool ReadExactly(Stream s, byte[] buf, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _decodeArchive?.Dispose();
        _decodeArchive = null;
        _disposed = true;
    }
}

/// <summary>Locates an O2R archive for the current game on common paths.</summary>
public static class O2RLocator
{
    // Filenames that hold the base game's extracted assets, by game family.
    private static readonly string[] OoTNames  = ["oot.o2r", "oot.otr"];
    private static readonly string[] MMNames   = ["2ship.o2r", "mm.o2r", "2ship.otr"];

    /// <summary>
    /// Returns the first existing O2R/OTR for the given game, searching the supplied
    /// extra directories first, then known reference locations. Prefers .o2r (ZIP).
    /// </summary>
    public static string? Find(bool isOoT, IEnumerable<string?> extraDirs)
    {
        var names = isOoT ? OoTNames : MMNames;

        var dirs = new List<string>();
        foreach (var d in extraDirs)
            if (!string.IsNullOrWhiteSpace(d)) dirs.Add(d!);

        // Known reference locations in this workspace.
        dirs.Add(@"D:\Copilot_OOT\READ_ONLY_SoH\soh");
        dirs.Add(@"D:\Copilot_OOT\READ_ONLY_SoH\soh_mm");

        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            foreach (var name in names)
            {
                // Prefer .o2r (ZIP) — only those are decodable here.
                if (!name.EndsWith(".o2r", StringComparison.OrdinalIgnoreCase)) continue;
                var p = System.IO.Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }
}
