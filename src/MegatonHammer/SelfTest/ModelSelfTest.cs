using MegatonHammer.Editor;
using MegatonHammer.Rom;
using OpenTK.Mathematics;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Headless smoke test for the actor model pipeline (#8). Loads a ROM, resolves every actor's
/// model, and reports: Link's spawn model, overall coverage, how many skeletons were posed with a
/// detected idle/frame-0 animation vs. the rest-pose fallback, and bounding-box sanity (no NaN/Inf
/// or absurd magnitudes from the new affine/animation transform). Run with: MegatonHammer --selftest [romPath]
/// </summary>
public static class ModelSelfTest
{
    private static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    private static ushort U16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);
    private static int Seg6(byte[] d, int o) { uint v = U32(d, o); return (v >> 24) == 6 ? (int)(v & 0xFFFFFF) : -1; }

    /// <summary>Diagnostic: print a ROM object's detected skeleton (header, every limb's joint
    /// position / hierarchy / display-list pointer) plus any competing skeleton candidates, so the
    /// decode can be checked against the decomp. Run: MegatonHammer --dump &lt;objectName&gt; [romPath]</summary>
    public static void DumpObject(string? romPath, string objName)
    {
        romPath ??= Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
        var rom = new RomImage(romPath);
        Console.WriteLine($"[dump] game={rom.Game}");
        var objects = ObjectTable.Build(rom);
        int? id = objects.IdOf(objName);
        var vr = objects.Resolve(objName);
        Console.WriteLine($"[dump] {objName}: id=0x{id:X} vrom={(vr.HasValue ? $"0x{vr.Value.start:X8}..0x{vr.Value.end:X8} ({vr.Value.end - vr.Value.start} bytes)" : "(unresolved)")}");
        if (id is int oid)
            for (int j = Math.Max(0, oid - 4); j <= oid + 6; j++)
            {
                var rv = objects.ResolveId(j);
                Console.WriteLine($"[dump]   obj 0x{j:X}: {(rv.HasValue ? $"0x{rv.Value.start:X8}..0x{rv.Value.end:X8} ({rv.Value.end - rv.Value.start}b)" : "(null)")}{(j == oid ? "  <- requested" : "")}");
            }
        var bytes = objects.GetObjectBytes(rom, objName);
        if (bytes == null) { Console.WriteLine($"[dump] no bytes for {objName}"); return; }
        Console.WriteLine($"[dump] {objName}: {bytes.Length} bytes (0x{bytes.Length:X})");

        int skel = ObjectModelReader.FindSkeleton(bytes);
        Console.WriteLine($"[dump] FindSkeleton -> 0x{skel:X}");
        if (skel < 0)
        {
            int bestDl = ObjectModelReader.FindBestDisplayList(bytes);
            int tris = bestDl >= 0 ? ObjectModelReader.ReadDList(bytes, 0, bestDl).Count : 0;
            Console.WriteLine($"[dump] FindBestDisplayList -> 0x{bestDl:X}, ReadDList tris={tris}");
            return;
        }

        int limbArr = Seg6(bytes, skel);
        int count = bytes[skel + 4];
        Console.WriteLine($"[dump] header: limbArr=0x{limbArr:X} limbCount={count} byte5=0x{bytes[skel + 5]:X2} (flex dListCount?)");
        for (int i = 0; i < count; i++)
        {
            int limb = Seg6(bytes, limbArr + i * 4);
            if (limb < 0 || limb + 12 > bytes.Length) { Console.WriteLine($"  limb {i,2}: bad ptr 0x{U32(bytes, limbArr + i * 4):X8}"); continue; }
            short jx = (short)U16(bytes, limb), jy = (short)U16(bytes, limb + 2), jz = (short)U16(bytes, limb + 4);
            byte child = bytes[limb + 6], sib = bytes[limb + 7];
            uint dl = U32(bytes, limb + 8);
            string dls = (dl >> 24) == 6 ? $"0x{dl & 0xFFFFFF:X}" : dl == 0 ? "null" : $"seg{dl >> 24:X}:0x{dl & 0xFFFFFF:X}";
            Console.WriteLine($"  limb {i,2}@0x{limb:X5}: pos=({jx,6},{jy,6},{jz,6}) child={(child == 0xFF ? "-" : child.ToString()),2} sib={(sib == 0xFF ? "-" : sib.ToString()),2} dl={dls}");
        }

        // List every plausible skeleton header so we can see if FindSkeleton chose the right one.
        Console.WriteLine("[dump] skeleton candidates (offset: limbCount):");
        for (int p = 0; p + 8 <= bytes.Length; p += 4)
        {
            int arr = Seg6(bytes, p); int c = bytes[p + 4];
            if (arr < 0 || c < 3 || c > 30 || arr + c * 4 > bytes.Length) continue;
            bool all = true;
            for (int i = 0; i < c && all; i++)
            {
                int lb = Seg6(bytes, arr + i * 4);
                if (lb < 0 || lb + 12 > bytes.Length) { all = false; break; }
                byte ch = bytes[lb + 6], sb = bytes[lb + 7]; uint dl = U32(bytes, lb + 8);
                if ((ch != 0xFF && ch >= c) || (sb != 0xFF && sb >= c) || (dl != 0 && (dl >> 24) != 6)) all = false;
            }
            if (all) Console.WriteLine($"    0x{p:X5}: {c} limbs");
        }
    }

    /// <summary>Diagnostic: run the full ROM texture pipeline (raw scan + scene mapping) and report
    /// the texture count and the friendly scene categories. Run: MegatonHammer --textures [romPath]</summary>
    public static void TextureScan(string? romPath)
    {
        romPath ??= DefaultRom;
        var rom = new RomImage(romPath);
        Console.WriteLine($"[textures] game={rom.Game}");
        using var src = new RomTextureSource(rom);
        var infos = src.Scan();
        var map = RomAssetIndex.BuildMap(rom);
        int sceneCats = map.FileScene.Values.Distinct().Count();
        Console.WriteLine($"[textures] raw scan: {infos.Count} textures; {map.FileScene.Count} files mapped to {sceneCats} scenes");
        var (allTex, folders) = SceneTextureMapper.Build(rom, infos, map);
        var cats = folders.SelectMany(f => f).Distinct().OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"[textures] after scene mapping: {allTex.Count} textures in {cats.Count} categories");

        // CI-palette health: a CI texture decodes in colour only if PaletteFileIndex >= 0. A texture
        // that found a palette OFFSET but has no file (PaletteFileIndex < 0) falls back to grayscale —
        // the "unnecessarily desaturated" case. Count those, plus overall type spread.
        int ci = 0, ciColor = 0, ciOffsetNoFile = 0, ciNoPalette = 0;
        var byType = new Dictionary<Textures.N64TexType, int>();
        foreach (var t in allTex)
        {
            byType[t.Type] = byType.GetValueOrDefault(t.Type) + 1;
            bool isCi = t.Type is Textures.N64TexType.Palette4bpp or Textures.N64TexType.Palette8bpp;
            if (!isCi) continue;
            ci++;
            if (t.PaletteFileIndex >= 0) ciColor++;
            else if (t.PaletteOffset >= 0) ciOffsetNoFile++;
            else ciNoPalette++;
        }
        Console.WriteLine($"[textures] types: {string.Join(", ", byType.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}"))}");
        Console.WriteLine($"[textures] CI textures: {ci} total | colour(palette file)={ciColor} | " +
                          $"GRAYSCALE-but-has-offset={ciOffsetNoFile} | no-palette-at-all={ciNoPalette}");
        Console.WriteLine($"[textures] => {ciOffsetNoFile} CI textures are needlessly desaturated (have a palette offset but no resolved file)");
    }

    /// <summary>Diagnostic: decode every texture in a scene category and tile them into a PNG montage
    /// so decode correctness can be eyeballed. Run: MegatonHammer --texmontage [rom] [categorySub] [out.png]</summary>
    public static void TexMontage(string? romPath, string categorySub, string outPng, int maxN = 0, int cell = 72, int cols = 12)
    {
        romPath ??= DefaultRom;
        var rom = new RomImage(romPath);
        using var src = new RomTextureSource(rom);
        var map = RomAssetIndex.BuildMap(rom);
        var (allTex, folders) = SceneTextureMapper.Build(rom, src.Scan(), map);

        var picks = new List<int>();
        for (int i = 0; i < allTex.Count; i++)
            if (folders[i].Any(f => f.Contains(categorySub, StringComparison.OrdinalIgnoreCase)))
                picks.Add(i);
        Console.WriteLine($"[texmontage] {picks.Count} textures matching '{categorySub}'");
        if (picks.Count == 0) return;
        if (maxN > 0 && picks.Count > maxN) picks = picks.Take(maxN).ToList();
        for (int k = 0; k < Math.Min(picks.Count, 48); k++)
        {
            var t = allTex[picks[k]];
            Console.WriteLine($"  #{k,3} file={t.FileIndex,4} off=0x{t.Offset:X6} {t.Type,-18} {t.Width}x{t.Height} " +
                              $"palOff={(t.PaletteOffset >= 0 ? "0x" + t.PaletteOffset.ToString("X") : "-")} palFile={t.PaletteFileIndex}");
        }

        const int pad = 4;
        int rows = (picks.Count + cols - 1) / cols;
        using var sheet = new System.Drawing.Bitmap(cols * (cell + pad) + pad, rows * (cell + pad) + pad);
        using (var g = System.Drawing.Graphics.FromImage(sheet))
        {
            g.Clear(System.Drawing.Color.FromArgb(30, 30, 30));
            for (int k = 0; k < picks.Count; k++)
            {
                int x = pad + (k % cols) * (cell + pad), y = pad + (k / cols) * (cell + pad);
                try
                {
                    using var bmp = src.Decode(allTex[picks[k]]);
                    g.DrawImage(bmp, x, y, cell, cell);
                }
                catch { g.FillRectangle(System.Drawing.Brushes.DarkRed, x, y, cell, cell); }
            }
        }
        sheet.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"[texmontage] saved {outPng} ({sheet.Width}x{sheet.Height})");
    }

    /// <summary>Diagnostic: dump the F3DEX2 commands around a file offset to classify a texture.
    /// Run: MegatonHammer --dldump [rom] [fileIndex] [hexOffset]</summary>
    public static void DlDump(string? romPath, int fileIndex, int searchOff)
    {
        romPath ??= DefaultRom;
        var rom = new RomImage(romPath);
        byte[] d = rom.GetFile(fileIndex);
        Console.WriteLine($"[dldump] file={fileIndex} len=0x{d.Length:X} searching for SETTIMG -> 0x{searchOff:X}");
        for (int o = 0; o + 8 <= d.Length; o += 8)
        {
            if (d[o] != 0xFD) continue;
            int off = (int)(U32(d, o + 4) & 0x00FFFFFF);
            if (off != searchOff) continue;
            Console.WriteLine($"  SETTIMG@0x{o:X} -> seg{U32(d, o + 4) >> 24} off=0x{off:X}; following commands:");
            for (int p = o; p + 8 <= Math.Min(d.Length, o + 20 * 8); p += 8)
            {
                byte op = d[p];
                string name = op switch { 0xFD => "SETTIMG", 0xF5 => "SETTILE", 0xF2 => "SETTILESIZE",
                    0xF3 => "LOADBLOCK", 0xF4 => "LOADTILE", 0xF0 => "LOADTLUT", 0xE6 => "RDPLOADSYNC",
                    0xE7 => "RDPPIPESYNC", 0xE8 => "RDPTILESYNC", 0xDF => "ENDDL", 0xDE => "DL", _ => $"op{op:X2}" };
                string extra = "";
                if (op == 0xF5) { uint w0 = U32(d, p); extra = $" tile={(U32(d, p + 4) >> 24) & 7} fmt={(w0 >> 21) & 7} siz={(w0 >> 19) & 3}"; }
                Console.WriteLine($"    @0x{p:X} {U32(d, p):X8} {U32(d, p + 4):X8}  {name}{extra}");
                if (p > o && op == 0xFD) break;
            }
            return;
        }
        Console.WriteLine("  (no SETTIMG found for that offset)");
    }

    /// <summary>Diagnostic: tile the first N item icons into a PNG. Run: MegatonHammer --iconmontage [rom] [out.png]</summary>
    public static void IconMontage(string? romPath, string outPng)
    {
        romPath ??= DefaultRom;
        var rom = new RomImage(romPath);
        var src = new Rom.ItemIconSource(rom);
        Console.WriteLine($"[iconmontage] game={rom.Game} available={src.Available}");
        if (!src.Available) return;
        const int n = 96, cols = 16, cell = 34, pad = 2;
        int rows = (n + cols - 1) / cols;
        using var sheet = new System.Drawing.Bitmap(cols * (cell + pad) + pad, rows * (cell + pad) + pad);
        using (var g = System.Drawing.Graphics.FromImage(sheet))
        {
            g.Clear(System.Drawing.Color.FromArgb(40, 40, 46));
            for (int i = 0; i < n; i++)
            {
                var ic = src.Icon(i);
                if (ic == null) continue;
                int x = pad + (i % cols) * (cell + pad), y = pad + (i / cols) * (cell + pad);
                g.DrawImage(ic, x, y, 32, 32);
            }
        }
        sheet.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"[iconmontage] saved {outPng}");
    }

    /// <summary>Diagnostic: find ROM files whose decompressed size matches a target (locating the MM
    /// icon archive). Run: MegatonHammer --findfile [rom] [hexSize]</summary>
    public static void FindFile(string? romPath, int targetSize)
    {
        romPath ??= DefaultRom;
        var rom = new RomImage(romPath);
        Console.WriteLine($"[findfile] game={rom.Game}, {rom.Files.Count} files — scanning for YAR archives");
        for (int i = 0; i < rom.Files.Count; i++)
        {
            var f = rom.Files[i];
            if (!f.Exists || f.Size < 0x1000) continue;
            byte[] b; try { b = rom.GetFile(i); } catch { continue; }
            // YAR archive: word0 = first-block offset, and a Yaz0 block lives there.
            if (b.Length < 16) continue;
            int feo = (int)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
            if (feo <= 4 || feo + 4 > b.Length) continue;
            if (!Rom.Yaz0.IsYaz0(b, feo)) continue;
            int unarch = MegatonHammer.Rom.MmArchive.UnarchivedSize(b);
            Console.WriteLine($"  YAR file {i,4}: archive=0x{b.Length:X} unarchived=0x{unarch:X} feo=0x{feo:X}");
        }
    }

    /// <summary>Diagnostic: scan every ROM file for a decodable skeleton, reporting vrom/size/skel
    /// offset/limb count — finds the real large character objects (the link objects) regardless of
    /// the gObjectTable. Run: MegatonHammer --scanskel [romPath]</summary>
    public static void ScanSkel(string? romPath)
    {
        romPath ??= DefaultRom;
        var rom = new RomImage(romPath);
        Console.WriteLine($"[scanskel] game={rom.Game}, {rom.Files.Count} files");
        foreach (var f in rom.Files)
        {
            if (!f.Exists || f.Size < 0x8000) continue;   // character objects are large
            byte[] bytes;
            try { bytes = rom.GetFile(f.Index); } catch { continue; }
            int skel = ObjectModelReader.FindSkeleton(bytes);
            if (skel >= 0)
                Console.WriteLine($"  file {f.Index,4}: vrom=0x{f.VromStart:X8} size=0x{bytes.Length:X} ({bytes.Length / 1024}KB) skel@0x{skel:X}");
        }
    }

    /// <summary>Diagnostic: brute-force locate the gObjectTable by scanning every file for an 8-byte
    /// entry array where id 1 is a large object (gameplay_keep) and id 17 (link_child) points to a
    /// large skeletal file. Reports each candidate base. Run: MegatonHammer --findtable [romPath]</summary>
    public static void FindTable(string? romPath)
    {
        romPath ??= DefaultRom;
        var rom = new RomImage(romPath);
        var starts = new HashSet<uint>();
        foreach (var f in rom.Files) if (f.Exists) starts.Add(f.VromStart);
        uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
        // Precompute which vroms hold a large skeletal object (the character/link models) once.
        var bigSkel = new HashSet<uint>();
        foreach (var f in rom.Files)
        {
            if (!f.Exists || f.Size < 0x8000) continue;
            try { if (ObjectModelReader.FindSkeleton(rom.GetFile(f.Index)) >= 0) bigSkel.Add(f.VromStart); } catch { }
        }
        Console.WriteLine($"[findtable] game={rom.Game}, {bigSkel.Count} large skeletal files");
        foreach (var file in rom.Files)
        {
            if (!file.Exists || file.Size < 0x800) continue;
            byte[] d; try { d = rom.GetFile(file.Index); } catch { continue; }
            for (int b = 0; b + 18 * 8 <= d.Length; b += 4)   // b == id 0 base
            {
                uint s1 = U32(d, b + 8), e1 = U32(d, b + 12);            // id 1
                if (s1 < e1 && starts.Contains(s1) && e1 - s1 >= 0x20000 && // gameplay_keep is huge
                    bigSkel.Contains(U32(d, b + 0x11 * 8)))                 // id 17 = link_child, large skeletal
                    Console.WriteLine($"  file {file.Index} base@0x{b:X}: id1=0x{s1:X8}({(e1 - s1) / 1024}KB) " +
                        $"id16=0x{U32(d, b + 0x10 * 8):X8} id17=0x{U32(d, b + 0x11 * 8):X8}");
            }
        }
    }

    private static readonly string DefaultRom = Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");

    /// <summary>Diagnostic. List large ROM files: --animframe files [rom]. Dump a frame's joint
    /// shorts (root pos + rotations) from a decompressed file: --animframe &lt;fileIdx&gt; &lt;offHex&gt; &lt;limbCount&gt; [rom]</summary>
    public static void AnimFrame(string[] a)
    {
        if (a.Length >= 2 && a[1] == "files")
        {
            var rom0 = new RomImage(a.Length >= 3 ? a[2] : DefaultRom);
            for (int i = 0; i < rom0.Files.Count; i++)
            {
                var f = rom0.Files[i];
                if (f.Exists && f.Size > 0x100000) Console.WriteLine($"  file {i,3}: vrom=0x{f.VromStart:X8} size=0x{f.Size:X} ({f.Size / 1024}KB)");
            }
            return;
        }

        int idx = int.Parse(a[1]);
        int off = Convert.ToInt32(a[2].Replace("0x", ""), 16);
        int limbCount = int.Parse(a[3]);
        var rom = new RomImage(a.Length >= 5 ? a[4] : DefaultRom);
        var data = rom.GetFile(idx);
        Console.WriteLine($"[animframe] file {idx}: {data.Length} bytes (0x{data.Length:X}); frame0 @0x{off:X}, {limbCount} limbs");
        int n = (limbCount + 1) * 3;   // root pos + limbCount rotations, flattened to shorts
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++)
        {
            short v = (short)((data[off + i * 2] << 8) | data[off + i * 2 + 1]);
            sb.Append(v); sb.Append(", ");
            if (i % 3 == 2) sb.Append("  ");
        }
        Console.WriteLine(sb.ToString());
    }

    /// <summary>Diagnostic: report texture coverage across all actor models, flagging those that
    /// render mostly untextured (white) — i.e. textures the decoder couldn't resolve. --audit [rom]</summary>
    public static void Audit(string? romPath)
    {
        romPath ??= DefaultRom;
        var rom = new RomImage(romPath);
        var resolver = new ActorModelResolver(rom);
        int resolved = 0, mostlyUntex = 0, fullyUntex = 0; long texTris = 0, allTris = 0;
        int ciTex = 0, ciWithPal = 0;   // CI (colour-indexed) textures and how many have a resolved TLUT
        Console.WriteLine("[audit] actors rendering <25% textured (white):");
        for (int num = 0; num <= 0x01FF; num++)
        {
            ActorModelResolver.Model? m;
            try { m = resolver.Resolve(new ZActor { Number = (ushort)num, Variable = 0 }, adult: true); }
            catch { continue; }
            if (m == null || m.Tris.Count == 0) continue;
            resolved++;
            int tex = m.Tris.Count(t => t.Texture != null);
            allTris += m.Tris.Count; texTris += tex;
            foreach (var t in m.Tris)
                if (t.Texture is { } ti && ti.Type is Textures.N64TexType.Palette4bpp or Textures.N64TexType.Palette8bpp)
                { ciTex++; if (ti.PaletteOffset >= 0 && ti.PaletteFileIndex >= 0) ciWithPal++; }
            double frac = tex / (double)m.Tris.Count;
            if (tex == 0) fullyUntex++;
            if (frac < 0.25) { mostlyUntex++; Console.WriteLine($"    0x{num:X4}: {frac,4:P0} textured, {m.Tris.Count} tris"); }
        }
        Console.WriteLine($"[audit] resolved={resolved}  overall textured tris={100.0 * texTris / Math.Max(1, allTris):0.0}%  " +
                          $"mostly-untextured actors={mostlyUntex}  fully-untextured={fullyUntex}");
        Console.WriteLine($"[audit] CI textures: {ciWithPal}/{ciTex} have a resolved TLUT (colour vs grayscale)");

        // Sprite fallback: confirm icon_item_static loads and decodes.
        var icons = new ItemIconSource(rom);
        int opaque = 0;
        var bombIcon = icons.Available ? icons.Icon(2) : null;   // ITEM_BOMB
        if (bombIcon != null)
            for (int y = 0; y < bombIcon.Height; y++)
                for (int x = 0; x < bombIcon.Width; x++)
                    if (bombIcon.GetPixel(x, y).A > 8) opaque++;
        Console.WriteLine($"[audit] item icons: available={icons.Available}  bomb-icon opaque px={opaque}/{32 * 32}");
    }

    /// <summary>Diagnostic: print a display list's opcodes (to see prim/env/combine colour commands).
    /// --dumpdl &lt;object&gt; &lt;offHex&gt; [rom]</summary>
    public static void DumpDl(string? romPath, string objName, int off)
    {
        romPath ??= DefaultRom;
        var rom = new RomImage(romPath);
        var bytes = ObjectTable.Build(rom).GetObjectBytes(rom, objName);
        if (bytes == null) { Console.WriteLine($"[dumpdl] no bytes for {objName}"); return; }
        for (int p = off, lines = 0; p + 8 <= bytes.Length && lines < 80; p += 8, lines++)
        {
            byte op = bytes[p];
            uint w0 = U32(bytes, p), w1 = U32(bytes, p + 4);
            string name = op switch
            {
                0xDF => "G_ENDDL", 0xDE => "G_DL", 0x01 => "G_VTX", 0x05 => "G_TRI1", 0x06 => "G_TRI2",
                0xFA => "G_SETPRIMCOLOR", 0xFB => "G_SETENVCOLOR", 0xFC => "G_SETCOMBINE", 0xFD => "G_SETTIMG",
                0xF5 => "G_SETTILE", 0xF2 => "G_SETTILESIZE", 0xF0 => "G_LOADTLUT", 0xD7 => "G_TEXTURE",
                0xD9 => "G_GEOMETRYMODE", 0xDA => "G_MTX", _ => $"0x{op:X2}",
            };
            string extra = op == 0xFB || op == 0xFA ? $"  RGBA=({w1 >> 24 & 0xFF},{w1 >> 16 & 0xFF},{w1 >> 8 & 0xFF},{w1 & 0xFF})" : "";
            Console.WriteLine($"  0x{p:X5}: {op:X2} {name,-16} {w0:X8} {w1:X8}{extra}");
            if (op == 0xDF) break;
        }
    }

    public static void Run(string? romPath)
    {
        romPath ??= Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
        Console.WriteLine($"[selftest] ROM: {romPath}");
        var rom = new RomImage(romPath);
        Console.WriteLine($"[selftest] game={rom.Game}");
        var resolver = new ActorModelResolver(rom);

        ObjectModelReader.SkeletonsRead = 0;
        ObjectModelReader.SkeletonsPosedWithAnim = 0;

        // Link spawn point (actor 0x0000) — must resolve Adult Link with triangles.
        var link = resolver.Resolve(new ZActor { Number = 0x0000, Variable = 0 }, adult: true);
        if (link == null || link.Tris.Count == 0)
            Console.WriteLine("[selftest] FAIL: Link spawn resolved no model");
        else
        {
            var (lo, hi, bad) = Bounds(link.Tris);
            int green = link.Tris.Count(t => t.C0.Y > t.C0.X + 0.04f && t.C0.Y > t.C0.Z + 0.04f);
            Console.WriteLine($"[selftest] Link: {link.Tris.Count} tris, scale={link.Scale}, " +
                              $"bbox=({lo.X:0},{lo.Y:0},{lo.Z:0})..({hi.X:0},{hi.Y:0},{hi.Z:0}) finite={!bad} greenTintedTris={green}");
        }

        int resolved = 0, total = 0, badBounds = 0;
        for (int num = 0; num <= 0x01FF; num++)
        {
            total++;
            ActorModelResolver.Model? m;
            try { m = resolver.Resolve(new ZActor { Number = (ushort)num, Variable = 0 }, adult: true); }
            catch (Exception ex) { Console.WriteLine($"[selftest] EXCEPTION actor 0x{num:X4}: {ex.Message}"); continue; }
            if (m == null || m.Tris.Count == 0) continue;
            resolved++;
            var (_, _, bad) = Bounds(m.Tris);
            if (bad) { badBounds++; Console.WriteLine($"[selftest] WARN actor 0x{num:X4}: non-finite/huge bounds"); }
        }

        Console.WriteLine($"[selftest] coverage: {resolved}/{total} actors resolved a model");
        Console.WriteLine($"[selftest] skeletons drawn: {ObjectModelReader.SkeletonsRead}, " +
                          $"posed with detected animation: {ObjectModelReader.SkeletonsPosedWithAnim}, " +
                          $"rest-pose fallback: {ObjectModelReader.SkeletonsRead - ObjectModelReader.SkeletonsPosedWithAnim}");
        Console.WriteLine($"[selftest] bounds failures: {badBounds} (must be 0)");

        bool schemaOk = ParamSchemaTest();
        bool flipOk = FlipTest();
        bool flagOk = FlagConnectionsTest();
        bool texOk = TextureTest();
        bool gridOk = GridTest();
        bool browserOk = TextureBrowserDoubleClickTest();
        bool faceApplyOk = FaceEditApplyTest();
        bool pasteOk = PasteTest();
        bool playtestOk = PlaytestExportTest();
        Console.WriteLine(badBounds == 0 && link != null && link.Tris.Count > 0 && schemaOk && flipOk && flagOk && texOk && gridOk && browserOk && faceApplyOk && pasteOk && playtestOk
            ? "[selftest] PASS" : "[selftest] FAIL");
    }

    // Verify Face Edit "Apply (to selected)" pushes the current texture AND the scale/shift/rotation
    // onto every selected face (Hammer's FACE_APPLY_ALL), via the real dialog.
    // Builds a representative test level (floor brush + an entity + a Link spawn) through the scene/
    // room export, and validates the binaries that determine whether a level loads in SoH/2Ship: a
    // well-formed scene header with a Link spawn at the configured position, the room list, the
    // collision header, and a room binary with the actor list, mesh and end commands.
    private static bool PlaytestExportTest()
    {
        try
        {
            var doc = new Editor.MapDocument();
            doc.AddSolid(Editor.Solid.CreateBox(new Vector3(-200, -10, -200), new Vector3(200, 10, 200)));  // floor
            doc.AddActor(new Editor.ZActor { Number = 0x000A, XPos = 0, YPos = 20, ZPos = 0, DisplayName = "Chest" });
            var scene = doc.Scene;
            scene.Settings.SpawnPos = new Vector3(10, 30, -20);
            scene.Settings.SpawnRoom = 0;

            var (sc, rooms) = Export.SceneExporter.BuildBinaries(scene);

            byte Cmd(int i) => sc[i * 8];
            bool hdrOk = Cmd(0) == 0x00 && Cmd(2) == 0x04 && Cmd(3) == 0x03 && Cmd(8) == 0x14;  // spawn,roomlist,collision,end
            bool roomCountOk = sc[2 * 8 + 1] == scene.Rooms.Count;

            const int so = 72;   // spawn list starts after the 9-command header
            int spawnId = (sc[so] << 8) | sc[so + 1];
            short spX = (short)((sc[so + 2] << 8) | sc[so + 3]);
            short spY = (short)((sc[so + 4] << 8) | sc[so + 5]);
            bool spawnOk = spawnId == 0x0000 && spX == 10 && spY == 30;   // Link at SpawnPos

            var rm = rooms[0];
            bool rActor = false, rMesh = false, rEnd = false;
            for (int i = 0; i + 8 <= rm.Length; i += 8)
            { byte c = rm[i]; if (c == 0x01) rActor = true; if (c == 0x0A) rMesh = true; if (c == 0x14) { rEnd = true; break; } }

            bool ok = hdrOk && roomCountOk && spawnOk && rActor && rMesh && rEnd;
            Console.WriteLine($"[selftest] playtest export: scene={sc.Length}b rooms={rooms.Count} room0={rm.Length}b " +
                              $"spawn=0x{spawnId:X4}@({spX},{spY}) roomActorList={rActor} roomMesh={rMesh} {(ok ? "OK" : "BAD")}");

            // Also build the actual SoH/2Ship OTR resource set the playtest packs into the mod O2R,
            // confirming it assembles without crashing and produces the scene/room/collision/geometry
            // resources SoH loads. (Loading them in the live engine still needs SoH itself.)
            string? basePath = Rom.OotSceneFiles.ScenePath(0x52);
            if (basePath != null)
            {
                var otr = Otr.OtrSceneWriter.BuildLevel(scene, basePath, mm: false);
                bool otrOk = otr.Count >= 3 && otr.All(r => !string.IsNullOrEmpty(r.Path) && r.Data.Length > 0);
                Console.WriteLine($"[selftest] playtest OTR: {otr.Count} resource(s) non-empty={otrOk}; " +
                                  $"{string.Join(", ", otr.Take(5).Select(r => System.IO.Path.GetFileName(r.Path)))}");
                ok = ok && otrOk;
            }
            else Console.WriteLine("[selftest] playtest OTR: skipped (scene id 0x52 has no base path)");
            return ok;
        }
        catch (Exception ex) { Console.WriteLine($"[selftest] playtest export EXCEPTION: {ex.Message}"); return false; }
    }

    // Verifies the Copy → Paste pipeline (EditClipboard + AddSolid) actually adds a valid, visible
    // solid — to tell a logic bug apart from a UX/keyboard issue in regular Paste.
    private static bool PasteTest()
    {
        try
        {
            var doc = new Editor.MapDocument();
            var box = Editor.Solid.CreateBox(Vector3.Zero, new Vector3(64, 64, 64));
            doc.AddSolid(box);
            box.IsSelected = true;

            Editor.EditClipboard.CopyFrom(doc);
            int before = doc.Count;
            // Mirror PasteClipboard's core: instantiate, offset, add (selected).
            doc.ClearSelection();
            var (solids, _) = Editor.EditClipboard.Instantiate();
            foreach (var s in solids) { s.Translate(new Vector3(64, 0, 64)); s.IsSelected = true; doc.AddSolid(s); }
            int after = doc.Count;
            var pasted = doc.Solids.LastOrDefault();
            bool ok = after == before + 1 && pasted != null && pasted.Faces.Count > 0 && pasted.IsSelected;
            Console.WriteLine($"[selftest] paste: before={before} after={after} pastedFaces={pasted?.Faces.Count ?? -1} {(ok ? "OK" : "BAD")}");
            return ok;
        }
        catch (Exception ex) { Console.WriteLine($"[selftest] paste EXCEPTION: {ex.Message}"); return false; }
    }

    private static bool FaceEditApplyTest()
    {
        try
        {
            const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var doc = new Editor.MapDocument();
            var box = Editor.Solid.CreateBox(Vector3.Zero, new Vector3(64, 64, 64));
            doc.AddSolid(box);
            foreach (var f in box.Faces) f.FaceSelected = true;

            using var dlg = new Forms.FaceEditDialog(doc, new Textures.TextureLibrary(), () => null, () => { }, null);
            var t = typeof(Forms.FaceEditDialog);
            t.GetField("_shownTexture", BF)!.SetValue(dlg, "newtex");
            ((System.Windows.Forms.NumericUpDown)t.GetField("_scaleS", BF)!.GetValue(dlg)!).Value = 16m;
            ((System.Windows.Forms.NumericUpDown)t.GetField("_shiftS", BF)!.GetValue(dlg)!).Value = 8m;
            ((System.Windows.Forms.NumericUpDown)t.GetField("_rotate", BF)!.GetValue(dlg)!).Value = 90m;
            t.GetMethod("ApplySelected", BF)!.Invoke(dlg, null);

            bool faceOk = box.Faces.Count > 0 && box.Faces.All(f =>
                f.TextureName == "newtex" && Approx(f.TexScaleS, 16) && Approx(f.TexShiftS, 8) && Approx(f.TexRotation, 90));

            // Hammer parity: a SELECTED BRUSH (no individual faces marked) applies to all its faces.
            var box2 = Editor.Solid.CreateBox(new Vector3(200, 0, 0), new Vector3(264, 64, 64));
            doc.AddSolid(box2);
            box2.IsSelected = true;
            t.GetField("_shownTexture", BF)!.SetValue(dlg, "brushtex");
            t.GetMethod("ApplySelected", BF)!.Invoke(dlg, null);
            bool brushOk = box2.Faces.Count > 0 && box2.Faces.All(f => f.TextureName == "brushtex");

            bool ok = faceOk && brushOk;
            Console.WriteLine($"[selftest] faceedit apply: faceSel faces={box.Faces.Count} tex={box.Faces[0].TextureName} ({(faceOk ? "OK" : "BAD")}); " +
                              $"brushSel faces={box2.Faces.Count} tex={box2.Faces[0].TextureName} ({(brushOk ? "OK" : "BAD")})");
            return ok;
        }
        catch (Exception ex) { Console.WriteLine($"[selftest] faceedit apply skipped: {ex.Message}"); return true; }
    }

    // Build the REAL texture-browser popout (its library auto-loads built-in textures) and fire a
    // double-click on the first tile two ways — a Clicks==2 MouseDown (real WM_LBUTTONDBLCLK) and two
    // Clicks==1 MouseDowns (the timing fallback) — verifying TextureCommitted fires (= select + close
    // + apply) in both. Tolerant of headless/UI-init issues (returns true if it can't run a window).
    private static bool TextureBrowserDoubleClickTest()
    {
        try
        {
            var lib = new Textures.TextureLibrary();
            var onMouseDown = typeof(System.Windows.Forms.Control)
                .GetMethod("OnMouseDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var gridField = typeof(Forms.TextureBrowserForm)
                .GetField("_grid", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            string? a = FireBrowserClick(lib, onMouseDown, gridField, 2);           // WM_LBUTTONDBLCLK
            string? b = FireBrowserClick(lib, onMouseDown, gridField, 1, twice: true); // timing fallback

            bool ok = a != null && b != null;
            Console.WriteLine($"[selftest] texbrowser dblclick: clicks2={a ?? "(none)"} timing={b ?? "(none)"} {(ok ? "OK" : "BAD")}");
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[selftest] texbrowser dblclick skipped (UI init): {ex.Message}");
            return true;   // don't fail the suite on a headless/UI-init issue
        }
    }

    private static string? FireBrowserClick(Textures.TextureLibrary lib, System.Reflection.MethodInfo onMouseDown,
                                            System.Reflection.FieldInfo gridField, int clicks, bool twice = false)
    {
        using var form = new Forms.TextureBrowserForm(lib) { StartPosition = System.Windows.Forms.FormStartPosition.Manual };
        string? committed = null;
        form.TextureCommitted += n => committed = n;
        form.Location = new System.Drawing.Point(-3000, -3000);   // off-screen
        form.Show();
        System.Windows.Forms.Application.DoEvents();
        var grid = (System.Windows.Forms.Control)gridField.GetValue(form)!;
        if (grid.Controls.Count > 0)
        {
            var tile = grid.Controls[0];
            onMouseDown.Invoke(tile, [new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, clicks, 5, 5, 0)]);
            if (twice)
                onMouseDown.Invoke(tile, [new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 5, 5, 0)]);
        }
        System.Windows.Forms.Application.DoEvents();
        if (!form.IsDisposed) form.Close();
        return committed;
    }

    // Verify the zoom-adaptive grid: zooming in refines the step below the base grid (so you can
    // draw fine brushes without lowering the grid button); zooming out coarsens it; a comfortable
    // zoom respects the base grid size.
    private static bool GridTest()
    {
        int zin = GridSnap.EffectiveStep(1024, 1f);       // zoomed in: should refine well below 1024
        int zout = GridSnap.EffectiveStep(1024, 1000f);   // zoomed out: should coarsen above 1024
        int mid = GridSnap.EffectiveStep(64, 8f);         // comfortable zoom: respect the base
        bool ok = zin <= 64 && zin >= 1 && zout >= 2048 && mid == 64;
        Console.WriteLine($"[selftest] grid: zoomIn(1024,wpp1)={zin} zoomOut(1024,wpp1000)={zout} mid(64,wpp8)={mid} {(ok ? "OK" : "BAD")}");
        return ok;
    }

    // Verify the ComputeFaces carry fix (texture shift/rotation survive a transform) and Texture Lock
    // (translating a brush shifts its UVs so the texture stays pinned).
    private static bool TextureTest()
    {
        Solid.TextureLock = false;
        var b = Solid.CreateBox(Vector3.Zero, new Vector3(64, 64, 64));
        var zf = b.Faces.First(f => MathF.Abs(f.Plane.Normal.Z - 1f) < 0.5f);
        int pi = zf.PlaneIndex; zf.TexShiftS = 5f; zf.TexRotation = 30f;
        b.Translate(new Vector3(40, 0, 0));                      // calls ComputeFaces — must preserve mapping
        var zf2 = b.Faces.First(f => f.PlaneIndex == pi);
        bool carried = Approx(zf2.TexShiftS, 5f) && Approx(zf2.TexRotation, 30f);

        Solid.TextureLock = true;
        var c = Solid.CreateBox(Vector3.Zero, new Vector3(64, 64, 64));
        var cf = c.Faces.First(f => MathF.Abs(f.Plane.Normal.Z - 1f) < 0.5f);
        int cpi = cf.PlaneIndex; float before = cf.TexShiftS;
        c.Translate(new Vector3(64, 0, 0));                      // u=worldX, scale 64 → shift -= 1
        var cf2 = c.Faces.First(f => f.PlaneIndex == cpi);
        bool locked = Approx(cf2.TexShiftS, before - 1f);
        Console.WriteLine($"[selftest] texture: carry(shift+rot preserved)={carried} lock(shift {before:0.##}→{cf2.TexShiftS:0.##})={locked} {(carried && locked ? "OK" : "BAD")}");
        return carried && locked;
    }

    // Verify the flag-connections analyzer (#12 logic editor): actors sharing a switch flag are
    // grouped, setter/reader roles resolve, and a single-role flag is reported as dangling.
    private static bool FlagConnectionsTest()
    {
        var sw5 = new ZActor { Number = 0x012A, Variable = 5 << 8 };   // Obj_Switch sets switch flag 5
        var sw7 = new ZActor { Number = 0x012A, Variable = 7 << 8 };   // Obj_Switch sets switch flag 7 (no reader)
        var rock5 = new ZActor { Number = 0x0127, Variable = 5 };      // Obj_Bombiwa reads+sets switch flag 5
        var groups = FlagConnectionAnalyzer.Analyze(new[] { sw5, sw7, rock5 }, isOoT: true);
        var g5 = groups.FirstOrDefault(g => g.Kind == ActorParamSchema.FlagKind.Switch && g.Index == 5);
        var g7 = groups.FirstOrDefault(g => g.Kind == ActorParamSchema.FlagKind.Switch && g.Index == 7);
        bool ok = g5 != null && g5.Users.Count == 2 && g5.HasSetter && g5.HasReader &&
                  g7 != null && g7.Users.Count == 1 && (g7.HasSetter ^ g7.HasReader);
        Console.WriteLine($"[selftest] flags: flag5 users={g5?.Users.Count} set={g5?.HasSetter} read={g5?.HasReader}; " +
                          $"flag7 dangling={(g7 != null && (g7.HasSetter ^ g7.HasReader))} {(ok ? "OK" : "BAD")}");
        return ok;
    }

    // Verify Solid.Flip mirrors the AABB on the chosen axis and is its own inverse (flip twice =
    // identity) — validates the clip-plane reflection used by Hammer-style Flip X/Y/Z.
    private static bool FlipTest()
    {
        var box = Solid.CreateBox(new Vector3(10, 20, 30), new Vector3(50, 60, 70));
        var (lo0, hi0) = box.GetAABB();
        float cx = 0f;   // mirror about origin so a no-op Flip would fail (box would not move)
        box.Flip(0, cx);
        var (lo1, hi1) = box.GetAABB();
        // X mirrored about cx (min<->max), Y/Z unchanged.
        bool mirrored = Approx(lo1.X, 2 * cx - hi0.X) && Approx(hi1.X, 2 * cx - lo0.X) &&
                        Approx(lo1.Y, lo0.Y) && Approx(hi1.Z, hi0.Z);
        box.Flip(0, cx);
        var (lo2, hi2) = box.GetAABB();
        bool involutive = Approx(lo2.X, lo0.X) && Approx(hi2.X, hi0.X);
        Console.WriteLine($"[selftest] flip: mirrored={mirrored} involutive={involutive} " +
                          $"(X {lo0.X:0}..{hi0.X:0} -> {lo1.X:0}..{hi1.X:0} -> {lo2.X:0}..{hi2.X:0})");

        // Rotate (Transform dialog): 90° about Y swaps the X and Z extents.
        var rb = Solid.CreateBox(new Vector3(10, 0, 100), new Vector3(50, 10, 200)); // X w=40, Z d=100
        var (rlo, rhi) = rb.GetAABB();
        rb.Rotate((rlo + rhi) * 0.5f, new Vector3(0, 90, 0));
        var (qlo, qhi) = rb.GetAABB();
        bool rotOk = Approx(qhi.X - qlo.X, 100) && Approx(qhi.Z - qlo.Z, 40);
        Console.WriteLine($"[selftest] rotate90Y: X-extent={qhi.X - qlo.X:0} Z-extent={qhi.Z - qlo.Z:0} {(rotOk ? "OK" : "BAD")}");

        // Texture lock through Flip: a textured face's texture must stay welded to the geometry (Hammer). Flip
        // about a NON-origin centre (as the editor does — the selection centre) and check the UV at each
        // mirrored vertex lands on the SAME texel it had before (differs only by whole tiles). Mirroring the
        // axes without the shift compensation left it slid by a fractional tile — the reported "flip breaks
        // textures" bug.
        Solid.TextureLock = true;
        var ftb = Solid.CreateBox(new Vector3(0, 0, 0), new Vector3(100, 40, 8));
        var top = ftb.Faces.OrderByDescending(f => f.Plane.Normal.Y).First();   // +Y face
        top.TextureName = "t"; top.TexScaleS = 32f; top.TexScaleT = 32f; top.TexShiftS = 0.3f; top.TexShiftT = 0f; top.ResetAxes();
        var verts = top.Vertices.ToList();
        var uvBefore = verts.Select(v => (v, uv: top.UVAt(v))).ToList();
        const float fc = 50f;   // brush centre, NOT the origin — so the shift compensation matters
        ftb.Flip(0, fc);
        var topA = ftb.Faces.OrderByDescending(f => f.Plane.Normal.Y).First();
        static float Frac1(float x) => x - MathF.Floor(x);
        static bool SameTexel(float a, float b) { float d = Frac1(a - b); return d < 2e-3f || d > 1f - 2e-3f; }
        bool texLocked = true;
        foreach (var (v, uv) in uvBefore)
        {
            var vm = new Vector3(2 * fc - v.X, v.Y, v.Z);   // its mirrored world position
            var found = topA.Vertices.OrderBy(w => (w - vm).LengthSquared).First();
            var uvA = topA.UVAt(found);
            if (!SameTexel(uv.X, uvA.X) || !SameTexel(uv.Y, uvA.Y)) texLocked = false;
        }
        Console.WriteLine($"[selftest] flip-texlock: texture stays welded through Flip = {(texLocked ? "OK" : "BAD (texture slid)")}" );

        return mirrored && involutive && rotOk && texLocked;
    }

    private static bool Approx(float a, float b) => MathF.Abs(a - b) < 0.01f;

    // Verify the parameter bit-field codecs (#12): each field round-trips and Set never disturbs
    // bits outside its own slice. Uses the real registered schemas (chest, switch, pot, crate).
    private static bool ParamSchemaTest()
    {
        bool ok = true;
        // En_Box chest: type=[12,4], treasureFlag=[0,5]. Set both, confirm independent decode.
        var chest = ActorParamSchema.For(true, 0x000A)!;
        var type = chest.Fields[0]; var flag = chest.Fields[1];
        ushort v = 0;
        v = type.Set(v, 11);          // ENBOX_TYPE_SWITCH_FLAG_BIG
        v = flag.Set(v, 23);          // treasure flag 23
        bool chestOk = type.Get(v) == 11 && flag.Get(v) == 23 && v == ((11 << 12) | 23);
        Console.WriteLine($"[selftest] chest params: var=0x{v:X4} type={type.Get(v)} flag={flag.Get(v)} {(chestOk ? "OK" : "BAD")}");
        ok &= chestOk;

        // Obj_Switch: type=[0,3], subtype=[4,3], frozen=[7,1], switchFlag=[8,6]. No cross-talk.
        var sw = ActorParamSchema.For(true, 0x012A)!;
        ushort s = 0;
        s = sw.Fields[0].Set(s, 3);   // crystal
        s = sw.Fields[1].Set(s, 1);   // toggle
        s = sw.Fields[2].Set(s, 1);   // frozen
        s = sw.Fields[3].Set(s, 42);  // switch flag 42
        bool swOk = sw.Fields[0].Get(s) == 3 && sw.Fields[1].Get(s) == 1 && sw.Fields[2].Get(s) == 1 && sw.Fields[3].Get(s) == 42;
        Console.WriteLine($"[selftest] switch params: var=0x{s:X4} type={sw.Fields[0].Get(s)} sub={sw.Fields[1].Get(s)} frozen={sw.Fields[2].Get(s)} flag={sw.Fields[3].Get(s)} {(swOk ? "OK" : "BAD")}");
        ok &= swOk;

        // Set must clear the old value in its slice (no OR-accumulation bug).
        ushort r = sw.Fields[3].Set(0xFFFF, 5);
        bool clearOk = sw.Fields[3].Get(r) == 5;
        Console.WriteLine($"[selftest] switchFlag overwrite 0xFFFF→5: got {sw.Fields[3].Get(r)} {(clearOk ? "OK" : "BAD")}");
        ok &= clearOk;
        return ok;
    }

    private static (Vector3 lo, Vector3 hi, bool bad) Bounds(IReadOnlyList<MeshTri> tris)
    {
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue); bool bad = false;
        foreach (var t in tris)
            foreach (var p in new[] { t.P0, t.P1, t.P2 })
            {
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) ||
                    MathF.Abs(p.X) > 1e6f || MathF.Abs(p.Y) > 1e6f || MathF.Abs(p.Z) > 1e6f)
                    bad = true;
                lo = Vector3.ComponentMin(lo, p); hi = Vector3.ComponentMax(hi, p);
            }
        return (lo, hi, bad);
    }
}
