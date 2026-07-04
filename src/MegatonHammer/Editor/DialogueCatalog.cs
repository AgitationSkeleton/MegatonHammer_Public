using System.Collections.Generic;

namespace MegatonHammer.Editor;

/// <summary>
/// Per-actor list of the vanilla message textIds an NPC/sign uses in the real game — the "Default Dialogue"
/// contextual list for the editor. Distilled from a decomp survey (docs + the actor overlays). Lets a mapper
/// see what an actor says and OVERRIDE a specific line (route to the Dialogue Editor at that textId), staying
/// vanilla-portable: the override just replaces the message-bank text at that id.
///
/// Most named NPCs are "type B" (their textId is hardcoded / story-flag-switched), so the exact line shown at
/// runtime still depends on save state — we surface the representative/default id(s). Signs, regions and a few
/// scrubs are "type A" (params select the textId) and already have a Message field; they're not repeated here.
/// This is a representative starter set (the most iconic talkers); extend freely.
/// </summary>
public static class DialogueCatalog
{
    public readonly record struct Line(int TextId, string Label)
    {
        public override string ToString() => $"0x{TextId:X4}  {Label}";
    }

    private static readonly Dictionary<ushort, Line[]> OoT = new()
    {
        [0x0084] = new[] { new Line(0x204B, "greeting"), new Line(0x2055, "asleep"), new Line(0x2080, "cucco game") },          // En_Ta Talon
        [0x00E7] = new[] { new Line(0x2041, "greeting"), new Line(0x2061, "teach Epona's Song") },                             // En_Ma1 Malon (child)
        [0x00D9] = new[] { new Line(0x204C, "greeting (castle)") },                                                            // En_Ma2 Malon (adult, castle)
        [0x01C5] = new[] { new Line(0x2000, "greeting (ranch)"), new Line(0x208E, "Epona / horse") },                          // En_Ma3 Malon (adult, ranch)
        [0x00CB] = new[] { new Line(0x203F, "greeting (child)"), new Line(0x201F, "horse race (adult)") },                     // En_In Ingo
        [0x0146] = new[] { new Line(0x1001, "greeting") },                                                                     // En_Sa Saria
        [0x016D] = new[] { new Line(0x102F, "Kokiri Forest"), new Line(0x1060, "Lost Woods") },                               // En_Md Mido
        [0x0163] = new[] { new Line(0x1004, "Kokiri kid") },                                                                   // En_Ko Kokiri/Bros/Fado (params pick which)
        [0x0098] = new[] { new Line(0x301A, "greeting"), new Line(0x301C, "give Goron's Bracelet") },                          // En_Du Darunia
        [0x01AE] = new[] { new Line(0x3030, "Goron"), new Line(0x3053, "Biggoron trade") },                                    // En_Go2 Gorons (params pick role)
        [0x013D] = new[] { new Line(0x3049, "Medigoron") },                                                                    // En_Gm Medigoron
        [0x01CE] = new[] { new Line(0x4006, "Zora") },                                                                         // En_Zo Zora (params pick which)
        [0x0164] = new[] { new Line(0x401A, "greeting (Ruto's Letter)"), new Line(0x4012, "adult") },                          // En_Kz King Zora
        [0x00A1] = new[] { new Line(0x402C, "Ruto (Jabu)") },                                                                  // En_Ru1 Ruto (child)
        [0x0138] = new[] { new Line(0x6001, "guard greeting"), new Line(0x6040, "horseback archery") },                        // En_Ge1 Gerudo (valley/archery)
        [0x0186] = new[] { new Line(0x6005, "member greeting"), new Line(0x6004, "give Gerudo Card") },                        // En_Ge2 Gerudo patrol
        [0x0153] = new[] { new Line(0x5034, "greeting"), new Line(0x5035, "teach Song of Storms") },                          // En_Fu Windmill Man
        [0x014D] = new[] { new Line(0x2064, "Kaepora Gaebora (advice)") },                                                     // En_Owl (params pick spot)
        [0x000B] = new[] { new Line(0x00DB, "Great Fairy appears"), new Line(0x00DA, "reward") },                              // Bg_Dy_Yoseizo Great Fairy
        [0x0162] = new[] { new Line(0x2029, "Running Man") },                                                                  // En_Mm Running Man
        [0x01D4] = new[] { new Line(0x607D, "marathon") },                                                                     // En_Mm2 Running Man (marathon)
        [0x0085] = new[] { new Line(0x501A, "Dampe (offer game)") },                                                           // En_Tk Dampe
        [0x0115] = new[] { new Line(0x101B, "Skull Kid") },                                                                    // En_Skj Skull Kid
        [0x013E] = new[] { new Line(0x405E, "magic bean salesman") },                                                         // En_Ms bean seller
        [0x011A] = new[] { new Line(0x10A0, "business scrub (shop)") },                                                        // En_Dns Deku scrub salesman
        [0x0192] = new[] { new Line(0x109B, "hint scrub") },                                                                   // En_Hintnuts
        [0x016E] = new[] { new Line(0x7016, "townsfolk") },                                                                    // En_Hy Hylian (params pick which)
        [0x003D] = new[] { new Line(0x70AC, "shopkeeper / mask shop") },                                                       // En_Ossan
        [0x00ED] = new[] { new Line(0x40A9, "frog choir") },                                                                   // En_Fr frogs
        [0x014A] = new[] { new Line(0x4000, "lakeside professor") },                                                          // En_Mk
        [0x0175] = new[] { new Line(0x5005, "big Poe") },                                                                      // En_Po_Field
    };

    /// <summary>The known vanilla lines for an actor, or null if it has no catalogued dialogue.</summary>
    public static Line[]? For(bool isMM, ushort actorId) => isMM ? null : OoT.GetValueOrDefault(actorId);

    public static bool Has(bool isMM, ushort actorId) => For(isMM, actorId) != null;
}
