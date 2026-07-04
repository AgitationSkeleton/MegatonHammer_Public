# Megaton Hammer fork audit & reintegration guide

Audit date: 2026-06-28. Compares the Megaton Hammer (MH) playtest forks against pristine upstream:

- **SoH** (`WorkFolders/MegatonHammer/SoH`) vs `READ_ONLY_SourceCodes/Shipwright-develop`
- **2Ship** (`WorkFolders/MegatonHammer/2Ship`) vs `READ_ONLY_SourceCodes/2ship2harkinian-develop`

Both forks' working trees are checked out at the **same upstream commit** as the pristine snapshots
(verified byte-identical for several files), and all three submodules in each fork are at the exact
commit the parent records (no submodule drifted). So every divergence is an uncommitted **working-tree
edit**, fully captured here.

## How to read this

- **Real change** = a content difference. **CRLF noise** = the whole file re-written LF↔CRLF with *zero*
  content change (the dominant "modification" in the submodules — see §4).
- **Drift?** answers your question: *does this change benefit the MH playtest-launch mechanism, or is it
  unrelated drift that could be reverted with no loss to playtesting?*
  - **CORE** = required for playtest launch/render; reverting it removes the feature.
  - **CRASH-GUARD** = required so an editor scene can't crash the engine.
  - **BUILD** = needed only to *build* the fork (not at playtest runtime).
  - **DRIFT** = not needed for playtest launch; safe to revert (with the noted caveat).
  - **DEBUG** = diagnostic logging embedded inside a CORE file; strippable, no playtest benefit.
- **Reintegration method**: every file's *original* version is its committed HEAD (= upstream), so the
  universal command to restore upstream is:

  ```sh
  "C:/Program Files/Git/cmd/git.exe" -C <repo> checkout -- <path>
  ```

  For a CORE file this *disables* that part of playtest; for a DRIFT file it's a clean revert. For DEBUG
  blocks that live *inside* a CORE file, you can't `checkout` the file (it would also drop the CORE hook)
  — strip the marked block by hand and regenerate the patch (see §5).

---

## 1. SoH (Ship of Harkinian / OoT) — 9 real changes (447 other "changes" = CRLF noise)

