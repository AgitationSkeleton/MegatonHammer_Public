using MegatonHammer.Editor;

namespace MegatonHammer.Rom;

/// <summary>
/// Computes a flat list of SaveContext memory pokes (offset, size, value) that reproduce the SoH/2Ship
/// <c>MhApplyCustomInventory</c> behaviour on the N64 / PJ64 playtest path. All game-specific item/bit
/// knowledge lives HERE, in testable C#; the PJ64 fork just applies the list blindly via RMW. The offsets
/// are relative to the <c>save</c> substruct base the fork already targets (gc-eu-mq-dbg
/// kSaveContext=0x8011A5D0; MM kSaveContext=0x801EF670, base=Save). Verified against SoH's logged
/// "[mh_inv] applied:" values (e.g. OoT Default → equipment=0x1133, equips=0x1122, upgrades=0x40).
/// </summary>
public static class N64SavePokes
{
    /// <summary>One memory write: Size is 1/2/4 bytes, big-endian, at SaveContext+Offset.</summary>
    public readonly record struct Poke(int Offset, int Size, uint Value);

    // ── OoT (gc-eu-mq-dbg) save-substruct offsets ─────────────────────────
    private const int O_HealthCap = 0x2E, O_Health = 0x30, O_MagicLevel = 0x32, O_Magic = 0x33;
    private const int O_PlayerName = 0x24;                       // char playerName[8] (OoT name encoding)
    private const int O_SwordHealth = 0x36, O_BgsFlag = 0x3E;   // Giant's Knife health + Biggoron's-Sword flag
    private const int O_IsMagic = 0x3A, O_IsDoubleMagic = 0x3C, O_ButtonItems = 0x68, O_CButtonSlots = 0x6C;
    private const int O_EquipsEquipment = 0x70, O_Items = 0x74, O_Ammo = 0x8C, O_InvEquipment = 0x9C;
    private const int O_Upgrades = 0xA0, O_QuestItems = 0xA4, O_MagicCap = 0x13F4, O_ZTarget = 0x140C;
    private const int OotItemCount = 24, OotAmmoCount = 16;
    private const byte ITEM_NONE = 0xFF;

    // C-item key → (slot index, item id written to items[slot], starting ammo). Mirrors SoH's kItems
    // (debugconsole.cpp) with the gItemSlots slot mapping resolved (ocarina_time/longshot share a slot,
    // so items 9.. land one slot lower than their id).
    private static readonly (string key, int slot, byte item, sbyte ammo)[] OotItems =
    [
        ("stick",0,0,10), ("nut",1,1,20), ("bomb",2,2,20), ("bow",3,3,30), ("fire_arrow",4,4,0),
        ("dins_fire",5,5,0), ("slingshot",6,6,30), ("bombchu",8,9,20),
        // hookshot/longshot share slot 9 → handled as a tier below (item 10 = Hookshot, 11 = Longshot).
        ("ice_arrow",10,12,0), ("farores_wind",11,13,0), ("boomerang",12,14,0), ("lens",13,15,0),
        ("bean",14,16,10), ("hammer",15,17,0), ("light_arrow",16,18,0), ("nayrus_love",17,19,0),
        ("bottle",18,20,0),
    ];

    // quest-item key → bit index in inventory.questItems.
    private static readonly (string key, int bit)[] OotQuest =
    [
        ("med_forest",0),("med_fire",1),("med_water",2),("med_spirit",3),("med_shadow",4),("med_light",5),
        ("song_minuet",6),("song_bolero",7),("song_serenade",8),("song_requiem",9),("song_nocturne",10),("song_prelude",11),
        ("song_lullaby",12),("song_epona",13),("song_saria",14),("song_suns",15),("song_time",16),("song_storms",17),
        ("stone_emerald",18),("stone_ruby",19),("stone_sapphire",20),("agony",21),
    ];

    // upgrade key → (shift, bit-width) in inventory.upgrades.
    private static readonly (string key, int shift, int bits)[] OotUpgrades =
    [
        ("quiver",0,3),("bombbag",3,3),("strength",6,3),("scale",9,3),("wallet",12,2),("bulletbag",14,2),
    ];

