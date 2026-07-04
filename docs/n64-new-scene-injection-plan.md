# N64 new-scene injection — plan & probing

Goal: inject a Megaton Hammer level into vanilla **OoT/MM N64** as a **NEW** scene/room/entrance (the way
SoH/2Ship add `SCENE_MH_APPEND`), instead of **overwriting** an existing scene slot — while **keeping the
overwrite mode** as a fallback. Also fix two gaps the overwrite mode has vs SoH/2Ship: the injected scene
inherits the vanilla map's **time-of-day** (night) and does **not** get the editor's **shaded lighting**.

---

## 1. BACKUP NOTE — the current (overwrite) pipeline (as of 2026-06-29)

Code: `SelfTest/MmInjectScene.cs` (MM), the OoT path in `PlaytestPack.RunN64` / `Export/SceneExporter`,
`Rom/MmRomDecompressor.cs`, `Rom/SceneTableLocator.cs`. PJ64 fork: `forks/pj64` (+ backup bundle in
`forks/backups/`). This mode WORKS (user-confirmed: MM Termina Field injection renders + walkable; OoT too)
and MUST remain available.

How it works (MM):
1. **Decompress** the US-retail ROM (Yaz0 files → flat image) — `MmRomDecompressor`.
2. **Build** the editor level → scene + room **binaries** with the real exporter (`SceneExporter`/`RoomExporter`/
   `DisplayListBuilder`), same binaries the OTR path derives from.
3. **Overwrite the target slot IN PLACE**: `gSceneTable` @ **0xC5A1E0** (US retail decompressed), target
   **Termina Field slot 0x2D** (1 room, cold-loads & renders cleanly). The slot's scene-table entry vrom is
   read and the scene/room files at that vrom are overwritten with my data.
4. **Patch ALL headers** (primary @off 0 + alt headers from cmd 0x18): collision (0x03 → my collision block
   at a free offset `FreeColOff` 0x35000 within the slot), room list (0x04 → my room), music (0x15 seqId →
   chosen track), **transition actors (0x0E → none)** and **actor-cutscene list (0x1B → none)** so the
   vanilla door/entrance establishing cutscene doesn't fire, and **env light (0x0F → bright neutral 200)**.
5. **Draw config**: scene-table byte +0xB → 1 (MAT_ANIM) when the scene has an AnimatedMaterial list (cmd
   0x1A), else 0.
6. **Warp**: reach the slot via its **vanilla entrance**; time-of-day is whatever that entrance/scene forces.

Key constants: `SceneTableVrom=0xC5A1E0`, `TargetSceneId=0x2D` (MM), `FreeColOff=0x35000`. OoT uses the
analogous gSceneTable + a target slot. **Editor ROM = retail NTSC-1.0**; the *playtest* debug ROM
(gc-eu-mq-dbg) has DIFFERENT addresses ([[megaton-hammer-pj64-savecontext-base]],
[[megaton-hammer-keep-offsets-rom-version]]).

### Why the two gaps exist in overwrite mode
- **Time-of-day / night**: we reuse the *vanilla* scene + its entrance, which carries the vanilla map's
  default `dayTime` (and some maps force night / a fixed sky). We never set `gSaveContext.dayTime` like
  SoH/2Ship set `save.time` at boot — so the injected level inherits the host map's time.
- **Shaded lighting NOT inherited**: step 4 forces env light to **bright neutral (200,200,200)**, the
  opposite of the editor's `LightingMethod=2` (Shaded). The OTR/SoH path bakes per-face environment shade
  into the room DL (`DisplayListBuilder` gate); the N64 path currently does not carry that through, and the
  header light override flattens it. So vanilla OoT/MM look fullbright vs SoH/2Ship's shaded.

---

## 2. NEW-scene injection — the plan

Add a dedicated scene the way SoH/2Ship added `SCENE_MH_APPEND`, leaving every vanilla scene intact and
giving the level full authority over spawn, time, lighting, music, and headers.

### 2a. Where the new scene's data goes (probe first)
The vanilla scene/room files live in the (decompressed) ROM addressed by `gSceneTable[sceneId].sceneStart`.
Two options:
- **(A) Append to free ROM space** past the vanilla data and point a scene-table entry's vrom straight at
  it. Simplest; no dmadata churn. Must find a safe free region big enough for scene+room+collision+textures
  (the level can be large). PROBE: map the decompressed image's used range and the tail free space; confirm
  the cores don't bound-check vrom against dmadata.
