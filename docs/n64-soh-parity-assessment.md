# N64 ↔ SoH/2Ship export parity — assessment & careful plan (2026-07-02)

Supersedes the planning half of `n64-new-scene-injection-plan.md` (that doc predates the current
`RomBuilder`/`RomInjector` append machinery). Goal: make the **N64 ROM** export/playtest reach
parity with the **SoH/2Ship** path, which adds a level as a **new scene that leaves every vanilla
scene intact** — without the N64 procedure ever corrupting a base cartridge image.

---

## 0. TL;DR assessment

The append **machinery already exists and works** (decompress → append files → rebuild dmadata →
fix CIC-6105 CRC → pad; plus a debug-ROM free-space variant). The gap is **not** "can we add data"
— it's **"where does the new scene register, and how is it reached, without sacrificing a real
vanilla scene."** SoH/2Ship *grow* `scene_table.h`/`entrance_table.h` (new `SCENE_MH_APPEND`). A
binary ROM can't grow those fixed arrays cheaply, so the N64 analogue is **repurpose a guaranteed-
unused slot + its unused entrance**. OoT already does exactly this (SCENE_TEST01); **MM does not —
it overwrites Termina Field 0x2D, destroying a real scene.** Closing parity is four bounded deltas,
none of which require relocating a ROM table.

---

## 1. Reference model — how SoH/2Ship "append" (the target)

| | OoT / SoH | MM / 2Ship |
|---|---|---|
| New scene id | `SCENE_MH_APPEND` = **0x6E** | `SCENE_MH_APPEND` = **0x71** |
| Registration | `DEFINE_SCENE(mh_append_scene, none, SCENE_MH_APPEND, SDC_DEFAULT,…)` grows `SCENE_ID_MAX` | `DEFINE_SCENE(mh_append, …, SCENE_DRAW_CFG_MAT_ANIM)` grows `SCENE_MAX` + `ENTR_SCENE_MH_APPEND` (0x6E) grows `ENTR_SCENE_MAX` |
| Resource path | `scenes/shared/mh_append_scene/mh_append_scene` | `scenes/nonmq/mh_append/mh_append` |
| Entrance | **4** consecutive entries `ENTR_MH_APPEND_0..0_3` (0x614–0x617) — the engine reads `gEntranceTable[idx + sceneLayer]` for child/adult × day/night, so 4 slots avoid reading past the table | **1** packed entrance `ENTR_SCENE_MH_APPEND << 9` |
| Reached by | boot hook (`debugconsole.cpp:MhPlaytestBootHook`) reads `mh/info` JSON → sets save/time/age/inventory → warps to `ENTR_MH_APPEND_0` | same idiom (`DebugConsole.cpp`), `MapSelect_LoadGame(ENTRANCE(MH_APPEND,0))` |
| Vanilla scenes | **all intact** (new id appended past the last vanilla) | **all intact** |

Editor side is already wired for this: `PlaytestConfig.Append`, `O2RPacker` picks the
`mh_append` resource path when `Append`, and `mh/info` carries `append:true`, `timeOfDay`,
`inv`, `music`, etc. **This half needs no work.**

---

## 2. Current N64 reality (what actually ships)

The append **data** plumbing is mature and CRC-correct:
- `RomBuilder.Decompress` → flat image, dmadata rewritten 1:1.
- `RomBuilder.AppendFile` (retail) grows the image + registers a new dmadata entry (needs a spare
  16-byte slot; `SpareDmaSlots`). `RomBuilder.WriteFileAt` (gc-eu-mq-dbg **debug** ROM) writes into
  free space with **no** dmadata entry — the debug `DmaMgr_Init` walks a fixed-size filename array
  in lockstep, so growing dmadata crashes boot; the uncompressed ROM's arbitrary-DMA path
  (`z_std_dma.c` `!sDmaMgrIsRomCompressed`) reaches the out-of-table files via VROM pointers.
- `OotCrc.Update` fixes the CIC-6105 checksum over `0x1000..0x101000`; `PadToPow2` rounds to 64 MB.

