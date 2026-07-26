# Optional SoH-exclusive items (Roc's Feather, later FD Mask) — design + research

Modular editor system for off-by-default, engine-exclusive items the user can enable, put in the
playtest inventory, and place in chests / item-assignable actors. Gated to the SoH engine (no N64/2Ship).

## Editor UX (requested)
- Editor Settings: master checkbox **"Enable SoH-exclusive items"** (default OFF).
- Below it, per-item checkboxes (greyed unless master ON), each default ON when master is on:
  **"Enable Roc's Feather"** (first), later **"Enable Fierce Deity's Mask"** (soh_fd fork).
- When enabled: the item appears in the Playtest inventory toggles (only when the launch engine = SoH),
  and in the chest/En_Item00 GetItem picker.

## Modular registry (editor)
`OptionalItem { Key, DisplayName, Engine (SoH), InvToggleKey, GetItemId, ModIndex, GrantMethod, EnableCVar, SettingField }`.
Everything gates on `EditorSettings.EnableSohExclusiveItems && EditorSettings.Enable<Item>` && engine==SoH.

## Roc's Feather — SoH facts (research 2026-07-18)
- **Inventory id:** `ITEM_ROCS_FEATHER = 0x9D` (`include/z64item.h:313`; vanilla tops at 0x9B, ITEM_CUSTOM=0x9C).
  It has NO own slot — it TIME-SHARES `SLOT_NAYRUS_LOVE` with Nayru's Love (`gItemSlots` only covers 0x00-0x37).
- **Randomizer get id:** `RG_ROCS_FEATHER` (`randomizerEnums/RandomizerGet.h:321`), GetItemEntry in
  `randomizer/item_list.cpp:457` with **modIndex=MOD_RANDOMIZER**, GI id 0xE0, custom icon gRocsFeatherTex.
- **Persistence flag:** `RAND_INF_OBTAINED_ROCS_FEATHER` (`RandomizerInf.h:2643`).
- **Enable CVar (rando):** `RSK_ROCS_FEATHER` / `gRandomizerSettings.RocsFeather` (`settings.cpp:1315`).
- **Behavior:** `randomizer/RocsFeather.cpp` — ShipInit `RegisterRocsFeather` gated `{ "IS_RANDO" }`, and
  `shouldRegister = IS_RANDO && RAND_GET_OPTION(RSK_ROCS_FEATHER)` (line 23). Hooks: OnPlayerUpdate
  (air-jump reset), VB_CHANGE_HELD_ITEM_AND_USE_ITEM (the jump: link_rocs_feather_jump anim, upward vel
  7.0 child/7.5 adult), VB_DRAW_CUSTOM_ITEM_NAME. C-button usable (rides the Nayru slot).
- **Grant:** set flag + write slot — `INV_CONTENT(ITEM_NAYRUS_LOVE) = ITEM_ROCS_FEATHER;
  Flags_SetRandomizerInf(RAND_INF_OBTAINED_ROCS_FEATHER);` (`randomizer.cpp:1389`). OR give
  `RetrieveItemEntry(MOD_RANDOMIZER, RG_ROCS_FEATHER)` → `GiveItemEntryWithoutActor` (works via
  `z_actor.c:2037` MOD_RANDOMIZER path). Pause menu cycles Nayru↔Feather (RocsFeatherCycle.c).

## ⚠ THE BLOCKER (fork change required)
Behavior only registers under `IS_RANDO`. To work in an MH playtest (non-rando), relax
`RocsFeather.cpp shouldRegister` to ALSO accept an MH CVar (model on `SunlightArrows.cpp:44` /
`BlueFireArrows.cpp:31`, which OR a plain `CVAR_ENHANCEMENT` with the rando option). Plan:
1. Fork `RocsFeather.cpp`: `shouldRegister = (IS_RANDO && RAND_GET_OPTION(RSK_ROCS_FEATHER)) ||
   CVarGetInteger(CVAR_ENHANCEMENT("MhRocsFeather"), 0);` and drop/relax the ShipInit `{ "IS_RANDO" }` dep.
2. Fork `debugconsole.cpp MhApplyCustomInventory`: on a `rocs_feather` toggle → set the CVar,
   `INV_CONTENT(ITEM_NAYRUS_LOVE)=ITEM_ROCS_FEATHER`, `Flags_SetRandomizerInf(RAND_INF_OBTAINED_ROCS_FEATHER)`.
