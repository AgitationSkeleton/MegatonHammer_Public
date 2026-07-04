# Megaton Hammer — Deferred Features

Explicit list of requested features, with **what** was asked and **how** I'd implement it.
Kept current so any item can be picked up later. Goal: keep this list short.

## Status (most recent session)
- **DONE** — D1 (native-OTR playtest export + `mh_playtest` warp command; validated vs
  real oot.o2r; fork compiles). D3 (Project64 N64 playtest). D8 (3D brush/actor select).
  D9 (entity double-click → properties). D10 (2D/3D entity visibility toggles). D11
  (vertex-shade paint tool). D16 (NODRAW/CLIP/BLOCKPROJECTILE textures + waterboxes).
  D2-safety (read-only original-ROM guard + Options override — the CRITICAL constraint).
- **DONE this session (the ROM importer subsystem)** — **D2** Import Level from ROM:
  `SceneImporter` (scene/room headers, actors, alternate-header setups, lighting/skybox) +
  `RoomMeshReader` (F3DEX2 → textured world geometry) + `ImportedMeshRenderer` (textured+lit
  backdrop) + File ▸ Import Level from ROM. **D5/D7** actors imported with names + obsolete
  flagging (unknown ids → magenta boxed marker, round-trip preserved). **D14** per-room
  visibility panel for multi-room dungeons. **D4 (partial)** imported lighting/fog/skybox-id
  applied to scene settings. **D17** per-entity boss/miniboss survival guides in the config
  dialog. **D15** title-card name-texture generator (text → IA8 / OTEX).
- **Remaining / partial:** D4 real skybox *texture* (gradient+fog done; cubemap/cloud tex
  pending). D5 full skeletal/animated actor *models* (markers + obsolete placeholder done;
  mesh/SkelAnime rendering pending — the mesh reader foundation exists). D7 literal
  Eyeball-Frog sprite for unknowns (distinct marker done; gag-texture billboard pending).
  D12/D13 dungeon keys/locking doors/chests + GI_ item picker (needs the GI_ id table from
  decomp). D15 wiring the name texture into a scene's title-card slot on export + minimap
  auto-gen. D17 auto-satisfying boss requirements (guide done).

---

## Re-assessment results (acted on the decomp/tooling review)
Done this pass: **D16** collision poly winding (min-Y first + CCW) + CLIP ignore-
projectile flags. **D13** full GI_ item table from decomp. **D5** — fixed gObjectTable
locator (0→380 objects), `ObjectModelReader` (zobj static DL + skeleton rest pose),
`ActorObjectTable` (actor→object from decomp, 315 maps), `ActorModelResolver` + renderer:
placed actors now draw as their real models (Kaepora→owl, Skulltula, Player→Link). **D7**
unknown/obsolete actors render the **Eyeball Frog** (object_gi_frog gGiFrogDL). **D15**
confirmed title card = 144×24 IA8 and wired AreaName→title-card injection. **Export fix**:
room object lists now populated from actors (they wouldn't spawn before; also auto-loads a
boss's object — D17 partial).
D7 corrected: obsolete entities now show the **Eyeball Frog "OBSOLETE" billboard sprite**
(extracted gItemIconEyeballFrogTex + label, bundled PNG, game-agnostic) instead of the
frog model.

Genuinely-remaining (each blocked on in-game testing / engine-side / large, can't be
shipped to standard from here — precise plans below in this file):
- **D3 boot-entrance direct-warp**: version-specific RAM/entrance patch; unverifiable
  without running. Reach the level via the MQ debug map-select for now.
- **MM playtest path**: needs the MM scene-name/version table + MM `SetRoomBehavior`
  (6 bytes) + scene cmds 0x1A-0x1F in OtrSceneWriter (currently OoT-only; adding them
  without the rest is dead code).
- **D4 real skybox texture**: extract the OoT `vr_*` sky/cloud textures + cubemap render
  (gradient+fog already applied from the imported scene).
- **D5 skeleton rest-pose positioning**: decodes (Kaepora/Link/etc.) but some limbs/actors
  (e.g. Epona) need visual tuning of the joint transform — requires seeing the 3D output.
- **D17 collision/companion auto-satisfy**: the object is auto-loaded (done) + guide warns;
  auto-injecting Morpha's waterbox / Volvagia's lava / companion actors is speculative
  without in-game verification.
- **Minimaps**: editor-side generator DONE (Build ▸ Generate Minimap → top-down footprint
  PNG, from imported geometry or brushes). Remaining: register it as the in-game pause-map
  texture + map data/markers.
- **Chest-content override**: item isn't in En_Box params (decomp) — needs a give-item
  actor or a rando-style scene override on export; GI_ picker table (OotItems) is ready.

## Reference findings (decomp + tooling review — authoritative, use these)
Cross-checked our code against `READ_ONLY_SourceCodes`: OoT/MM decomp (`oot-master`,
`mm-main`), `fast64-main`, `zelda64-(collision-)import-blender`, `OTRExporter`/`Torch`,
`z64ovl`/`hm-actor-pack`/`Z64Recomp_ZobjUtils`, `2ship2harkinian`. Key facts for the
remaining work (FIXED items already applied: alt-header layers, UV normalize, spawn bound):

- **D5 actor models** — object (zobj, segment 6): `FlexSkeletonHeader{ void** segment;
  u8 limbCount; u8 dListCount }`; limbs are `LodLimb{ Vec3s jointPos; u8 child,sibling;
  Gfx* dLists[2] }` (or `StandardLimb`, dList at 0x08). Render rest pose = jointTable all
  zero, walk child/sibling tree applying jointPos translate + (zero) rotation, draw each
  limb's dList with our F3DEX2 interpreter (needs **G_MTX** matrix-stack support — rooms are
  world-space so we skip it there, but models need it). Actor→object: `ActorInit.objectId`;
  object table id→name (e.g. Link = object_link_boy/child). Dark Link = en_torch2 reuses
  `gDarkLinkSkel` (21 LodLimbs, Link-like).