**But the injector always REPOINTS/overwrites an existing scene-table slot** and ignores the
`Append` flag:

| Path | Target slot | Verdict |
|---|---|---|
| OoT debug (`RomInjector.InjectDebug`) | **SCENE_TEST01 0x65** + entrance `ENTR_TEST01_0` 0x0094 | ✅ already an unused dev slot — no real scene lost |
| OoT retail (`RomInjector.Inject`) | caller's `targetSceneId` (PlaytestPack default 0x52) | ⚠ 0x52 is a real scene |
| MM retail (`MmInjectScene`, gSceneTable @ 0xC5A1E0) | **Termina Field 0x2D** | ❌ destroys a real, central scene |
| MM debug | Kakusiana grotto 0x07 | ⚠ real (small) scene |

Entrance handling: **gEntranceTable is never patched.** N64 reaches the scene by a hand-assembled
MIPS boot detour that writes `gSaveContext.save.entrance` directly (`MmInjectScene.BakeAutoBoot`/
`BakePlayInitMenuFix`) or by PJ64-fork params (`mh_n64_playtest.txt`: `entrance=/scene=/timeOfDay=`).
This is actually *cleaner* than SoH's table-grow — the detour bypasses the entrance system — **but it
currently rides a real scene's entrance.**

Two remaining functional gaps vs SoH/2Ship (from the old doc, still true):
- **Time-of-day**: injected scene inherits the host entrance's time (MM Termina Field → night). The
  PJ64 OoT fork already pokes `dayTime`; MM/other paths don't set it.
- **Shaded lighting**: `DisplayListBuilder` has a `lighting` param but does `_ = lighting;` — N64
  rooms render fullbright regardless of `EditorSettings.LightingMethod` (default 2 = Shaded). SoH
  bakes per-face env shade. So N64 looks flat vs SoH/2Ship.

---

## 3. The parity plan — four bounded deltas (no ROM-table relocation)

The N64 "append" = **repurpose a guaranteed-unused slot + its unused entrance**, keeping every real
vanilla scene reachable. This is the safe analogue of `SCENE_MH_APPEND`; growing `SCENE_MAX`/
`ENTR_MAX` in a symbol-less binary (relocate the array, patch the bound + every reference) is high
risk for no gameplay benefit and is explicitly **out of scope** unless a probe proves a slot can't
be found.

### Δ1 — Target an unused scene slot (the core fix)
- **OoT**: already SCENE_TEST01 0x65 (debug) — formalize it as the append target for retail too
  (PlaytestPack passes 0x65, not 0x52, when `Append`). SCENE_TEST01 has a real title/entrance and is
  never reached in normal play.
- **MM**: **stop targeting Termina Field 0x2D.** Probe `gSceneTable` for an UNSET/unused slot (the
  MM table has `{0,0}` / NULL entries — `SceneTableLocator`/`MmSceneFiles` already know which). Pick
  one with a small/no vanilla footprint and repoint IT. Fall back to a `SCENE_TEST*` slot. Keep
  Termina-Field overwrite behind an explicit "compat" flag only.
- **Wire `PlaytestConfig.Append` into the N64 injectors** (they currently ignore it): `Append` →
  unused-slot target; `!Append` → legacy overwrite (kept as the fallback the old doc mandates).

### Δ2 — Entrance parity (reach it without clobbering a vanilla entrance)
- Keep the **boot-detour / fork-param** approach (no gEntranceTable growth). Point it at the
  **unused slot's own vanilla entrance** (OoT SCENE_TEST01 → `ENTR_TEST01_0` 0x0094 already works;
  find the MM unused slot's entrance encoding, or set `save.entrance` to a value whose
  `gEntranceTable[idx]` already resolves to the chosen slot).
- If the chosen unused slot has **no** entrance pointing at it, repoint a **single unused
  entrance-table entry** in place (not a grow) → {our scene, spawn 0}. Probe the entrance table
  location/format first (OoT linear `EntranceInfo`; MM packed `(sceneId<<9)|(spawn<<4)`).

