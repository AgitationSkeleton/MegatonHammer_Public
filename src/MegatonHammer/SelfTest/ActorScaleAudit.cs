using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Audits the decomp-derived per-actor draw scale for OoT and/or MM: prints the determined scale + its
/// source for every actor, and flags actors whose Actor_SetScale/.scale uses a non-literal argument (so
/// they fell back to the 0.01 engine default and may need a hand-checked override).
/// Run: MegatonHammer --scaleaudit [oot|mm|both] [all]
/// </summary>
public static class ActorScaleAudit
{
    public static void Run(string[] args)
    {
        bool all = args.Any(a => a.Equals("all", StringComparison.OrdinalIgnoreCase));
        bool oot = args.Any(a => a.Equals("oot", StringComparison.OrdinalIgnoreCase));
        bool mm  = args.Any(a => a.Equals("mm", StringComparison.OrdinalIgnoreCase));
        if (!oot && !mm) { oot = mm = true; }   // default: both

        if (oot) AuditGame("OoT", mm: false, all);
        if (mm)  AuditGame("MM",  mm: true,  all);
    }

    private static void AuditGame(string label, bool mm, bool all)
    {
        var rows = ActorScaleTable.Build(mm).Audit();
        Console.WriteLine($"\n================= {label} ACTOR SCALE AUDIT ({rows.Count} actors) =================");

        int def = rows.Count(r => Math.Abs(r.Scale - 0.01f) < 1e-6f && !r.NeedsReview);
        int explicitScale = rows.Count(r => !r.Source.StartsWith("0.01"));
        var review = rows.Where(r => r.NeedsReview).ToList();
        Console.WriteLine($"  explicit scale (literal):   {explicitScale}");
        Console.WriteLine($"  0.01 engine default:        {def}");
        Console.WriteLine($"  NEEDS REVIEW (non-literal): {review.Count}");

        // Distribution of explicit scales (the most common non-0.01 values).
        var dist = rows.Where(r => !r.Source.StartsWith("0.01"))
                       .GroupBy(r => r.Scale).OrderByDescending(g => g.Count()).Take(12);
        Console.WriteLine("  top explicit scale values: " +
            string.Join(", ", dist.Select(g => $"{g.Key:G}×{g.Count()}")));

        Console.WriteLine($"\n  --- NEEDS REVIEW (non-literal SetScale/.scale → defaulted to 0.01) ---");
        foreach (var r in review)
            Console.WriteLine($"    0x{r.Id:X3} {r.Name,-28} {r.Source}");

        if (all)
        {
            Console.WriteLine($"\n  --- ALL ACTORS (id, name, scale, source) ---");
            foreach (var r in rows)
                Console.WriteLine($"    0x{r.Id:X3} {r.Name,-28} {r.Scale,-10:G} {r.Source}");
        }
    }
}
