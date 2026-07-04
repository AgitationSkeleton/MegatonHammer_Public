using System.Text.Json;

namespace MegatonHammer.Editor;

/// <summary>A tiered upgrade rendered as a dropdown (sword, shield, wallet, quiver…). The selected
/// value is an index into <see cref="Options"/>; the engine grants everything up to that tier.</summary>
public sealed record InvTier(string Key, string Label, string[] Options, int Default);

/// <summary>A group of on/off inventory entries rendered as a grid of checkboxes in game-menu order
/// (e.g. the 6×4 MM mask pane, OoT's 6×3 C-item grid).</summary>
public sealed record InvGroup(string Name, int Columns, (string Key, string Label)[] Items);

/// <summary>#13: an inventory entry that comes in a varying amount (deku sticks, nuts, bombs, arrows,
/// seeds, bombchu). Rendered as a count spinner (0..max) instead of a checkbox. <see cref="Caps"/> is
/// the engine capacity per upgrade level [0..3]; <see cref="UpgShift"/> is the bit position in the
/// <c>inventory.upgrades</c> word whose level the chosen amount must auto-grant (−1 = fixed cap, no
/// upgrade). The amount is clamped to Caps[last]; the granted level is the smallest whose cap ≥ amount.</summary>
public sealed record AmmoSpec(string Key, int Slot, byte Item, int[] Caps, int UpgShift)
{
    public int Max => Caps[^1];
    /// <summary>Smallest upgrade level (1..3) whose capacity ≥ amount; 0 when amount ≤ 0.</summary>
    public int LevelFor(int amount)
    {
        if (amount <= 0) return 0;
        for (int l = 1; l < Caps.Length; l++) if (Caps[l] >= amount) return l;
        return Caps.Length - 1;
    }
}

/// <summary>
/// The per-game catalogue of what a playtest inventory can contain — tiered upgrades (dropdowns) and
/// on/off toggle groups (checkbox grids), laid out to mirror the in-game inventory subscreens. Keys
/// are canonical strings shared with the O2R payload and the engine boot hook.
/// </summary>
public static class InventoryCatalog
{
    public static InvTier[] Tiers(bool oot) => oot ? OotTiers : MmTiers;
    public static InvGroup[] Groups(bool oot) => oot ? OotGroups : MmGroups;
    public static AmmoSpec[] Ammo(bool oot) => oot ? OotAmmo : MmAmmo;
    public static AmmoSpec? AmmoFor(bool oot, string key)
    { foreach (var a in Ammo(oot)) if (a.Key == key) return a; return null; }

    // ── #13 ammo specs ── caps/shifts derived from oot/mm decomp gUpgradeCapacities + gUpgradeShifts:
    //   QUIVER shift 0, BOMB_BAG 3, BULLET_BAG 14, DEKU_STICKS 17, DEKU_NUTS 20 (each 3-bit). Slot/item
    //   ids mirror N64SavePokes' OotItems/MmItems. Keys overlap the "Items" groups: those entries render
    //   as count spinners instead of checkboxes.
    private static readonly AmmoSpec[] OotAmmo =
    [
        new("stick",     0, 0,  [0, 10, 20, 30], 17),   // Deku Sticks → UPG_DEKU_STICKS
        new("nut",       1, 1,  [0, 20, 30, 40], 20),   // Deku Nuts   → UPG_DEKU_NUTS
        new("bomb",      2, 2,  [0, 20, 30, 40], 3),    // Bombs       → UPG_BOMB_BAG
        new("bow",       3, 3,  [0, 30, 40, 50], 0),    // Arrows      → UPG_QUIVER
        new("slingshot", 6, 6,  [0, 30, 40, 50], 14),   // Seeds       → UPG_BULLET_BAG
        new("bombchu",   8, 9,  [0, 50, 50, 50], -1),   // Bombchu     → fixed 50
    ];
    // MM: same shifts. Deku sticks have NO obtainable upgrade in Majora's Mask → capped at 10.
    private static readonly AmmoSpec[] MmAmmo =
    [
        new("bow",     0x01, 0x01, [0, 30, 40, 50], 0),    // Arrows  → UPG_QUIVER
        new("bomb",    0x06, 0x06, [0, 20, 30, 40], 3),    // Bombs   → UPG_BOMB_BAG
        new("bombchu", 0x07, 0x07, [0, 50, 50, 50], -1),   // Bombchu → fixed 50
        new("stick",   0x08, 0x08, [0, 10, 10, 10], -1),   // Deku Sticks → fixed 10 (no MM upgrade)
        new("nut",     0x09, 0x09, [0, 20, 30, 40], 20),   // Deku Nuts   → UPG_DEKU_NUTS
    ];

