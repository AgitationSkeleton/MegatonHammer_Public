# First-run bootstrap & release pipeline

Goal: an end-user downloads **one file** — `MegatonHammer.exe` — and on first run the editor equips itself
(playtest engines) and (skippably) prompts for ROMs.

## First-run wizard (editor side)

On startup, if the wizard hasn't been completed/skipped and something is missing, `FirstRunWizard`
([Forms/FirstRunWizard.cs](../src/MegatonHammer/Forms/FirstRunWizard.cs)) appears
([hooked in Program.cs](../src/MegatonHammer/Program.cs), after the existing MD5 auto-detect):

- **Engines** — Ship of Harkinian (OoT), 2Ship (MM), Project64 (N64, optional). Each row shows
  Installed / Not installed. "Download & install missing" fetches our CI-built, already-patched binaries and
  installs them under a writable per-user folder (`%LocalAppData%\MegatonHammer\engines\<engine>`), then
  points `EditorSettings` at them. A **progress bar + status line + live log** track each download/extract.
- **ROMs** — three MD5-verified slots (`RomFingerprint`): OoT (USA v1.0), MM (USA), and the OoT MQ debug ROM
  for the vanilla-N64 path (optional). Browse or drag-drop; each shows ✓ recognized / ✗ mismatch. ROMs are
  never redistributed.
- **Skip for now** / **Done** both mark the wizard complete so it doesn't nag; re-open any time via
  **Options ▸ Asset auto-detection ▸ "Set up playtest engines…"**.

`EngineProvisioner` ([Editor/EngineProvisioner.cs](../src/MegatonHammer/Editor/EngineProvisioner.cs)) does the
work. Download source (overridable with `MH_ENGINE_BASEURL`):

```
https://github.com/AgitationSkeleton/MegatonHammer_Public/releases/latest/download/<asset>
   soh-win-x64.zip   2ship-win-x64.zip   pj64-win-x64.zip
```

**Dev fallback:** in a source checkout that has the fork submodules *and* a build toolchain
(`git` + `VCPKG_ROOT`), the wizard offers **build-from-source** instead (runs the existing
`apply-mh-patches.cmd` → `mh_configure` → `mh_build`). End-users without a toolchain always download.

The single-file exe is fully self-contained: the baked actor-schema data is **embedded** (`BakedSchemas`
reads the embedded copy first, then a `Data\` file on disk as a dev override), so no side files are needed.

## Release pipeline (CI)

Both live in [.github/workflows](../.github/workflows) and are mirrored to the **public** repo by
`sync-public.ps1` (the public repo's Releases are the end-user download source):

- **release-editor.yml** — `dotnet publish` a single self-contained win-x64 exe; on a `v*` tag, attaches
  `MegatonHammer.exe` to the GitHub Release. *(Validated locally — produces a ~71 MB standalone exe.)*
- **build-engines.yml** — SoH + 2Ship jobs **mirror the engines' own upstream Windows CI** (choco `ninja` →
  clone upstream at the pinned commit `948b84d8` / `3545e62e` with submodules → run `forks/apply-mh-patches.cmd`
  → `msvc-dev-cmd` → bootstrapped vcpkg → `cmake -G Ninja -DCMAKE_BUILD_TYPE=Release` → build →
  `GenerateSohOtr`/`Generate2ShipOtr` (confirmed target names; ROM-free engine assets) → **`cpack`**). No game
  ROM is used. The cpack zip is attached as `soh-win-x64.zip` / `2ship-win-x64.zip`. PJ64 overlays our delta →
  MSBuild. All three attach to the **same** tagged Release, so `releases/latest/download/<asset>` resolves.

> The SoH/2Ship recipe matches upstream's working CI, so the likely first-run tweaks are just cache keys and
> confirming the `.o2r` output path (`NOTE:` in the file). The **first run is slow** (cold vcpkg ~30–45 min).
> PJ64 (MSBuild overlay) is the least-validated job. release-editor.yml is validated locally.

### Game assets on a fresh machine
The CI engine bundle carries `soh.o2r`/`2ship.o2r` (the ROM-free *engine* assets) but not `oot.o2r`/`mm.o2r`
(the *game* assets). The editor already handles this: on the first playtest with no game archive, it offers to
launch the engine to import your ROM — and now **stages your MD5-verified ROM next to the engine first**, so
SoH/2Ship auto-extract it in one click instead of prompting. After that, playtests run normally.

### Cutting a release
1. `git tag v0.x && git push --tags` on the public repo (or run the workflows via *dispatch* first to iron
   out the engine build).
2. Both workflows attach their assets to the `v0.x` Release.
3. End-users download `MegatonHammer.exe` from that release; first run pulls the engine zips from it.
