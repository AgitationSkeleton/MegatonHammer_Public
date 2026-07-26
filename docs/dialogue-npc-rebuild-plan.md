# Dialogue-NPC demo rebuild — saved state

Status: **IN PROGRESS 2026-07-18.** User chose BOTH (build actor into forks + engine-free). SoH build running.

## PROGRESS (done so far)
- **Editor outcome emission** — `Export/O2RPacker.cs BuildMessagesJson` now emits prompt/choice1/choice2/o1/o2
  (give/cost/flag/next)/doneFlag/afterId. Builds clean. ✅
- **SoH interpreter** — `SoH/soh/soh/Enhancements/debugconsole.cpp`: extended MhMsgEntry (prompt/choices/outcomes),
  MhOnOpenText assembles two-choice (`CustomMessage::TWO_WAY_CHOICE`) + arms sMhOpenTextId; added MhApplyOutcome
  (Rupees_ChangeBy / ItemTableManager::RetrieveItemEntry+GiveItemEntryWithoutActor / Flags_SetSwitch /
  Message_ContinueTextbox) + MhDialogueInterpreter (OnGameFrameUpdate, keyed on msgCtx state + choiceIndex);
  registered in DebugConsole_Init. OnOpenText confirmed fired at z_message_PAL.c:2732. ✅ (needs SoH build)
- **En_MhTalk actor built into BOTH forks** (via subagent) — SoH id **0x01D7**, 2Ship id **0x2B2**. Visible
  spinning green-rupee beacon (gRupeeDL/gRupeeGreenTex from gameplay_keep, OBJECT_GAMEPLAY_KEEP), offers talk
  (SoH func_8002F2CC/Actor_ProcessTalkRequest; MM Actor_OfferTalk/Actor_TalkOfferAccepted), opens textId
  0x1000+(params&0xFF). Files: `{SoH/soh/src,2Ship/mm/src}/overlays/actors/ovl_En_MhTalk/z_en_mhtalk.{c,h}`
  + one line each in `.../include/tables/actor_table.h`. All symbols verified present. ✅ (needs fork build)
- **Editor game-aware id** — `ActorDatabase.MhTalkIdOot=0x01D7 / MhTalkIdMm=0x02B2 / MhTalkId(bool)`; schema
  `MhTalkDef()` registered in OoT dict @0x01D7 + MM dict @0x02B2; Program.cs self-test updated. Builds clean. ✅
- **Demos regenerated** — generator places En_MhTalk 0x01D7 (visible) per setup; textId 0x1000+slot matches.
  4 variants in `WorkFolders/megaton_mhprojs`. ✅

## REMAINING
- **SoH build** (running, background id b0zygr3ya, log src/MegatonHammer/bin/soh_build.log) — verify compiles;
  fix any errors. Then user playtests SoH demos.
