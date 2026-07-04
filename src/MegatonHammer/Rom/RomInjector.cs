namespace MegatonHammer.Rom;

public sealed class InjectResult
{
    public byte[] Rom = [];
    public uint   SceneVrom;
    public uint[] RoomVroms = [];
    public int    SceneTableOffset = -1;
    public int    SceneId;
    public bool   Repointed;
    public string Message = "";
}

/// <summary>
/// Injects an exported scene + rooms into a base OoT ROM, producing a decompressed ROM
/// that warps to the chosen scene id. The scene's room list is patched with the rooms'
/// VROM addresses, new dmadata entries are registered, the scene-table slot is repointed,
/// and the ROM checksum is fixed.
/// </summary>
public static class RomInjector
{
    public static InjectResult Inject(RomImage baseRom, byte[] sceneBytes,
                                      IReadOnlyList<byte[]> roomBytes, int targetSceneId,
                                      string? areaName = null, bool waterScroll = false,
                                      byte[]? crossGameSeq = null, int crossGameSrcSeqId = -1)
    {
        var result = new InjectResult { SceneId = targetSceneId };
        var flat = RomBuilder.Decompress(baseRom);

        // Need one dmadata slot per appended file (scene + rooms + optional title card).
        int needed = 1 + roomBytes.Count + (string.IsNullOrWhiteSpace(areaName) ? 0 : 1);
        if (RomBuilder.SpareDmaSlots(flat) < needed)
            throw new InvalidOperationException(
                $"Not enough spare dmadata slots ({RomBuilder.SpareDmaSlots(flat)}) for {needed} new files.");

        // 1. Append room files first so their VROM addresses are known.
        var roomVroms = new uint[roomBytes.Count];
        var roomEnds  = new uint[roomBytes.Count];
        for (int i = 0; i < roomBytes.Count; i++)
        {
            roomVroms[i] = RomBuilder.AppendFile(flat, roomBytes[i]);
            roomEnds[i]  = roomVroms[i] + (uint)roomBytes[i].Length;
        }

        // 2. Patch the scene's room-list (header command 0x04) with those addresses.
        var scene = (byte[])sceneBytes.Clone();
        PatchRoomList(scene, roomVroms, roomEnds);
        InjectCrossGameMusic(flat, baseRom, scene, crossGameSeq, crossGameSrcSeqId, ref result);

        // 3. Append the scene file.
        uint sceneVrom = RomBuilder.AppendFile(flat, scene);
        uint sceneEnd  = sceneVrom + (uint)scene.Length;

        result.SceneVrom = sceneVrom;
        result.RoomVroms = roomVroms;

        // 4. Repoint the scene-table slot (OoT only; located by fingerprint).
        var loc = SceneTableLocator.Find(flat.Data, flat.Files);
        result.SceneTableOffset = loc.Offset;
        if (loc.Offset >= 0 && targetSceneId >= 0)
        {
            int o = loc.Offset + targetSceneId * SceneTableLocator.EntrySize;
            if (o + SceneTableLocator.EntrySize <= flat.Data.Length)
            {
                W32(flat.Data, o,      sceneVrom);     // sceneFile.vromStart
                W32(flat.Data, o + 4,  sceneEnd);      // sceneFile.vromEnd

                // Area name → a 144x24 IA8 title card appended as a file, referenced as titleFile.
                if (!string.IsNullOrWhiteSpace(areaName) && RomBuilder.SpareDmaSlots(flat) >= 1)
                {
                    using var bmp = NameTextureGenerator.Render(areaName);
                    byte[] card = NameTextureGenerator.ToIA8(bmp);
                    uint cardVrom = RomBuilder.AppendFile(flat, card);
                    W32(flat.Data, o + 8,  cardVrom);                       // titleFile.vromStart
                    W32(flat.Data, o + 12, cardVrom + (uint)card.Length);  // titleFile.vromEnd
                }
                else
                {
                    W32(flat.Data, o + 8,  0);         // titleFile.vromStart (none)
                    W32(flat.Data, o + 12, 0);         // titleFile.vromEnd
                }
                flat.Data[o + 16] = 0;                 // unk_10
                // drawConfig: SDC_CALM_WATER (0x2F) when the scene has scrolling water — a pure two-layer
                // scroll of segment 0x08 (which the water DL's xluPtr references); else default (0).
                flat.Data[o + 17] = (byte)(waterScroll ? 0x2F : 0x00);
                flat.Data[o + 18] = 0;                 // unk_12
                flat.Data[o + 19] = 0;                 // unk_13
                result.Repointed = true;
            }
        }

        // 5. Pad to a power-of-2 size (N64 ROMs are; PJ64 and many cores reject/мiscount an unpadded
        //    ROM — an injected decompressed OoT lands at ~52 MB, so round up to 64 MB). Trailing zeros
        //    are past the dmadata and don't affect the checksum (computed over 0x1000..0x101000).
        flat.Data = PadToPow2(flat.Data);

        // 6. Fix the ROM checksum.
        OotCrc.Update(flat.Data);

        result.Rom = flat.Data;
        result.Message = result.Repointed
            ? $"Injected scene at VROM 0x{sceneVrom:X8} into scene slot 0x{targetSceneId:X2}."
            : $"Appended scene at VROM 0x{sceneVrom:X8}; scene table not located (repoint skipped).";
        return result;
    }

