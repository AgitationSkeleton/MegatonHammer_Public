# OoT Playtest Inventory Parity Plan

Goal: bring OoT custom/empty playtest inventory to the same verified state we just reached for
MM, on **both** engines — N64 (PJ64 fork) and SoH (OTR). This is a plan/roadmap; each phase ends
with a concrete verification. No guessing — every offset/behaviour is checked against the OoT
decomp (SoH `soh/src` / `include`) before it's asserted, the way the MM Deku-B fix was.

Status legend: ✅ done/verified · ⚠️ exists but unverified/suspect · ❌ missing.

---

## Current state (investigated 2026-07-03)

### N64 — editor side (`Rom/N64SavePokes.cs::ComputeOoT`)
Already the most complete of the two games. Emits struct-relative pokes (base = the fork's
`kSaveContext`) for:
- Hearts (`healthCapacity`/`health`), magic (isMagic/level/double/cap/meter).
- Owned + equipped equipment nibbles (sword/shield/tunic/boots; Kokiri tunic+boots always).
- **B-button = sword item** (`buttonItems[0]`), Biggoron-sword bgsFlag + swordHealth (#10).
- **C-buttons already cleared** (`buttonItems[1..3]` + `cButtonSlots[0..2]` = ITEM_NONE) — the
  OoT analogue of the MM C-button fix is already in place. ✅
- items[], ammo[], quest items, upgrades (quiver/bombbag/strength/scale/wallet/bulletbag),
  player name (#21), Z-target (#5).

Gaps to confirm/close:
- ❌/⚠️ **Bottles**: verify bottle contents land in the right slots (bottle 1..4) with the right
  item ids; SoH has 4 bottle slots. Check `OotItems` "bottle" (slot 18) vs SoH's per-bottle logic.
- ⚠️ **Child vs Adult equips**: OoT `SaveContext` has separate `childEquips` and `adultEquips`
  (each an `ItemEquips`) plus the live `equips`. `ComputeOoT` writes only `equips`. Confirm whether
  the debug save + our poke leave the *selected age's* B/C buttons correct after the age is forced
  (the playtest sets `linkAge`). If not, also poke the matching `childEquips`/`adultEquips`.
- ⚠️ **Dungeon items** (`inventory.dungeonItems` boss key/compass/map, `dungeonKeys`), **double
  defense** hearts, **trade/adult-trade** items — audit vs SoH `MhApplyCustomInventory` field list.

### N64 — fork side (`Project64/.../MegatonHammer.cpp`)
- `kSaveContext = 0x8015E660` (gc-eu-mq-dbg debug base), `kPlay_Init = 0x8009A750`.
- Inventory applied by `MhApplySavePokes(kSaveContext)` **inside `MhDoWarp`** (line ~480), which
  only runs after `MhScanForPlayState(0x8000, kPlay_Init)` finds a PlayState (`init == Play_Init`).
- ⚠️ **PRIME SUSPECT**: this is the *same* `init == Play_Init` scan that failed for MM during
  gameplay (init is only briefly Play_Init). If the OoT scan misses the window under auto-boot, the
  inventory silently never applies → the player keeps the debug loadout, exactly the MM symptom.
  MUST be runtime-verified; if broken, apply the MM-style fix: decouple the inventory apply from the
  PlayState scan and gate it on a robust "save is up" signal (e.g. `save.entrance != 0` /
  `gameMode` in 0..3 for N frames) writing to the fixed `kSaveContext`. See
  `[[megaton-hammer-n64-debug-controls]]` for the MM precedent (fork commit 1db1275).

### SoH — engine side (`SoH/soh/soh/Enhancements/debugconsole.cpp::MhApplyCustomInventory`)
- Boots a **clean save**, so C-buttons start empty (no clear needed, unlike the N64 debug save).
- Sets `equips.buttonItems[0]` = sword, `equips.equipment` sword/shield/tunic/boots nibbles, etc.
- OoT has **no transformation forms**, so there is no direct Deku-B analogue. The nearest
  action-button quirks to check: ocarina auto-slot, Biggoron sword (bgsFlag/health), bottle
  contents, and whether the chosen age's button layout is honoured.

---

## Phased plan

### Phase 1 — N64 apply reliability ✅ DONE (2026-07-03, fork 6a2bfd0 / editor c50031c)
CONFIRMED the bug then fixed it: the OoT inventory/age were poked only inside `MhDoWarp`, gated
behind `MhScanForPlayState(kPlay_Init)`, and `GameState.init` is **cleared during gameplay** so the
scan never found the PlayState under auto-boot (linkAge stayed 0, custom pokes never ran — the HUD
kept the debug loadout). Fixes shipped:
1. `MhScanForPlayState` now matches by **size** (~0x12518) + valid pointers, not init.
2. Inventory + dayTime applied to the fixed `kSaveContext` **decoupled from the scan**, gated on
   `entranceIndex != 0` + valid gameMode, 3× spaced. HEADLESS-VERIFIED:
   `OoT custom inventory applied @0x8015E660 (69 pokes, pass 1..3)`.
3. **Age** poked pre-scene-load by `OotDebugAutoBoot` (linkAge @ gSaveContext+0x04) so Link's model
   is correct — a post-load poke can't swap the already-loaded model.
Remaining: interactive confirmation of the HUD loadout + adult/child model swap.

### Phase 2 — N64 content audit (field-by-field vs SoH)
- Diff `ComputeOoT` output against SoH `MhApplyCustomInventory` for the same payload (the SoH path
  logs `[mh_inv] applied: equip=… upgrades=… quest=…`). Close bottles / child-adult equips /
  dungeon items / double-defense / trade items as needed. Add asserts to `--testinv` (as done for MM
  masks + C-buttons).

### Phase 3 — SoH content audit + quirks
- Verify `MhApplyCustomInventory(OoT)` covers every editor toggle (bottles, trade items, medallions,
  stones, songs). Check the age-specific button layout and ocarina/Biggoron-sword edge cases.
- Confirm parity of the **empty** preset (Kokiri tunic+boots + 3 hearts, nothing else) between N64
  and SoH.

### Phase 4 — cross-engine verification
- Same custom payload → N64 (PJ64, headless log + interactive HUD) and SoH (interactive). Compare
  the resulting loadouts. Record in memory like the MM inventory entry.

---

## Notes / anti-guessing rails
- OoT `SaveContext` offsets are gc-eu-mq-dbg **debug** addresses (base 0x8015E660), NOT retail
  0x8011A5D0 — see `[[megaton-hammer-pj64-savecontext-base]]`.
- The N64 inventory pokes are struct-relative and applied to `kSaveContext`; verify each new offset
  against SoH `include/z64save.h` (`SaveContext`→`equips`/`inventory`/`playerData`) exactly as the
  MM offsets were verified against `2Ship/mm/include/z64save.h`.
- SoH boots a clean save; the N64 debug ROM boots the **debug** save (full inventory + C-buttons
  pre-equipped) — that asymmetry is why the N64 path must *clear* as well as *set*, and is the
  reason MM needed the C-button clear that OoT already has.
