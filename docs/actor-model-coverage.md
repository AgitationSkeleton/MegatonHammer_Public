# Actor model coverage

How a placed actor is shown in the editor / renders, and the current coverage. Audited by
`MegatonHammer --coverage [oot|mm]`.

## Resolution pipeline (`ActorModelResolver`)

For each actor (id + variable):

1. **Object** — the decomp actor→object table (`ActorObjectTable`, parsed from each overlay's
   `InitVars` object field) gives the real ROM object; it always wins because it's a real object.
   Otherwise the **render-DB hint** (`ActorRenderDb`, SharpOcarina `ActorRendering.xml`) supplies an
   object, used only when that object actually exists in the ROM (the XML often points at its own
   `custom_*` models that aren't in retail — those are ignored for geometry).
2. **Model** — if the hint targets the same real object, its display-list offsets are used; otherwise
   the object's skeleton (posed at idle frame 0, like Link / Fierce Deity) or largest clean display
   list is auto-detected. Textures resolve from the object (seg 6), gameplay_keep (seg 4) and the
   scene keep (seg 5: gameplay_field/dangeon_keep — grass, rocks, gossip stones).
3. **Fallback** — only when no model resolves: a billboard sprite (OoT item icon via `ActorSpriteMap`,
   or a flat colour quad in MM), so logic/effect actors read as what they do. The yellow origin cross
   is never drawn.

## Coverage (resolve a real 3D model)

| Game | Actors | Resolve a model | Object but no model | No object (sprite) |
|---|---|---|---|---|
| OoT | 430 | **332 (77%)** | 10 | 88 |
| MM | 577 | **417 (72%)** | 14 | 146 |

The "no object" bucket is dominated by genuinely model-less **logic / effect** actors (shops, enemy
spawners, cutscene actors, ambient sound, weather, checkable spots, projectiles, dynamically-drawn
collectables) — these correctly fall back to a sprite. A minority are real actors drawn from a keep
object at a draw-code-specific display list (no static object mapping), which need per-actor display-
list curation to render as their model.

## Recent fixes

- **Chests** (En_Box) and other actors with a real object now resolve — the resolver no longer pairs
  a real object with a `custom_*` hint's mismatched offsets.
- **Field props** (grass, rocks, gossip stones) texture correctly via the segment-5 scene keep.
- **Per-tile F3DEX2 format** fix removed the grey/garbled actor + geometry textures.
- Relevant item-icon sprites added for projectiles/magic/collectables.

## Remaining work (per-actor curation)

Full 100 % real-model coverage needs display-list offsets for keep-drawn actors and object mappings
for the param-dependent / draw-code-only actors (e.g. doors drawn from gameplay_keep). The
`--coverage` audit lists the exact remaining ids per game to guide that.
