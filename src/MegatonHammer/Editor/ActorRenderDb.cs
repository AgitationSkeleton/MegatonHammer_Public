using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MegatonHammer.Editor;

/// <summary>
/// Maps an actor (id + variable) to the object model that represents it in the editor,
/// from SharpOcarina's ActorRendering.xml. Each entry has a variable regex pattern, the
/// object name, a render scale, and optional display-list / texture offsets.
/// </summary>
public sealed class ActorRenderDb
{
    public sealed record Entry(
        ushort ActorId, string VarPattern, string ObjectName,
        float Scale, IReadOnlyList<int> DlOffsets, int DlCount, bool Animated, int Hierarchy,
        bool IgnoreYaw);

    private readonly List<Entry> _entries = [];

    public int Count => _entries.Count;

    public static ActorRenderDb Load(bool isOoT)
    {
        var db = new ActorRenderDb();
        string game = isOoT ? "OOT" : "MM";
        string? path = AppPaths.SourceFile("SharpOcarina-main", "XML", game, "ActorRendering.xml");
        if (path == null) return db;

        try
        {
            foreach (var el in XDocument.Load(path).Root!.Elements("Actor"))
            {
                ushort id = Convert.ToUInt16((string)el.Attribute("Key")!, 16);
                string var = (string?)el.Attribute("Var") ?? "....";
                string obj = el.Value.Trim();
                float scale = ParseFloat((string?)el.Attribute("Scale"), 0.01f);
                // "Offsets" lists several display lists (e.g. a tree's trunk + leaves); "Offset" is one.
                var offs = ParseHexList((string?)el.Attribute("Offsets"));
                if (offs.Count == 0 && ParseHexNullable((string?)el.Attribute("Offset")) is int single)
                    offs = [single];
                // "DListCount" = number of sequential display lists at Offset (e.g. tree trunk + leaves).
                int dlCount = int.TryParse((string?)el.Attribute("DListCount"), out int dc) && dc > 0 ? dc : Math.Max(1, offs.Count);
                bool anim = (string?)el.Attribute("Animated") == "1";
                int hier = int.TryParse((string?)el.Attribute("Hierarchy"), out int h) ? h : 0;
                // IgnoreRotation="x,y,z" (1 = don't apply that axis); we only apply yaw, so y matters.
                bool ignoreYaw = ((string?)el.Attribute("IgnoreRotation"))?.Split(',') is { Length: >= 2 } ir
                                 && ir[1].Trim() == "1";
                db._entries.Add(new Entry(id, var, obj, scale, offs, dlCount, anim, hier, ignoreYaw));
            }
        }
        catch { /* fall back to empty */ }
        return db;
    }

    /// <summary>Finds the render entry for an actor id + variable (first matching pattern).</summary>
    public Entry? Resolve(ushort actorId, ushort variable)
    {
        string varHex = variable.ToString("X4");
        foreach (var e in _entries)
        {
            if (e.ActorId != actorId) continue;
            try { if (Regex.IsMatch(varHex, e.VarPattern)) return e; }
            catch { return e; }   // malformed pattern → accept
        }
        return null;
    }

    private static float ParseFloat(string? s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    private static int? ParseHexNullable(string? s) =>
        s != null && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? v : null;

    private static List<int> ParseHexList(string? s)
    {
        var list = new List<int>();
        if (s != null)
            foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) list.Add(v);
        return list;
    }
}
