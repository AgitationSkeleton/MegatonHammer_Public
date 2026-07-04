# Megaton Hammer ↔ Valve Hammer feature-parity audit

Audit of Valve Hammer (Source SDK 2013 editor) controls/features against Megaton Hammer,
accounting for **BSP/Source vs Zelda 64** differences. Hammer source:
`READ_ONLY_SourceCodes/sdk-2013-hammer-master`. MH source: `src/MegatonHammer`.

**Legend** — ✅ present · ◐ partial · ✗ missing (and *applicable* to Z64) · ⊘ not applicable
(Source/BSP-specific, no meaningful Z64 analogue).

The fundamental engine difference that drives this audit: Source maps are **CSG brushwork +
data-defined entities (FGD) + an I/O event graph + lightmaps + displacements**. Zelda 64 scenes are
**display-list meshes + collision mesh + fixed C-overlay actors parameterised by a packed 16-bit
`params` word + scene/switch/chest flags + vertex colours + a room/scene/entrance structure**. So
"entity keyvalues" → **actor params bit-fields**, "I/O connections" → **switch/scene flags**,
"lightmaps" → **vertex colours + environment lighting**, "VisGroups" → **rooms / actor categories**.

---

## 1. File / Project

| Valve Hammer | MH | Notes |
|---|---|---|
| New / Open / Save / Save As | ✅ | `.mhp` projects |
| Close | ✅ | |
| Export (multi-format) | ✅ | N64 `.zmap/.zscene`, OBJ, VMF, O2R |
| Export Again (Alt+B) | ✗ | one-key re-export to last target — easy add |
| Run Map (F9) | ✅ | Build & Export / Playtest |
| Recent files (MRU) | ✗ | small QoL gap |
| Reload Sounds | ⊘ | (Z64 audio handled differently) |

## 2. Edit / clipboard / undo

| Valve Hammer | MH | Notes |
|---|---|---|
| Undo / Redo | ✅ | |
| Cut / Copy / Paste | ✅ | |
| Paste Special (copies, offset, rotation, prefix, unique-name) | ✅ | `PasteSpecialDialog` |
| Clone / duplicate | ✅ | Ctrl+D + Shift-drag clone (added this session) |
| Delete | ✅ | |
| Select All / Clear Selection | ✅ | |
| Properties (Alt+Enter) | ◐ | opens via double-click; **no Alt+Enter accelerator** |
| Find Entities (Ctrl+Shift+F) | ✅ | `FindEntitiesDialog` |
| **Replace** (keyvalue search/replace across map) | ✗ | Z64 analogue: bulk-edit actor params/flags by query — useful |

## 3. Selection & manipulation tools

| Valve Hammer | MH | Notes |
|---|---|---|
| Selection tool (move/scale/rotate/shear handles) | ✅ | SelectTool Scale/Rotate/Skew modes |
| Box/marquee select, additive (Shift), subtractive (Ctrl) | ✅ | Ctrl/Shift toggle |
| Shift-drag clone | ✅ | added this session |
| Arrow-key nudge (+ Shift coarse) | ✅ | Ctrl=fine, Shift=10× (added) |
| **Eyedropper** (Alt+click copies entity props) | ✗ | Z64 analogue: copy actor id+params to a "paint" buffer |
| **Transform dialog** (exact move/rotate/scale by value) | ✗ | **applicable, missing — high-value easy win** |
| **Align objects** (L/R/T/B) | ✗ | applicable, missing |
| **Snap selected to grid** (+ individually) | ✗ | applicable, missing |
| **Center origins** | ✗ | applicable (actor/brush origin) |
| Flip horizontal/vertical | ✅ | Flip X/Y/Z (added; Hammer only has H/V) |
| Selection-bounds dimensions readout | ✅ | drawn in 2D |

## 4. Geometry creation & CSG

| Valve Hammer | MH | Notes |
|---|---|---|
| Block/brush creation (deferred box → Enter) | ✅ | BrushTool, Hammer-style |
| Clip tool (Front/Back/Both, cycle) | ✅ | ClipTool |
| Vertex/Morph editing | ✅ | VertexTool (convex-hull rebuild) |
| **Carve** (boolean subtract) | ✗ | applicable to brushes — medium effort |
| **Make Hollow** | ✗ | applicable — medium effort |
| **Arch / Torus / primitive generators** | ✗ | applicable; handy for pillars/rings |
| **Sphere / cylinder / wedge prefabs** | ✗ | applicable |
| Displacement surfaces (sculpt/paint/sew/subdivide) | ⊘→◐ | No Source "displacements", but a **heightfield/terrain sculpt** on a mesh would map to Z64; low priority |
| Swept hull / player-hull tools | ⊘ | Source playerclip-specific |

## 5. Textures / face editing