    /// <summary>
    /// Injects a scene into the OoT gc-eu-mq-dbg DEBUG ROM. Unlike <see cref="Inject"/> (which appends
    /// files + dmadata entries — fatal on the debug build, whose DmaMgr_Init walks a fixed-size
    /// filename array in lockstep with the dma table), this writes the scene + rooms into the ROM's
    /// free padding and repoints ONLY the scene-table slot + room-list, adding NO dmadata entries. The
    /// debug ROM is uncompressed, so the game DMAs these out-of-table VROM regions via its
    /// arbitrary-DMA path. The scene-table slot's title/draw-config fields are left as the original
    /// scene's, so the area keeps a valid (if mislabelled) title card. Use for the MQ debug ROM only.
    /// </summary>
    public static InjectResult InjectDebug(RomImage baseRom, byte[] sceneBytes,
                                           IReadOnlyList<byte[]> roomBytes, int targetSceneId, bool mm = false,
                                           IReadOnlyList<(int textId, byte[] body)>? messages = null,
                                           bool waterScroll = false,
                                           byte[]? crossGameSeq = null, int crossGameSrcSeqId = -1)
    {
        var result = new InjectResult { SceneId = targetSceneId };
        var flat = RomBuilder.Decompress(baseRom);

        uint cursor = RomBuilder.EndOfFiles(flat);

        // 1. Write room files into free space (no dmadata entry); record their VROM ranges.
        var roomVroms = new uint[roomBytes.Count];
        var roomEnds  = new uint[roomBytes.Count];
        for (int i = 0; i < roomBytes.Count; i++)
        {
            roomVroms[i] = RomBuilder.WriteFileAt(flat, roomBytes[i], ref cursor);
            roomEnds[i]  = roomVroms[i] + (uint)roomBytes[i].Length;
        }

        // 2. Patch the scene's room-list with those addresses, then write the scene file.
        var scene = (byte[])sceneBytes.Clone();
        PatchRoomList(scene, roomVroms, roomEnds);
        InjectCrossGameMusic(flat, baseRom, scene, crossGameSeq, crossGameSrcSeqId, ref result);
        uint sceneVrom = RomBuilder.WriteFileAt(flat, scene, ref cursor);
        uint sceneEnd  = sceneVrom + (uint)scene.Length;
        result.SceneVrom = sceneVrom;
        result.RoomVroms = roomVroms;

        // 3. Repoint the scene-table slot's sceneFile range only (keep title/drawConfig fields). The
        // sceneFile RomFile is the first 8 bytes of the entry in both games; only the entry stride and
        // locator differ (MM: 0x10-byte entries via FindMM; OoT: 0x14 via Find).
        int entrySize = mm ? SceneTableLocator.MmEntrySize : SceneTableLocator.EntrySize;
        var loc = mm ? SceneTableLocator.FindMM(flat.Data, flat.Files, baseRom)
                     : SceneTableLocator.Find(flat.Data, flat.Files);
        result.SceneTableOffset = loc.Offset;
        if (loc.Offset >= 0 && targetSceneId >= 0)
        {
            int o = loc.Offset + targetSceneId * entrySize;
            if (o + entrySize <= flat.Data.Length)
            {
                W32(flat.Data, o,     sceneVrom);
                W32(flat.Data, o + 4, sceneEnd);
                // OoT water scroll: set drawConfig = SDC_CALM_WATER (0x2F) so segment 0x08 scrolls under the
                // water DL. OoT entry drawConfig is at offset 0x11. (MM stays static here — see SceneExporter.)
                if (!mm && waterScroll) flat.Data[o + 0x11] = 0x2F;
                result.Repointed = true;
            }
        }

        // Authored dialogue: overwrite the referenced textIds' message text in place (OoT only; the MM
        // build's message system differs and uses the SoH/2Ship runtime path instead).
        if (!mm && messages is { Count: > 0 })
        {
            int n = AppendMessages(flat.Data, baseRom, messages, out var mlog);
            result.Message += $" Dialogue: {n}/{messages.Count} overwritten ({mlog}).";
        }

        flat.Data = PadToPow2(flat.Data);
        OotCrc.Update(flat.Data);
        result.Rom = flat.Data;
        result.Message = (result.Repointed
            ? $"Injected scene into debug ROM at free VROM 0x{sceneVrom:X8}, slot 0x{targetSceneId:X2} (no dmadata entries)."
            : $"Wrote scene at free VROM 0x{sceneVrom:X8}; scene table not located (repoint skipped).") + result.Message;
        return result;
    }

