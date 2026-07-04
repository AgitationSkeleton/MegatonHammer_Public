using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Read-only probing for the N64 append-parity work (docs/n64-soh-parity-assessment.md, phase 5).
/// Dumps gSceneTable for OoT + MM (decompressed), flags UNSET / test slots that are safe to
/// repoint as the MH-append target (vs clobbering a real scene), and reports free tail space and
/// spare dmadata capacity. Touches NOTHING — it only reads and prints. Run: MegatonHammer --n64probe
/// </summary>
public static class N64Probe
{
    private static readonly string OotRom = Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
    private static readonly string OotDbgRom = Editor.AppPaths.Rom(@"ZELOOTMA.Z64");   // gc-eu-mq-dbg (OoT injection target)
    private static readonly string MmRom  = Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).z64");

    public static void Run(string[] args)
    {
        Console.WriteLine("=== N64 append-parity probe (read-only) ===\n");
        if (File.Exists(OotRom)) ProbeOot(OotRom, "retail NTSC-1.0"); else Console.WriteLine("[oot] retail ROM not found\n");
        if (File.Exists(OotDbgRom)) ProbeOot(OotDbgRom, "gc-eu-mq-dbg (injection target)"); else Console.WriteLine("[oot] debug ROM not found\n");
        if (File.Exists(MmRom))  ProbeMm();  else Console.WriteLine("[mm] ROM not found\n");
    }

    private static void ProbeOot(string romPath, string label)
    {
        Console.WriteLine($"--- OoT ({label}) ---");
        var rom = new RomImage(romPath);
        var flat = RomBuilder.Decompress(rom);
        var loc = SceneTableLocator.Find(flat.Data, flat.Files);
        Console.WriteLine($"Find -> 0x{loc.Offset:X} (Count={loc.Count}; the locator's Count truncates at NULL runs — real SCENE_ID_MAX ~0x6E), entry 0x{SceneTableLocator.EntrySize:X}");
        if (loc.Offset < 0) { Console.WriteLine("  (not located)\n"); return; }

        // Dump the current OoT-debug append target SCENE_TEST01 (0x65) + neighbours, and list true-empty
        // slots across the real 0x00..0x6D range (the locator's Count is unreliable, so scan the full range).
        DumpOotSlot(flat, loc.Offset, 0x65);   // SCENE_TEST01 (current target)
        DumpOotSlot(flat, loc.Offset, 0x66);   // SCENE_TEST02
        DumpOotSlot(flat, loc.Offset, 0x67);   // SCENE_DEPTH_TEST
        var empty = new List<string>();
        for (int id = 0; id <= 0x6D; id++)
        {
            int o = loc.Offset + id * SceneTableLocator.EntrySize;
            if (o + 8 > flat.Data.Length) break;
            if (U32(flat.Data, o) == 0 && U32(flat.Data, o + 4) == 0) empty.Add($"0x{id:X2}");
        }
        Console.WriteLine($"  true-empty OoT slots (start==end==0, 0x00..0x6D): {(empty.Count == 0 ? "(none)" : string.Join(",", empty))}");
        Console.WriteLine($"  SCENE_TEST01 (0x65) is the current OoT-debug append target (ENTR_TEST01_0 0x0094).");
        ReportTail(flat, "oot");
        Console.WriteLine();
    }

    private static void DumpOotSlot(FlatRom flat, int tableOff, int id)
    {
        int o = tableOff + id * SceneTableLocator.EntrySize;
        if (o + 8 > flat.Data.Length) { Console.WriteLine($"  slot 0x{id:X2} out of range"); return; }
        uint start = U32(flat.Data, o), end = U32(flat.Data, o + 4);
        bool plausible = start < end && end <= flat.Data.Length && (end - start) is > 0x40 and < 0x400000;
        Console.WriteLine($"  slot 0x{id:X2} ({OotSceneNames.Pretty(id)}): start=0x{start:X8} end=0x{end:X8} size={(int)(end - start)} {(start == 0 && end == 0 ? "[EMPTY]" : plausible ? "(scene file)" : "(?)")}");
    }

    private const int MmInjectSceneTableVrom = 0xC5A1E0;   // the hardcoded constant MmInjectScene uses (known-working)

    private static void ProbeMm()
    {
        Console.WriteLine("--- MM (retail NTSC-1.0) ---");
        var rom = new RomImage(MmRom);
        var flat = RomBuilder.Decompress(rom);
        var loc = SceneTableLocator.FindMM(flat.Data, flat.Files, rom);
        Console.WriteLine($"FindMM -> 0x{loc.Offset:X} ({loc.Count} slots); MmInjectScene hardcode = 0x{MmInjectSceneTableVrom:X}; entry 0x{SceneTableLocator.MmEntrySize:X}");

        // Cross-check: dump Termina Field slot 0x2D (Z2_00KEIKOKU) from BOTH candidate tables. The REAL
        // gSceneTable's 0x2D entry must point at a plausible scene file (start<end, within the image).
        DumpSlot("FindMM   ", flat, loc.Offset, 0x2D);
        DumpSlot("hardcode ", flat, MmInjectSceneTableVrom, 0x2D);
        // A couple more real scenes to disambiguate (Clock Town 0x6C, Woodfall 0x1B).
        DumpSlot("FindMM   ", flat, loc.Offset, 0x6C);
        DumpSlot("hardcode ", flat, MmInjectSceneTableVrom, 0x6C);

        // Now list genuinely-empty slots (start==end==0) from BOTH tables so we can see which matches the
        // decomp's UNSET set (0x01-0x06, 0x09, 0x0E, 0x0F).
        Console.WriteLine("  True-empty slots (start==end==0):");
        Console.WriteLine("    via FindMM   : " + EmptySlots(flat, loc.Offset, loc.Count));
        Console.WriteLine("    via hardcode : " + EmptySlots(flat, MmInjectSceneTableVrom, 0x92));
        ReportTail(flat, "mm");
        ProbeMmEntrance(flat);
        Console.WriteLine();
    }

    // MM `code` RAM->VROM: derived from two known anchors in MmInjectScene (Play_Init RAM 0x8016A2C8 =
    // VROM 0xC00808; RoutineRam 0x801BCC48 = VROM 0xC53188). Both give VROM = RAM - 0x7F569AC0.
    private const long MmRamToVrom = -0x7F569AC0L;
    private static int RamToVrom(uint ram) => (int)((long)ram + MmRamToVrom);
    private static bool IsMmCodeRam(uint p) => p is >= 0x80000000 and < 0x80300000;

    // READ-ONLY: locate sSceneEntranceTable + validate the entrance chase lands on Termina's sceneId 0x2D,
    // per docs/n64-soh-parity-assessment.md. Nothing is written. This gates the future 1-byte redirect.
    private static void ProbeMmEntrance(FlatRom flat)
    {
        Console.WriteLine("  --- MM entrance chase (read-only validation) ---");
        Console.WriteLine($"  code RAM->VROM anchors check: Play_Init RAM 0x8016A2C8 -> VROM 0x{RamToVrom(0x8016A2C8):X} (want 0xC00808); gSceneTable VROM 0xC5A1E0 -> RAM 0x{(uint)(0xC5A1E0 - MmRamToVrom):X8}");

        // Fingerprint sSceneEntranceTable: an array of 0xC-byte SceneEntranceTableEntry {u8 count(+3 pad),
        // EntranceTableEntry** table, char* name}. table (+4) must be a code RAM ptr; name (+8) a code RAM
        // ptr or 0; count (+0, big-endian top byte) small. Scan the code region; validate each candidate by
        // the Termina 0x5400 chase yielding sceneId 0x2D (the definitive test — many ptr arrays exist).
        // DIAGNOSTIC: scan near gSceneTable (same source file z_scene_table.c) and report any base whose
        // Termina chase resolves to ANY plausible sceneId (0..0x91), to see if the pointer chain works.
        var any = new List<(int b, int sid)>();
        var hits = new List<int>();
        for (int b = 0xC30000; b + 0x60 * 0xC <= flat.Data.Length && b < 0xC90000; b += 4)
        {
            int sc = ChaseTerminaSceneId(flat, b);
            if (sc is >= 0 and <= 0x91) any.Add((b, sc));
            if (sc == 0x2D) hits.Add(b);
        }
        Console.WriteLine($"  bases near gSceneTable whose chase resolves to a plausible sceneId: {any.Count}" +
                          (any.Count > 0 ? "; first: " + string.Join(" ", any.Take(8).Select(t => $"0x{t.b:X}=>0x{t.sid:X2}")) : ""));
        Console.WriteLine($"  bases yielding Termina 0x2D exactly: {hits.Count}" + (hits.Count > 0 ? " @ " + string.Join(",", hits.Take(6).Select(h => $"0x{h:X}")) : ""));
        int found = hits.Count == 1 ? hits[0] : -1;
        if (found < 0) { Console.WriteLine("  NOT uniquely located — the chase never yields 0x2D; entrance struct/segment assumption to revisit."); return; }
        uint tableRam = (uint)(found - MmRamToVrom);
        Console.WriteLine($"  sSceneEntranceTable @ VROM 0x{found:X} (RAM 0x{tableRam:X8}).");
        int sid = ChaseTerminaSceneId(flat, found, out int sidByteVrom);
        Console.WriteLine($"  Termina 0x5400 -> [0x2A].table[0][0].sceneId = 0x{sid:X2} (want 0x2D) => {(sid == 0x2D ? "VALIDATED" : "MISMATCH")}.");
        Console.WriteLine($"  >>> REDIRECT PATCH TARGET: sceneId byte @ VROM 0x{sidByteVrom:X} (currently 0x{flat.Data[sidByteVrom]:X2}).");
        Console.WriteLine($"  Append-mode write (future): 0x2D -> 0x0E (empty slot) at that byte -> entrance 0x5400 loads appended slot 0x0E; Termina scene data preserved.");
    }

    private static int ChaseTerminaSceneId(FlatRom flat, int tableVrom) => ChaseTerminaSceneId(flat, tableVrom, out _);

    // sSceneEntranceTable[0x2A].table[0][0].sceneId for Termina entrance 0x5400. Returns -1 on any bad ptr;
    // sidByteVrom is the VROM of the sceneId byte itself (the redirect patch target).
    private static int ChaseTerminaSceneId(FlatRom flat, int tableVrom, out int sidByteVrom)
    {
        sidByteVrom = -1;
        int entry = tableVrom + 0x2A * 0xC;                       // sSceneEntranceTable[0x2A]
        if (entry + 0xC > flat.Data.Length) return -1;
        uint tablePtr = U32(flat.Data, entry + 4);                // EntranceTableEntry** table
        if (!IsMmCodeRam(tablePtr)) return -1;
        int tableArr = RamToVrom(tablePtr);                      // -> array of EntranceTableEntry*
        if (tableArr < 0 || tableArr + 4 > flat.Data.Length) return -1;
        uint entryPtr = U32(flat.Data, tableArr);                // [0] = EntranceTableEntry*
        if (!IsMmCodeRam(entryPtr)) return -1;
        int e = RamToVrom(entryPtr);                             // -> EntranceTableEntry
        if (e < 0 || e >= flat.Data.Length) return -1;
        sidByteVrom = e;                                         // [0].sceneId (s8 at +0)
        return (sbyte)flat.Data[e];
    }

    private static void DumpSlot(string tag, FlatRom flat, int tableOff, int id)
    {
        if (tableOff < 0) { Console.WriteLine($"  [{tag}] table not located"); return; }
        int o = tableOff + id * SceneTableLocator.MmEntrySize;
        if (o + 16 > flat.Data.Length) { Console.WriteLine($"  [{tag}] slot 0x{id:X2} out of range"); return; }
        uint start = U32(flat.Data, o), end = U32(flat.Data, o + 4);
        bool plausible = start < end && end <= flat.Data.Length && (end - start) is > 0x40 and < 0x400000;
        Console.WriteLine($"  [{tag}] slot 0x{id:X2}: start=0x{start:X8} end=0x{end:X8} size={(int)(end - start)} {(plausible ? "(plausible scene file)" : "(NOT a scene file)")}");
    }

    private static string EmptySlots(FlatRom flat, int tableOff, int count)
    {
        if (tableOff < 0) return "(table not located)";
        var ids = new List<string>();
        for (int id = 0; id < count; id++)
        {
            int o = tableOff + id * SceneTableLocator.MmEntrySize;
            if (o + 8 > flat.Data.Length) break;
            if (U32(flat.Data, o) == 0 && U32(flat.Data, o + 4) == 0) ids.Add($"0x{id:X2}");
        }
        return ids.Count == 0 ? "(none)" : string.Join(",", ids);
    }

    private static void ReportTail(FlatRom flat, string tag)
    {
        uint endOfFiles = RomBuilder.EndOfFiles(flat);
        int spare = RomBuilder.SpareDmaSlots(flat);
        Console.WriteLine($"  [{tag}] end-of-files (free tail start) = 0x{endOfFiles:X8}; image size = 0x{flat.Data.Length:X8}; spare dmadata slots = {spare}");
    }

    private static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
}