- **2Ship interpreter** — NOT yet added (demos are OoT/SoH). 2Ship actor file IS written. MM two-choice code
  (0xC2) isn't mapped in 2Ship CustomMessage; MM prompt support limited. Do in an MM pass. `2Ship/mm/2s2h/
  DeveloperTools/DebugConsole.cpp:486` uses CustomMessage::Entry (different struct from SoH).
- **Commit** — after SoH build succeeds: commit editor+docs to MAIN private repo; regen forks/patches/*.patch
  for SoH (debugconsole+actor+actor_table) and 2Ship (actor+actor_table). PRIVATE ONLY (no public/tag).
- Editor 3D preview of En_MhTalk (0x01D7/0x2B2) — no model resolver entry yet (shows nothing/placeholder in
  editor; renders fine in-game). Optional polish.

## ORIGINAL DIAGNOSIS (kept for reference)
Status was: PAUSED for host reboot 2026-07-18.

## The problem (diagnosed, confirmed)
The OoT dialogue-demo maps don't show any talkable NPC / dialogue on SoH. Root causes:

1. **En_MhTalk (actor id 0x0230) is NOT a real actor in the forks.** OoT vanilla
   `ACTOR_ID_MAX = 0x0192` (`SoH/soh/include/z64actor_enum.h`). 0x0230 is out of range;
   `portable/ovl_En_MhTalk/*` is *reference source*, never compiled into SoH/2Ship. So placing
   it spawns nothing — the user saw only the marker pot.
2. **The fork's only dialogue mechanism is text-override.** `SoH/soh/soh/Enhancements/debugconsole.cpp`
   (~line 1789) loads `mh/messages` and registers an `OnOpenText` hook per textId: when a REAL actor
   opens that textId it swaps in the custom text (`MhOnOpenText`, ~1800). `MhMsgEntry` stores only
   `{text,type,pos}` — **no give-item / rupee-charge / branch.** `O2RPacker.BuildMessagesJson`
   (~line 289) emits only `{id,type,pos,icon,text}` for `IsOverride` messages.

## User decision
Asked how to make dialogue work; answered: **"both — build actor into forks, AND engine-free methods."**
(Earlier "don't touch the engine" was about the lighting bug; adding playtest dialogue tooling is now
explicitly authorized.)

## Chosen architecture (unified, minimal-risk)
The editor already models real, param-textId talk actors whose text the OnOpenText hook already
overrides (`ActorParamSchema.cs`): **En_Kanban sign 0x0141 (textId 0x300+n)**, Elf_Msg / Elf_Msg2
(0x100+n), En_Wonder_Talk2 0x0185 (0x200+…). These are the **engine-free** path — visible/real,
work today, text-only.

Plan = drive outcomes through the SAME authored textIds via an extended C++ interpreter, plus
optionally a real visible En_MhTalk actor built into the forks:

### A. Editor (C#) — needed for outcomes either way
- `Export/O2RPacker.cs BuildMessagesJson`: also emit `isPrompt`, `choice1`/`choice2`,
  and per-outcome `giveItem`, `rupeeCost`, `fireFlag`, `nextMsgId` (from `MhMessage.Outcome1/2`).
- Consider emitting non-`IsOverride` messages too, or make the generator mark demo messages `IsOverride=true`
  (they already are).

### B. Fork SoH (`debugconsole.cpp`) — the "generic interpreter" that was always intended
- Extend `MhMsgEntry` + `MhLoadMessages` to parse the new outcome fields into memory.
- Add a hook (OnGameFrameUpdate or OnDialogMessage, see below) that watches the currently-open
  authored textId; on box close / choice confirm, apply outcome: `Rupees_ChangeBy(-cost)` (guard
  affordability), give item (`GiveItemEntryWithoutActor` already used in debugconsole.cpp GiveItemHandler
  ~line 412), set flag, branch (`Message_ContinueTextbox`).
- Available hooks (confirmed): `OnOpenText(uint16_t*,bool*)` and `OnDialogMessage()`
  (`GameInteractor_HookTable.h` lines 62,90; OnDialogMessage fired in `z_message_PAL.c:4465`).
  `GiveItemEntryWithoutActor` exists (debugconsole.cpp ~412). Flag/item hooks: OnItemReceive,
  OnFlagSet in HookTable.
- Talk element = a real vanilla actor at the authored textId (En_Kanban sign, or an Elf_Msg region,
  or a visible NPC whose textId we override). Outcomes then fire regardless of which actor opened it.

### C. Fork build — real En_MhTalk actor (the "build actor into forks" half)
- **PENDING RESEARCH** (Explore agent was mapping this when we paused — RE-LAUNCH on resume):
  how SoH/2Ship register a NEW actor id above vanilla max; internal (statically-linked, non-DMA)
  actor pattern; simplest ActorProfile to copy; drawing a DL from OBJECT_GAMEPLAY_KEEP so it's
  visible with no new object. Report wanted: exact files/lines for gActorOverlayTable, ACTOR_ enum,
  a minimal actor example, and 2Ship differences.
- If feasible: build En_MhTalk in with a small gameplay_keep model → a VISIBLE talkable NPC with
  full outcomes (best result). If not clean, fall back to signs/regions + the C++ interpreter (B),
  which fully delivers text + outcomes engine-free-ish.

### D. Generator (`SelfTest/TestTempleBuilder.cs BuildOotSetupDemo`)
- Replace the invisible En_MhTalk (0x0230) + marker pot with a working visible talk element per setup:
  En_Kanban sign (guaranteed) and/or real NPC + built En_MhTalk. Keep 4 setups, per-setup distinct
  NPC + different authored tree (textId per setup), rupees for purchases.
- Keep the lighting fix already shipped (IndoorLighting=true, Sky=None, per-setup day/night).

### E. Rebuild + regenerate
- `mh-build.ps1` (editor → Staging→Deploy). Rebuild SoH + 2Ship forks (they're submodules; changes
  go through `forks/patches/*.patch` — regen patches after). Regenerate demos into
  `D:\Copilot_OOT\WorkFolders\megaton_mhprojs`. Mark dialogue **needs playtest** (can't runtime-test here).

## Standing rules in effect
- **PRIVATE ONLY** — commit/push to origin/main; NO sync-public, NO release tag until user verifies.
- After editor changes run `mh-build.ps1` so Staging actually updates (else sync-deploy copies a stale exe).
- Rebuild editor + all 3 forks before asking user to test.

## Key facts / addresses
- OoT `ACTOR_ID_MAX = 0x0192`; `ACTOR_NUMBER_MAX = 2000` (z64actor.h).
- SoH mh dialogue: `SoH/soh/soh/Enhancements/debugconsole.cpp` ~1789-1837 (MhLoadMessages/MhOnOpenText),
  ~1847 MhPlaytestBootHook, ~412 GiveItemEntryWithoutActor.
- Editor msg model: `Editor/MhMessage.cs` (MhMessage/MhOutcome), `Export/MessageEncoder.cs`
  (& = newline, ^ = new box, %r/%g/… colour), `Export/O2RPacker.cs:289` BuildMessagesJson.
- Param-textId actors in editor: `Editor/ActorParamSchema.cs` En_Kanban 0x0141 (0x300+n), Elf_Msg/2
  (0x100+n), En_Wonder_Talk2 0x0185 (0x200+n).
- GetItem ids used: Deku Nuts 0x02, Bottle 0x0F, Bottle of Milk 0x14, Piece of Heart 0x3E,
  Recovery Heart 0x48, Deku Sticks 0x61. Rupee actor En_Item00 0x0015 var 2 = red(20).
- Related memory: [[megaton-hammer-fromscratch-lighting-and-mhtalk]], [[megaton-hammer-dialogue-editor]],
  [[megaton-hammer-public-sync-rule]].