- **(B) Add a dmadata file** (grow the file table) for the new scene/room, then reference by vrom. Cleaner
  (matches the engine's model) but requires rewriting `dmadata` and keeping vrom==prom on the decompressed
  image. PROBE: dmadata layout + how the MM/OoT loader resolves scene vrom (DMA vs direct).

Recommendation: start with **(A)** on the decompressed image (we already operate decompressed), reusing the
exporter's existing binaries; graduate to (B) only if the loader bounds-checks.

### 2b. Which scene-table slot
Rather than relocate/grow `gSceneTable` (SCENE_MAX is fixed; growing it means moving the array + fixing the
`SCENE_MAX` bound and every reference), **repurpose an UNUSED / safe slot**:
- MM: a test/unused scene id that's never reached in normal play (candidates: the `SCENE_TEST*` / unused
  slots — PROBE the scene table for entries with no real entrance). Keep Termina Field (0x2D) overwrite as
  the fallback mode.
- OoT: likewise an unused/debug slot.
Fill its entry: `sceneStart/sceneEnd` → our appended data (2a), `drawConfig` → MAT_ANIM if animated, and the
restriction/title fields → permissive. This is the **minimal-footprint** analogue of `DEFINE_SCENE(mh_append…)`.

### 2c. Entrance + warp + TIME (fixes night)
The injected level needs an **entrance** that sets scene + spawn + **time** without the vanilla override:
- Repurpose/append an entry in `gEntranceTable` → {our scene id, spawn index 0}. PROBE the entrance table
  location + format for MM and OoT (decompressed addresses).
- **Set time explicitly** like SoH/2Ship: at warp, write `gSaveContext.dayTime` (OoT) / `save.time` (MM) to
  the editor's `PlaytestTimeOfDay`, and clear any "fixed night" flag. On N64 this is done by the **PJ64
  fork boot hook** (it already pokes SaveContext for inventory/time — extend it to set dayTime for the new
  entrance), OR by patching the entrance's time field. PROBE: the gc-eu-mq-dbg SaveContext dayTime offset
  ([[megaton-hammer-pj64-savecontext-base]] has gSaveContext=0x8015E660 for the OoT debug ROM).

### 2d. Shaded lighting on N64 (fixes the lighting gap)
Make the N64 export honour `EditorSettings.LightingMethod`:
- **Method 2 (Shaded)**: the room DL already bakes per-face env shade in `DisplayListBuilder` (the same gate
  the OTR path uses) — ensure the N64 `RoomExporter`/`DisplayListBuilder` path runs that gate (it currently
  may fullbright), AND DROP the header light override (step 4's 200,200,200): instead emit the scene's env
  light settings (cmd 0x0F) from the editor's `SceneSettings` ambient/light/fog so the scene reads shaded
  exactly like SoH/2Ship.
- **Method 1 (Fullbright)**: keep the current bright-neutral behaviour.
PROBE: confirm `DisplayListBuilder.LightingMethod` is wired on the N64 path (it was added for export; verify
the N64 binaries pick it up) and that removing the 0x0F override doesn't darken to black (set real light
colours, not 0).

### 2e. Keep the door/cutscene suppression
Retain the header fixes that make a bare injected scene stable (0x0E→none, 0x1B→none) but now scoped to OUR
new scene's headers (vanilla scenes untouched).

---

## 3. Probing checklist (do before coding)
1. MM + OoT: dump `gSceneTable` (decompressed) → list unused/safe slots (no entrance points to them).
2. Locate `gEntranceTable` (decompressed) for MM + OoT; document format (scene id, spawn, flags incl. time).
3. Map the decompressed image's free tail space; pick an append region + size budget.
4. Verify the loader resolves scene vrom directly (no dmadata bound-check) on the decompressed image.
5. Confirm `DisplayListBuilder`/`RoomExporter` apply `LightingMethod` on the N64 path; check the 0x0F header
   light path for a shaded export.
6. gc-eu-mq-dbg SaveContext `dayTime` offset for the PJ64 boot-hook time set (OoT 0x8015E660 base known).
7. Headless boot the PJ64 fork on a new-scene ROM to confirm warp + render (the forks run headless).

## 4. Deliverables / modes
- `--injectmmscene` / N64 packer gains a **mode flag**: `overwrite` (current, default-safe) vs `newscene`.
- New scene id + entrance constants documented per game.
- Time + shaded-lighting applied in `newscene` mode (and back-ported to `overwrite` where feasible).

Forks/PJ64 backup: `forks/backups/pj64-megaton-fork.bundle` (full history) + `forks/pj64` source mirror +
the committed `project64-develop` working repo. Re-bundle after PJ64 changes.
