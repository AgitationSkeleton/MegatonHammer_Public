namespace MegatonHammer.Editor;

/// <summary>
/// The Zelda 64 analogue of Hammer's "Check for Problems": scans the scene for likely mistakes —
/// missing Link spawn, unknown/obsolete actors, conflicting switch-flag setters, dangling flags,
/// empty rooms, void-out triggers, and high actor counts. Each problem can carry a target actor/brush
/// so the report can jump to it.
/// </summary>
public static class SceneValidator
{
    public enum Severity { Error, Warning, Info }

    public sealed record Problem(Severity Level, string Message, object? Target = null);

    private const ushort ActorPlayer = 0x0000;
    private const int HighActorCount = 64;   // soft per-room threshold

    public static List<Problem> Check(MapDocument doc, bool isOoT)
    {
        var problems = new List<Problem>();
        var actors = doc.AllActors.ToList();
        var solids = doc.Solids.ToList();

        // Link spawn point.
        if (!actors.Any(a => a.Number == ActorPlayer))
            problems.Add(new Problem(Severity.Warning, "No Link spawn point (actor 0x0000) placed — the scene has no entry position."));

        // Unknown / obsolete actors (imported ids the editor doesn't recognise).
        foreach (var a in actors.Where(a => a.IsObsolete))
            problems.Add(new Problem(Severity.Warning, $"Unknown/obsolete actor 0x{a.Number:X4} at ({a.XPos:0}, {a.YPos:0}, {a.ZPos:0}) — preserved but not editable.", a));

        // Flag connections: conflicting setters and dangling flags.
        foreach (var g in FlagConnectionAnalyzer.Analyze(actors, isOoT))
        {
            int setters = g.Users.Count(u => u.Role is ActorParamSchema.FlagRole.Setter or ActorParamSchema.FlagRole.Both);
            if (g.Kind == ActorParamSchema.FlagKind.Switch && setters > 1)
                problems.Add(new Problem(Severity.Warning,
                    $"Switch flag {g.Index} is set by {setters} actors — usually a bug (one flag, multiple setters).",
                    g.Users.First().Actor));
            // Duplicate chest/treasure flag: two chests on the same flag share opened-state (opening one opens
            // both). Surfaced here since the connection overlay no longer draws chest self-state as a wire.
            if (g.Kind == ActorParamSchema.FlagKind.Chest && g.Users.Select(u => u.Actor).Distinct().Count() > 1)
                problems.Add(new Problem(Severity.Warning,
                    $"Chest flag {g.Index} is shared by {g.Users.Select(u => u.Actor).Distinct().Count()} chests — give each chest a unique treasure flag (opening one would open both).",
                    g.Users.First().Actor));
            if (g.HasSetter ^ g.HasReader)
                problems.Add(new Problem(Severity.Info,
                    $"{KindName(g.Kind)} flag {g.Index} is {(g.HasSetter ? "set but never read" : "read but never set")} — possible dangling logic.",
                    g.Users.First().Actor));
        }

        // Per-room checks.
        foreach (var room in doc.Scene.Rooms)
        {
            int actorCount = room.Actors.Count;
            if (room.Geometry.Count == 0)
                problems.Add(new Problem(Severity.Warning, $"Room \"{room.Name}\" has no geometry."));
            if (actorCount > HighActorCount)
                problems.Add(new Problem(Severity.Info, $"Room \"{room.Name}\" has {actorCount} actors (> {HighActorCount}) — may exceed the game's actor budget."));
        }

        // Void-out triggers (no destination entrance) — usually intentional, surfaced as info.
        int voidTriggers = solids.Count(s => s.IsTrigger && s.ExitEntrance < 0);
        if (voidTriggers > 0)
            problems.Add(new Problem(Severity.Info, $"{voidTriggers} trigger volume(s) have no destination (void out)."));

        if (problems.Count == 0)
            problems.Add(new Problem(Severity.Info, "No problems found."));

        return problems.OrderBy(p => p.Level).ToList();
    }

    private static string KindName(ActorParamSchema.FlagKind k) => k switch
    {
        ActorParamSchema.FlagKind.Switch => "Switch",
        ActorParamSchema.FlagKind.Chest => "Chest",
        ActorParamSchema.FlagKind.Collectible => "Collectible",
        _ => "Scene",
    };
}
