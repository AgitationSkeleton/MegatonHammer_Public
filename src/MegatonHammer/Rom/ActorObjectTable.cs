using System.Text.RegularExpressions;

namespace MegatonHammer.Rom;

/// <summary>
/// Maps an OoT actor id → the object (model) it loads, by reading the decomp: actor_table.h
/// gives id ↔ ACTOR_* enum, and each overlay's <c>ActorInit …_InitVars</c> gives ACTOR_* →
/// OBJECT_* (the 4th field). Combined, that's actor id → object name (e.g. 0x0000 → Player →
/// object_link_boy). Used to render placed actors as their real models (D5).
/// </summary>
public sealed partial class ActorObjectTable
{
    private readonly string _root;

    private ActorObjectTable(bool mm) =>
        _root = $@"D:\Copilot_OOT\READ_ONLY_SourceCodes\{(mm ? "mm-main" : "oot-master")}";

    private readonly Dictionary<int, string> _idToObject = [];   // actor id → object name (lowercase)

    public int Count => _idToObject.Count;

    /// <summary>Object name for an actor id (e.g. "object_link_boy"), or null.</summary>
    public string? ObjectFor(int actorId) => _idToObject.GetValueOrDefault(actorId);

    // group1 = id, group2 = actor NAME (e.g. Boss_Goma), group3 = ACTOR_* enum.
    [GeneratedRegex(@"/\*\s*0x([0-9A-Fa-f]+)\s*\*/\s*DEFINE_ACTOR\w*\(\s*(\w+)\s*,\s*(ACTOR_\w+)")]
    private static partial Regex ActorTableRegex();

    // group1 = InitVars name prefix (the actor name, e.g. En_Goma), group2 = struct body.
    [GeneratedRegex(@"ActorInit\s+(\w+)_InitVars\s*=\s*\{(.*?)\}", RegexOptions.Singleline)]
    private static partial Regex InitVarsRegex();

    [GeneratedRegex(@"(ACTOR_\w+)")] private static partial Regex ActorEnumRegex();
    [GeneratedRegex(@"(OBJECT_\w+)")] private static partial Regex ObjectEnumRegex();

    public static ActorObjectTable Build(bool mm = false)
    {
        var t = new ActorObjectTable(mm);
        try { t.Load(); } catch { }
        return t;
    }

    private void Load()
    {
        // 1. actor id from the actor table, keyed by BOTH the ACTOR_* enum and the actor NAME. The
        // name is the reliable key: some InitVars structs spoof the id field (En_Goma — baby Gohma —
        // sets it to ACTOR_BOSS_GOMA, which would otherwise overwrite Queen Gohma's object with the
        // baby's object_gol), but the InitVars VARIABLE name always matches the actor table name.
        var enumToId = new Dictionary<string, int>(StringComparer.Ordinal);
        var nameToId = new Dictionary<string, int>(StringComparer.Ordinal);
        string tablePath = Path.Combine(_root, "include", "tables", "actor_table.h");
        if (!File.Exists(tablePath)) return;
        foreach (var line in File.ReadLines(tablePath))
        {
            var m = ActorTableRegex().Match(line);
            if (!m.Success) continue;
            int id = Convert.ToInt32(m.Groups[1].Value, 16);
            nameToId[m.Groups[2].Value] = id;   // actor name (e.g. Boss_Goma)
            enumToId[m.Groups[3].Value] = id;   // ACTOR_* enum
        }

        // 2. <name>_InitVars → OBJECT_* from each overlay. The actor is identified by the InitVars
        // variable name (→ name table), not by the spoofable id field inside the struct.
        string overlays = Path.Combine(_root, "src", "overlays", "actors");
        if (!Directory.Exists(overlays)) return;
        foreach (var file in Directory.EnumerateFiles(overlays, "*.c", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            foreach (Match block in InitVarsRegex().Matches(text))
            {
                string name = block.Groups[1].Value;
                string body = block.Groups[2].Value;
                var oe = ObjectEnumRegex().Match(body);
                if (!oe.Success) continue;
                // Prefer the actor NAME → id (reliable); fall back to the struct's ACTOR_* id field.
                int id;
                if (!nameToId.TryGetValue(name, out id))
                {
                    var ae = ActorEnumRegex().Match(body);
                    if (!ae.Success || !enumToId.TryGetValue(ae.Groups[1].Value, out id)) continue;
                }

                // OBJECT_GAMEPLAY_KEEP and similar are "no specific model"; skip those.
                string objName = oe.Groups[1].Value.ToLowerInvariant();
                if (objName is "object_gameplay_keep" or "object_gameplay_field_keep" or "object_gameplay_dangeon_keep")
                    continue;
                _idToObject[id] = objName;
            }
        }
    }
}