- **D7 Eyeball Frog placeholder** — `object_gi_frog` (OBJECT_GI_FROG = **0x0149**), static
  (no skeleton): `gGiFrogDL` @ 0x0D60, `gGiFrogEyesDL` @ 0x1060. Extract via our DL reader
  for the literal gag sprite.
- **D12/D13 chest contents** — CORRECTION: En_Box params encode ONLY chest **type**
  (`params >> 12 & 0xF`, ENBOX_TYPE_*) and the treasure **flag** (`params & 0x1F`). The
  ITEM is NOT in params — vanilla chest contents come from game logic; to set arbitrary
  contents use a rando-style override or a custom give-item actor. GI_ ids (z64item.h):
  dungeon items ITEM_DUNGEON_BOSS_KEY=0x74, COMPASS=0x75, MAP=0x76, SMALL_KEY=0x77.
- **D16 collision flags/quality** — poly vtx fields are 13-bit (`& 0x1FFF`); top 3 bits of
  `flags_vIA` (0xE000) = ignore flags (camera/entity/projectile → CLIP vs BLOCKPROJECTILE);
  bit 0x2000 of `flags_vIB` = conveyor. Surface type is a hi/lo u32 pair (floor/wall/exit/
  camera/echo/lighting/sound/terrain) — we write one type=0. For exported collision,
  fast64 also requires **min-Y vertex first + CCW winding** (dynapoly). WaterBox.properties
  CONFIRMED: room = bits13-18 (0x3F=all), light=bits8-12, bgCam=bits0-7.
- **MM / 2Ship** — same OTR format + opcodes + CRC64 (our serializer reuses ~95%). Diffs:
  SetRoomBehavior is 6 bytes (vs OoT 1+4), extra scene cmds 0x1A SetAnimatedMaterialList,
  0x1B SetActorCutsceneList, 0x1C SetMinimapList, 0x1E SetMinimapChests, 0x1F SetCutscenesMM;
  MM area name = on-screen message text (not a title-card texture). Needs an MM scene-name/
  version table for the override path.
- **D15 title card** — dims (144x24) NOT confirmed by decomp headers (item-name tex is
  128x16 IA4); verify our card against a real extracted title file before shipping.
- **D3 P64 direct-warp** — no P64 CLI/Lua warp entrypoint found; `z64quicktest` patches the
  boot entrance in the ROM. Implement direct-warp as a boot-entrance ROM patch (else reach
  via debug map-select / normal play, as now).
