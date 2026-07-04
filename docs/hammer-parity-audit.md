# Megaton Hammer — tool/control parity audit vs Valve Hammer (Source SDK 2013)

Behavioral + controls comparison of each editor tool against SDK2013 Hammer, listing missing
features. Priorities: **[H]** core workflow gap, **[M]** notable, **[L]** minor/nice-to-have.
"Present" = already at/near parity. Audited 2026-06-25 against the current `Tools/` + viewport code.

## Implemented since the audit (2026-06-25)

- ✅ **2D marquee box-select** (Selection tool) — drag empty space → rubber-band touch-select; Ctrl/Shift additive.
- ✅ **Group / Ungroup** (Ctrl+G / Ctrl+U) — click a member selects the whole group.
- ✅ **Visgroups** (Map ▸ Visgroups) — create-from-selection, per-group show/hide, Show All.
- ✅ **Camera gizmos** — draggable eye+look cameras in 2D, PgUp/PgDn cycle, active drives the 3D view.
- ✅ **Block tool primitive shapes** — Block / Wedge / Cylinder / Spike / Sphere (Tools ▸ Block Shape),
  built as convex hulls so they're always valid brushes.
- ✅ **Movable Player Start** — the Link spawn is now a selectable/movable editor-only marker (syncs to
  SpawnPos/SpawnYaw); default placement actor changed from Player/Link to a treasure chest.
- ✅ Audit fix: Entity tool instant-commit confirmed correct (not a gap).

- ✅ **Player Start = the real spawn** (clarified): the movable Link marker IS the compiled in-game
  spawn (scene header cmd 0x00 via SpawnPos/SpawnYaw), not a dummy. Separately, an **editor-only
  insertable dummy Link** (scale reference) can be placed and is never compiled (exporters skip
  `IsEditorOnly`). Default placement entity is now **configurable per game** (Options ▸ General),
  defaulting to the dummy Link.

- ✅ **Face Edit Sheet** — CORRECTION: it already exists (`FaceEditDialog`, opened by the Texture tool):
  scale/shift/rotation, World-vs-Face align, Justify L/R/T/B/C, Fit, Browse/Replace/Mark,
  Align→adjacent. The original audit's "no Face Edit Sheet" was a false negative (the subagent read
  only `TextureTool.cs`, which delegates to the dialog). Added this session: **Alt+click texture lift**
  (eyedropper) and a working **"Treat as one"** for Fit/Justify.

- ✅ **Vertex tool** — multi-select (click / Ctrl-toggle / marquee box), move-the-whole-selection,
  edge-drag (grab an edge midpoint → both endpoints), and Insert-key edge split (adds a midpoint
  vertex). Selected vertices highlight red. (Inward/concave pulls are clamped by the convex-hull
  rebuild — fully concave brushes would need non-convex brush support, a separate larger change.)

All audited tool gaps are now addressed or intentionally scoped out.

## Current status reconciliation (2026-06-25 re-audit)

> The per-tool sections **below this point are the ORIGINAL pre-implementation audit**, retained for
> reference. Several gaps they list as missing are now DONE (see above). Cross-checked against the
> current code, the accurate present-day status is:

**Done since the original audit (the sections below are stale on these):** 2D marquee box-select,
Group/Ungroup, Visgroups, camera gizmos, Block-tool primitive shapes, Face Edit Sheet (+ Alt-click
lift, Treat-as-one), Vertex tool multi-select/edge-drag/split, movable Player Start + editor-only
dummy Link + configurable default actor, **path polyline rendering** (`SolidRenderer.DrawPaths2D/3D`
draw the connecting track — the "no visible polyline" note is stale).

**Done 2026-06-25 (this pass):**
- ✅ **3D-view brush move** — the Selection tool now drags a selected brush (and the whole selection,
  brushes + actors together) on the grabbed face's plane height in the 3D view, mirroring the existing
  actor 3D move (`SelectTool.Begin3DMove`/`Apply3DMove`). 3D brush *selection* already existed
  (`Picking.PickFace`). (3D resize/rotate handles + 3D box-select are still 2D-only — lower priority.)
- ✅ **Clip-tool keep-side shading** — while a clip line is pending, the kept brush halves draw bright
  (white) and the discarded halves greyed, Hammer-style (`ClipTool.PreviewSegments` →
  `DrawConnections2D`). The clip itself was already correct.
- ✅ **Pane maximize** — double-click a viewport's title bar to fill the window with that view (and
  again to restore the 4-pane grid), Hammer-style. `ViewportPanel.HeaderDoubleClicked` →
  `MainForm.ToggleMaximizeViewport` collapses the other split panels.
