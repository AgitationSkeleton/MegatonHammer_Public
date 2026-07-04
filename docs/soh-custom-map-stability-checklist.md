# Custom Map Stability Checklist — vanilla SoH O2R

For shipping a **custom scene** (new geometry + vanilla actors + music) as an `.o2r` that loads in
**unmodified** Ship of Harkinian. Written against the "PvP arena replacing Link's House" PoC, but the
items generalise. Vanilla SoH is resource-driven: it loads scene/room/collision/actor-list/object
resources by *path* from the O2R, so the whole job is (a) emit well-formed data and (b) route it to a
path SoH already loads. No engine changes are used.

Legend: `[ ]` do it · ⚠️ = the failure that bites hardest here · ✅ = already handled by the editor.

## 1. Scene targeting & entrance (the #1 crash source)
- [ ] **Replace an EXISTING scene** (Link's House = scene `0x34`, `SCENE_LINKS_HOUSE`). Do NOT append a
      brand-new scene — a new scene needs a new *entrance-table* row, which is engine/table data vanilla
      SoH won't take from an O2R. Replacing reuses the existing entrance that already points at 0x34. ⚠️
- [ ] O2R resource **path matches the vanilla scene's path** so SoH overrides it (scene + every room).
- [ ] Exactly one valid **spawn** entry in the scene header; a missing/malformed spawn = boot crash or
      instant void. Spawn stands on a real floor poly.
- [ ] Keep the scene's **exit list** valid (see §6).

## 2. Scene & room headers
- [ ] **Object dependency list includes EVERY object any placed actor needs.** A placed actor whose
      object isn't loaded renders as nothing or crashes. For `Bg_Ddan_Jd` (rising platform) include the
      Dodongo object; include `gameplay_keep` / `dangeon_keep` as the actors require. ⚠️
- [ ] **Light rig / environment set** — a scene with no lighting can render pitch black (this has bitten
      the project before). Give it a light setting even for an indoor arena.
- [ ] **Music**: `SceneSettings.MusicSeq = 0x38` (`NA_BGM_MINI_BOSS`), `MusicCrossGame = false`. 0x38 is a
      stock OoT sequence → plays in vanilla SoH. (Cross-game music is the one music case needing the fork.)
- [ ] Collision header present and referenced by the scene.
- [ ] Actor + spawn lists are well-formed (the editor's exporter already pads/orders these correctly ✅).

## 3. Collision & hazards
- [ ] **Every intended-walkable surface has a floor poly.** Gaps = fall; use gaps *intentionally* as void.
- [ ] **Void-out planes**: paint them with the **"Void-out floor (fall = reload)"** surface preset
      (FLOOR_PROPERTY_12). Confirm the player respawns at a sane last-safe position (place the arena so
      the pre-void safe spot is on the platform, not off it).
- [ ] **Lava floors**: simplest stable route = a lava-textured floor poly sitting over a **void-out** poly
      → falling in is a clean instant elimination. (True heart-draining lava is a room/surface-property
      behavior; verify it in-game before relying on it — void-lava is the safe PoC choice.)
- [ ] **Solid walls / arena bounds.** Ensure perimeter walls are SOLID collision so players can't clip out
      of the arena (non-solid wall collision has been a past pitfall on injected scenes). ⚠️
- [ ] Put a floor OR a void plane under the *entire* arena footprint — no true "nothing" below play space.

## 4. Actors
- [ ] **Vanilla actors only.** `Bg_Ddan_Jd` (0x0058), `Obj_Lift`, torches, switches, etc. exist in stock
      SoH. A brand-new *code* actor would need a mod, not an O2R — out of scope for a vanilla PoC.
- [ ] **Verify `Bg_Ddan_Jd` behaves standalone** — read its params/Init; if it hardcodes Dodongo-scene
      state (a specific switch flag or position), either set that flag/param or swap to a more portable
      riser (`Obj_Lift`, or the Forest Temple elevator 0x0087). ⚠️ this is the actor most likely to misbehave.
- [ ] Actor params seeded sensibly (editor placement defaults). Avoid params of `0` on actors that are
      inert or "invisible until triggered" (e.g. Stalfos type 0) unless intended.
- [ ] Do NOT place boss/cutscene actors that expect scripted scene setup unless you mean to.
- [ ] Keep actor count modest for a first PoC (dozens, not hundreds).

## 5. Geometry & textures
- [ ] Export via a **current editor build** — the entrance-list / TRI-byte-order / texture-handler(cmd0)
      correctness fixes are in the exporter, so output is vanilla-loadable ✅. Don't use a stale build.
- [ ] Textures embedded/packed in the O2R; no external/missing texture references (missing texture can
      render garbage or crash the DL).
- [ ] Room display lists close properly (editor handles G_ENDDL ✅).

## 6. Warp / exit to Kokiri Forest
- [ ] Warp-out is a trigger brush carrying the **WARP** tool texture + a valid **ExitEntrance** id that
      points at a Kokiri Forest entrance.
- [ ] Test the warp **both ways** (arena → Kokiri Forest, and back into the arena via the Link's House door).

## 7. Packaging
- [ ] O2R contains: scene + all rooms + collision + objects (incl. the pillar's object) + textures + music
      is by seq-id (no audio asset needed for a stock seq).
- [ ] Run `SceneValidator` (editor) before packing if available.
- [ ] Drop the O2R in SoH's **mods** folder; nothing else required for vanilla load.

## 8. In-game test pass (stock SoH)
- [ ] Boots without crash; enter Link's House → arena renders (NOT black).
- [ ] Collision solid; can't clip through walls/floor.
- [ ] Void planes reload the player; lava behaves as intended.
- [ ] Rising platform(s) actually move.
- [ ] Mid-boss music plays on entry.
- [ ] Warp exits to Kokiri Forest; return works.
- [ ] Test with a **fresh save** and an **existing save**.
- [ ] Test with SoH **enhancements default AND toggled** (some enhancements alter scene/collision behavior).
- [ ] On any crash: read the SoH log (`%AppData%\...\Ship of Harkinian` logs) for the failing resource.

## 9. Things that would NEED our fork (avoid for a vanilla PoC)
- Adding a wholly **new scene + entrance** (vs. replacing 0x34).
- **Cross-game music** (an MM track in OoT) — needs the sequenceMap patch.
- **Custom inventory / auto-warp** on boot — those are playtest-harness conveniences, not O2R content.
- **New code actors** — need a compiled mod, not an O2R resource.

## 10. Why Link's House specifically is the safe target
Replacing 0x34 means the **existing entrance routes to your scene** (no entrance-table edit), it's a tiny
single-room scene (small footprint to overwrite), and its only special engine hook — the game-start
wake-up cutscene — fires on the *intro* entrance, not when you **warp in from Kokiri Forest**, so you sidestep
it. The vanilla cow / records sign / Navi actors are just decor in the original room; replacing the whole
scene drops them and nothing external requires them.