    // Cross-game music: overwrite a host sequence slot in the target's audioseq (dmadata file 4, already
    // decompressed at its VROM in flat.Data) with a sequence extracted from the OTHER game, then point this
    // scene's 0x15 (SetSoundSettings) music id at that host slot. No-op when no source seq is supplied.
    private static void InjectCrossGameMusic(FlatRom flat, RomImage baseRom, byte[] scene,
                                             byte[]? seq, int srcSeqId, ref InjectResult result)
    {
        if (seq == null || seq.Length == 0) return;
        if (baseRom.Files.Count <= 4) return;
        var f4 = baseRom.Files[4];                                   // audioseq (dmadata index 4, both games)
        int host = CrossGameMusic.InjectInPlace(flat.Data, baseRom.Game, (int)f4.VromStart, f4.Size, seq, srcSeqId);
        if (host >= 0 && PatchSceneMusic(scene, (byte)host))
            result.Message += $" Cross-game music: src seq 0x{srcSeqId:X2} -> host slot 0x{host:X2}.";
        else
            result.Message += " Cross-game music: injection failed (no host slot / no 0x15 command).";
    }

    // Points the scene's SetSoundSettings (header command 0x15) at sequence <paramref name="seqId"/>. The
    // command is [15 reverb 00 00 | 00 nightSfx 00 seqId] — seqId is the last byte. Returns false if absent.
    private static bool PatchSceneMusic(byte[] scene, byte seqId)
    {
        int limit = Math.Min(scene.Length, 0x200);
        for (int o = 0; o + 8 <= limit; o += 8)
        {
            byte cmd = scene[o];
            if (cmd == 0x14) break;                 // end-of-header
            if (cmd == 0x15) { scene[o + 7] = seqId; return true; }
        }
        return false;
    }

    // Finds the 0x04 (room list) command in the scene header and writes the rooms'
    // {vromStart, vromEnd} pairs into the list it points at.
    public static void PatchRoomList(byte[] scene, uint[] roomVroms, uint[] roomEnds)
    {
        int limit = Math.Min(scene.Length, 0x200);
        for (int o = 0; o + 8 <= limit; o += 8)
        {
            byte op = scene[o];
            if (op == 0x14) break;                 // end of header
            if (op != 0x04) continue;

            int listOff = (int)(U32(scene, o + 4) & 0x00FFFFFF);
            int count = Math.Min(scene[o + 1], roomVroms.Length);
            for (int i = 0; i < count; i++)
            {
                int e = listOff + i * 8;
                if (e + 8 > scene.Length) break;
                W32(scene, e,     roomVroms[i]);
                W32(scene, e + 4, roomEnds[i]);
            }
            return;
        }
    }

