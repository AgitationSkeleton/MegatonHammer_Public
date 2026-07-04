namespace MegatonHammer.Editor;

/// <summary>
/// GetItem (GI_*) tables: maps a chest/freestanding item's content id → a friendly name, indexed by the
/// id value (so it can drive an <see cref="ActorParamSchema.FieldKind.Enum"/> dropdown directly). Index
/// 0 = "None (empty)". Taken verbatim from the decomp GetItemID enums (z64item.h OoT / MM). Covers the
/// full 7-bit chest content range (0x00–0x7F).
/// </summary>
public static class GetItemTable
{
    /// <summary>OoT GetItemID names (z64item.h). Index = GI value.</summary>
    public static readonly string[] OoT =
    [
        /* 0x00 */ "None (empty)",
        /* 0x01 */ "Bombs (5)", /* 0x02 */ "Deku Nuts (5)", /* 0x03 */ "Bombchus (10)", /* 0x04 */ "Fairy Bow",
        /* 0x05 */ "Fairy Slingshot", /* 0x06 */ "Boomerang", /* 0x07 */ "Deku Stick (1)", /* 0x08 */ "Hookshot",
        /* 0x09 */ "Longshot", /* 0x0A */ "Lens of Truth", /* 0x0B */ "Zelda's Letter", /* 0x0C */ "Ocarina of Time",
        /* 0x0D */ "Megaton Hammer", /* 0x0E */ "Cojiro", /* 0x0F */ "Empty Bottle", /* 0x10 */ "Red Potion",
        /* 0x11 */ "Green Potion", /* 0x12 */ "Blue Potion", /* 0x13 */ "Bottled Fairy", /* 0x14 */ "Bottle of Milk",
        /* 0x15 */ "Ruto's Letter", /* 0x16 */ "Magic Bean", /* 0x17 */ "Skull Mask", /* 0x18 */ "Spooky Mask",
        /* 0x19 */ "Cucco (chicken)", /* 0x1A */ "Keaton Mask", /* 0x1B */ "Bunny Hood", /* 0x1C */ "Mask of Truth",
        /* 0x1D */ "Pocket Egg", /* 0x1E */ "Pocket Cucco", /* 0x1F */ "Odd Mushroom", /* 0x20 */ "Odd Potion",
        /* 0x21 */ "Poacher's Saw", /* 0x22 */ "Broken Goron's Sword", /* 0x23 */ "Prescription", /* 0x24 */ "Eyeball Frog",
        /* 0x25 */ "Eye Drops", /* 0x26 */ "Claim Check", /* 0x27 */ "Kokiri Sword", /* 0x28 */ "Giant's Knife",
        /* 0x29 */ "Deku Shield", /* 0x2A */ "Hylian Shield", /* 0x2B */ "Mirror Shield", /* 0x2C */ "Goron Tunic",
        /* 0x2D */ "Zora Tunic", /* 0x2E */ "Iron Boots", /* 0x2F */ "Hover Boots", /* 0x30 */ "Big Quiver (40)",
        /* 0x31 */ "Biggest Quiver (50)", /* 0x32 */ "Bomb Bag (20)", /* 0x33 */ "Big Bomb Bag (30)", /* 0x34 */ "Biggest Bomb Bag (40)",
        /* 0x35 */ "Silver Gauntlets", /* 0x36 */ "Golden Gauntlets", /* 0x37 */ "Silver Scale", /* 0x38 */ "Golden Scale",
        /* 0x39 */ "Stone of Agony", /* 0x3A */ "Gerudo Card", /* 0x3B */ "Fairy Ocarina", /* 0x3C */ "Deku Seeds (5)",
        /* 0x3D */ "Heart Container", /* 0x3E */ "Piece of Heart", /* 0x3F */ "Boss Key", /* 0x40 */ "Compass",
        /* 0x41 */ "Dungeon Map", /* 0x42 */ "Small Key", /* 0x43 */ "Magic Jar (small)", /* 0x44 */ "Magic Jar (large)",
        /* 0x45 */ "Adult Wallet", /* 0x46 */ "Giant Wallet", /* 0x47 */ "Weird Egg", /* 0x48 */ "Recovery Heart",
        /* 0x49 */ "Arrows (small)", /* 0x4A */ "Arrows (medium)", /* 0x4B */ "Arrows (large)", /* 0x4C */ "Green Rupee (1)",
        /* 0x4D */ "Blue Rupee (5)", /* 0x4E */ "Red Rupee (20)", /* 0x4F */ "Heart Container (2)", /* 0x50 */ "Milk (refill)",
        /* 0x51 */ "Goron Mask", /* 0x52 */ "Zora Mask", /* 0x53 */ "Gerudo Mask", /* 0x54 */ "Goron's Bracelet",
        /* 0x55 */ "Purple Rupee (50)", /* 0x56 */ "Huge Rupee (200)", /* 0x57 */ "Biggoron's Sword", /* 0x58 */ "Fire Arrow",
        /* 0x59 */ "Ice Arrow", /* 0x5A */ "Light Arrow", /* 0x5B */ "Gold Skulltula Token", /* 0x5C */ "Din's Fire",
        /* 0x5D */ "Farore's Wind", /* 0x5E */ "Nayru's Love", /* 0x5F */ "Bullet Bag (30)", /* 0x60 */ "Bullet Bag (40)",
        /* 0x61 */ "Deku Sticks (5)", /* 0x62 */ "Deku Sticks (10)", /* 0x63 */ "Deku Nuts (5)", /* 0x64 */ "Deku Nuts (10)",
        /* 0x65 */ "Bombs (1)", /* 0x66 */ "Bombs (10)", /* 0x67 */ "Bombs (20)", /* 0x68 */ "Bombs (30)",
        /* 0x69 */ "Deku Seeds (30)", /* 0x6A */ "Bombchus (5)", /* 0x6B */ "Bombchus (20)", /* 0x6C */ "Fish (refill)",
        /* 0x6D */ "Bugs (refill)", /* 0x6E */ "Blue Fire (refill)", /* 0x6F */ "Poe (refill)", /* 0x70 */ "Big Poe (refill)",
        /* 0x71 */ "Door Key (minigame)", /* 0x72 */ "Green Rupee (lose)", /* 0x73 */ "Blue Rupee (lose)", /* 0x74 */ "Red Rupee (lose)",
        /* 0x75 */ "Purple Rupee (lose)", /* 0x76 */ "Piece of Heart (win)", /* 0x77 */ "Deku Stick Upgrade (20)", /* 0x78 */ "Deku Stick Upgrade (30)",
        /* 0x79 */ "Deku Nut Upgrade (30)", /* 0x7A */ "Deku Nut Upgrade (40)", /* 0x7B */ "Bullet Bag (50)", /* 0x7C */ "Ice Trap",
        /* 0x7D */ "Text 0 (no item)", /* 0x7E */ "(unused)", /* 0x7F */ "(unused)",
    ];