| # | File (relative to `SoH/`) | What changed | Why | Drift? | Reintegrate |
|---|---|---|---|---|---|
| 1 | `soh/include/tables/scene_table.h` | +1 line: reserves `SCENE_MH_APPEND` (0x6E) | The editor warps playtest scenes into a reserved slot so no vanilla scene is repurposed | **CORE** | `git -C SoH checkout -- soh/include/tables/scene_table.h` (loses the playtest scene slot) |
| 2 | `soh/include/tables/entrance_table.h` | +4 lines: `ENTR_MH_APPEND_0..3` (group-of-4) | The warp targets a reserved entrance; OoT requires entrances in groups of 4 | **CORE** | `git -C SoH checkout -- soh/include/tables/entrance_table.h` |
| 3 | `soh/soh/Enhancements/debugconsole.cpp` | +374 lines: `mh_playtest` warp command, auto-boot hook (`MhPlaytestBootHook`), custom-inventory apply (incl. the new magic tier), authored-dialogue message bank, **and** diagnostic probes (`MhBootLog`, skybox/player `MhPlaytestRenderProbe`) | The whole SoH-side launch + inventory + dialogue mechanism the editor drives via the `mh/info` payload | **CORE** (+ DEBUG: the `MhBootLog`/render-probe logging) | Whole file: `git -C SoH checkout -- soh/soh/Enhancements/debugconsole.cpp` (loses playtest). DEBUG only: strip the `MhBootLog`/`MhPlaytestRenderProbe` `[mh_diag]` writes (see §5). |
| 4 | `soh/soh/util.cpp` | +23 lines: STL shims `__std_find_not_ch_1` / `__std_find_last_not_ch_pos_1` | This toolchain's linked STL lib is older than its `cl.exe`, so `find_first/last_not_of` emit char-search intrinsics the lib doesn't export → LNK2001. The shim resolves the link. | **BUILD** | `git -C SoH checkout -- soh/soh/util.cpp` — **but the SoH build then fails to link** on this machine unless the toolchain is fixed. Keep unless your toolchain matches. |
| 5 | `libultraship/src/fast/interpreter.cpp` | +25/−3: `gfx_set_timg_otr_hash_handler_custom` `cmd0` advance fix **and** `MhTexDiag` debug logging | The fix stops a one-word DL-stream desync when an OTR-hash texture/​palette misses (was crashing a later opcode on a garbage pointer). Verified **behavior-neutral on the success path**. The `MhTexDiag` block logs only genuine hash misses (de-duped) to `mh_tex_diag.log`. | **CORE** (the fix) + **DEBUG** (`MhTexDiag`) | Whole file revert removes the desync fix (don't): `git -C SoH/libultraship checkout -- src/fast/interpreter.cpp`. DEBUG only: delete the `MhTexDiag` helper + its two call sites (see §5). |
| 6 | `libultraship/cmake/dependencies/git-patch.cmake` | +2/−2: adds `--ignore-whitespace` to the `git apply` calls | Lets FetchContent dependency patches apply cleanly over the CRLF-converted submodule trees | **DRIFT** (build-only) | `git -C SoH/libultraship checkout -- cmake/dependencies/git-patch.cmake` — **safe to revert IF you also normalize the CRLF noise (§4)**; otherwise dep patches may fail to apply on the CRLF tree. |
| 7 | `OTRExporter/OTRExporter/Main.cpp` | +5/−1: skip reading a ROM when the baserom path is a **directory** | Part of an MH directory-based asset-extraction pipeline (extract from a folder of files instead of a `.z64`), used by `mh_build.cmd` | **DRIFT** (build/tooling — no playtest-launch benefit) | `git -C SoH/OTRExporter checkout -- OTRExporter/Main.cpp` — safe to revert **only if `mh_build.cmd` does not feed a directory baserom**; else SoH asset extraction breaks at build time. |
| 8 | `ZAPDTR/ZAPD/Globals.cpp` | +3: `GetBaseromFile` reads from a disk directory when baseRomPath is a directory | Same directory-extraction pipeline as #7 | **DRIFT** (build/tooling) | `git -C SoH/ZAPDTR checkout -- ZAPD/Globals.cpp` — same caveat as #7 |
| 9 | `ZAPDTR/ZAPD/Main.cpp` | +30/−1: directory-extract code path (`ExtractDirectory` when baseRomPath isn't a directory; list/extract files from a folder) | Same directory-extraction pipeline as #7 | **DRIFT** (build/tooling) | `git -C SoH/ZAPDTR checkout -- ZAPD/Main.cpp` — same caveat as #7 |

**Not in any saved patch:** #6, #7, #8, #9 are real edits that the `forks/patches/` set does **not** capture
(only #1–#5 are in `soh-mh_playtest.patch` / `soh-buildfix.patch` / `soh-libultraship.patch`). They're
intentional and guarded (`Directory::Exists(...)`), so they don't change ROM-based behavior — but a clean
re-apply of the patches would **not** reproduce the tree. See §6.

---

## 2. 2Ship (2 Ship 2 Harkinian / MM) — 10 real changes (513 other "changes" = CRLF noise)

| # | File (relative to `2Ship/`) | What changed | Why | Drift? | Reintegrate |
|---|---|---|---|---|---|
| 1 | `mm/2s2h/DeveloperTools/DebugConsole.cpp` | +487 lines: auto-boot hook, `mh_playtest` warp, custom-inventory apply (incl. magic tier), chest-flag clear loop, message bank, NPC-schedule VM, **and** frame-30 diagnostics (actor-id dump, En_Box count, A-button gate state, loaded-object slots) | The MM-side launch + inventory + chest + schedule mechanism | **CORE** (+ DEBUG: the frame-30 `[mh_diag]` logs) | Whole file: `git -C 2Ship checkout -- mm/2s2h/DeveloperTools/DebugConsole.cpp` (loses playtest). DEBUG only: strip the `[mh_diag]` blocks in `MhPlaytestRenderProbe` (see §5). |
| 2 | `mm/2s2h/z_play_2SH.cpp` | +41 lines: `CutsceneManager_Init(play,NULL,0)` pre-init; HUD-force + cutscene-stop for `SCENE_MH_APPEND`; per-room actor/En_Box/bg diagnostic (`MhSceneLog`) | Makes the bare playtest scene show the full HUD; pre-inits the cutscene manager so a scene without an actor-cutscene list can't null-deref | **CORE** (+ DEBUG: `MhSceneLog`) | Whole file: `git -C 2Ship checkout -- mm/2s2h/z_play_2SH.cpp`. DEBUG only: strip the `MhSceneLog` helper + its call (see §5). |
| 3 | `mm/src/code/z_eventmgr.c` | +15/−1: `CutsceneManager_GetCutsceneEntryImpl` returns a static inert entry (hudVisibility=ALL) on NULL/out-of-range; `CutsceneManager_StoreCamera` null-guard | A custom scene may carry no/short actor-cutscene list; indexing it unchecked crashed player init | **CRASH-GUARD** | `git -C 2Ship checkout -- mm/src/code/z_eventmgr.c` — **don't**: editor scenes can crash on boot without it. (Vanilla scenes hit the unchanged path, so it's behavior-neutral for them.) |
| 4 | `mm/src/code/z_scene_table.c` | +11: `SCENE_MH_APPEND` entrance entry + `sSceneEntranceTable` slot 0x6E | Routes the warp's entrance to the reserved scene | **CORE** | `git -C 2Ship checkout -- mm/src/code/z_scene_table.c` |
| 5 | `mm/include/z64scene.h` | +4/−1: `ENTR_SCENE_MH_APPEND`, `ENTR_SCENE_MAX` 0x6E→0x6F | Reserves the entrance enum (grows the table; repurposes nothing) | **CORE** | `git -C 2Ship checkout -- mm/include/z64scene.h` |
| 6 | `mm/include/tables/scene_table.h` | +4: `DEFINE_SCENE(mh_append, SCENE_MH_APPEND, …)` slot 0x71 | Reserves the scene slot | **CORE** | `git -C 2Ship checkout -- mm/include/tables/scene_table.h` |
| 7 | `mm/2s2h/DeveloperTools/BetterMapSelect.c` | +2/−2: array sizes 106→107, 102→103 | Sizes the dev map-select arrays for the +1 reserved scene (no overflow) | **CORE** (supports the reserved scene) | `git -C 2Ship checkout -- mm/2s2h/DeveloperTools/BetterMapSelect.c` |
| 8 | `libultraship/src/fast/interpreter.cpp` | +10/−3: the same `gfx_set_timg_otr_hash_handler_custom` `cmd0` DL-desync fix as SoH (no `MhTexDiag` here) | Same OTR-hash texture-load correctness fix | **CORE** | `git -C 2Ship/libultraship checkout -- src/fast/interpreter.cpp` (don't — removes the fix) |
| 9 | `libultraship/src/ship/window/gui/Gui.cpp` | +7: `Gui::LoadGuiTexture` null-resource guard | Skips a missing GUI texture instead of dereferencing null at boot | **CRASH-GUARD** | `git -C 2Ship/libultraship checkout -- src/ship/window/gui/Gui.cpp` (don't — can crash boot) |
| 10 | `libultraship/cmake/dependencies/git-patch.cmake` | +1/−2: adds `--ignore-whitespace` to `git apply` (and reverse-check) | CRLF-tolerant dependency patching, like SoH #6 | **DRIFT** (build-only) | `git -C 2Ship/libultraship checkout -- cmake/dependencies/git-patch.cmake` — safe to revert IF you also normalize CRLF (§4) |

**Not in any saved patch:** #10 only (2Ship's OTRExporter/ZAPDTR have **zero** real changes — unlike SoH).
All of #1–#9 match `2ship-mh_playtest.patch` / `2ship-libultraship.patch` byte-for-byte.

---

## 3. Per-fork verdict

Both forks are **clean**: every real source change is either a deliberate MH playtest hook, a crash-guard,
the build shim, the OTR-hash render fix, embedded diagnostics, or build-tooling drift. **No accidental
edits, no rendering-pipeline corruption, and the OTR-hash fix is verified behavior-neutral on the success
path.** Submodules are perfectly aligned.

The only changes that are genuine **drift with no benefit to the playtest *launch* mechanism**:

- **CRLF/line-ending noise** (§4) — pure churn, revert freely.
- **`git-patch.cmake` `--ignore-whitespace`** (SoH #6, 2Ship #10) — build-only; exists *because* of the CRLF
  noise. Revert together with §4.
- **SoH OTRExporter/ZAPDTR directory-extraction** (SoH #7/#8/#9) — *build-time* asset extraction, unrelated
  to launching a playtest. Revert only if `mh_build.cmd` doesn't rely on directory baseroms.
- **Embedded diagnostics** (DEBUG rows) — strippable; see §5.

Everything else (CORE / CRASH-GUARD / BUILD) is required and must stay for playtest to work.

---

## 4. CRLF / line-ending noise (the bulk of the "modified" files)

These are files git lists as modified but whose entire diff is LF↔CRLF with **zero content change**:

| Tree | "Modified" | Real | Pure CRLF noise |
|---|---|---|---|
| SoH `libultraship` | 191 | 2 | 189 |
| SoH `OTRExporter` | 118 | 1 | 117 |
| SoH `ZAPDTR` | 143 | 2 | 141 |
| 2Ship `libultraship` | 288 | 3 | 285 |
| 2Ship `OTRExporter` | 5 | 0 | 5 |
| 2Ship `ZAPDTR` | 220 | 0 | 220 |

No behavioral effect whatsoever. To revert **only** the noise (leaving the real changes intact), in each
submodule run:

```sh
GIT="C:/Program Files/Git/cmd/git.exe"
"$GIT" -C <submodule> diff --ignore-all-space --numstat \
  | awk '($1+$2)==0 {print $3}' \
  | xargs -r "$GIT" -C <submodule> checkout --
```

(That lists every file whose change vanishes under `--ignore-all-space` — i.e. the CRLF-only files — and
restores them.) A durable fix is to add a `.gitattributes` (`* text=auto eol=lf`) and renormalize so the
noise doesn't recur on checkout.

---

## 5. Stripping the embedded diagnostics (DEBUG rows)

These were added to pin down this session's bugs and aren't part of the launch feature. They're inside CORE
files, so revert them by hand (then regenerate the patch, §6), not by `git checkout`:

- **SoH `interpreter.cpp`** — the `static void MhTexDiag(...)` helper (just after `namespace Fast {`) and its
  two call sites in `gfx_set_timg_otr_hash_handler_custom` (the `MISS-NAME` / `MISS-LOAD` writes).
- **SoH `debugconsole.cpp`** — the `[mh_diag] skyboxId=… player=… equip=…` block in `MhPlaytestRenderProbe`.
- **2Ship `DebugConsole.cpp`** — in `MhPlaytestRenderProbe`: the `actors total=… En_Box=… ids=[…]`, the
  `[mh_diag] A-button: …`, and the `[mh_diag] objects: …` blocks.
- **2Ship `z_play_2SH.cpp`** — the `static void MhSceneLog(...)` helper (+`#include <fstream>`) and its
  `[mh_diag] room=… spawned actors…` call in `OTRfunc_800973FC`.

All are de-duped or once-only and gated to `SCENE_MH_APPEND`, so they don't flood or slow anything — purely
a cleanliness call. (The `MhBootLog` boot/warp trace is more useful than noisy; keep or gate behind a cvar.)

---

## 6. Recommendations (cosmetic, none blocking)

1. **Normalize CRLF** in all six submodules (`.gitattributes` + renormalize) to erase the ~960 noise files;
   then the `git-patch.cmake` `--ignore-whitespace` tweaks (SoH #6, 2Ship #10) become unnecessary and can be
   reverted.
2. **Update the saved patch set** so the fork is fully reproducible from `forks/apply-mh-patches.cmd`:
   regenerate patches that capture SoH's four undocumented changes (#6 `git-patch.cmake`, #7 `OTRExporter
   Main.cpp`, #8/#9 `ZAPDTR Globals.cpp`/`Main.cpp`). 2Ship needs only its `git-patch.cmake` folded in.
3. **Strip or cvar-gate** the §5 diagnostics before any release build.

Reproducibility commands for the maintainers (regenerate a patch after editing a fork file):

```sh
GIT="C:/Program Files/Git/cmd/git.exe"
# e.g. refresh the SoH libultraship patch from the current tree:
"$GIT" -C SoH/libultraship diff -- src/fast/interpreter.cpp > forks/patches/soh-libultraship.patch
```
