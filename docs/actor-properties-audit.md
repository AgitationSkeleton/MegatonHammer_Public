# Actor properties popout — coverage audit (2026-06-25)

How the editor's actor **Properties** popout (`ActorParamSchema` → `EntityConfigDialog`/`PropertiesPanel`)
compares to what each actor actually supports: configuration/variant, contents/item-give, flags, and
**dialogue/message**. Goal: find where the popout can't express what the actor can do — especially text.

## How the popout works today

`ActorParamSchema` holds a `Def(title, Field[], note)` per actor id. Each `Field(name, shift, length,
kind, …)` slices the 16-bit **Variable** (params) into a labelled control: `Enum` → dropdown, `Int` →
number, `Flag` → checkbox; `FlagKind`/`FlagRole` wires the flag bus. Actors **without** a Def fall back
to a single raw hex **Variable** box (plus the transform's Rot X/Y/Z). Today there are **~44 Defs**
(31 OoT + 13 MM); every other actor (the large majority) is raw-hex only.

### Per-category coverage

| Category | Status | Notes |
|---|---|---|
| Configuration / variant | **Good for the 44 Def'd actors**, raw-hex for the rest | doors, switches, platforms, lifts, walls, etc. are well modelled |
| Contents / item-give | **Good** | chests (`GetItemTable`), `En_Item00`, `Item_Etcetera` all give configurable items |
| Flags (switch/chest/collectible/clear/event/GS) | **Good** | flag-bus model + named channels + connection graph |
| **Dialogue / message** | **Partial / mostly missing** | see below — this is the real gap |
| Rot X/Y/Z special fields | Partial | exposed generically; a few actors encode data here (documented per-Def) |

## Dialogue / message — the gap

A message-bearing actor picks an in-game **textId** from its params (or rot). The popout should let a
mapper choose/author that line. Current state per actor:

### OoT (SoH decomp — verified bit layouts)

| Actor | Id | textId source | In schema? | Gap |
|---|---|---|---|---|
| **En_Kanban** (sign) | 0x0141 | `textId = params \| 0x300` (FISHING/PIECE special) | ❌ **absent** | the headline "sign dialogue" actor isn't modelled at all |
| Elf_Msg (trigger msg) | 0x011B | `textId = (params&0xFF)+0x100`; switch `(params>>8)&0x3F` | ⚠️ raw "Message / data" int | not decoded as a real message id; no text authoring |
| Elf_Msg2 | 0x0173 | same pattern (+0x100) | ⚠️ raw int | same |
| En_Wonder_Talk (POI) | 0x0147 | type-LUT → fixed textIds; switch `params&0x3F` | ❌ absent | inspect-point POI not modelled |
| En_Wonder_Talk2 (POI) | 0x0185 | `textId = 0x200 \| ((params>>6)&0xFF)`; mode `(params>>14)&3` | ❌ absent | configurable POI text not modelled |
| En_Gs (gossip stone) | 0x01B9 | fixed (hint system) | ✅ type/switch | correct — text is engine-driven, not designer text |
| En_Owl | 0x014D | fixed per owl type | ✅ type/switch | correct — fixed lines |

### MM (2Ship decomp — verified bit layouts)

| Actor | Id | textId source | In schema? | Gap |
|---|---|---|---|---|
| En_Kanban (sign) | 0x0A8 | `textId = params \| 0x300` | ❌ absent | MM signs not modelled |
| Elf_Msg / Elf_Msg2 / Elf_Msg3 | 0x08B / 0x0C6 / 0x146 | `(params&0xFF) ± 0x200`; switch `(params>>8)&0x7F` | ⚠️ Elf_Msg/2 raw int; Elf_Msg3 absent | not decoded; Elf_Msg3 missing |
| En_Owl (statue) | 0x0AF | fixed per type; path `(params>>12)&0xF` | ✅ partial | path index not exposed |
| En_Gs | 0x0EF | stone-type fixed | ⚠️/❌ | MM En_Gs not Def'd |
| Schedule NPCs (En_In 0x067, En_Daiku 0x09C, En_Bombers 0x280, En_Invadepoh 0x200, …) | — | day/state **internal tables**, params pick variant+path | ❌ absent | dialogue is engine-tabled (not directly authorable); variant/path worth exposing |

**Takeaway:** signs (`En_Kanban`) and inspect-point POIs (`En_Wonder_Talk2`) — exactly the things a mapper
wants to write text for — are the least covered. The Elf_Msg actors capture the bits but present them as a
bare number with no way to author the underlying text. Many NPC lines are engine-tabled by day/state and
are not directly designer-authorable without engine changes (relevant to MM living-town work).

## Recommendations

1. **Introduce a `Message` field kind** (`FieldKind.Message` / a `FlagKind.Message`-style hook): a field
   that (a) writes the correct params bits using the actor's textId encoding (e.g. `| 0x300` for Kanban,
   `+0x100` for Elf_Msg), and (b) opens a **Message picker** tied to a project-level **Message Bank** so a
   mapper selects or authors the actual dialogue. See [dialogue-authoring-plan.md](dialogue-authoring-plan.md).
2. **Add the missing message actors** to `ActorParamSchema`: OoT `En_Kanban` (0x0141), `En_Wonder_Talk2`
   (0x0185), `En_Wonder_Talk` (0x0147); MM `En_Kanban` (0x0A8), `Elf_Msg3` (0x146). Give each a `Message`
   field + its switch/variant fields. Re-label Elf_Msg/Elf_Msg2 "Message / data" as a real `Message` field.
3. **Broaden Def coverage** opportunistically for high-value unmodelled actors (the raw-hex fallback is
   safe but opaque). Track in this doc as they're added.
4. **Document engine-tabled NPC dialogue** (MM schedule NPCs) as not-directly-authorable without a fork
   message hook; the dialogue plan's runtime-hook path (SoH/2Ship) is what would unlock overriding them.
