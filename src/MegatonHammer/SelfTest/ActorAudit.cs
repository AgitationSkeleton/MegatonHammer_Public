using System.Text;
using MegatonHammer.Editor;
using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Whole-game actor/gimmick coverage audit. Walks every scene/room of OoT and MM, collects the
/// distinct set of actor types actually placed, and cross-references them against the editor's
/// actor name database (does it show a real name vs. "Obsolete 0xNNNN"?) and the typed param-schema
/// table (does it get Hammer-style SmartEdit fields for its switch/chest/flag logic?). Reports every
/// gap so nothing the games use is invisible or uneditable in the editor.
/// Run: MegatonHammer --actoraudit [oot|mm|both]
/// </summary>
public static class ActorAudit
{
    private const string OotRom = @"D:\Copilot_OOT\READ_ONLY_GameROMs\Legend of Zelda, The - Ocarina of Time (USA).z64";
    private const string MmRom  = @"D:\Copilot_OOT\READ_ONLY_GameROMs\Legend of Zelda, The - Majora's Mask (USA).z64";

    public static void Run(string[] a)
    {
        string mode = a.Length >= 2 ? a[1].ToLowerInvariant() : "both";
        var sb = new StringBuilder();
        if (mode != "mm")  AuditGame(sb, "OoT (SoH)",  OotRom, isOoT: true);
        if (mode != "oot") AuditGame(sb, "MM (2Ship)", MmRom,  isOoT: false);

        try
        {
            string outPath = Path.Combine(AppContext.BaseDirectory, "actor-coverage.txt");
            File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine($"\nWrote {outPath}");
        }
        catch (Exception ex) { Console.WriteLine($"  (could not write report: {ex.Message})"); }
    }

    private static void AuditGame(StringBuilder sb, string label, string romPath, bool isOoT)
    {
        Console.WriteLine($"\n================  ACTOR COVERAGE: {label}  ================");
        RomImage rom;
        try { rom = new RomImage(romPath); }
        catch (Exception ex) { Console.WriteLine($"  cannot open ROM: {ex.Message}"); return; }

        var db = ActorDatabase.Load(isOoT);
        IEnumerable<int> ids = rom.Game == RomGame.MM
            ? MmSceneFiles.All.Select(t => t.Id)
            : Enumerable.Range(0, 128).Where(OotSceneFiles.IsValid);

        var usage    = new Dictionary<ushort, int>();             // actor id -> # placements
        var inScenes = new Dictionary<ushort, HashSet<int>>();    // actor id -> distinct scene ids
        int scenesWalked = 0;

        foreach (int id in ids)
        {
            ImportedScene? s;
            try { s = SceneImporter.Import(rom, id); } catch { continue; }
            if (s == null) continue;
            scenesWalked++;

            void Tally(ushort aid)
            {
                usage[aid] = usage.GetValueOrDefault(aid) + 1;
                if (!inScenes.TryGetValue(aid, out var hs)) inScenes[aid] = hs = [];
                hs.Add(id);
            }
            foreach (var r in s.Rooms) foreach (var act in r.Actors) Tally(act.Id);
            foreach (var t in s.Transitions) Tally(t.Id);
        }

        int distinct = usage.Count;
        var unnamed = usage.Keys.Where(k => db.Get(k) == null).OrderByDescending(k => usage[k]).ToList();
        int named = distinct - unnamed.Count;
        int schemad = usage.Keys.Count(k => ActorParamSchema.Has(isOoT, k));

        // Gimmick actors: those whose params drive logic (doors, switches, chests, water, flags). If
        // a frequently-placed one lacks a schema, flag it as a SmartEdit gap worth a typed schema.
        var noSchema = usage.Keys.Where(k => db.Get(k) != null && !ActorParamSchema.Has(isOoT, k))
                                 .OrderByDescending(k => usage[k]).ToList();

        sb.AppendLine($"==================== ACTOR COVERAGE: {label} ====================");
        sb.AppendLine($"scenes walked: {scenesWalked}");
        sb.AppendLine($"distinct actor types placed: {distinct}");
        sb.AppendLine($"  with editor name (ActorDatabase): {named}/{distinct}");
        sb.AppendLine($"  with typed param schema (SmartEdit): {schemad}/{distinct}");
        sb.AppendLine();

        if (unnamed.Count == 0)
            sb.AppendLine("-- UNNAMED actor ids: none — every placed actor resolves to a real editor name. --");
        else
        {
            sb.AppendLine($"-- UNNAMED actor ids ({unnamed.Count}) — these render as 'Obsolete 0xNNNN' in the editor --");
            foreach (var k in unnamed)
                sb.AppendLine($"   0x{k:X4}  uses={usage[k],4}  scenes={inScenes[k].Count}");
        }
        sb.AppendLine();

        sb.AppendLine($"-- top placed actors WITHOUT a typed param schema ({noSchema.Count}) — SmartEdit candidates --");
        foreach (var k in noSchema.Take(40))
            sb.AppendLine($"   0x{k:X4}  uses={usage[k],4}  {db.Get(k)?.Name}");
        sb.AppendLine();

        sb.AppendLine("-- all placed actor types (by usage) --");
        sb.AppendLine("   id      uses scenes  name                                    schema");
        foreach (var k in usage.Keys.OrderByDescending(k => usage[k]).ThenBy(k => k))
        {
            var info = db.Get(k);
            bool sch = ActorParamSchema.Has(isOoT, k);
            sb.AppendLine($"   0x{k:X4} {usage[k],6} {inScenes[k].Count,6}  {(info?.Name ?? "(unknown)"),-40}{(sch ? "yes" : "")}");
        }
        sb.AppendLine();

        Console.WriteLine($"  scenes={scenesWalked} distinct={distinct} named={named} " +
                          $"schema={schemad} unnamed={unnamed.Count} noSchema={noSchema.Count}");
    }
}
