# Playtest starting inventory

The playtest dialog can launch SoH/2Ship with a **custom starting inventory** authored in a full
per-game editor (Playtest ▸ Custom ▸ **Edit Inventory…**).

## Editor side

- **Model** — `Editor/PlaytestInventory.cs`: heart count, tiered upgrades (`Tiers`), and a set of
  on/off `Toggles`. `Editor/InventoryCatalog.cs` defines the per-game catalogue laid out to mirror
  the in-game subscreens — OoT's 6×3 C-item grid, **MM's 6×4 24-mask pane**, songs, medallions/
  stones, remains, equipment — plus tier dropdowns (sword, shield, wallet, quiver, …).
- **UI** — `Forms/InventoryDialog.cs`: checkbox grids + dropdowns, a hearts spinner, a preset
  dropdown (built-in **Default / Nothing / Full inventory** + named user presets), **Load / Save As
  / Delete**, and **Reset** (to Default).
- **Persistence** — `EditorSettings`: the last-used inventory is remembered **per game across
  restarts**; named presets are stored per game too. State is independent for OoT and MM.
- **Defaults** (match the spec):
  - **OoT** — 3 hearts, Kokiri Tunic + Boots, Kokiri & Master Sword, Deku & Hylian Shield, Child
    Wallet, Goron Bracelet, Fairy Ocarina.
  - **MM** — 3 hearts, Child Wallet, Kokiri Sword, Hero's Shield, Deku/Goron/Zora Masks, Ocarina of
    Time, Song of Time.
  - **Nothing** — 3 hearts (+ Kokiri tunic/boots for OoT). **Full** — everything within reason
    (all toggles, maxed tiers, 20 hearts).

`MegatonHammer --testinv` verifies defaults/presets/JSON-round-trip/payload (ALL PASS).

## Payload

When the playtest is launched in **Custom** mode, the inventory is emitted into the mod O2R's
`mh/info` JSON as an `"inv"` object:

```json
"inv": { "hearts": 3, "tiers": { "sword": 2, "shield": 2, "ocarina": 1, "wallet": 0, "strength": 1 },
         "toggles": ["boots_kokiri", "tunic_kokiri"] }
```

Tiers grant everything **up to** the chosen level (e.g. `sword:2` = Kokiri + Master).

## Engine side (boot hook)

`forks/patches/soh-mh_playtest.patch` and `forks/patches/2ship-mh_playtest.patch` extend each fork's
playtest boot hook: for `inventory == "custom"` they start from a fresh save (`Sram_InitNewSave`) and
apply the `inv` payload onto `gSaveContext` — heart capacity, equipment tiers (sword/shield, tunics/
boots), upgrades (`Inventory_ChangeUpgrade`), C-items/masks (`INV_CONTENT` + starting ammo), and
songs/medallions/stones/remains (quest-item bits). Toggle keys map 1:1 to the decomp `ITEM_*` /
`EQUIP_*` / `QUEST_*` enums.

**Rebuild required.** The patches are regenerated source diffs against the pinned upstream submodule
commit. Apply + rebuild the engines to pick up custom-inventory support:

```
git submodule update --init --recursive
forks\apply-mh-patches.cmd
SoH\mh_build.cmd     ::  and / or
2Ship\mh_build.cmd
```

> **SoH/OoT verified end-to-end** (2026-06-23): rebuilt soh.exe with the patched boot hook, packed a
> Default-inventory playtest O2R (`--packplaytest`), launched, and confirmed `mh_playtest_boot.log`:
> `healthCap=48` (3 hearts), `equipment=0x1133` (Kokiri+Master sword, Deku+Hylian shield, Kokiri
> tunic+boots), `upgrades=0x40` (Goron Bracelet), `ocarinaSlot=7` (Fairy Ocarina), `bSword=60`
> (Master Sword equipped) — every value matches the spec. The 2Ship/MM apply mirrors this code but
> hadn't been rebuilt at the time of writing. Empty/Debug modes are unchanged.
