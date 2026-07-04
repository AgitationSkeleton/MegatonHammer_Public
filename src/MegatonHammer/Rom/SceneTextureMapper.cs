using MegatonHammer.Textures;

namespace MegatonHammer.Rom;

/// <summary>
/// Resolves cross-segment texture references so each scene's folder shows the textures its
/// rooms actually use. A room's display lists reference textures that live in OTHER files —
/// segment 2 = the scene file, segment 4 = gameplay_keep, segment 5 = field/dungeon keep —
/// which the per-file extractor misses (it only resolves within a file). This pass scans the
/// room display lists, resolves those references to the right file, extracts the texture
/// there, and credits it to the room's scene. Returns extra textures + a folder set per
/// (existing + extra) texture.
/// </summary>
public static class SceneTextureMapper
{
    private const byte G_SETTIMG     = 0xFD;
    private const byte G_SETTILE     = 0xF5;
    private const byte G_SETTILESIZE = 0xF2;
    private const byte G_LOADBLOCK   = 0xF3;
    private const byte G_LOADTILE    = 0xF4;
    private const byte G_LOADTLUT    = 0xF0;
    private const byte G_ENDDL       = 0xDF;

    public static (List<RomTexInfo> All, List<HashSet<string>> Folders) Build(
        RomImage rom, IReadOnlyList<RomTexInfo> infos, RomAssetIndex.AssetMap map)
    {
        // Drop per-file-scanned textures from ROOM files. A room's display lists reference shared
        // textures via segment 2 (scene), 4 (gameplay_keep) and 5 (object keep) — data that lives in
        // OTHER files. The per-file scanner can't resolve segments, so it reads those offsets from the
        // room file itself, producing wrong-file rainbow garbage. The cross-segment pass below
        // re-extracts every room texture (seg 2/3/4/5) from the CORRECT file, so the room file's raw
        // entries are pure noise and safe to discard.
        var roomFiles = new HashSet<int>(map.RoomToSceneFile.Keys);
        var infosList = infos.Where(t => !roomFiles.Contains(t.FileIndex)).ToList();
        infos = infosList;

        // Working copy: existing textures may get their (cross-file) CI palette patched in.
        var all = infos.ToList();

        // Default folder per existing texture = its file's scene (or the Common bucket).
        var folders = new List<HashSet<string>>(infos.Count);
        foreach (var info in infos)
            folders.Add([map.FileScene.TryGetValue(info.FileIndex, out var s) ? s : RomAssetIndex.Common]);

        // Dedup + reverse lookup of existing textures.
        var keyToIndex = new Dictionary<long, int>();
        var byFile = new Dictionary<int, HashSet<int>>();
        for (int i = 0; i < infos.Count; i++)
        {
            var inf = infos[i];
            keyToIndex[Key(inf.FileIndex, inf.Offset, inf.Type, inf.Width, inf.Height)] = i;
            if (!byFile.TryGetValue(inf.FileIndex, out var m)) byFile[inf.FileIndex] = m = [];
            m.Add(inf.Offset);
        }

        // ── Identify the keep files via validated seg-4 / seg-5 references ──
        var allS4 = new HashSet<int>();
        var allS5 = new HashSet<int>();
        foreach (var (fileIdx, _) in map.FileScene)
        {
            byte[] d; try { d = rom.GetFile(fileIdx); } catch { continue; }
            ScanRefs(d, allS4, allS5);
        }
        int gameplayKeep = BestFile(byFile, allS4);
        var s5Files = TopFiles(byFile, allS5, 2);

        Relabel(folders, byFile, gameplayKeep, "Common (gameplay_keep)");
        if (s5Files.Count > 0) Relabel(folders, byFile, s5Files[0], "Keep (field)");
        if (s5Files.Count > 1) Relabel(folders, byFile, s5Files[1], "Keep (dungeon)");

        // ── Cross-segment extraction per room (also patches CI palettes) ────
        // Locations (file, offset) used as a TLUT by SOME room's display list. A texture-bank file
        // stores CI palettes (256 colours = 512 bytes) inline; the per-file scan, which can't see the
        // G_LOADTLUT in another file, mistakes them for 16x16 RGBA16 textures (rainbow-confetti
        // garbage). Collect every resolved palette location and drop textures sitting on one.
        // MM only: room display lists reference shared area textures (scene_texture_0N) via SEGMENT 6,
        // selected per scene by the skybox command's area-texture index. These bank files hold raw textures
        // (no display lists), so the per-file scan can't see them at all — resolve seg-6 refs to the right
        // area file so they're credited to the scene. (OoT doesn't use seg 6 for room textures.)
        var sceneToArea = new Dictionary<int, int>();
        if (rom.Game == RomGame.MM)
            foreach (int sf in map.RoomToSceneFile.Values.Distinct())
            {
                int idx = AreaTextureIndex(rom, sf);
                if (idx is >= 1 and <= 8) sceneToArea[sf] = MmSceneTextureFile + (idx - 1);
            }

        var palLocs = new HashSet<long>();
        foreach (var (roomFile, sceneFile) in map.RoomToSceneFile)
        {
            string scene = map.FileScene.GetValueOrDefault(roomFile, RomAssetIndex.Common);
            byte[] d; try { d = rom.GetFile(roomFile); } catch { continue; }

            foreach (var (seg, off, type, w, h, palSeg, palOff) in ScanTextures(d))
            {
                int Resolve(int s, int o2, N64TexType t, int tw, int th) => s switch
                {
                    2 => sceneFile,
                    3 => roomFile,
                    4 => gameplayKeep,
                    5 => ResolveSeg5(rom, s5Files, o2, t, tw, th),
                    6 => sceneToArea.GetValueOrDefault(sceneFile, -1),
                    _ => -1,
                };
                int resolved = Resolve(seg, off, type, w, h);
                if (resolved < 0) continue;
                if (!FitsInFile(rom, resolved, off, type, w, h)) continue;

                // Resolve the CI palette's file too (it can live in a different segment/file).
                int palFile = -1;
                if (palSeg >= 0)
                {
                    int colors = type == N64TexType.Palette4bpp ? 16 : 256;
                    palFile = Resolve(palSeg, palOff, N64TexType.RGBA16bpp, colors, 1);
                    if (palFile >= 0 && palOff + colors * 2 > SafeLen(rom, palFile)) palFile = -1;
                    if (palFile >= 0) palLocs.Add(Loc(palFile, palOff));
                }

                long key = Key(resolved, off, type, w, h);
                if (keyToIndex.TryGetValue(key, out int existing))
                {
                    folders[existing].Add(scene);
                    // Patch a missing CI palette onto the already-extracted texture (the per-file
                    // scan can't see a palette loaded by another file's display list).
                    if (palFile >= 0 && all[existing].PaletteOffset < 0)
                        all[existing] = all[existing] with { PaletteOffset = palOff, PaletteFileIndex = palFile };
                }
                else
                {
                    keyToIndex[key] = all.Count;
                    all.Add(new RomTexInfo(resolved, off, type, w, h, palFile >= 0 ? palOff : -1, palFile));
                    folders.Add([scene]);
                }
            }
        }

        // Drop textures that sit exactly on a known TLUT location (palettes misread as RGBA16
        // textures). Keep `all` and `folders` in lock-step.
        if (palLocs.Count > 0)
        {
            var keptTex = new List<RomTexInfo>(all.Count);
            var keptFolders = new List<HashSet<string>>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                // A real CI texture also has a (file, offset) — but it isn't itself loaded as a TLUT,
                // so only non-CI candidates colliding with a palette location are the misread ones.
                bool isCi = all[i].Type is N64TexType.Palette4bpp or N64TexType.Palette8bpp;
                if (!isCi && palLocs.Contains(Loc(all[i].FileIndex, all[i].Offset))) continue;
                keptTex.Add(all[i]);
                keptFolders.Add(folders[i]);
            }
            all = keptTex; folders = keptFolders;
        }