    /// <summary>Aggregate field values (for logging / verification against SoH's [mh_inv] applied:).</summary>
    public readonly record struct OotSummary(int HealthCap, ushort InvEquipment, ushort EquipsEquipment,
                                             uint Upgrades, uint QuestItems, byte ButtonItem0, int MagicCap, int OcarinaSlotItem);

    /// <summary>
    /// Computes the OoT poke list for a custom/empty inventory. Always writes the full field set (from a
    /// zero base) so it deterministically overwrites whatever the debug save left — i.e. clears + applies.
    /// </summary>
    public static List<Poke> ComputeOoT(PlaytestInventory inv, string mode, out OotSummary sum)
    {
        var pokes = new List<Poke>();
        int hearts = Math.Max(1, inv.Hearts);
        bool empty = mode == "empty";

        int Tier(string k) => empty ? 0 : inv.Tier(k);
        bool Has(string k) => !empty && inv.Has(k);
        int Amount(string k) => empty ? 0 : inv.Amount(k);

        // Hearts.
        pokes.Add(new(O_HealthCap, 2, (uint)(hearts * 16)));
        pokes.Add(new(O_Health, 2, (uint)(hearts * 16)));

        // Owned equipment bits (sword/shield up to tier; tunics/boots toggles; Kokiri tunic+boots always).
        int sw = Math.Clamp(Tier("sword"), 0, 3), sh = Math.Clamp(Tier("shield"), 0, 3);
        uint owned = 0;
        for (int i = 0; i < sw; i++) owned |= (1u << i) << (0 * 4);   // sword nibble
        for (int i = 0; i < sh; i++) owned |= (1u << i) << (1 * 4);   // shield nibble
        owned |= 1u << 8;    // EQUIP_INV_TUNIC_KOKIRI (always)
        owned |= 1u << 12;   // EQUIP_INV_BOOTS_KOKIRI (always)
        if (Has("tunic_goron")) owned |= 1u << 9;
        if (Has("tunic_zora"))  owned |= 1u << 10;
        if (Has("boots_iron"))  owned |= 1u << 13;
        if (Has("boots_hover")) owned |= 1u << 14;
        pokes.Add(new(O_InvEquipment, 2, owned));

        // Equipped nibbles (highest owned sword/shield, Kokiri tunic+boots) + B-button = sword item.
        uint equips = (1u << 8) | (1u << 12);   // tunic=1 (Kokiri), boots=1 (Kokiri)
        if (sw > 0) equips |= (uint)sw << 0;
        if (sh > 0) equips |= (uint)sh << 4;
        pokes.Add(new(O_EquipsEquipment, 2, equips));
        byte bItem0 = sw > 0 ? (byte)(59 + (sw - 1)) : ITEM_NONE;   // ITEM_SWORD_KOKIRI=59/MASTER=60/BGS=61
        pokes.Add(new(O_ButtonItems + 0, 1, bItem0));
        // #10: the BGS slot is Biggoron's Sword ONLY when bgsFlag is set; otherwise the game shows the
        // breakable Giant's Knife (broken icon). For sword tier 3, set bgsFlag=1 + swordHealth=8 (full,
        // unbreakable) so the user actually gets Biggoron's Sword. Lower tiers clear bgsFlag.
        pokes.Add(new(O_BgsFlag, 1, sw >= 3 ? 1u : 0u));
        if (sw >= 3) pokes.Add(new(O_SwordHealth, 2, 8u));

        // #21: injected player name (default "Link"). Without it the save has a blank name and Navi/text
        // shows e.g. "Dark " for Dark Link. OoT name encoding, 8 bytes at SaveContext+0x24.
        var nameBytes = inv.EncodeNameOoT();
        for (int i = 0; i < 8; i++) pokes.Add(new(O_PlayerName + i, 1, nameBytes[i]));
        // #5: Z-targeting mode (0 = Switch/Toggle, 1 = Hold). A control preference, applied regardless of mode.
        pokes.Add(new(O_ZTarget, 1, inv.ZTargetHold ? 1u : 0u));
        pokes.Add(new(O_ButtonItems + 1, 1, ITEM_NONE));           // C-left/down/right cleared for a fresh slate
        pokes.Add(new(O_ButtonItems + 2, 1, ITEM_NONE));
        pokes.Add(new(O_ButtonItems + 3, 1, ITEM_NONE));
        pokes.Add(new(O_CButtonSlots + 0, 1, ITEM_NONE));
        pokes.Add(new(O_CButtonSlots + 1, 1, ITEM_NONE));
        pokes.Add(new(O_CButtonSlots + 2, 1, ITEM_NONE));

        // Upgrade levels accumulate from explicit tier dropdowns AND from whatever the #13 ammo amounts
        // require; the highest wins (so 50 arrows auto-grants the big quiver even with the dropdown at None).
        var upgByShift = new Dictionary<int, int>();
        void RaiseUpg(int shift, int level) { if (level > upgByShift.GetValueOrDefault(shift, 0)) upgByShift[shift] = level; }
        foreach (var (key, shift, bits) in OotUpgrades)
        { int v = Tier(key); if (v > 0) RaiseUpg(shift, v & ((1 << bits) - 1)); }

        // items[] / ammo[]: clear all, then set toggled C-items (and the ocarina by tier). Ammo-spec keys
        // (#13) are driven by their count spinner below, not the checkbox toggle — skip them here.
        var items = new byte[OotItemCount]; var ammo = new byte[OotAmmoCount];
        for (int i = 0; i < OotItemCount; i++) items[i] = ITEM_NONE;
        foreach (var (key, slot, item, am) in OotItems)
            if (Has(key) && InventoryCatalog.AmmoFor(true, key) is null)
            { items[slot] = item; if (am != 0) ammo[slot] = (byte)am; }
        // Hookshot/Longshot tier → slot 9 (both share it): 1 = Hookshot (item 10), 2 = Longshot (item 11).
        int hookTier = Tier("hookshot");
        if (hookTier == 1) items[9] = 10;
        else if (hookTier >= 2) items[9] = 11;
        // #13: varying-amount items — the count sets ammo[slot] (item present) and auto-grants its capacity upgrade.
        foreach (var a in InventoryCatalog.Ammo(true))
        {
            int amt = Math.Min(Amount(a.Key), a.Max);
            if (amt <= 0) continue;
            items[a.Slot] = a.Item; ammo[a.Slot] = (byte)amt;
            if (a.UpgShift >= 0) RaiseUpg(a.UpgShift, a.LevelFor(amt));
        }
        int oc = Tier("ocarina");
        if (oc == 1) items[7] = 7;        // ITEM_OCARINA_FAIRY
        else if (oc >= 2) items[7] = 8;   // ITEM_OCARINA_TIME
        // #22: the two trade-quest slots (single item each). Child slot 0x17 = ITEM 0x20+idx (Weird Egg
        // 0x21..Mask of Truth 0x2B); adult slot 0x16 = ITEM 0x2C+idx (Pocket Egg 0x2D..Claim Check 0x37).
        int tc = Tier("trade_child"); if (tc > 0 && 0x17 < OotItemCount) items[0x17] = (byte)(0x20 + tc);
        int ta = Tier("trade_adult"); if (ta > 0 && 0x16 < OotItemCount) items[0x16] = (byte)(0x2C + ta);
        for (int i = 0; i < OotItemCount; i++) pokes.Add(new(O_Items + i, 1, items[i]));
        for (int i = 0; i < OotAmmoCount; i++) pokes.Add(new(O_Ammo + i, 1, ammo[i]));

        // Upgrades (explicit tiers merged with ammo-derived capacity levels, accumulated above).
        uint upgrades = 0;
        foreach (var (shift, level) in upgByShift) upgrades |= (uint)level << shift;
        pokes.Add(new(O_Upgrades, 4, upgrades));

        // Quest items (songs / medallions / stones).
        uint quest = 0;
        foreach (var (key, bit) in OotQuest) if (Has(key)) quest |= 1u << bit;
        pokes.Add(new(O_QuestItems, 4, quest));

        // Magic.
        int mg = Tier("magic");
        int magicCap = 0;
        if (mg >= 1)
        {
            int level = mg >= 2 ? 2 : 1;
            magicCap = level * 0x30;
            pokes.Add(new(O_IsMagic, 1, 1));
            pokes.Add(new(O_MagicLevel, 1, (uint)level));
            pokes.Add(new(O_IsDoubleMagic, 1, (uint)(mg >= 2 ? 1 : 0)));
            pokes.Add(new(O_MagicCap, 2, (uint)magicCap));
            pokes.Add(new(O_Magic, 1, (uint)magicCap));
        }
        else
        {
            pokes.Add(new(O_IsMagic, 1, 0)); pokes.Add(new(O_MagicLevel, 1, 0));
            pokes.Add(new(O_IsDoubleMagic, 1, 0)); pokes.Add(new(O_MagicCap, 2, 0)); pokes.Add(new(O_Magic, 1, 0));
        }

        sum = new OotSummary(hearts * 16, (ushort)owned, (ushort)equips, upgrades, quest, bItem0, magicCap, items[7]);
        return pokes;
    }

