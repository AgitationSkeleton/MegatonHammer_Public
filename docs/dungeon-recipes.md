# Dungeon Recipes — rebuild a Zelda 64 dungeon, room by room

A casual step-list for recreating each OoT/MM dungeon's signature mechanics in Megaton
Hammer using **only vanilla-compatible pieces** — the *Dungeon Mechanisms* preset palette
(pre-wired groups of real actors) plus the actor property editor. Everything here runs on
unmodified OoT/MM carts and on vanilla SoH/2Ship. Grounded in the 2026-07-03 recreatability
comb (see `docs/dungeon-recreation-plan.md`) and the verified preset/actor set.

## How to read a recipe

- **Presets** are inserted from *Dungeon Mechanisms* and drop a pre-wired, grouped set of
  actors (one click = a working puzzle on a fresh scene flag). Named in **bold**.
- **Actors** are placed from *Place Actor*; their property panel now shows friendly
  dropdowns (type/size/flag) instead of raw hex.
- **Fallback** marks a Tier-3 mechanic that is scene-hardcoded in the original and is
  rebuilt as a simpler vanilla equivalent (never an engine hack).

The universal building blocks:

| Want | Use |
|---|---|
| Hit a switch → a gate opens | **Crystal switch → gate** / **Eye switch → gate** |
| Light one torch → a gate opens | **Torch → gate** |
| Light *all* torches → a gate opens (AND) | **Light all torches → gate** |
| Collect all silver rupees → unbar | **Silver-rupee room → gate** |
| Weigh down a floor switch | **Push statue onto switch → gate** (Armos; push blocks can't hold a switch) |
| Defeat the boss → reward + leave | **Boss reward + exit** |
| Room link | place a **Door** / **Shutter Door** between two rooms (auto-emits the transition) |
| Locked progression | **Door**, type *Locked (small key)* + a chest holding a Small Key |
| Combat gate | **Shutter Door**, type *1 (opens on room clear)* |

---

## Ocarina of Time

### Deku Tree  — *Mostly recreatable*
- Web floor to the basement: place a **Cobweb (Bg_Ydan_Sp)**, floor type — burn or drop through.
- Scrub trio / gohma antechamber: place **Deku Baba** (Normal/Big) and **Deku Scrub** actors;
  gate the exit with a **Shutter Door** *opens on room clear*.
- Compass/torch rooms: **Torch → gate**.
- Boss: place Gohma, then **Boss reward + exit** (Heart Container + warp pad → set the exit).

### Dodongo's Cavern  — *Mostly recreatable*
- Main lobby bomb-wall: place a **Bombable Wall (Bg_Breakwall)** as the room link.
- Lizalfos miniboss room: two **Lizalfos** actors set to *mini-boss A* and *mini-boss B*, both
  given the **same Room-clear switch flag**; wire a **Shutter Door** to that flag.
- Fire/eye rooms: **Eye switch → gate** and **Torch → gate**.
- **Fallback** — the giant skull's eye-bomb "open the mouth" gimmick is a one-off scripted
  scene event. Rebuild it as **two Eye switches → gate** (shoot both eyes to open the way).
- King Dodongo: **Boss reward + exit**.

### Forest Temple  — *Mostly recreatable*
- Courtyard well / elevator: use **Push Block (Obj_Oshihiki)** for reach puzzles (size sets
  the strength needed — Large needs the Bracelet, Huge the Silver Gauntlets).
- Twisted-hallway block: place a **Push Block** to reach the ledge.
- Poe-sisters torch/painting puzzle: **Light all torches → gate** captures the "all four flames"
  idea (the shared torch-group flag is vanilla's real AND-gate).
- Floormaster ambush: **Wallmaster** (trigger = *Proximity*), then a **Shutter Door** *room clear*.
- **Fallback** — the live room rotation is scene-hardcoded; build the corridors in their final
  (already-rotated) orientation as static geometry.
- Phantom Ganon: **Boss reward + exit**.

### Fire Temple  — *Mostly / Fully recreatable*
- Barred-cell Gorons behind switches: **Crystal switch → gate** (the gate reader is the Fire
  Temple grate the preset already uses).
- Fire-maze torches: **Light all torches → gate** for the "light them together" rooms, or
  **Torch → gate** for a single flame.
- Weighted blocks / Boss key: **Push statue onto switch → gate** for the Armos-weight puzzles.
- Volvagia: **Boss reward + exit**.

### Ice Cavern  — *Mostly / Fully recreatable*
- Red-ice blocking items/paths: place **Ice Crystal (Obj_Ice_Poly)** (Small/Medium/Large) — melt
  with Blue Fire (a bottled scoop). Put a **Freestanding Item** or chest behind it.
- Wolfos room for the Iron Boots: a **Wolfos** set to *White (mini-boss)* with a Room-clear
  switch flag → wire a **Shutter Door** to that flag.
- Keese/Freezard filler: place **Keese** (element type; add *Invisible (Lens)* for the well-hidden
  ones) around the ice.

### Gerudo Training Ground  — *Mostly / Fully recreatable*
- The whole dungeon is switch/silver-rupee/lock puzzles and **no boss** — the most reproducible
  in the game.
- Silver-rupee cells: one **Silver-rupee room → gate** per room (an invisible tracker counts
  the rupees on a shared flag; the gate opens on the last).
- Torch cells: **Light all torches → gate**.
- Lava/eye cells: **Eye switch → gate**.
- Lock progression: **Door** *Locked (small key)* + chests holding Small Keys.

### Ganon's Castle  — *Partially / Mostly recreatable*
- Each of the 6 trials is individually authorable: Forest = **Silver-rupee room → gate**;
  Fire = **Light all torches → gate**; Water = **Ice Crystal** + Blue Fire; Shadow/Spirit/Light
  = **Eye/Crystal switch → gate** + **Push statue onto switch → gate**.
- **Fallback** — the 6-trials→tower master gate and the Ganondorf/Ganon multi-phase finale are
  scripted; author the trials as a hub of rooms and treat the finale as retained-only (or omit).

---

## Majora's Mask

*(MM switch flags are 0–127; MM actor ids differ from OoT — the editor handles both.)*

### Woodfall Temple  — *Mostly recreatable*
- Deku-flower launch and water-level valves: build the reachable layout with static geometry +
  a switch-raised waterbox layer.
- Barred rooms: **Switch → ladder appears** (MM's clean switch-gated unlock: strike a crystal
  switch, a climbable ladder fades in).
- Bomb-blocked path: **Bombable Boulder (Obj_Bombiwa)** — it sets its switch flag when bombed,
  so it stays gone *and* can trigger a gate.
- Beehives over water: **Beehive (Obj_Comb)** with a drop + collectible flag.
- Odolwa: place the boss, then a warp/heart reward (build a boss-exit by hand — the *Boss reward
  + exit* preset is OoT-flavoured; on MM place Item_B_Heart + a warp trigger pad).

### Snowhead / Great Bay / Stone Tower  — *Partially / Not (documented fallbacks)*
- **Snowhead** — the central rising-pillar elevator is scene-hardcoded. **Fallback**: static
  stairs or a switch-gated platform to carry the vertical progression.
- **Great Bay** — the networked current/valve water machine is the least reproducible mechanic
  in either game. **Fallback**: local, non-networked flow actors + flag-gated water-level layers.
- **Stone Tower** — whole-dungeon inversion is a scene *swap*, not a live transform. **Fallback**:
  build two linked scenes (right-side-up + a pre-built inverted copy) joined by a scene exit.

### Beneath the Well / Secret Shrine / Spider Houses  — *Mostly / Fully recreatable*
- Well trading + Gibdo requests: place **ReDead/Gibdo** actors (type dropdown covers Gibdo vs the
  ReDead variants) and author their lines in the **Dialogue Editor**.
- Spider houses: place **Skulltula** (Normal/Big/Invisible-Lens) and Gold Skulltula tokens; the
  Day-1 timed reward is a cosmetic gap — grant the reward on clear instead.

---

## Notes for the recipe author

- Prefer a **preset** over hand-placing + hand-wiring — it allocates a free scene flag, groups the
  actors, and is undoable as a unit. Extend a preset by adding more readers on the same flag.
- The **flag bus** is the wiring: one actor *sets* a switch flag (a setter, e.g. a switch/torch),
  another *reads* it (a reader, e.g. a gate/ladder/door). The Outputs/Inputs panel on an actor
  shows the live wires.
- Anything marked **Fallback** is a Tier-3 scene-hardcoded original; the rebuild is a legitimate
  vanilla equivalent, not an engine modification — the level still runs on stock carts and engines.
