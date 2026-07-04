# Dialogue authoring + append plan (OoT / MM × N64 / SoH / 2Ship)

Plan to let a mapper **author dialogue** for signs, POIs, and NPCs in Megaton Hammer, and **append** that
text so it works on all four targets: vanilla OoT N64, vanilla MM N64, SoH (Shipwright), 2Ship (2s2h).

## 1. Editor model — a project-level Message Bank

Add a **Message Bank** to the project (`ZScene`/project level, serialized like `FlagNames`):

```
MessageBank : List<MhMessage>
MhMessage { int Id; string Text; BoxType Type; YPos Position; Icon Icon; /* optional per-language */ }
```

- `Text` uses a **friendly markup** (engine-agnostic): `&` = newline, `^` = new page/box, `%r %g %b %y %w …`
  = colour, `[item:ID]` = item icon, `[name]` = player name, `[choice2]/[choice3]` = prompts. The encoder
  lowers this to each engine's control bytes.
- `Id` is allocated by the editor (a "Free message id" allocator, like `NextFreeFlag`). For vanilla targets
  the id must live in a sensible textId bank (e.g. signs in `0x0300+`); a per-target id-map handles offsets.
- A **Message Bank dialog** (mirrors `ScheduleDialog`/`InventoryDialog`) edits the list: add/remove, edit
  text with a live byte-count + box-break preview, set box type/position/icon.

### Wiring into actor properties

- New **`FieldKind.Message`**: renders as a "Message…" picker (dropdown of bank entries + "New…"). On apply
  it writes the actor's params using that actor's **textId encoding**, decoded back when shown:
  - `En_Kanban` (OoT 0x0141 / MM 0x0A8): `params = textId & ~0x300` (textId = `params | 0x300`).
  - `Elf_Msg`/`Elf_Msg2` (OoT): `params bits0-7 = textId - 0x100`.
  - `En_Wonder_Talk2` (OoT 0x0185): `params bits6-13 = textId - 0x200`.
- The schema gains a per-field `textIdBase`/`encoding` so `FieldKind.Message` knows the offset. Picking a
  bank message sets both the params bits and (for vanilla N64) reserves that textId in the export.

## 2. Encoder — friendly markup → engine bytes

One encoder with two back-ends (the control maps differ substantially):

| | OoT (N64 + SoH share codes) | MM (N64 + 2Ship share codes) |
|---|---|---|
| Terminator | `0x02` (END) | `0xBF` |
| Newline | `0x01` | `0x11` |
| New box/page | `0x04` | `0x10` |
| Colour | `0x05` + arg (1=red,3=blue,6=yellow,…) | bare byte `0x00-0x08` (1=red,2=green,3=blue,4=yellow,…) |
| Box type/pos | in the **table entry** (`typePos=(type<<4)\|pos`) | in the **message body header** (`[type,pos,icon,nextId hi,lo,…]`) |
| Item icon | `0x13`+id | header `icon` byte / inline |

SoH/2Ship already ship friendly→bytes converters (`CustomMessage::Format`/`AutoFormat`) — the editor's
encoder mirrors them so editor preview == in-game rendering.

## 3. Per-target append / runtime mechanism

