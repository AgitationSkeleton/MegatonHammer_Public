# Mirror-Shield & Light Puzzles (vanilla-compatible)

Two vanilla actors let you author light puzzles that compile identically to OoT/MM N64 and
SoH/2Ship, with **no engine/fork change** — the editor is purely the medium.

## 1. Shield-reflect puzzle — `Mir_Ray` (OoT + MM)

The real "stand in the sunbeam, reflect it with the Mirror Shield, aim the reflection at a sun
switch" mechanic.

**How it works (z_mir_ray.c):**
- `Mir_Ray`'s beam **volume is compiled** into the overlay (`sMirRayData[params]`) and **ignores the
  actor's placed position**. There are 10 fixed beams (Spirit Temple + Ganon's Castle Spirit Trial),
  identical on OoT and MM. So you can't invent a new beam — you **reuse a vanilla one and build around
  its fixed world location**.
- Spawning `Mir_Ray` with params 0–9 in *any* scene manifests that beam at its fixed coords.
- Link standing in the beam frustum with the Mirror Shield reflects a **player-aimed** quad collider
  (`AT_TYPE_PLAYER`) from the shield in whatever direction he faces.
- The reflection triggers **any AC collider it crosses** — and `Obj_Lightswitch` (the Sun Switch) is
  **fully placeable** and accepts `AT_TYPE_PLAYER`. So the *target* goes anywhere; only *where Link
  stands* is fixed.

**Recipe:**
1. Place a **Mirror-Shield Light Beam (Mir_Ray)** and pick a **downlight** slot in the *Beam location*
   dropdown (Bombchu / Sun-block / Single-Cobra / Armos / Top-room, or Ganon's Spirit Trial). The
   marker snaps to the beam and a **yellow gizmo** draws the beam volume (source→pool, with a width
   cross at each end) in the 2D and 3D views.
2. Build a **standable floor inside the beam** (use the gizmo — Link's shield must enter the cone
   between the two crosses).
3. Place a **Sun Switch (Obj_Lightswitch)** where the reflected beam can reach, and give it a switch
   flag.
4. Wire that switch flag to a **door/gate** (the editor's flag bus). Done — a working reflect puzzle.

**Constraint:** the beam is one of 10 fixed world positions. Move your *geometry* to the beam, not the
beam to your geometry. A truly arbitrary-position reflectable beam isn't possible in vanilla (it would
need a custom actor, which breaks portability).

## 2. Placeable light shaft — `Mir_Ray2` (MM / 2Ship only)

For an **arbitrary-position** light-trigger (not a shield-reflect puzzle). See the *Light Shaft
(Mir_Ray2)* entity: it anchors a point light + a light-trigger collider to its **placed** position and
lights a nearby Sun Switch automatically while enabled (switch-flag gated). MM-target only — OoT's
engine has no placeable beam actor. Pair it with an additive/translucent scrolling brush for the
visible shaft. It **auto-triggers when lit** — it is not redirected by the Mirror Shield.

## Which to use

| Want | Use | Games |
|---|---|---|
| Reflect-with-shield puzzle (Link aims) | `Mir_Ray` + `Obj_Lightswitch` | OoT + MM (beam at a fixed vanilla spot) |
| Arbitrary-position auto light trigger | `Mir_Ray2` + `Obj_Lightswitch` | MM / 2Ship only |

---

# Position-anchored actors

Some actors **ignore where you place them** and force themselves to a hardcoded position in-game. For these the
editor snaps the placed marker onto the position the actor will actually occupy (so you're not misled), and the
property sheet shows a note explaining it. Managed centrally in `Editor/AnchoredActors.cs`.

An exhaustive OoT+MM decomp sweep found that **`Mir_Ray` is the only actor with a *multi-position* table** (the 10
beams above → a dropdown). Every other position-hardcoded actor forces a **single** fixed position, so there's
nothing to pick — just a snap + a note:

| Actor | Game | Forced position | Notes |
|---|---|---|---|
| `Mir_Ray` | OoT (0x00B7) / MM (0x0062) | 1 of 10 beams (dropdown) | The reflect-puzzle beam above |
| `Boss_Sst` (Bongo Bongo) | OoT (0x00E9) | ≈ (0, 0, -650) | Head forces arena centre; hands are child-spawned |
| `Boss_03` (Gyorg) | MM (0x12B) | (1216, 140, -1161) | Main boss forces its spawn |

> **Actor ids are per-game.** OoT and MM reuse the same numbers for different actors — `Mir_Ray` is `0x00B7` on
> OoT but `0x0062` on MM (and `0x0062` on OoT is `Bg_Menkuri_Eye`). All anchoring lookups are game-qualified.

Reported but **not** anchored (niche / ambiguous): `Boss_01` Odolwa (MM, cutscene ceiling-drop from `(0,2400,0)`)
and `Boss_07` Majora's Remains (MM, its params low nibble is a damage-effect selector, not a clean form/position
index). Everything else in both games either honours its placed position or only spawns child actors at offsets
from it.