    // ── OoT ──
    private static readonly InvTier[] OotTiers =
    [
        new("sword",     "Sword",      ["None", "Kokiri", "Master", "Biggoron"],                 0),
        new("shield",    "Shield",     ["None", "Deku", "Hylian", "Mirror"],                     0),
        new("ocarina",   "Ocarina",    ["None", "Fairy", "Of Time"],                             0),
        new("magic",     "Magic",      ["None", "Single", "Double"],                             0),
        new("wallet",    "Wallet",     ["Child (99)", "Adult (200)", "Giant (500)"],             0),
        new("strength",  "Strength",   ["None", "Goron Bracelet", "Silver Gauntlets", "Gold Gauntlets"], 0),
        new("scale",     "Diving Scale",["None", "Silver", "Golden"],                            0),
        new("quiver",    "Quiver",     ["None", "30", "40", "50"],                               0),
        new("bombbag",   "Bomb Bag",   ["None", "20", "30", "40"],                               0),
        new("bulletbag", "Bullet Bag", ["None", "30", "40", "50"],                               0),
        // #22: the two OoT trade-quest slots (single selection, not cumulative — the dropdown picks which
        // trade item occupies the slot). Child slot 0x17 = items 0x21..0x2B; adult slot 0x16 = 0x2D..0x37.
        new("trade_child", "Child Trade", ["None", "Weird Egg", "Chicken", "Zelda's Letter", "Keaton Mask",
            "Skull Mask", "Spooky Mask", "Bunny Hood", "Goron Mask", "Zora Mask", "Gerudo Mask", "Mask of Truth"], 0),
        new("trade_adult", "Adult Trade", ["None", "Pocket Egg", "Pocket Cucco", "Cojiro", "Odd Mushroom",
            "Odd Potion", "Poacher's Saw", "Broken Goron's Sword", "Prescription", "Eyeball Frog", "Eye Drops", "Claim Check"], 0),
    ];

    private static readonly InvGroup[] OotGroups =
    [
        new("Equipment", 3, [
            ("tunic_kokiri","Kokiri Tunic"), ("tunic_goron","Goron Tunic"), ("tunic_zora","Zora Tunic"),
            ("boots_kokiri","Kokiri Boots"), ("boots_iron","Iron Boots"),  ("boots_hover","Hover Boots"),
        ]),
        // C-item subscreen: 6 columns × 3 rows, in the pause-menu order.
        new("Items", 6, [
            ("stick","Deku Stick"), ("nut","Deku Nut"), ("bomb","Bomb"), ("bow","Fairy Bow"), ("fire_arrow","Fire Arrow"), ("dins_fire","Din's Fire"),
            ("slingshot","Slingshot"), ("bombchu","Bombchu"), ("hookshot","Hookshot"), ("ice_arrow","Ice Arrow"), ("farores_wind","Farore's Wind"), ("boomerang","Boomerang"),
            ("lens","Lens of Truth"), ("bean","Magic Bean"), ("hammer","Megaton Hammer"), ("light_arrow","Light Arrow"), ("nayrus_love","Nayru's Love"), ("bottle","Bottle"),
        ]),
        new("Songs", 6, [
            ("song_lullaby","Zelda's Lullaby"), ("song_epona","Epona's Song"), ("song_saria","Saria's Song"),
            ("song_suns","Sun's Song"), ("song_time","Song of Time"), ("song_storms","Song of Storms"),
            ("song_minuet","Minuet"), ("song_bolero","Bolero"), ("song_serenade","Serenade"),
            ("song_requiem","Requiem"), ("song_nocturne","Nocturne"), ("song_prelude","Prelude"),
        ]),
        new("Medallions & Stones", 5, [
            ("med_forest","Forest"), ("med_fire","Fire"), ("med_water","Water"), ("med_spirit","Spirit"), ("med_shadow","Shadow"),
            ("med_light","Light"), ("stone_emerald","Kokiri Emerald"), ("stone_ruby","Goron Ruby"), ("stone_sapphire","Zora Sapphire"), ("agony","Stone of Agony"),
        ]),
    ];

