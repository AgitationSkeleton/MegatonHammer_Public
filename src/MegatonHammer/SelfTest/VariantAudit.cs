using MegatonHammer.Editor;
using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>--variantaudit [mm] : for every VARIABLE actor (one id → different models/objects/DLs by its
/// spawn variable — Kokiri kids, townsfolk, shopkeepers, dungeon switch/wall objects, MM lens props/graves…)
/// resolve EACH variant and report model?/tex/notex/posed/maxdim, so a variant that billboards, crumples,
/// renders untextured, or blows up (wrong DL) is caught. Companion to --rendervariants (the visual pass).</summary>
public static class VariantAudit
{
    private static readonly string Oot = Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
    private static readonly string Mm  = Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).z64");

    // (actorId, label, variant values to sweep). Variant encodings mirror each actor's params decode.
    public static readonly (ushort id, string label, ushort[] vars)[] OotSets =
    {
        (0x0009, "Door",              new ushort[]{0,1,2,3,4,5,6,7}),
        (0x000A, "Chest",             new ushort[]{0x0000,0x1000,0x5000,0x6000,0x7000,0x9000,0xA000}),
        (0x003D, "En_Ossan shopkeeper",new ushort[]{0,1,2,3,4,5,6,7,8,9,10}),
        (0x0093, "Bg_Po_Event",       new ushort[]{0x000,0x100,0x200,0x300,0x400}),
        (0x00AE, "Bg_Haka_Megane",    new ushort[]{0,1,2,3,4,5}),
        (0x00B8, "Bg_Spot09_Obj",     new ushort[]{0,1,2,3,4}),
        (0x00C8, "Bg_Bdan_Objects",   new ushort[]{0,1,2}),
        (0x00CF, "Bg_Hidan_Kowareru", new ushort[]{0,1,2}),
        (0x00E6, "Bg_Bdan_Switch",    new ushort[]{0,1,2,3,4}),
        (0x012A, "Obj_Switch",        new ushort[]{0,1,2,3,4,0x10,0x11,0x12,0x13}),
        (0x0133, "En_Daiku carpenter", new ushort[]{0,1,2,3}),
        (0x014E, "En_Ishi rock",      new ushort[]{0,1}),
        (0x0163, "En_Ko Kokiri kid",  new ushort[]{0,1,2,3,4,5,6,7,8,9,10,11,12}),
        (0x016E, "En_Hy townsfolk",   new ushort[]{0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20}),
        (0x01A3, "En_Dnt_Nomal scrub",new ushort[]{0,1}),
        (0x01BA, "Bg_Mizu_Bwall",     new ushort[]{0,1,2,3,4}),
    };
    public static readonly (ushort id, string label, ushort[] vars)[] MmSets =
    {
        (0x0093, "MM Obj_Switch",     new ushort[]{0,1,2,3,4,5}),
        (0x002A, "MM En_Ossan",       new ushort[]{0,1}),
        (0x0158, "MM En_Test2 lens",  new ushort[]{0,1,2,3,4,5,6,7,8,9,10,11,12}),
        (0x015C, "MM En_Sekihi grave",new ushort[]{0,1,2,3,4}),
        (0x0255, "Bg_Ikana_Bombwall", new ushort[]{0x000,0x100}),
    };

    public static void Run(string[] args)
    {
        bool mm = args.Length >= 2 && args[1].Equals("mm", System.StringComparison.OrdinalIgnoreCase);
        string path = mm ? Mm : Oot;
        if (!System.IO.File.Exists(path)) { System.Console.WriteLine($"[variantaudit] ROM not found: {path}"); return; }
        var rom = new RomImage(path);
        var resolver = new ActorModelResolver(rom);
        var sets = mm ? MmSets : OotSets;

        System.Console.WriteLine($"[variantaudit] {(mm ? "MM" : "OoT")}: variable actors, each variant resolved:");
        foreach (var (id, label, vars) in sets)
        {
            System.Console.WriteLine($"  0x{id:X4} {label} ({vars.Length} variants):");
            foreach (ushort v in vars)
            {
                ObjectModelReader.ResetTexDiag();
                int r0 = ObjectModelReader.SkeletonsRead, p0 = ObjectModelReader.SkeletonsPosedWithAnim;
                var za = new ZActor { Number = id, Variable = v, XPos = 0, YPos = 0, ZPos = 0 };
                object? model; try { model = resolver.Resolve(za, adult: true); } catch (System.Exception e) { System.Console.WriteLine($"      v{v:X4}: EXC {e.Message}"); continue; }
                if (model == null) { System.Console.WriteLine($"      v{v:X4}: BILLBOARD (no model)"); continue; }
                int tex = ObjectModelReader.DiagTexTris, notex = ObjectModelReader.DiagNoTexTris;
                int readD = ObjectModelReader.SkeletonsRead - r0, posedD = ObjectModelReader.SkeletonsPosedWithAnim - p0;
                var wb = resolver.ModelWorldBounds(za, true);
                float md = wb is (var mn, var mx) ? System.MathF.Max(mx.X - mn.X, System.MathF.Max(mx.Y - mn.Y, mx.Z - mn.Z)) : 0;
                string flag = "";
                if (readD > posedD) flag += " CRUMPLE";
                if (tex + notex > 0 && notex * 100 / (tex + notex) >= 50) flag += " UNTEX";
                if (md > 5000) flag += " GIANT";
                System.Console.WriteLine($"      v{v:X4}: tris={tex + notex} (tex {tex}/{notex}) maxdim={md:F0}u{(readD > 0 ? $" skel[{posedD}/{readD}]" : "")}{flag}");
            }
        }
    }
}
