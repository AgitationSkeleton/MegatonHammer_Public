# Special Scene Mechanics — how they work + what the editor needs

Investigation (decomp-cited) of four signature dungeon mechanics, and the data an editor must
model to recreate them. Sources: `READ_ONLY_SourceCodes/oot-master` and `…/2ship2harkinian-develop/mm`.

## 1. Water Temple — Dark Link miniboss room (the "infinite mirage")

The illusory endless room is **not** a camera/room-header trick. A prop actor **`En_Blkobj`**
(OoT id **0x0136**, `ovl_En_Blkobj/z_en_blkobj.c`) renders two display lists from `object_blkobj`
and cross-fades them by alpha: `gIllusionRoomNormalDL` (the real room) and `gIllusionRoomIllusionDL`
(the mirrored/endless far wall + door), with a `Gfx_TwoTexScroll` UV scroll for depth. It also owns
the room's `gIllusionRoomCol` collision (so the mirage is solid). When **Dark Link** (`En_Torch2`,
OoT id **0x0033**, a Player-sized boss actor) is defeated, `Flags_SetClear(play, room)` fires; the
Blkobj then fades to the real room and drops the illusion collision (`z_en_blkobj.c:91-116`).

**Editor support:** place `En_Blkobj` (0x0136) + `En_Torch2` (0x0033) in a room; the room-clear flag
+ the object's baked illusion DLs do the rest. No special scene data — both are ordinary actors.

## 2. Water Temple — three water levels (raise/lower)

A single hidden BG actor **`Bg_Mizu_Water`** (OoT id **0x0065**, `ovl_Bg_Mizu_Water`) drives the
water. Params: `type = params & 0xFF`, `switchFlag = (params>>8) & 0xFF`. Each frame it reads three
**scene switch flags** and rewrites the **`ySurface`** of eight waterboxes (`z_bg_mizu_water.c`):

| Level | Y | Switch flag |
|---|---:|---|
| F3 (top) | 765 | 0x1E |
| F2 | 445 | 0x1D |
| F1 | -15 | 0x1C |
| B1 (drained) | -835 | (none set) |

The Triforce switches set 0x1C/0x1D/0x1E; the actor animates the surface and plays the rise/fall
one-point cutscene. **Water level is a waterbox property, not a moving mesh.**

A **WaterBox** lives in the collision header (`z64bgcheck.h:102`): `{ s16 xMin, ySurface, zMin,
xLength, zLength; u32 properties }` (0x10 bytes); `properties` packs bgCamIndex / lightIndex / room
/ flags. The CollisionHeader has `numWaterBoxes` + a `waterBoxList*`.

**Editor support (the concrete gap):** a **waterbox primitive** — a box region with an editable
top-surface Y + room/flags — that the collision builder writes into the header (currently it emits
`numWaterBoxes = 0`). Plus placing `Bg_Mizu_Water` (0x0065) and wiring switch flags 0x1C–0x1E. This
is the editor's missing collision primitive (audit item #9) and is shared by every water scene.

## 3. Great Deku Tree — death texture-morph

OoT animates a scene per frame via a **scene draw config**: each scene-table entry names a
`SceneDrawConfig` id (`z64scene.h:401`, `SDC_DEFAULT=0 … SDC_DEKU_TREE=19 … SDC_DEKU_TREE_BOSS=28`)
that indexes `sSceneDrawConfigs[]` (`z_scene_table.c`); `Scene_Draw` calls it every frame. The Deku
Tree boss config scrolls/animates its segment-8 textures; the withered look is gated by an event
flag (`EVENTCHKINF_70`, set when Gohma dies). So the "morph" is a **per-scene draw-config function +
a runtime flag**, not geometry or an alt header.