    // ── MM ──
    private static readonly InvTier[] MmTiers =
    [
        new("sword",  "Sword",  ["None", "Kokiri", "Razor", "Gilded"],                  0),
        new("shield", "Shield", ["None", "Hero's", "Mirror"],                           0),
        new("magic",  "Magic",  ["None", "Single", "Double"],                           0),
        new("wallet", "Wallet", ["Child (99)", "Adult (200)", "Giant (500)", "Royal (999)"], 0),
        new("quiver", "Quiver", ["None", "30", "40", "50"],                             0),
        new("bombbag","Bomb Bag",["None", "20", "30", "40"],                            0),
    ];

    private static readonly InvGroup[] MmGroups =
    [
        // Mask subscreen: exactly 6 columns × 4 rows, in pause-menu order.
        new("Masks", 6, [
            ("mask_postman","Postman's Hat"), ("mask_allnight","All-Night Mask"), ("mask_blast","Blast Mask"), ("mask_stone","Stone Mask"), ("mask_greatfairy","Great Fairy"), ("mask_deku","Deku Mask"),
            ("mask_keaton","Keaton Mask"), ("mask_bremen","Bremen Mask"), ("mask_bunny","Bunny Hood"), ("mask_dongero","Don Gero's"), ("mask_scents","Mask of Scents"), ("mask_goron","Goron Mask"),
            ("mask_romani","Romani's Mask"), ("mask_circus","Circus Leader's"), ("mask_kafei","Kafei's Mask"), ("mask_couple","Couple's Mask"), ("mask_truth","Mask of Truth"), ("mask_zora","Zora Mask"),
            ("mask_kamaro","Kamaro's Mask"), ("mask_gibdo","Gibdo Mask"), ("mask_garo","Garo's Mask"), ("mask_captain","Captain's Hat"), ("mask_giant","Giant's Mask"), ("mask_fierce","Fierce Deity"),
        ]),
        new("Items", 6, [
            ("ocarina","Ocarina of Time"), ("bow","Hero's Bow"), ("fire_arrow","Fire Arrow"), ("ice_arrow","Ice Arrow"), ("light_arrow","Light Arrow"), ("bomb","Bomb"),
            ("bombchu","Bombchu"), ("nut","Deku Nut"), ("bean","Magic Bean"), ("powder_keg","Powder Keg"), ("pictograph","Pictograph Box"), ("lens","Lens of Truth"),
            ("hookshot","Hookshot"), ("great_fairy_sword","Great Fairy Sword"), ("bottle","Bottle"), ("powder","Blue Potion"), ("fishing_rod","—"), ("fishing_rod2","—"),
        ]),
        new("Songs", 6, [
            ("song_time","Song of Time"), ("song_healing","Song of Healing"), ("song_epona","Epona's Song"), ("song_soaring","Song of Soaring"), ("song_storms","Song of Storms"), ("song_awakening","Sonata of Awakening"),
            ("song_lullaby","Goron Lullaby"), ("song_bossa","New Wave Bossa Nova"), ("song_elegy","Elegy of Emptiness"), ("song_oath","Oath to Order"), ("song_saria","Saria's Song"), ("song_suns","Sun's Song"),
        ]),
        new("Remains", 4, [
            ("remains_odolwa","Odolwa"), ("remains_goht","Goht"), ("remains_gyorg","Gyorg"), ("remains_twinmold","Twinmold"),
        ]),
        // Companion: Tatl rides along by default in every preset but "Nothing". The engine boot hook maps
        // this toggle to gSaveContext.save.saveInfo.playerData.hasTatl (the "bell_flag"), not a questItem.
        new("Companion", 1, [
            ("tatl","Tatl (fairy companion)"),
        ]),
    ];
}

