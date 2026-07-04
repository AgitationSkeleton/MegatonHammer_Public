using System.Collections.Generic;

namespace MegatonHammer.Editor;

/// <summary>
/// Per-actor list of the vanilla message textIds an NPC/sign uses in the real game — the "Default Dialogue"
/// contextual list for the editor. Distilled from a verified decomp survey (every identity was confirmed by
/// reading the actor's overlay, incl. the age/scene checks). Lets a mapper see what an actor says and OVERRIDE
/// a specific line (route to the Dialogue Editor at that textId), staying vanilla-portable: the override just
/// replaces the message-bank text at that id.
///
/// Labels state the SPEAKER'S AGE/CONTEXT explicitly where an actor is age- or instance-specific — several
/// OoT actors look like duplicates but are distinct (e.g. En_Ma1 = CHILD Malon who teaches Epona's Song, while
/// En_Ma2 AND En_Ma3 are both ADULT Malon: Ma2 ambient, Ma3 the ranch race). Most named NPCs are "type B"
/// (textId is hardcoded / story-flag- / age-switched), so the exact line at runtime still depends on save
/// state — we surface the representative/default id(s). "Type A" actors (params select the textId) already
/// carry a Message field; a few iconic ones are still listed here for discoverability.
/// </summary>
public static class DialogueCatalog
{
    public readonly record struct Line(int TextId, string Label)
    {
        public override string ToString() => $"0x{TextId:X4}  {Label}";
    }

