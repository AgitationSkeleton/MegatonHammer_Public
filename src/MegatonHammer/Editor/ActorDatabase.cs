using System.Xml.Linq;

namespace MegatonHammer.Editor;

public sealed class ActorDatabase
{
    public record ActorInfo(ushort Id, string Name, string? DebugName, IReadOnlyDictionary<ushort, string> Variables);

    private readonly Dictionary<ushort, ActorInfo> _actors = [];

    /// <summary>Megaton Hammer's own placeable custom actor: the "dialogue point" (En_MhTalk), now built into the
    /// SoH/2Ship playtest forks via actor_table.h. Game-specific ids: SoH assigns it the id right after the last
    /// vanilla OoT actor (Obj_Warp2block 0x01D6 → 0x01D7); 2Ship the id after the last vanilla MM actor
    /// (En_Rsn 0x2B1 → 0x2B2). (The old 0x0230 was a placeholder before the overlay existed.)</summary>
    public const ushort MhTalkIdOot = 0x01D7;
    public const ushort MhTalkIdMm  = 0x02B2;
    public static ushort MhTalkId(bool isOoT) => isOoT ? MhTalkIdOot : MhTalkIdMm;

    private void RegisterCustom(bool isOoT)
    {
        ushort id = MhTalkId(isOoT);
        _actors[id] = new ActorInfo(id, "Dialogue Point (En_MhTalk)", "En_MhTalk",
            new Dictionary<ushort, string>());
    }

    /// <summary>Corrections for inaccurate SharpOcarina actor names (decomp-verified). OoT actor ids; applied
    /// after the XML load so the shown name is right.</summary>
    private static readonly Dictionary<ushort, string> OotNameCorrections = new()
    {
        // SharpOcarina calls En_Kusa "Grass Clump, doesn't regrow", but z_en_kusa.c has EnKusa_Regrow /
        // EnKusa_CutWaitRegrow / EnKusa_SetupRegrow — the standard cuttable grass DOES regrow (a replenishable
        // Deku-Tree resource). Its own preset even reads "Cut-able, regenerating grass".
        [0x0125] = "Cuttable Grass / Bush (En_Kusa)",
    };

    public static ActorDatabase Load(bool isOoT)
    {
        var db   = new ActorDatabase();
        db.RegisterCustom(isOoT);   // always available, even on a public build with no XML DB
        string game = isOoT ? "OOT" : "MM";
        string? path = AppPaths.SourceFile("SharpOcarina-main", "XML", game, "ActorNames.xml");
        if (path == null) return db;   // no reference sources (e.g. public build) -> DB has just the custom actors

        try
        {
            var root = XDocument.Load(path).Root!;
            foreach (var el in root.Elements("Actor"))
            {
                string keyStr = (string)el.Attribute("Key")!;
                ushort id   = Convert.ToUInt16(keyStr, 16);
                string name = (string?)el.Attribute("Name") ?? $"Actor_{id:X4}";
                string? dbg = (string?)el.Attribute("DebugName");

                var vars = new Dictionary<ushort, string>();
                foreach (var v in el.Elements("Variable"))
                {
                    ushort vid = Convert.ToUInt16((string)v.Attribute("Var")!, 16);
                    vars[vid]  = v.Value.Trim();
                }

                db._actors[id] = new ActorInfo(id, name, dbg, vars);
            }
        }
        catch { /* silently fall back to empty database */ }

        // Fix inaccurate SharpOcarina names (decomp-verified). OoT-only: the ids are OoT actor ids.
        if (isOoT)
            foreach (var (id, name) in OotNameCorrections)
                if (db._actors.TryGetValue(id, out var info))
                    db._actors[id] = info with { Name = name };

        return db;
    }

    public ActorInfo? Get(ushort id) => _actors.GetValueOrDefault(id);

    public string GetName(ushort id)
        => _actors.TryGetValue(id, out var info) ? info.Name : $"Actor_{id:X4}";

    public string GetVariableName(ushort actorId, ushort variable)
    {
        if (_actors.TryGetValue(actorId, out var info) &&
            info.Variables.TryGetValue(variable, out var varName))
            return varName;
        return $"0x{variable:X4}";
    }

    public IEnumerable<ActorInfo> All => _actors.Values.OrderBy(a => a.Id);

    public int Count => _actors.Count;
}
