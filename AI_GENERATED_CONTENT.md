# AI-Generated Content Disclosure & Credits

**Megaton Hammer is released as a tool on GameBanana.** Its source code was written with
substantial **AI coding assistance**, so this file is its disclosure and credits under
GameBanana's **[AI Generated Content Policy][gb]** (Site Documentation wiki #2175). It is
meant to travel with the release: paste the relevant parts into the submission body and
keep this file in the download.

> Good-faith guidance, not legal advice. GameBanana's live wiki is authoritative; where
> anything here disagrees with it, **the wiki wins.**

[gb]: https://gamebanana.com/wikis/2175

---

## Disclaimer (please read first)

> **Megaton Hammer's source code was co-written with an AI coding assistant (Anthropic's
> Claude, via Claude Code).** The program was directed, reviewed, tested, and is
> maintained by its human author, but a large portion of the code was AI-generated. It
> was built by studying many existing open-source projects (listed below), and it *builds
> against* several game engines and tools that keep their own licenses. No Nintendo game
> data ships in this release — you supply your own legally-owned ROM.

When uploading to GameBanana, reproduce the bold line above at the top of the body **or**
put **`(AI Assisted)`** in the submission title, so it is unmistakable that the code was
AI-generated and not wholly hand-authored.

---

## How the policy maps to Megaton Hammer

GameBanana's policy describes AI content in four parts. For a *tool whose code was
AI-assisted*, they map like this:

| Policy part | For Megaton Hammer |
|---|---|
| **Software** | the AI assistant that generated code — **Anthropic Claude / Claude Code** |
| **Input** | the human author's direction, prompts, design decisions, and reviews |
| **Dataset / sources** | the concrete open-source projects studied to build the editor (the "Extensive Credits" list below) |
| **Inference** | the editor's own source code, which the author reviewed and is responsible for |

**A note on "what the AI was trained on":** a language model's *training corpus* is not
something anyone can enumerate, and it is not what this section is about. What the policy
actually wants — and what this file provides — is an honest account of the **specific
projects this editor was derived from and built against.** Those are listed in full below;
if any relevant source is missing, that is an omission to fix, not a claim of
independence.

---

## Software sharing

The AI tool used to author the code:

- **Anthropic — Claude (Claude Code / Opus model family)** — <https://www.anthropic.com/claude-code>

Anthropic's usage terms permit redistribution and commercial use of assistant-produced
code; the editor is released under **MIT** (see `LICENSE`), and its author reviewed and is
responsible for the shipped source.

---

## Extensive credits — projects Megaton Hammer was built from

Full attribution and licensing for the engines it redistributes-a-delta-of / builds
against is in **[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)**; this is the
consolidated list of everything that shaped the editor, grouped by role. Unless noted,
these were **studied as references** (design, file formats, behavior) — Megaton Hammer
does not vendor their source.

### Level-editor design & interaction (the "Hammer" heritage)

| Project | Author / upstream | Role |
|---|---|---|
| Valve **Source SDK 2013** (Hammer, `vbsp`/`vvis`) | Valve — <https://github.com/ValveSoftware/source-sdk-2013> | Hammer's tool/UX design and its brush/face/visibility model were the reference for Megaton Hammer's editing paradigm. **No Valve code is included**; design/behavior reference only. |
| **Sledge** | LogicAndTrick — <https://github.com/LogicAndTrick/sledge> | open-source C# Hammer-alike; reference for editor architecture |
| **TrenchBroom** | kduske / TrenchBroom — <https://github.com/TrenchBroom/TrenchBroom> | brush-editing UX reference |

### Zelda 64 scene / asset tooling (formats, extraction, export)

| Project | Author / upstream | Role |
|---|---|---|
| **SharpOcarina** | hylian-modding — <https://github.com/hylian-modding/SharpOcarina> | OoT scene/room + collision building reference |
| **ZAPDTR** | HarbourMasters — <https://github.com/HarbourMasters/ZAPDTR> | asset (scene/room/display-list/texture) format reference |
| **Torch** | HarbourMasters — <https://github.com/HarbourMasters/Torch> | asset extraction / OTR resource format reference |
| **OTRExporter** | HarbourMasters — <https://github.com/HarbourMasters/OTRExporter> | OTR/O2R packing reference |
| **fast64** | Fast-64 — <https://github.com/Fast-64/fast64> | F3DEX2 display-list / geometry export reference |

### Playtest engines (built against; see `THIRD-PARTY-NOTICES.md` for licenses)

| Project | Author / upstream | License | Role |
|---|---|---|---|
| **Ship of Harkinian** (Shipwright) | HarbourMasters — <https://github.com/HarbourMasters/Shipwright> | upstream | OoT playtest target; patched with a console command |
| **2Ship2Harkinian** | HarbourMasters — <https://github.com/HarbourMasters/2ship2harkinian> | upstream | MM playtest target; patched with a console command |
| **libultraship** | HarbourMasters (nested) | upstream | rendering/resource layer used by SoH/2Ship |
| **Project64** | Project64 team — <https://github.com/project64/project64> | **GPLv2** | vanilla-N64 playtest target; the modified files in `forks/pj64/` are a GPLv2 derivative |

### Game semantics (actor / scene / param behavior)

| Project | Author / upstream | Role |
|---|---|---|
| **OoT decompilation** | zeldaret — <https://github.com/zeldaret/oot> | actor/scene/collision/param semantics reference (**no decompiled source is included**) |
| **MM decompilation** | zeldaret — <https://github.com/zeldaret/mm> | same, for Majora's Mask |

**Input performer / author:** the Megaton Hammer author (GitHub: **AgitationSkeleton**) —
map layouts, design, direction, and code review.

---

## Prohibited categories — confirmation none apply

- **Blacklisted sources** — the AI assistance was used to write **software**, not to clone
  a celebrity, online personality, or political figure, and not to reproduce a blacklisted
  game's assets. The "dataset" here is a set of **open-source software projects**, credited
  above.
- **Offensive language / indecent artwork / slander** — none. The editor generates no
  speech, art, or audio; it ships no such content.

The tool is provided in good faith, with credit to every project it stands on.

---

## License compatibility (for a commercial/donation-enabled upload)

- **Editor code:** MIT (`LICENSE`) — AI-assisted, author-reviewed; redistribution and
  commercial use permitted.
- **Playtest engines:** keep their own licenses and are fetched from upstream, not
  redistributed wholesale. **Project64 changes in `forks/pj64/` are GPLv2** — if you
  bundle a built Project64, honor GPLv2. SoH/2Ship/libultraship keep their upstream terms.
- **Donation buttons:** compatible with Claude/Anthropic's terms and with MIT; make sure
  any engine binaries you *bundle* are redistributable under their own licenses before
  enabling donations on a package that includes them.

See **[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)** for the authoritative
third-party attribution and the pinned upstream commits (`forks/README.md`).

---

_Structured after GameBanana "AI Generated Content Policy" wiki #2175. Retrieved 2026-07-09.
This is a fan-made tool, not affiliated with or endorsed by Nintendo._
