using System.Globalization;
using System.Text.RegularExpressions;

namespace MegatonHammer.Rom;

/// <summary>
/// #7: ground-truth per-actor draw scale, parsed from the decomp. Each actor's overlay calls
/// <c>Actor_SetScale(&amp;this-&gt;actor, X)</c> (or sets actor.scale directly) with the object→world
/// scale the game uses; that is the correct render scale for the actor's real object. The editor
/// previously defaulted everything to 0.01 (right for the ~100×-oversized humanoid objects but wrong
/// for world-sized Bg/scenery actors, which then drew as a dot — e.g. Bg_Gjyo_Bridge). This reads the
/// real value so each actor renders at its in-game size. Bg_* actors with no SetScale call are treated
/// as world-sized (1.0). Mirrors <see cref="ActorObjectTable"/>'s decomp parsing.
/// </summary>
public sealed partial class ActorScaleTable
{
    private readonly string _root;
    private readonly bool _mm;

    private ActorScaleTable(bool mm)
    {
        _mm = mm;
        _root = $@"D:\Copilot_OOT\READ_ONLY_SourceCodes\{(mm ? "mm-main" : "oot-master")}";
    }

    private readonly Dictionary<int, float> _idToScale = [];

    public int Count => _idToScale.Count;

    /// <summary>The decomp draw scale for an actor id, or null if none was found.</summary>
    public float? ScaleFor(int actorId) => _idToScale.TryGetValue(actorId, out float s) ? s : null;

    [GeneratedRegex(@"/\*\s*0x([0-9A-Fa-f]+)\s*\*/\s*DEFINE_ACTOR\w*\(\s*(\w+)\s*,")]
    private static partial Regex ActorTableRegex();

    [GeneratedRegex(@"ActorInit\s+(\w+)_InitVars\s*=")]
    private static partial Regex InitVarsRegex();

    // Actor_SetScale(&<actor>, <expr>) — group1 = the actor expression (to reject indexed-child actors),
    // group2 = the scale expr (captured for literal evaluation).
    [GeneratedRegex(@"Actor_SetScale\(\s*(&[^,]+),\s*([^)]+)\)")]
    private static partial Regex SetScaleRegex();

    // <actor>.scale.x = <expr>;  (some actors set the components directly instead of Actor_SetScale).
    // group1 = the LHS actor expression, group2 = the value.
    [GeneratedRegex(@"([A-Za-z_][\w.>\[\]\-]*)\.scale\.[xyz]\s*=\s*([^;]+);")]
    private static partial Regex ScaleFieldRegex();

    // True if a scale-assignment target is a CHILD/sub-actor the overlay spawns and scales (e.g. a
    // shopkeeper's displayed items: `this->items[i]->actor.scale.x = 0.2f`), NOT the actor's own scale.
    // Such indexed assignments are the wares' scale, not the NPC's — reading them made En_Fsn / En_Trt /
    // En_Sob1 render 20x oversized. The actor's OWN scale is never array-indexed (always this->actor /
    // this->dyna.actor / thisx), so an '[' in the target marks a child to skip.
    private static bool IsChildScaleTarget(string lhs) => lhs.Contains('[');

    // InitChain uniform scale: ICHAIN_VEC3F_DIV1000(scale, N, ...) sets scale = N/1000. Many Bg_*/scenery
    // actors set their scale ONLY this way (no Actor_SetScale), e.g. Bg_Ctower_Rot (N=100 -> 0.1).
    [GeneratedRegex(@"ICHAIN_VEC3F_DIV1000\(\s*scale\s*,\s*(\d+)")]
    private static partial Regex InitChainScaleRegex();

    public static ActorScaleTable Build(bool mm = false)
    {
        var t = new ActorScaleTable(mm);
        try { t.Load(); } catch { }
        return t;
    }

    private void Load()
    {
        var nameToId = new Dictionary<string, int>(StringComparer.Ordinal);
        string tablePath = Path.Combine(_root, "include", "tables", "actor_table.h");
        if (!File.Exists(tablePath)) return;
        foreach (var line in File.ReadLines(tablePath))
        {
            var m = ActorTableRegex().Match(line);
            if (m.Success) nameToId[m.Groups[2].Value] = Convert.ToInt32(m.Groups[1].Value, 16);
        }

        string overlays = Path.Combine(_root, "src", "overlays", "actors");
        if (!Directory.Exists(overlays)) return;
        foreach (var file in Directory.EnumerateFiles(overlays, "*.c", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }

            // The actor this overlay defines (by its InitVars variable name → id).
            var iv = InitVarsRegex().Match(text);
            if (!iv.Success || !nameToId.TryGetValue(iv.Groups[1].Value, out int id)) continue;

            // Prefer Actor_SetScale; else a direct .scale.x = literal assignment. Take the FIRST literal
            // value found (Init-time), which is the spawn/draw scale for the overwhelming majority.
            float? scale = null;
            foreach (Match sm in SetScaleRegex().Matches(text))
                if (!IsChildScaleTarget(sm.Groups[1].Value) && TryEvalLiteral(sm.Groups[2].Value, out float v)) { scale = v; break; }
            // InitChain scale (Bg_*/scenery actors that never call Actor_SetScale, e.g. the clock tower 0.1).
            if (scale == null)
            {
                var im = InitChainScaleRegex().Match(text);
                if (im.Success && int.TryParse(im.Groups[1].Value, out int div) && div > 0) scale = div / 1000f;
            }
            if (scale == null)
                foreach (Match sm in ScaleFieldRegex().Matches(text))
                    if (!IsChildScaleTarget(sm.Groups[1].Value) && TryEvalLiteral(sm.Groups[2].Value, out float v) && v > 0f) { scale = v; break; }

            // GROUND TRUTH: every actor's scale defaults to 0.01 — Actor_Init() calls
            // Actor_SetScale(actor, 0.01f) for EVERY actor before its own Init runs, and Actor_Draw()
            // applies Matrix_Scale(actor->scale) before the actor's draw(). So an actor that never calls
            // Actor_SetScale renders at 0.01, NOT world scale. (The old "Bg_/Obj_ with no SetScale -> 1.0"
            // assumption made every no-SetScale scenery actor 100x too big, e.g. Bg_Ctower_Rot / the
            // North Clock Town towers.) Only an explicit Actor_SetScale / .scale assignment overrides 0.01.
            scale ??= 0.01f;

            if (scale is > 0f) _idToScale[id] = scale.Value;
        }
    }

