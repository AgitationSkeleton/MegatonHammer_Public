# Logic / wire-display audit vs Valve Hammer SDK2013 I/O

Audit of the editor's wire display + logic-linking + logic-properties against Hammer SDK2013's entity
Input/Output system (source: `READ_ONLY_SourceCodes/sdk-2013-hammer-master/hammer`), grounded in the OoT/MM
decomp (SoH/2Ship `src`). Done 2026-06-29.

## Hammer's I/O model (reference)
- A **connection** = `CEntityConnection` { source output name, target entity, target input name, parameter,
  delay, fire-once } (`entityconnection.h`). Each entity stores outgoing `m_Connections` + incoming `m_Upstream`.
- Each entity class defines its named **outputs**/**inputs** in the FGD (`gdclass.h`, `inputoutput.h`).
- UI: an **Outputs tab** (`COP_Output`: icon | My Output | Target | Their Input | Delay | Once | Param) and an
  **Inputs tab** (`COP_Input`: read-only inbound view). "Mark" jumps to the source/target.
- The **Logical view** draws a wire ONLY between entities that are actually connected (`RenderConnections`),
  colour-coded by output, red/blinking when broken.

## How Megaton Hammer maps onto it
OoT/MM has no named outputs/inputs — the wire is a **shared flag**: one actor SETS flag N (an "output"),
another READS flag N (an "input"); the flag index IS the connection. Namespaces (the wire "type"): Switch,
Chest/Treasure, Collectible, Room-Clear, eventChkInf/eventInf (Event), Gold-Skulltula, MM weekEventReg.
`ActorParamSchema` is the FGD analogue (per-actor: which param bit-field is a flag, its FlagKind + FlagRole
Setter/Reader/Both). `FlagConnectionAnalyzer` is the connection engine.

## Findings
1. **Wires already draw only when actually connected.** `FlagConnectionAnalyzer.Links()` emits a wire only
   for a flag that has BOTH a setter and a reader (`setters x readers`); a lone setter/reader/unconfigured
   flag draws nothing. Verified — this matches Hammer's "only between connected entities" and the test temple.
   Sentinels are skipped: switch/collectible all-ones (0x3F/0x7F/0xFF = "no flag"), collectible 0 (no-op),
   and the door low-bits text-id vs switch-flag is gated by door type (`FlagFieldActive`). Earlier phantom
   wires (collectible-0, ordinary knob doors) were schema/sentinel bugs, since fixed.
2. **Coverage is ~99% (decomp-verified).** Every common flag-using actor is in the schema with the correct
   namespace + role + bit-field (incl. OoT 6-bit vs MM 7-bit switch/collectible ranges, and the conditional
   door-type switch flags). No missing actors or wrong roles were found. Event-flag-gated actors (Door_Toki,
   Demo_Kekkai) are correctly Readers-only with no params field; story/schedule writes (weekEventReg) are
   engine-driven, correctly not exposed as placeable wires.
3. **Gap that EXISTED: no per-actor I/O view.** The global `FlagConnectionsDialog` (tree by flag) + the
   viewport wires were there, but the per-actor properties (`EntityConfigDialog`) showed only the raw flag
   FIELDS — not the connections, i.e. no Hammer Outputs/Inputs tab.

## What was added/changed
- **Per-actor Outputs/Inputs (Hammer's Outputs/Inputs tabs).** `EntityConfigDialog` now has a
  **CONNECTIONS (I/O)** section: **OUTPUTS** = "sets {flag} -> {actor that reads it}", **INPUTS** =
  "reads {flag} <- {actor that sets it}", colour-coded by namespace (matching the wire colour), each row a
  link that **jumps to the connected actor** (Hammer "Mark"). It shows ONLY real connections (same source as
  the viewport wires) and is hidden entirely when the actor is wired to nothing. Backed by
  `FlagConnectionAnalyzer.ConnectionsFor(actor, actors, isOoT)`.

## Known limitation (documented, not yet wired)
A few actors keep their flag in **Rot.Z** instead of params: `En_Box` falling/appearing chest types 3/8
(switch flag in Rot.Z) and `Obj_Kibako2` large crate (collectible flag in Rot.Z). The analyzer reads
`a.Variable` only, so these don't auto-wire. The En_Box falling-chest case is a genuine switch->chest wire;
adding a "flag-from-Rot.Z" field kind (with the chest-type condition) would close the last coverage gap.
