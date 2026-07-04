using System;
using System.Collections.Generic;
using System.Linq;
using MegatonHammer.Editor;
using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Audits per-area texture completeness: for each scene it decodes the actual geometry (RoomMeshReader,
/// the same path the editor renders with — the ground truth of which textures a level uses) and compares
/// that set against what the texture-browser pipeline (RomTextureSource + SceneTextureMapper) catalogs. A
/// texture bound by the geometry but absent from the catalog is a browser gap. MegatonHammer --textureaudit [oot|mm] [nameSubstr]
/// </summary>
public static class TextureAudit
{
    private static readonly string OotRom = Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
    private static readonly string MmRom  = Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).z64");

    public static void Run(string[] args)
    {
        bool mm = args.Length >= 2 && args[1].Equals("mm", StringComparison.OrdinalIgnoreCase);
        string sub = args.Length >= 3 ? args[2] : "";
        string romPath = mm ? MmRom : OotRom;
        if (!System.IO.File.Exists(romPath)) { Console.WriteLine($"ROM not found: {romPath}"); return; }

        var rom = new RomImage(romPath);
        var map = RomAssetIndex.BuildMap(rom);
        var infos = new RomTextureSource(rom).Scan();
        var (allTex, _) = SceneTextureMapper.Build(rom, infos, map);

        // What the browser catalogs, keyed by (file, offset) — the texture's ROM identity.
        var catalog = new HashSet<(int, int)>(allTex.Select(t => (t.FileIndex, t.Offset)));

        IEnumerable<(int id, string name)> scenes = mm
            ? MmSceneFiles.All
            : Enumerable.Range(0, 128).Where(OotSceneFiles.IsValid).Select(i => (i, OotSceneNames.Pretty(i)));

        int totalGeom = 0, totalMissing = 0, scenesWithGaps = 0;
        foreach (var (id, name) in scenes.Where(t => t.name.Contains(sub, StringComparison.OrdinalIgnoreCase)))
        {
            ImportedLevel? level;
            try { level = ImportedLevel.Load(rom, id); } catch { continue; }
            if (level == null) continue;

            // Distinct textures the geometry actually binds (the renderer's own resolution).
            var geom = level.RoomMeshes
                .SelectMany(m => m)
                .Where(t => t.Texture is { })
                .Select(t => (t.Texture!.Value.FileIndex, t.Texture!.Value.Offset))
                .Distinct()
                .ToList();

            var missing = geom.Where(g => !catalog.Contains(g)).ToList();
            totalGeom += geom.Count;
            totalMissing += missing.Count;
            if (missing.Count > 0)
            {
                scenesWithGaps++;
                Console.WriteLine($"[{id:X2}] {name}: geometry uses {geom.Count} textures, {missing.Count} NOT in browser catalog");
                foreach (var (f, o) in missing.Take(12))
                    Console.WriteLine($"       missing  file={f} off=0x{o:X}");
                if (missing.Count > 12) Console.WriteLine($"       … +{missing.Count - 12} more");
            }
            else if (!string.IsNullOrEmpty(sub))
                Console.WriteLine($"[{id:X2}] {name}: geometry uses {geom.Count} textures — all catalogued OK");
        }
        Console.WriteLine($"\n=== {(mm ? "MM" : "OoT")} texture audit: {totalMissing}/{totalGeom} geometry textures missing from the browser catalog across {scenesWithGaps} scenes ===");
    }
}