### (a) Vanilla OoT N64 ROM — extend the message table  ✅ locator + decode VERIFIED (2026-06-25)
`MessageTableEntry { u16 textId; u8 typePos; const char* segment }`, 8 bytes laid out as
`[id(u16) | opts(u8) | 0x00 | bank(u8) | offset(u24)]`; sorted by ascending offset, `0xFFFF`-terminated;
length = next.offset − this.offset; body DMA'd from `<msgdata file ROM start> + offset`. **The bank varies
by build** (the EU/debug ZELOOTD ROM uses **0x07**, US uses 0x08), so `Rom/MessageTableLocator.cs`
auto-detects it (terminator-anchored back-walk). Verified against ZELOOTD: table @ `0xBC24E0` (2116
entries, bank 0x07), msg-data file @ `0x8C6000`, entry 0x0001 round-trips to real text. `--testmsgtable`
PASSes. **Append — DONE via in-place overwrite** (`Rom/RomInjector.AppendMessages`, `--testmsgappend`
PASS): for each (textId, encoded body) it finds the entry in `sNesMessageEntryTable` and rewrites the text
*within the slot the table already allocates* (length = next.offset − this.offset). NO table growth, code
relocation, or dmadata changes — only message-data bytes — so it can't corrupt the message system and is
verifiable by re-decoding. Wired into `InjectDebug`/`RunN64` (OoT only). The editor reuses existing
textIds (a sign placed with params → textId 0x300+ overrides that vanilla sign's text); bodies longer than
the slot are skipped + reported. (Growing the table to add *brand-new* ids would need the heavier
code-relocation surgery — all addresses are mapped in [[megaton-hammer-dialogue-injection]] — but the
in-place path covers the sign/POI use case without that risk.)

### (b) Vanilla MM N64 ROM — same table, MM format
8-byte entries, `typePos` **unused** (always 0 — box type/pos live in the **body header**), terminator
`0xBF`, three tables (use the US/NES one). Otherwise identical append mechanic. The encoder's MM back-end
emits the 11-byte header + body + `0xBF`.

### (c) SoH (Shipwright) — runtime CustomMessageManager + OnOpenText hook (no ROM edit)
SoH already has the machinery. The `mh_playtest` SoH fork patch, at boot:
1. read an OTR resource `mh/messages` (the editor emits it via `O2RPacker`) — a JSON/Text list of
   `{id, type, pos, icon, text}`;
2. `CustomMessageManager::AddCustomMessageTable("MegatonHammer")` + `CreateMessage(table, id, CustomMessage(text,type,pos))` for each;
3. register one `OnOpenText` handler over the MH id range: `RetrieveMessage` → `LoadIntoFont` → set
   `loadFromMessageTable = false`.
Actors just call the normal `Message_StartTextbox(textId)`. **No new engine code model needed — only the
fork patch + the editor emitting `mh/messages`.**

### (d) 2Ship (2s2h) — extend the existing CustomMessage path to a multi-id bank
2Ship has `CustomMessage` (single reserved id `0x004B` + `OnOpenText` + `LoadCustomMessageIntoFont`, MM
format-aware). It is **one active message at a time**, not an id-addressed bank. Mirror MH's existing
resource convention (`mh/schedules`, `weekEvents`): the editor emits an `mh/messages` O2R resource; the
2Ship fork patch loads it into `unordered_map<u16, CustomMessage::Entry>` and **extends the `OnOpenText`
registration beyond `0x004B`** to the MH id range — handler looks up `textId`, fills `activeCustomMessage`,
`LoadCustomMessageIntoFont`, clears `loadFromMessageTable`. Editor encodes MM bytes so the patch can memcpy.

## 4. Editor export glue

- N64 builds (`RomInjector`/`--packplaytestn64`): call `AppendMessages` with the bank → ROM table grows.
- SoH/2Ship builds (`O2RPacker.PackOtr`): emit `mh/messages` resource (alongside `mh/info`, `mh/schedules`).
- Both reference messages by the same `MhMessage.Id`; only the lowering differs.

## 5. Phasing

1. **Model + UI**: Message Bank + `FieldKind.Message` + the missing sign/POI Defs (En_Kanban, En_Wonder_Talk2).
   Lets a mapper *assign existing* vanilla textIds immediately (no append) — already useful for signs.
2. **SoH path** (lowest risk, no ROM edits): `mh/messages` resource + fork patch + `OnOpenText`. Validate
   via `soh_harness`.
3. **2Ship path**: extend `CustomMessage` to the id-mapped bank via the same resource. Validate via `2ship_harness`.
4. **Vanilla N64 append** (OoT then MM): `RomInjector.AppendMessages` + encoder back-ends. Validate via the
   N64 boot harness (and the MM N64 path from [mm-injection-debug-plan.md](mm-injection-debug-plan.md)).

## 6. Broad compatibility — do NOT tie mods to our forks

Design rule (user, 2026-07-03): a mapper making a **total-conversion mod** must be able to use this WITHOUT
basing their project on our SoH/2Ship forks. So the system is tiered by how much "behaviour" a message needs:

- **Tier A — presentation (universal, zero engine changes).** Text, colour (`CTRL_COLOR`), timing
  (`CTRL_TEXT_SPEED`/`BOX_BREAK_DELAYED`/`FADE`/`AWAIT_BUTTON_PRESS`), multi-box (`BOX_BREAK`), two-choice
  prompts (`CTRL_TWO_CHOICE`), and **branch-to-message** (`CTRL_TEXTID` goto) are all *standard vanilla message
  control bytes*. They run on unmodified OoT/MM carts, on **stock** SoH/2Ship, and on any decomp base
  (HackerOoT, TC mods) — identical bytes, no custom code. This already covers the bulk of Zelda message
  objects: signs, POIs, readable objects, and flavour/branching NPCs. `MhMessage`/`MhOutcome` are engine-neutral
  data; `MessageEncoder` emits these vanilla bytes. **Nothing here depends on our forks.**

- **Tier B — outcomes (give item / charge rupees / set flag / per-choice branch).** These are NPC *behaviour*,
  not message data, so they need code. To stay fork-independent, the editor emits the outcome table as portable
  data **and generates a small, standalone, permissively-licensed decomp-C "dialogue NPC" actor** (e.g.
  `ovl_En_MhTalk`) that reads that table and performs the outcome (offer item via `Actor_OfferGetItem`, deduct
  `Rupees_ChangeBy(-cost)` gated on affording it, `Flags_SetSwitch`, `Message_ContinueTextbox(nextId)`). The
  modder drops that ONE actor into *their* base — vanilla decomp, SoH, 2Ship, HackerOoT, or a TC mod. **Our
  forks include the same open actor; they get no special treatment.** Where an existing vanilla actor already
  does the job (a shop = browse+charge+give), a preset reskins that instead — also fork-independent.

So: the runtime is an **open drop-in actor + a documented data format**, never a proprietary hook in our
SoH/2Ship. The editor's job is authoring engine-neutral data + the vanilla message bytes + (optionally) the
portable actor source.

Cross-refs: [[megaton-hammer-logic-parity]] (flag-bus + the O2R-resource/fork-hook convention this reuses),
[actor-properties-audit.md](actor-properties-audit.md) (the gap this closes).
