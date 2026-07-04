# Playtest-Injection Timeline & Learned Audit (ea3c415 → present)

Scope: every change touching the **SoH / 2Ship (OTR/O2R)** and **PJ64 / N64 ROM injection**
playtest paths for **MM and OoT**, from `ea3c4156` to HEAD (78 commits, 2026‑06‑25 → 06‑27).
Annotated with what worked, what regressed, where the work went in circles, and the live
testing feedback that drove each turn.

> **The single most important takeaway:** there was a genuine **high‑water mark at
> `c696dd8` (06‑26)** — MM N64 injection booted straight into a walkable, textured level
> with a full HUD, pause, Tatl, and items; OoT debug auto‑boot worked; 2Ship/SoH rendered.
> Almost everything after that on 06‑27 was texture/cosmetic refinement, and several of
> those changes **regressed** the c696dd8 baseline (daytime→HUD) or **chased the wrong
> cause** (TMEM), while a **silent blocker** (a corrupted fork patch) made every
> fork‑side fix from `00df2aa` onward a no‑op until `3da8514`.

---

## Phase 0 — Foundation & dialogue (06‑25)  ✅ steady, mostly off‑path

| commit | what | verdict |
|---|---|---|
| 0c377fe / 1177451 / 61fbe39 / f0ac63a | Dialogue Message Bank (editor + fork hooks + encoder) | ✅ orthogonal to injection |
| d6610d9 | MM‑aware `InjectDebug` (the foundation for N64 MM injection) | ✅ foundation |
| 50b3911 / 256dd1d | `--packplaytestn64 mm`, validated against a **real MM debug ROM** | ✅ validated |
| 22023dc → 5a44dc6 | OoT message‑table locator → in‑place overwrite dialogue | ✅ (dialogue) |

## Phase 1 — MM boots on PJ64 (06‑25)  ✅ the breakthrough run

| commit | what | verdict |
|---|---|---|
| c9afee1 | PJ64 hook MM support + headless popup suppression | ✅ |
| **1af00d1** | **MM boots in PJ64** — root‑caused to a 32‑bit fork build + 32‑bit plugins (architecture fix) | ✅✅ key unlock |
| 3621517 | inject into KAKUSIANA (0x07) + write warp params | ✅ |
| 7460d85 | heuristic PlayState scan + gameplay gate | ⚠️ *first stuck point:* "warp blocked on EU‑debug RAM map" |
| **97ca5ca** | **vanilla‑MM N64 scene injection — renders & walkable** | ✅✅ |

## Phase 2 — MM injection hardening + boot SOLVED (06‑26)  ✅ peak

| commit | what | verdict |
|---|---|---|
| 641e764 | collision plane‑distance fix — walls were non‑solid in *all* engines | ✅ (recurs later, see 47d6cfa) |
| 3875676 | strip vanilla transition actors from the scene shell | ✅ |
| 50b39f1 | N64 room render mode (no z‑write) + real MM textures | ✅ textures first render |
| d6cd6df / b4feefc | inject MM‑safe actors (chest+sign), object list, music, closed chest | ✅ |
| e386586 | **diagnose** `gameMode=NORMAL` freeze; gate menu‑fix behind a flag | ⚠️ stuck point (freeze) |
| **6f207d5** | **MM playtest‑boot SOLVED** — full HUD/pause/Tatl via a FileChoose‑style runtime reset + skip the entrance cutscene | ✅✅✅ |
| a1ec84b | auto‑boot via `ConsoleLogo_Main` detour (default on) | ✅ |
| 59c62c5 | wire MM N64 playtest into the editor menu + auto‑boot checkbox | ✅ |
| 6a73464 / 1e3d546 | **OoT** MQ‑debug auto‑boot (detour `TitleSetup_InitImpl`) | ✅ OoT path |
| **c696dd8** | nop the detour delay slot (latent stack‑write fixed) | ✅ **HIGH‑WATER MARK** |

At `c696dd8` the user later confirmed: HUD perfect, chest spawned ("FD mask"), level
walkable. **This is the reference point all later regressions are measured against.**

## Phase 3 — Editor backlog (06‑26 → 06‑27)  ◻️ off‑path UX

`1e23e3e … e7311bb`, `5b649ef`, `cf57ea4`, `3de7da3`, `abc5cfa`, `53dbc40`, `c623eb8`,
`7e65033`, `4cf7b94`, `d05e3e5`, `c2659dc` — 2D/3D editor UX, tool parity, model audit,
ROM auto‑detect. Not injection behaviour, but `2ab3bfb` here matters later:

