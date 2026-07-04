# Per-NPC vanilla dialogue textId catalog (OoT)

From a decomp survey of `oot-master`. Feeds the editor's **Dialogue** row (the "vanilla lines" contextual
list + override path) and `Editor/DialogueCatalog.cs`. **Type:** **A** = params select the textId (directly
overridable by placement; these already get a Message field). **B** = hardcoded / story-flag-switched (override
the *text*, not via params). Many are **A+B** (params pick the persona/family → a textId group; story flags
pick the exact line). For B, we surface the representative/default id.

## Directly param-overridable (type A — Message field)
| actor | id | textId formula |
|---|---|---|
| En_Kanban (sign) | 0x0141 | `params \| 0x300` → 0x0300–0x03FF |
| Elf_Msg / Elf_Msg2 (regions) | 0x011B / 0x0173 | `(params&0xFF)+0x100` → 0x0100–0x01FF |
| En_Wonder_Talk2 (POI) | 0x0185 | `0x200 \| ((params>>6)&0xFF)` → 0x0200–0x02FF |
| En_Dns (scrub salesman) | 0x011A | `D_809F040C[params]` (params 0–10) |
| En_Hintnuts (hint scrub) | 0x0192 | `(params>>8)&0xFF` |
| En_Po_Relay (ghost relay) | 0x0122 | relays a supplied id |

## Composite A+B — params pick the persona/family
| actor | id | selector | representative ids |
|---|---|---|---|
| En_Hy (townsfolk) | 0x016E | `params&0x7F` (21 people) | 0x70xx/0x50xx families |
| En_Ko (Kokiri/Bros/Fado) | 0x0163 | `params&0xFF` (13 kids) | ~0x1004–0x10DA |
| En_Go2 (Gorons) | 0x01AE | `params&0x1F` (14 roles) | 0x3002–0x3070 |
| En_Zo (Zora) | 0x01CE | `params&0x3F` | 0x4006–0x402F |
| En_Owl (Kaepora) | 0x014D | `(params>>6)&0x3F` (13 spots) | 0x2064–0x2072, 0x4002+ |
| En_Ossan (shopkeeper) | 0x003D | `params` = shop type | 0x70AC (mask shop) + buy/sell |
| En_Ge1 (Gerudo) | 0x0138 | `params&0xFF` (role) | 0x6001, 0x6040 (archery) |
| En_Sth / En_Poh | 0x0189 / 0x000D | `params` = which/flavour | reward / composer poe |

## Type B — hardcoded / story-flag (override the text)
Talon 0x0084 (0x204B greet…), Malon 0x00E7/0x00D9/0x01C5, Ingo 0x00CB, Saria 0x0146 (0x1001), Mido 0x016D,
Darunia 0x0098 (0x301A), Medigoron 0x013D, King Zora 0x0164 (0x401A), Ruto 0x00A1/0x00D2, Windmill Man 0x0153
(0x5034), Running Man 0x0162/0x01D4, Dampe 0x0085, Skull Kid 0x0115, bean seller 0x013E, professor 0x014A,
frogs 0x00ED, big Poe 0x0175, Great Fairy 0x000B (0xDB), Anju 0x013C, Cow 0x01C6, Ge2/Ge3 0x0186/0x01D0,
Heishi guards 0x008F/0x00B3/0x0142/0x0178, Nabooru 0x00C3, En_Ani 0x0167, En_Mk 0x014A, En_Fr, En_Hs 0x013F…

## Delivery patterns
1. `Npc_UpdateTalking(..., GetTextId, ...)` — the standard quest-NPC idiom (GetTextId returns a story-switched literal).
2. `Actor_ProcessTalkRequest` + `actor.textId = <literal>` then `Message_StartTextbox/ContinueTextbox`.
3. Proximity/inspect volumes (Elf_Msg / En_Wonder_Talk2 / En_Okarina_Tag).

## Not talkable (no dialogue picker)
Cutscene-only: Great Deku Tree intro, Impa (Demo_Im), En_Zl1/En_Zl3 escort. No text: En_Dekunuts, En_Dekubaba,
En_Ik, En_Attack_Niw, En_Shopnuts, En_Wonder_Item, Obj_* crates/pots/beehive, logic tags.

## Editor use
`DialogueCatalog.cs` holds a representative starter set (the iconic talkers). The editor shows these as a
"Dialogue" row on catalogued actors → **Customize…** creates an override (`MhMessage.IsOverride`) at the chosen
textId, replacing the words while keeping the NPC's own behaviour. Extend `DialogueCatalog` with more rows from
this table as needed. MM (`mm-main`) uses the same `Npc_UpdateTalking`/textId patterns.