### Δ3 — Time-of-day (fixes night)
- Set `gSaveContext…dayTime` (OoT) / `Save…time` (MM) explicitly at boot to `mh/info.timeOfDay`,
  exactly like SoH's boot hook. Offsets are already documented in `n64-playtest-parity-plan.md`
  (OoT dbg `dayTime` @ save+0x0C; MM retail SaveContext). OoT PJ64 fork already does this — extend
  the MM detour + PJ64 MM path to write time and clear any fixed-night flag.

### Δ4 — Shaded lighting — ALREADY DONE (verified 2026-07-02)
`DisplayListBuilder` (N64) line ~107 already bakes `lighting.BakedShade(face.Normal)` under
`LightingMethod >= 2`, the **same** `SceneSettings.BakedShade` (ambient + 2 directional lights) the
OTR path bakes in `OtrRoomGeometry`. `SceneExporter` passes `n64Hw ? scene.Settings : null`, so the
N64 room geometry is shaded at parity with SoH/2Ship. The old "fullbright" claim was stale (a
misleading `_ = lighting;` no-op line, now removed). **Remaining nuance (minor, optional):** the
`MmInjectScene` scene-header 0x0F override sets a bright-neutral env light — that affects *actor*/
dynamic lighting, not the baked room verts, so it doesn't flatten the room shade; revisit only if
actors look washed. Room shading itself needs no work.

---

## 4. "Careful procedure" — safety rails for the ROM patching

This is where the *care* the task calls for concentrates. Every N64 write path must:
1. **Operate on a decompressed copy**, never the user's base ROM; write output to a new file. The
   editor already loads a base and builds a fresh image — confirm no in-place base mutation.
