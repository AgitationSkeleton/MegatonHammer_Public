using System.Text;
using MegatonHammer.Editor;
using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>
/// #7: model-resolution coverage. For every actor id known to the decomp, attempts to resolve a 3D
/// model and reports which actors render as a real model vs fall back to a billboard/point, plus the
/// resolved object + scale + triangle count. The "NO MODEL" list is the gap list to work through.
/// Run: MegatonHammer --modelaudit [oot|mm|both] [romPath]
/// </summary>
public static class ModelAudit
{
    public static void Run(string[] a)
    {
        string which = a.Length >= 2 ? a[1].ToLowerInvariant() : "both";
        if (which is "oot" or "both") One(false, a);
        if (which is "mm" or "both") One(true, a);
    }

    private static void One(bool mm, string[] a)
    {
        string? romPath = a.Length >= 3 ? a[2]
            : mm ? @"D:\Copilot_OOT\WorkFolders\MegatonHammer\2Ship\OTRExporter\mm.z64"
                 : @"D:\Copilot_OOT\READ_ONLY_GameROMs\ZELOOTMA.Z64";
        string label = mm ? "MM" : "OoT";
        Console.WriteLine($"\n================  MODEL COVERAGE: {label}  ================");
        RomImage rom;
        try { rom = new RomImage(romPath); }
        catch (Exception ex) { Console.WriteLine($"  cannot open ROM {romPath}: {ex.Message}"); return; }

        var resolver = new ActorModelResolver(rom);
        var db = ActorDatabase.Load(isOoT: !mm);

        int modelled = 0, noModel = 0;
        var sb = new StringBuilder();
        var gaps = new List<string>();
        foreach (var info in db.All)
        {
            var actor = new ZActor { Number = info.Id };
            ActorModelResolver.Model? m;
            try { m = resolver.Resolve(actor, adult: true); }
            catch { m = null; }
            int tris = m?.Tris.Count ?? 0;
            if (tris > 0) modelled++;
            else { noModel++; gaps.Add($"0x{info.Id:X4}  {info.Name}"); }
        }

        Console.WriteLine($"  actors: {db.Count}   modelled: {modelled}   NO MODEL (billboard/point): {noModel}");
        sb.AppendLine($"# {label} model-coverage gaps ({noModel}/{db.Count} render as billboard/point)\n");
        foreach (var g in gaps) sb.AppendLine(g);
        string outPath = Path.Combine(AppContext.BaseDirectory, $"modelaudit_{label.ToLowerInvariant()}.txt");
        try { File.WriteAllText(outPath, sb.ToString()); Console.WriteLine($"  wrote {outPath}"); }
        catch (Exception ex) { Console.WriteLine($"  (could not write: {ex.Message})"); }
    }
}
