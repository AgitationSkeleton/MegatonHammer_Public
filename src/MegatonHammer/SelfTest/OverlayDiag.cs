using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>--diagoverlay [actorIdHex] : validates the overlay-mesh pipeline. Locates gActorOverlayTable,
/// prints the actor's overlay VROM/VRAM, reads the overlay file, and scans it for geometry display lists
/// (version-independent), reporting each DL's offset / tri count / bounds. Proves we can render an actor's
/// overlay-embedded mesh (Bg_Ganon_Otyuka platform, id 0x0106) instead of the shared object's boss body.</summary>
public static class OverlayDiag
{
    private static readonly string Oot = Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");

    public static void Run(string[] args)
    {
        int actorId = 0x0106;   // Bg_Ganon_Otyuka
        if (args.Length >= 2 && int.TryParse(args[1].Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out int a))
            actorId = a;

        var rom = new RomImage(Oot);
        var ovl = ActorOverlayTable.Build(rom);
        System.Console.WriteLine($"gActorOverlayTable: {ovl.Count} overlay entries located.");

        if (ovl.For(actorId) is not { } e)
        {
            System.Console.WriteLine($"actor 0x{actorId:X4}: no overlay entry (internally-linked or table not found).");
            return;
        }
        System.Console.WriteLine($"actor 0x{actorId:X4}: vrom=0x{e.VromStart:X8}..0x{e.VromEnd:X8} vram=0x{e.VramStart:X8}..0x{e.VramEnd:X8} init=0x{e.InitInfo:X8}");

        var bytes = ovl.GetOverlayBytes(rom, actorId);
        if (bytes == null) { System.Console.WriteLine("could not read overlay file bytes."); return; }
        int fileIdx = -1;
        foreach (var f in rom.Files) if (f.Exists && f.VromStart == e.VromStart) { fileIdx = f.Index; break; }
        System.Console.WriteLine($"overlay file: {bytes.Length} bytes, fileIndex={fileIdx}");

        var matOffs = ObjectModelReader.ScanOverlayMaterials(bytes, e.VramStart);
        System.Console.WriteLine($"material DLs: {string.Join(", ", matOffs.ConvertAll(m => $"0x{m:X4}"))}");
        var dls = ObjectModelReader.ScanOverlayGeometry(bytes, fileIdx, e.VramStart);
        System.Console.WriteLine($"geometry DLs found: {dls.Count}");
        foreach (var (goff, _) in dls)
        {
            int mat = -1; foreach (int m in matOffs) if (m < goff) mat = m;
            if (mat < 0) continue;
            var tt = ObjectModelReader.ReadOverlayDLs(bytes, fileIdx, e.VramStart, mat, goff);
            int tex = tt.FindAll(t => t.Texture != null).Count;
            System.Console.WriteLine($"  geom 0x{goff:X4} + mat 0x{mat:X4} -> {tt.Count} tris, {tex} textured");
        }
        foreach (var (off, tris) in dls)
        {
            float mnx = 1e9f, mny = 1e9f, mnz = 1e9f, mxx = -1e9f, mxy = -1e9f, mxz = -1e9f;
            int tex = 0;
            foreach (var tr in tris)
            {
                foreach (var pt in new[] { tr.P0, tr.P1, tr.P2 })
                {
                    mnx = System.MathF.Min(mnx, pt.X); mny = System.MathF.Min(mny, pt.Y); mnz = System.MathF.Min(mnz, pt.Z);
                    mxx = System.MathF.Max(mxx, pt.X); mxy = System.MathF.Max(mxy, pt.Y); mxz = System.MathF.Max(mxz, pt.Z);
                }
                if (tr.Texture != null) tex++;
            }
            System.Console.WriteLine($"  DL @0x{off:X4}: tris={tris.Count} (tex={tex}) X[{mnx:F0},{mxx:F0}] Y[{mny:F0},{mxy:F0}] Z[{mnz:F0},{mxz:F0}] size={mxx-mnx:F0}x{mxy-mny:F0}x{mxz-mnz:F0}");
        }
    }
}
