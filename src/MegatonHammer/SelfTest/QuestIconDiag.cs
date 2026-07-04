using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>--diagquesticons : decode the 4 OoT dungeon quest icons (boss key/compass/map/small key) from
/// icon_item_24_static and save PNGs so the file index + offsets can be verified visually.</summary>
public static class QuestIconDiag
{
    public static void Run()
    {
        const string oot = @"D:\Copilot_OOT\READ_ONLY_GameROMs\Legend of Zelda, The - Ocarina of Time (USA).z64";
        string outDir = @"D:\Copilot_OOT\WorkFolders\MegatonHammer\out\questicons";
        System.IO.Directory.CreateDirectory(outDir);
        var rom = new RomImage(oot);
        var src = new ItemIconSource(rom);
        // Probe-report which file got picked as icon_item_24_static.
        for (int i = 8; i < System.Math.Min(16, rom.Files.Count); i++)
            if (rom.Files[i].Exists)
            { try { var b = rom.GetFile(i); System.Console.WriteLine($"file {i}: len=0x{b.Length:X}"); } catch { } }

        (string name, int gi)[] items = { ("bosskey", 0x3F), ("compass", 0x40), ("map", 0x41), ("smallkey", 0x42) };
        foreach (var (name, gi) in items)
        {
            int idx = Editor.GetItemTable.IconForGi(gi);
            var bmp = src.Icon(idx);
            if (bmp == null) { System.Console.WriteLine($"{name}: NULL (idx=0x{idx:X})"); continue; }
            string p = System.IO.Path.Combine(outDir, $"quest_{name}.png");
            bmp.Save(p, System.Drawing.Imaging.ImageFormat.Png);
            System.Console.WriteLine($"{name}: idx=0x{idx:X} -> {bmp.Width}x{bmp.Height} {p}");
        }
    }
}
