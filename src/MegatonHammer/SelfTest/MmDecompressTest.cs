using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Decompresses an MM ROM to a flat (ROM==VROM) image and validates it by reading
/// <c>gSceneTable</c> directly out of the result. On the US retail ROM the table is at vrom
/// <c>0xC5A1E0</c> (MMR's <c>SCENE_TABLE</c>); slot 0 must be Z2_20SICHITAI2 (titleId 0x116,
/// drawCfg 1) and slot 7 KAKUSIANA. This is the base image the MM N64 injector now targets.
/// Run: MegatonHammer --decompressmm [inRom] [outRom]
/// </summary>
public static class MmDecompressTest
{
    // US retail gSceneTable VROM (decompressed). Entry size 0x10; sceneFile {vromStart,vromEnd} at +0/+4.
    public const int UsRetailSceneTableVrom = 0xC5A1E0;

    private const string DefaultIn =
        @"D:\Copilot_OOT\READ_ONLY_GameROMs\Legend of Zelda, The - Majora's Mask (USA).z64";

    public static void Run(string[] args)
    {
        string inRom  = args.Length >= 2 ? args[1] : DefaultIn;
        string outRom = args.Length >= 3 ? args[2]
            : Path.Combine(Path.GetTempPath(), "MegatonHammer", "mm_decompressed.z64");
        Directory.CreateDirectory(Path.GetDirectoryName(outRom)!);

        var rom = new RomImage(inRom);
        Console.WriteLine($"loaded {Path.GetFileName(inRom)}: game={rom.Game}, files={rom.Files.Count}, dmadata@0x{rom.DmaTableOffset:X}");

        // Diagnostic: report any compressed file whose Yaz0 decode throws or yields empty.
        int fails = 0;
        foreach (var f in rom.Files)
        {
            if (!f.Exists || !f.Compressed) continue;
            try
            {
                var b = Yaz0.Decompress(rom.Data, (int)f.RomStart);
                if (b.Length != f.Size) { Console.WriteLine($"  [yaz0] file#{f.Index} rom@0x{f.RomStart:X} size mismatch got={b.Length} want=0x{f.Size:X}"); fails++; }
            }
            catch (Exception ex) { Console.WriteLine($"  [yaz0] file#{f.Index} rom@0x{f.RomStart:X} THREW {ex.GetType().Name}: {ex.Message}"); fails++; }
        }
        Console.WriteLine($"yaz0 decode failures: {fails}");

        var dec = MmRomDecompressor.Decompress(rom);
        File.WriteAllBytes(outRom, dec);
        Console.WriteLine($"decompressed -> {outRom} ({dec.Length} bytes, 0x{dec.Length:X})");

        // Validate the scene table directly out of the flat image.
        int st = UsRetailSceneTableVrom;
        uint U32(int o) => (uint)((dec[o] << 24) | (dec[o + 1] << 16) | (dec[o + 2] << 8) | dec[o + 3]);
        ushort U16(int o) => (ushort)((dec[o] << 8) | dec[o + 1]);

        Console.WriteLine($"gSceneTable @ vrom 0x{st:X}:");
        foreach (int idx in new[] { 0, 7, 8 })
        {
            int o = st + idx * 0x10;
            Console.WriteLine($"  slot 0x{idx:X2}: vrom[0x{U32(o):X7},0x{U32(o + 4):X7}] titleId=0x{U16(o + 8):X4} drawCfg=0x{dec[o + 0xB]:X2}");
        }

        int s7 = (int)U32(st + 7 * 0x10);
        int e7 = (int)U32(st + 7 * 0x10 + 4);
        bool slot0Ok = U16(st + 8) == 0x0116 && dec[st + 0xB] == 0x01;
        bool slot7Ok = s7 > 0 && e7 > s7 && dec[s7] < 0x20;   // valid scene header command
        // Read KAKUSIANA's room list (scene header cmd 0x04) to report room count.
        int rooms = -1;
        for (int p = s7; p < e7 && p + 8 <= dec.Length; p += 8)
        {
            if (dec[p] == 0x14) break;
            if (dec[p] == 0x04) { rooms = dec[p + 1]; break; }
        }
        Console.WriteLine($"KAKUSIANA(slot7): scene vrom[0x{s7:X},0x{e7:X}] size=0x{e7 - s7:X} rooms={rooms}");
        Console.WriteLine(slot0Ok && slot7Ok
            ? "[decompressmm] PASS — flat image valid; scene table & KAKUSIANA readable."
            : "[decompressmm] FAIL — scene table did not validate (wrong ROM/version?).");
    }
}