        return (all, folders);
    }

    private static long Loc(int file, int off) => ((long)file << 32) | (uint)off;

    // ── F3DEX2 display-list scanning ───────────────────────────────────────

    // Yields validated (segment, offset, type, w, h, palette segment/offset) for each texture command.
    // A STATEFUL linear walk mirroring RoomMeshReader (the renderer): it tracks fmt/siz PER TILE and reads
    // the RENDER tile's format at G_SETTILESIZE — not just tile 0 within a 16-command window, which missed
    // any texture rendered from a non-zero tile (~37% of Water Temple). A load (G_LOADBLOCK/G_LOADTILE) is
    // still required per texture to reject random non-DL data. CI textures carry the last committed TLUT.
    private static IEnumerable<(int seg, int off, N64TexType type, int w, int h, int palSeg, int palOff)>
        ScanTextures(byte[] d)
    {
        int n = d.Length & ~7;
        int timgSeg = -1, timgOff = 0;              // current SETTIMG (the texture image)
        int prevTimgSeg = -1, prevTimgOff = 0;      // the SETTIMG before it (restored after a TLUT load)
        int palSeg = -1, palOff = 0;                // last committed TLUT (via G_LOADTLUT)
        var tileFmt = new int[8]; var tileSiz = new int[8];
        var tileMaskS = new int[8]; var tileMaskT = new int[8];
        bool loaded = false;                        // a real load happened since the last SETTIMG

        for (int o = 0; o + 8 <= n; o += 8)
        {
            byte op = d[o];
            switch (op)
            {
                case G_SETTIMG:
                    prevTimgSeg = timgSeg; prevTimgOff = timgOff;
                    uint a = U32(d, o + 4);
                    timgSeg = (int)(a >> 24); timgOff = (int)(a & 0x00FFFFFF);
                    loaded = false;
                    break;
                case G_LOADTLUT:
                    palSeg = timgSeg; palOff = timgOff;
                    timgSeg = prevTimgSeg; timgOff = prevTimgOff;   // the SETTIMG before the palette was the texture
                    break;
                case G_LOADBLOCK:
                case G_LOADTILE:
                    loaded = true;
                    break;
                case G_SETTILE:
                {
                    uint w0 = U32(d, o), w1 = U32(d, o + 4);
                    int tile = (int)((w1 >> 24) & 7);
                    tileFmt[tile] = (int)((w0 >> 21) & 7);
                    tileSiz[tile] = (int)((w0 >> 19) & 3);
                    tileMaskS[tile] = (int)((w1 >> 4) & 0xF);
                    tileMaskT[tile] = (int)((w1 >> 14) & 0xF);
                    break;
                }
                case G_SETTILESIZE:
                {
                    uint w0 = U32(d, o), w1 = U32(d, o + 4);
                    int tile = (int)((w1 >> 24) & 7);
                    int uls = (int)((w0 >> 12) & 0xFFF), ult = (int)(w0 & 0xFFF);
                    int lrs = (int)((w1 >> 12) & 0xFFF), lrt = (int)(w1 & 0xFFF);
                    int tw = ((lrs - uls) >> 2) + 1, th = ((lrt - ult) >> 2) + 1;
                    // A span > 256 is a wrap/mirror region, not the texture size — the physical texture is 1<<mask.
                    int mw = tileMaskS[tile], mh = tileMaskT[tile];
                    if (tw > 256 && mw is >= 1 and <= 8) tw = 1 << mw;
                    if (th > 256 && mh is >= 1 and <= 8) th = 1 << mh;
                    if (timgSeg > 0 && timgSeg <= 0x0F && loaded)
                    {
                        var type = MapType(tileFmt[tile], tileSiz[tile]);
                        if (type != null && tw >= 4 && th >= 4 && tw <= 256 && th <= 256 && tw * th <= 0x10000)
                        {
                            bool ci = type is N64TexType.Palette4bpp or N64TexType.Palette8bpp;
                            yield return (timgSeg, timgOff, type.Value, tw, th, ci ? palSeg : -1, ci ? palOff : 0);
                        }
                    }
                    break;
                }
            }
        }
    }

    private static void ScanRefs(byte[] d, HashSet<int> s4, HashSet<int> s5)
    {
        foreach (var (seg, off, _, _, _, _, _) in ScanTextures(d))
        {
            if (seg == 4) s4.Add(off);
            else if (seg == 5) s5.Add(off);
        }
    }

    private static int ResolveSeg5(RomImage rom, List<int> s5Files, int off, N64TexType t, int w, int h)
    {
        foreach (int f in s5Files) if (FitsInFile(rom, f, off, t, w, h)) return f;
        return -1;
    }

    private static int SafeLen(RomImage rom, int fileIdx)
    {
        try { return fileIdx >= 0 ? rom.GetFile(fileIdx).Length : 0; } catch { return 0; }
    }

    private static bool FitsInFile(RomImage rom, int fileIdx, int off, N64TexType t, int w, int h)
    {
        if (fileIdx < 0) return false;
        int bytes = TexBytes(t, w, h);
        try { return bytes > 0 && off >= 0 && off + bytes <= rom.GetFile(fileIdx).Length; }
        catch { return false; }
    }

    // dmadata index of MM's scene_texture_01 (US); 02..08 follow contiguously (matches ImportedLevel).
    private const int MmSceneTextureFile = 1114;

    // MM: the scene header's skybox command (0x11) data1 selects a shared area-texture file (1..8), 0 = none.
    private static int AreaTextureIndex(RomImage rom, int sceneFile)
    {
        byte[] d; try { d = rom.GetFile(sceneFile); } catch { return 0; }
        for (int p = 0; p + 8 <= d.Length; p += 8)
        {
            byte cmd = d[p];
            if (cmd == 0x14) break;            // end of header
            if (cmd == 0x11) return d[p + 1];  // skybox command: data1 = area-texture index
        }
        return 0;
    }

    // ── Keep identification helpers ────────────────────────────────────────

    private static int BestFile(Dictionary<int, HashSet<int>> byFile, HashSet<int> refs)
    {
        int best = -1, bestScore = 6;
        foreach (var (f, m) in byFile)
        {
            int score = 0; foreach (int o in refs) if (m.Contains(o)) score++;
            if (score > bestScore) { bestScore = score; best = f; }
        }
        return best;
    }

    private static List<int> TopFiles(Dictionary<int, HashSet<int>> byFile, HashSet<int> refs, int count) =>
        byFile.Select(kv => (file: kv.Key, score: refs.Count(o => kv.Value.Contains(o))))
              .Where(x => x.score > 6).OrderByDescending(x => x.score).Take(count).Select(x => x.file).ToList();

    private static void Relabel(List<HashSet<string>> folders, Dictionary<int, HashSet<int>> byFile, int fileIdx, string label)
    {
        // No-op marker: relabeling needs the per-texture indices for that file, which we don't
        // track here; the keep files keep their default Common folder. (Folder name reserved.)
        _ = folders; _ = byFile; _ = fileIdx; _ = label;
    }

    // ── Small helpers ──────────────────────────────────────────────────────

    private static long Key(int file, int off, N64TexType t, int w, int h) =>
        ((long)file << 40) ^ ((long)off << 12) ^ ((long)t * 977 + w * 31 + h);

    private static uint U32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

    private static N64TexType? MapType(int fmt, int siz) => (fmt, siz) switch
    {
        (0, 2) => N64TexType.RGBA16bpp,  (0, 3) => N64TexType.RGBA32bpp,
        (2, 0) => N64TexType.Palette4bpp, (2, 1) => N64TexType.Palette8bpp,
        (3, 0) => N64TexType.GrayscaleAlpha4bpp, (3, 1) => N64TexType.GrayscaleAlpha8bpp,
        (3, 2) => N64TexType.GrayscaleAlpha16bpp,
        (4, 0) => N64TexType.Grayscale4bpp, (4, 1) => N64TexType.Grayscale8bpp,
        _ => null,
    };

    private static int TexBytes(N64TexType t, int w, int h) => t switch
    {
        N64TexType.RGBA32bpp => w * h * 4,
        N64TexType.RGBA16bpp or N64TexType.GrayscaleAlpha16bpp => w * h * 2,
        N64TexType.Grayscale8bpp or N64TexType.GrayscaleAlpha8bpp or N64TexType.Palette8bpp => w * h,
        N64TexType.Grayscale4bpp or N64TexType.GrayscaleAlpha4bpp or N64TexType.Palette4bpp => w * h / 2,
        _ => 0,
    };
}