/// <summary>
/// A playtest starting-inventory: heart count, tiered upgrade selections, and the set of enabled
/// toggle keys. Serialises to JSON for persistence (EditorSettings) and for the O2R payload the
/// engine boot hook reads. Provides the Default / Nothing / Full presets.
/// </summary>
public sealed class PlaytestInventory
{
    public int Hearts { get; set; } = 3;
    public Dictionary<string, int> Tiers { get; set; } = new();
    public HashSet<string> Toggles { get; set; } = new();
    /// <summary>#13: count for varying-amount items (deku sticks/nuts/bombs/arrows/seeds/bombchu). 0 = none.</summary>
    public Dictionary<string, int> Amounts { get; set; } = new();
    /// <summary>The injected save's player name (#21). Default "Link"; configurable from the inventory menu.</summary>
    public string PlayerName { get; set; } = "Link";
    /// <summary>#5: Z-targeting mode — false = Switch/Toggle (press to lock on, press to release), true = Hold
    /// (hold to stay locked). Maps to gSaveContext.zTargetSetting (0/1) on every engine. Persisted per game.</summary>
    public bool ZTargetHold { get; set; }

    public int Tier(string key) => Tiers.TryGetValue(key, out var v) ? v : 0;
    public void SetTier(string key, int v) => Tiers[key] = v;
    public bool Has(string key) => Toggles.Contains(key);
    public void Set(string key, bool on) { if (on) Toggles.Add(key); else Toggles.Remove(key); }
    public int Amount(string key) => Amounts.TryGetValue(key, out var v) ? v : 0;
    public void SetAmount(string key, int v) { if (v > 0) Amounts[key] = v; else Amounts.Remove(key); }

    public PlaytestInventory Clone() => new()
    { Hearts = Hearts, Tiers = new(Tiers), Toggles = new(Toggles), Amounts = new(Amounts),
      PlayerName = PlayerName, ZTargetHold = ZTargetHold };

    /// <summary>Encodes the player name to OoT's 8-byte name table (A-Z=0x0A.., a-z=0x24.., 0-9=0x00..,
    /// space=0x3E; padded with 0x3E). Verified against z_sram.c ("LINK" = 15 12 17 14 3E 3E 3E 3E).</summary>
    public byte[] EncodeNameOoT()
    {
        var outp = new byte[8];
        string nm = string.IsNullOrWhiteSpace(PlayerName) ? "Link" : PlayerName;
        for (int i = 0; i < 8; i++)
        {
            byte c = 0x3E;   // space / blank
            if (i < nm.Length)
            {
                char ch = nm[i];
                if (ch is >= '0' and <= '9') c = (byte)(ch - '0');
                else if (ch is >= 'A' and <= 'Z') c = (byte)(0x0A + (ch - 'A'));
                else if (ch is >= 'a' and <= 'z') c = (byte)(0x24 + (ch - 'a'));
                // anything else stays a space
            }
            outp[i] = c;
        }
        return outp;
    }

    // ── Presets ──
    public static PlaytestInventory Preset(string name, bool oot) => name switch
    {
        "Nothing" => Nothing(oot),
        "Full inventory" => Full(oot),
        _ => Default(oot),
    };

    public static readonly string[] PresetNames = ["Default", "Nothing", "Full inventory"];

