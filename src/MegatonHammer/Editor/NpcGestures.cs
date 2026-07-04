using System.Collections.Generic;

namespace MegatonHammer.Editor;

/// <summary>
/// Per-NPC gesture (talk-animation) lists for the Dialogue Editor's Gesture picker, distilled from the
/// decomp survey (see docs/dialogue-npc-gestures.md). The value is an index into that actor's own
/// gesture set; a supporting runtime (a per-actor hook, or En_MhTalk with a model) maps it to the
/// animation. Gestures are actor-internal in vanilla, so this is authoring metadata, not a universal path.
/// Only NPCs with distinct, meaningfully-selectable gestures are listed; anyone else gets "(default)".
/// </summary>
public static class NpcGestures
{
    public readonly record struct Gesture(int Index, string Name)
    {
        public override string ToString() => Name;   // so it shows nicely in a ComboBox
    }

    /// <summary>For actors without a curated gesture set (e.g. the generic Dialogue Point): just the default,
    /// so the picker stays friendly rather than showing meaningless numbered slots.</summary>
    public static Gesture[] Generic() => new[] { Default };

    private static readonly Gesture Default = new(-1, "(default idle-talk)");

    private static readonly Dictionary<ushort, Gesture[]> OoT = new()
    {
        [0x0084] = new[] { Default, new(1, "arm gesture (haw)") },                               // En_Ta Talon
        [0x0098] = new[] { Default, new(7, "joy dance"), new(14, "dance end") },                 // En_Du Darunia
        [0x0146] = new[] { Default, new(2, "arm extended"), new(3, "hands out"),                 // En_Sa Saria
                           new(4, "hands on hips"), new(5, "point"), new(7, "behind back"), new(8, "hands on face") },
        [0x0162] = new[] { Default, new(5, "excited"), new(6, "happy") },                        // En_Mm Running Man
        [0x0164] = new[] { Default, new(2, "\"mweep\" cry") },                                    // En_Kz King Zora
        [0x016D] = new[] { Default, new(2, "raise hand (halt)"), new(3, "halt"),                 // En_Md Mido
                           new(4, "hand down"), new(11, "angry slam"), new(13, "angry head-turn") },
        [0x01CE] = new[] { Default, new(6, "foot-tap (impatient)"), new(7, "open arms"), new(5, "throw rupees") }, // En_Zo Zora
        [0x01D3] = new[] { Default, new(28, "laugh"), new(29, "happy"), new(31, "cock head"),    // En_Zl4 Child Zelda
                           new(26, "point at window"), new(5, "surprised"), new(6, "lean in") },
    };

    /// <summary>The named gestures for an actor, or null if it has no distinct ones (use a plain index).</summary>
    public static Gesture[]? For(bool isMM, ushort actorId) => isMM ? null : OoT.GetValueOrDefault(actorId);
}
