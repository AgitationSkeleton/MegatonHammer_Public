# Boss rooms & project cross-linking (#5)

**Problem.** Several bosses hard-code their world position (Gohma forces `world.y =
-300`, "the ceiling"). Faking that with a tall negative-Y "pit" room in the SAME scene
renders badly in-engine: a hall-of-mirrors (depth range too large / far geometry past
the view frustum) and the boss + player draw invisible. Vanilla OoT/MM never does this —
**every boss lives in its own scene**, reached by a boss door that performs a real scene
transition. So the right fix is structural, not geometric.

## Goal

Author a boss lair (and, more generally, any connected level) as a **separate `.mhproj`**,
and link it to the parent level through a door/exit. At playtest the editor packs all
linked projects together and the boss door warps between them, exactly like a vanilla
dungeon → boss-room transition.

## What already exists to build on

- `O2RPacker.PackOtrMulti` already packs **multiple scenes** into one playtest O2R at
  reserved append slots (`mh_append_0..15`) and writes a `scenes[]` manifest in `mh/info`.
- The forks already grow the scene/entrance tables for `SCENE_MH_APPEND` and warp to it.
- Transition actors (doors) already carry front/back room; a *scene-exit* door is the
  same idea one level up (front room → an ENTRANCE into another scene).

## Design

1. **Link model (editor).** A scene-exit is a door/loadzone whose target is
   `project:entrance` — another `.mhproj` (by stable id) + a spawn index. Store links in
   the project: `SceneLink { FromDoorId, TargetProjectId, TargetSpawn }`. A "boss door"
   is just a styled scene-exit that also sets the boss-defeated flag on return.
2. **Project set / manifest.** A small sidecar (e.g. `<name>.mhset.json`, kept in
   `megaton_mhprojs/`) lists the projects in a playtest set and their reserved append-slot
   assignment, so ids are stable across regens.
3. **Pack (editor).** Extend the multi-scene pack: each linked project → its own append
   slot; rewrite each scene-exit's target to the engine entrance for the target slot
   (`ENTR_MH_APPEND_<slot>`), emit the link table in `mh/info`.
4. **Engine (SoH/2Ship).** No new core code needed for the warp itself — scene-exits use
   the existing per-slot entrances. The boss door additionally: on entering the boss
   scene set up the boss fight (already automatic — the boss actor self-inits), and on
   boss death + exit, set the parent's clear flag (carried in the link table).
5. **N64 (PJ64).** Same: the multi-scene injection already supports several appended
   scenes; the exit just targets the right injected entrance. Boss clear flag via the
   existing save-poke path.

## Phasing

- **P1 — generic scene-to-scene exit.** Two normal `.mhproj`s, a load-zone door from A→B
  and B→A, packed together, warp works on SoH. Proves linking end-to-end.
- **P2 — boss door semantics.** Mark a link as a boss door: styled door model, boss
  scene flagged so the boss self-inits at its hard-coded position (now valid — it's the
  only thing in its scene), clear flag set on return.
- **P3 — N64 + MM parity**, then editor UX (draw links in the 2D/3D views, a target
  picker in the door's properties).

## Immediate action taken

Reverted the Test Temple Sanctum from the broken pit back to a normal room (so the rest
of the dungeon renders correctly). Gohma there still spawns at her forced y=-300 (below
the floor) — a known limitation that P2 resolves by moving her to a dedicated boss scene.
Recommend doing P1 next so the Gohma lair becomes the first real boss-room link.
