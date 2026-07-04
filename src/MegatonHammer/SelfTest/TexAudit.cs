using MegatonHammer.Editor;
using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>--texaudit [mm] : resolves EVERY actor id (var 0) and reports those whose model has untextured
/// triangles — a full-coverage texture-completeness sweep (the gallery only covers a curated subset). Prints
/// worst-first with the null reason (noimg/fmt/dims) + which SETTIMG segments the actor uses, so the gap can
/// be traced (unbound seg8-D face texture, undecodable format, material-DL-only texture, …).</summary>
public static class TexAudit
{
    private static readonly string Oot = Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
    private static readonly string Mm  = Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).z64");

    public static void Run(string[] args)
    {
        bool mm = args.Length >= 2 && args[1].Equals("mm", System.StringComparison.OrdinalIgnoreCase);
        string path = mm ? Mm : Oot;
        if (!System.IO.File.Exists(path)) { System.Console.WriteLine($"[texaudit] ROM not found: {path}"); return; }
        var rom = new RomImage(path);
        var resolver = new ActorModelResolver(rom);
        var names = ActorObjectTable.Build(mm);   // id → object (for a rough label)

        var rows = new System.Collections.Generic.List<(int id, int tex, int notex, int noimg, int fmt, int dims, string segs)>();
        int modeled = 0, fullyTex = 0;
        for (int id = 0; id <= 0x1D7; id++)
        {
            var za = new ZActor { Number = (ushort)id, Variable = 0, XPos = 0, YPos = 0, ZPos = 0 };
            ObjectModelReader.ResetTexDiag();
            object? model;
            try { model = resolver.Resolve(za, adult: true); } catch { continue; }
            if (model == null) continue;   // billboard / no model — no texture to miss
            modeled++;
            int tex = ObjectModelReader.DiagTexTris, notex = ObjectModelReader.DiagNoTexTris;
            if (notex == 0) { fullyTex++; continue; }
            var d = ObjectModelReader.DiagTimgSegs;
            string segs = d.Count == 0 ? "-" : string.Join(",", System.Linq.Enumerable.Select(
                System.Linq.Enumerable.OrderBy(d, kv => kv.Key), kv => $"seg{kv.Key}:{kv.Value}"));
            rows.Add((id, tex, notex, ObjectModelReader.DiagNullNoTex, ObjectModelReader.DiagNullFmt, ObjectModelReader.DiagNullDims, segs));
        }

        rows.Sort((a, b) => b.notex - a.notex);
        System.Console.WriteLine($"[texaudit] {(mm ? "MM" : "OoT")}: {modeled} modeled actors, {fullyTex} fully textured, {rows.Count} with untextured tris:");
        foreach (var r in rows)
        {
            string obj = names.ObjectFor(r.id) ?? "?";
            string frac = r.tex + r.notex == 0 ? "0%" : $"{100.0 * r.notex / (r.tex + r.notex):F0}% untex";
            System.Console.WriteLine($"  0x{r.id:X4} {obj,-24} tex/notex={r.tex}/{r.notex} ({frac}) null[noimg={r.noimg} fmt={r.fmt} dims={r.dims}] segs={{{r.segs}}}");
        }
    }
}
