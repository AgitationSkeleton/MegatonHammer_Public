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
        ["bean"] = 0x10, ["hammer"] = 0x11, ["light_arrow"] = 0x12, ["nayrus_love"] = 0x13, ["bottle"] = 0x14,
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
        ["bottle"] = 0x12, ["powder"] = 0x15,
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
}
