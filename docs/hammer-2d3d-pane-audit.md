# Megaton Hammer — 2D / 3D view-pane audit vs Valve Hammer (Source SDK 2013)

Focused audit of the **2D ortho panes** and the **3D perspective pane** — their controls,
interactions, and presentation — against SDK2013 Hammer. Complements the tool/control audit in
[`hammer-parity-audit.md`](hammer-parity-audit.md). Written 2026-06-27 alongside the 33-item
nitpick pass (see `megaton-hammer-editor-backlog` memory). Legend: ✅ parity / fixed this pass ·
⚠️ partial · ❌ gap (with priority **[H]/[M]/[L]**).

## 1. 2D ortho panes

### Navigation
- ✅ **Mouse pan** (middle-drag) and **zoom** (wheel) — present.
- ✅ **Arrow-key view scroll when nothing is selected** — *fixed (#3)*. Arrows pan the focused/last 2D
  view; with a selection they still nudge it. **Verify the pan direction feels right** (h/v signs were
  chosen without a live test; flip in `MainForm.ProcessCmdKey` if inverted).
- ❌ **[M] Drag-edge auto-scroll** (#12) — Hammer scrolls the 2D view when a drag reaches the pane edge.
  Deferred: the camera-pan-mid-drag interaction (keeping the dragged object under the cursor) is easy to
  make jittery and needs live tuning. Suggested approach: in `GLViewport.OnMouseMove`, when a left-drag is
  active and the cursor is within ~24px of the edge, `cam.Pan()` proportionally + `Invalidate()`, ideally
  on a ~30ms timer for smooth continuous scroll.

### Selection
- ✅ **Overlap click priority** (#1) — *fixed*: among brushes whose 2D box contains the click, the
  **smallest (inner-most)** wins. Hammer also cycles overlapping objects on repeat clicks — a future add
  (track last click point + cycle by ascending area).
- ✅ **Marquee box-select** — present (prior audit).
- ✅ **Transform-mode handles** (#13) — *fixed*: Scale = small white squares, Rotate = larger bright-green,
  Skew = larger orange (was a single size + subtle colour). Hammer additionally uses different *shapes*
  (circles for rotate); a renderer enhancement (`DrawSelectionHandles2D` shape param) would complete it.
- ✅ **Undo/redo preserves selection** (#30) — *fixed*: `IsSelected` now round-trips through the project
  DTOs, so the document-restore undo keeps the selection.
- ⚠️ **Click-cycle Scale→Rotate→Skew** — present for brushes *and* actors. Paste now resets to Scale (#14).

### Brushes / transforms
- ✅ **Flip Horizontal / Vertical** (#25) — *added* to the 2D right-click menu (view-relative: Top→X/Z,
  Front→X/Y, Side→Z/Y). Edit menu keeps world-axis Flip X/Y/Z.
- ❌ **[H] Texture lock on rotation** (#24) — rotating a brush does **not** carry its textures. Root cause:
  faces store *derived* texture axes (scale/shift/rotation off the face normal), not Hammer's explicit
  world-space U/V axis vectors, so a rotation can't be applied to the mapping cleanly (world-aligned faces
  especially). **Real fix = refactor `SolidFace` to store explicit U/V axes** (then rotate them with the
  geometry, like Hammer's "Texture Lock"). Sizeable; touches serialization + export + render. Deferred —
  too risky to fake without visual iteration.
- ❌ **[M] Slice/clip tool parity** (#9) — user reports the slice does nothing with a Hammer-style
  strikethrough. `ClipTool` exists; needs interactive testing to find where the strikethrough→cut path
  breaks and to match Hammer's behaviour/preview exactly. Deferred (needs a live editor).

### Actors in 2D
- ✅ **Facing indicator** (#29) — *added*: a line from each actor's box along its in-plane yaw (Top→Y,
  Front→Z, Side→X), so rotation is visible and visibly turns when you click-cycle to Rotate and drag.
  **Verify rotation handedness** (sin/cos convention picked without a live test).
- ❌ **[M] Wireframe model footprints** (#7) — Hammer draws entities as their model wireframe in 2D, not a
  box. Deferred: projecting the actor model to the ortho plane each frame is a real rendering feature.
  Recommend an `EditorSettings.ActorWireframe2D` toggle (default on) gating it once implemented; the
  orientation line (above) is the interim.

### Grid / presentation
- ✅ **Sector gridline colour** (#2) — *fixed*: 1024-unit sector lines orange → muted violet, so they no
  longer read like the selection highlight.

## 2. 3D perspective pane
- ✅ **WASD + right-drag fly**, focus-follows-mouse — present.
- ✅ Selection box around the model footprint — present.
- ⚠️ **Texture fidelity** (#5) — MM Lost Woods textures look "desaturated". They are **i8 / ia16 grayscale**
  in the ROM; the game tints them via the combiner + vertex/environment colours, which the editor doesn't
  emulate, so raw decode = gray. Not a bug per se. A faithful fix needs combiner+vertex-colour emulation in
  the editor's shading path (or at minimum applying the scene's env colour as a tint to intensity formats).
  Deferred (rendering feature; needs visual iteration).

## 3. Window / perf
- ❌ **[M] Un-fullscreen / resize lag** (#8) — severe stutter leaving fullscreen. Deferred: needs profiling
  on a live run. Likely suspects: per-frame GL context churn on resize, the 4 viewports all redrawing
  synchronously during the resize drag, or a layout pass thrashing. Recommend throttling viewport redraws
  during `ResizeBegin`/`ResizeEnd` and coalescing the splitter re-layout.

## 4. Face Edit Sheet (Material tool)
- ✅ **Negative texture scale = mirror** (#26), **inherit clicked face's texture** (#22), **global texture
  search** (#4), **Justify/Fit lowered** (#19), **per-face Justify/Fit/Apply** (#21), **face highlight
  clears on tool switch** (#27) — all *fixed* this pass.
- ❌ **[M] Align→adjacent UV continuation** (#15) — current button copies the lead face's mapping to other
  selected faces (needs 2+). Hammer's "align to adjacent" auto-finds the edge-adjacent face and continues
  the texture across the seam. Needs edge-adjacency detection + UV-continuation math. Deferred.
- ❌ **[L] Right-click texture = realign to current values** (#23) — ambiguous; confirm exact Hammer
  behaviour before implementing.

## 5. Labels / clarity (cross-cutting)
- ✅ **Untruncated property checkboxes** (#11), **auto-save field overlap** (#17), **dropdown tooltips**
  (#6), **"Megaton Hammer fork" naming** (#18), **About noted** (#16) — *fixed/handled*.
- ❌ **[H] Friendly labels + dropdowns** (#10) — "Night SFX", "Echo", Time Speed/Override, fog near/far,
  raw surface-type, "Setup (single header)"/Variants are raw/cryptic. The ask: friendly labels + dropdowns
  for enumerated fields, with raw numeric entry hidden behind an *Advanced* toggle. This is a real
  Properties-panel UX rework (enumerate each field's valid values from the decomp). High value, deferred
  as a focused project; recommend doing it field-by-field with a shared `AddEnum(label, options, get, set)`
  helper + an "Advanced (raw values)" expander.

## Priority queue for the next focused pass
1. **[H] #10** friendly labels/dropdowns — biggest day-to-day clarity win; mechanical once scoped.
2. **[H] #24** explicit per-face UV axes → real texture lock (move + rotation + flip all "stick").
3. **[M] #9** slice tool, **#12** drag-edge scroll, **#8** resize perf — all need a live editor to validate.
4. **[M] #7** actor wireframes, **#15** align-to-adjacent — rendering/geometry features.
5. **[M] #32** 2Ship spawn void-out — MM OTR export spawn/collision (runtime debug).
6. **[L] #5** texture tinting, **#23** right-click realign — fidelity/ambiguous.

All 21 fixed items this pass are committed to `main` (build-gated). The deferred items are deferred
specifically because they need a running editor (interaction/visual tuning) or a non-trivial refactor —
not because they're out of scope.