- **OTR serializer** — header/opcodes/CRC64 CONFIRMED (our byte-validation vs real oot.o2r
  stands; the *_OTR_HASH hash is a separate 8-byte word after the command, w1 keeps its
  value — matches ours). Verify standalone OVTX vs OTRExporter's SOH_Array-wrapped vertices.

## D1. SoH/2Ship playtest: load editor level + warp + playtest settings (integration #1c)
**Asked:** The custom-forked engine must load the editor's level from the temp mod O2R,
warp to it, and apply playtest settings (child/adult age; inventory empty / custom
checklist / "Use Debug Inventory").
**Architecture (revised — much simpler than the original "engine converter"):** SoH's
renderer is resource-path based, so instead of converting raw N64 in C++ we emit the level
as **native libultraship OTR resources** (the same format real mods use) that OVERRIDE the
target scene's archive paths. Dropping them in `mods/mh_playtest.o2r` (auto-loaded by SoH,
OTRGlobals.cpp:387) makes the level load with **zero changes to the resource pipeline**.
**Status — DONE & validated (editor side):**
- `src/MegatonHammer/Otr/`: `OtrCrc64` (libultraship path hash), `OtrResourceWriter`
  (64-byte header + LE primitives), `OtrRoomGeometry` (OVTX + ODLT with G_VTX_OTR hashes),
  `OtrCollisionHeader` (OCOL), `OtrSceneWriter` (OROM scene/room command lists).
- `OotSceneFiles` maps scene id → resource name + version folder (all 101 shipped scenes
  validated vs oot.o2r). `O2RPacker.PackOtr` writes the override o2r; `PlaytestDialog` uses it.
- Validation: header + CRC64 + DL opcodes byte-exact vs SoH's own oot.o2r; scene/room/
  collision round-trip-parse exactly as `ResourceFactoryBinarySceneV0` reads them; packed
  override path equals what SoH requests for the target scene.
- Engine warp: added `mh_playtest [sceneIdHex] [child|adult]` console command in the SoH
  fork (debugconsole.cpp) — scans gEntranceTable for the scene and warps, sets linkAge.
**Remaining (small):**
- **In-game verification** (only the user can do this): build the fork, launch, start a
  file, run `mh_playtest` → should spawn in the editor's geometry with working collision.
- **One-click auto-warp**: replace the manual console command with a GameInteractor
  `OnLoadGame` hook that reads `mh/info` (via ArchiveManager) and sets
  `gSaveContext.entranceIndex` + age + inventory so a new file spawns directly in the level.
- **Inventory application** (D6): apply `mh/info.items[]` / "debug" save on boot.
- **Textured geometry**: OtrRoomGeometry is currently vertex-coloured/shade-only; add
  textured display lists (OTEX resources + G_SETTIMG_OTR refs) — the texture resource
  format is already proven (byte-exact vs oot.o2r OTEX).
- **2Ship/MM**: same pipeline; needs the MM scene-name/version table + MM command variants.

## D2. Load existing levels/dungeons/rooms from the loaded ROM/SoH/2Ship
**Asked:** Open existing game scenes/rooms and see fully **textured + lit world geometry**.
Togglable **scene setups** from the detected scene list — e.g. Hyrule Field has child
gameplay (Kaepora Gaebora, peahats, bushes, stalchild triggers), adult gameplay (Big
Poes, Running Man + scripted waypoints), and cutscene setups (credits, Ganondorf-horse
+ Zelda/Impa intro, etc.). Option to **Save As new level** or **overwrite existing**,
always producing a NEW rom — originals stay read-only unless a safety check in options
is disabled. MM example: Stock Pot Inn with time-of-day scripted actors (Anju, Postman,
"???" toilet hand, Kafei) incl. hidden actors. **If any OoT/MM/SoH/2Ship scene feature
is unaccounted for during this, add support for it** (collision, paths, waterboxes,
alternate/cutscene headers, transition actors, environment/lighting, cutscenes, etc.).
**How:** Build a scene/room **importer** (inverse of the exporters): parse a ROM scene's
header commands incl. **alternate headers (0x18)** → expose each setup as a toggle;
parse room mesh (display lists → textured geometry via an F3DEX2 interpreter), collision,
actor lists, transition actors, paths, env/lighting. Load referenced object textures via
segments 4/5 (keep files) + 2/3. Render with the textured+lit 3D pipeline. Map into the
editor's ZScene/ZRoom/ZActor model (extend the model for every encountered feature).
Read-only ROM guard: never write the source ROM; a `RomInjector`-produced **new** ROM is
the only output; add an Options toggle (default OFF) to allow overwriting originals.

