using MegatonHammer.Editor;

namespace MegatonHammer.SelfTest;

/// <summary>Verifies the playtest-inventory model: the per-game defaults match the spec, presets are
/// sane, and JSON round-trips losslessly. Run: MegatonHammer --testinv</summary>
public static class InventoryTest
{
    public static void Run()
    {
        int fail = 0;
        void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fail++; }

        // ── OoT default: Kokiri+Master sword, Deku+Hylian shield, Fairy Ocarina, Child Wallet,
        //    Goron Bracelet, Kokiri tunic+boots, 3 hearts. ──
        var oot = PlaytestInventory.Default(oot: true);
        Console.WriteLine("OoT default:");
        Check(oot.Hearts == 3, "3 hearts");
        Check(oot.Tier("sword") == 2, "sword = Master (grants Kokiri+Master)");
        Check(oot.Tier("shield") == 2, "shield = Hylian (grants Deku+Hylian)");
        Check(oot.Tier("ocarina") == 1, "ocarina = Fairy");
        Check(oot.Tier("wallet") == 0, "wallet = Child");
        Check(oot.Tier("strength") == 1, "strength = Goron Bracelet");
        Check(oot.Has("tunic_kokiri") && oot.Has("boots_kokiri"), "Kokiri tunic + boots");

        // ── MM default: Kokiri sword, Hero's shield, Child Wallet, Deku/Goron/Zora masks,
        //    Ocarina of Time, Song of Time, 3 hearts. ──
        var mm = PlaytestInventory.Default(oot: false);
        Console.WriteLine("MM default:");
        Check(mm.Hearts == 3, "3 hearts");
        Check(mm.Tier("sword") == 1, "sword = Kokiri");
        Check(mm.Tier("shield") == 1, "shield = Hero's");
        Check(mm.Tier("wallet") == 0, "wallet = Child");
        Check(mm.Has("mask_deku") && mm.Has("mask_goron") && mm.Has("mask_zora"), "Deku/Goron/Zora masks");
        Check(mm.Has("ocarina"), "Ocarina of Time");
        Check(mm.Has("song_time"), "Song of Time");

        // ── Presets ──
        Console.WriteLine("Presets:");
        var nothingO = PlaytestInventory.Nothing(true);
        Check(nothingO.Hearts == 3 && nothingO.Has("tunic_kokiri") && nothingO.Has("boots_kokiri") && nothingO.Toggles.Count == 2,
            "OoT Nothing = 3 hearts + Kokiri tunic/boots only");
        var nothingM = PlaytestInventory.Nothing(false);
        Check(nothingM.Hearts == 3 && nothingM.Toggles.Count == 0, "MM Nothing = 3 hearts only");
        var fullO = PlaytestInventory.Full(true);
        Check(fullO.Hearts == 20 && fullO.Tier("sword") == 3 && fullO.Tier("wallet") == 2 && fullO.Has("song_prelude"),
            "OoT Full = 20 hearts, maxed tiers, all toggles");
        var fullM = PlaytestInventory.Full(false);
        int maskCount = fullM.Toggles.Count(t => t.StartsWith("mask_"));
        Check(maskCount == 24, $"MM Full has all 24 masks (got {maskCount})");

        // ── JSON round-trip ──
        Console.WriteLine("JSON round-trip:");
        var rt = PlaytestInventory.FromJson(oot.ToJson());
        Check(rt.Hearts == oot.Hearts && rt.Tier("sword") == 2 && rt.Has("tunic_kokiri") && rt.Toggles.SetEquals(oot.Toggles),
            "OoT default survives ToJson/FromJson");
        Console.WriteLine($"  OoT payload: {oot.ToPayloadJson()}");
        Console.WriteLine($"  MM  payload: {mm.ToPayloadJson()}");

        // ── Catalog sanity (MM mask grid is 6×4) ──
        var masks = InventoryCatalog.Groups(false).First(g => g.Name == "Masks");
        Check(masks.Columns == 6 && masks.Items.Length == 24, "MM mask pane is 6×4 (24 masks)");

        // ── N64 MM save pokes (PJ64 parity): masks land in items[], C-buttons cleared ──
        Console.WriteLine("MM N64 save pokes:");
        var mmPokes = Rom.N64SavePokes.ComputeMM(mm, "custom", out _);
        uint PokeVal(int off)
        {
            uint v = 0xDEAD;
            foreach (var p in mmPokes) if (p.Offset == off) v = p.Value;   // last write wins (mirrors apply order)
            return v;
        }
        Check(PokeVal(0x70 + 0x1D) == 0x32, "Deku mask -> items[0x1D]=0x32");   // items base 0x70 + SLOT_MASK_DEKU
        Check(PokeVal(0x70 + 0x23) == 0x33, "Goron mask -> items[0x23]=0x33");
        Check(PokeVal(0x70 + 0x29) == 0x34, "Zora mask -> items[0x29]=0x34");
        Check(PokeVal(0x4D) == 0xFF && PokeVal(0x4E) == 0xFF && PokeVal(0x4F) == 0xFF,
            "C-buttons cleared (buttonItems[0][1..3]=0xFF)");

        Console.WriteLine($"\n{(fail == 0 ? "ALL PASS" : $"{fail} FAILURE(S)")}");
    }
}