- ✅ **Path loop/close + properties** — `L` toggles the active path's closed flag (draws the closing
  segment from last → first waypoint); double-clicking a waypoint opens `PathPropertiesDialog`
  (name / loop / MM additionalPathIndex / customValue). The `ZPath.Closed` flag is serialized; the
  exported 0x0D point list is unchanged (looping is the following actor's behavior, not the path data).

**Evaluated and intentionally NOT changed (current behavior already matches Hammer / doesn't map):**
- **Camera tool, 3D view = no-op** — correct: in SDK2013 the Camera tool works in the 2D views (place/
  drag camera gizmos); the 3D view *is* the live camera. There is no camera drag-in-3D to match.
- **[L] Cordon bounds** — deferred: Hammer's cordon gates *compile* to a sub-region, which doesn't map
  onto OoT's discrete room-based scene compile; not a minor change and semantically fuzzy here.

**Genuinely still unimplemented (verified absent in code):**
- **[M] Carve (subtract) / Make Hollow** — no CSG boolean ops on brushes. *Scoped out:* OoT geometry
  is authored as convex brushes + OBJ import; boolean CSG is a large feature with no Zelda-pipeline need.
- **[M] Tie to Entity / Move to World (brush entities)** — *intentionally N/A:* OoT has no
  func_/trigger_-style brush entities; actors are point entities. No compiled target exists.
- **[M] 3D-view brush resize/rotate handles + 3D box/frustum select** — 3D move is done (above); 3D
  handle-resize/rotate and frustum box-select remain 2D-only. Lower priority.
- **[L] Cordon bounds** — deferred (see rationale above; doesn't map to room-based compile).
- **Overlay tool** — intentionally omitted (no OoT runtime overlay entity; Decal covers the use case).

Nothing in this set is a dead/broken control — they are unbuilt features or cosmetic polish, each with
a rationale above.

**Overlay tool — out of scope (won't implement):** OoT has no runtime overlay entity; the Decal tool
already stamps textures onto faces, which covers the use case. A Hammer overlay would be editor-only
decoration with no compiled output, so it is intentionally omitted.

## Substrate / global (cross-cutting)

Present: 4-pane layout (3 ortho + 1 perspective), shared selection across views, undo/redo
(Ctrl+Z/Y), copy/cut/paste/paste-special, **Clone (Ctrl+D)**, arrow-key nudge (grid / Ctrl-fine /
Shift-coarse, + PgUp/Dn depth), grid cycle `[` `]`, snap-to-grid toggle, middle-mouse pan,
wheel zoom, RMB freelook + Z locked-fly (WASD/QE), right-click 2D context menu, status bar
(coords / selection size / grid / zoom), Transform dialog (Ctrl+M), Flip, Snap-to-grid (Ctrl+B),
Select-all (Ctrl+A), center-on-selection (Ctrl+E).

Missing vs Hammer:
- **[H] Group / Ungroup (Ctrl+G / Ctrl+U).** No grouping concept at all — so no "click selects the
  whole group" selection semantics either. Core to Hammer's editing workflow.
- **[M] Tie to Entity / Move to World (Ctrl+T / Ctrl+Shift+W).** No brush entities — a brush can't be
  turned into a func_/trigger_-style entity.
- **[M] Carve (subtract) and Make Hollow.** No CSG boolean ops on brushes.
- **[M] Visgroups.** No show/hide layer system.
- **[L] Cordon bounds** (compile/preview a sub-region).
- **[L] Pane maximize** (Hammer: double-click a viewport title to fullscreen it). Splitters drag only.
- **[L] Space-drag pan** (Hammer's hand pan). MH uses middle-mouse pan instead — acceptable.

## Selection tool

Current: 2D click-pick by AABB; Ctrl/Shift additive toggle; re-click cycles Scale→Rotate→Skew with
8 handles; Shift-drag clones; 3D click-picks **actors** and drags them on a ground plane; 15° rotate
snap; Delete.

Missing vs Hammer:
- **[H] Marquee / rubber-band box select in 2D.** Dragging empty space does nothing (currently just
  deselects). Hammer draws a selection rectangle and selects everything inside/touching — the single
  most-felt gap. (Note: a rubber-band renderer already exists for the Block tool — `DrawRubberBand2D`
  in [GLViewport.cs](../src/MegatonHammer/Forms/GLViewport.cs#L374) — and could be reused.)
- **[H] 3D box/frustum select** and 3D selection of **brushes** (3D currently only picks/moves actors).
- **[M] Move/resize/rotate brushes in the 3D view.** Hammer lets you drag brushes (and faces) in 3D;
  MH restricts 3D dragging to actors.
- **[M] Drag-move in 2D snaps the whole selection but there's no "selection clone leaves original"
  visual / Alt-modifiers parity** (Hammer: Shift-drag clones — present; but no group-aware drag).
- **[L] No rotate/skew in 3D** (Hammer mostly does these in 2D too — minor).

## Block / Brush tool

Current: drag a box in 2D, 8 resize handles + move-inside, Enter commits / Esc cancels, third-axis
depth auto-computed and centered. Box only.

Missing vs Hammer:
- **[H] Primitive shapes.** Hammer's Block tool has a shape menu: Block, **Wedge, Cylinder, Spike,
  Arch, Sphere, Torus**. MH makes axis-aligned boxes only.
- **[M] Size the third axis in another view before committing.** Hammer shows the pending box in all
  three 2D views and you size each; MH auto-derives depth from the drawn face.
- **[L] "Create as prefab/entity" on commit** and per-shape parameters (sides, arc, etc.).

## Clipping tool

Current: draw a clip line in a 2D view; `X` re-press cycles keep Front→Back→Both; Enter applies to
all selected brushes across views; Esc cancels.

Missing vs Hammer:
- **[M] Visual keep-side feedback.** The keep mode cycles but nothing shades/outlines which part
  survives (Hammer whites-out the kept solid and greys the discarded half).
- **[L] Clip on a 3-point/arbitrary plane.** Single 2D line only (Hammer is effectively the same —
  near parity).

## Vertex tool

Current: drag a **single** vertex of the selected solid in a 2D view, grid-snapped; the brush is
rebuilt as the convex hull each move. No 3D.

Missing vs Hammer:
- **[H] Multi-vertex selection + box-select of vertices**, and moving several at once.
- **[H] Edge manipulation** (drag edges) and **edge/face split** to add geometry — Hammer's vertex
  tool edits vertices, edges, and can split. MH has none.
- **[M] Non-convex results.** Convex-hull rebuild means you can't create concave brushes; Hammer
  allows free vertex moves (and flags invalid solids on compile).
- **[L] Vertex-selection scale/rotate; numeric vertex entry.**

## Entity tool

Current: one click places a point entity (`ActiveActorId` set by the hierarchy panel); 3D placement
ray-picks geometry (unsnapped, like Hammer), 2D placement grid-snapped.

Present: instant click-to-place is the intended behavior (matches Hammer — placing an entity commits
immediately; no Enter-to-confirm step needed).

Missing vs Hammer:
- **[M] Brush entities** (tie a selected brush to an entity class) — see substrate gap.
- **[L] In-tool/object-bar entity-class quick pick** (MH relies on the external hierarchy panel).

## Texture application (Face) tool

Current: 3D only — click selects a face, Ctrl multi-selects faces, Shift quick-applies the active
texture; raises events for an external Face Edit dialog and the texture browser.

Missing vs Hammer (largest single-tool gap):
- **[H] Face Edit Sheet controls.** No texture **scale X/Y, shift/offset X/Y, rotation**, **justify**
  (Fit / Left / Right / Top / Bottom / Center), **alignment World vs Face**, **"Treat as one"**,
  **smoothing groups**, or **lightmap scale**. MH only assigns a texture *name* to a face — no UV/
  alignment control at all.
- **[M] Alt+click texture "lift"/sample** in 3D (copy a face's texture **and** its alignment to the
  active tool). MH has Shift-apply but no documented lift.
- **[M] Face selection / marking in 2D views** (Hammer can mark faces from any view).
- **[L] Replace-textures, "mark faces", and apply-to-selection-of-objects** batch ops.

## Camera tool

Current: 2D click sets the 3D camera position (keeps the off-plane axis); drag aims yaw/pitch. 3D
no-op.

Missing vs Hammer:
- **[M] Camera gizmos.** Hammer places named, re-grabbable eye→look camera objects in the 2D views and
  cycles them (PgUp/PgDn). MH only nudges the single live 3D camera — you can't drop/keep multiple
  saved viewpoints.

## Decal tool

Current (OoT-specific): click stamps the active texture onto a face; Shift clears it.

Missing vs Hammer:
- **[L] True point-decal entity** (Hammer's Decal places an `info_decal`). MH paints face texture
  directly — already noted as intended for the Zelda pipeline; flagged as future in-code.

## Overlay tool

- **[M] Missing entirely.** Hammer has an Overlay tool (`info_overlay`) for projected, movable decals
  on surfaces with handle-resizable corners. No MH equivalent (DecalTool is the closest but is a
  face-texture stamp).

## Magnify tool

Present / parity: click zoom-in, Shift+click zoom-out, drag for continuous zoom anchored at the click.
No gaps.

## Path tool

Current (OoT-equivalent of path_track): click appends a waypoint to the active path, drag a waypoint
to move it, Delete removes, Enter starts a new path, Esc deselects. 2D only.

Missing vs Hammer:
- **[L] Visible polyline between waypoints**, loop/close toggle, and a path properties dialog
  (name / interpolation). Functional but sparse.

## Suggested priority order (highest workflow impact first)

1. **[H] Marquee box-select** in 2D (Selection tool) — explicitly requested; renderer already exists.
2. **[H] Face Edit Sheet** (texture scale/shift/rotate/justify/align) — Texture tool.
3. **[H] Group / Ungroup** — substrate.
4. **[H] Vertex tool**: multi-select + edge drag + split.
5. **[H] Block tool primitive shapes** (wedge/cylinder/spike/arch/sphere).
6. **[M] 3D brush manipulation + 3D box-select** (Selection tool).
7. **[M] Carve / Hollow**, **Tie to Entity / brush entities**, **Visgroups**, **Overlay tool**,
   **camera gizmos**, **clip keep-side shading**, **entity placement preview**.
