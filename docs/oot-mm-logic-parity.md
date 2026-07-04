# OoT/MM gameplay-logic authoring — assessment & Hammer-I/O parity design

Can a user recreate the level/scene/room/actor logic of *any* OoT dungeon or MM area (including
schedule-driven NPC towns) from scratch in Megaton Hammer, with a UI modeled on Valve Hammer
(SDK2013)'s Input/Output trigger scripting? This doc answers that from the engine source (OoT = SoH
decomp, MM = 2S2H decomp), maps the engine's logic model onto Hammer's I/O model, audits current
editor coverage, and lays out the work to close the gap. Researched 2026-06-25 against the decomp.

## Verdict (short)

- **OoT puzzle/dungeon logic: achievable** with the editor's *existing* abstraction — no new
  architecture needed, mostly breadth. The editor already has the correct core
  (`ActorParamSchema` typed bit-fields + `FlagConnectionAnalyzer`'s setter/reader grouping). To make
  "wire a dungeon from scratch" real it needs: (a) decode the ~25 missing switch/clear actors,
  (b) a **room-clear** (kill-all-enemies) namespace, (c) a read/write **EventChkInf** (story/cutscene)
  namespace, (d) **scene-header layers** for child/adult/day/night actor sets, and (e) an authoring
  (not just analyzing) front-end with viewport connection lines. **Not yet, but close.**
- **MM scheduled NPC towns: not yet possible.** Two MM-only systems are unmodeled: **HALFDAYBIT**
  per-actor day/half-day spawn gating (the data is already round-tripped in the actor rotation fields —
  an achievable next step) and the **schedule bytecode VM** that decides where each NPC stands and what
  it does by clock/day (a substantially larger feature, plus the `weekEventReg` event-flag layer the
  schedules branch on). Until those exist, a Clock-Town-style town can be placed but not brought to life.

The rest of this doc is the evidence and the design.

---

## 1. How OoT/MM expresses level logic (synthesis)

### 1.1 There is no targetname and no I/O table
A room actor entry is just `{ id:u16, pos:3×s16, rot:3×s16, params:u16 }` (`z_actor.c` `Actor_SpawnEntry`).
Actors coordinate **only** through:
1. The **16-bit `params`** word — per-actor packed bit-fields (no universal layout).
2. **Rotation fields**, frequently overloaded to carry data (chest switch flag in `rot.z`; large-crate
   flag in `rot.z`; Elf_Msg appear/kill condition in `rot.y`; MM half-day mask in `rot.x`/`rot.z`).
3. A small set of **scene-wide flag namespaces** — the "flag bus."

### 1.2 The flag bus (OoT) — `ActorContext.flags` (`z64.h:358-368`)
| Namespace | Range | Accessors (`z_actor.c`) | Persist | Indexed by |
|---|---|---|---|---|
| Switch | 0–31 (`swch`) + 32–63 (`tempSwch`) | `Flags_Get/Set/UnsetSwitch` 664+ | low 32 saved; temp resets | author index in params/rot |
| Chest | 0–31 (`chest`) | `Flags_Get/SetTreasure` 740+ | saved | params bits 0–4 |
| Clear | 0–31 (`clear`) + `tempClear` | `Flags_Get/Set/UnsetClear` 759+ | clear saved | **room number** |
| Collectible | 0–31 (`collect`) + 32–63 | `Flags_Get/SetCollectible` 811+ | low 32 saved | author index |

Permanence: at scene load `gSaveContext.sceneFlags[sceneNum]` seeds the live flags
(`z_actor.c:2534`); on exit `Play_SaveSceneFlags` writes the permanent bits back (`z_play.c:2091`).
Story/cutscene gating uses a **separate global** array, **`eventChkInf[]`** (`Flags_GetEventChkInf`,
`z_actor.c:4925`), which crosses scenes.

**The wire is a shared integer.** A setter actor calls `Flags_SetSwitch(n)`; any number of reader
actors call `Flags_GetSwitch(n)` with the same `n`. That shared `n` *is* the connection — many-to-many,
broadcast. The **Clear** namespace is special: its index is the *room number*, so "defeat all enemies
in this room → open the FRONT_CLEAR door / spawn the room-clear chest" is wired with zero authoring
(the engine sets `Flags_SetTempClear(room)` when the last `ACTORCAT_ENEMY` in the room dies, and refuses
to respawn enemies in a `Flags_GetClear` room).

### 1.3 The actor "logic vocabulary" (OoT, representative)
- **Switches (setters):** Obj_Switch (floor/eye/crystal; momentary/toggle), Bg_Bdan_Switch, Obj_Lift
  (collapse → set), Obj_Bombiwa / Bg_Breakwall (break → set), Bg_Hidan_Hamstep (8-bit flag).
- **Barred doors / gates (readers):** Door_Shutter — FRONT_CLEAR (type 1, on room clear), FRONT_SWITCH
  (type 2/7, live on a switch flag), KEY_LOCKED/BOSS (set switch on open); En_Door locked (read/set
  `params&0x3F`); Bg_Hidan_Kousi grate; elevators/platforms Bg_Mori_Elevator / Bg_Jya_Lift /
  Bg_Mizu_Movebg (read switch to choose state).
- **Chests:** En_Box — opened = `Flags_GetTreasure(params&0x1F)`; appear by TYPE on room clear
  (1/7), on a switch flag (3/8 fall, 11 rise; flag in `rot.z`), or on a song.
- **Trigger regions:** Elf_Msg/Elf_Msg2 — message + switch-flag set + appear/kill condition packed in
  `rot.y` (the richest single "wiring" actor); En_Wonder_Talk.
- **Cutscene/story triggers:** gate on `eventChkInf`, set it, then `gSaveContext.cutsceneTrigger=1`
  (Demo_Kekkai, Bg_Breakwall intro, Bg_Po_Event, Bg_Treemouth…). One-point "look-at" cutscenes are
  fired imperatively (`OnePointCutscene_*`).
- **Conditional spawn patterns:** already-done self-kill at Init (read flag → `Actor_Kill`); gate-until-
  flag (alpha 0 until `Flags_Get*`); per-actor age/time self-kill (`LINK_IS_ADULT`/`IS_DAY`). The only
  *list-level* conditional spawn is **alternate scene headers** (below) — not a per-actor field.

### 1.4 Scene/room structure (OoT) — `z_scene.c` command interpreter
Scene & room headers share one command set (0x00–0x19, `Scene_ExecuteCommands`). The author-relevant
ones: 0x00 spawn list (multiple spawns supported), 0x01 room actor list, 0x03 collision (carries
exit/cam/water-box data), 0x06 entrance list `{spawn,room}`, 0x0B object list, 0x0E transition actors,
0x0F light/fog settings, 0x10 time, 0x11 skybox, 0x12 skybox disables, 0x13 exit list, 0x16 echo,
0x17 cutscene blob, **0x18 alternate headers**, plus wind (0x05) and point lights (0x0C). The
exit chain: collision-poly **5-bit exit index** (`z_bgcheck.c:3995`) → `setupExitList[N-1]` → entrance
index → `gEntranceTable[idx]` `{scene,spawn,field}` → dest scene + spawn-list/room.

**Alternate headers (0x18)** are how a scene shows different content by *layer*: `Play_Init`
(`z_play.c:465`) picks `sceneSetupIndex` from age/time (CHILD_DAY/CHILD_NIGHT/ADULT_DAY/ADULT_NIGHT) or
the cutscene index's low nibble, with per-scene special overrides (flags). Each layer can swap the
actor list. This is the engine's "spawn this set under these conditions."

### 1.5 MM's added systems
- **Three-day clock:** `save.time` (u16, day = 0x10000), `save.day`, `isNight`. Setups are **not**
  auto-selected by clock — they're chosen by **event flags via the cutscene-index** (`sceneLayer =
  (cutsceneIndex&0xF)+1`); temple-clear `weekEventReg` flags push `nextCutsceneIndex` to `0xFFF0+` to
  load world-state variants. *Within* a setup, time-of-day population is done per-actor (next bullet).
- **HALFDAYBIT spawn gating (per-actor):** the top 3 id bits (`0xE000`) flag that `rot.x/y/z` carry
  **packed data, not rotations** (`Actor_SpawnEntry`, `z_actor.c:3865`). `halfDaysBits =
  ((rot.x&7)<<7)|(rot.z&0x7F)` is a 10-bit mask, one bit per half-day across the cycle
  (`z64actor.h:437`); `csId = rot.y&0x7F`. The engine spawns/kills actors at each dawn/dusk boundary by
  this mask (`Actor_SpawnSetupActors`/`Actor_KillAllOnHalfDayChange`). **This is the lever for "this NPC
  appears Day 2 night."**
- **Event flags:** `weekEventReg[100]` = 800 flags (`PACK_WEEKEVENTREG_FLAG(idx,mask)`), the main quest/
  schedule/world-state bus; `eventInf[8]` = 64 transient flags. Scene flags are **duplicated**:
  `permanentSceneFlags[]` (survive resets) vs `cycleSceneFlags[]` (reset each cycle). The 3-day reset
  AND-masks each scene's cycle flags by a per-scene `persistentCycleFlags` table baked into
  `DEFINE_SCENE(...)`, and filters `weekEventReg` by a 2-bit-per-flag persistence mask
  (`z_sram_NES.c:19-54`). Authoring an MM area means choosing this persistence.
- **Schedule bytecode VM** (`z_schedule.c`, `z64schedule.h`): scheduled NPCs run a `u8[]` script of
  `SCHEDULE_CMD_*` opcodes — `CHECK_TIME_RANGE`, `CHECK_BEFORE_TIME`, `CHECK_NOT_IN_DAY`,
  `CHECK_NOT_IN_SCENE`, `CHECK_WEEK_EVENT_REG`, `CHECK_MISC` (mask/letter/room-key), `RET_VAL`,
  `RET_TIME(t0,t1,result)` (a behavior code + time window for path interpolation), `RET_NONE`, `BRANCH`.
  Each NPC maps `result` → position/path/animation in a big switch. ~17 actors use it (Anju, Kafei,
  Bombers, postman, Gorman, shopkeepers…). The schedule decides *behavior while present*; **HALFDAYBIT
  decides existence**.
- Plus: owl-statue saves, `playerForm`/masks as gates, song events, Bombers'-notebook/bank state,
  composite NPCs (En_Ossan/En_Ko/En_Hy parameterized many ways).

---

## 2. The Hammer I/O model and how OoT/MM maps onto it

Hammer entity I/O: each entity has a **targetname**, a **keyvalues** grid, a **Flags** (spawnflags)
tab, and an **Outputs** table of rows `(my output event) → (target by targetname) → (their input) [+
parameter] [+ delay] [+ fire-once]`, with **connection lines drawn in the 2D/3D views**. Glue entities
(`logic_relay`, `logic_branch`, `math_counter`, `trigger_*`) provide indirection, boolean gating,
counting, and spatial triggers.

Mapping table (OoT/MM ⇄ Hammer):

| Hammer concept | OoT/MM analogue | Fit |
|---|---|---|
| Output→input wire | Two actors sharing a flag index (Setter ⇄ Reader) | **Natural** — the core mapping |
| `targetname` (handle) | The **flag index** (switch 0–63, chest 0–31, collectible 0–63; MM week-event flag) | Partial — a scarce global int, not a free string |
| Output event name | "actor sets flag N when its condition fires" (button/chest/kill) | Awkward — hard-coded in C, one implicit output per actor |
| Input action | "actor reads flag N and changes" (door opens, platform appears) | Awkward — hard-coded, no parameter |
| Parameter / delay / fire-once | — | No analogue (1-bit flags, no timing) |
| Fan-out | many readers of one flag | **Natural** (global bus is broadcast) |
| `func_button` OnPressed → door Open | Obj_Switch (set flag N) ⇄ Door_Shutter FRONT_SWITCH (read flag N) | **Natural** — the headline case |
| `trigger_multiple` OnStartTouch | Elf_Msg proximity region (sets a switch flag); warp trigger brushes | **Natural** |
| `trigger_changelevel` | Transition actors + entrance table | Structural, via index/entrance not output |
| OnAllEnemiesDead → Open | room-clear: `Flags_SetTempClear(room)` on last enemy death | **Natural but implicit** — wire = room number |
| `logic_relay`/`logic_branch`/`math_counter` | **none** (gating baked into specific actors or cutscenes) | **Major gap** |
| `logic_auto` OnMapSpawn (initial state) | scene/room **setups** (age/time/cutscene layers) + pre-set flags | Awkward — data-table selection, not an event |
| SpawnFlags tab | the params bit-fields (already shown as the bit grid + decoded LOGIC fields) | **Natural — closest existing 1:1** |
| Outputs/Inputs tabs | the per-flag setter/reader lists (computed by `FlagConnectionAnalyzer`) | **Natural** read-side analogue |
| Connection lines in views | — | **Missing** (no actor-to-actor lines drawn) |

**Conclusion of the mapping:** OoT/MM's "shared flag bus" is genuinely I/O-shaped: *setter = output,
reader = input, flag index = wire*. Hammer's per-entity Outputs table doesn't map 1:1 (the event/action
are fixed by the actor, there are no parameters/delays), but a **flag-centric** I/O UI fits perfectly —
and the editor already computes exactly this graph (read-only). The awkward residue is cutscenes,
MM schedules/time, and the lack of generic logic/relay/counter primitives.

---

## 3. Current editor coverage (audit)

**Already built (the right foundation):**
- `ActorParamSchema` — typed bit-field decode with `FlagKind{Switch,Chest,Collectible,Scene}` +
  `FlagRole{Setter,Reader,Both}`. Schemas for ~18 OoT / ~9 MM actors.
- `EntityConfigDialog` — per-actor: ID picker, raw variable, a **16-bit spawnflags grid** (Hammer's
  Flags tab), and a decoded **LOGIC** section (typed fields) — strong keyvalues/flags parity.
- `FlagConnectionAnalyzer` + `FlagConnectionsDialog` — groups all placed actors by `(FlagKind, index)`,
  shows `⇒ set / ⇐ read / ⇄ both`, flags dangling `set-never-read`/`read-never-set`, lists exits/warps,
  double-click-to-go-to. This **is** a Hammer-style Inputs/Outputs view — but **read-only/inferred**.
- `ZActor` already carries Hammer-modeled editor-only `Name` (targetname), `GroupId`, `VisGroupId`,
  transition linkage; MM `IdFlags` (top-3 id bits) round-tripped.
- Scene/room structure: spawn (single), actor lists, collision, exits (trigger brushes), transition
  actors, lighting/fog, skybox, time, echo, **multi-setup** (`ZScene.Setups`/`ZSetup`), object lists.

**Gaps blocking "from scratch" logic authoring** (consolidated from the engine research):
1. **No authoring of connections** — you set matching flag numbers on each actor by hand; the graph is
   inferred after the fact. No way to *create* a wire, no flag allocator/namer, **no connection lines in
   the viewports**.
2. **Room-clear namespace missing** — nothing represents `Flags_Clear`/`TempClear` (room-indexed). The
   single most common dungeon gate ("kill all enemies → door/chest") is invisible to the analyzer.
3. **EventChkInf (OoT) / weekEventReg + eventInf (MM) not modeled** — cutscene/story wiring and (MM)
   the entire quest/schedule/world-state bus have no representation (read-only at minimum is needed).
4. **Schema breadth** — ~25 OoT switch/clear actors absent (Bg_Bdan_Switch, Obj_Lift, Bg_Breakwall,
   Bg_Hidan_Kousi, elevators, Obj_Timeblock, En_Owl, En_Gs, Bg_Po_Event, Demo_Kekkai, Door_Toki…), and
   Door_Shutter/En_Door switch flags + doorType aren't decoded → switch-gated doors don't appear in the
   graph. 8-bit switch fields need per-field length (the `Field.Length` plumbing already supports it).
5. **Schema corrections** (engine-verified): `En_Item00 0x8000` = "falling/arcing drop," NOT "spawn if
   uncollected" (the collectible check is unconditional); `En_Sw 0x0095` is the **Gold Skulltula token**
   (GS-flag namespace), not a "silver-rupee/counter switch"; Elf_Msg appear/kill condition lives in
   `rot.y` and isn't decoded.
6. **Scene-header layer semantics** — setups are generic ("Setup N"); the age/time/cutscene/flag-driven
   selection (`Play_Init`) and (MM) the `cutsceneIndex→sceneLayer→0xFFF0` world-state mapping aren't
   represented, so "child vs adult vs day vs night actor sets" can't be authored meaningfully.
7. **Multiple spawns per scene** — the entrance list is hard-coded to one `{spawn0, room}`.
8. **MM HALFDAYBIT not decoded** — the per-actor half-day spawn mask (`rot.x/z`) + `csId` (`rot.y`) are
   round-tripped as raw rotations with no UI. Biggest blocker for time-populated MM towns.
9. **MM schedule VM not supported** — no reading/authoring of `SCHEDULE_CMD_*` scripts; scheduled NPCs
   (Anju/Kafei/Bombers/postman) can't be configured. Largest single MM feature.
10. **No generic logic primitives** — OoT/MM lack `logic_relay`/`counter`/`branch`; complex gating uses
    cutscenes or special actors, which the editor doesn't author.

---

## 4. Proposed Hammer-I/O UI for OoT/MM (design)

The goal is to present the flag-bus as Hammer-style I/O **without inventing engine features** — every
"connection" must compile to vanilla flag indices / actor params.

**4.1 The "Logic" model in the editor.** Treat each flag namespace as an address space of **named
channels**: `switch:Name`, `chest:Name`, `collect:Name`, `clear:room`, `event:Name` (OoT EventChkInf /
MM weekEventReg). A channel has an allocator (auto-pick a free index, with collision warnings against
vanilla usage) and a friendly name (editor-only, like `ZActor.Name`). The compiled scene just uses the
integer index; the names live in the project file.

**4.2 The entity I/O dialog (extend `EntityConfigDialog`).** Add **Outputs** and **Inputs** tabs that
read the actor's schema `FlagRole`:
- *Outputs* = each `Setter` field → "When this fires (`<implicit event>`), it **sets** channel
  `switch:GateA`." The user picks the channel (dropdown of named channels + "new…"); compiles to the
  flag index in params/rot.
- *Inputs* = each `Reader` field → "This **reads** channel `switch:GateA` to <implicit action>."
- The implicit event/action text comes from the schema (e.g. Obj_Switch "OnThrown → set",
  Door_Shutter "reads → unbar"), so it reads like Hammer's "OnPressed → Open" even though it's fixed.

**4.3 Connection lines in the viewports.** When an actor is selected (or always, toggle), draw lines
between every setter and reader of each shared channel in the 2D and 3D views (reuse the
`DrawClipLine2D` primitive), colored by namespace, dashed/red when dangling (set-never-read /
read-never-set) — directly mirroring Hammer's connection rendering and the analyzer's existing dangling
detection. The room-clear channel draws "OnAllEnemiesDead(room) → <readers in room>".

**4.4 Promote `FlagConnectionsDialog` to an authoring view.** Keep the `⇒/⇐/⇄` tree but allow:
create channel, rename, re-assign an actor's field to a channel, and "add reader/setter" (which selects
a compatible actor to place). This turns the inferred graph into an editable one.

**4.5 Scene-layer (setup) authoring.** Give each `ZSetup` a semantic tag (Child-Day/Child-Night/
Adult-Day/Adult-Night/Cutscene-N for OoT; MM `cutsceneIndex`/world-state for MM) so "spawn this actor
set under these conditions" is explicit, compiling to the 0x18 alternate-header layer index.

**4.6 MM-specific panels.**
- **HALFDAYBIT editor** (tractable now — data already round-tripped): a Day0…Day4 × Dawn/Night
  checkbox grid on the actor's properties that reads/writes the packed `rot.x/z` mask + sets the
  `0xE000` id bits; plus a `csId` field. This alone enables authoring time-populated towns' *presence*.
- **Event-flag (`weekEventReg`) view** — at least a read/write named-flag list + per-scene
  `persistentCycleFlags` authoring, since schedules and setups branch on these.
- **Schedule editor** (largest) — a structured editor for `SCHEDULE_CMD_*` scripts: rows of
  "if <time/day/scene/flag/misc> then <behavior result> [over time window]", compiling to the bytecode.
  This is the keystone for scheduled NPCs and should be its own milestone.

---

## 5. Implementation progress & status

> **Progress (2026-06-25):** items **1** (schema breadth + engine-verified corrections), **3**
> (viewport connection lines — View ▸ Show Logic Connections), and **6** (MM HALFDAYBIT spawn-condition
> editor — a Day0–4 × Dawn/Night checkbox grid in the entity dialog that packs the per-actor half-day
> mask into Rot X/Z + the id bits, so MM towns can be populated by time-of-day) are implemented.
> item **2 (partial)** — **room-clear** channel surfaced (Door_Shutter FRONT_CLEAR + room-clear chests
> grouped by room). **4** done — named flag channels (Hammer targetnames) + allocator: name a channel
> in the Flag Connections dialog, allocate a fresh one with the entity dialog's "Free" button.
> **5** done — setups carry a SetupLayer tag ("Loads under" dropdown). **6** done (HALFDAYBIT) — plus
> the per-actor **csId** cutscene link (rot.y) is now editable too.
>
> **§9 long tail:** **wind (0x05)** and **point lights (0x0C)** are now implemented (both N64 scene
> builders + serialization + Properties-panel UI; no-light/no-wind scenes are byte-for-byte unchanged).
> **Multiple spawns per scene** is deferred by design: extra spawn points are only reachable via the
> global `gEntranceTable` (engine ROM data, not scene data), so without entrance-table authoring they'd
> be unreachable — low utility for moderate export risk. **Cutscene authoring** is engine/overlay-side
> (see §6).
>
> **Items 7 (weekEventReg starting flags) and 8 (NPC schedules) are now IMPLEMENTED** via the custom-
> engine convention §6 foresaw: the editor emits `mh/schedules` + `mh/info` "weekEvents" into the mod
> O2R and the **2Ship fork** applies them — a schedule rule overrides the actor's position/facing by the
> in-game clock each frame; week-events are `SET_WEEKEVENTREG` on boot. Authored via the entity dialog's
> `ScheduleDialog` (per-actor rules) and the Properties panel ("Start week-events"). Validated
> end-to-end (`2ship_harness.ps1`: `[mh_sched] loaded N` + `[mh_boot] set N starting week-event flag(s)`
> + RENDER OK, no crash). **The roadmap is complete.** Still NOT authorable: the actor's *internal*
> schedule bytecode VM (we override position instead — sufficient for "NPC is here at this time") and
> full cutscene scripting (retained binary only).

## 6. Architectural boundary — what the editor can and cannot author

The editor's output is **scene/room data + actor *placements***: each actor is a 16-byte entry
(`id`, `pos`, `rot`, `params`) in a room actor list, plus the scene/room header commands. Everything
expressible in that data is now authorable, including the MM per-actor spawn condition (HALFDAYBIT in
`rot.x/z`, csId in `rot.y`) and the whole flag bus (params bit-fields → switch/chest/collectible/
room-clear channels).

What is **NOT** in scene data — and therefore cannot be authored by placing/parameterising actors —:
- **MM NPC schedules (#8).** A scheduled NPC's `SCHEDULE_CMD_*` bytecode lives in the **actor overlay's
  data segment** (compiled into the ROM/engine), not in the room actor entry. Placing En_An (Anju)
  spawns the vanilla Anju with the vanilla schedule; there is no field in the actor entry to supply a
  different script. Authoring custom schedules would require one of: (a) the editor emitting **actor-
  overlay patches** (rewriting ROM overlay data — a disassemble/reassemble pipeline), or (b) a **custom-
  engine convention** where the playtest mod O2R carries a per-actor schedule *resource* that the
  SoH/2Ship fork loads and applies (mirroring how `mh_playtest.o2r` already carries `mh/info`). Both are
  substantial standalone projects, not an actor-placement feature.
- **MM `weekEventReg` cycle persistence (#7).** The per-scene `persistentCycleFlags` (which chest/switch
  bits survive a 3-day reset) is baked into the engine's `DEFINE_SCENE(...)` macro + `z_sram_NES.c`
  tables — engine source, not scene-file data. Same for which `weekEventReg` bits persist. The editor
  can't set these via scene export; it'd need the same (b) custom-engine convention (a persistence table
  in the mod O2R).
- **Cutscene scripts (OoT 0x17 / story `eventChkInf`).** The cutscene blob is retained binary on
  round-trip but the cue/camera VM isn't authorable; story-event flags are global engine state with no
  per-actor index, so they stay annotation-only.

**Conclusion:** for **OoT dungeons**, "wire the logic from scratch" is now genuinely achievable in the
editor (flag bus + room-clear + named channels + scene layers, all scene-data). For **MM towns**, the
*presence* of NPCs by day/time is authorable (HALFDAYBIT + csId), but their *scripted behavior*
(schedules) and the *cycle-persistence/event* layer are engine/overlay-side and need a dedicated
engine-convention or overlay-patching pipeline — tracked here as a future milestone rather than a tool
gap. The forks already demonstrate the convention (the mod O2R + boot hook), so the path exists; it's
just a large, separate build.

## 7. Roadmap to full parity (prioritized)


1. **Schema breadth + corrections** (small, high value): add the ~25 missing OoT switch/clear actors,
   decode Door_Shutter/En_Door switch flags + doorType, fix `En_Item00 0x8000` and `En_Sw`, decode
   Elf_Msg `rot.y` condition. Immediately enriches the existing connection graph.
2. **Room-clear + EventChkInf channels** (small-medium): add `clear:room` and a read/write `event:*`
   namespace to `FlagKind` + the analyzer; surfaces the kill-gate and story wiring.
3. **Connection lines in viewports** (medium): draw setter⇄reader lines; reuse `DrawClipLine2D` +
   the analyzer. Turns the graph visual, Hammer-style.
4. **Authoring front-end** (medium): named channels + allocator, Outputs/Inputs tabs in
   `EntityConfigDialog`, editable `FlagConnectionsDialog`.
5. **Scene-layer semantics** (medium): tag setups; author conditional actor sets.
6. **MM HALFDAYBIT editor** (medium, unlocks MM town *presence*): decode/author the half-day mask + csId.
7. **MM weekEventReg/eventInf + persistentCycleFlags** (medium).
8. **MM schedule VM editor** (large, the keystone for living towns): structured `SCHEDULE_CMD_*` author.
9. **Multiple spawns per scene; wind (0x05) & point lights (0x0C); cutscene authoring** (assorted).

Items 1–5 get OoT dungeons to genuine "wire it from scratch with a Hammer-style UI." Items 6–8 get MM
scheduled towns there. Item 9 closes the long tail.

---

### Source map (for follow-up implementation)
OoT: `SoH/soh/src/code/z_scene.c` (commands 177–508), `z_actor.c` (flags 664–828, global 4925+, spawn
2534/2591/3290/3350, transition 3445), `z_play.c` (setup select 465, save flags 2091), `z_bgcheck.c`
(exit index 3995), `z_demo.c` (cutscene 152–184); `include/z64.h:358-368`, `z64scene.h`, `z64save.h`,
`entrance_table.h`. MM: `2Ship/mm/src/code/z_schedule.c`, `z_actor.c` (3865 id-bits, 2596/2651/3538
half-day), `z_sram_NES.c` (cycle reset 19–54, 460+, 1281+), `z_play.c` (setup 2363); `include/
z64schedule.h`, `z64save.h` (weekEventReg 346/735, scene flags 278/288/330/524), `z64actor.h:437`.
Editor: `Editor/ActorParamSchema.cs`, `Editor/FlagConnectionAnalyzer.cs`, `Forms/FlagConnectionsDialog.cs`,
`Forms/EntityConfigDialog.cs`, `Editor/ZActor.cs`, `Editor/ZSetup.cs`.