    /// <summary>Game-start-ish loadout requested for each game (the editor's "Default").</summary>
    public static PlaytestInventory Default(bool oot)
    {
        var inv = new PlaytestInventory { Hearts = 3 };
        if (oot)
        {
            // 3 hearts, Kokiri Tunic + Boots, Kokiri & Master Sword, Deku & Hylian Shield,
            // Child Wallet, Goron Bracelet, Fairy Ocarina.
            inv.SetTier("sword", 2);     // Master (grants Kokiri+Master)
            inv.SetTier("shield", 2);    // Hylian (grants Deku+Hylian)
            inv.SetTier("ocarina", 1);   // Fairy Ocarina
            inv.SetTier("magic", 1);     // first magic upgrade (single)
            inv.SetTier("wallet", 0);    // Child Wallet
            inv.SetTier("strength", 1);  // Goron Bracelet
            inv.Set("tunic_kokiri", true);
            inv.Set("boots_kokiri", true);
        }
        else
        {
            // 3 hearts, Child Wallet, Kokiri Sword, Hero's Shield, Deku/Goron/Zora Masks,
            // Ocarina of Time, Song of Time.
            inv.SetTier("sword", 1);     // Kokiri
            inv.SetTier("shield", 1);    // Hero's
            inv.SetTier("magic", 1);     // first magic upgrade (single)
            inv.SetTier("wallet", 0);    // Child Wallet
            inv.Set("mask_deku", true);
            inv.Set("mask_goron", true);
            inv.Set("mask_zora", true);
            inv.Set("ocarina", true);
            inv.Set("song_time", true);
            inv.Set("tatl", true);       // Tatl on by default (MM companion)
        }
        return inv;
    }

    /// <summary>Bare minimum: 3 hearts (+ Kokiri tunic/boots for OoT).</summary>
    public static PlaytestInventory Nothing(bool oot)
    {
        var inv = new PlaytestInventory { Hearts = 3 };
        if (oot) { inv.Set("tunic_kokiri", true); inv.Set("boots_kokiri", true); }
        return inv;
    }

    /// <summary>Everything, within game reason — all toggles on, every tier maxed, 20 hearts.</summary>
    public static PlaytestInventory Full(bool oot)
    {
        var inv = new PlaytestInventory { Hearts = 20 };
        foreach (var t in InventoryCatalog.Tiers(oot)) inv.SetTier(t.Key, t.Options.Length - 1);
        foreach (var g in InventoryCatalog.Groups(oot))
            foreach (var (key, label) in g.Items)
            {
                if (label == "—") continue;
                if (InventoryCatalog.AmmoFor(oot, key) is { } a) inv.SetAmount(key, a.Max);   // #13: ammo → full count
                else inv.Set(key, true);
            }
        return inv;
    }

    // ── Serialisation ──
    private sealed class Dto
    {
        public int Hearts { get; set; } = 3;
        public Dictionary<string, int> Tiers { get; set; } = new();
        public List<string> Toggles { get; set; } = new();
        public Dictionary<string, int> Amounts { get; set; } = new();
        public string PlayerName { get; set; } = "Link";
        public bool ZTargetHold { get; set; }
    }

    public string ToJson() => JsonSerializer.Serialize(new Dto
    { Hearts = Hearts, Tiers = new(Tiers), Toggles = Toggles.ToList(), Amounts = new(Amounts),
      PlayerName = PlayerName, ZTargetHold = ZTargetHold });

    public static PlaytestInventory FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new PlaytestInventory();
        try
        {
            var d = JsonSerializer.Deserialize<Dto>(json) ?? new Dto();
            return new PlaytestInventory { Hearts = d.Hearts, Tiers = new(d.Tiers), Toggles = new(d.Toggles),
                                           Amounts = new(d.Amounts ?? new()), ZTargetHold = d.ZTargetHold,
                                           PlayerName = string.IsNullOrWhiteSpace(d.PlayerName) ? "Link" : d.PlayerName };
        }
        catch { return new PlaytestInventory(); }
    }

    /// <summary>Compact JSON object embedded in the O2R "mh/info" payload (engine reads it).</summary>
    public string ToPayloadJson()
    {
        string tiers = string.Join(",", Tiers.Select(kv => $"\"{kv.Key}\":{kv.Value}"));
        string toggles = string.Join(",", Toggles.OrderBy(s => s).Select(s => $"\"{s}\""));
        string amounts = string.Join(",", Amounts.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key)
                                                  .Select(kv => $"\"{kv.Key}\":{kv.Value}"));
        string nm = (PlayerName ?? "Link").Replace("\\", "").Replace("\"", "");
        return $"{{\"hearts\":{Hearts},\"playerName\":\"{nm}\",\"zTarget\":{(ZTargetHold ? 1 : 0)}," +
               $"\"tiers\":{{{tiers}}},\"toggles\":[{toggles}],\"amounts\":{{{amounts}}}}}";
    }
}
