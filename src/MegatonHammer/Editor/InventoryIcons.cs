using System.Drawing;
using System.Drawing.Drawing2D;

namespace MegatonHammer.Editor;

/// <summary>
/// #10b: maps a playtest-inventory key → the ITEM_* icon index in the game's icon_item_static file, so
/// the InventoryDialog can show the real in-game sprite next to each item. Indices are the decomp ITEM_*
/// enum values (icon_item_static is laid out in that order, 32×32 RGBA32 per slot). MM songs aren't in
/// the ITEM enum (separate system), so they fall back to a drawn generic music note — what the user
/// asked for ("the generic white note ones") and what OoT's note icons resemble.
/// </summary>
public static class InventoryIcons
{
    // key → OoT ITEM_* index.
    private static readonly Dictionary<string, int> Oot = new(StringComparer.Ordinal)
    {
        ["stick"] = 0x00, ["nut"] = 0x01, ["bomb"] = 0x02, ["bow"] = 0x03, ["fire_arrow"] = 0x04,
        ["dins_fire"] = 0x05, ["slingshot"] = 0x06, ["bombchu"] = 0x09, ["hookshot"] = 0x0A,
        ["ice_arrow"] = 0x0C, ["farores_wind"] = 0x0D, ["boomerang"] = 0x0E, ["lens"] = 0x0F,
        ["bean"] = 0x10, ["hammer"] = 0x11, ["light_arrow"] = 0x12, ["nayrus_love"] = 0x13,
        ["bottle1"] = 0x14, ["bottle2"] = 0x14, ["bottle3"] = 0x14, ["bottle4"] = 0x14,   // all four share the empty-bottle icon
        ["tunic_kokiri"] = 0x41, ["tunic_goron"] = 0x42, ["tunic_zora"] = 0x43,
        ["boots_kokiri"] = 0x44, ["boots_iron"] = 0x45, ["boots_hover"] = 0x46,
        // NOTE: OoT songs, medallions, spiritual stones and the Stone of Agony are NOT in icon_item_static
        // (that file holds only the C-button/equipment item icons, indices 0x00..~0x46). The quest icons
        // live in the separate 24x24 quest-status asset (icon_item_24_static) and several are stored as
        // runtime-tinted grayscale. Mapping song_/med_/stone_/agony to file-8 indices 0x5A..0x6F therefore
        // decoded WRONG, washed-out grey sprites — the "songs/medallions/stones categories are bugged" the
        // user saw. They're intentionally omitted: IconFor() falls songs back to a drawn note glyph and
        // leaves medallions/stones/agony text-only (clean) instead of showing a garbage sprite.
    };

    // key → MM ITEM_* index. (MM songs have no ITEM icon → generic note.)
    private static readonly Dictionary<string, int> Mm = new(StringComparer.Ordinal)
    {
        ["ocarina"] = 0x00, ["bow"] = 0x01, ["fire_arrow"] = 0x02, ["ice_arrow"] = 0x03, ["light_arrow"] = 0x04,
        ["bomb"] = 0x06, ["bombchu"] = 0x07, ["nut"] = 0x09, ["bean"] = 0x0A, ["powder_keg"] = 0x0C,
        ["pictograph"] = 0x0D, ["lens"] = 0x0E, ["hookshot"] = 0x0F, ["great_fairy_sword"] = 0x10,
        ["bottle1"] = 0x12, ["bottle2"] = 0x12, ["bottle3"] = 0x12, ["bottle4"] = 0x12, ["bottle5"] = 0x12, ["bottle6"] = 0x12,   // six bottle slots, shared icon
        ["stick"] = 0x08, ["powder"] = 0x15,
        ["mask_deku"] = 0x32, ["mask_goron"] = 0x33, ["mask_zora"] = 0x34, ["mask_fierce"] = 0x35,
        ["mask_truth"] = 0x36, ["mask_kafei"] = 0x37, ["mask_allnight"] = 0x38, ["mask_bunny"] = 0x39,
        ["mask_keaton"] = 0x3A, ["mask_garo"] = 0x3B, ["mask_romani"] = 0x3C, ["mask_circus"] = 0x3D,
        ["mask_postman"] = 0x3E, ["mask_couple"] = 0x3F, ["mask_greatfairy"] = 0x40, ["mask_gibdo"] = 0x41,
        ["mask_dongero"] = 0x42, ["mask_kamaro"] = 0x43, ["mask_captain"] = 0x44, ["mask_stone"] = 0x45,
        ["mask_bremen"] = 0x46, ["mask_blast"] = 0x47, ["mask_scents"] = 0x48, ["mask_giant"] = 0x49,
    };

    /// <summary>ITEM icon index for a key, or -1 if it has none (caller may use the note fallback).</summary>
    public static int IconIndex(string key, bool mm)
        => (mm ? Mm : Oot).GetValueOrDefault(key, -1);

    /// <summary>True for a song key (so a missing icon falls back to a music note, not a blank).</summary>
    public static bool IsSong(string key) => key.StartsWith("song_", StringComparison.Ordinal);

    private static Bitmap? _note;

