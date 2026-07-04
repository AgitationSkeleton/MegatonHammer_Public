namespace MegatonHammer.Editor;

public readonly record struct DrawConfigPreset(byte Id, string Name);

/// <summary>
/// Named scene draw-config presets. The draw config selects the engine's per-frame scene routine —
/// texture/material scrolling, the Deku Tree death texture-morph, and the reflective water/floor
/// effect (Zora's Domain, Water Temple, Chamber of the Sages, Great Bay Temple) which re-draws the
/// player flipped through the water plane with a depth-based parallax offset. Picking a preset just
/// sets <see cref="SceneSettings.DrawConfig"/> to the matching index. Values mirror the decomp
/// SceneDrawConfig enums (OoT: 0–52; MM: 0–7).
/// </summary>
public static class DrawConfigPresets
{
    public static IReadOnlyList<DrawConfigPreset> For(bool isOoT) => isOoT ? Oot : Mm;

    public static readonly DrawConfigPreset[] Oot =
    [
        new(0,  "Default"),
        new(1,  "Hyrule Field"),
        new(2,  "Kakariko Village"),
        new(3,  "Zora's River"),
        new(4,  "Kokiri Forest"),
        new(5,  "Lake Hylia"),
        new(6,  "Zora's Domain (reflective water)"),
        new(7,  "Zora's Fountain"),
        new(8,  "Gerudo Valley"),
        new(9,  "Lost Woods"),
        new(10, "Desert Colossus"),
        new(11, "Gerudo's Fortress"),
        new(12, "Haunted Wasteland"),
        new(13, "Hyrule Castle"),
        new(14, "Death Mountain Trail"),
        new(15, "Death Mountain Crater"),
        new(16, "Goron City"),
        new(17, "Lon Lon Ranch"),
        new(18, "Fire Temple"),
        new(19, "Deku Tree"),
        new(20, "Dodongo's Cavern"),
        new(21, "Jabu-Jabu"),
        new(22, "Forest Temple"),
        new(23, "Water Temple (reflective water)"),
        new(24, "Shadow Temple & Well"),
        new(25, "Spirit Temple"),
        new(26, "Inside Ganon's Castle"),
        new(27, "Gerudo Training Ground"),
        new(28, "Deku Tree Boss (texture-morph death)"),
        new(29, "Water Temple Boss"),
        new(30, "Temple of Time"),
        new(31, "Grottos"),
        new(32, "Chamber of the Sages (reflective floor)"),
        new(33, "Great Fairy's Fountain"),
        new(34, "Shooting Gallery"),
        new(35, "Castle Courtyard Guards"),
        new(36, "Outside Ganon's Castle"),
        new(37, "Ice Cavern"),
        new(38, "Ganon's Tower Collapse (exterior)"),
        new(39, "Fairy's Fountain"),
        new(40, "Thieves' Hideout"),
        new(41, "Bombchu Bowling Alley"),
        new(42, "Royal Family's Tomb"),
        new(43, "Lakeside Laboratory"),
        new(44, "Lon Lon Buildings"),
        new(45, "Market Guard House"),
        new(46, "Potion Shop Granny"),
        new(47, "Calm Water"),
        new(48, "Grave Exit (light shining)"),
        new(49, "Besitu (test)"),
        new(50, "Fishing Pond"),
        new(51, "Ganon's Tower Collapse (interior)"),
        new(52, "Inside Ganon's Castle Collapse"),
    ];

    public static readonly DrawConfigPreset[] Mm =
    [
        new(0, "Default"),
        new(1, "Material animation"),
        new(2, "Nothing (static)"),
        new(3, "Unused 3"),
        new(4, "Unused 4"),
        new(5, "Unused 5"),
        new(6, "Great Bay Temple (reflective water)"),
        new(7, "Material animation (manual step)"),
    ];
}