2. **Verify the target slot is genuinely unused before repointing** — dump the entry, confirm no
   live entrance references it (scan gEntranceTable), and log the old value so the write is
   reversible/auditable. Refuse (don't guess) if the fingerprint locate fails.
3. **Respect dmadata capacity** — retail `AppendFile` throws when `SpareDmaSlots < needed`; surface
   that as a clean editor error, not a crash. Debug path must use `WriteFileAt` (no dmadata) — never
   grow dmadata on gc-eu-mq-dbg.
4. **Always re-run `OotCrc.Update` last**, after padding, over the correct range; a wrong CRC = a
   ROM that some cores silently reject or others boot then fault.
5. **Keep VROM == PROM** on the decompressed image (uncompressed: romStart=vromStart, romEnd=0) so
   the loader's direct-DMA math holds.
6. **Headless-boot the PJ64 fork** on each new-scene ROM to capture warp+render diagnostics before
   asking the user to test (the forks run headless — see the fork-audit note).
7. **Back up** the PJ64 fork bundle after any fork-hook change; keep the overwrite mode as the
   documented fallback.

---

## 5. Probing checklist (do first, no code changes)
1. Dump MM `gSceneTable` (decompressed) → list `{0,0}`/UNSET slots; pick the MH-append target +
   confirm nothing warps to it. Do the same for OoT (confirm SCENE_TEST01 is safe).
2. Locate + document `gEntranceTable` for OoT (linear) and MM (packed) on the decompressed image;
   find the target slot's existing entrance, or a spare entry to repoint.
3. Confirm the loader resolves scene VROM directly (no dmadata bound-check) on the decompressed
   image for both the retail (dmadata-appended) and debug (free-space) variants.
4. Confirm `DisplayListBuilder`/`RoomExporter` receive `SceneSettings` on the N64 path (they do —
   `SceneExporter` passes `n64Hw ? scene.Settings : null`) and wire `LightingMethod` through.
5. gc-eu-mq-dbg + MM-retail `dayTime`/`time` write offsets for the boot-hook time set (documented).

## 5b. Probe results (2026-07-02, `--n64probe`, read-only)

Ground truth from the actual ROMs — **two locator bugs found, both critical to get right before patching:**

- **MM real gSceneTable = 0xC5A1E0** (the constant `MmInjectScene` already hardcodes). Confirmed:
  its empty slots (start==end==0) are exactly `0x01-0x06, 0x09, 0x0E, 0x0F, 0x31, 0x3A` — matching
  the decomp `DEFINE_SCENE_UNSET` set — and Termina 0x2D / Clock Town 0x6C entries are plausibly
  sized. **`SceneTableLocator.FindMM` is WRONG on retail MM** — it returns 0x1F820 (a false-positive
  table: Termina reads 21 KB not 231 KB, no empty slots). `RomInjector.InjectDebug` uses FindMM for
  MM, so that path repoints the wrong table — **must switch MM to the 0xC5A1E0 anchor (or fix FindMM)**.
- **OoT retail scene table is NOT reliably located by `Find`** either (slot 0x65 reads garbage), BUT
  the OoT injection target is the **gc-eu-mq-dbg debug ROM**, where `Find` → **0xBA0BB0** works and
  **SCENE_TEST01 (0x65)** is a real 16 KB throwaway test scene reached by **ENTR_TEST01_0 (0x0094)**.
  So OoT is already effectively "append": it repoints a disposable test slot, not real content. ✅
- Both ROMs report **16 spare dmadata slots** (ample for scene + rooms + title card) and the free tail
  begins at end-of-files (append grows the image, then PadToPow2 → 64 MB).

**Consequences for the plan:**
- **OoT Δ1 is essentially done** (SCENE_TEST01). Action: make the retail `Inject` path also target 0x65
  under `Append` (PlaytestPack currently passes 0x52 for retail); the debug path already uses 0x65.
- **MM is the real work:** anchor on 0xC5A1E0 (not FindMM), and pick the append target. Empty slots
  (0x0E/0x0F) destroy no scene but have **no entrance**.

**MM has NO disposable test scene** (unlike OoT). The `*TEST`-named slots are real, used scenes in
2Ship: Z2_DANPEI2TEST 0x30 = "Beneath Graveyard and Dampe's House", Z2_TOUGITES 0x51 = "Ghost Hut",
Z2_LABO 0x2F = "Marine Research Lab". Overwriting any of them destroys real content. So the OoT
"overwrite a throwaway test slot" trick has no MM equivalent — the empty slots (0x0E/0x0F) are the
only zero-damage option, and they need an entrance.

**Refined MM design — the entrance REDIRECT (preserves ALL vanilla scene data):**
1. Append/write the level's scene+room data (existing `AppendFile`/`WriteFileAt`).
2. Repoint an **empty** scene-table slot (0x0E) at 0xC5A1E0+0x0E*0x10 → our data. **Termina 0x2D and
   every other real scene stay byte-for-byte intact.**
3. **Redirect one entrance to load slot 0x0E instead of patching a scene:** locate the
   `EntranceTableEntry` that Termina's boot entrance (0x5400) resolves to (`Entrance_GetSceneIdAbsolute`
   → `sSceneEntranceTable[entrance>>9]` → an `EntranceTableEntry` list; the entry carries the sceneId),
   and change its `sceneId` field 0x2D→0x0E. Now booting entrance 0x5400 loads our appended slot 0x0E;
   Termina Field the *scene* is untouched (still reachable by its other entrances / data preserved).
4. Boot detour sets `save.entrance = 0x5400` and `dayTime` (Δ3) as today.
This is strictly better than the current Termina-slot overwrite: it changes ONE entrance entry's target
instead of destroying 231 KB of Termina scene data.

**MM entrance mechanism (decoded from 2ship z_scene_table.c — the exact byte to patch):**
```c
EntranceTableEntry* Entrance_GetTableEntry(u16 entrance) {
    return &sSceneEntranceTable[entrance >> 9].table[(entrance >> 4) & 0x1F][entrance & 0xF];
}
// EntranceTableEntry { /*0x0*/ s8 sceneId; /*0x1*/ s8 spawnNum; /*0x2*/ u16 flags; } size 0x4
// SceneEntranceTableEntry { /*0x0*/ u8 tableCount; /*0x4*/ EntranceTableEntry** table; /*0x8*/ char* name; } size 0xC
```
Termina boot entrance **0x5400**: `>>9 = 0x2A` (entranceScene), `(>>4)&0x1F = 0` (spawn), `&0xF = 0`
(variant). So the target is `sSceneEntranceTable[0x2A].table[0][0]`, whose **sceneId (offset +0) = 0x2D**.
The redirect = write **0x0E** into that one byte. Pointer chase to reach it in the decompressed ROM:
1. Locate `sSceneEntranceTable` VROM (it lives in `code`; fingerprint = an array of 0xC-byte entries
   whose `table` field is a plausible `0x80xxxxxx` RAM ptr — or derive from a known retail address).
