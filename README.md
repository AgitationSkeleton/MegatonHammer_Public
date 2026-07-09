# Megaton Hammer

A Valve-Hammer-style **level editor for Zelda 64** — *Ocarina of Time* and
*Majora's Mask*. Build rooms from brushes, place and wire actors, paint textures
ripped from your own ROM, and **playtest** the result in one click on a real engine
(Ship of Harkinian for OoT, 2Ship2Harkinian for MM, or Project64 on vanilla N64).

> You must supply your own legally-owned OoT / MM ROM. No game data ships here.

## What it does

- **Brush geometry & collision** — block/wedge/cylinder/spike/sphere shapes, clipping,
  vertex editing, per-face texture mapping with a Hammer-like toolset.
- **Actors** — a curated, decomp-verified parameter schema with a *simplified* mode
  (meaningful fields only) and an advanced mode; the editor auto-fills sane defaults
  and auto-allocates logic flags so simple levels "just work".
- **Dungeon Mechanisms** — one-click, pre-wired puzzle presets (switch → gate, torch →
  gate, silver-rupee room, MM switch → ladder) built from real vanilla actors + the
  flag bus, so authored levels run on unmodified games.
- **Textures** — extract and paint textures directly from a supplied ROM.
- **Playtest** — packs the scene as a native resource and warps a customized SoH /
  2Ship / PJ64 build straight into your level.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build-megaton.ps1            # editor + forks
powershell -ExecutionPolicy Bypass -File .\build-megaton.ps1 -EditorOnly # just the editor (fast)
```

**Prerequisites:** Git, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
(editor); Visual Studio 2022 with the *Desktop development with C++* workload + CMake
(the playtest forks).

The script builds the editor from `src/`, then — for the forks — clones **SoH / 2Ship
from HarbourMasters upstream** (and Project64 from its upstream) at pinned commits and
applies Megaton Hammer's patches from `forks/`. It never depends on any private fork.

## ROMs

Put your own legally-owned ROMs in `roms/` (see `roms/README.md`). The editor reads a
ROM at runtime (set the paths in *Options* on first run); the SoH/2Ship forks extract
assets from a ROM at build time.

## Repository layout

```
src/                 the C# editor (Megaton Hammer)
forks/
  patches/           Megaton Hammer's changes to SoH / 2Ship (git patches)
  pj64/              Megaton Hammer's changes to Project64 (GPLv2)
  build-scripts/     per-fork configure/build helpers
  apply-mh-patches.cmd
docs/                design notes & parity write-ups
build-megaton.ps1    one-run build (fetches engines from upstream)
```

## License & credits

Megaton Hammer's own code is **MIT** (`LICENSE`). The playtest engines it builds
against keep their own licenses and are fetched from upstream, not redistributed here;
the Project64 changes in `forks/pj64/` are **GPLv2**. Full attribution to HarbourMasters
(SoH / 2Ship / libultraship), the Project64 team, and the zeldaret decompilation
community is in **`THIRD-PARTY-NOTICES.md`**. This is a fan-made tool, not affiliated
with or endorsed by Nintendo.

The editor's source was written with substantial **AI coding assistance**. That
disclosure, and full credit for every project Megaton Hammer was built from — following
GameBanana's **AI Generated Content Policy** for the release — is in
**[`AI_GENERATED_CONTENT.md`](AI_GENERATED_CONTENT.md)**.