| commit | what | later relevance |
|---|---|---|
| **2ab3bfb** | **#5: tint grayscale (i8/ia16) ROM textures by their DL prim colour** | The grass‑colour saga. The prim it captured for the Lost Woods grass was **white (0)**, so the tint is a no‑op → textures stay grey in the editor library *and* both exports. |
| f1a879d | SoH/2Ship OTR render: z‑write + no‑cull | ✅ |
| 6b1ad11 | OTR collision now includes OBJ‑mesh tris | ✅ |
| 10b0ba0 | editor renders transparent textures as cutout | (foreshadows the N64 transparency back‑and‑forth) |
| **f577156** | **FIX regression: an em‑dash in a GLSL comment broke shader compile → all viewports black** | ⚠️ self‑inflicted regression, quickly fixed |

## Phase 4 — 2Ship actors + config (06‑27)  ✅

| commit | what | verdict |
|---|---|---|
| 485ac30 | playtest ROM config (auto‑detect, retail OoT, clipped button) | ✅ |
| **47d6cfa** | **OTR collision plane‑distance formula was wrong** (Link fell through floors) | ✅ the 2Ship/SoH void‑out fix |
| **157615e** | **2Ship actors now spawn** — mm‑aware object table (OoT ids were resolving wrong objects for MM) | ✅✅ |

## Phase 5 — The PJ64 texture/geometry struggle (06‑27)  🔁 cyclic confusion

This is where the work "drifted." The user's repeated live feedback was *"no discernible
progress"* across many turns. Sequence:

