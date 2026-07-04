using System.Drawing;
using System.Drawing.Imaging;

namespace MegatonHammer.Rom;

/// <summary>
/// Loads OoT item / inventory icons (32×32 RGBA32) from the ROM's icon_item_static file, for the
/// unmodeled-actor sprite fallback. Icons are laid out in ITEM-id order, each at index * 0x1000.
/// Best-effort: if the file can't be located the source reports unavailable and the caller falls
/// back to a plain marker. Companion to <see cref="MegatonHammer.Editor.ActorSpriteMap"/>.
/// </summary>
public sealed class ItemIconSource
{
    public const int Size = 32;
    private const int IconBytes = Size * Size * 4;   // 0x1000

    // Quest-status icons (boss key / compass / dungeon map / small key …) are NOT in icon_item_static; they
    // live in icon_item_24_static as 24×24 RGBA32 textures at fixed byte offsets. A pseudo-index of
    // (Quest24 | byteOffset) routes Icon() to that file so the chest/pot holograms decode them correctly.
    public const int Quest24 = 0x10000;
    public const int Size24 = 24;
    private const int Icon24Bytes = Size24 * Size24 * 4;   // 0x900

    private readonly byte[]? _data;
    private readonly byte[]? _data24;
    private readonly Dictionary<int, Bitmap> _cache = new();

    public bool Available => _data != null;

    public ItemIconSource(RomImage rom)
    {
        try
        {
            if (rom.Game == RomGame.MM)
            {
                // MM stores item icons in a Yaz0 ARchive (icon_item_static_yar). Find it (its
                // unarchived blob is ~0x96000 — 32x32 RGBA32 icons at i*0x1000, like OoT) and decode.
                for (int i = 10; i < Math.Min(48, rom.Files.Count); i++)
                {
                    if (!rom.Files[i].Exists) continue;
                    byte[] b; try { b = rom.GetFile(i); } catch { continue; }
                    if (b.Length < 16) continue;
                    int feo = (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
                    if (feo <= 4 || feo + 4 > b.Length || !Yaz0.IsYaz0(b, feo)) continue;
                    var un = MmArchive.Unarchive(b);
                    if (un != null && un.Length is >= 0x90000 and <= 0x9C000) { _data = un; break; }
                }
            }
            // icon_item_static is dmadata file 8 in retail NTSC OoT (right after link_animetion at 7).
            // Validate by size so a different layout doesn't decode garbage.
            else if (rom.Files.Count > 8 && rom.Files[8].Exists)
            {
                var bytes = rom.GetFile(8);
                if (bytes.Length >= 0x80000 && bytes.Length % IconBytes < IconBytes && bytes.Length / IconBytes >= 80)
                    _data = bytes;

                // icon_item_24_static is the next file (9). Validate by size: it must hold the quest icons
                // at their offsets (last = small key @0x9900, +0x900 = 0xA200) but isn't huge.
                for (int i = 9; i < Math.Min(16, rom.Files.Count); i++)
                {
                    if (!rom.Files[i].Exists) continue;
                    byte[] b; try { b = rom.GetFile(i); } catch { continue; }
                    if (b.Length is >= 0xA200 and < 0x20000) { _data24 = b; break; }
                }
            }
        }
        catch { }
    }

    /// <summary>Decode a 24×24 RGBA32 quest icon at <paramref name="byteOffset"/> in icon_item_24_static.</summary>
    private Bitmap? Quest24Icon(int byteOffset)
    {
        if (_data24 == null || byteOffset < 0 || byteOffset + Icon24Bytes > _data24.Length) return null;
        var bmp = new Bitmap(Size24, Size24, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, Size24, Size24), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* dst = (byte*)bd.Scan0;
            for (int i = 0; i < Size24 * Size24; i++)
            {
                int s = byteOffset + i * 4;
                dst[i * 4 + 0] = _data24[s + 2];   // B
                dst[i * 4 + 1] = _data24[s + 1];   // G
                dst[i * 4 + 2] = _data24[s + 0];   // R
                dst[i * 4 + 3] = _data24[s + 3];   // A
            }
        }
        bmp.UnlockBits(bd);
        return bmp;
    }

    /// <summary>Decoded icon <paramref name="index"/> (ITEM id), or null if unavailable/out of range.</summary>
    public Bitmap? Icon(int index)
    {
        if (index < 0) return null;
        if (_cache.TryGetValue(index, out var cached)) return cached;

        // Quest icon (24×24, icon_item_24_static) routed by the Quest24 pseudo-index flag.
        if ((index & Quest24) != 0)
        {
            var q = Quest24Icon(index & 0xFFFF);
            if (q != null) _cache[index] = q;
            return q;
        }

        if (_data == null) return null;
        int off = index * IconBytes;
        if (off + IconBytes > _data.Length) return null;

        var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, Size, Size);
        var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* dst = (byte*)bd.Scan0;
            for (int i = 0; i < Size * Size; i++)
            {
                int s = off + i * 4;
                // RGBA32 (N64) → BGRA (GDI+).
                dst[i * 4 + 0] = _data[s + 2];   // B
                dst[i * 4 + 1] = _data[s + 1];   // G
                dst[i * 4 + 2] = _data[s + 0];   // R
                dst[i * 4 + 3] = _data[s + 3];   // A
            }
        }
        bmp.UnlockBits(bd);
        _cache[index] = bmp;
        return bmp;
    }
}
