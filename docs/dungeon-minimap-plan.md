# Dungeon minimap / pause-map parity (#3 / #11-map)

**Goal:** an injected Megaton Hammer level flagged as a dungeon shows the **dungeon
pause-map** (floor map + room reveal + Link's position marker) instead of the
overworld minimap, identically on SoH (OoT/OTR), 2Ship (MM/OTR) and PJ64 (N64).

Today the reserved `SCENE_MH_APPEND` isn't in any engine's map tables, so `Map_Init`
falls through and the pause screen shows the overworld map of "the current area."

## How OoT's map works (verified in `z_map_exp.c`)

- `Map_Init(play)` switches on `play->sceneId`:
  - overworld scenes → `mapIndex = sceneId - SCENE_HYRULE_FIELD`, overworld minimap.
  - dungeon scenes (≤ `SCENE_ICE_CAVERN`, + bosses) → `mapIndex = sceneId`, **dungeon map**:
    `gMapData->dgnCompassInfo[mapIndex]`, `dgnTexIndexBase[mapIndex]`, then
    `Map_InitRoomData` + `MapMark_Init`.
- `gMapData` (`gMapDataTable`, `z_map_data.c`) is the per-mapIndex data: `roomPalette`,
  `maxPaletteCount`, `paletteRoom[map][floor][i]`, `floorCoordY`, `dgnMinimapCount`,
  `dgnMinimapTexIndexOffset`, compass scale/offset, room-rect bounds.
- The minimap **textures** are per-room 96×85 IA8 images (`dgnMinimapTextures`),
  indexed by `dgnTexIndexBase[mapIndex] + room`.
- `MapMark_Init` draws the chest/boss-key dots from `sMapMarkDataTable[mapIndex]`.

MM (`2Ship`) is the same shape with MM scene ids/tables.

## Editor-side generation (shared, the hard part)

From the scene the editor already has room AABBs + a 2D top-down render
(`TestTempleBuilder.RenderMap` already produces one). Generate, per project:

1. **Floor assignment** — group rooms by Y band (the boss-pit `FloorY` etc.); each band
   is a map "floor". Emit `floors[]` with `floorCoordY` (the Y threshold) and the room
   list per floor.
2. **Room rectangles** — project each room's XZ AABB into OoT map space (the compass
   scale/offset that fits all rooms into the 96×85 frame). Emit per-room rect + the
   derived `dgnCompassInfo` (scale/offset) so Link's marker lands correctly.
3. **Minimap textures** — render each room's footprint (walls as lines) to a 96×85 IA8
   image, matching the vanilla style. One texture per room per floor.
4. **Mark data** — chests/boss already known to the editor → map-mark dots (optional v2).

Emit a new resource **`mh/minimap`** (JSON: mapIndex hint, floors, per-room rect +
texture name) plus the IA8 textures as `mh/minimap_tex_{n}` (OTR) or a packed blob (N64).

## Per-engine wiring

- **SoH / 2Ship (OTR)** — boot hook (`MhPlaytestBootHook`) reads `mh/minimap`, then:
  - reserve a spare dungeon `mapIndex` (add one growable slot to `gMapDataTable`, like we
    grew the scene/entrance tables), fill its `dgnCompassInfo`/`paletteRoom`/`floorCoordY`/
    `dgnTexIndexBase` from the editor data, and register the textures with the ResourceMgr.
  - add `SCENE_MH_APPEND` to the dungeon branch of `Map_Init` (small fork patch) keyed to
    that reserved mapIndex. `MapMark` table likewise gets a reserved slot.
- **PJ64 (N64)** — bake the same into the ROM injection: write the `gMapDataTable` slot +
  the IA8 textures into the kaleido map segment, and patch `Map_Init`'s scene switch (the
  injection already patches code; this adds one `case`). gSaveContext gets `DUNGEON_MAP`
  in `dungeonItems[mapIndex]` so the floor map is unlocked (already grantable via the
  existing inventory poke path).

## Phasing (so each step is testable)

- **P1 — reservation only:** register the reserved mapIndex with a SINGLE-floor, single
  placeholder texture so the pause screen shows the *dungeon* subscreen (not overworld),
  Link marker centered. Proves the table plumbing on all 3 engines. *(small, low-risk)*
- **P2 — real geometry:** editor generates accurate per-room rects + IA8 textures + floors.
- **P3 — marks:** chest/boss dots from the editor's actor list.

## Risks / care

- The map overlays assume well-formed `gMapData`; a missing field crashes kaleido. Reserve
  a slot and fully populate it (don't reuse a real dungeon's index — that corrupts its map).
- N64: the kaleido map segment is size-bounded; large texture sets need a DMA/segment plan.
- Keep the mapIndex reservation in ONE place per engine (a constant) shared with the
  scene/entrance reservation already used for `SCENE_MH_APPEND`.

Recommend building **P1 first** behind the existing `SceneSettings.Dungeon` flag, verify the
dungeon subscreen appears on SoH, then iterate P2/P3 with editor texture generation.
