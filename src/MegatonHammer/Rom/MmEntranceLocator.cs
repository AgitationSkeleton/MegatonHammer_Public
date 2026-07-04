namespace MegatonHammer.Rom;

/// <summary>
/// Read-only locator for MM's <c>sSceneEntranceTable</c> in a DECOMPRESSED retail MM image, and the exact
/// byte to patch for the append-mode entrance REDIRECT (see docs/n64-soh-parity-assessment.md).
///
/// MM resolves an entrance value via
///   <c>sSceneEntranceTable[e&gt;&gt;9].table[(e&gt;&gt;4)&amp;0x1F][e&amp;0xF].sceneId</c>
/// (EntranceTableEntry is {s8 sceneId, s8 spawnNum, u16 flags}, size 4). Termina's boot entrance 0x5400
/// therefore resolves to <c>sSceneEntranceTable[0x2A].table[0][0].sceneId</c>, whose value is 0x2D. To make
/// entrance 0x5400 load an APPENDED spare slot (0x0E) instead of overwriting Termina Field's scene data, the
/// append injector flips that one sceneId byte 0x2D→0x0E. This class finds it and, crucially, VALIDATES the
/// whole pointer chase lands on 0x2D before returning — the injector refuses to write if validation fails.
/// </summary>
public static class MmEntranceLocator
{
    // MM `code` RAM->VROM (VROM = RAM - 0x7F569AC0), from two known MmInjectScene anchors (Play_Init RAM
    // 0x8016A2C8 = VROM 0xC00808; RoutineRam 0x801BCC48 = VROM 0xC53188). gSceneTable is in the same file.
    private const long MmRamToVrom = -0x7F569AC0L;
    public const int GSceneTableVrom = 0xC5A1E0;       // hardcode verified by SceneTableLocator cross-check
    public const int TerminaEntrance = 0x5400;         // ENTRANCE(TERMINA_FIELD, 0) — the boot entrance
    public const int TerminaEntranceScene = 0x2A;      // 0x5400 >> 9
    public const int TerminaSceneId = 0x2D;            // the value the chase must yield

    public readonly record struct Result(bool Valid, int TableVrom, int RedirectByteVrom, int CurrentSceneId);

    private static bool IsCodeRam(uint p) => p is >= 0x80000000 and < 0x80300000;
    private static int RamToVrom(uint ram) => (int)((long)ram + MmRamToVrom);
    private static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

    /// <summary>Chase Termina 0x5400 from a candidate table base; returns sceneId (or -1) and the sceneId
    /// byte's VROM. Every pointer hop is bounds/range-checked, so a wrong base simply returns -1.</summary>
    private static int Chase(byte[] dec, int tableVrom, out int sidByteVrom)
    {
        sidByteVrom = -1;
        int entry = tableVrom + TerminaEntranceScene * 0xC;   // sSceneEntranceTable[0x2A]
        if (entry < 0 || entry + 0xC > dec.Length) return -1;
        uint tablePtr = U32(dec, entry + 4);                  // .table (EntranceTableEntry**)
        if (!IsCodeRam(tablePtr)) return -1;
        int arr = RamToVrom(tablePtr);
        if (arr < 0 || arr + 4 > dec.Length) return -1;
        uint entryPtr = U32(dec, arr);                        // [spawn 0]
        if (!IsCodeRam(entryPtr)) return -1;
        int e = RamToVrom(entryPtr);
        if (e < 0 || e >= dec.Length) return -1;
        sidByteVrom = e;                                      // [variant 0].sceneId (+0)
        return (sbyte)dec[e];
    }

    /// <summary>Locate + validate. Scans near gSceneTable (same source file z_scene_table.c) and accepts the
    /// UNIQUE base whose Termina chase yields sceneId 0x2D. Result.Valid is false if not uniquely found.</summary>
    public static Result Locate(byte[] dec)
    {
        var hits = new List<(int table, int sidByte)>();
        for (int b = 0xC30000; b + 0x60 * 0xC <= dec.Length && b < 0xC90000; b += 4)
            if (Chase(dec, b, out int sidByte) == TerminaSceneId) hits.Add((b, sidByte));
        if (hits.Count != 1) return new Result(false, -1, -1, -1);
        var (table, sid) = hits[0];
        return new Result(true, table, sid, dec[sid]);
    }
}
