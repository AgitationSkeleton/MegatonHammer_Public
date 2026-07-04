using System.IO;
using System.Linq;
using System.Text;

namespace MegatonHammer.Editor;

/// <summary>
/// Portable, SHIPPED actor-param schemas. <see cref="ActorParamSchemaExtractor"/> derives named fields (and
/// enum dropdowns) from the decomp, but only when the decomp is present — a public checkout / end-user install
/// has none, so those actors would fall back to raw hex. This baker serialises the extractor's output to a
/// small text file under <c>Data/</c> that ships with the editor; the loader reads it at runtime so EVERY
/// decomp-derivable actor keeps friendly named fields with no decomp on disk. Regenerate with
/// <c>MegatonHammer --dumpschemas</c> whenever the extractor or decomp changes. Curated schemas still win;
/// the live extractor wins when the decomp IS present (freshest); this is the portable fallback tier.
///
/// Line format (one actor per line, tab-separated): <c>0xID  Title  field  field …</c> where each field is
/// <c>Name|Shift|Len|Kind|Flag|Role|EnumBase|Opt;Opt;…</c>.
/// </summary>
public static class BakedSchemas
{
    private const string Note = "Named fields derived from the decomp (shipped so they work with no decomp present).";

    public static string FileName(bool isOoT) => isOoT ? "actor-schemas-oot.txt" : "actor-schemas-mm.txt";

    // ── serialise (used by --dumpschemas) ──
    public static string Serialize(System.Collections.Generic.IReadOnlyDictionary<ushort, ActorParamSchema.Def> defs)
    {
        var sb = new StringBuilder();
        static string San(string s) => s.Replace('\t', ' ').Replace('|', '/').Replace(';', ',').Trim();
        foreach (var (id, def) in defs.OrderBy(kv => kv.Key))
        {
            sb.Append("0x").Append(id.ToString("X4")).Append('\t').Append(San(def.Title));
            foreach (var f in def.Fields)
            {
                string opts = f.Options == null ? "" : string.Join(';', f.Options.Select(San));
                sb.Append('\t')
                  .Append(San(f.Name)).Append('|').Append(f.Shift).Append('|').Append(f.Length).Append('|')
                  .Append(f.Kind).Append('|').Append(f.Flag).Append('|').Append(f.Role).Append('|')
                  .Append(f.EnumBase).Append('|').Append(opts);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ── deserialise + cache (runtime) ──
    private static readonly System.Collections.Generic.Dictionary<bool, System.Collections.Generic.IReadOnlyDictionary<ushort, ActorParamSchema.Def>> _cache = new();
    private static readonly object _gate = new();

    public static System.Collections.Generic.IReadOnlyDictionary<ushort, ActorParamSchema.Def> For(bool isOoT)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(isOoT, out var c)) return c;
            var built = Load(isOoT);
            _cache[isOoT] = built;
            return built;
        }
    }

    private static System.Collections.Generic.Dictionary<ushort, ActorParamSchema.Def> Load(bool isOoT)
    {
        var result = new System.Collections.Generic.Dictionary<ushort, ActorParamSchema.Def>();
        string path = Path.Combine(AppPaths.BaseDir, "Data", FileName(isOoT));
        if (!File.Exists(path)) return result;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cols = line.Split('\t');
                if (cols.Length < 3) continue;
                if (!int.TryParse(cols[0].Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out int id)) continue;
                var fields = new System.Collections.Generic.List<ActorParamSchema.Field>();
                for (int i = 2; i < cols.Length; i++)
                {
                    var p = cols[i].Split('|');
                    if (p.Length < 7) continue;
                    var opts = p.Length >= 8 && p[7].Length > 0 ? p[7].Split(';') : null;
                    fields.Add(new ActorParamSchema.Field(
                        p[0], int.Parse(p[1]), int.Parse(p[2]),
                        System.Enum.Parse<ActorParamSchema.FieldKind>(p[3]),
                        Options: opts,
                        Flag: System.Enum.Parse<ActorParamSchema.FlagKind>(p[4]),
                        Role: System.Enum.Parse<ActorParamSchema.FlagRole>(p[5]),
                        EnumBase: int.Parse(p[6])));
                }
                if (fields.Count > 0) result[(ushort)id] = new ActorParamSchema.Def(cols[1], fields, Note);
            }
        }
        catch { /* corrupt/partial baked file → just skip; curated still covers the important actors */ }
        return result;
    }
}