    /// <summary>MM GetItemID names (2S2H z64item.h). Index = GI value. Covers 0x00–0x7F (chest range).</summary>
    public static readonly string[] MM =
    [
        /* 0x00 */ "None (empty)",
        /* 0x01 */ "Green Rupee (1)", /* 0x02 */ "Blue Rupee (5)", /* 0x03 */ "Rupees (10)", /* 0x04 */ "Red Rupee (20)",
        /* 0x05 */ "Purple Rupee (50)", /* 0x06 */ "Silver Rupee (100)", /* 0x07 */ "Huge Rupee (200)", /* 0x08 */ "Adult Wallet",
        /* 0x09 */ "Giant Wallet", /* 0x0A */ "Recovery Heart", /* 0x0B */ "(item 0x0B)", /* 0x0C */ "Piece of Heart",
        /* 0x0D */ "Heart Container", /* 0x0E */ "Magic Jar (small)", /* 0x0F */ "Magic Jar (large)", /* 0x10 */ "(item 0x10)",
        /* 0x11 */ "Stray Fairy", /* 0x12 */ "(item 0x12)", /* 0x13 */ "(item 0x13)", /* 0x14 */ "Bomb (1)",
        /* 0x15 */ "Bombs (5)", /* 0x16 */ "Bombs (10)", /* 0x17 */ "Bombs (20)", /* 0x18 */ "Bombs (30)",
        /* 0x19 */ "Deku Stick (1)", /* 0x1A */ "Bombchus (10)", /* 0x1B */ "Bomb Bag (20)", /* 0x1C */ "Big Bomb Bag (30)",
        /* 0x1D */ "Biggest Bomb Bag (40)", /* 0x1E */ "Arrows (10)", /* 0x1F */ "Arrows (30)", /* 0x20 */ "Arrows (40)",
        /* 0x21 */ "Arrows (50)", /* 0x22 */ "Quiver (30)", /* 0x23 */ "Big Quiver (40)", /* 0x24 */ "Biggest Quiver (50)",
        /* 0x25 */ "Fire Arrow", /* 0x26 */ "Ice Arrow", /* 0x27 */ "Light Arrow", /* 0x28 */ "Deku Nut (1)",
        /* 0x29 */ "Deku Nuts (5)", /* 0x2A */ "Deku Nuts (10)", /* 0x2B */ "(item 0x2B)", /* 0x2C */ "(item 0x2C)",
        /* 0x2D */ "(item 0x2D)", /* 0x2E */ "Bombchus (20)", /* 0x2F */ "(item 0x2F)", /* 0x30 */ "(item 0x30)",
        /* 0x31 */ "(item 0x31)", /* 0x32 */ "Hero's Shield", /* 0x33 */ "Mirror Shield", /* 0x34 */ "Powder Keg",
        /* 0x35 */ "Magic Beans", /* 0x36 */ "Bombchu (1)", /* 0x37 */ "Kokiri Sword", /* 0x38 */ "Razor Sword",
        /* 0x39 */ "Gilded Sword", /* 0x3A */ "Bombchus (5)", /* 0x3B */ "Great Fairy's Sword", /* 0x3C */ "Small Key",
        /* 0x3D */ "Boss Key", /* 0x3E */ "Dungeon Map", /* 0x3F */ "Compass", /* 0x40 */ "(item 0x40)",
        /* 0x41 */ "Hookshot", /* 0x42 */ "Lens of Truth", /* 0x43 */ "Pictograph Box", /* 0x44 */ "(item 0x44)",
        /* 0x45 */ "(item 0x45)", /* 0x46 */ "(item 0x46)", /* 0x47 */ "(item 0x47)", /* 0x48 */ "(item 0x48)",
        /* 0x49 */ "(item 0x49)", /* 0x4A */ "(item 0x4A)", /* 0x4B */ "(item 0x4B)", /* 0x4C */ "Ocarina of Time",
        /* 0x4D */ "(item 0x4D)", /* 0x4E */ "(item 0x4E)", /* 0x4F */ "(item 0x4F)", /* 0x50 */ "Bombers' Notebook",
        /* 0x51 */ "(item 0x51)", /* 0x52 */ "Gold Skulltula Token", /* 0x53 */ "(item 0x53)", /* 0x54 */ "(item 0x54)",
        /* 0x55 */ "Odolwa's Remains", /* 0x56 */ "Goht's Remains", /* 0x57 */ "Gyorg's Remains", /* 0x58 */ "Twinmold's Remains",
        /* 0x59 */ "Red Potion (bottle)", /* 0x5A */ "Empty Bottle", /* 0x5B */ "Red Potion", /* 0x5C */ "Green Potion",
        /* 0x5D */ "Blue Potion", /* 0x5E */ "Bottled Fairy", /* 0x5F */ "Deku Princess", /* 0x60 */ "Bottle of Milk",
        /* 0x61 */ "Half Milk", /* 0x62 */ "Fish", /* 0x63 */ "Bug", /* 0x64 */ "Blue Fire",
        /* 0x65 */ "Poe", /* 0x66 */ "Big Poe", /* 0x67 */ "Spring Water", /* 0x68 */ "Hot Spring Water",
        /* 0x69 */ "Zora Egg", /* 0x6A */ "Gold Dust", /* 0x6B */ "Mushroom", /* 0x6C */ "(item 0x6C)",
        /* 0x6D */ "(item 0x6D)", /* 0x6E */ "Seahorse", /* 0x6F */ "Chateau Romani", /* 0x70 */ "Hylian Loach",
        /* 0x71 */ "(item 0x71)", /* 0x72 */ "(item 0x72)", /* 0x73 */ "(item 0x73)", /* 0x74 */ "(item 0x74)",
        /* 0x75 */ "(item 0x75)", /* 0x76 */ "Ice Trap", /* 0x77 */ "(item 0x77)", /* 0x78 */ "Deku Mask",
        /* 0x79 */ "Goron Mask", /* 0x7A */ "Zora Mask", /* 0x7B */ "Fierce Deity's Mask", /* 0x7C */ "Captain's Hat",
        /* 0x7D */ "Giant's Mask", /* 0x7E */ "All-Night Mask", /* 0x7F */ "Bunny Hood",
    ];

