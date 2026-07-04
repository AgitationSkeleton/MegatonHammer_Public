using System.Xml.Linq;

namespace MegatonHammer.Editor;

public sealed class ActorDatabase
{
    public record ActorInfo(ushort Id, string Name, string? DebugName, IReadOnlyDictionary<ushort, string> Variables);

    private readonly Dictionary<ushort, ActorInfo> _actors = [];

    /// <summary>Megaton Hammer's own placeable custom actor: the portable "dialogue point" (En_MhTalk).
    /// Register the matching overlay at this id in your base (see portable/README.md).</summary>
    public const ushort MhTalkId = 0x0230;

    private void RegisterCustom()
    {
        _actors[MhTalkId] = new ActorInfo(MhTalkId, "Dialogue Point (En_MhTalk)", "En_MhTalk",
            new Dictionary<ushort, string>());
    }

    public static ActorDatabase Load(bool isOoT)
    {
        var db   = new ActorDatabase();
        db.RegisterCustom();   // always available, even on a public build with no XML DB
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