| Valve Hammer | MH | Notes |
|---|---|---|
| Face Edit sheet (scale/shift/rotate, world vs face, justify, fit, align) | ✅ | `FaceEditDialog` |
| Texture application tool (3D click-to-paint, apply/apply-all) | ✅ | TextureTool + RMB apply |
| Texture browser (filter, search, used-only) | ✅ | `TextureBrowserForm`/`TexturePanel` |
| Double-click browser → apply | ✅ | fixed this session |
| **Replace Textures** (map-wide find/replace) | ◐ | per-face replace exists; **no map-wide replace dialog** |
| **Texture Lock** (keep UVs pinned while moving/scaling) | ✗ | applicable, useful — Source `Shift+L` |
| Justify (top/bottom/left/right/center/fit) | ◐ | fit/align present; full justify set unclear |
| Lightmap scale | ⊘ | Z64 has no lightmaps (uses vertex colours) |
| Decals / Overlays | ◐ | MH has a Decal tool; Source info_overlay is its own entity (⊘) |
| **Vertex colour paint** | ✅ | Shade tool — the Z64 lighting analogue (Source has `$vertexcolor` paint) |

## 6. Entity / logic editing — *the biggest divergence*

| Valve Hammer | MH | Notes |
|---|---|---|
| Object Properties: typed keyvalues / SmartEdit | ✅(Z64 form) | `EntityConfigDialog` + `ActorParamSchema` (typed bit-fields → dropdown/spinner/checkbox) |
| Spawnflags page (named flag checkboxes) | ✅ | raw 16-bit grid + named flags where schema exists |
| FGD entity-definition system (data-driven classes) | ◐ | Z64 actors are fixed C overlays; `ActorParamSchema` is the analogue but **hand-authored per actor** (only ~4 actors so far) → needs broad coverage |
| **Inputs/Outputs (I/O connection graph)** | ✗ | **The key gap.** Z64 analogue = **switch-flag / scene-flag connections** (which actor *sets* a flag vs which *reads* it), plus the **warp/exit table** and transition actors. A "connections" view + flag pickers is the Z64 I/O editor. **Highest-value logic feature.** |
| Angle picker (visual), target picker, face picker (eyedroppers) | ✗ | Z64 analogue: visual yaw picker; flag/scene picker |
| Entity help (FGD docs) | ◐ | actor names + a survival-guide note exist; no per-param docs panel |
| Model/sprite browser | ◐(Z64 form) | actor id+name picker; renders actor model |
| Sound browser | ✗ | Z64 analogue: sequence/SFX id picker for sound actors |

## 7. Organization / visibility

| Valve Hammer | MH | Notes |
|---|---|---|
| Group / Ungroup | ✗ | applicable; group brushes+actors as a unit |
| Tie to Entity / Move to World | ⊘ | brush-entity binding is Source-specific (no Z64 brush-entities) |
| **VisGroups** (user + auto, color, hierarchy) | ✗ | Z64 analogue: group by **room / actor category / object** + show/hide |
| Quick Hide / Hide Unselected / Isolate / Unhide | ✗ | applicable; complex scenes need it. (MH has per-room toggles via `ImportedRoomsForm` only) |
| Cordon (edit/save bounds) | ◐ | Z64 scenes are bounded already; an **edit-focus cordon** could still help big scenes |
| Show/Hide entity names, helpers, models-in-2D | ◐ | some 2D/3D entity toggles exist |

## 8. Navigation & view

| Valve Hammer | MH | Notes |
|---|---|---|
| 4-view layout (Top/Front/Side/3D) | ✅ | |
| 2D pan/zoom, grid show, grid size [ ] | ✅ | grid 1–1024 |
| 3D fly-nav (WASD + mouse-look) | ✅ | + Z mouselook toggle |
| Render modes: wireframe / flat / textured / shaded | ◐ | textured/shaded yes; **wireframe & flat toggles** unclear/missing |
| Center views on selection (Ctrl+E) | ◐ | Find-entity centers; **no generic "center on selection"** |
| **Go to Coordinates** | ✗ | easy add |
| **Go to Brush Number** (Ctrl+Shift+G) | ✗ | Z64 analogue: go-to actor/brush by index |
| Autosize 4 views (Ctrl+A) | ✗ | minor |
| Units (none/inches/feet) | ◐ | MH uses raw Z64 units (correct for Z64) |
| Lighting / ray-traced preview | ⊘ | Z64 uses baked vertex colours + env lighting |

## 9. Map utilities / validation

| Valve Hammer | MH | Notes |
|---|---|---|
| Entity Report (filter by class/keyvalue/visibility, goto) | ✅ | `EntityReportDialog` |
| **Check for Problems** (Alt+P) | ✗ | **High-value Z64 analogue**: validate actor count per room, missing required objects, invalid params/flags, room/mesh limits, unreferenced exits, dangling switch flags |
| Map Properties / Info (counts, texmem) | ◐ | room count shown; **no full stats/limits panel** |
| Map diff | ✗ | low priority |
| Pointfile / portalfile | ⊘ | BSP-compile artifacts |
| Snap settings, grid settings dialog | ✅ | Options |

## 10. Build / playtest / assets — *MH is ahead here*

