# Playtest engine forks

Megaton Hammer playtests levels by packing them as native OTR resources into a mod
`.o2r` that a customized **Ship of Harkinian** (OoT) or **2Ship2Harkinian** (MM) build loads
and warps into (see [`PlaytestDialog`](../src/MegatonHammer/Forms/PlaytestDialog.cs) and
[`O2RPacker`](../src/MegatonHammer/Export/O2RPacker.cs)). Those two engines are part of this
project — without them there is no playtest target.

They are **git submodules** pinned to a specific upstream commit, *not* vendored, because the
working trees are ~10 GB each (almost entirely build output). The repo tracks only the small
Megaton Hammer delta: a console-command patch per fork plus the build scripts.

```
forks/
  patches/
    soh-mh_playtest.patch     # adds the `mh_playtest` console command to SoH
    2ship-mh_playtest.patch   # adds the `mh_playtest` console command to 2Ship
  build-scripts/
    soh/    mh_configure.cmd  mh_build.cmd
    2ship/  mh_configure.cmd  mh_build.cmd
  apply-mh-patches.cmd        # applies the patches + installs the scripts (idempotent)
```

The submodules live at the repo root (`SoH/`, `2Ship/`) — that is where the build scripts and
the editor's default exe paths expect them.

## First-time setup

```cmd
git submodule update --init --recursive
forks\apply-mh-patches.cmd
SoH\mh_configure.cmd   &&  SoH\mh_build.cmd
2Ship\mh_configure.cmd &&  2Ship\mh_build.cmd
```

`mh_configure.cmd` / `mh_build.cmd` assume MSVC Build Tools at `C:\BuildTools`, a pre-cloned
`vcpkg` at `D:\Copilot_OOT\WorkFolders\vcpkg`, and a staged ROM for asset extraction. Adjust the
paths at the top of those scripts for a different machine.

## The custom change (`mh_playtest`)

Each fork gains one debug-console command — `mh_playtest [sceneIdHex]` — that warps to the scene
the editor packed its `mods/mh_playtest.o2r` override against. Everything else (the scene/room
resource format) is stock libultraship, so no other engine change is needed. See
[`MM_2SHIP_COMPAT.md`](../MM_2SHIP_COMPAT.md) for the OoT-vs-MM resource-format differences.

## Pins

| Fork  | Upstream | Pinned commit |
|-------|----------|---------------|
| SoH   | HarbourMasters/Shipwright `develop`        | `948b84d8` |
| 2Ship | HarbourMasters/2ship2harkinian `develop`   | `3545e62e` |

To move to a newer upstream: update the submodule, re-run `apply-mh-patches.cmd`, resolve any
patch fuzz, regenerate the patch (`git -C <fork> diff -- <console file>`), and commit the new
pin + patch together.

## Project64

There is no Project64 fork. The PJ64 playtest path
([`Project64Playtest.cs`](../src/MegatonHammer/Forms/Project64Playtest.cs)) launches an
already-installed Project64 with an injected ROM; nothing to build here.