## D3. Project64 vanilla playtest
**Asked:** For vanilla OoT/MM N64 configs, playtest via a (forked) Project64 + a
custom-injected MQ debug ROM with **direct warp**. (P64 source in READ_ONLY_SourceCodes;
user's old work at `D:\OOT_Mapping`.)
**How:** Use `RomInjector` to inject the scene into a copy of the MQ debug ROM; set a
boot entrance / patch so it warps directly to the scene (or a small P64 Lua/cheat /
fork that writes `gSaveContext.entranceIndex` + triggers the warp). Launch
`Project64.exe <rom>`. Editor "Playtest (N64)" action mirrors PlaytestDialog.

## D4. Real skybox preview from ROM
**Asked:** The 3D shaded+textured editor preview should show the **actual** OoT/MM
skybox (not just a gradient) for vanilla rom configs.
**How:** Read the scene's skybox config; for the standard sky, render the day/night
gradient + the **cloud/skybox texture** extracted from the ROM (gameplay skybox files);
for cubemap-style skyboxes, build a cubemap. Replace SkyRenderer's flat gradient when a
ROM/skybox is available.

## D5. Entity model rendering (resolver WIP)
**Asked:** Placed actors show their real in-game models with textures in their idle
animation loop (e.g. Dark Link).
**Status:** Resolver WIP, UNCOMMITTED. `ActorRenderDb` (parses ActorRendering.xml:
actor+var → object, scale, anim, hierarchy) works. `ObjectTable` (object name→id via
decomp object_table.h + locate gObjectTable in ROM) finds multiple candidate tables and
is **not validated** to the right one.
**How:** Validate gObjectTable (cross-check via gameplay_keep seg-4 references, or known
object); build an F3DEX2 display-list interpreter → textured mesh; render at actor
pos×scale; later add SkelAnime skeleton + idle animation playback.

## D6. Custom-inventory application (engine)
**Asked (part of playtest):** Custom item checklist should actually grant those items
in-game.
**How:** Engine reads `mh/info.items[]`, maps each to gSaveContext item/equip/upgrade
slots, applies on boot (part of D1).

## D7. Obsolete-entity placeholder + render-all-entities (newest queue — do LAST)
**Asked:** (a) When loading a level, if the editor doesn't recognize an entity, it must
still load it as an **"obsolete" entity** drawn in the 3D view as a **billboard sprite of
the Eyeball Frog** (gag texture so a non-loading/nonexistent entity is visually
identifiable). (b) **Every** entity that has a model should render — in 3D view as its
real model, in the 2D views as that model's **wireframe** — in its default resting/idle
state. Examples: Link player-start → Young or Adult Link model depending on the scene;
Dark Link entity → Dark Link's model (which is `en_torch2` in OoT).
**Status:** Not started. Builds on D5 (entity model rendering / ActorRenderDb +
ObjectTable). Append after everything else.
**How:**
- Extend the actor→model resolver (D5) to cover *special* placements, not just enemies:
  the player spawn (age-dependent Link object — `object_link_boy`/`object_link_child`),
  and actors whose visible model is a *different* object than their actor id implies
  (Dark Link → `En_Torch2`/its object). Drive these from `ActorRendering.xml` overrides
  plus a small hand-maintained special-case table.
- **Unknown entity → Eyeball Frog billboard:** when the resolver returns no model for an
  actor id/variant, fall back to an "obsolete entity" render: a camera-facing **billboard
  quad** textured with the Eyeball Frog texture (extract `object_zo`/eyeball-frog gag
  texture from ROM, or bundle a placeholder). Tag the placement as Obsolete in the model
  so Save round-trips it unchanged (never silently drop unknown actors).
- **2D wireframe:** in the ortho viewports, render each resolved model's mesh as edges
  only (reuse the F3DEX2-decoded vertex/triangle buffers from D5, draw GL_LINES); the
  obsolete billboard shows as a simple box/cross marker in 2D.
- Idle pose: load each model's default/idle SkelAnime animation, evaluate frame 0 (or
  loop) for the resting pose (shared with D5's animation work).

## D8. Select brushes in 3D view with the Select tool
**Asked:** The Select tool should be able to pick/select brushes directly in the 3D view
(not only the 2D ortho views).
**How:** Add ray-pick in the 3D viewport: unproject the mouse ray from the GL camera,
intersect against each solid's faces (Möller–Trumbore per triangle), pick nearest hit →
select that `Solid` (respecting additive/shift-select). Reuse the existing selection
model the 2D views drive; just add a 3D picker + hover highlight.

## D9. Double-click an entity to open its configuration panel
**Asked:** Double-clicking an entity in either the 3D or 2D view should immediately open
that entity's configuration/properties panel.
**How:** On double-click, ray-pick (3D) / box-pick (2D) the actor billboard/marker →
select the `ZActor` → open the existing entity property panel focused on it. Wire a
`DoubleClick` handler in both viewport controls to a shared `EditEntity(actor)` action.

## D10. Toggle entity visibility in 2D and 3D views separately
**Asked:** Independent show/hide toggles for entities in the 2D views vs the 3D view.
**How:** Add two view flags (`ShowEntities2D`, `ShowEntities3D`) on the viewport/render
settings; gate actor drawing in each renderer on its flag. Surface as two toggle buttons
/ menu checkboxes. (Extends to per-room visibility in D14.)

## D11. Vertex-shade paint tool (spray-paint vertex colors in 3D)
**Asked:** A NEW tool to "spray paint" RGB shading onto brush surfaces in the 3D view,
Blender vertex-paint style: pick an RGB color (clickable rainbow picker + opacity),
default black, then paint on a brush to shade it. Must be stored efficiently and be
reversible (undo).
**How:** OoT/MM vertex lighting IS per-vertex color (the Vtx.cn RGBA we already emit), so
this maps naturally: store an optional `Dictionary<vertexKey,Color>` (or per-face vertex
color array) override on each `Solid`, default null (= use scene light). Paint = brush
falloff that blends color into the nearest vertices' overrides; opacity = blend factor.
Reversibility: push an undo record of (vertex, oldColor) spans on stroke begin/end (one
undo entry per stroke). Render uses the override color in the GL vertex buffer; export
writes it into the Vtx color bytes. New `ShadePaintTool` with a color/opacity palette UI
(reuse a standard HSV rainbow control).

## D12. Full dungeon functionality (keys, locking doors, chests, items, ice traps)
**Asked:** Keys; doors; doors that lock behind you on entry until unlocked (boss defeat /
switch / scriptable sequence); chests with configurable contents — small key (current
dungeon), boss key (current dungeon), dungeon map, compass, ANY game item, or an ice
trap, etc. Needs auto-generated dungeon **minimaps** and dungeon **name textures** (à la
OcarinaSharp; see D15).
**How:** Model doors as the OoT door actors/transition actors (`En_Door`, `Door_Shutter`
for locking shutters) with a "locked until <flag>" property; the lock flag ties to the
dungeon's `switchFlags`/`clearFlag` (boss clear) — these are gameplay flags the engine
already honors, so set the actor params + flags rather than custom behavior. Chests =
`En_Box` with contents param → map editor item picker to OoT `GetItem`/chest content
IDs (small key, boss key, map, compass, any item, ice trap = its item id). Per-dungeon
key counts derive from placed locked doors. Minimap auto-gen + name textures = D15.
Vanilla-compatible: only place stock actors with stock params/flags; no behavior changes.

## D13. (folded into D12) — dungeon item/chest content picker
**How:** A reusable "game item" picker enumerating OoT/MM item ids (incl. dungeon-scoped
small key / boss key / map / compass and ice trap), shared by chests (D12), and any actor
that grants items.

## D14. Multi-room dungeons load as one big level with per-room visibility
**Asked:** Dungeons/levels with multiple connected sections (Jabu-Jabu, Forest Temple,
Stone Tower, etc.) should load as ONE big level with togglable per-room visibility in the
existing scene/room sidebar, showing all rooms + all their entities, with connected doors
configurable and rearrangeable.
**How:** Importer (D2) loads all rooms of a scene into one document; the sidebar already
lists rooms — add a visibility checkbox per room driving the renderer (positions are
already in shared scene space). Transition/door actors between rooms expose a "connects
room A↔B" link; allow editing/reassigning the target room + spawn. Keep each room's
geometry/actors in its own `ZRoom` so Save writes them back to the right room file.

## D15. Area name textures + dungeon minimaps (OcarinaSharp-style)
**Asked:** Create the area **nameplate** that appears on entering a scene — OoT uses a
TEXTURE ("Kokiri Forest", "Fire Temple", …) with the correct font; **MM uses on-screen
TEXT on a header instead of a texture** (both must be supported). Generate from the
user's typed text with the correct font, mapped into the output game config. Also
auto-create dungeon **minimaps**.
**How:** OoT: render typed text with the OoT title-card font (extract the font glyph
texture from ROM, or bundle it) into the title-card texture dimensions/format the scene's
title-card slot expects; write it as an OTEX resource + point the scene/entrance title
card at it. MM: set the message/text-id path instead of a texture. Minimaps: rasterize a
top-down outline of each room's floor collision/geometry into the pause-map texture
format + register map data (dungeon map/compass markers). Provide a name-entry UI.

## D16. Water, NODRAW, player-clip, and projectile-clip brushes
**Asked:** (a) Reliable **water**: Hammer-style — a brush with a NODRAW texture on all
faces except the top; player can swim/dive (with water effects, sinking via iron boots /
MM Zora), animated, and scriptable (Lake Hylia / Water Temple). (b) A **NODRAW** texture
(yellow, "NODRAW" text) that never renders in-game and is skipped on export. (c) **Player
clip** brushes and (d) OoT/MM's equivalent of **block-projectiles** ("blockbullets") if
they exist.
**How:** Water in OoT/MM is a **waterbox** (scene collision `WaterBox` list: x/z/w/d,
ySurface, room, properties), NOT a visible brush — so: a water brush's top face defines
the waterbox rectangle + surface Y; emit a WaterBox into the collision header; swimming /
iron-boots / Zora behavior is engine-native once the waterbox exists. Animated/scriptable
surface = optional water-surface display list + a scene flag. NODRAW: add a built-in
special texture (yellow "NODRAW" swatch) flagged `NoRender` → skipped in the GL pass and
omitted from exported display lists (faces still contribute collision). Player-clip &
projectile-clip: OoT collision **poly flags** encode surface behavior — map dedicated
special textures (CLIP, BLOCKPROJECTILE) to collision polys with the right flags
(walls/floors that block the player; the projectile-blocking flag) and no visible geometry.

## D17. Safe boss/miniboss placement + per-entity survival guides
**Asked:** Add **careful, vanilla-compatible** support for placing bosses and minibosses
in custom rooms WITHOUT changing their real behavior or the game's code (so they still
work in stock N64/z64 ROMs and SoH/2Ship). E.g. building a from-scratch dungeon containing
Phantom Ganon, Barinade, Morpha, etc. must spawn them in a way that doesn't crash/confuse
them for being outside their normal room. If a boss can only be handled a specific way,
surface a **viewable per-entity guide** in the entity list (next to each entity) that
explains the conditions that actor needs to spawn and survive without crashing/breaking,
derived from its code. Applies to OoT and MM.
**How:** Bosses/minibosses depend on room invariants their original scene guarantees —
required objects loaded (boss object + gameplay/dungeon keep), a boss/warp setup, specific
collision (e.g. Morpha needs a water box; Volvagia needs lava floor polys), camera/cutscene
setup actors, room size/segment assumptions, sometimes a clear-flag or companion actor.
So: (1) build a **boss-requirements database** (JSON/XML keyed by actor id + variant) listing
required objects, collision/waterbox needs, companion/setup actors, params, and known
crash conditions — sourced from decomp (`z_boss_*`, `z_en_*`) `Init`/`Update` reads. (2)
When the user places a boss, the editor **auto-satisfies** its requirements (adds the boss
object to the room SetObjectList, injects needed waterbox/collision/companion actors,
warns if the room lacks something) — all via stock actors/params only, never code changes.
(3) Show each entity's requirement text as a **guide panel** beside the entity list (and a
warning badge when unmet). Keep it data-driven so MM bosses (Odolwa/Goht/Gyorg/Twinmold,
Majora) extend the same DB. Ties to D5 (models), D12 (dungeon flags), D16 (waterboxes).
