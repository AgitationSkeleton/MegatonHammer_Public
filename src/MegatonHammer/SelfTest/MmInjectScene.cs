using OpenTK.Mathematics;
using MegatonHammer.Editor;
using MegatonHammer.Export;
using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>
/// End-to-end MM N64 scene injection on the <b>decompressed</b> US-retail ROM:
/// decompress -> build a minimal custom 1-room scene with the editor's real exporter ->
/// overwrite the target slot's scene+room files <i>in place</i> (Termina Field, slot 0x2D —
/// a single-room scene that cold-loads &amp; renders via the map-select) -> bake the
/// level-select enabler -> fix the CIC-6105 checksum. Load map-select entry "1" to see it.
/// Run: MegatonHammer --injectmmscene [inRom] [outRom]
/// </summary>
public static class MmInjectScene
{
    public const int SceneTableVrom = 0xC5A1E0;   // US retail (decompressed) gSceneTable
    public const int TargetSceneId  = 0x2D;       // Termina Field: 1 room, cold-loads & renders

    private static readonly string DefaultRetail =
        Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).z64");

    public static void Run(string[] args)
    {
        string inRom  = args.Length >= 2 ? args[1] : DefaultRetail;
        string outRom = args.Length >= 3 ? args[2]
            : System.IO.Path.Combine(Editor.AppPaths.BaseDir, @"roms\mm_test\mm_injected.z64");
        Directory.CreateDirectory(Path.GetDirectoryName(outRom)!);

        // DIAGNOSTIC: "noscene" skips ALL custom-scene injection and only applies the level-select + the
        // gameMode=NORMAL fix. Map-select entry 1 then loads the VANILLA Termina Field with gameMode forced
        // — isolates whether the gameMode=NORMAL freeze comes from our injected scene or the debug-save state.
        bool noScene = args.Any(a => string.Equals(a, "noscene", StringComparison.OrdinalIgnoreCase));
        bool append  = args.Any(a => string.Equals(a, "append", StringComparison.OrdinalIgnoreCase));

        // 1. Decompress the retail ROM to a flat (ROM==VROM) image.
        var rom = new RomImage(inRom);
        var dec = MmRomDecompressor.Decompress(rom);
        Console.WriteLine($"decompressed {rom.Game}: {dec.Length} bytes" + (noScene ? "  [NOSCENE diag]" : "") + (append ? "  [APPEND]" : ""));

      if (!noScene)
      {
        // 2. Build a minimal, distinctive 1-room scene with the real editor exporter, using REAL MM
        // ROM textures (falling back to a solid colour for any name that doesn't resolve).
        var scene = BuildScene();
        var mmTex = PlaytestPack.BuildRomTexResolver(mm: true);
        System.Drawing.Bitmap? Tex(string nm) => mmTex?.Invoke(nm) ?? SolidTex(nm);
        var (sceneBytes, rooms) = SceneExporter.BuildBinaries(scene, Tex, ActorObjectResolver.Build(mm: true), mm: true);
        Console.WriteLine($"built scene={sceneBytes.Length}b, room0={rooms[0].Length}b");

        if (append)
        {
            // Snapshot Termina slot 0x2D's entry + scene bytes to PROVE append preserves them.
            int slot2D = SceneTableVrom + TargetSceneId * 0x10;
            uint tScene = U32(dec, slot2D), tEnd = U32(dec, slot2D + 4);
            byte[] terminaBefore = new byte[(int)(tEnd - tScene)];
            Array.Copy(dec, (int)tScene, terminaBefore, 0, terminaBefore.Length);
            var loc0 = MmEntranceLocator.Locate(dec);
            Console.WriteLine($"  entrance locator: valid={loc0.Valid} redirectByte@0x{loc0.RedirectByteVrom:X} cur=0x{loc0.CurrentSceneId:X2}");

            if (!InjectSceneAppend(ref dec, rom, sceneBytes, rooms[0], out string aerr)) { Console.WriteLine("APPEND FAIL: " + aerr); return; }

            // Verify invariants (append must destroy no vanilla data).
            byte[] terminaAfter = new byte[terminaBefore.Length];
            Array.Copy(dec, (int)tScene, terminaAfter, 0, terminaAfter.Length);
            bool terminaOk = tScene == U32(dec, slot2D) && tEnd == U32(dec, slot2D + 4) && terminaBefore.AsSpan().SequenceEqual(terminaAfter);
            int slot0E = SceneTableVrom + AppendSlotId * 0x10;
            uint aScene = U32(dec, slot0E), aEnd = U32(dec, slot0E + 4);
            byte redirect = dec[loc0.RedirectByteVrom];
            // The appended scene VROM must now be covered by a dmadata entry (else MM's DMA blanks it)...
            bool dmaOk = DmaEntryCovers(dec, rom, aScene) && DmaEntryCovers(dec, rom, aScene + aEnd - aScene - 1);
            // ...AND the FIRST (game-order) covering entry must be OUR real entry (romStart == aScene), not an
            // earlier romStart=0xFFFFFFFF virtual file (the black-screen hang). This is the invariant that failed.
            uint firstRom = FirstDmaRomStart(dec, rom, aScene);
            bool firstOk = firstRom == aScene;
            Console.WriteLine($"  VERIFY: Termina 0x2D preserved={terminaOk}; slot 0x0E=0x{aScene:X8}..0x{aEnd:X8} (was {{0,0}}); entrance byte now 0x{redirect:X2} (want 0x0E); dmadata covers appended scene={dmaOk}; first-match romStart=0x{firstRom:X8} (want 0x{aScene:X8}) ok={firstOk}");
            if (!terminaOk || aScene == 0 || redirect != AppendSlotId || !dmaOk || !firstOk) { Console.WriteLine("APPEND VERIFY FAILED"); return; }
            Console.WriteLine("  APPEND invariants OK: Termina intact, slot 0x0E populated, entrance redirected, DMA-registered.");
        }
        else if (!InjectSceneInto(dec, sceneBytes, rooms[0], out string err)) { Console.WriteLine("FAIL: " + err); return; }
      }

        // Common: bake level-select + the playtest boot/menu fix + fix CRC.
        BakeLevelSelect(dec);
        if (!args.Any(a => string.Equals(a, "nomenufix", StringComparison.OrdinalIgnoreCase)))
            BakePlayInitMenuFix(dec);
        if (!args.Any(a => string.Equals(a, "noautoboot", StringComparison.OrdinalIgnoreCase)))
            BakeAutoBoot(dec);
        OotCrc.Update(dec);
        File.WriteAllBytes(outRom, dec);
        Console.WriteLine($"[injectmmscene] PASS -> {outRom}");
    }

    /// <summary>Editor entry point: decompress the configured MM ROM, inject the editor's already-built
    /// scene/room binaries into the playtest slot, apply the playtest boot/menu fix, and return the finished
    /// ROM bytes. <paramref name="autoBoot"/>=true boots straight into the level (skips logo/title/map-select);
    /// false leaves the title→map-select flow (load entry "1"). Throws on a scene/room that doesn't fit.</summary>
    public static byte[] BuildPlaytestRom(string mmRomPath, byte[] sceneBytes, byte[] roomBytes, bool autoBoot,
                                          byte musicSeq = 0x15, byte[] crossGameSeq = null, int crossGameSrcSeqId = -1,
                                          bool append = false)
    {
        MusicSeq = musicSeq;   // the scene's chosen MM music (patched into the Termina Field sound command)
        var romImg = new RomImage(mmRomPath);
        var dec = MmRomDecompressor.Decompress(romImg);

        // Cross-game music: overwrite a host seq slot in MM's audioseq with the OoT sequence and play the
        // scene through that slot (keeping the host slot's font). audioseq = dmadata file index 4.
        if (crossGameSeq != null && crossGameSeq.Length > 0)
        {
            var f4 = romImg.Files[4];
            int host = CrossGameMusic.InjectInPlace(dec, romImg.Game, (int)f4.VromStart, f4.Size, crossGameSeq, crossGameSrcSeqId);
            if (host >= 0) MusicSeq = (byte)host;   // scene 0x15 sound command → the host slot
        }

        // APPEND mode = new-scene parity (docs/n64-soh-parity-assessment.md): clone Termina's scene/room
        // structure into free space, point spare slot 0x0E at the clone, inject there, and REDIRECT Termina's
        // entrance byte 0x2D→0x0E — so entrance 0x5400 loads the appended level while Termina's real scene data
        // stays intact. OVERWRITE mode (append=false, the proven default) overwrites Termina Field slot 0x2D.
        string err;
        bool ok = append ? InjectSceneAppend(ref dec, romImg, sceneBytes, roomBytes, out err)
                         : InjectSceneInto(dec, sceneBytes, roomBytes, out err);
        if (!ok) throw new InvalidOperationException($"MM playtest injection failed ({(append ? "append" : "overwrite")}): " + err);
        BakeLevelSelect(dec);
        BakePlayInitMenuFix(dec);
        if (autoBoot) BakeAutoBoot(dec);
        OotCrc.Update(dec);
        return dec;
    }

    /// <summary>Spare/UNSET MM scene slot the append target reuses (its gSceneTable entry is {0,0} — repointing
    /// it destroys no real scene). Decomp UNSET set: 0x01-0x06,0x09,0x0E,0x0F,0x31,0x3A.</summary>
    public const int AppendSlotId = 0x0E;

    // Append parity: clone Termina Field's (slot 0x2D) scene+room binaries to free space, run the SAME proven
    // header patch on the clone, point spare slot 0x0E at it, and flip one entrance byte so 0x5400 loads it —
    // leaving Termina's real data untouched. Grows the decompressed image (uncompressed MM permits arbitrary
    // DMA to the appended VROM, reached purely via the scene-table + room-list pointers).
    private static bool InjectSceneAppend(ref byte[] dec, RomImage rom, byte[] sceneBytes, byte[] roomBytes, out string err)
    {
        err = "";
        void Log(string m) => Editor.DiagnosticLog.Step("[mm-append] " + m);   // mirrored into the playtest log

        // 1. LOCATE + VALIDATE the entrance redirect byte FIRST. Refuse to append (and never patch) if the
        //    pointer chase doesn't validate to sceneId 0x2D — safety gate for the one destructive-looking byte.
        var loc = MmEntranceLocator.Locate(dec);
        if (!loc.Valid) { err = "sSceneEntranceTable not located/validated (Termina chase != 0x2D)"; return false; }
        if (dec[loc.RedirectByteVrom] != MmEntranceLocator.TerminaSceneId) { err = "redirect byte not 0x2D at validated addr"; return false; }
        Log($"entrance table @ VROM 0x{loc.TableVrom:X}; redirect byte @ 0x{loc.RedirectByteVrom:X} = 0x{loc.CurrentSceneId:X2} (validated)");

        // 2. Source structure: Termina Field slot 0x2D's scene file + room0 (proven-good headers we clone).
        int slot2D = SceneTableVrom + TargetSceneId * 0x10;
        uint srcSceneVrom = U32(dec, slot2D), srcSceneEnd = U32(dec, slot2D + 4);
        int sceneSize = (int)(srcSceneEnd - srcSceneVrom);
        if (sceneSize is <= 0 or > 0x100000) { err = $"implausible Termina scene size 0x{sceneSize:X}"; return false; }
        var (room0Vrom, room0Size) = FirstRoom(dec, (int)srcSceneVrom);
        if (room0Vrom == 0) { err = "Termina room list not found"; return false; }
        if (roomBytes.Length > room0Size) { err = $"room too big for clone slot ({roomBytes.Length} > {room0Size} bytes)"; return false; }
        Log($"clone source: Termina scene 0x{srcSceneVrom:X}..0x{srcSceneEnd:X} ({sceneSize}B), room0 0x{room0Vrom:X} ({room0Size}B)");

        // 3. Append COPIES of Termina's scene + room0, placed ABOVE every existing dmadata VROM (incl. the
        //    romStart=0xFFFFFFFF virtual files past the image end) so our appended DMA entry is the only one that
        //    covers the destination — otherwise MM's linear DmaMgr matches a virtual entry first and hangs.
        uint dmaCeil = MaxDmaVromEnd(dec, rom);
        Log($"dmadata VROM ceiling = 0x{dmaCeil:X} (image end 0x{dec.Length:X}); appending above it");
        uint newSceneVrom = AppendCopy(ref dec, (int)srcSceneVrom, sceneSize, dmaCeil);
        uint newRoomVrom  = AppendCopy(ref dec, (int)room0Vrom, (int)room0Size);
        Log($"appended clone: scene @ VROM 0x{newSceneVrom:X}, room @ 0x{newRoomVrom:X} (image now {dec.Length} bytes)");

        // 3b. REGISTER dmadata entries for the appended files. Without this, MM's DmaMgr_FindDmaEntry returns
        //     NULL for the out-of-table VROM and the scene load reads nothing -> black scene (z_std_dma.c).
        //     MM retail reads gDmaDataTable dynamically, so writing into its spare terminator slots is safe.
        if (!RegisterDma(dec, rom, newSceneVrom, (uint)sceneSize, out err)) { err = "scene dma: " + err; return false; }
        if (!RegisterDma(dec, rom, newRoomVrom, room0Size, out err)) { err = "room dma: " + err; return false; }
        Log($"dmadata entries registered for scene + room (dmadata @ 0x{rom.DmaTableOffset:X})");

        // 4. Point spare slot 0x0E at the cloned scene (Termina slot 0x2D untouched); set draw config.
        //    Slot 0x0E is UNSET, so its non-sceneFile fields (config/restrictions/flags) are garbage — copy
        //    Termina's FULL known-good 0x10-byte entry first, then repoint only the sceneFile + drawConfig.
        int slot0E = SceneTableVrom + AppendSlotId * 0x10;
        Array.Copy(dec, slot2D, dec, slot0E, 0x10);
        W32(dec, slot0E, newSceneVrom);
        W32(dec, slot0E + 4, newSceneVrom + (uint)sceneSize);
        bool hasAnimMat = false;
        for (int p = 0; p + 8 <= sceneBytes.Length; p += 8) { if (sceneBytes[p] == 0x14) break; if (sceneBytes[p] == 0x1A) { hasAnimMat = true; break; } }
        dec[slot0E + 0xB] = (byte)(hasAnimMat ? 0x01 : 0x00);
        Log($"scene table slot 0x{AppendSlotId:X2} -> 0x{newSceneVrom:X}..0x{newSceneVrom + (uint)sceneSize:X} drawCfg={(hasAnimMat ? 1 : 0)}");

        // 5. Inject my room + headers into the CLONE — the exact same PatchAllHeaders the overwrite path uses.
        Array.Copy(roomBytes, 0, dec, (int)newRoomVrom, roomBytes.Length);
        int hdrs = PatchAllHeaders(dec, (int)newSceneVrom, sceneBytes, newRoomVrom, (uint)roomBytes.Length);

        // 6. Redirect: entrance 0x5400 -> slot 0x0E (one byte). Termina Field scene data preserved.
        dec[loc.RedirectByteVrom] = (byte)AppendSlotId;
        Log($"patched {hdrs} header(s); entrance 0x5400 sceneId 0x2D -> 0x{AppendSlotId:X2}. Termina slot 0x2D preserved.");
        return true;
    }

    // Copies <paramref name="size"/> bytes from <paramref name="srcVrom"/> to the 16-aligned free tail of the
    // (uncompressed) image, growing it. Returns the appended data's VROM (== physical offset). <paramref
    // name="minVrom"/> is a FLOOR the destination must clear: MM's VROM space extends past the physical image
    // end via "virtual" dmadata entries (romStart=0xFFFFFFFF, no ROM bytes — e.g. 0x02EDB000..0x02EE7040 in US
    // retail). Appending at the raw image end would land INSIDE such an entry, and MM's linear DmaMgr_FindDmaEntry
    // would match that romStart=0xFFFFFFFF entry first → DMA from ROM 0xFFFFFFFF → hang (black screen). Padding
    // past every existing VROM guarantees our appended entry is the only one covering the destination.
    private static uint AppendCopy(ref byte[] dec, int srcVrom, int size, uint minVrom = 0)
    {
        uint end = (uint)dec.Length;
        if (minVrom > end) end = minVrom;
        uint vrom = (end + 0xFu) & ~0xFu;
        var grown = new byte[vrom + size];
        Array.Copy(dec, grown, dec.Length);           // zero-pads the gap (dec.Length .. vrom) automatically
        Array.Copy(dec, srcVrom, grown, (int)vrom, size);
        dec = grown;
        return vrom;
    }

    // Highest VROM referenced by ANY dmadata entry (real or virtual romStart=0xFFFFFFFF), walking to the {0,0}
    // terminator. Appended files must start at or above this so their VROM collides with no existing entry.
    private static uint MaxDmaVromEnd(byte[] dec, RomImage rom)
    {
        int dmaOff = rom.DmaTableOffset;
        if (dmaOff < 0) return (uint)dec.Length;
        uint dmaEnd = 0;
        foreach (var f in rom.Files) if (f.Exists && dmaOff >= f.VromStart && dmaOff < f.VromEnd) { dmaEnd = f.VromEnd; break; }
        if (dmaEnd == 0) dmaEnd = (uint)dec.Length;
        uint max = 0;
        for (int p = dmaOff; p + 16 <= dmaEnd; p += 16)
        {
            uint vs = U32(dec, p), ve = U32(dec, p + 4);
            if (vs == 0 && ve == 0) break;             // terminator
            if (ve > max) max = ve;
        }
        return max;
    }

    // Registers an UNCOMPROMISED-file dmadata entry (romEnd=0) for an appended region, written into the first
    // free (vromEnd==0) terminator slot of the dmadata table. MM's DmaMgr requires every DMA'd VROM to live in
    // a dmadata entry (DmaMgr_FindDmaEntry NULL -> hang/blank); MM retail reads the table dynamically so this
    // is safe (unlike the OoT gc-eu-mq-dbg debug ROM whose DmaMgr walks a fixed filename array).
    private static bool RegisterDma(byte[] dec, RomImage rom, uint vrom, uint size, out string err)
    {
        err = "";
        int dmaOff = rom.DmaTableOffset;
        if (dmaOff < 0) { err = "no dmadata table"; return false; }
        uint dmaEnd = 0;
        foreach (var f in rom.Files) if (f.Exists && dmaOff >= f.VromStart && dmaOff < f.VromEnd) { dmaEnd = f.VromEnd; break; }
        if (dmaEnd == 0) { err = "dmadata file bounds not found"; return false; }
        int p = dmaOff;
        while (p + 16 <= dmaEnd && U32(dec, p + 4) != 0) p += 16;   // first free slot (vromEnd == 0)
        if (p + 32 > dmaEnd) { err = "no spare dmadata slot (need entry + terminator)"; return false; }
        W32(dec, p, vrom); W32(dec, p + 4, vrom + size); W32(dec, p + 8, vrom); W32(dec, p + 12, 0);
        // Write a {0,0,0,0} TERMINATOR in the next slot. MM's DmaMgr_FindDmaEntry walks `while (vromEnd != 0)`,
        // and MM's dmadata padding is NOT clean zeros — without this the walk runs past our entry into garbage
        // "entries" (the runtime probe saw ~1554 vs the real ~1550), which can mis-resolve a later DMA.
        W32(dec, p + 16, 0); W32(dec, p + 20, 0); W32(dec, p + 24, 0); W32(dec, p + 28, 0);
        return true;
    }

    // Mirrors the game's DmaMgr_FindDmaEntry: is <paramref name="vrom"/> covered by any dmadata entry?
    private static bool DmaEntryCovers(byte[] dec, RomImage rom, uint vrom)
    {
        int dmaOff = rom.DmaTableOffset;
        uint dmaEnd = 0;
        foreach (var f in rom.Files) if (f.Exists && dmaOff >= f.VromStart && dmaOff < f.VromEnd) { dmaEnd = f.VromEnd; break; }
        for (int p = dmaOff; p + 16 <= dmaEnd && U32(dec, p + 4) != 0; p += 16)
            if (vrom >= U32(dec, p) && vrom < U32(dec, p + 4)) return true;
        return false;
    }

    // The romStart of the FIRST dmadata entry (linear order = the game's DmaMgr_FindDmaEntry) covering
    // <paramref name="vrom"/>, or 0xFFFFFFFF if none. If this isn't our appended (real) entry's romStart, the
    // game DMAs the wrong file — a romStart=0xFFFFFFFF virtual entry here = the black-screen hang we fixed.
    private static uint FirstDmaRomStart(byte[] dec, RomImage rom, uint vrom)
    {
        int dmaOff = rom.DmaTableOffset;
        uint dmaEnd = 0;
        foreach (var f in rom.Files) if (f.Exists && dmaOff >= f.VromStart && dmaOff < f.VromEnd) { dmaEnd = f.VromEnd; break; }
        for (int p = dmaOff; p + 16 <= dmaEnd; p += 16)
        {
            uint vs = U32(dec, p), ve = U32(dec, p + 4);
            if (vs == 0 && ve == 0) break;
            if (vrom >= vs && vrom < ve) return U32(dec, p + 8);   // romStart
        }
        return 0xFFFFFFFF;
    }

    // Injects already-built scene/room binaries into the target slot of the decompressed image (steps 3-6):
    // overwrite room0, point every header (primary + alt) at our collision/room/spawn, drop MAT_ANIM cfg.
    private static bool InjectSceneInto(byte[] dec, byte[] sceneBytes, byte[] roomBytes, out string err)
    {
        err = "";
        uint sceneVrom = U32(dec, SceneTableVrom + TargetSceneId * 0x10);
        var (room0Vrom, room0Size) = FirstRoom(dec, (int)sceneVrom);
        if (room0Vrom == 0) { err = "target scene room list not found"; return false; }
        if (roomBytes.Length > room0Size) { err = $"room too big for slot ({roomBytes.Length} > {room0Size} bytes)"; return false; }
        Array.Copy(roomBytes, 0, dec, (int)room0Vrom, roomBytes.Length);
        PatchAllHeaders(dec, (int)sceneVrom, sceneBytes, room0Vrom, (uint)roomBytes.Length);
        // Draw config: 1 (MAT_ANIM) when the scene carries an AnimatedMaterial list (cmd 0x1A — brush-authored
        // scrolling textures) so the engine animates them; else 0 (DEFAULT) so it never derefs a missing list.
        bool hasAnimMat = false;
        for (int p = 0; p + 8 <= sceneBytes.Length; p += 8) { if (sceneBytes[p] == 0x14) break; if (sceneBytes[p] == 0x1A) { hasAnimMat = true; break; } }
        dec[SceneTableVrom + TargetSceneId * 0x10 + 0xB] = (byte)(hasAnimMat ? 0x01 : 0x00);
        return true;
    }

    /// <summary>Points every scene header (primary + alt headers via cmd 0x18) at my data: collision
    /// (relocated to free space at <c>FreeColOff</c>), room list (my room), and spawns (moved into my
    /// room). Returns the header count. Collision is shared across vanilla headers, but room/spawn are
    /// per-header, so all must be patched.</summary>
    private const int FreeColOff = 0x35000;   // past Termina Field's data (~0x1b000), within the slot
    private static int PatchAllHeaders(byte[] dec, int sceneVrom, byte[] myScene, uint roomVrom, uint roomSize)
    {
        // Extract my collision block (cmd 0x03 → header+lists, which the exporter emits last in the scene)
        // and relocate it to FreeColOff in the target scene.
        int myColOff = (int)(FindCmdData(myScene, 0, 0x03) & 0xFFFFFF);
        var col = new byte[myScene.Length - myColOff];
        Array.Copy(myScene, myColOff, col, 0, col.Length);
        uint dC = (uint)(FreeColOff - myColOff);
        // Relocate EVERY pointer in the CollisionHeader by dC. 0x28 (waterBoxList) was missing, so an
        // injected water box's pointer stayed stale → MM never found it and the water read as a non-solid
        // brush you fall through. (vtxList 0x10, polyList 0x18, surfaceTypeList 0x1C, bgCamList 0x20,
        // waterBoxList 0x28.)
        foreach (int off in new[] { 0x10, 0x18, 0x1C, 0x20, 0x28 })
        {
            uint old = U32(col, off);
            if (old != 0) W32(col, off, 0x02000000u | ((old & 0xFFFFFF) + dC));
        }
        Array.Copy(col, 0, dec, sceneVrom + FreeColOff, col.Length);

        // The user's actual spawn (position + yaw) from the exported scene's own 0x00 spawn list — its first
        // entry is a player ActorEntry {s16 id, x, y, z, rotX, rotY, rotZ, u16 params}. The vanilla Termina
        // Field spawn entries below are overwritten with this so the mapper's Player Start is obeyed (it was
        // hardcoded to (0,30,260), ignoring the level's real spawn).
        short spawnX = SpawnX, spawnY = SpawnY, spawnZ = SpawnZ, spawnYaw = unchecked((short)0x8000);
        int mySpawnOff = (int)(FindCmdData(myScene, 0, 0x00) & 0xFFFFFF);
        if (mySpawnOff > 0 && mySpawnOff + 16 <= myScene.Length)
        {
            spawnX   = (short)U16(myScene, mySpawnOff + 2);
            spawnY   = (short)U16(myScene, mySpawnOff + 4);
            spawnZ   = (short)U16(myScene, mySpawnOff + 6);
            spawnYaw = (short)U16(myScene, mySpawnOff + 0xA);   // rotY
        }

        // Gather headers: primary (offset 0) + alt headers from cmd 0x18's list.
        var headers = new List<int> { 0 };
        int altl = (int)(FindCmdData(dec, sceneVrom, 0x18) & 0xFFFFFF);
        if (altl != 0)
            for (int i = 0; i < 24; i++)
            {
                uint v = U32(dec, sceneVrom + altl + i * 4);
                if ((v >> 24) != 0x02) break;
                headers.Add((int)(v & 0xFFFFFF));
            }

        foreach (int h in headers)
        {
            for (int p = sceneVrom + h; p < sceneVrom + h + 0x140 && p + 8 <= dec.Length; p += 8)
            {
                byte c = dec[p];
                if (c == 0x14) break;
                if (c == 0x15 && MusicSeq != 0) dec[p + 7] = MusicSeq;            // scene music → chosen MM track (seqId byte)
                else if (c == 0x0E) dec[p + 1] = 0;                              // transition actors (vanilla doors) → none
                else if (c == 0x1B) dec[p + 1] = 0;                              // actor-cutscene list → none, so the vanilla
                                                                                 // entrance/"first-visit" establishing cutscene
                                                                                 // (Cutscene_HandleEntranceTriggers →
                                                                                 // CutsceneManager_FindEntranceCsId) finds nothing
                else if (c == 0x0F)                                              // env light settings → bright neutral
                {
                    int ln = dec[p + 1];
                    int lo = (int)(U32(dec, p + 4) & 0xFFFFFF);
                    for (int i = 0; i < ln; i++)
                    {
                        int e = sceneVrom + lo + i * 0x16;
                        if (e + 0x16 > dec.Length) break;
                        dec[e + 0] = dec[e + 1] = dec[e + 2] = 230;       // ambient
                        dec[e + 6] = dec[e + 7] = dec[e + 8] = 230;       // light1 colour
                        dec[e + 0xC] = dec[e + 0xD] = dec[e + 0xE] = 200; // light2 colour
                    }
                }
                else if (c == 0x03) W32(dec, p + 4, 0x02000000u | FreeColOff);   // collision → mine
                else if (c == 0x04)                                              // room list → my room
                {
                    dec[p + 1] = 1;
                    int rl = (int)(U32(dec, p + 4) & 0xFFFFFF);
                    W32(dec, sceneVrom + rl, roomVrom);
                    W32(dec, sceneVrom + rl + 4, roomVrom + roomSize);
                }
                else if (c == 0x00)                                              // spawns → into my room
                {
                    int sl = (int)(U32(dec, p + 4) & 0xFFFFFF);
                    int cnt = Math.Max((int)dec[p + 1], 1);
                    for (int i = 0; i < cnt; i++)
                    {
                        int pe = sceneVrom + sl + i * 16;
                        W16(dec, pe + 2, (ushort)spawnX); W16(dec, pe + 4, (ushort)spawnY); W16(dec, pe + 6, (ushort)spawnZ);
                        W16(dec, pe + 0xA, (ushort)spawnYaw);   // rotY (facing) → the level's spawn yaw
                    }
                }
            }
        }
        return headers.Count;
    }

    private const short SpawnX = 0, SpawnY = 30, SpawnZ = 260;
    // The MM sequence id to play, set per-playtest from the scene's chosen Music (was hardcoded to
    // Clock Town 0x15, so the user's pick — e.g. Lost Woods — was ignored). 0 = leave vanilla.
    private static byte MusicSeq = 0x15;

    // First data pointer of command <paramref name="op"/> within the header at <paramref name="hdrOff"/>.
    private static uint FindCmdData(byte[] d, int hdrOff, byte op)
    {
        for (int p = hdrOff; p < hdrOff + 0x140 && p + 8 <= d.Length; p += 8)
        {
            if (d[p] == op) return U32(d, p + 4);
            if (d[p] == 0x14) break;
        }
        return 0;
    }

    private static void W32(byte[] d, int o, uint v)
    { d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v; }
    private static void W16(byte[] d, int o, ushort v) { d[o] = (byte)(v >> 8); d[o + 1] = (byte)v; }

    private static ZScene BuildScene()
    {
        var doc = new MapDocument();
        var scene = doc.Scene;
        scene.Name = "MH Test Room";
        var st = scene.Settings;
        st.AreaName = "MH Test Room";
        st.Sky = SkyMode.None;                          // black background; geometry stands out
        st.IndoorLighting = true;
        // Neutral white lighting so textures show their true colours (vertex shade multiplies the
        // texture; a tinted light would re-colour everything).
        st.Ambient   = RgbColor.From(230, 230, 230);
        st.Light1Col = RgbColor.From(220, 220, 220);
        st.Light2Col = RgbColor.From(200, 200, 200);
        st.FogColor  = RgbColor.From(0, 0, 0);
        st.FogNear = 0x03F0; st.FogFar = 0x03FF;
        st.MusicSeq = 0;

        var room = scene.Rooms[0];
        const float H = 600f;                           // 1200-unit square room
        // Real MM ROM textures (rom_{file}_{offset}): forest-floor, Ikana stone, Clock Tower.
        const string Floor = "rom_1488_005A18";   // Lost Woods ground
        const string Wall  = "rom_1224_00B870";   // Ancient Castle of Ikana stone
        const string Pillar= "rom_1475_007F78";   // Clock Tower Interior
        AddBox(room, (-H, -20, -H), ( H,   0,  H), Floor);      // floor
        AddBox(room, (-H,   0, -H), (-H+40, 260,  H), Wall);    // west wall
        AddBox(room, ( H-40, 0, -H), ( H,   260,  H), Wall);    // east wall
        AddBox(room, (-H,   0, -H), ( H,   260, -H+40), Wall);  // north wall
        AddBox(room, (-H,   0,  H-40), ( H, 260,  H), Wall);    // south wall
        AddBox(room, (-90,  0, -90), ( 90, 420,  90), Pillar);  // central pillar

        st.SpawnPos  = new Vector3(0, 30, 260);         // stand in front of the pillar
        st.SpawnRoom = 0;
        st.SpawnYaw  = unchecked((short)0x8000);        // face -Z toward the pillar

        // A couple of MM-safe actors (their objects are auto-added to the room's object list 0x0B):
        // a treasure chest (En_Box, MM id 0x06) and a wooden sign (En_Kanban, MM id 0x8C).
        // En_Box: type[12,4]=0 big, item[5,7]=0x14, treasureFlag[0,5]=0x1F (high, unlikely pre-set so
        // it spawns CLOSED). Variable = (0<<12)|(0x14<<5)|0x1F.
        room.Actors.Add(new ZActor { Number = 0x0006, Variable = (0x14 << 5) | 0x1F, XPos = 200, YPos = 0, ZPos = 0, YRot = unchecked((short)0x8000) });
        room.Actors.Add(new ZActor { Number = 0x00A8, Variable = 0x0000, XPos = -200, YPos = 0, ZPos = 0, YRot = 0x4000 });
        return scene;
    }

    private static void AddBox(ZRoom room, (float x, float y, float z) lo, (float x, float y, float z) hi, string tex)
    {
        var s = Solid.CreateBox(new Vector3(lo.x, lo.y, lo.z), new Vector3(hi.x, hi.y, hi.z));
        foreach (var f in s.Faces) { f.TextureName = tex; f.TexScaleS = 192f; f.TexScaleT = 192f; }
        room.Geometry.Add(s);
    }

    // Solid-colour texture per name so geometry always renders (no ROM-texture dependency).
    private static System.Drawing.Bitmap SolidTex(string name)
    {
        var c = name switch
        {
            "mh_floor"  => System.Drawing.Color.FromArgb(60, 180, 75),
            "mh_wall"   => System.Drawing.Color.FromArgb(160, 160, 175),
            "mh_pillar" => System.Drawing.Color.FromArgb(220, 60, 60),
            _           => System.Drawing.Color.FromArgb(210, 210, 0),
        };
        var bmp = new System.Drawing.Bitmap(32, 32);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(c);
        return bmp;
    }

    // Sets gSaveContext.gameMode = GAMEMODE_NORMAL (0) ONCE at the very start of Play_Init when entering our
    // target level — exactly like FileChoose does before launching Play (file_choose_NES.c:2165). The
    // map-select path skips that, leaving gameMode = TITLE_SCREEN, which disables the HUD (z_play.c:1091
    // Interface_Draw) and the pause menu (z_play.c:965 KaleidoSetup_Update). Doing it at Play_Init+0 (before
    // the scene/day machinery) keeps the whole scene-load consistently in NORMAL mode — forcing it mid-flow
    // per-frame instead froze the level. Conditional on gSaveContext.save.entrance == 0x5400
    // (ENTRANCE(TERMINA_FIELD,0)) so the title/intro Play scenes are untouched.
    // entrance is Save.entrance @ Save+0x00 (s32 "scene_no") = SaveContext+0x00 = 0x801EF670 (NOT +0x10,
    // which is isNight — RespawnData is the struct with entrance@0x10). RAM-VROM delta = 0x7F569AC0.
    // MIPS encoders (R/I-type) for the boot-fix routine. Registers: t0=8,t1=9,t2=10,t3=11,t4=12,t5=13,
    // t6=14, zero=0, sp=29, s1=17. Immediates are sign-extended by lw/sw/sh/sb/addiu.
    private static uint Lui(int rt, int imm)             => 0x3C000000u | ((uint)rt << 16) | (uint)(imm & 0xFFFF);
    private static uint Ori(int rt, int rs, int imm)     => 0x34000000u | ((uint)rs << 21) | ((uint)rt << 16) | (uint)(imm & 0xFFFF);
    private static uint Addiu(int rt, int rs, int imm)   => 0x24000000u | ((uint)rs << 21) | ((uint)rt << 16) | (uint)(imm & 0xFFFF);
    private static uint Lw(int rt, int off, int b)       => 0x8C000000u | ((uint)b  << 21) | ((uint)rt << 16) | (uint)(off & 0xFFFF);
    private static uint Sw(int rt, int off, int b)       => 0xAC000000u | ((uint)b  << 21) | ((uint)rt << 16) | (uint)(off & 0xFFFF);
    private static uint Sh(int rt, int off, int b)       => 0xA4000000u | ((uint)b  << 21) | ((uint)rt << 16) | (uint)(off & 0xFFFF);
    private static uint Sb(int rt, int off, int b)       => 0xA0000000u | ((uint)b  << 21) | ((uint)rt << 16) | (uint)(off & 0xFFFF);
    private static uint Bne(int rs, int rt, int off)     => 0x14000000u | ((uint)rs << 21) | ((uint)rt << 16) | (uint)(off & 0xFFFF);
    private static uint Jmp(uint target)                 => 0x08000000u | ((target >> 2) & 0x03FFFFFFu);
    private static uint Jal(uint target)                 => 0x0C000000u | ((target >> 2) & 0x03FFFFFFu);

    // Auto-boot: detours ConsoleLogo_Main (the N64-logo gamestate's per-frame main) so that on its first
    // frame it builds the debug save, points the entrance at our level, and switches straight to Play_Init —
    // skipping the logo animation, title screen and map-select entirely. ConsoleLogo_Init (which runs first)
    // still does the critical SRAM setup (flashSaveAvailable / Sram_Alloc / fileNum), so this is safe; doing
    // it from Setup directly would skip that. The Play_Init detour (BakePlayInitMenuFix) then does the
    // FileChoose-style runtime reset + gameMode=NORMAL as usual. ConsoleLogo_Main @ vram 0x8080066C →
    // overlay vrom 0xC7AB4C (ovl entry 2: vrom 0xC7A4E0 / vram 0x80800000). Routine lives in free code space.
    private static void BakeAutoBoot(byte[] dec)
    {
        const int ConsoleLogoMainVrom = 0xC7AB4C; // ConsoleLogo_Main[0]
        const int RoutineVrom         = 0xC53188; // free zero run in code (RAM 0x801BCC48)
        const uint RoutineRam         = 0x801BCC48;
        const int T0=8, T1=9, T2=10, T3=11, ZERO=0, SP=29, RA=31, A0=4;
        uint[] r =
        {
            Addiu(SP, SP, -0x20),
            Sw(RA, 0x1C, SP),
            Sw(A0, 0x18, SP),                 // save GameState* (this)
            // playerForm = HUMAN BEFORE Sram_InitDebugSave, so its `if (form != HUMAN)` C-DOWN override
            // (D_801C6A48[form]) doesn't fire — keeps the normal debug equip layout (matches map-select path).
            Lui(T0, 0x801F), Ori(T1, ZERO, 4), Sb(T1, unchecked((short)0xF690), T0),
            Jal(0x80144968), 0x00000000,      // Sram_InitDebugSave()  (delay nop)
            Lw(A0, 0x18, SP),                 // restore this
            Lui(T0, 0x801F),
            Ori(T1, ZERO, 0x5400),
            Sw(T1, unchecked((short)0xF670), T0), // gSaveContext.save.entrance = ENTRANCE(TERMINA_FIELD,0)
            Lui(T2, 0x8016), Ori(T2, T2, unchecked((short)0xA2C8)), // Play_Init = 0x8016A2C8
            Sw(T2, 0x0C, A0),                 // this->init = Play_Init
            Lui(T3, 0x0001), Ori(T3, T3, 0x9258),                   // sizeof(PlayState) = 0x19258
            Sw(T3, 0x10, A0),                 // this->size
            Sb(ZERO, 0x9B, A0),               // this->running = 0  (STOP_GAMESTATE → switch next frame)
            Lw(RA, 0x1C, SP),
            Addiu(SP, SP, 0x20),
            Jmp(0x0) /*placeholder jr*/, 0x00000000,
        };
        // replace the placeholder with a real `jr $ra` (0x03E00008)
        r[r.Length - 2] = 0x03E00008;
        for (int i = 0; i < r.Length; i++) W32(dec, RoutineVrom + i * 4, r[i]);
        W32(dec, ConsoleLogoMainVrom, Jmp(RoutineRam));     // ConsoleLogo_Main[0] → j routine
        W32(dec, ConsoleLogoMainVrom + 4, 0x00000000);      // [1] → nop: the original delay slot (sw $s0,
                                                            // 0x18($sp)) ran with the un-decremented $sp and
                                                            // wrote into the caller's frame. Benign here (it's
                                                            // $s0, not $ra — that's why MM worked anyway) but
                                                            // the routine never falls through, so nop it.
    }

    // Detours Play_Init to replicate what FileChoose/Sram_OpenSave does that the debug map-select path skips,
    // for our target level (entrance == 0x5400). Forcing gameMode=NORMAL alone freezes because the debug-save
    // path (Sram_InitDebugSave) leaves gSaveContext's runtime contexts (past the Save struct) uninitialized;
    // only Sram_OpenSave resets them. We reset them here the same way (without the flash-IO / save-write deps
    // that make calling Sram_OpenSave directly unsafe): eventInf=0, cycleSceneFlags=0, all timer state=0,
    // magicLevel=0, isFirstCycle=0, playerForm=HUMAN, nextCutsceneIndex=0, mapIndex=0, fileNum=0; then
    // gameMode=NORMAL and re-assert entrance. gSaveContext @ 0x801EF670; base $t0 = 0x801F0000.
    private static void BakePlayInitMenuFix(byte[] dec)
    {
        const int PlayInitVrom = 0xC00808;   // Play_Init @ RAM 0x8016A2C8
        const int StubVrom     = 0xC42A50;   // free zero space in code (RAM 0x801AC510), 0x9c bytes
        const int T0=8, T1=9, T2=10, T3=11, T4=12, T5=13, T6=14, ZERO=0, SP=29, S1=17;
        var s = new System.Collections.Generic.List<uint>();
        s.Add(Addiu(SP, SP, -0xA8));      // 0: original Play_Init+0  (addiu $sp,$sp,-0xA8)
        s.Add(Sw(S1, 0x30, SP));          // 1: original Play_Init+4  (sw $s1,0x30($sp))
        s.Add(Lui(T0, 0x801F));           // 2: $t0 = 0x801F0000
        s.Add(Lw(T1, unchecked((short)0xF670), T0)); // 3: $t1 = save.entrance (s32 @ 0x801EF670)
        s.Add(Ori(T2, ZERO, 0x5400));     // 4: ENTRANCE(TERMINA_FIELD,0)
        int condIdx = s.Count; s.Add(0);  // 5: bne $t1,$t2, END  (offset patched below)
        s.Add(0);                         // 6: delay nop
        // --- entrance matched: do the full FileChoose-style setup ---
        s.Add(Sw(ZERO, 0x3318, T0));      // 7: gameMode = GAMEMODE_NORMAL (0x801F3318)
        s.Add(Sw(ZERO, 0x3310, T0));      // 8: fileNum = 0 (0x801F3310)
        s.Add(Sh(ZERO, 0x35A6, T0));      // 9: mapIndex = 0 (0x801F35A6)
        s.Add(Sh(ZERO, 0x35BA, T0));      //10: nextCutsceneIndex = 0 (0x801F35BA)
        s.Add(Sb(ZERO, unchecked((short)0xF675), T0)); //11: isFirstCycle = 0 (0x801EF675)
        s.Add(Ori(T4, ZERO, 4));          //12: PLAYER_FORM_HUMAN
        s.Add(Sb(T4, unchecked((short)0xF690), T0));   //13: playerForm = HUMAN (0x801EF690)
        s.Add(Sb(ZERO, unchecked((short)0xF6A8), T0)); //14: magicLevel = 0 (0x801EF6A8)
        // Force DAYTIME so day-only field music is audible (the debug save defaults to night). The earlier
        // attempt set save.time alone and left save.isNight inconsistent → broke the HUD; set BOTH now.
        // save @ 0x801EF670: time (u16) @ +0x0C = 0x801EF67C, isNight (s32) @ +0x10 = 0x801EF680.
        // time ~9am (0x6000 of the 0x10000/day clock); isNight (asahiru_fg) = 0 = day.
        s.Add(Ori(T4, ZERO, 0x6000));                  //    $t4 = 0x6000
        s.Add(Sh(T4, unchecked((short)0xF67C), T0));   //    save.time = 0x6000 (day)
        s.Add(Sw(ZERO, unchecked((short)0xF680), T0)); //    save.isNight = 0 (day)
        s.Add(Ori(T5, ZERO, 0x5400));     //15
        s.Add(Sw(T5, unchecked((short)0xF670), T0));   //16: re-assert entrance = 0x5400
        s.Add(Lui(T6, 0x8020));           //17: base for eventInf (0x801FF67C is out of $t0 range)
        s.Add(Sw(ZERO, unchecked((short)0xF67C), T6)); //18: eventInf[0..3] = 0 (0x801FF67C)
        s.Add(Sw(ZERO, unchecked((short)0xF680), T6)); //19: eventInf[4..7] = 0
        // --- zero timer block: SaveContext 0x3DC0..0x3F14 (RAM 0x801F3430..0x801F3584) ---
        s.Add(Addiu(T1, T0, 0x3430));     //20: start
        s.Add(Addiu(T3, T0, 0x3584));     //21: end (exclusive; stops before seqId@0x3F16)
        int loop1 = s.Count;
        s.Add(Sw(ZERO, 0, T1));           //22
        s.Add(Addiu(T1, T1, 4));          //23
        s.Add(Bne(T1, T3, (loop1 - (s.Count + 1)))); //24: branch back to loop1
        s.Add(0);                         //25: delay nop
        // --- zero cycleSceneFlags[120] (0x14 each): RAM 0x801F35D8..0x801F3F38 ---
        s.Add(Addiu(T1, T0, 0x35D8));     //26: start
        s.Add(Addiu(T3, T0, unchecked((short)0x3F38))); //27: end (0x35D8 + 0x960)
        int loop2 = s.Count;
        s.Add(Sw(ZERO, 0, T1));           //28
        s.Add(Addiu(T1, T1, 4));          //29
        s.Add(Bne(T1, T3, (loop2 - (s.Count + 1)))); //30: branch back to loop2
        s.Add(0);                         //31: delay nop
        int endIdx = s.Count;
        s.Add(Jmp(0x8016A2D0));           //32: END — back to Play_Init+8
        s.Add(0);                         //33: delay nop
        // patch the conditional branch to skip to END when entrance != 0x5400
        s[condIdx] = Bne(T1, T2, endIdx - (condIdx + 1));

        if (s.Count * 4 > 0x9c) throw new Exception($"boot-fix routine too big: {s.Count} instrs");
        for (int i = 0; i < s.Count; i++) W32(dec, StubVrom + i * 4, s[i]);
        W32(dec, PlayInitVrom,     Jmp(0x801AC510)); // j stub
        W32(dec, PlayInitVrom + 4, 0x00000000);      // nop (was sw $s1; the stub does it)
    }

    private static void BakeLevelSelect(byte[] dec)
    {
        uint delta = 0x801C3CA0u - 0xC5A1E0u;   // RAM -> VROM
        (uint ram, ushort val)[] code =
        {
            (0x801BDA04, 0x00C7), (0x801BDA06, 0xADF0), (0x801BDA08, 0x00C7), (0x801BDA0A, 0xE2D0),
            (0x801BDA0C, 0x8080), (0x801BDA0E, 0x0910), (0x801BDA10, 0x8080), (0x801BDA12, 0x3DF0),
            (0x801BDA18, 0x8080), (0x801BDA1A, 0x1B4C), (0x801BDA1C, 0x8080), (0x801BDA1E, 0x1B28),
        };
        foreach (var (ram, val) in code)
        {
            int v = (int)(ram - delta);
            dec[v] = (byte)(val >> 8); dec[v + 1] = (byte)val;
        }
    }

    private static (uint vrom, uint size) FirstRoom(byte[] data, int sceneVrom)
    {
        for (int o = sceneVrom; o < sceneVrom + 0x300 && o + 8 <= data.Length; o += 8)
        {
            if (data[o] == 0x14) break;
            if (data[o] == 0x04)
            {
                int list = sceneVrom + (int)(U32(data, o + 4) & 0xFFFFFF);
                uint rv = U32(data, list), re = U32(data, list + 4);
                return (rv, re - rv);
            }
        }
        return (0, 0);
    }

    private static uint U32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    private static ushort U16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);
}
