# N64 / PJ64 Playtest Parity Plan — inventory saves + debug controls

Status as of commit `e8863d2`. Companion to the SoH/2Ship OTR parity already shipped (gamestate
normalization `cec59b7`, time-of-day `ae6fee7`). Known-good baseline to revert to: **`ae6fee7`**.

The N64 path injects into the **OoT gc-eu-mq-dbg debug ROM** (auto-boot via `OotDebugAutoBoot.cs` +
the PJ64 fork hook `MegatonHammer.cpp`) and into **US-retail MM 1.0** (`MmInjectScene.cs` Play_Init
detour). The PJ64 fork already pokes `gSaveContext` after boot (linkAge, dayTime, nightFlag) using
`g_MMU->UpdateMemoryValue32` — the same mechanism every item below reuses.

---

## 1. Custom/empty inventory parity (PJ64) — behavioural match to SoH/2Ship `MhApplyCustomInventory`

**Editor half — DONE (`e8863d2`):** `Project64Playtest.cs` writes `%TEMP%\MegatonHammer\mh_n64_inv.json`
(the same payload string as OTR `mh/info`'s `inv`, from `PlaytestInventory.ToPayloadJson`) when the
inventory mode is `custom`; deletes a stale file otherwise. The params txt already carries
`inventory=0|1|2` (0=debug,1=custom,2=empty).

**Fork half — TODO (`MegatonHammer.cpp`):** apply the loadout AFTER the debug save runs, in `MhDoWarp`
(OoT) / `MhDoWarpMM` (MM) — same spot/timing as the working linkAge poke. Steps:
1. `MhLoadParams`: when `inventory != 0`, `fopen` `mh_n64_inv.json` and stash the text.
2. RMW poke helpers (N64 big-endian; only 32-bit accessors exist):
   - `PokeU32(base,off,v)` = `UpdateMemoryValue32(base+off, v)` (aligned fields only).
   - `PokeU16(base,off,v)`: read word @(off&~3); halfword is **hi** half if `(off&2)==0` (`<<16`,
     mask `0x0000FFFF`) else **lo** half (mask `0xFFFF0000`).
   - `PokeU8(base,off,v)`: word @(off&~3); `shift=(3-(off&3))*8`; `(w & ~(0xFF<<shift)) | (v<<shift)`.
3. For deterministic empty/custom, first **clear** the loadout-owned fields (items[], ammo[],
   inventory.equipment, upgrades, questItems, equips.equipment, equips.buttonItems[0], magic fields),
   then for `custom` write the parsed spec. Leave dungeonItems / sceneFlags alone.
4. Minimal hand JSON parser for the fixed schema (`"hearts":N`, `"tiers":{...}`, `"toggles":[...]`) —
   keys are unique so `IntAfterKey(json,"sword")` / substring `"\"tatl\""` membership suffice; no JSON lib.

### OoT gc-eu-mq-dbg SaveContext offsets (relative to `kSaveContext = 0x8011A5D0`, the `save` substruct)
entranceIndex 0x00 · linkAge 0x04 · dayTime 0x0C · nightFlag 0x10 · **healthCapacity 0x2E (s16)** ·
**health 0x30 (s16)** · **magicLevel 0x32 (s8)** · **magic 0x33 (s8)** · **rupees 0x34 (s16)** ·
**isMagicAcquired 0x3A (u8)** · **isDoubleMagicAcquired 0x3C (u8)** · **equips.buttonItems[0] 0x68 (u8)** ·
**equips.equipment 0x70 (u16)** · **inventory.items[24] 0x74** · **inventory.ammo[16] 0x8C** ·
**inventory.equipment 0x9C (u16)** · **inventory.upgrades 0xA0 (u32)** · **inventory.questItems 0xA4 (u32)** ·
**magicCapacity 0x13F4 (s16)** · gameMode 0x135C.

Bit math (bake as literals): `OWNED_EQUIP_FLAG(type,val)=(1<<val)<<(type*4)` (SWORD0/SHIELD1/TUNIC2/BOOTS3);
equips nibble = clear `0xF<<(type*4)`, OR `val<<(type*4)`; upgrades shift table QUIVER0 BOMB_BAG3
STRENGTH6 SCALE9 WALLET12 BULLET_BAG14; questItems `|= 1<<QUEST_*` (medallions 0-5, songs 6-0x11,
stones 0x12-0x14, agony 0x15); items[] write `ITEM_*` id at `0x74+SLOT_*` (ocarina SLOT=7).

### MM US-retail SaveContext offsets (`kMM_SaveContext = 0x801EF670`, base = `Save`; saveInfo nested)
hasTatl 0x22 (u8) · playerData.healthCapacity 0x34 · health 0x36 · magicLevel 0x38 · magic 0x39 ·
isMagicAcquired 0x40 · isDoubleMagicAcquired 0x41 · equips.equipment 0x6C (u16) · inventory.items[48] 0x70 ·
ammo[24] 0xA0 · upgrades 0xB8 (u32) · questItems 0xBC (u32) · SaveContext.magicCapacity 0x3F2E.
MM swords/shields are equip-only (nibbles, no owned bits); `tatl` toggle → hasTatl. **Verify MM
`gUpgradeMasks`/`gUpgradeShifts` (esp. wallet) against `mm-main z_parameter.c` before baking literals.**

Recommendation: implement OoT first (complete + buildable), then MM. Render verification is the user's
N64 test (PJ64 render verify has always been user-side).

---

## 2. Debug controls — OoT gc-eu-mq-dbg

**Every debug feature is compiled into the debug ROM and NONE read the debug save** — so dropping
`Sram_InitDebugSave` (use empty/custom) keeps them all working. The real switch is **controller-port
presence**, gated by `gIsCtrlr2Valid` (re-derived each frame from `validCtrlrsMask`):
- **Map Select** (jump from gameplay): **L+R+Z on controller 1**, but gated by `gIsCtrlr2Valid`
  (`graph.c:405`). Force it on by poking `gIsCtrlr2Valid = 1` every frame in `MegatonHammer_PerFrame`
  (resolve its gc-eu-mq-dbg address from `code_800D31A0.c`), OR by emulating a port-2 controller in PJ64.
- **Reg/HREG editor**: controller-2 D-pad (`game.c:88`). Needs real port-2 input → emulate pad 2.
- **Free/debug camera (no-clip view)**: `gDebugCamEnabled` (default false) toggled by START on
  **controller 3**; movement on pad 3 (`DEBUG_CAM_CONTROLLER_PORT=2`). Emulate pad 3; no poke needed.

**Plan:** add a playtest option "Debug controls (N64)"; when ON, (a) the C# autoboot keeps the empty/
custom inventory, (b) the PJ64 fork pokes `gIsCtrlr2Valid=1` each frame so **L+R+Z map-select works with
one controller**, and optionally (c) document enabling emulated pads 2/3 for reg-editor/free-cam. OFF by
default = no poke, debug hotkeys inert. No debug inventory required.

---

## 3. Debug controls — MM US-retail 1.0

No `#if` debug gating in MM; debug code was physically removed from the shipped source. Status is binary:
- **Map/scene select menu (`ovl_select`) — PRESENT in the ROM**, but **the entry path is gone** (nothing
  transitions into `MapSelect_Init`). **Plan A (highest value / lowest risk):** inject an L+R+Z detour
  (mirroring `MmInjectScene.BakeAutoBoot`/`BakeLevelSelect`) on a per-frame gamestate main that does
  `STOP_GAMESTATE; SET_NEXT_GAMESTATE(MapSelect_Init, sizeof(MapSelectState))` (resolve `MapSelect_Init`
  VRAM from the gamestate overlay table; the overlay DMAs in on demand). Poke `fileNum != 0xFF` before the
  load so `MapSelect_LoadGame` skips `Sram_InitDebugSave` (no debug inventory).
- **Free-move / no-clip — REMOVED.** Inject a small `Play_Update` detour (same idiom as the Play_Init
  detour) reading a runtime toggle byte (default 0): on L+D-pad, add velocity to
  `GET_PLAYER(play)->actor.world.pos` and zero `bgCheckFlags`/velocity each frame to skip collision. ~20-40
  instructions, reads only runtime state.
- **Free debug camera — driver REMOVED** (`db_camera.c` absent; `gDbgCamEnabled` flag + hooks survive but
  nothing drives the eye). Defer — the no-clip above covers the need.
- **Reg/actor/flag viewers — REMOVED** (`z_debug.c` is only `Regs_Init`). Out of scope.

**Plan:** ship Plan A first (L+R+Z → real `ovl_select`, inventory-suppressed); then the no-clip
`Play_Update` detour with a toggle byte. Both OFF unless the user opts in (MH simply doesn't write the
bytes). Do NOT reuse 2Ship's `BetterMapSelect.c` — that's the OTR/PC engine, not N64.

**Key MH injection sites to extend:** `MmInjectScene.cs` `BakeAutoBoot` / `BakePlayInitMenuFix` /
`BakeLevelSelect` (the hand-assembled MIPS detour idiom).

---

## Recommended sequencing
1. PJ64 **OoT inventory** poke (closes the last gamestate-parity gap; proven poke idiom).  ← do with user N64 test
2. PJ64 **MM inventory** poke (after verifying MM upgrade masks).
3. MM **L+R+Z map-select** injection (Plan A — reuses present retail code).
4. OoT **gIsCtrlr2Valid** force for one-controller map-select.
5. MM/OoT **no-clip** detour + toggle.