3. Chest/dialogue give: award `RetrieveItemEntry(MOD_RANDOMIZER, RG_ROCS_FEATHER)` (the interpreter already
   does MOD_NONE gives at debugconsole.cpp:2115 — extend for MOD_RANDOMIZER custom items).
4. Editor: emit the enable CVar + the rocs_feather toggle/give in mh/info; gate to SoH engine only.

## PJ64/N64 + 2Ship
SoH-ONLY. Item id 0x9D + custom O2R assets + ShipInit/GameInteractor + rando infra don't exist on cart.
Editor must NOT offer these for PJ64/N64 (and 2Ship unless separately ported).

## FD Mask — soh_fd fork research (2026-07-24) + PORT PLAN

**soh_fd = `D:\Copilot_OOT\WorkFolders\soh_fd`.** FD is NOT a clean ShipInit hook like Roc's Feather — it's a
**new link age baked into the player core**, so porting it is a large HAND-MERGE, not a cherry-pick.

**Repo skew (critical):** shared merge-base `948b84d8f`. soh_fd base `585530f68` (112 upstream commits AHEAD) + FD
squash `cdf2204e6`. OUR fork HEAD `b939f6171` = 12 MH commits ahead of merge-base = ~112 upstream commits BEHIND
soh_fd's base (z_player.c got a decomp pull in the gap). ⇒ hand-port hunk-by-hunk. Backup: SoH submodule tag
`mh-pre-fd-backup` / branch `mh-pre-fd-backup-branch`.

**Item/form:** `ITEM_MASK_DEITY=0x9F`, `ITEM_SWORD_DEITY=0x9E` (append after our 0x9D). `PLAYER_IA_MASK_DEITY=0x43`.
Owned = bool `gSaveContext.ship.hasFierceDeityMask`, equipped to a free C-button (`FierceDeity_EquipMaskToCButton`,
src/code/fierce_deity_items.c). FD = 3rd link age `LINK_AGE_DEITY=2`, `LINK_AGE_MAX 2→3`, `LINK_IS_DEITY`. ⚠
LINK_AGE_MAX 2→3 = highest blast radius (every `[2]`-indexed linkAge array must handle index 2 or OOB). Transform
hardcoded in z_player.c (+2332): PLAYER_IA_MASK_DEITY branch @4474 → cutscene → linkAge=DEITY; all LINK_IS_DEITY
gates. NOT ShipInit-gated. No master CVar exists → invent `CVAR_ENHANCEMENT("MhFierceDeity")` (key `fd_mask`); gate
the GRANT + transform-trigger on it.

**fd.o2r gen:** `soh/Extractor/FdO2rGen.{cpp,h}` + `FdO2rManifest.h` (137 ROM paths) + `assets/fd_bundled/**`.
`OTRGlobals.cpp:784` `FdO2rGen::Generate` when `NeedsGeneration()`, from the user's MM ROM (soh_fd = INTERACTIVE
picker). Mounts LAST via ArchiveManager::AddArchive (OTRGlobals.cpp:824). ⚠ FD needs fd.o2r to render (no fallback);
⚠ interactive picker HANGS headless harness. **User req: auto-gen fd.o2r from the AUTO-DETECTED MM ROM (like oot.o2r),
NOT a picker** → feed FdO2rGen the editor's MM ROM path.

**Port files:** COPY (low risk): fierce_deity_items.c, object_link_deity.h, FierceDeityMask*.{cpp,c,h},
TransformationMaskSafeguards.cpp, Extractor/FdO2rGen*+Manifest, assets/fd_bundled/**, extractor mm.txt/*_MM.txt,
soh_assets.h FD entries, ovl_En_M_Thunder beam. HAND-MERGE (high risk): z_player.c(+2332), z_player_lib.c, headers
(z64save/macros/z64item/z64player/variables/z64), z_parameter.c, z_kaleido_*, SaveManager.cpp, OTRGlobals.cpp
(gen+mount+FD voice), SohMenuEnhancements.cpp. Anchor/randomizer FD-touches OUT OF SCOPE. Smallest playtest grant:
`CVarSetInteger(MhFierceDeity,1); ShipInit::Init(...); gSaveContext.ship.hasFierceDeityMask=1;
FierceDeity_EquipMaskToCButton();` (still needs fd.o2r present).

Related: [[megaton-hammer-playtest-inventory]], [[megaton-hammer-items-chests]], [[aegiker-fd-mask-save-slot]].