| commit | claim | reality (learned later) |
|---|---|---|
| d1c92db | "fix invisible geometry (standard opaque render mode) + **force daytime**" | The render‑mode flailing was a wrong turn; **the daytime force was a latent regression** that later broke the HUD. |
| 5d21041 | play the scene's chosen music + "restore visible geometry" | music ✅; geometry still not actually textured |
| 314e047 | clamp accumulated UV shift (the c696dd8 "drift") | ✅ necessary (s16 S/T overflow) **but insufficient alone** |
| edf4ae8 | `--diagproj` diagnostic | ✅ good instinct (instrument, don't guess) |
| d26bbb9 | stop baking the per‑room palette colour into shade (blue textures) | ✅ necessary, still insufficient alone |
| **6c2e1bd** | **add missing othermode‑H (1‑cycle + texture mode)** | ✅✅ **the real texture breakthrough** — without it the cycle/TLUT mode was wrong so textures never sampled. The prior three fixes *stacked* under this one. |
| 00df2aa | Tatl toggle + one‑shot‑music warning + N64 texture transparency | ⚠️ **first fork‑side change after the patch silently broke** (see Phase 6); the N64 transparency was untested |
| **4602eb3** | **revert the daytime force** → fixes the PJ64 HUD regression | ✅ bisect‑proven: daytime (d1c92db) was the only behavioural change vs c696dd8 |
| bf4ea7b | 2Ship chest flag‑clear + revert N64 transparency | transparency revert ✅ (it had caused opacity artifacts); chest fix was fork‑side → **silently not applied** |
| **56c6df5** | "downscale oversized textures to fit N64 TMEM" | ❌ **dead end** — extracting the real ROM later proved the textures fit and were simply grayscale; TMEM was never the cause |
| 4320d3e / 97ef2f9 | `--diagtex` / `--diagrom` (decode brush textures / extract baked ROM textures) | ✅✅ the tools that finally produced ground truth |
| **fb3d2b9** | bake scene lighting into N64 vertex colours | ◻️ right *direction* (N64 rooms bake their lighting; the gray texture needs runtime tint), wrong *values* — the project's lights are warm‑grey, not the vivid green 2Ship applies |

### What the grass‑colour saga actually was (resolved by extraction)
- `--diagrom` on the **real** `mh_playtest.z64` and the **fresh** 2Ship O2R both show the
  grass texture baked **identical grey** (`~134,134,134` and `~168,168,168`, R=G=B). The
  exports are **consistent**.
- The Lost Woods grass is an **i8 intensity** texture: grey data, coloured **at draw time**
  by the scene environment. 2Ship's engine applies that tint → green; the N64 injection runs
  flat white shade → grey.
- So "make PJ64 match 2Ship" = **replicate the runtime tint in the baked N64 vertex colours**
  (the `fb3d2b9` approach) **using the colour 2Ship actually applies**, not the editor's
  warm‑grey light values. *(open item)*

## Phase 6 — The silent blocker, finally found (06‑27)

| commit | what | verdict |
|---|---|---|
| **3da8514** | **FIX corrupt 2Ship patch** | 🧨 **the hidden root cause.** When the Tatl hook + chest‑flag clear were added to `2ship-mh_playtest.patch`, the hunk header line‑count wasn't updated (`@@ -259,7 +321,369 @@` should have been `+321,384`). `git apply` rejected the **entire** patch ("corrupt patch at line 490"), so `apply-mh-patches.cmd` skipped it and the fork was **never rebuilt with any fork‑side change** from `00df2aa` onward. The running `2ship.exe` was a 06‑25 build the whole time. This is why "Tatl still missing" recurred no matter how many times it was "fixed." |

---

## Cross‑cutting lessons (the patterns to not repeat)

1. **A working baseline existed (`c696dd8`) and was not treated as sacred.** The daytime
   force (`d1c92db`) shipped on top of it and silently broke the HUD; it took a bisect
   (`4602eb3`) to undo. *Lesson: when something regresses, diff against the last‑known‑good
   commit first, before theorising.*

2. **Cyclic confusion on the PJ64 textures (Phase 5).** Multiple render‑mode/UV/TMEM
   theories were tried and shipped before the cause (missing othermode‑H, then
   "the textures are simply grayscale") was actually known. The turning point each time was
   **instrumentation** (`--diagproj`, `--diagtex`, `--diagrom`) and **extracting ground truth
   from the artifact**, not reasoning about the pipeline. *Lesson: extract the baked bytes
   early; don't argue about what the export "should" contain.*

3. **The TWO export paths diverge silently.** N64 (`DisplayListBuilder`/`RoomExporter`) vs
   OTR (`OtrRoomGeometry`/`OtrSceneWriter`) must mirror each other; several fixes landed in
   one and not the other (othermode‑H, shade handling, transparency). *Lesson: any
   geometry/shade/texture change needs a paired edit + a diff of the two outputs.*

4. **Fork changes have a build/apply step that can fail invisibly.** Editing a `.patch`
   file by hand corrupts it if hunk counts aren't recomputed; `apply‑mh‑patches.cmd` is only
   idempotent for the *same* patch. *Lesson: after editing the patch, `git apply --check`
   against pristine submodule source, and rebuild the fork — every time.*

5. **"Fixed" ≠ "in the build."** Fork‑side fixes (Tatl, chest flags, mask transformation)
   only exist once `apply‑mh‑patches.cmd` re‑applies **and** `mh_build.cmd` rebuilds
   `2ship.exe`. The audit window had ~1.5 days where this wasn't happening.

---

## Live testing arc with the user (PJ64 / MM / OoT injection & warps)

Reconstructed from the testing conversation; this is the part that "we have down for the
most part now":

- **Boot & warp:** MM boots on PJ64 (32‑bit build, `1af00d1`), injects into the playtest
  scene slot, and **auto‑boots straight into the level** (`a1ec84b`/`c696dd8`). OoT debug
  ROM auto‑boots too (`6a73464`/`1e3d546`). The early "warp blocked on EU‑debug RAM map"
  stuck point (`7460d85`) was resolved by the FileChoose‑style runtime reset (`6f207d5`).
  **Status: working.**
- **Collision / void‑outs:** the recurring "Link spawns in a void and falls" was a
  plane‑distance formula bug — fixed for N64 (`641e764`) and again for OTR (`47d6cfa`).
  **Status: working.**
- **Geometry rendering:** N64 geometry went invisible → textured‑white → blue → finally
  correct once othermode‑H (`6c2e1bd`) stacked on the UV‑clamp (`314e047`) and shade
  (`d26bbb9`) fixes. 2Ship/SoH rendering fixed at `f1a879d`. **Status: rendering; grass
  colour is a known runtime‑tint gap, not an export bug.**
- **Actors:** 2Ship actors initially didn't spawn (OoT object ids for an MM scene) → fixed
  with the mm‑aware object table (`157615e`). The chest "spawned then stopped" turned out to
  be (a) export was always correct and (b) the fork‑side flag clear wasn't in the build
  (corrupt patch). **Status: PJ64 chest works; 2Ship chest pending the rebuilt fork.**
- **HUD:** perfect at `c696dd8`, regressed by the daytime force, restored by reverting it
  (`4602eb3`). **Status: should match c696dd8 on a fresh build.**
- **Music:** scene‑chosen sequence now plays (`5d21041`); a warning flags one‑shot
  sequences that won't loop (`00df2aa`).

## Open items (current front)
- **Grass/texture parity:** bake the *correct* runtime tint into N64 vertex colours so PJ64
  matches 2Ship (the texture data is already consistent/grey in both).
- **Actor parity vanilla↔2Ship/SoH:** verify the same actors/objects/rotations resolve
  identically across both export paths (user‑flagged: chest rotation differed).
- **2Ship mask transformation:** no camera/blue‑cone effect + A‑button loss after a mask —
  to be investigated on the freshly rebuilt fork (`Player_SetEquipmentData` /
  `GET_CUR_FORM_BTN_ITEM` per‑form button equipment).
- **Fork rebuild discipline:** now that the patch applies, rebuild `2ship.exe` on every
  fork‑side change (this audit's build run is doing exactly that).