    // ── MM (NTSC-U retail) Save-substruct offsets (base 0x801EF670, = Save). Verified vs mm-main z64save.h.
    private const int M_HasTatl = 0x22, M_HealthCap = 0x34, M_Health = 0x36, M_MagicLevel = 0x38, M_Magic = 0x39;
    private const int M_IsMagic = 0x40, M_IsDoubleMagic = 0x41, M_EquipsEquipment = 0x6C, M_Items = 0x70;
    // ItemEquips @ Save+0x4C: buttonItems[4][4] @+0x00, cButtonSlots[4][4] @+0x10 (so absolute 0x4C / 0x5C).
    private const int M_ButtonItems = 0x4C, M_CButtonSlots = 0x5C;
    private const int M_Ammo = 0xA0, M_Upgrades = 0xB8, M_QuestItems = 0xBC, M_MagicCap = 0x3F2E;
    private const int M_ZTarget = 0x3F45;   // SaveContext.options.zTargetSetting (0x3F40 + 0x5)
    private const int MmItemCount = 48, MmAmmoCount = 24;

    // MM regular item key → (items[] slot, item id, starting ammo). Slots/ids from mm-main z64item.h.
    private static readonly (string key, int slot, byte item, sbyte ammo)[] MmItems =
    [
        ("ocarina",0x00,0x00,0), ("bow",0x01,0x01,30), ("bomb",0x06,0x06,20), ("bombchu",0x07,0x07,20),
        ("bean",0x0A,0x0A,0), ("powder_keg",0x0C,0x0C,0), ("pictograph",0x0D,0x0D,0), ("lens",0x0E,0x0E,0),
        ("hookshot",0x0F,0x0F,0), ("great_fairy_sword",0x10,0x10,0), ("bottle",0x12,0x12,0),
    ];
    // MM mask key → (items[] slot, item id). Slots 0x18..0x2F (SLOT_MASK_*), ids ITEM_MASK_*. Keys MUST carry
    // the "mask_" prefix to match the editor payload toggles (PlaytestInventory/InventoryIcons) and the 2Ship
    // MhApplyCustomInventory reference — bare keys (deku/goron/zora) silently matched nothing, so no mask imported.
    private static readonly (string key, int slot, byte item)[] MmMasks =
    [
        ("mask_postman",0x18,0x3E), ("mask_allnight",0x19,0x38), ("mask_blast",0x1A,0x47), ("mask_stone",0x1B,0x45),
        ("mask_greatfairy",0x1C,0x40), ("mask_deku",0x1D,0x32), ("mask_keaton",0x1E,0x3A), ("mask_bremen",0x1F,0x46),
        ("mask_bunny",0x20,0x39), ("mask_dongero",0x21,0x42), ("mask_scents",0x22,0x48), ("mask_goron",0x23,0x33),
        ("mask_romani",0x24,0x3C), ("mask_circus",0x25,0x3D), ("mask_kafei",0x26,0x37), ("mask_couple",0x27,0x3F),
        ("mask_truth",0x28,0x36), ("mask_zora",0x29,0x34), ("mask_kamaro",0x2A,0x43), ("mask_gibdo",0x2B,0x41),
        ("mask_garo",0x2C,0x3B), ("mask_captain",0x2D,0x44), ("mask_giant",0x2E,0x49), ("mask_fierce",0x2F,0x35),
    ];
    private static readonly (string key, int bit)[] MmQuest =
    [
        ("remains_odolwa",0),("remains_goht",1),("remains_gyorg",2),("remains_twinmold",3),
        ("song_awakening",6),("song_lullaby",7),("song_bossa",8),("song_elegy",9),("song_oath",10),
        ("song_saria",11),("song_time",12),("song_healing",13),("song_epona",14),("song_soaring",15),
        ("song_storms",16),("song_suns",17),
    ];
    private static readonly (string key, int shift, int bits)[] MmUpgrades =
        [("quiver",0,3),("bombbag",3,3),("wallet",12,2)];

