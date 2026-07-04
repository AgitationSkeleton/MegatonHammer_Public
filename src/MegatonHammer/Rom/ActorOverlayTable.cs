using System.Text.RegularExpressions;

namespace MegatonHammer.Rom;

/// <summary>
/// Locates <c>gActorOverlayTable</c> in the ROM and resolves an actor id → its overlay file
/// (VROM range + VRAM load base). Some actors keep their real display-list geometry embedded in
/// their OVERLAY (not in a shared object): Bg_Ganon_Otyuka's collapsing platform, Bg_Jya_Cobra's
/// mirror, En_Kanban's sign, … The decomp extracts these under assets/xml/&lt;ver&gt;/overlays/*.xml.
/// This table gives the overlay file so <see cref="ObjectModelReader"/> can read that mesh directly
/// instead of auto-detecting the shared object (which grabs the wrong body — e.g. giant Ganondorf).
///
/// gActorOverlayTable is an array of ActorOverlay, indexed by actor id, 0x20 bytes/entry:
///   0x00 u32 vromStart   0x04 u32 vromEnd    (dmadata file range; 0/0 for internally-linked slots)
///   0x08 u32 vramStart   0x0C u32 vramEnd     (0x80xxxxxx load range)
///   0x10 u32 loadedRamAddr(0 on disk)  0x14 u32 initInfo(vram)  0x18 u32 name(vram/0)
///   0x1C u16 allocType; s8 numLoaded; s8 pad
/// The 0x80xxxxxx vramStart + loadedRamAddr==0 + vromStart∈dmadata is a strong, decoy-resistant sig.
/// </summary>
public sealed partial class ActorOverlayTable
{
    public readonly record struct Entry(uint VromStart, uint VromEnd, uint VramStart, uint VramEnd, uint InitInfo);

    private readonly Dictionary<int, Entry> _idToEntry = [];
    private readonly Dictionary<string, int> _nameToId = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _idToEntry.Count;

    /// <summary>Overlay file entry for an actor id, or null if unresolved / internally-linked.</summary>
    public Entry? For(int actorId) =>
        _idToEntry.TryGetValue(actorId, out var e) && e.VromStart < e.VromEnd ? e : null;

    /// <summary>Actor id for an overlay name (e.g. "Bg_Ganon_Otyuka"), or null.</summary>
    public int? IdOfOverlay(string overlayName) =>
        _nameToId.TryGetValue(overlayName, out int id) ? id : null;

    /// <summary>Decompressed overlay file bytes for an actor id, or null.</summary>
    public byte[]? GetOverlayBytes(RomImage rom, int actorId)
    {
        if (For(actorId) is not { } e) return null;
        foreach (var f in rom.Files)
            if (f.Exists && f.VromStart == e.VromStart)
                try { return rom.GetFile(f.Index); } catch { return null; }
        return null;
    }

    [GeneratedRegex(@"/\*\s*0x([0-9A-Fa-f]+)\s*\*/\s*DEFINE_ACTOR\w*\(\s*(\w+)")]
    private static partial Regex ActorLineRegex();

    public static ActorOverlayTable Build(RomImage rom)
    {
        var t = new ActorOverlayTable();
        t.LoadNames(rom.Game == RomGame.MM);
        t.Locate(rom);
        return t;
    }