    // ── Ocarina of Time ──
    private static readonly Dictionary<ushort, Line[]> OoT = new()
    {
        [0x0084] = new[] { new Line(0x204B, "greeting"), new Line(0x207E, "Cucco game (Lon Lon)"), new Line(0x702A, "asleep at castle") },   // En_Ta Talon
        [0x00E7] = new[] { new Line(0x2041, "first meeting (CHILD Malon)"), new Line(0x204A, "after song (child)"), new Line(0x2061, "teach Epona's Song") }, // En_Ma1
        [0x00D9] = new[] { new Line(0x204C, "check on Epona, day (ADULT Malon)"), new Line(0x2050, "night (adult)"), new Line(0x2056, "once you have Epona") }, // En_Ma2 (ambient)
        [0x01C5] = new[] { new Line(0x2000, "horse-race intro (ADULT Malon)"), new Line(0x2012, "race timer / highscore"), new Line(0x208E, "Epona / race start") }, // En_Ma3 (ranch race)
        [0x00CB] = new[] { new Line(0x203F, "ranch hand (child Ingo)"), new Line(0x2030, "horse rental (adult Ingo)"), new Line(0x2032, "Ingo race") }, // En_In
        [0x0146] = new[] { new Line(0x1002, "greeting (Saria)"), new Line(0x1047, "Sacred Meadow (teach Saria's Song)"), new Line(0x10AD, "has song") }, // En_Sa
        [0x016D] = new[] { new Line(0x102F, "Kokiri Forest gate (Mido)"), new Line(0x1046, "Mido's House"), new Line(0x1060, "Lost Woods") }, // En_Md
        [0x0163] = new[] { new Line(0x1004, "Kokiri kid (child)"), new Line(0x104E, "Kokiri (adult era)") },                     // En_Ko (param picks which villager / Fado)
        [0x0098] = new[] { new Line(0x301A, "greeting (Darunia)"), new Line(0x301C, "give Goron's Bracelet"), new Line(0x3039, "Fire Temple door") }, // En_Du
        [0x01AE] = new[] { new Line(0x3030, "Goron (param picks role)"), new Line(0x3053, "Biggoron trade"), new Line(0x7122, "Goron bazaar") }, // En_Go2
        [0x0152] = new[] { new Line(0x3030, "Goron Link / special Goron (param)"), new Line(0x3053, "Biggoron") },              // En_Go (special/scripted)
        [0x013D] = new[] { new Line(0x3049, "Medigoron") },                                                                     // En_Gm
        [0x01CE] = new[] { new Line(0x4006, "Zora (param picks which)"), new Line(0x402D, "Zora's Sapphire") },                  // En_Zo
        [0x0164] = new[] { new Line(0x401A, "Ruto's Letter (child)"), new Line(0x4012, "eyeball frog / Zora Tunic (adult)"), new Line(0x4045, "Serenade of Water") }, // En_Kz King Zora
        [0x00A1] = new[] { new Line(0x404C, "escort inside Jabu (child Ruto)"), new Line(0x402C, "face reaction") },             // En_Ru1
        [0x00D2] = new[] { new Line(0x403E, "adult Ruto (Water Temple)") },                                                     // En_Ru2
        [0x0138] = new[] { new Line(0x6001, "friendly Gerudo guard"), new Line(0x6013, "Training Ground"), new Line(0x603F, "horseback archery") }, // En_Ge1 (white)
        [0x0186] = new[] { new Line(0x6000, "capture (jail guard)"), new Line(0x6005, "friendly (carpenters freed)"), new Line(0x6004, "give Gerudo Card") }, // En_Ge2 (purple)
        [0x01D0] = new[] { new Line(0x6004, "give Gerudo's Card"), new Line(0x6005, "greeting") },                              // En_Ge3 (red)
        [0x0153] = new[] { new Line(0x5032, "windmill (calm, child)"), new Line(0x5034, "windmill (angry, adult)"), new Line(0x5035, "teach Song of Storms") }, // En_Fu
        [0x014D] = new[] { new Line(0x2064, "Kaepora Gaebora (param picks perch)"), new Line(0x2068, "Castle perch"), new Line(0x206C, "Kakariko perch") }, // En_Owl
        [0x000B] = new[] { new Line(0x00DB, "Great Fairy appears"), new Line(0x00DA, "farewell") },                             // Bg_Dy_Yoseizo
        [0x0162] = new[] { new Line(0x2029, "Running Man, child era"), new Line(0x202A, "sell Bunny Hood") },                    // En_Mm
        [0x01D4] = new[] { new Line(0x607D, "Marathon Man, adult era (race)"), new Line(0x6084, "race finish") },               // En_Mm2
        [0x0085] = new[] { new Line(0x5018, "Dampé gravedigging offer"), new Line(0x501A, "dig result") },                      // En_Tk (living Dampé)
        [0x0122] = new[] { new Line(0x0000, "Dampé's Ghost, Hookshot race (textId = param)") },                                 // En_Po_Relay
        [0x0115] = new[] { new Line(0x101B, "Skull Kid (Lost Woods)"), new Line(0x10BE, "ocarina game") },                       // En_Skj
        [0x013E] = new[] { new Line(0x405E, "magic bean salesman") },                                                          // En_Ms
        [0x011A] = new[] { new Line(0x10A0, "business scrub shop (item by param)") },                                          // En_Dns
        [0x0192] = new[] { new Line(0x109B, "hint scrub") },                                                                   // En_Hintnuts
        [0x016E] = new[] { new Line(0x7016, "townsfolk (param picks which Hylian)") },                                         // En_Hy
        [0x003D] = new[] { new Line(0x009E, "shop greeting (type by param)"), new Line(0x70AC, "mask shop") },                  // En_Ossan
        [0x00ED] = new[] { new Line(0x40A9, "frog choir (repeat reward)"), new Line(0x40AC, "learn/play song") },              // En_Fr
        [0x014A] = new[] { new Line(0x4000, "lakeside professor") },                                                          // En_Mk
        [0x0175] = new[] { new Line(0x5005, "big Poe") },                                                                      // En_Po_Field
        [0x00C3] = new[] { new Line(0x601D, "Nabooru, crawlspace (first)"), new Line(0x6024, "Nabooru (later)") },             // En_Nb
        [0x01B8] = new[] { new Line(0x70F4, "Poe Seller (spiel)"), new Line(0x70F6, "sell a Poe") },                           // En_Gb
        [0x000D] = new[] { new Line(0x5005, "composer / sellable Poe") },                                                      // En_Poh
        [0x0029] = new[] { new Line(0x703C, "child Zelda at window (idle)"), new Line(0x702E, "first meeting"), new Line(0x7039, "give Zelda's Letter") }, // En_Zl1
        [0x01D3] = new[] { new Line(0x702E, "child Zelda courtyard meeting"), new Line(0x703C, "idle") },                       // En_Zl4 (no Lullaby — Impa teaches that)
        [0x00A9] = new[] { new Line(0x708E, "Impa (escort/escape)") },                                                         // Demo_Im
        [0x00B3] = new[] { new Line(0x2009, "Hylian guard") },                                                                 // En_Heishi2
        [0x00FE] = new[] { new Line(0x4089, "fishing-pond owner") },                                                          // Fishing
        [0x003E] = new[] { new Line(0x0905, "Deku Tree mouth (gate)") },                                                       // Bg_Treemouth
        [0x017B] = new[] { new Line(0x4076, "scarecrow (Bonooru)") },                                                          // En_Kakasi
        [0x01C6] = new[] { new Line(0x2006, "cow moo"), new Line(0x2007, "cow (milk)") },                                     // En_Cow
        [0x013C] = new[] { new Line(0x5036, "Anju (missing cuccos)"), new Line(0x503E, "Anju (trade)") },                     // En_Niw_Lady
        [0x0149] = new[] { new Line(0x504F, "Potion Shop granny") },                                                          // En_Ds
        [0x014B] = new[] { new Line(0x7058, "Bombchu Bowling lady") },                                                        // En_Bom_Bowl_Man
        [0x0189] = new[] { new Line(0x001F, "Skulltula House man") },                                                          // En_Sth
        [0x0167] = new[] { new Line(0x5050, "man on the roof") },                                                             // En_Ani
        [0x019A] = new[] { new Line(0x7000, "Cucco Lady (Cojiro)") },                                                         // En_Niw_Girl
        [0x016C] = new[] { new Line(0x2022, "graveyard boy"), new Line(0x2023, "Spooky Mask") },                              // En_Cs
        [0x01A4] = new[] { new Line(0x700D, "Happy Mask Shop customer") },                                                     // En_Guest
        [0x0141] = new[] { new Line(0x0300, "wooden signpost (textId = param | 0x300)") },                                     // En_Kanban
    };

