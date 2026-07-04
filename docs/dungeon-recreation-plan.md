# Dungeon Recreation Plan — casual-friendly mechanism authoring

**Goal.** Let a casual end-user mechanically recreate any OoT/MM dungeon puzzle in the
editor without touching raw params or hand-wiring flags, by shipping a palette of
**pre-wired mechanism presets** — each a composition of *real vanilla actors* + standard
scene/flag data. Derived from the 2026-07-03 dungeon-recreatability comb (all 26 OoT+MM
dungeons/mini-dungeons cross-referenced vs walkthroughs).

## Hard constraint — vanilla compatibility (all 4 engines)

Every preset MUST emit only:
1. **standard actor entries** (correct id + params, verified against decomp), and/or
2. **standard scene/room binary** (collision surface types, exit lists, cmd 0x0E transition
   actors, cmd 0x18 alt-header setups), and/or
3. **flag-bus wiring** — one actor writes a scene flag, another reads the same flag.

The authored level must run on **unmodified OoT & MM carts** and on **vanilla SoH & 2Ship**.
No engine behaviour is invented. The playtest *inventory* pipeline is the ONLY engine-coupled
convenience and is explicitly out of scope for authored-level compatibility.

> Verify-before-build rule: never assume an actor id/param. Confirm against
> `READ_ONLY_SourceCodes/oot-master` / `mm-main` decomp. (Caught during planning:
> `En_Ex_Ruppy` 0x0131 is the Zora *diving-game* rupee, **not** the silver-rupee puzzle actor.)

## Gap taxonomy (what actually needs building)

### Tier 1 — already authorable (no work)
Geometry, collision incl. climb/ladder/crawl/grab, actors, masks & player-form abilities,
the flag bus, waterboxes, locked doors + small/boss keys, warps, torches, chests,
setup LAYERS (child/adult, day/night). ~80% of dungeon content. Corrections from the comb:
room↔room transitions are carried by placing a **door** (auto-emits the cmd-0x0E transition);
multi-room from-scratch dungeons already play.

### Tier 2 — authorable but fiddly → build PRESETS
| Preset | Vanilla recipe (to verify per-game) | Reused by |
|---|---|---|
| Boss ending | boss-clear flag → Door_Warp1 blue warp (OoT 0x005D/MM 0x0038) + Item_B_Heart (OoT 0x005F/MM 0x003A) + scene exit | every main dungeon |
| Room-clear door | Door_Shutter/En_Door "type 1 = open on room clear" | most combat rooms |
| Multi-switch AND-gate | N switches share a switch flag; door reads it (all-must-set) | Ganon trials, temples |
| Silver-rupee room | *actor TBD — verify OoT silver-rupee actor id first* → shared switch flag → door | Shadow, Spirit, Gerudo, Ganon, Ice, Stone Tower |
| Push-block → floor switch | Bg push-block + Obj_Switch FLOOR subtype on shared flag | Spirit, Jabu, many |
| Torch → door | Obj_Syokudai lit-flag → door | Deku, Dodongo, Ikana |
| Song-of-Time block | Obj_Timeblock appear/vanish on song | Gerudo, Ganon, Shadow |
| Sun-switch / Mirror-Shield | OoT sun switch / MM Obj_Lightswitch → door | Spirit, Stone Tower, Ikana |

### Tier 3 — scene-hardcoded/scripted → document + provide FALLBACKS (never engine-hack)
| Mechanic | Why hard | Casual fallback (still vanilla) |
|---|---|---|
| Water Temple 3-level water | Bg_Mizu_Water hardcoded to scene | per-setup waterbox layers, or switch-raised single waterbox |
| Great Bay current network | networked directional flow, scene-bound | local current actors + flag-gated water elevators (not networked) |
| Snowhead pillar elevator | scene-hardcoded vertical spine | generic elevator platform actor / static stairs |
| Stone Tower inversion | whole-dungeon scene-SWAP | two linked scenes (right-side-up + inverted) via scene exit |
| Dodongo skull eye-bomb | one-off scripted scene gimmick | two shootable eye-switches → door (skip mouth animation) |
| Jabu escort-Ruto | scripted companion NPC | redesign as weighted-switch puzzle without the carry |
| Forest twisted corridors | live room rotation | static pre-rotated geometry |
| Boss multi-phase / Ganon collapse | scripted sequence | fight is authorable; scripted finale is not |
| MM 3-day timed rewards | global clock | drop the time-gate; give reward on clear |
| Scripted cutscenes | not authorable | retain from an imported scene, or omit |

## Implementation roadmap (iterate)

- **Phase 0 — infrastructure.** A "Dungeon Mechanisms" insert palette (menu/toolbar) that
  drops a pre-wired actor GROUP at the cursor: auto-assigns a free scene flag (reusing
  `MapDocument.NextFree*Flag` + `SeedPlacementDefault`), game-aware (OoT/MM), undoable,
  selectable as a unit. Each preset = a small factory returning `ZActor[]` + optional
  surface/exit data, with a `--presettest` self-test asserting emitted ids/params/flags.
- **Phase 1 — Tier-2 presets** (highest reuse, fully verified): boss ending, room-clear
  door, multi-switch AND-gate, torch→door, push-block→switch, silver-rupee room.
- **Phase 2 — light/song presets**: sun-switch/Mirror-Shield, Song-of-Time block.
- **Phase 3 — Tier-3 fallback templates** + a per-dungeon "recipe" doc (step list a casual
  user follows), and the Stone-Tower two-scene link helper.

Each preset: decomp-sourced comment, `--presettest` coverage, confirmed to emit
vanilla-only data (round-trips through N64 SceneExporter *and* OTR writer for all 4 engines).