    public sealed record AuditRow(int Id, string Name, float Scale, string Source, bool NeedsReview);

    /// <summary>
    /// Per-actor scale audit for review: for every actor overlay, the determined draw scale + WHERE it
    /// came from (Actor_SetScale literal / .scale assignment / 0.01 engine default), flagging actors whose
    /// scale call uses a NON-literal (param/macro/variable) argument we couldn't evaluate — those default
    /// to 0.01 and may need a hand-checked override. Mirrors Load()'s parse with source tracking.
    /// </summary>
    public List<AuditRow> Audit()
    {
        var rows = new List<AuditRow>();
        var nameToId = new Dictionary<string, int>(StringComparer.Ordinal);
        string tablePath = Path.Combine(_root, "include", "tables", "actor_table.h");
        if (!File.Exists(tablePath)) return rows;
        foreach (var line in File.ReadLines(tablePath))
        {
            var m = ActorTableRegex().Match(line);
            if (m.Success) nameToId[m.Groups[2].Value] = Convert.ToInt32(m.Groups[1].Value, 16);
        }
        string overlays = Path.Combine(_root, "src", "overlays", "actors");
        if (!Directory.Exists(overlays)) return rows;
        foreach (var file in Directory.EnumerateFiles(overlays, "*.c", SearchOption.AllDirectories))
        {
            string text; try { text = File.ReadAllText(file); } catch { continue; }
            var iv = InitVarsRegex().Match(text);
            if (!iv.Success || !nameToId.TryGetValue(iv.Groups[1].Value, out int id)) continue;
            string name = iv.Groups[1].Value;

            float? scale = null; string source = "0.01 (engine default — no SetScale)"; bool review = false;
            // Track whether a SetScale/scale assignment EXISTS but was non-literal (couldn't evaluate).
            bool sawSetScale = false, sawScaleField = false;
            foreach (Match sm in SetScaleRegex().Matches(text))
            {
                if (IsChildScaleTarget(sm.Groups[1].Value)) continue;   // child/ware scale, not the actor's own
                sawSetScale = true;
                if (TryEvalLiteral(sm.Groups[2].Value, out float v)) { scale = v; source = $"Actor_SetScale({sm.Groups[2].Value.Trim()})"; break; }
            }
            if (scale == null)
                foreach (Match sm in ScaleFieldRegex().Matches(text))
                {
                    if (IsChildScaleTarget(sm.Groups[1].Value)) continue;
                    sawScaleField = true;
                    if (TryEvalLiteral(sm.Groups[2].Value, out float v) && v > 0f) { scale = v; source = $".scale = {sm.Groups[2].Value.Trim()}"; break; }
                }
            if (scale == null) { scale = 0.01f; review = sawSetScale || sawScaleField; if (review) source = "0.01 (default — but a NON-LITERAL SetScale/.scale exists; REVIEW)"; }
            rows.Add(new AuditRow(id, name, scale.Value, source, review));
        }
        rows.Sort((a, b) => a.Id.CompareTo(b.Id));
        return rows;
    }

    // Evaluates a scale expression made of float literals and at most one * or / (covers the decomp's
    // common forms: "0.01f", "64.8f * 0.0001f", "1.0f / 7.0f"). Returns false for anything non-literal
    // (variables, function calls) so those fall back to the resolver's default.
    private static bool TryEvalLiteral(string expr, out float result)
    {
        result = 0f;
        expr = expr.Trim();
        // Reject if it contains identifiers (letters other than a trailing 'f'/'F' on numbers) or casts.
        // Tokenize on * and / only.
        char op = '\0';
        string[] parts;
        if (expr.Contains('*')) { op = '*'; parts = expr.Split('*'); }
        else if (expr.Contains('/')) { op = '/'; parts = expr.Split('/'); }
        else parts = [expr];
        if (parts.Length > 2) return false;

        float acc = 0f;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!TryParseFloatLit(parts[i], out float f)) return false;
            if (i == 0) acc = f;
            else acc = op == '*' ? acc * f : (f != 0f ? acc / f : acc);
        }
        result = acc;
        return true;
    }

    private static bool TryParseFloatLit(string s, out float f)
    {
        s = s.Trim().TrimEnd('f', 'F');
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
    }
}