| Feature | MH | Notes |
|---|---|---|
| Compile & run | ✅ | Build & Export |
| **ROM import** (read-only scene/actors/collision/env) | ✅ | Z64-only; no Hammer equivalent |
| **ROM injection** | ✅ | Z64-only |
| **Multi-engine playtest** (SoH / 2Ship / PJ64) | ✅ | with age + inventory + auto-warp |
| **Cross-game textures** (OoT↔MM) | ✅ | Z64-only |
| **Minimap generation** | ✅ | Z64-only |
| Prefabs (Create Prefab + library) | ✗ | applicable: reusable actor/geometry templates |
| Sound browser | ✗ | see §6 |

---

## Features that are genuinely N/A to Z64 (⊘ — do not implement)
- Lightmaps / lighting & ray-traced 3D preview (Z64 = vertex colours + env light).
- Displacement surfaces, sew/subdivide (Source terrain system).
- Tie-to-Entity / Move-to-World, brush-entities (Source solid entities).
- info_overlay entities, instancing/collapse, pointfile/portalfile, swept/player hulls.
- FGD as a *file format* (but its *concept* — typed entity defs — maps to `ActorParamSchema`).

## Features MH has that Hammer does not (Z64 strengths)
ROM import/inject · O2R + multi-emulator playtest with age/inventory + RDRAM auto-warp ·
cross-game texture/music borrowing · typed actor-param logic editor · vertex-shade paint ·
minimap generation · per-room visibility.

---

## Recommended additions, prioritised (applicable to Z64)

**Tier 1 — high value, the real logic/parity gaps**
1. **Switch/scene-flag "connections" view** (the Z64 I/O analogue): per-scene flag table showing
   which actors *set* vs *read* each switch/chest/scene flag; flag pickers in the actor dialog;
   the **warp/exit table** + transition-actor editor. (Extends #12.)
2. **Broaden `ActorParamSchema` coverage** from ~4 actors toward the common logic actors
   (doors, En_Item00, more switches, owls/warps, spawners, scene-exit actors).
3. **Check-for-Problems / scene validation** (actor/room limits, invalid params, dangling flags,
   missing objects/exits).

**Tier 2 — clear quick wins**
4. **Transform dialog** (exact move/rotate/scale by value), **Align L/R/T/B**, **Center origins**,
   **Snap-selected-to-grid**.
5. **Go to Coordinates** + **Go to Brush/Actor #**; **Center views on selection (Ctrl+E)**;
   **Properties Alt+Enter**, **Export Again (Alt+B)**, recent-files MRU.
6. **Texture Lock** (pin UVs during move/scale) + **map-wide Replace Textures** dialog.

### Implemented (this pass) ✅
- **Tier 1.1 Flag-connections view** — `Map ▸ Flag Connections (logic)`: switch/chest/collectible
  flags grouped by who ⇒sets / ⇐reads them, dangling flags flagged amber, plus the exits/warps list
  (trigger volumes + transition actors). `FlagConnectionAnalyzer` + `FlagConnectionsDialog`.
- **Tier 1.2 Broader `ActorParamSchema`** — now 13 actors (added En_Door, Door_Shutter, En_Holl,
  En_Sw, Obj_Bombiwa, Obj_Hamishi, Elf_Msg, Elf_Msg2, Obj_Kibako2) with flag-role metadata.
- **Tier 1.3 Check for Problems** — `Map ▸ Check for Problems` (Alt+P): missing Link spawn, obsolete
  actors, conflicting/dangling flags, empty rooms, void triggers, high actor counts. `SceneValidator`.
- **Tier 2.4** — `Tools ▸ Transform…` (Ctrl+M, exact move/rotate/scale; added `Solid.Rotate`),
  `Tools ▸ Align ▸ L/R/T/B`, `Tools ▸ Snap Selected to Grid` (Ctrl+B).
- **Tier 2.5** — `View ▸ Center on Selection` (Ctrl+E), `View ▸ Go to Coordinates…`,
  `Edit ▸ Properties` (Alt+Enter). *(Go-to-brush#, Export-Again, recent-files MRU not done — minor.)*
- **Tier 2.6** — `Tools ▸ Texture Lock` (persisted) + `Tools ▸ Replace Textures…` (map-wide / selected).
  Also fixed a latent bug: brush transforms used to wipe per-face texture shift/rotation (`ComputeFaces`
  now carries the full mapping).
- All logic verified headlessly via `--selftest` (flag grouping, rotate, texture carry + lock).

**Tier 3 — structural / nice-to-have**
7. **Group/Ungroup** + **VisGroups** (by room/category) with **Quick Hide / Isolate / Unhide**.
8. **CSG Carve / Make Hollow**; **primitive generators** (arch/torus/cylinder/sphere/wedge).
9. **Prefab** library (reusable actor/geometry templates); **eyedropper** actor-paint;
   **wireframe/flat 3D render toggles**; **sound/sequence picker** for audio actors.
