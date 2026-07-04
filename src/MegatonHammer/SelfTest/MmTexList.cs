using MegatonHammer.Rom;
using MegatonHammer.Textures;

namespace MegatonHammer.SelfTest;

/// <summary>Lists Majora's Mask ROM textures (name → category/format/size) so the injector can pick
/// real textures instead of solid colours. Run: MegatonHammer --listmmtex [substr]</summary>
public static class MmTexList
{
    private static readonly string MmRomPath = Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).z64");

    public static void Run(string[] args)
    {
        string? filter = args.Length >= 2 ? args[1] : null;
        var rom = new RomImage(MmRomPath);
        var lib = new TextureLibrary();
        var src = new RomTextureSource(rom);
        var map = RomAssetIndex.BuildMap(rom);
        var (allTex, allFolders) = SceneTextureMapper.Build(rom, src.Scan(), map);
        lib.AddRomTextures(allTex, src, map.FileScene, allFolders);
        Console.WriteLine($"MM textures: {lib.RomCount}");

        // Print one example per category, plus any matching a filter, with decoded size.
        var byCat = new Dictionary<string, (string name, int w, int h)>();
        var matches = new List<string>();
        foreach (var e in lib.Entries)
        {
            if (!e.Name.StartsWith("rom_")) continue;
            int w = 0, h = 0;
            try { var bmp = e.Image; if (bmp != null) { w = bmp.Width; h = bmp.Height; } } catch { }
            if (!byCat.ContainsKey(e.Category)) byCat[e.Category] = (e.Name, w, h);
            if (filter != null && (e.Category.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                    e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                matches.Add($"  {e.Name}  [{e.Category}] {w}x{h}");
        }
        Console.WriteLine($"\n=== one texture per category ({byCat.Count}) ===");
        foreach (var kv in byCat.OrderBy(k => k.Key))
            Console.WriteLine($"  {kv.Value.name}  [{kv.Key}] {kv.Value.w}x{kv.Value.h}");
        if (filter != null)
        {
            Console.WriteLine($"\n=== matches for '{filter}' ({matches.Count}) ===");
            foreach (var m in matches.Take(40)) Console.WriteLine(m);
            // Dump decoded PNGs for any name-exact matches so we can eyeball the decode.
            string dump = System.IO.Path.Combine(Editor.AppPaths.BaseDir, @"roms\mm_test\tex_dump");
            Directory.CreateDirectory(dump);
            foreach (var e in lib.Entries)
                if (e.Name.Equals(filter, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        e.Image.Save(Path.Combine(dump, e.Name + ".png"));
                        Console.WriteLine($"  saved {e.Name}.png  type={e.TypeLabel} {e.Image.Width}x{e.Image.Height}");
                    }
                    catch (Exception ex) { Console.WriteLine($"  decode FAIL {e.Name}: {ex.Message}"); }
                }
        }
    }
}
