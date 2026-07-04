# MM N64 room injection + debug testing plan (2026-06-25)

Plan to extend Megaton Hammer's OoT N64 inject-and-headless-boot harness to **Majora's Mask**, matching the
existing OoT path. The data-model groundwork is largely done; the work is a small injector branch, the PJ64
hook's MM addresses, and **acquiring an MM debug ROM**.

## Current OoT N64 path (baseline)

`--packplaytestn64` → `PlaytestPack.RunN64()` builds the project (`SceneExporter.BuildBinaries`) and injects
into the OoT **gc-eu-mq-dbg** debug ROM via `RomInjector.InjectDebug`:
- `SceneTableLocator.Find()` fingerprints OoT `gSceneTable` (**0x14-byte** entries) version-independently.
- `InjectDebug` writes scene+room files into free ROM with **no new dmadata entries** (the debug build's
  `DmaMgr_Init` walks a fixed `sDmaMgrFileNames[]`, so a grown table crashes boot) and repoints only the
  scene-table slot's `{vromStart,vromEnd}`. `PatchRoomList` overwrites the scene's `0x04` room-list VROM
  placeholders (the `0x00000000` pairs `RoomExporter`/`SceneExporter` emit) with each room's real VROM.
- `OotCrc` fixes the CIC-6105 checksum.
- Headless: `bootcheck.ps1` runs PJ64 with the `forks/pj64/MegatonHammer.cpp` drop-in (per-frame heartbeat,
  `MH_MAXFRAMES`, reads `mh_n64_playtest.txt`, observes `gSaveContext`/PlayState at gc-eu-mq-dbg addresses,
  pokes `nextEntranceIndex`+`transitionTrigger` to auto-warp), and parses `mh_n64_playtest.log`.

**N64 injection is hard-gated to OoT** (`Project64Playtest.cs:53`, `RomInjectDialog.cs:119`). SoH/2Ship
(custom-engine) playtest is separate and already MM-capable.

## What already branches for MM (foundation in place)

- `SceneTableLocator.FindMM()` — MM `gSceneTable` uses **0x10-byte** entries (one `sceneFile` RomFile +
  `titleTextId` + drawConfig; no second title-card RomFile).
- `SceneImporter`, `MmSceneFiles` (~102 MM scenes), `RomImage` MM detection ("MAJORA"/cart "Z2"), and the
  whole O2R/2Ship export path are MM-aware.
- The room-list format (`0x04`) and `OotCrc` (CIC-6105) are shared between games.

## MM-vs-OoT injection deltas to handle

1. **Scene-table entry = 0x10, not 0x14.** Injector must use `FindMM` + 0x10 stride and write `sceneFile`
   {start,end} at slot `+0/+4` only. No OoT-style title card to generate.
2. **Entrances.** MM doesn't warp by a debug map-select slot the way OoT's does; it uses
   `sSceneEntranceTable`/`sEntranceTable`. The PJ64 hook needs MM entrance encoding + the table scan (the
   2Ship engine path already does this for the custom engine; the N64 hook must replicate it).
3. **PJ64 constants are gc-eu-mq-dbg-only.** MM needs its own `gSaveContext`, `Play_Init`, and PlayState
   field offsets, pinned from the **zeldaret/mm** symbol map for the specific debug build.
4. **Debug DMA strategy reusable.** MM debug `DmaMgr_Init` similarly walks a filename array, so the
   no-dmadata free-space write (as in `InjectDebug`) is the right approach for MM too (untested).

## Plan

**A. Injector (C#)** — `RomInjector`: ✅ **DONE (2026-06-25)**
- `InjectDebug(..., bool mm = false)` now branches on `mm`: `SceneTableLocator.FindMM` + the new
  `MmEntrySize` (0x10) stride; `sceneFile` written at slot `+0/+4` (same in both games); reuses
  `RomBuilder.WriteFileAt` (no dmadata), `PatchRoomList`, `PadToPow2`, `OotCrc` (CIC-6105, shared). No
  title card for MM. Compile-verified; **boot test pending the MM debug ROM**. (The GUI append path
  `RomInjector.Inject` + `RomInjectDialog`/`Project64Playtest` gates remain OoT-only — un-gating those
  and `RunN64` is the remaining wiring, below.)
- Confirm `SceneExporter.BuildBinaries` emits MM-correct **N64** scene/room commands (e.g. 6-byte MM
  `SetRoomBehavior`) on the N64 path (today exercised OoT-only).
- Un-gate the two `Game != RomGame.OoT` checks once the MM path exists.

**B. Entry point** — ✅ **DONE & validated against the real EU MM debug ROM (2026-06-25):**
`PlaytestPack.RunN64` takes a bare `mm` flag (`--packplaytestn64 mm`): builds MM-aware binaries
(`ActorObjectResolver.Build(mm:true)` + `BuildRomTexResolver(mm:true)`), injects via `InjectDebug(mm:true)`
into `MmDebugRomPath`, reports scene-table status. `RomImage` loads the `.n64` build (6121 MM textures),
`FindMM` locates `gSceneTable` @ 0x2A3D0, slot repointed, 64 MB ROM written — `[n64-mm] PASS`.

**C. PJ64 hook + MM boot** — ✅ **MM BOOTS IN PJ64 (2026-06-25).** `forks/pj64/MegatonHammer.cpp` detects MM
and uses the MM addresses (gSaveContext 0x801EF670, Play_Init 0x8016A2C8, PlayState sceneId@0xA4 /
transitionTrigger@0x18875 / nextEntrance@0x1887A; MhDoWarpMM). The fork's bundled plugins (Glide64 + HLE
RSP) hung MM at 0x8009F2B4; the fix was an **architecture + plugin** issue, not the hook:
- Build the fork **32-bit** (`pj64_build_x86.bat`) so it can load 32-bit plugins.
- Use the user's **GLideN64 3.0 + RSP 1.7** (32-bit, from their stock PJ64 2.4) — RSP 1.7 completes the MM
  RSP task the HLE RSP stalled on.