**Editor support:** expose the **scene draw-config id** as a scene-level property (a dropdown of the
known configs incl. material-animation / morphing ones) and write it into the scene-table entry on
export. The actual per-frame curve lives in engine code keyed by that id — the editor selects it.
(MM's equivalent is `SCENE_DRAW_CFG_MAT_ANIM`, the material/texture-animation config Stone Tower uses.)

## 4. Stone Tower Temple — inversion gimmick

Inversion is **two separate scenes**, not an alternate header: `SCENE_INISIE_N` (normal) and
`SCENE_INISIE_R` (inverted), plus the exteriors `SCENE_F40`/`SCENE_F41` (`scene_table.h`). The red
sun **flip switch** is `Obj_Lightswitch` type **FLIP** (`LIGHTSWITCH_TYPE_FLIP`, params: type
`[4,2]`, invisible `[3,1]`, switch flag `[8,7]`) — shooting it with light arrows plays a cinema and
**sets its switch flag**. A sentinel actor **`Obj_Wturn`** (MM id **0x027**, "Stone Tower Temple
Inverter", `switchFlag = params`) watches that flag; when it disagrees with the current scene it runs
the 180° camera/gravity-flip cutscene and triggers the transition to the inverted scene
(`z_obj_wturn.c:53-128`).

**Editor support:** author **two scenes** (the editor already supports multiple scenes), place
`Obj_Lightswitch` FLIP (already in the MM logic schema) + `Obj_Wturn` (0x027) sharing one switch
flag. The flip is a flag-driven scene-to-scene transition, modeled with the existing scene + actor +
flag-connection tooling.

## What this adds to the editor roadmap

| Mechanic | Already supported | New support needed |
|---|---|---|
| Dark Link room | place En_Blkobj + En_Torch2 (actors) | actor schemas (added) |
| Water levels | Bg_Mizu_Water actor + switch flags | **waterbox primitive** (collision export/import) |
| Deku Tree morph | — | **scene draw-config id** property |
| Stone Tower invert | 2 scenes + flip switch + flag I/O | Obj_Wturn schema (added) |
| Reflective water/floor | — | **named draw-config presets** (scene property) |

## Reflective water / floor parallax (Zora's Domain, Water Temple, Chamber of the Sages, Great Bay Temple)

Investigated against SoH (`soh/include/z64scene.h`, `soh/src/code/z_scene_table.c`) and 2Ship
(`mm/src/code/z_room.c`, `mm/src/overlays/actors/ovl_player_actor/z_player.c`).

**Mechanism.** The "reflection" is *not* a stencil pass, a render-to-texture, or a special room
shape — it is **the player drawn a second time, flipped through the water plane**. When the active
scene draw config is a reflective one, the engine re-draws Link with a **negative Y scale** and a
**depth-based parallax Y offset** (`2.0f * depthInWater`) using the front-face-cull display list
(`z_player.c` ~13428–13447, gated by `PLAYER_STATE2_4000000`). The shimmering water surface itself
is the room's own translucent mesh, animated by the scene draw-config routine
(`Gfx_TwoTexScrollEx` material scroll). So the whole effect is selected by the **scene draw config**:

| Scene | Draw config (index) |
|---|---|
| Zora's Domain (OoT) | `SDC_ZORAS_DOMAIN` = 6 |
| Water Temple (OoT) | `SDC_WATER_TEMPLE` = 23 |
| Chamber of the Sages (OoT) | `SDC_CHAMBER_OF_THE_SAGES` = 32 |
| Great Bay Temple (MM) | `SCENE_DRAW_CFG_GREAT_BAY_TEMPLE` = 6 |

**Editor support.** The scene's **Draw config** field is now a **named-preset dropdown**
(`DrawConfigPresets`, per game — OoT 0–52, MM 0–7) with the reflective configs labelled, so a
scene can opt into the reflective water/floor (or the Deku Tree texture-morph death, or any material
animation) by name instead of a raw byte. The effect needs the matching room mesh (a translucent
water surface) + a waterbox, both already authorable; the draw config is what turns on the
flipped-player reflection at runtime.
