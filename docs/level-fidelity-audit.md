# Level Load / Edit / Compile Fidelity Audit (OoT + MM, SoH + 2Ship)

Date: 2026-06-23. Scope: verify that vanilla scenes/rooms/dungeons are loadable, editable,
compilable and testable, that an unmodified recompile preserves the level 1:1, and that all
aspects (including OoT age and MM time-of-day setups) are surfaced and editable Hammer-style.

## Architecture finding (root cause of most gaps)

The editor uses a **re-synthesis** pipeline, not a pass-through one. Import parses a subset of
the scene/room into editable document objects; **no original bytes are retained**
(`SceneImporter`/`ImportedScene` hold only parsed fields). Export
(`SceneExporter.BuildBinaries`, `OtrSceneWriter.BuildLevel`) rebuilds everything from the
document. Consequence: anything the document does not model is **lost on recompile**, so an
unmodified import→export is *functionally* similar but **not 1:1**.

## Incongruity log

Severity: 🔴 breaks the user's explicit requirement · 🟠 data loss / not editable · 🟡 minor.

| # | Aspect | Status | Sev | Evidence |
|---|--------|--------|-----|----------|
| 1 | **Alternate headers / setups (0x18)** — OoT child/adult, MM time-of-day (Stock Pot Inn) | Parsed into `ImportedScene.Setups` but **not surfaced, not editable, not exported** → lost on recompile | 🔴 | `SceneImporter.cs:279 ParseAltHeaders`; no UI; `SceneExporter`/`OtrSceneWriter` emit no 0x18 |
| 2 | **1:1 round-trip** | Re-synthesis only; not byte- or fully-functionally identical | 🔴 | `SceneExporter.BuildBinaries`, `CollisionBuilder`, `DisplayListBuilder` rebuild from document |
| 3 | **Collision** (surface types, camera data, waterboxes) | Only exit-trigger volumes imported; full collision re-triangulated from brushes on export; surface types / camera / waterboxes dropped | 🟠 | `SceneImporter` exit parse only; `CollisionBuilder.cs` rebuild; camData always null |
| 4 | **Imported visual geometry** | Read-only backdrop; export rebuilds mesh from brushes; original DLs/UVs not preserved | 🟠 | `RoomMeshReader` (read-only); `DisplayListBuilder` |
| 5 | **Cutscenes (0x17)** | Dropped entirely on import and export | 🟠 | not parsed; not written |
| 6 | **Multiple lighting environments (0x0F)** | Only one env imported/exported | 🟠 | `SceneExporter` hard-writes 1 env |
| 7 | **Room object list (0x0B)** | Imported as metadata only, not editable; N64 export omits it; OTR derives from actors | 🟡 | `SceneImporter` stores `room.Objects`; not surfaced |
| 8 | **Paths / waypoints** | Not modeled at all | 🟠 | no `Path` type |
| 9 | **Waterboxes** | `WaterBox` exists for OTR but not imported/editable | 🟡 | `OtrCollisionHeader` |
| 10 | **Setup/layer switcher UI** | No Hammer-style variant/visgroup switcher to host age/time-of-day variants | 🟠 | UI has no setup control |

## What already works (verified)

- Import of a vanilla scene+rooms for OoT and MM; actors/transition actors, spawn, scene
  env/lighting, skybox, music, room behavior/time/echo become editable.
- Hammer-style UI: four viewports (3 ortho + 1 perspective), left tool palette, right
  texture/objects/properties dock, status bar, grid/snap, select/brush/clip/vertex/texture tools.
- Compile + playtest export to SoH/2Ship (scene/room binary + OTR resources) builds valid output.

## Empirical whole-game results (`--auditlevels both`, full log: `level-audit-results.txt`)

Every scene imports and round-trip-compiles headlessly with **zero failures**:
OoT 101 scenes / 388 rooms, MM 102 scenes / 299 rooms, importFail=0, buildFail=0.
Warps: OoT 275 collision exits (1 unmapped), MM 292 (1 unmapped).

Per-command preservation across the whole game (status = what happens on import→recompile):

| Cmd | Name | OoT | MM | Status | Meaning |
|----|------|----:|---:|--------|---------|
| 0x00 spawn / 0x03 collision / 0x04 rooms / 0x07 keep / 0x0F light / 0x11 skybox / 0x15 sound | core | all | all | **PRESERVED** | imported + re-emitted |
| 0x0E | transition actors | 62 | 70 | **LOST** | parsed but written as plain actors, not a 0x0E list |
| 0x13 | exit list | 100 | 93 | **LOST** | parsed (exits) but not re-emitted |
| 0x18 | alt headers (setups) | 32 | 31 | **LOST** | setups parsed, never exported (age / time-of-day) |
| 0x0B | room object list | all | all | **LOST** (N64) | metadata only; OTR derives it |
| 0x08/0x10/0x12/0x16 | room behavior / time / echo / skybox-mod | all | all | **RESET** | NOT read on import → exported from editor defaults → original values silently changed |
| 0x0D | path list | 34 | 66 | **IGNORED** | dropped on import |
| 0x17 | cutscene | 30 | 62 | **IGNORED** | dropped |
| 0x05 | wind | 6 | 3 | **IGNORED** | dropped |
| 0x19 | censor flag | all | all | **IGNORED** | dropped |
| 0x0F | lighting (multi-env) | 68 | 102 | **PARTIAL** | only env[0] preserved |
| MM 0x02/0x0C/0x1A/0x1B/0x1C/0x1E | actor-cs / unused / tex-anim / actor-cs-list / minimap / minimap-chests | — | most | **IGNORED** | MM-specific, dropped |

## Recommended action order

1. **Setups (0x18) end-to-end** (addresses #1, #2-partial, #10): retain alt-header data on the
   document, add a Hammer-style setup switcher so child/adult and time-of-day variants are
   viewable/editable, and re-emit 0x18 on export so they survive recompile.
2. **Round-trip preservation** (#2-#7): retain original scene/room bytes and pass through any
   header command the document does not model, so unmodified recompile is 1:1.
3. **Remaining editability** (#3 surface types, #5 cutscenes, #8 paths, #9 waterboxes).
