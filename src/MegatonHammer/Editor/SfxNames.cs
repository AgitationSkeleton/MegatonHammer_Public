namespace MegatonHammer.Editor;

/// <summary>
/// A curated, friendly-named list of common in-game sound effects for the Dialogue Editor's Sound picker
/// (ids from the OoT decomp sfx tables; MM shares most). This is a representative starter set — a "(custom)"
/// entry is synthesized on the fly for any id not listed, and the table is meant to be extended (and, later,
/// to allow user-added custom sounds). -1 = silent.
/// </summary>
public static class SfxNames
{
    public readonly record struct Sfx(int Id, string Name)
    {
        public override string ToString() => Name;
    }

    public static readonly Sfx[] Common =
    {
        new(-1, "(none)"),
        // System / UI
        new(0x4802, "Correct chime"),  new(0x4803, "Rupee get"),      new(0x4806, "Error buzz"),
        new(0x4808, "Confirm"),        new(0x480A, "Cancel"),         new(0x480B, "Health recover"),
        new(0x4807, "Chest appears"),  new(0x481F, "Gauge up"),
        // Text blips
        new(0x4804, "Text blip (woman)"), new(0x4805, "Text blip (man)"),
        // Link voice
        new(0x6805, "Link hurt"),  new(0x6813, "Link groan"), new(0x680E, "Link sneeze"),
        new(0x6811, "Link relax"), new(0x6807, "Link fall"),  new(0x6810, "Link drink"),
        // Animals / world
        new(0x2811, "Cucco cluck"), new(0x2813, "Cucco crow"), new(0x2805, "Horse neigh"),
        new(0x28D8, "Dog bark"),    new(0x28BF, "Deku emerges"),
    };
}