2. Entry 0x2A → read its `table` ptr (RAM) → convert RAM→VROM via the `code` file's VRAM base/VROM start.
3. `table[0]` → read ptr → `EntranceTableEntry` array VROM → `[0]` → sceneId byte at +0. Verify it reads
   **0x2D** before writing 0x0E (a read-only validation gates the write).

**Next pass (delicate, do with per-hop verification):** implement `MmEntranceLocator` (read-only find +
validate the chain lands on sceneId 0x2D), then the append-mode MM injector: repoint empty slot 0x0E +
redirect this one entrance byte. Keep Termina-overwrite as the `!Append` fallback.

**LOCATED + VALIDATED 2026-07-02 (`--n64probe`, read-only):** on retail MM (decompressed):
- `sSceneEntranceTable` @ **VROM 0xC5BC60** (RAM 0x801C5720), just past gSceneTable (0xC5A1E0).
- The Termina 0x5400 chase `[0x2A].table[0][0].sceneId` resolves to **0x2D — VALIDATED**.
- **The single redirect-patch byte is at VROM 0xC5AE84** (currently 0x2D). Append-mode write: set it to
  **0x0E** so entrance 0x5400 loads the appended empty slot 0x0E; Termina's scene data stays intact.
- MM code RAM->VROM = `RAM - 0x7F569AC0` (verified via Play_Init + gSceneTable). The locator scans near
  gSceneTable and accepts the UNIQUE base whose Termina chase yields 0x2D (no fingerprint guesswork).

**WRITE = IMPLEMENTED + data-verified 2026-07-02** (`MmInjectScene.InjectSceneAppend`, `Rom/
MmEntranceLocator.cs`; `--injectmmscene <rom> <out> append`). The append injector:
1. Locates + VALIDATES the entrance redirect byte (`MmEntranceLocator.Locate`) and refuses if the
   Termina chase != 0x2D — never patches blind.
2. Clones Termina slot 0x2D's scene + room0 binaries to the free tail (`AppendCopy`; uncompressed MM
   permits arbitrary DMA to the appended VROM).
3. Points spare slot 0x0E's gSceneTable entry at the clone (Termina 0x2D entry untouched) + draw config.
4. Runs the SAME proven `PatchAllHeaders` on the clone (room list, collision, spawn, cutscene-suppress,
   env light).
5. Flips the one entrance byte 0x2D->0x0E so boot entrance 0x5400 loads slot 0x0E.
Verified invariants on retail MM: **Termina 0x2D scene entry + bytes preserved**, slot 0x0E populated,
entrance byte redirected to 0x0E; overwrite mode (default) unchanged. Editor wiring: the MM playtest
path passes `PlaytestConfig.Append` -> append mode. **Runtime boot/render is the user's PJ64 test**
(N64 render verify has always been user-side); dayTime + shaded-light are the follow-ups if needed.

## 6. Sequencing (each independently testable, overwrite mode stays the safe default)
1. **Wire `Append` into the injectors + MM unused-slot target** (Δ1/Δ2) — biggest parity win,
   stops MM clobbering Termina Field. Probe-gated.
2. **Time-of-day set** (Δ3) — small, reuses documented offsets + existing PJ64 poke idiom.
3. **Shaded lighting** (Δ4) — shared `DisplayListBuilder` change; verify against a SoH render.
4. **Optional**: single-entry entrance-table repoint if a chosen unused slot lacks an entrance.

Deliverable modes: N64 packer gains `overwrite` (current default-safe) vs `append` (new-slot) —
matching the `Append` flag the OTR path already uses, so one editor toggle drives both engines.
