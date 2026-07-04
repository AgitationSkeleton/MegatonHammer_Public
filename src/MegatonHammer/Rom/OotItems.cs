namespace MegatonHammer.Rom;

/// <summary>
/// OoT GetItemID (GI_) table — the items a chest/give-item actor can grant — from the decomp
/// (z64item.h). Includes the dungeon-scoped items (small key / boss key / map / compass) and
/// the ice trap. Note: a chest's contents are NOT in the En_Box actor params (those hold only
/// chest type + treasure flag); granting arbitrary contents needs a rando-style override or a
/// custom give-item actor — this table is the picker for that. (D13)
/// </summary>
public static class OotItems
{
    public const int BossKey = 0x3F, Compass = 0x40, DungeonMap = 0x41, SmallKey = 0x42, IceTrap = 0x7C;

    private static readonly Dictionary<int, string> Names = new()
    {
        [0x00] = "NONE", [0x01] = "BOMBS_5", [0x02] = "DEKU_NUTS_5", [0x03] = "BOMBCHUS_10",
        [0x04] = "BOW", [0x05] = "SLINGSHOT", [0x06] = "BOOMERANG", [0x07] = "DEKU_STICKS_1",
        [0x08] = "HOOKSHOT", [0x09] = "LONGSHOT", [0x0A] = "LENS_OF_TRUTH", [0x0B] = "ZELDAS_LETTER",
        [0x0C] = "OCARINA_OF_TIME", [0x0D] = "HAMMER", [0x0E] = "COJIRO", [0x0F] = "BOTTLE_EMPTY",
        [0x10] = "BOTTLE_POTION_RED", [0x11] = "BOTTLE_POTION_GREEN", [0x12] = "BOTTLE_POTION_BLUE",
        [0x13] = "BOTTLE_FAIRY", [0x14] = "BOTTLE_MILK_FULL", [0x15] = "BOTTLE_RUTOS_LETTER",
        [0x16] = "MAGIC_BEAN", [0x17] = "MASK_SKULL", [0x18] = "MASK_SPOOKY", [0x19] = "CHICKEN",
        [0x1A] = "MASK_KEATON", [0x1B] = "MASK_BUNNY_HOOD", [0x1C] = "MASK_TRUTH", [0x1D] = "POCKET_EGG",
        [0x1E] = "POCKET_CUCCO", [0x1F] = "ODD_MUSHROOM", [0x20] = "ODD_POTION", [0x21] = "POACHERS_SAW",
        [0x22] = "BROKEN_GORONS_SWORD", [0x23] = "PRESCRIPTION", [0x24] = "EYEBALL_FROG", [0x25] = "EYE_DROPS",
        [0x26] = "CLAIM_CHECK", [0x27] = "SWORD_KOKIRI", [0x28] = "SWORD_KNIFE", [0x29] = "SHIELD_DEKU",
        [0x2A] = "SHIELD_HYLIAN", [0x2B] = "SHIELD_MIRROR", [0x2C] = "TUNIC_GORON", [0x2D] = "TUNIC_ZORA",
        [0x2E] = "BOOTS_IRON", [0x2F] = "BOOTS_HOVER", [0x30] = "QUIVER_40", [0x31] = "QUIVER_50",
        [0x32] = "BOMB_BAG_20", [0x33] = "BOMB_BAG_30", [0x34] = "BOMB_BAG_40", [0x35] = "SILVER_GAUNTLETS",
        [0x36] = "GOLD_GAUNTLETS", [0x37] = "SCALE_SILVER", [0x38] = "SCALE_GOLDEN", [0x39] = "STONE_OF_AGONY",
        [0x3A] = "GERUDOS_CARD", [0x3B] = "OCARINA_FAIRY", [0x3C] = "DEKU_SEEDS_5", [0x3D] = "HEART_CONTAINER",
        [0x3E] = "HEART_PIECE", [0x3F] = "BOSS_KEY", [0x40] = "COMPASS", [0x41] = "DUNGEON_MAP",
        [0x42] = "SMALL_KEY", [0x43] = "MAGIC_JAR_SMALL", [0x44] = "MAGIC_JAR_LARGE", [0x45] = "WALLET_ADULT",
        [0x46] = "WALLET_GIANT", [0x47] = "WEIRD_EGG", [0x48] = "RECOVERY_HEART", [0x49] = "ARROWS_5",
        [0x4A] = "ARROWS_10", [0x4B] = "ARROWS_30", [0x4C] = "RUPEE_GREEN", [0x4D] = "RUPEE_BLUE",
        [0x4E] = "RUPEE_RED", [0x4F] = "HEART_CONTAINER_2", [0x50] = "MILK", [0x51] = "MASK_GORON",
        [0x52] = "MASK_ZORA", [0x53] = "MASK_GERUDO", [0x54] = "GORONS_BRACELET", [0x55] = "RUPEE_PURPLE",
        [0x56] = "RUPEE_GOLD", [0x57] = "SWORD_BIGGORON", [0x58] = "ARROW_FIRE", [0x59] = "ARROW_ICE",
        [0x5A] = "ARROW_LIGHT", [0x5B] = "SKULL_TOKEN", [0x5C] = "DINS_FIRE", [0x5D] = "FARORES_WIND",
        [0x5E] = "NAYRUS_LOVE", [0x5F] = "BULLET_BAG_30", [0x60] = "BULLET_BAG_40", [0x61] = "DEKU_STICKS_5",
        [0x62] = "DEKU_STICKS_10", [0x63] = "DEKU_NUTS_5_2", [0x64] = "DEKU_NUTS_10", [0x65] = "BOMBS_1",
        [0x66] = "BOMBS_10", [0x67] = "BOMBS_20", [0x68] = "BOMBS_30", [0x69] = "DEKU_SEEDS_30",
        [0x6A] = "BOMBCHUS_5", [0x6B] = "BOMBCHUS_20", [0x6C] = "BOTTLE_FISH", [0x6D] = "BOTTLE_BUGS",
        [0x6E] = "BOTTLE_BLUE_FIRE", [0x6F] = "BOTTLE_POE", [0x70] = "BOTTLE_BIG_POE", [0x71] = "DOOR_KEY",
        [0x72] = "RUPEE_GREEN_LOSE", [0x73] = "RUPEE_BLUE_LOSE", [0x74] = "RUPEE_RED_LOSE",
        [0x75] = "RUPEE_PURPLE_LOSE", [0x76] = "HEART_PIECE_WIN", [0x77] = "DEKU_STICK_UPGRADE_20",
        [0x78] = "DEKU_STICK_UPGRADE_30", [0x79] = "DEKU_NUT_UPGRADE_30", [0x7A] = "DEKU_NUT_UPGRADE_40",
        [0x7B] = "BULLET_BAG_50", [0x7C] = "ICE_TRAP",
    };

    public static int Count => Names.Count;

    /// <summary>All (id, friendly name) pairs, id-ordered — for an item picker dropdown.</summary>
    public static IEnumerable<(int Id, string Name)> All =>
        Names.OrderBy(kv => kv.Key).Select(kv => (kv.Key, Pretty(kv.Value)));

    /// <summary>Friendly name for a GI_ id (e.g. "Small Key", "Boss Key", "Ice Trap").</summary>
    public static string NameOf(int id) => Names.TryGetValue(id, out var n) ? Pretty(n) : $"GI 0x{id:X2}";

    /// <summary>The dungeon-scoped items (bound to the current dungeon when granted).</summary>
    public static bool IsDungeonItem(int id) => id is BossKey or Compass or DungeonMap or SmallKey;

    private static string Pretty(string raw)
    {
        var words = raw.Split('_');
        for (int i = 0; i < words.Length; i++)
            words[i] = words[i].Length == 0 ? "" : char.ToUpperInvariant(words[i][0]) + words[i][1..].ToLowerInvariant();
        return string.Join(' ', words);
    }
}
