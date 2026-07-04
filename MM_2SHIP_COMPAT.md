# Majora's Mask / 2 Ship 2 Harkinian — compatibility assessment

Tested the editor pipeline against the retail **MM (USA) ROM** and the built **mm.o2r / 2ship**.
Summary: the game-agnostic parts, scene **import**, and the MM **playtest** path all work now.
The two MM-specific gaps below were closed (importer ported, OTR writer/packer made game-aware,
`mh_playtest` warp ported to the 2Ship fork).

## Works for MM now (verified)
| Area | Result |
|---|---|
| ROM load / detection | `Game = MM`, "ZELDA MAJORA'S MASK", 1535 files ✓ |
| Texture extraction | 6100 textures scanned from the MM ROM ✓ (format-agnostic) |
| Actor database (names/params) | 577 MM actors (SharpOcarina MM `ActorRendering.xml`/`ActorNames.xml`) ✓ |
| Object table (id→VROM) | 381 MM objects resolve (game-aware: reads `mm-main`) ✓ |
| Actor→object map | 424 MM actor→object maps (e.g. 0x11→object_niw) ✓ |
| Actor model rendering (D5) | works once an MM level is loaded (resolver is game-aware) ✓ |
| Brush editing / Face Edit / tools | game-agnostic — fully works ✓ |
| Export OBJ / VMF | game-agnostic — works ✓ |
| MM scene-name table | `MmSceneFiles`: all 102 MM scenes → `scenes/nonmq/{NAME}` (validated vs mm.o2r) ✓ |
| OTR resource format | identical to OoT (validated); our serializer is reusable for 2Ship ✓ |

## Closed gaps (done)
1. **Scene import** ✓ — `SceneImporter` is now game-aware: MM `SceneTableLocator.FindMM`
   (0x10-stride table validated by header content), MM scene-table entry size (0x10), and
   `MmSceneFiles` names. Verified on the retail ROM (Woodfall Temple → 13 rooms / 6141 tris).
   The mesh/collision readers are format-shared and work once the headers parse.
2. **2Ship playtest path** ✓ — `OtrSceneWriter`/`O2RPacker.PackOtr` take an `mm` flag: the scene
   archive path comes from `MmSceneFiles`, and `SetRoomBehavior` is emitted in MM's 6-byte form
   (matching 2Ship's `SetRoomBehaviorMMFactory`; every other scene/room command is byte-identical
   between the forks, verified against 2Ship's per-command factories). The `mh_playtest` warp
   console command is ported into the 2Ship fork (`DebugConsole.cpp`): it scans
   `sSceneEntranceTable` for an entrance that resolves to the target scene and warps via
   `nextEntrance`. Differential test: MM room resource is exactly +1 byte vs OoT (the 6-byte
   room behavior); the 2Ship object compiles clean.

## Still MM-only / unhandled (minor)
3. **MM-specific scene features** — time-of-day scripted actors (Stock Pot Inn etc.) ride on
   the alternate-header/setup system already parsed; MM cutscene actor lists (0x1B) and
   minimaps (0x1C/0x1E) are MM-only and unhandled.
4. **Transformation masks / Fierce Deity** — out of scope for level editing (gameplay actors).

## Status
MM is at parity with OoT for the core editor loop: ROM load, texture/actor/object data, scene
**import**, edit, and **playtest** into 2Ship all work. Remaining items (#3/#4) are MM-only scene
features that don't block level editing or playtesting.

The SoH/2Ship playtest engines are git submodules pinned to upstream; the Megaton Hammer
`mh_playtest` patches and build scripts live in [`forks/`](forks/) — see
[`forks/README.md`](forks/README.md) to build them from a fresh clone.