    /// <summary>
    /// Overwrites authored dialogue into an OoT ROM <b>in place</b>: for each (textId, encoded body), find
    /// the message in <c>sNesMessageEntryTable</c> and rewrite its text within the slot the table already
    /// allocates for it (length = next entry's offset − this offset). This needs NO table growth, code
    /// relocation, or dmadata changes — only message-data bytes — so it can't corrupt the message system,
    /// and it's verifiable by re-decoding. Intended for reusing existing textIds (e.g. a sign placed with
    /// params giving textId 0x300+ overrides that vanilla sign's text). Bodies longer than the slot are
    /// skipped (reported). Returns the number applied. The ROM's CRC must be fixed by the caller after.
    /// </summary>
    public static int AppendMessages(byte[] data, RomImage baseRom,
                                     IReadOnlyList<(int textId, byte[] body)> msgs, out string log)
    {
        log = "";
        if (msgs == null || msgs.Count == 0) return 0;
        var loc = MessageTableLocator.Find(data);
        if (!loc.Found) { log = "message table not located"; return 0; }

        int dataStart = FindMessageData(data, baseRom, loc);
        if (dataStart < 0) { log = "message-data file not located"; return 0; }

        // textId → (this offset, allocated length) from the table (offsets are ascending).
        var slot = new Dictionary<int, (uint off, uint len)>();
        var e = loc.Entries;
        for (int i = 0; i + 1 < e.Count; i++)
            if (!slot.ContainsKey(e[i].TextId))
                slot[e[i].TextId] = (e[i].Offset, e[i + 1].Offset - e[i].Offset);

        int applied = 0; var sb = new System.Text.StringBuilder();
        foreach (var (textId, body) in msgs)
        {
            if (!slot.TryGetValue(textId, out var s)) { sb.Append($" 0x{textId:X4}=no-such-id"); continue; }
            if (body.Length > s.len) { sb.Append($" 0x{textId:X4}=too-long({body.Length}>{s.len})"); continue; }
            int dst = dataStart + (int)s.off;
            if (dst + body.Length > data.Length) { sb.Append($" 0x{textId:X4}=oob"); continue; }
            Array.Copy(body, 0, data, dst, body.Length);   // body carries its own 0x02 END; trailing bytes ignored
            applied++; sb.Append($" 0x{textId:X4}=ok({body.Length}/{s.len})");
        }
        log = $"table@0x{loc.Offset:X} bank0x{loc.Bank:X2} data@0x{dataStart:X};" + sb;
        return applied;
    }

    // Finds the message-data file's ROM start: the dmadata file whose size ≈ the table's offset span and
    // whose bytes at the table's offsets read as (mostly-ASCII) message text.
    private static int FindMessageData(byte[] data, RomImage baseRom, MessageTableLocator.Result loc)
    {
        var e = loc.Entries;
        uint span = e[^1].Offset;
        foreach (var f in baseRom.Files)
        {
            if (!f.Exists) continue;
            int sz = f.Size;
            if (sz < (int)span || sz > (int)span + 0x4000) continue;
            int v = (int)f.VromStart;
            bool ok = true;
            for (int k = 1; k <= 3 && ok; k++)
            {
                int o = v + (int)e[k].Offset;
                int printable = 0;
                for (int b = 0; b < 16 && o + b < data.Length; b++)
                    if (data[o + b] >= 0x20 && data[o + b] < 0x7F) printable++;
                if (printable < 6) ok = false;
            }
            if (ok) return v;
        }
        return -1;
    }

    private static uint U32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    private static void W32(byte[] d, int o, uint v)
    {
        d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
    }

    // Rounds the ROM up to the next power-of-2 size (min 32 MB), padding with 0xFF as real cartridges
    // do. An injected decompressed OoT is ~52 MB → 64 MB.
    private static byte[] PadToPow2(byte[] data)
    {
        int target = 0x2000000;                          // 32 MB floor
        while (target < data.Length) target <<= 1;       // → 64 MB for a ~52 MB ROM
        if (target == data.Length) return data;
        var padded = new byte[target];
        Array.Copy(data, padded, data.Length);
        for (int i = data.Length; i < target; i++) padded[i] = 0xFF;
        return padded;
    }
}
