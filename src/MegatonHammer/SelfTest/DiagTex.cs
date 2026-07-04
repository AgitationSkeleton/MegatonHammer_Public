using System.Drawing;
using System.Drawing.Imaging;
using MegatonHammer.Editor;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Diagnose the PJ64 "grey textures" report: for every ROM texture a project's brushes use, decode it the
/// same way the editor texture library does (incl. the #5 prim tint) and report its format, size, prim
/// colour and the AVERAGE RGB of the decoded bitmap — so we can tell whether the baked texel data is
/// actually coloured (→ a render-side problem) or grey (→ the tint isn't being baked).
/// Run: MegatonHammer --diagtex &lt;path.mhproj&gt;
/// </summary>
public static class DiagTex
{
    public static void Run(string[] a)
    {
        if (a.Length < 2) { Console.WriteLine("usage: --diagtex <path.mhproj>"); return; }
        string path = a[1];
        var doc = new MapDocument();
        try { ProjectSerializer.Load(doc, path); }
        catch (Exception ex) { Console.WriteLine($"load failed: {ex.Message}"); return; }

        // Distinct brush textures used in the project + how each is oriented (floor/wall/ceiling).
        var orient = new Dictionary<string, (int floor, int wall, int ceil)>();
        foreach (var f in doc.Scene.Rooms.SelectMany(r => r.Geometry).SelectMany(s => s.Faces))
        {
            if (f.TextureName == null) continue;
            var o = orient.GetValueOrDefault(f.TextureName);
            float ny = f.Plane.Normal.Y;
            if (ny > 0.7f) o.floor++; else if (ny < -0.7f) o.ceil++; else o.wall++;
            orient[f.TextureName] = o;
        }
        var names = orient.Keys.ToList();
        Console.WriteLine($"== {Path.GetFileName(path)}: {names.Count} distinct brush textures ==");
        foreach (var (k, o) in orient) Console.WriteLine($"   {k}: floor={o.floor} wall={o.wall} ceiling={o.ceil}");
        Console.WriteLine();

        string mmRom = EditorSettings.MmRomPath;
        if (string.IsNullOrWhiteSpace(mmRom) || !File.Exists(mmRom))
            mmRom = @"D:\Copilot_OOT\READ_ONLY_GameROMs\Legend of Zelda, The - Majora's Mask (USA).z64";
        if (!File.Exists(mmRom)) { Console.WriteLine($"MM ROM not found: {mmRom}"); return; }

        var rom = new Rom.RomImage(mmRom);
        var src = new Rom.RomTextureSource(rom);
        Console.WriteLine("building texture library exactly like the editor (SceneTextureMapper + AddRomTextures)…");
        var rawInfos = src.Scan();
        var map = Rom.RomAssetIndex.BuildMap(rom);
        var (combined, folders) = Rom.SceneTextureMapper.Build(rom, rawInfos, map);
        var lib = new Textures.TextureLibrary();
        lib.AddRomTextures(combined, src, map.FileScene, folders);
        Console.WriteLine($"library has {lib.Entries.Count} textures; resolving the project's brush names (the EXACT path the exporter uses)…\n");

        foreach (var name in names)
        {
            var entry = lib.Find(name);
            if (entry == null) { Console.WriteLine($"{name}: NOT FOUND in library -> face would export UNTEXTURED (flat grey shade)"); continue; }
            using var bmp = entry.Image;   // the exact bitmap the N64 + OTR exporters bake
            var (ar, ag, ab) = Average(bmp);
            int chroma = Math.Max(Math.Max(ar, ag), ab) - Math.Min(Math.Min(ar, ag), ab);
            Console.WriteLine($"{name}: {entry.TypeLabel} {bmp.Width}x{bmp.Height} avgRGB=({ar},{ag},{ab}) chroma={chroma} " +
                              $"{(chroma < 8 ? "<-- GREY in the baked bitmap" : "coloured in the baked bitmap")}");
        }
    }

    private static (int r, int g, int b) Average(Bitmap bmp)
    {
        long r = 0, g = 0, b = 0; int w = bmp.Width, h = bmp.Height, n = w * h;
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int px = System.Runtime.InteropServices.Marshal.ReadInt32(bd.Scan0, y * bd.Stride + x * 4);
                    b += px & 0xFF; g += (px >> 8) & 0xFF; r += (px >> 16) & 0xFF;
                }
        }
        finally { bmp.UnlockBits(bd); }
        return n == 0 ? (0, 0, 0) : ((int)(r / n), (int)(g / n), (int)(b / n));
    }
}