    public static string[] For(bool isOoT) => isOoT ? OoT : MM;

    // #6: GI value → icon_item_static index (= the GetItem's ITEM id, from the decomp sGetItemTable).
    // 0xFF = no 32x32 inventory icon (stacked consumables / rupees / hearts are ITEM ids >= 0x80). Index 0
    // (GI_NONE) = none. Used to draw the chest-contents hologram. Map/compass/keys ARE here (0x74..0x77).
    private static readonly byte[] OoTIconByGi =
    [
        0xFF, // 0 = none
        0xFF,0xFF,0x09,0x03,0x06,0x0E,0x00,0x0A,0x0B,0x0F,0x23,0x08,0x11,0x2F,0x14,0x15,0x16,0x17,0x18,0x1A,
        0x1B,0x10,0x25,0x26,0x22,0x24,0x27,0x2B,0x2D,0x2E,0x30,0x31,0x32,0x33,0x34,0x35,0x36,0x37,0x3B,0x3D,
        0x3E,0x3F,0x40,0x42,0x43,0x45,0x46,0x4B,0x4C,0x4D,0x4E,0x4F,0x51,0x52,0x53,0x54,0x6F,0x70,0x07,0x58,
        0x72,0x7A,0x74,0x75,0x76,0x77,0x78,0x79,0x56,0x57,0x21,
    ];

    /// <summary>The icon_item_static index for the item a chest with this GI value contains, or -1 when it
    /// has no 32x32 inventory icon (or the GI is out of range / GI_NONE). OoT only.</summary>
    public static int IconForGi(int gi)
    {
        // Dungeon quest items have NO 32×32 icon in icon_item_static (reading indices 0x74-0x77 there
        // decoded garbage); their icons are 24×24 in icon_item_24_static. Route them to those byte offsets
        // via the Quest24 pseudo-index so the hologram shows the real boss key / compass / map / small key.
        switch (gi)
        {
            case 0x3F: return Rom.ItemIconSource.Quest24 | 0x7E00;   // Boss Key
            case 0x40: return Rom.ItemIconSource.Quest24 | 0x8700;   // Compass
            case 0x41: return Rom.ItemIconSource.Quest24 | 0x9000;   // Dungeon Map
            case 0x42: return Rom.ItemIconSource.Quest24 | 0x9900;   // Small Key
        }
        if (gi <= 0 || gi >= OoTIconByGi.Length) return -1;
        byte v = OoTIconByGi[gi];
        return v == 0xFF ? -1 : v;
    }
}
