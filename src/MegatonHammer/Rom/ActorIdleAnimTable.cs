using System.Text.RegularExpressions;

namespace MegatonHammer.Rom;

/// <summary>
/// Ground-truth IDLE animation offset per object, parsed from the decomp's asset XMLs
/// (assets/xml/objects/*.xml, each `&lt;Animation Name="g..." Offset="0x..."/&gt;`). Skeletal actors must be
/// posed with frame 0 of an animation or they render as a bind-pose tangle; the auto-detector
/// (<see cref="ObjectModelReader.FindFrame0JointTable"/>) picks the animation with the MOST dynamic
/// joints, but an NPC's idle/wait animation is subtle (few joints move) while its walk/attack animation
/// moves many — so the heuristic systematically picks the wrong (action) animation and the frame-0 pose
/// collapses. This reads the real idle/wait/stand animation Nintendo named, so every humanoid NPC stands
/// in its in-game resting pose. Mirrors <see cref="ActorScaleTable"/>'s per-game decomp parsing; built
/// once per game and cached. The XML Offset is the segment-6 offset of the AnimationHeader within the
/// object file, which is exactly the <c>animOffset</c> <see cref="ObjectModelReader.ReadAnimFrame0"/> wants.
/// </summary>
public sealed partial class ActorIdleAnimTable
{
    private readonly Dictionary<string, int> _objToOffset = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The idle-animation offset for an object, or null if no idle/wait animation was named.</summary>
    public int? OffsetFor(string objectName) => _objToOffset.TryGetValue(objectName, out int o) ? o : null;

    public int Count => _objToOffset.Count;

    [GeneratedRegex(@"<Animation\s+Name=""([^""]+)""\s+Offset=""0x([0-9A-Fa-f]+)""")]
    private static partial Regex AnimRegex();

    private static ActorIdleAnimTable? _ootCache, _mmCache;

    public static ActorIdleAnimTable Build(bool mm)
    {
        if (mm && _mmCache != null) return _mmCache;
        if (!mm && _ootCache != null) return _ootCache;
        var t = new ActorIdleAnimTable();
        try { t.Load(mm); } catch { }
        if (mm) _mmCache = t; else _ootCache = t;
        return t;
    }

    private void Load(bool mm)
    {
        string dir = $@"D:\Copilot_OOT\READ_ONLY_SourceCodes\{(mm ? "mm-main" : "oot-master")}\assets\xml\objects";
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.xml"))
        {
            string objName = Path.GetFileNameWithoutExtension(file);   // object_fsn
            string text; try { text = File.ReadAllText(file); } catch { continue; }

            int bestOff = -1, bestScore = int.MinValue;
            foreach (Match m in AnimRegex().Matches(text))
            {
                int score = ScoreIdle(m.Groups[1].Value);
                if (score == int.MinValue) continue;
                int off = Convert.ToInt32(m.Groups[2].Value, 16);
                // Prefer the highest idle score; tie-break on the LOWER offset (the primary idle is
                // usually defined first, ahead of variant/unused idles).
                if (score > bestScore || (score == bestScore && (bestOff < 0 || off < bestOff)))
                { bestScore = score; bestOff = off; }
            }
            if (bestOff >= 0) _objToOffset[objName] = bestOff;
        }
    }

    // Scores how likely an animation name is the canonical resting/idle pose. int.MinValue = not an idle
    // candidate. The reject set drops action/transition animations whose frame 0 is a mid-motion pose
    // (so e.g. "gFooWaitAttackAnim" doesn't beat "gFooIdleAnim"); "idle" > "wait" > "neutral"/"stand".
    private static int ScoreIdle(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("unused") || n.Contains("attack") || n.Contains("walk") || n.Contains("run")
            || n.Contains("death") || n.Contains("die") || n.Contains("hurt") || n.Contains("damage")
            || n.Contains("jump") || n.Contains("swim") || n.Contains("turn") || n.Contains("sleep")
            || n.Contains("wake") || n.Contains("fall") || n.Contains("sit") || n.Contains("dead")
            || n.Contains("fly") || n.Contains("dance") || n.Contains("throw") || n.Contains("dig"))
            return int.MinValue;
        if (n.Contains("idle")) return 100;
        if (n.Contains("wait")) return 90;
        if (n.Contains("neutral")) return 80;
        if (n.Contains("stand")) return 70;
        return int.MinValue;
    }
}