    private void LoadNames(bool mm)
    {
        string root = mm ? "mm-main" : "oot-master";
        string path = $@"D:\Copilot_OOT\READ_ONLY_SourceCodes\{root}\include\tables\actor_table.h";
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            var m = ActorLineRegex().Match(line);
            if (m.Success) _nameToId[m.Groups[2].Value] = Convert.ToInt32(m.Groups[1].Value, 16);
        }
    }

    private void Locate(RomImage rom)
    {
        var starts = new HashSet<uint>();
        foreach (var f in rom.Files) if (f.Exists) starts.Add(f.VromStart);
        int maxId = _nameToId.Count > 0 ? _nameToId.Values.Max() : 0x1D7;

        // gActorOverlayTable is indexed by actor id (id 0 = Player, an internally-linked non-overlay slot),
        // so run-detection can't anchor id 0. Instead score every 4-aligned candidate BASE by how many ids
        // 0..maxId land on a valid overlay entry; the real table maximizes that (most actors have overlays).
        // A cheap pre-filter (a few early Bg_ ids must be valid) prunes almost every offset before the full count.
        bool dbg = Environment.GetEnvironmentVariable("MH_DEBUG_OVLTABLE") == "1";
        byte[]? bestFile = null; int bestBase = -1, bestScore = -1;
        foreach (var f in rom.Files)
        {
            if (!f.Exists || f.Size < 0x40000) continue;
            byte[] d; try { d = rom.GetFile(f.Index); } catch { continue; }
            int n = d.Length;
            int last = n - (maxId + 1) * 0x20;
            for (int b = 0; b <= last; b += 4)
            {
                // Pre-filter: require a handful of nearby entries to be valid overlays (cheap reject).
                int sample = 0;
                for (int id = 1; id <= 12; id++) if (IsOverlayEntry(d, b + id * 0x20, starts)) sample++;
                if (sample < 4) continue;
                int score = 0;
                for (int id = 0; id <= maxId; id++) if (IsOverlayEntry(d, b + id * 0x20, starts)) score++;
                if (score > bestScore) { bestScore = score; bestBase = b; bestFile = d; }
            }
        }
        if (dbg) Console.Error.WriteLine($"[ovltable] bestBase=0x{bestBase:X} score={bestScore} maxId=0x{maxId:X}");
        if (dbg && bestFile != null)
            foreach (int id in new[] { 0, 1, 2, 3, 4, 0x0106, 0x0107, 0x0150 })
            {
                int q = bestBase + id * 0x20;
                if (q + 0x20 > bestFile.Length) continue;
                Console.Error.WriteLine($"[ovltable] id 0x{id:X3} @0x{q:X}: vrom=0x{U32(bestFile, q):X8}..0x{U32(bestFile, q + 4):X8} vram=0x{U32(bestFile, q + 8):X8} loaded=0x{U32(bestFile, q + 0x10):X8} init=0x{U32(bestFile, q + 0x14):X8} inStarts={starts.Contains(U32(bestFile, q))}");
            }
        if (bestFile == null || bestScore < 100) return;

        for (int id = 0; id <= maxId; id++)
        {
            int q = bestBase + id * 0x20;
            if (q + 0x20 > bestFile.Length) break;
            uint vs = U32(bestFile, q), ve = U32(bestFile, q + 4);
            uint rs = U32(bestFile, q + 8), re = U32(bestFile, q + 0x0C), init = U32(bestFile, q + 0x14);
            if (vs < ve && starts.Contains(vs) && IsVram(rs))
                _idToEntry[id] = new Entry(vs, ve, rs, re, init);
        }
    }

    // A real, loaded overlay entry: vromStart∈dmadata, vromStart<vromEnd, vramStart in overlay RAM,
    // loadedRamAddr(0x10)==0, initInfo(0x14) a vram pointer. (Internally-linked slots like Player have
    // vrom 0/0 — handled as zero slots, not counted as file entries.)
    private static bool IsOverlayEntry(byte[] d, int o, HashSet<uint> starts)
    {
        if (o + 0x20 > d.Length) return false;
        uint vs = U32(d, o), ve = U32(d, o + 4), rs = U32(d, o + 8);
        uint loaded = U32(d, o + 0x10), init = U32(d, o + 0x14);
        return vs < ve && ve - vs <= 0x40000 && starts.Contains(vs)
               && IsVram(rs) && loaded == 0 && IsVram(init);
    }

    // A null/internally-linked slot: vrom 0/0 and loadedRamAddr 0 (vram may be 0 or set for Player).
    private static bool IsZeroSlot(byte[] d, int o)
    {
        if (o + 0x20 > d.Length) return false;
        return U32(d, o) == 0 && U32(d, o + 4) == 0 && U32(d, o + 0x10) == 0;
    }

    // Actor overlays carry a HIGH link-time VRAM base (0x80A00000+; they're relocated to a real
    // heap address when loaded), so accept the whole 0x80xxxxxx range up to the sentinel top.
    private static bool IsVram(uint a) => a is >= 0x80000000 and < 0x81000000;

    private static uint U32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
}