    public readonly record struct MmSummary(int HealthCap, ushort EquipsEquipment, uint Upgrades,
                                            uint QuestItems, int MagicCap, int HasTatl);

    /// <summary>MM (NTSC-U) custom/empty inventory pokes — reproduces 2Ship's MhApplyCustomInventory.
    /// MM swords/shields are equip-only (nibbles); masks/items land in items[48]; tatl is a Save flag.</summary>
    public static List<Poke> ComputeMM(PlaytestInventory inv, string mode, out MmSummary sum)
    {
        var pokes = new List<Poke>();
        int hearts = Math.Max(1, inv.Hearts);
        bool empty = mode == "empty";
        int Tier(string k) => empty ? 0 : inv.Tier(k);
        bool Has(string k) => !empty && inv.Has(k);
        int Amount(string k) => empty ? 0 : inv.Amount(k);

        pokes.Add(new(M_HealthCap, 2, (uint)(hearts * 16)));
        pokes.Add(new(M_Health, 2, (uint)(hearts * 16)));

        // Equipped sword/shield nibbles (no owned-bit array in MM). EQUIP_VALUE_* are 1-based.
        int sw = Math.Clamp(Tier("sword"), 0, 3), sh = Math.Clamp(Tier("shield"), 0, 2);
        uint equips = 0;
        if (sw > 0) equips |= (uint)sw << 0;        // {Kokiri,Razor,Gilded} = 1,2,3
        if (sh > 0) equips |= (uint)sh << 4;        // {Hero,Mirror} = 1,2
        pokes.Add(new(M_EquipsEquipment, 2, equips));

        // Upgrade levels accumulate from explicit tiers + #13 ammo amounts (highest wins).
        var upgByShift = new Dictionary<int, int>();
        void RaiseUpg(int shift, int level) { if (level > upgByShift.GetValueOrDefault(shift, 0)) upgByShift[shift] = level; }
        foreach (var (key, shift, bits) in MmUpgrades)
        { int v = Tier(key); if (v > 0) RaiseUpg(shift, v & ((1 << bits) - 1)); }

        // items[] / ammo[]: clear all, then masks + items + ammo. Ammo-spec keys are driven by the #13
        // count spinner below, not the checkbox toggle — skip them in the toggle loop.
        var items = new byte[MmItemCount]; var ammo = new byte[MmAmmoCount];
        for (int i = 0; i < MmItemCount; i++) items[i] = ITEM_NONE;
        foreach (var (key, slot, item, am) in MmItems)
            if (Has(key) && InventoryCatalog.AmmoFor(false, key) is null)
            { items[slot] = item; if (am != 0) ammo[slot] = (byte)am; }
        foreach (var (key, slot, item) in MmMasks)
            if (Has(key)) items[slot] = item;
        foreach (var a in InventoryCatalog.Ammo(false))
        {
            int amt = Math.Min(Amount(a.Key), a.Max);
            if (amt <= 0) continue;
            items[a.Slot] = a.Item; ammo[a.Slot] = (byte)amt;
            if (a.UpgShift >= 0) RaiseUpg(a.UpgShift, a.LevelFor(amt));
        }
        for (int i = 0; i < MmItemCount; i++) pokes.Add(new(M_Items + i, 1, items[i]));
        for (int i = 0; i < MmAmmoCount; i++) pokes.Add(new(M_Ammo + i, 1, ammo[i]));

        uint upgrades = 0;
        foreach (var (shift, level) in upgByShift) upgrades |= (uint)level << shift;
        pokes.Add(new(M_Upgrades, 4, upgrades));

        uint quest = 0;
        foreach (var (key, bit) in MmQuest) if (Has(key)) quest |= 1u << bit;
        pokes.Add(new(M_QuestItems, 4, quest));

        int mg = Tier("magic"); int magicCap = 0;
        if (mg >= 1)
        {
            int level = mg >= 2 ? 2 : 1; magicCap = level * 0x30;
            pokes.Add(new(M_IsMagic, 1, 1)); pokes.Add(new(M_MagicLevel, 1, (uint)level));
            pokes.Add(new(M_IsDoubleMagic, 1, (uint)(mg >= 2 ? 1 : 0)));
            pokes.Add(new(M_MagicCap, 2, (uint)magicCap)); pokes.Add(new(M_Magic, 1, (uint)magicCap));
        }
        else
        {
            pokes.Add(new(M_IsMagic, 1, 0)); pokes.Add(new(M_MagicLevel, 1, 0));
            pokes.Add(new(M_IsDoubleMagic, 1, 0)); pokes.Add(new(M_MagicCap, 2, 0)); pokes.Add(new(M_Magic, 1, 0));
        }

        int tatl = Has("tatl") ? 1 : 0;
        pokes.Add(new(M_HasTatl, 1, (uint)tatl));
        // #5: Z-targeting mode (0 = Switch/Toggle, 1 = Hold), applied regardless of inventory mode.
        pokes.Add(new(M_ZTarget, 1, inv.ZTargetHold ? 1u : 0u));

        // Clear the C-button equips. The N64 debug map-select save (which our playtest ROM boots from)
        // pre-equips Bow / Red Potion / Ocarina onto the C-buttons; the OTR path never does this (clean save),
        // so it never needed clearing. MM reads the C-button items from equips.buttonItems[0][1..3] regardless
        // of the current form (see GET_CUR_FORM_BTN_ITEM), so clearing exactly those three bytes (+ their
        // cButtonSlots) empties the C-buttons WITHOUT touching any form's B-button (buttonItems[form][0] = sword).
        for (int btn = 1; btn <= 3; btn++)
        {
            pokes.Add(new(M_ButtonItems  + btn, 1, ITEM_NONE));   // buttonItems[0][btn]  -> no item on C-button
            pokes.Add(new(M_CButtonSlots + btn, 1, ITEM_NONE));   // cButtonSlots[0][btn] -> no inventory slot
        }

        sum = new MmSummary(hearts * 16, (ushort)equips, upgrades, quest, magicCap, tatl);
        return pokes;
    }

    /// <summary>Serialises a poke list to the flat text the PJ64 fork reads (one "offsetHex size valueHex"
    /// line each). Tagged with the game so the fork can pick the right SaveContext base.</summary>
    public static string Format(IEnumerable<Poke> pokes, bool oot)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("game=").Append(oot ? "oot" : "mm").Append('\n');
        foreach (var p in pokes)
            sb.Append("0x").Append(p.Offset.ToString("X")).Append(' ').Append(p.Size)
              .Append(" 0x").Append(p.Value.ToString("X")).Append('\n');
        return sb.ToString();
    }
}