    /// <summary>A small drawn "generic white note" glyph for songs with no ITEM icon (MM songs, or any
    /// song when the ROM's icons are unavailable). Cached.</summary>
    public static Bitmap NoteGlyph()
    {
        if (_note != null) return _note;
        var bmp = new Bitmap(24, 24);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var pen = new Pen(Color.White, 2f);
            using var brush = new SolidBrush(Color.White);
            // stem
            g.DrawLine(pen, 15f, 4f, 15f, 16f);
            // flag
            g.DrawLine(pen, 15f, 4f, 20f, 7f);
            // note head
            g.FillEllipse(brush, 8f, 13f, 8f, 6f);
        }
        _note = bmp;
        return bmp;
    }

    private static readonly Dictionary<string, Bitmap> _glyphs = new(StringComparer.Ordinal);

    /// <summary>Drawn glyphs for optional SoH-exclusive items with no vanilla icon-atlas index
    /// (Roc's Feather, Fierce Deity's Mask). Cached per key; null for anything else.</summary>
    public static Bitmap? OptionalGlyph(string key)
    {
        if (_glyphs.TryGetValue(key, out var c)) return c;
        // 1. Prefer the item's REAL icon embedded as Assets/item_<key>.png (present in the full/private build,
        //    e.g. the actual Roc's Feather texture).
        Bitmap? bmp = LoadEmbeddedItemIcon(key);
        // 2. The Fierce Deity's Mask icon is a Nintendo MM texture, so it is NOT shipped in the asset-free
        //    public build. Extract it at runtime from the user's own MM ROM instead (MM icon_item_static
        //    index 0x35 — the same icon MM projects show for mask_fierce). No game data is redistributed.
        if (bmp == null && key == "fd_mask") bmp = ExtractFdMaskFromMmRom();
        // 3. Last resort: a drawn placeholder glyph (no ROM configured).
        bmp ??= key switch
        {
            "rocs_feather" => Feather(),
            "fd_mask"      => FierceDeityMask(),
            _              => null,
        };
        if (bmp != null) _glyphs[key] = bmp;
        return bmp;
    }

    // The Fierce Deity's Mask inventory icon, decoded from the user's MM ROM (icon_item_static index 0x35),
    // so the asset-free build can show the real icon without redistributing the Nintendo texture. Null if no
    // MM ROM is configured / the icon can't be located, in which case the caller draws a placeholder.
    private static Bitmap? ExtractFdMaskFromMmRom()
    {
        try
        {
            var mm = EditorSettings.MmRomPath;
            if (string.IsNullOrWhiteSpace(mm) || !System.IO.File.Exists(mm)) return null;
            var src = new Rom.ItemIconSource(new Rom.RomImage(mm));
            return src.Available ? src.Icon(0x35) : null;   // 0x35 = SLOT/ITEM index of the FD mask in MM icon_item_static
        }
        catch { return null; }
    }

    private static Bitmap? LoadEmbeddedItemIcon(string key)
    {
        try
        {
            using var s = typeof(InventoryIcons).Assembly.GetManifestResourceStream($"MegatonHammer.Assets.item_{key}.png");
            return s == null ? null : new Bitmap(s);
        }
        catch { return null; }
    }

    // A light-blue jump feather: a leaf-shaped vane with a central quill.
    private static Bitmap Feather()
    {
        var bmp = new Bitmap(24, 24);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var vane = new SolidBrush(Color.FromArgb(160, 210, 240));
        using var edge = new Pen(Color.FromArgb(90, 150, 200), 1.3f);
        using var path = new GraphicsPath();
        path.AddClosedCurve([new PointF(17, 3), new PointF(21, 10), new PointF(12, 20), new PointF(5, 21), new PointF(9, 11)]);
        g.FillPath(vane, path);
        g.DrawPath(edge, path);
        using var shaft = new Pen(Color.FromArgb(245, 251, 255), 1.5f);
        g.DrawLine(shaft, 17f, 3f, 6f, 21f);
        return bmp;
    }

    // The Fierce Deity mask: a pale face with fierce white eyes and the purple/red cheek face-paint.
    private static Bitmap FierceDeityMask()
    {
        var bmp = new Bitmap(24, 24);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var face = new SolidBrush(Color.FromArgb(236, 236, 226));
        g.FillEllipse(face, 5f, 2.5f, 14f, 18.5f);
        using var edge = new Pen(Color.FromArgb(120, 120, 110), 1.1f);
        g.DrawEllipse(edge, 5f, 2.5f, 14f, 18.5f);
        using var eye = new SolidBrush(Color.FromArgb(70, 80, 105));
        g.FillEllipse(eye, 8f, 9f, 3.2f, 2.4f);
        g.FillEllipse(eye, 13f, 9f, 3.2f, 2.4f);
        using var mark1 = new Pen(Color.FromArgb(130, 70, 160), 1.6f);
        using var mark2 = new Pen(Color.FromArgb(195, 65, 65), 1.6f);
        g.DrawLine(mark1, 7f, 13f, 10f, 16.5f);
        g.DrawLine(mark2, 17f, 13f, 14f, 16.5f);
        return bmp;
    }
}
