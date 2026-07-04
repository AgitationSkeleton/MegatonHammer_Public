using System.Drawing;
using System.Drawing.Imaging;
using MegatonHammer.Editor;
using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Read-only diagnostic for the "forest scenes render in autumn red/brown" bug. For each forest scene it
/// loads the imported geometry (RoomMeshReader — same path the level renderer uses) and, per distinct bound
/// texture, prints its format/palette AND the average DECODED colour vs the average per-vertex shade tint of
/// the tris using it. That separates a wrong-palette decode (texture itself decodes red) from a wrong prim/
/// env vertex tint (texture is grey but the baked vertex colour is red). Run: MegatonHammer --foresttex
/// </summary>
public static class ForestTexProbe
{
    private const string OotRom = @"D:\Copilot_OOT\READ_ONLY_GameROMs\Legend of Zelda, The - Ocarina of Time (USA).z64";

    public static void Run()
    {
        if (!File.Exists(OotRom)) { Console.WriteLine("[foresttex] OoT ROM not found"); return; }
        var rom = new RomImage(OotRom);
        foreach (var (id, name) in new[] { (0x55, "Kokiri Forest"), (0x56, "Sacred Forest Meadow"), (0x5B, "Lost Woods"), (0x3E, "Grottos") })
        {
            Console.WriteLine($"\n=== {name} (0x{id:X2}) ===");
            try
            {
                var s = SceneImporter.Import(rom, id);
                if (s != null)
                {
                    Console.WriteLine($"  scene has {s.Lights.Count} light setting(s); RoomMeshReader uses Lights[0]:");
                    for (int li = 0; li < Math.Min(s.Lights.Count, 6); li++)
                    {
                        var lt = s.Lights[li];
                        Console.WriteLine($"    [{li}] amb=({lt.AmbR},{lt.AmbG},{lt.AmbB}) L1=({lt.L1r},{lt.L1g},{lt.L1b}){(li == 0 ? "  <- used" : "")}");
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"  light dump ex: {ex.Message}"); }

            ImportedLevel? level;
            try { level = ImportedLevel.Load(rom, id); } catch (Exception ex) { Console.WriteLine($"  load EXCEPTION: {ex.Message}"); continue; }
            if (level == null) { Console.WriteLine("  import null"); continue; }

            using var src = new RomTextureSource(rom);
            // Aggregate per distinct texture: tri count + summed vertex shade.
            var agg = new Dictionary<long, (RomTexInfo tex, int tris, double sr, double sg, double sb)>();
            foreach (var mesh in level.RoomMeshes)
                foreach (var t in mesh)
                {
                    if (t.Texture is not { } ti) continue;
                    long key = ((long)ti.FileIndex << 40) ^ ((long)ti.Offset << 8) ^ (long)ti.Type;
                    var a = agg.TryGetValue(key, out var e) ? e : (ti, 0, 0, 0, 0);
                    var c = (t.C0 + t.C1 + t.C2) / 3f;
                    agg[key] = (a.tex, a.tris + 1, a.sr + c.X, a.sg + c.Y, a.sb + c.Z);
                }

            foreach (var (_, e) in agg.OrderByDescending(kv => kv.Value.tris).Take(8))
            {
                var ti = e.tex;
                (int dr, int dg, int db) = AvgDecoded(src, ti);
                int vr = (int)(255 * e.sr / e.tris), vg = (int)(255 * e.sg / e.tris), vb = (int)(255 * e.sb / e.tris);
                bool decodedRed = dr > dg + 25 && dr > db + 25;
                bool tintRed = vr > vg + 25 && vr > vb + 25;
                Console.WriteLine($"  tris={e.tris,-5} {ti.Type,-16} off=0x{ti.Offset:X6} pal=0x{ti.PaletteOffset:X}/f{ti.PaletteFileIndex} " +
                                  $"decoded=({dr},{dg},{db}){(decodedRed ? " RED-TEX" : "")}  vtxTint=({vr},{vg},{vb}){(tintRed ? " RED-TINT" : "")}");
            }
        }
    }

    private static (int, int, int) AvgDecoded(RomTextureSource src, RomTexInfo ti)
    {
        try
        {
            using Bitmap bmp = src.Decode(ti);
            long r = 0, g = 0, b = 0; int n = 0;
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            unsafe
            {
                byte* p = (byte*)data.Scan0;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        byte* px = p + y * data.Stride + x * 4;
                        b += px[0]; g += px[1]; r += px[2]; n++;
                    }
            }
            bmp.UnlockBits(data);
            return n == 0 ? (0, 0, 0) : ((int)(r / n), (int)(g / n), (int)(b / n));
        }
        catch { return (-1, -1, -1); }
    }
}
