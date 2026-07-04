using System.Globalization;
using System.Text.RegularExpressions;

namespace MegatonHammer.Editor;

/// <summary>
/// Auto-derives an actor-param schema from the decomp for actors that don't have a hand-curated
/// <see cref="ActorParamSchema"/> entry, so the editor can present their packed <c>params</c> as NAMED
/// fields (no raw hex) for every actor — not just the curated few. The field names come from the decomp's
/// own <c>&lt;PREFIX&gt;_GET_&lt;FIELD&gt;(thisx)</c> param macros (e.g. <c>DOORSHUTTER_GET_TYPE</c>,
/// <c>IK_GET_SWITCH_FLAG</c>) and inline <c>PARAMS_GET_U/S(thisx-&gt;params, shift, len)</c> calls, so the
/// labels are the game's own (no guessing). Switch/chest/collectible fields are classified by name so they
/// wire into the flag-connections view. Curated schemas always win; this only fills the gaps.
/// </summary>
public static partial class ActorParamSchemaExtractor
{
    private static readonly Dictionary<bool, IReadOnlyDictionary<ushort, ActorParamSchema.Def>> _cache = new();
    private static readonly object _gate = new();

    public static IReadOnlyDictionary<ushort, ActorParamSchema.Def> For(bool isOoT)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(isOoT, out var c)) return c;
            IReadOnlyDictionary<ushort, ActorParamSchema.Def> built;
            try { built = Build(isOoT); } catch { built = new Dictionary<ushort, ActorParamSchema.Def>(); }
            _cache[isOoT] = built;
            return built;
        }
    }

    // <PREFIX>_GET_<FIELD>(arg)  (((arg)->params >> SHIFT) & MASK)
    [GeneratedRegex(@"#define\s+\w+_GET_(\w+)\s*\([^)]*\)\s*\(\(\(\w+\)->params\s*>>\s*(\d+)\)\s*&\s*(0[xX][0-9A-Fa-f]+|\d+)\)")]
    private static partial Regex GetMacroShiftMask();

    // <PREFIX>_GET_<FIELD>(arg)  (((arg)->params & MASK) >> SHIFT)
    [GeneratedRegex(@"#define\s+\w+_GET_(\w+)\s*\([^)]*\)\s*\(\(\(\w+\)->params\s*&\s*(0[xX][0-9A-Fa-f]+)\)\s*>>\s*(\d+)\)")]
    private static partial Regex GetMacroMaskShift();

    // <lhs> = PARAMS_GET_U/S(thisx->params, shift, len)  — modern OoT inline form; name from the LHS var.
    [GeneratedRegex(@"(\w+)\s*=\s*PARAMS_GET_[US]\(\s*\w+->params\s*,\s*(\d+)\s*,\s*(\d+)\s*\)")]
    private static partial Regex AssignParamsGet();

    // <lhs> = (thisx->params >> shift) & mask  /  thisx->params & mask  — older inline form.
    [GeneratedRegex(@"(\w+)\s*=\s*\(?\s*\w+->params\s*>>\s*(\d+)\s*\)?\s*&\s*(0[xX][0-9A-Fa-f]+|\d+)")]
    private static partial Regex AssignShiftMask();

    [GeneratedRegex(@"ActorInit\s+(\w+)_InitVars\s*=")]
    private static partial Regex InitVarsRegex();

    [GeneratedRegex(@"/\*\s*0x([0-9A-Fa-f]+)\s*\*/\s*DEFINE_ACTOR\w*\(\s*(\w+)\s*,")]
    private static partial Regex ActorTableRegex();

    private static Dictionary<ushort, ActorParamSchema.Def> Build(bool isOoT)
    {
        string? root = AppPaths.SourceDir(isOoT ? "oot-master" : "mm-main");
        var result = new Dictionary<ushort, ActorParamSchema.Def>();
        if (root == null) return result;   // no decomp sources (e.g. public build) -> curated schema only

        // name → id
        var nameToId = new Dictionary<string, int>(StringComparer.Ordinal);
        string tablePath = Path.Combine(root, "include", "tables", "actor_table.h");
        if (!File.Exists(tablePath)) return result;
        foreach (var line in File.ReadLines(tablePath))
        {
            var m = ActorTableRegex().Match(line);
            if (m.Success) nameToId[m.Groups[2].Value] = Convert.ToInt32(m.Groups[1].Value, 16);
        }

        string overlays = Path.Combine(root, "src", "overlays", "actors");
        if (!Directory.Exists(overlays)) return result;

        foreach (var dir in Directory.EnumerateDirectories(overlays))
        {
            // The actor id from this overlay's InitVars (scan its .c files).
            int id = -1; string actorName = "";
            foreach (var c in Directory.EnumerateFiles(dir, "*.c"))
            {
                string txt; try { txt = File.ReadAllText(c); } catch { continue; }
                var iv = InitVarsRegex().Match(txt);
                if (iv.Success && nameToId.TryGetValue(iv.Groups[1].Value, out id)) { actorName = iv.Groups[1].Value; break; }
            }
            if (id < 0) continue;

            // Collect named bit-fields from this overlay's headers + sources.
            var fields = new List<ActorParamSchema.Field>();
            var seen = new HashSet<(int, int)>();
            void AddField(string name, int shift, int len)
            {
                if (len <= 0 || len > 16 || shift < 0 || shift + len > 16) return;
                if (!seen.Add((shift, len))) return;   // de-dupe identical slices
                var (kind, flag, role) = Classify(name, len);
                fields.Add(new ActorParamSchema.Field(Pretty(name), shift, len, kind, Flag: flag, Role: role));
            }

            foreach (var f in Directory.EnumerateFiles(dir, "*.*").Where(p => p.EndsWith(".h") || p.EndsWith(".c")))
            {
                string txt; try { txt = File.ReadAllText(f); } catch { continue; }
                foreach (Match m in GetMacroShiftMask().Matches(txt))
                    AddField(m.Groups[1].Value, int.Parse(m.Groups[2].Value), MaskLen(ParseInt(m.Groups[3].Value)));
                foreach (Match m in GetMacroMaskShift().Matches(txt))
                {
                    uint mask = (uint)ParseInt(m.Groups[2].Value); int sh = int.Parse(m.Groups[3].Value);
                    AddField(m.Groups[1].Value, sh, MaskLen((int)(mask >> sh)));
                }
                // Modern OoT inline assignments give the field its variable name (this->switchFlag = …).
                foreach (Match m in AssignParamsGet().Matches(txt))
                {
                    if (IsGenericName(m.Groups[1].Value)) continue;
                    AddField(m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                }
                foreach (Match m in AssignShiftMask().Matches(txt))
                {
                    if (IsGenericName(m.Groups[1].Value)) continue;
                    AddField(m.Groups[1].Value, int.Parse(m.Groups[2].Value), MaskLen(ParseInt(m.Groups[3].Value)));
                }
            }

            if (fields.Count == 0) continue;
            fields.Sort((a, b) => b.Shift.CompareTo(a.Shift));   // high bits first (type/subtype before flags)
            result[(ushort)id] = new ActorParamSchema.Def(Pretty(actorName),
                fields, "Auto-derived from the decomp param macros — field names are the game's own.");
        }
        return result;
    }

    // Generic / non-descriptive LHS names that wouldn't make a useful field label.
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
        { "params", "param", "temp", "tmp", "var", "val", "value", "v", "i", "j", "k", "n", "ret",
          "phi", "sp", "pad", "this", "thisx", "actor", "data", "x", "y", "z", "result", "arg" };
    private static bool IsGenericName(string s) => s.Length <= 1 || Generic.Contains(s) || s.StartsWith("phi_");

    private static int ParseInt(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(s[2..], NumberStyles.HexNumber) : int.Parse(s);

    // Contiguous-mask bit length (0xF→4, 0x3F→6, 0xFF→8). Non-contiguous → highest set bit count, best effort.
    private static int MaskLen(int postShiftMask)
    {
        if (postShiftMask <= 0) return 0;
        int len = 0; int m = postShiftMask;
        while (((m >> len) & 1) == 1 && len < 16) len++;
        return len;
    }

    private static (ActorParamSchema.FieldKind kind, ActorParamSchema.FlagKind flag, ActorParamSchema.FlagRole role)
        Classify(string rawName, int len)
    {
        string u = rawName.ToUpperInvariant();
        if (u.Contains("SWITCH") && u.Contains("FLAG"))
            return (ActorParamSchema.FieldKind.Int, ActorParamSchema.FlagKind.Switch, ActorParamSchema.FlagRole.Both);
        if ((u.Contains("CHEST") || u.Contains("TREASURE")) && u.Contains("FLAG"))
            return (ActorParamSchema.FieldKind.Int, ActorParamSchema.FlagKind.Chest, ActorParamSchema.FlagRole.Both);
        if (u.Contains("COLLECTIBLE") && u.Contains("FLAG"))
            return (ActorParamSchema.FieldKind.Int, ActorParamSchema.FlagKind.Collectible, ActorParamSchema.FlagRole.Both);
        return (len == 1 ? ActorParamSchema.FieldKind.Flag : ActorParamSchema.FieldKind.Int,
                ActorParamSchema.FlagKind.None, ActorParamSchema.FlagRole.None);
    }

    // "SWITCH_FLAG" → "Switch flag"; "EN_BOX" → "En box". Decomp name, just title-cased.
    private static string Pretty(string raw)
    {
        var words = raw.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select((w, i) =>
            i == 0 ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w.ToLowerInvariant()));
    }
}
