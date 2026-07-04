# Put your own ROMs here

Megaton Hammer ships **no** game data. To use the editor and the playtest forks you
must supply your own **legally-owned** copies of the games:

- **Ocarina of Time** — for the OoT editor + the SoH playtest fork.
- **Majora's Mask** — for the MM editor + the 2Ship playtest fork.
- (Optional) an **OoT MQ debug ROM** (`gc-eu-mq-dbg`) for the Project64 N64 playtest path.

Drop the ROM files in this folder. They are git-ignored and will never be committed.

The editor reads a ROM at **runtime** (set the paths under *Options* on first launch).
The SoH / 2Ship forks extract assets from a ROM at **build time**, so `build-megaton.ps1`
looks here for them.

Nintendo owns these games. Dumping and using a ROM of a cartridge you own is your
responsibility; do not distribute ROMs or extracted assets.
