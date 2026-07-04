# Third-party notices & attribution

Megaton Hammer is a level editor. To *playtest* levels it builds against three
existing emulator/engine projects. **None of their code is redistributed wholesale
here** — the build script fetches each one from its original upstream and applies a
small patch. Full credit for those engines goes to their authors; this project only
adds a thin editor-integration delta.

Megaton Hammer's own code (the `src/` editor + scripts + docs) is MIT — see `LICENSE`.

## Playtest engines (fetched from upstream at build time)

| Project | Author / upstream | License | How we use it |
|---|---|---|---|
| Ship of Harkinian (Shipwright) | HarbourMasters — <https://github.com/HarbourMasters/Shipwright> | see upstream LICENSE | fetched at a pinned commit; patched with `forks/patches/soh-*.patch` (adds the `mh_playtest` console command + build/libultraship fixes) |
| 2Ship2Harkinian | HarbourMasters — <https://github.com/HarbourMasters/2ship2harkinian> | see upstream LICENSE | fetched at a pinned commit; patched with `forks/patches/2ship-*.patch` |
| Project64 | Project64 team — <https://github.com/project64/project64> | **GPLv2** | the modified files in `forks/pj64/` are a derivative of Project64 and remain **GPLv2**; overlay them onto an upstream Project64 checkout |
| libultraship | HarbourMasters (nested in SoH/2Ship) | see upstream LICENSE | patched via `forks/patches/*-libultraship.patch` |

The pinned upstream commits are listed in `forks/README.md`.

## Research references (not vendored, not required to build)

The editor's actor/scene/param handling was written by cross-referencing the
community **OoT / MM decompilation** projects (zeldaret) and their derivatives.
No decompiled source is included in this repository.

## Game assets / ROMs — NOT included

This repository contains **no Nintendo ROMs, ROM dumps, or extracted game assets**.
The editor reads a ROM you supply at runtime; the SoH/2Ship forks extract assets from
a ROM you supply at build time. You must own legal copies of *The Legend of Zelda:
Ocarina of Time* and/or *Majora's Mask*. See `roms/README.md`.

The only images in `src/.../Assets/` are Megaton Hammer's own UI icons and tool-texture
swatches — original artwork, not game data.