    // ── Majora's Mask (Clock Town human NPCs live in the 0x2368–0x2B25 message band) ──
    private static readonly Dictionary<ushort, Line[]> MM = new()
    {
        [0x0202] = new[] { new Line(0x28D1, "\"looking for Kafei?\" (Anju)"), new Line(0x28E6, "Kafei's letter") },            // En_An Anju
        [0x0253] = new[] { new Line(0x28FC, "Anju's Mother") },                                                               // En_Ah
        [0x0243] = new[] { new Line(0x2912, "Anju's Grandma (storybook)") },                                                  // En_Nb
        [0x0159] = new[] { new Line(0x145F, "Kafei (hideout meeting)") },                                                     // En_Test3 Kafei
        [0x0262] = new[] { new Line(0x27AC, "Madame Aroma (Kafei's letter)"), new Line(0x2AA6, "Milk Bar") },                 // En_Al
        [0x026F] = new[] { new Line(0x2ABD, "Mayor Dotour (council)"), new Line(0x2368, "wearing Kafei's Mask") },            // En_Dt
        [0x0234] = new[] { new Line(0x2A94, "Toto (Zora band manager)") },                                                    // En_Toto
        [0x01D5] = new[] { new Line(0x277C, "Postman (schedule)") },                                                          // En_Pm
        [0x01B5] = new[] { new Line(0x2004, "Happy Mask Salesman greeting") },                                                // En_Osn
        [0x002A] = new[] { new Line(0x06A4, "Trading Post welcome (shop by param)") },                                        // En_Ossan
        [0x0135] = new[] { new Line(0x12CE, "Bomb/Zora/Goron shop welcome (shopType)") },                                     // En_Sob1
        [0x0280] = new[] { new Line(0x073D, "Bombers kid (hide-and-seek)") },                                                 // En_Bombers
        [0x0281] = new[] { new Line(0x0725, "Bombers Hideout guard (password)") },                                            // En_Bombers2
        [0x01A4] = new[] { new Line(0x334D, "Romani (alien-defense explanation)"), new Line(0x3357, "outcome") },             // En_Ma4
        [0x021F] = new[] { new Line(0x334D, "Romani (paired with Cremia)") },                                                 // En_Ma_Yts
        [0x0220] = new[] { new Line(0x3395, "Cremia (milk run)") },                                                           // En_Ma_Yto
        [0x00A6] = new[] { new Line(0x33F7, "Grog (chicks grown → Bunny Hood)") },                                            // En_Hs
        [0x0067] = new[] { new Line(0x347A, "Gorman Brothers (track twins)") },                                               // En_In
        [0x00A4] = new[] { new Line(0x2AA4, "Gorman Troupe entertainer") },                                                   // En_Gm
        [0x017F] = new[] { new Line(0x080B, "Deku Butler (race guide)") },                                                    // En_Dno
        [0x016A] = new[] { new Line(0x0898, "Deku King (throne)") },                                                          // En_Dnq
        [0x01FC] = new[] { new Line(0x0967, "Deku Princess (rescued)") },                                                     // En_Dnp
        [0x0138] = new[] { new Line(0x0E10, "Goron Village NPC (multiplexer by param)") },                                    // En_Go
        [0x0201] = new[] { new Line(0x0E7A, "Goron Elder's crying son") },                                                    // En_Gk
        [0x0213] = new[] { new Line(0x0DAC, "Goron Elder (Darmani)") },                                                       // En_Jg
        [0x0224] = new[] { new Line(0x1004, "Indigo-Go's Zora band member") },                                               // En_Zog
        [0x01BD] = new[] { new Line(0x060E, "roaming Business Scrub (trader)") },                                             // En_Sellnuts
        [0x024C] = new[] { new Line(0x162F, "Business Scrub (Heart Piece deal)") },                                          // En_Scopenuts
        [0x0176] = new[] { new Line(0x1D00, "Tingle (map seller)") },                                                        // En_Bal
        [0x0130] = new[] { new Line(0x059A, "Great Fairy") },                                                                 // Bg_Dy_Yoseizo
        [0x00EF] = new[] { new Line(0x20D0, "Gossip Stone (with Mask of Truth)"), new Line(0x20D2, "silent (no mask)") },     // En_Gs
        [0x00A8] = new[] { new Line(0x0300, "signpost (textId = param | 0x300)") },                                          // En_Kanban
        [0x00AF] = new[] { new Line(0x0BEA, "Owl (perch by param)") },                                                       // En_Owl
        [0x00CA] = new[] { new Line(0x1653, "Pierre the Scarecrow (first meet)"), new Line(0x1650, "\"Shall we dance?\"") },   // En_Kakasi
        [0x0177] = new[] { new Line(0x044C, "Bank teller (deposit)") },                                                       // En_Ginko_Man
        [0x00B5] = new[] { new Line(0x283C, "Honey & Darling (minigame host)") },                                            // En_Fu
        [0x01EF] = new[] { new Line(0x2716, "Swordsman (training school)") },                                                 // En_Kendo_Js
    };

    /// <summary>The known vanilla lines for an actor, or null if it has no catalogued dialogue.</summary>
    public static Line[]? For(bool isMM, ushort actorId) =>
        (isMM ? MM : OoT).GetValueOrDefault(actorId);

    public static bool Has(bool isMM, ushort actorId) => For(isMM, actorId) != null;
}
