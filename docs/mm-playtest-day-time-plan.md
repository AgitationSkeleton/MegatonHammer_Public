# Plan: MM playtest day + time (symmetric across 2Ship and vanilla-MM PJ64)

**Goal.** Let a level author pick a specific Majora's Mask **day** *and* **time**, and have the playtest boot
directly into that state — identically on **2Ship** (O2R) and **vanilla MM via Project64**. It only sets the
initial boot state; the clock then runs normally. No change to normal gameplay/day-advance logic.

## 1. MM day/time model (decomp `include/z64save.h`)

The relevant `gSaveContext.save` fields:

| Field | Off | Type | Meaning |
|---|---|---|---|
| `time` | 0x0C | u16 | Clock. `0x0000`=midnight, `0x4000`≈6:00, `0x8000`=noon, `0xC000`≈18:00 (same convention the editor already uses for `PlaytestTimeOfDay`). |
| `isNight` | 0x10 | s32 | Day/night flag (0=day, 1=night). Derived from `time` at the day/dusk boundary. |
| `day` | 0x18 | s32 | "totalday". `CURRENT_DAY = day % 5` → **0** = before the cycle / first cycle, **1/2/3** = the three days, **4** = "the dawn of a new day" (cycle end). |
| `eventDayCount` | 0x1C | s32 | Days elapsed for event bookkeeping. |
| `isFirstCycle` | 0x05 | u8 | Pre-first-Song-of-Time state. |

So booting into "Day N at time T" = writing `save.day = N`, `save.time = T`, `save.isNight = (T is night)`,
and keeping `eventDayCount`/`isFirstCycle` consistent. "No-day" scenes (intro, clock-tower interior, moon)
freeze the clock — for those the day field is ignored by the game, so the preset is a no-op there.

## 2. Editor changes

- **`SceneSettings.PlaytestDay`** (new) — an enum/byte: `Day1 / Day2 / Day3 / NewDay(4) / FirstCycle(0)`.
  Keep `PlaytestTimeOfDay` for the clock. A "no day" choice = leave both unset (current behaviour).
- **Scene properties UI** (MM only): a "Playtest day" dropdown beside the existing "Playtest time" field.
  Optionally a combined quick-preset (Day 1 morning / Day 2 night / Final hours / …) that sets both.
- **Pack into BOTH configs** (already carry time — add day):
  - PJ64: `Project64Playtest.WriteN64Params` → add `day=` to `mh_n64_playtest.txt`.
  - 2Ship: the O2R `mh` info blob that already carries `timeOfDay` → add `day`.
- Derive `isNight` from the chosen time in one shared helper so both engines get the same flag.

## 3. 2Ship (fork) — boot hook

2Ship already normalizes time on playtest boot. Extend that one hook to also set, **once at boot**:
`gSaveContext.save.day = day; gSaveContext.save.time = time; gSaveContext.save.isNight = isNight;`
(plus `eventDayCount`/`isFirstCycle` for consistency). Read `day` from the same `mh` info it reads `timeOfDay`
from. This is the existing "boot into a known state" path — no gameplay logic touched.

## 4. Vanilla MM via Project64 — `MegatonHammer.cpp` hook (the new work)

The PJ64 hook (`pj64_megaton/Source/Project64-core/N64System/MegatonHammer.cpp`) currently supports **OoT
only** (detects the OoT debug ROM, scans RDRAM for the OoT `PlayState`, pokes entrance/age). Add an **MM
branch**, symmetric to 2Ship:

1. **Detect MM** by internal ROM name (e.g. "ZELDA MAJORA'S MASK") — alongside the existing OoT-debug check.
2. **Locate `gSaveContext`** in RDRAM. Two options: (a) fixed symbol address for MM US retail (from the MM
   map / a known constant, like the OoT `gSaveContext` base we already use), or (b) scan for the MM `PlayState`
   the same way the OoT path does and reach save context from a known global. Prefer the fixed address (simpler,
   deterministic) — **research item: confirm MM US-retail `gSaveContext` VRAM address + that the save block is
   at the same offsets as decomp.**
3. **Poke** `save.day` (0x18), `save.time` (0x0C), `save.isNight` (0x10) with the values from
   `mh_n64_playtest.txt` (`day=`, `timeOfDay=`), byte-swapped for RDRAM, **once** when a valid gameMode is first
   seen (mirroring the existing OoT one-shot poke so a non-matching ROM is never touched).
4. Reuse the existing headless/heartbeat/`MH_MAXFRAMES` scaffolding unchanged.

The scene injection for MM-N64 already works (`--injectmmscene`); this adds only the day/time state poke.

## 5. Symmetry

Define the exact write set in ONE place (a short spec both engines follow):
`{ save.day, save.time, save.isNight[, eventDayCount, isFirstCycle] }` with `isNight` derived from `time` by the
same rule. 2Ship writes them in its boot hook; PJ64 pokes the identical RDRAM offsets. Same inputs ⇒ same boot
state (clock, sky/environment for the time, and any day-gated actor spawns via `halfDaysBits`/weekEventReg all
follow naturally from the set day/time).

## 6. "No gameplay change" guarantee

- Writes happen **once, at boot**, into the initial save state — never in the per-frame update path, and never
  to the day/time *advance* logic. After boot the clock runs exactly as vanilla.
- Gated behind the playtest config being present (the injected scene / `mh_n64_playtest.txt`); a normal ROM/run
  is untouched. On PJ64 this is the same one-shot, ROM-validated guard the OoT hook already uses.

## 7. Edge cases

- **No-day scenes** (moon, intro, clock-tower interior): clock frozen → day field ignored; preset is inert
  (leave day unset). Document this.
- **Day 4 / "new day"**: `day % 5 == 4` is the cycle-end/"dawn of a new day" state — useful for testing the
  final cutscene area; note it isn't a normal playable day.
- **`isNight` vs `time`**: set together to avoid a mismatched sky/enemy set. Confirm whether MM stores `isNight`
  or recomputes it on load — if recomputed, we may only need `time` + `day`.
- **weekEventReg / final-hours**: day-3 night triggers "final hours" music/environment; the scene's existing
  `StartWeekEvents` still applies on top. No conflict — they compose.

## 8. Verification

Boot the SAME level with the SAME (day, time) on 2Ship and on MM-PJ64 and confirm: identical on-screen clock,
matching day/night sky + lighting, and the same day-gated NPCs/actors present. A headless PJ64 run
(`MH_HEADLESS` + `MH_MAXFRAMES`) can dump `save.day`/`save.time` to the log to assert the poke landed.

## Open research items before coding
1. MM US-retail `gSaveContext` VRAM address (for the PJ64 poke) + confirm save-field offsets vs decomp.
2. Whether `isNight` is persisted or derived on scene load (decides if we write it).
3. The exact 2Ship boot-hook function that currently sets `timeOfDay` (to add the day writes beside it).