- Force the **interpreter** (`MH_INTERP=1`) — the 4.0 fork's recompiler crashes on MM.
- Run dir `pj64run_x86/` (clone of the 2.4 install + the Win32 fork exe + the fork's cfg → GLideN64/RSP1.7).
- Validated: retail MM + the injected debug ROM boot, render the title screen, hook reads MM (gameMode 0→1).
Also added headless popup suppression (gated on MH_HEADLESS): N64System.cpp catch + Project64-video rdp.cpp.
**Remaining:** an MM-authored test scene (OoT Test Temple geometry won't render under MM) + wiring the warp
(hook nextEntrance or the debug scene-select) + an MM harness variant (pj64run_x86 + MH_INTERP) in bootcheck.

**D. Debug ROM** — ✅ **available.** `MmDebugRomPath` =
`READ_ONLY_GameROMs\Legend of Zelda, The - Majora's Mask (Europe) (En,Fr,De,Es) (Debug Version).n64`
(64 MB uncompressed EU debug build). RAM-symbol pinning for the PJ64 hook is the remaining sub-task.

## Debug ROM acquisition

The standard MM debug build (analogous to OoT's gc-eu-mq-dbg "THE LEGEND OF DEBUG") is the **zeldaret/mm
N64 debug ROM** ("Zelda Majora's Mask" debug build). Expected filename mirrors OoT's `ZELOOTD.z64` →
**`ZELMMD.z64`**. It is **not present** in the repo today (the only MM ROM is the 32 MB retail `mm.z64` used
for OTR/O2R asset extraction). Place it per `roms/README.md`. *(I cannot download ROMs — this is a
user-supplied dependency.)*

## Effort / risk

- Injector MM branch (0x10 entries, no title card): **low**, foundation exists.
- Un-gating the OoT checks: **trivial**.
- PJ64 hook MM addresses + entrance warp: **medium**, and gated on having the debug ROM + its symbol map.
- **Hard dependency / blocker:** acquiring the MM debug ROM. Everything else can be built and unit-checked
  against the retail `mm.z64` for table-locating, but a *boot* test needs the debug ROM.

Cross-refs: [[megaton-hammer-n64-playtest]] (the OoT PJ64 harness this extends), [[megaton-hammer-forks]].

## ✅ SOLVED (2026-06-25): editor → vanilla-MM N64 injection renders & is walkable

The MM N64 path now works on the **US retail** ROM (matches `mm-main` decomp; the EU debug ROM
was abandoned — it's compressed and has no matching decomp, so `gSceneTable` couldn't be located).

Pipeline (`--injectmmscene`, `MmInjectScene.cs` + `MmRomDecompressor.cs`):
1. **Decompress** the whole ROM (`MmRomDecompressor`: Yaz0-decode every dmadata file, rewrite the
   table so ROM==VROM uncompressed). Validated byte-perfect vs an independent decoder.
2. **Locate** `gSceneTable` @ vrom **0xC5A1E0** (MMR's `SCENE_TABLE`); KAKUSIANA=slot7, Termina
   Field=slot **0x2D** (a 1-room scene that cold-loads via map-select).
3. **Build** the scene/room with the editor's real `SceneExporter` (solid-colour textures).
4. **Inject** by keeping the vanilla target scene SHELL and pointing EVERY header (primary + the
   9 alt headers) at my data: room0 overwritten; collision relocated to free space (0x35000) and
   every cmd 0x03 repointed; every cmd 0x04 → my room list; every cmd 0x00 spawn moved into my room.
5. **drawConfig → DEFAULT(0)**, **bake the US level-select** GameShark (`811BDA..`, press START at
   title → map-select), **CIC-6105** checksum.

### Three root-cause crashes found & fixed
- **drawConfig MAT_ANIM**: slot's draw config calls `AnimatedMat_Draw(sceneMaterialAnims)`, a
  pointer only set by scene cmd 0x1A → null deref on first draw. Fix: force DRAW_CFG_DEFAULT.
- **sceneLayer ≠ 0 needs an alt header**: the map-select's debug save sets `cutsceneIndex ≥ 0xFFF0`,
  so `sceneLayer = (cutsceneIndex&0xF)+1 ≠ 0`; the game then *requires* the scene's alt header. A
  minimal single-header scene falls back to the primary and hangs in `Play_Init`. Fix: reuse the
  vanilla scene's alt-header structure and patch all headers (above).
- **Data overlap**: first all-headers attempt overwrote collision at the shared 0xdce8, which
  overlapped an alt header's spawn/room data. Fix: relocate collision to free space (0x35000).

Result: custom room **loads, renders, and Link walks on the authored floor**. Remaining polish:
**wall/pillar collision is non-solid** (only the floor poly registers — likely the box-collision
normal classification or wall thickness in the editor's collision builder).
