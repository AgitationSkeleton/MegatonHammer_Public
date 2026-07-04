namespace MegatonHammer.SelfTest;

using A = LogicDemoBuilder.A;
using Demo = LogicDemoBuilder.Demo;

/// <summary>
/// The wired-logic demo catalogue: each entry is one distinct flag-bus configuration from OoT/MM, the
/// setter + reader actors hand-placed at their real decomp param/flag values, with a README describing
/// what it does, how it works, and where it appears in the game. Actor ids + bit layouts verified from
/// the SoH/2S2H decomp. Built by <see cref="LogicDemoBuilder"/> (Run: MegatonHammer --logicdemos).
/// </summary>
public static class LogicDemos
{
    // Actor ids (decomp-accurate).
    const ushort ObjSwitch = 0x012A, DoorShutter = 0x002E, EnDoor = 0x0009, EnBox = 0x000A,
                 HidanKousi = 0x006F, HidanHamstep = 0x0071, MoriElevator = 0x0087, MizuMovebg = 0x0064,
                 MizuWater = 0x0065, JyaLift = 0x0157, ObjLift = 0x012C, Bombiwa = 0x0127, Breakwall = 0x0059,
                 Hamishi = 0x01D2, Bombstone = 0x00CD, BdanSwitch = 0x00E6, ElfMsg = 0x011B, ElfMsg2 = 0x0173,
                 DemoKekkai = 0x01A7, PoEvent = 0x0093, EnSw = 0x0095, GSwitch = 0x0117, Dekubaba = 0x0125;

    public static readonly Demo[] All =
    {
        // ── OoT: switch-flag setters → readers ────────────────────────────────
        new("OoT Forest Temple", "Switch flag - crystal switch opens a barred door", false,
@"WIRED LOGIC: switch flag (the headline 'button opens door' pattern)

WIRING (shared switch flag 0x05)
  Setter: Obj_Switch (0x012A) type 3 = CRYSTAL, switchFlag (params>>8)&0x3F = 5 -> params 0x0503.
  Reader: Door_Shutter (0x002E) doorType 2 = FRONT_SWITCH, switchFlag params&0x3F = 5 -> params 0x0085.
  The wire is switch flag 5: hitting the crystal calls Flags_SetSwitch(5); the door polls
  Flags_GetSwitch(5) and unbars while it is set.

HOW IT WORKS
  Switch flags are a scene-wide array of single bits (0-31 saved in the scene's save block, 32-63
  temporary). Setter and reader coordinate ONLY by sharing the index 5 - there is no targetname; the
  flag number IS the connection, and it is broadcast (any number of readers can watch the same flag).

WHERE IT'S USED
  Every dungeon. Strike the blue crystal switch to drop the bars blocking the next room (Forest/Fire/
  Water temples). The canonical Zelda 'press the button, the gate opens'.",
            new[]
            {
                new A("Door_Shutter FRONT_SWITCH (flag 5)", DoorShutter, (2 << 6) | 5, 0, -330),
                new A("Obj_Switch CRYSTAL (sets flag 5)", ObjSwitch, 3 | (5 << 8), 0, 100),
            }),

        new("OoT Fire Temple", "Switch flag - eye switch raises a grate (8-bit flag)", false,
@"WIRED LOGIC: switch flag, 8-bit field (eye switch -> grate)

WIRING (shared switch flag 0x18)
  Setter: Obj_Switch (0x012A) type 2 = EYE, switchFlag (params>>8)&0x3F = 0x18 -> params 0x1802.
          Shoot the eye with an arrow/slingshot to set the flag.
  Reader: Bg_Hidan_Kousi (0x006F) Fire-Temple grate, switchFlag (params>>8)&0xFF = 0x18 (an 8-BIT
          field, so it can address temp switches 32-63), grate index params&0xFF = 0 -> params 0x1800.

HOW IT WORKS
  The grate sets its state once at init from the flag, then watches for the bit to flip and raises out
  of the way. Note the 8-bit switchFlag field on Bg_Hidan_Kousi / Bg_Hidan_Hamstep (most actors use a
  6-bit &0x3F field) - the editor exposes this as an 8-bit length for these actors.

WHERE IT'S USED
  Fire Temple - shoot the eye switch to clear the barred grate blocking a corridor.",
            new[]
            {
                new A("Bg_Hidan_Kousi grate (flag 0x18)", HidanKousi, 0x18 << 8, 0, -300),
                new A("Obj_Switch EYE (sets flag 0x18)", ObjSwitch, 2 | (0x18 << 8), 0, 120, 60),
            }),

        new("OoT Forest Temple", "Switch flag - floor switch raises an elevator", false,
@"WIRED LOGIC: switch flag (pressure plate -> elevator)

WIRING (shared switch flag 0x0A)
  Setter: Obj_Switch (0x012A) type 0 = FLOOR (pressure plate), switchFlag (params>>8)&0x3F = 0x0A ->
          params 0x0A00. (Type 1 FLOOR_RUSTY needs a heavy/Megaton weight.)
  Reader: Bg_Mori_Elevator (0x0087) Forest-Temple elevator, switchFlag params&0x3F = 0x0A -> params
          0x000A. Interpolates between two heights while the flag is set.

HOW IT WORKS
  Standing on the plate sets the flag; the elevator reads it and lerps up to its raised Y. Floor switch
  subtypes vary (momentary, toggle, timed) but all drive the same shared switch bit.

WHERE IT'S USED
  Forest Temple central elevator; the same setter/reader pair drives countless dungeon lifts.",
            new[]
            {
                new A("Bg_Mori_Elevator (flag 0x0A)", MoriElevator, 0x0A, 0, -200),
                new A("Obj_Switch FLOOR (sets flag 0x0A)", ObjSwitch, 0 | (0x0A << 8), 0, 150),
            }),

        new("OoT Water Temple", "Switch flag - floor switch raises a moving platform", false,
@"WIRED LOGIC: switch flag (pressure plate -> moving block)

WIRING (shared switch flag 0x0A)
  Setter: Obj_Switch (0x012A) type 0 = FLOOR, switchFlag 0x0A -> params 0x0A00.
  Reader: Bg_Mizu_Movebg (0x0064) Water-Temple moving platform, type (params>>12)&0xF = 4 (switch-
          driven), switchFlag params&0x3F = 0x0A -> params 0x400A. Rises 115.2 units when the flag is set.

HOW IT WORKS
  Identical flag-bus mechanism to the elevator, but the reader is a horizontal/vertical block whose
  rest and active offsets are baked into the actor; the single shared bit chooses which it is at.

WHERE IT'S USED
  Water Temple block puzzles - step on the plate, a platform rises to make a path.",
            new[]
            {
                new A("Bg_Mizu_Movebg (flag 0x0A)", MizuMovebg, (4 << 12) | 0x0A, 0, -200),
                new A("Obj_Switch FLOOR (sets flag 0x0A)", ObjSwitch, 0 | (0x0A << 8), 0, 150),
            }),

        new("OoT Water Temple", "Water level - the three triforce switches", false,
@"WIRED LOGIC: switch flags (reserved level flags 0x1C/0x1D/0x1E)

WIRING
  Setter: three Obj_Switch FLOOR plates, switchFlags 0x1C, 0x1D, 0x1E (params 0x1C00/0x1D00/0x1E00).
  Reader: Bg_Mizu_Water (0x0065) main water plane, type params&0xFF = 0 -> params 0x0000. It reads the
          three HARD-CODED flags 0x1C/0x1D/0x1E (F1/F2/F3 levels); the highest set flag wins and the
          plane lerps to that height. (Local water boxes, types 2-4, instead read their own switchFlag
          (params>>8)&0xFF and rise 85/110/160 units.)

HOW IT WORKS
  Unusual case: the reader's flag indices are fixed by the engine (not authored), so you wire the
  level by placing setters on flags 0x1C-0x1E. Highest set level wins.

WHERE IT'S USED
  Water Temple - the three triforce-symbol switches that raise/lower the whole dungeon's water.",
            new[]
            {
                new A("Bg_Mizu_Water main plane (reads 0x1C/0x1D/0x1E)", MizuWater, 0x0000, 0, 0, 0),
                new A("Obj_Switch FLOOR (sets 0x1C, level 1)", ObjSwitch, 0 | (0x1C << 8), -150, -200),
                new A("Obj_Switch FLOOR (sets 0x1D, level 2)", ObjSwitch, 0 | (0x1D << 8), 0, -200),
                new A("Obj_Switch FLOOR (sets 0x1E, level 3)", ObjSwitch, 0 | (0x1E << 8), 150, -200),
            }),

        new("OoT Spirit Temple", "Switch flag - switch raises a chain lift", false,
@"WIRED LOGIC: switch flag (switch -> Bg_Jya_Lift)

WIRING (shared switch flag 0x07)
  Setter: Obj_Switch (0x012A) FLOOR/CRYSTAL, switchFlag 0x07 -> params 0x0700.
  Reader: Bg_Jya_Lift (0x0157) Spirit-Temple chain lift, switchFlag params&0x3F = 0x07 -> params 0x0007.
          Rises from Y=1613 to Y=973 after a 20-frame delay once the flag is set.

WHERE IT'S USED
  Spirit Temple - the big chain platform that lowers when you hit its switch.",
            new[]
            {
                new A("Bg_Jya_Lift (flag 0x07)", JyaLift, 0x07, 0, -250, 0),
                new A("Obj_Switch (sets flag 0x07)", ObjSwitch, 0 | (0x07 << 8), 0, 150),
            }),

        new("OoT Fire Temple", "Collapsing platform - one-shot self setter+gate", false,
@"WIRED LOGIC: switch flag, self-consuming (closest to a fire-once relay)

WIRING (switch flag 0x0C)
  Actor: Obj_Lift (0x012C). collapse switchFlag (params>>2)&0x3F = 0x0C; fall delay (params>>8)&7 = 3
         (~30 frames) -> params 0x0330. At Init, if Flags_GetSwitch(0x0C) is ALREADY set it Actor_Kills
         itself (so it never respawns); when it finishes falling it Flags_SetSwitch(0x0C).

HOW IT WORKS
  A platform that is its own setter AND reader on flag 0x0C: it reads the flag to decide 'already
  collapsed -> don't exist', and sets it when it falls. This is the engine's nearest analogue to a
  fire-once logic relay - the wire is consumed the first time it triggers.

WHERE IT'S USED
  Fire/Spirit collapsing blocks that fall once and stay gone.",
            new[] { new A("Obj_Lift (collapse flag 0x0C)", ObjLift, (0x0C << 2) | (3 << 8), 0, -100, 200) }),

        // ── OoT: room-clear (kill-all-enemies) wires ──────────────────────────
        new("OoT Forest Temple", "Room clear - kill all enemies opens a door", false,
@"WIRED LOGIC: room-clear flag (the wire is the ROOM NUMBER)

WIRING (implicit - no flag to author)
  Setter: NONE you place. The engine calls Flags_SetTempClear(roomNumber) automatically when the last
          actor of category ACTORCAT_ENEMY in the room is removed.
  Reader: Door_Shutter (0x002E) doorType 1 = SHUTTER_FRONT_CLEAR -> params 0x0040. Reads
          Flags_GetClear(room) and unbars when the room is cleared. (Place enemies + this door.)

HOW IT WORKS
  The Clear namespace is special: it is indexed by the room number, not an author-chosen index. So
  'defeat all enemies in this room -> open the barred door' needs zero wiring - put ACTORCAT_ENEMY
  actors and a FRONT_CLEAR shutter in the same room. (The engine also refuses to respawn enemies in a
  room whose clear bit is set.)

WHERE IT'S USED
  Every combat room with a barred exit - the bars drop when you clear the room.",
            new[]
            {
                new A("Door_Shutter FRONT_CLEAR", DoorShutter, 1 << 6, 0, -330),
                new A("Deku Baba (ACTORCAT_ENEMY)", Dekubaba, 0, -120, 60),
                new A("Deku Baba (ACTORCAT_ENEMY)", Dekubaba, 120, 60),
            }),

        new("OoT Dungeon (generic)", "Room clear - kill all enemies spawns a chest", false,
@"WIRED LOGIC: room-clear flag -> chest appears

WIRING (room-clear, implicit)
  Reader: En_Box (0x000A) type (params>>12)&0xF = 1 = ROOM_CLEAR_BIG (or 7 = ROOM_CLEAR_SMALL),
          treasureFlag params&0x1F = 6 -> params 0x1006. Hidden until Flags_GetClear(room) is set, then
          it appears (and promotes the temp clear to permanent).
  Setter: implicit room-clear (kill the enemies).

WHERE IT'S USED
  The classic 'beat the enemies, the reward chest drops in' room.",
            new[]
            {
                new A("En_Box ROOM_CLEAR_BIG (chest flag 6)", EnBox, (1 << 12) | 6, 0, -200, 0, 0, unchecked((short)0x8000)),
                new A("Deku Baba (ACTORCAT_ENEMY)", Dekubaba, -120, 80),
                new A("Deku Baba (ACTORCAT_ENEMY)", Dekubaba, 120, 80),
            }),

        // ── OoT: chest treasure-flag patterns ─────────────────────────────────
        new("OoT Spirit Temple", "Switch-flag chest - chest falls when a switch is hit", false,
@"WIRED LOGIC: switch flag (in rot.z!) + chest treasure flag

WIRING
  Reader/chest: En_Box (0x000A) type (params>>12)&0xF = 3 = SWITCH_FLAG_FALL_BIG, treasureFlag
          params&0x1F = 4 -> params 0x3004. Its APPEAR switch flag is the WHOLE rot.z field (= 9 here),
          a different storage location from the treasureFlag in params.
  Setter: Obj_Switch (0x012A) CRYSTAL, switchFlag 9 -> params 0x0903.
  So rot.z = 9 wires the chest to switch flag 9; the chest's opened state is tracked separately on the
  treasure namespace via flag 4.

HOW IT WORKS
  ENBOX_PARAMS packs (type<<12)|(item<<5)|treasureFlag, but the appear-switch is uniquely in rot.z.
  Type 3/8 = falls in when the flag is set; type 11 = appears (non-falling) when set.

WHERE IT'S USED
  Spirit/Fire temples - hit a switch and a chest drops from the ceiling.",
            new[]
            {
                new A("En_Box SWITCH_FALL (treasure 4, appears on rot.z switch 9)", EnBox, (3 << 12) | 4, 0, -200, 0, 0, unchecked((short)0x8000), 9),
                new A("Obj_Switch CRYSTAL (sets flag 9)", ObjSwitch, 3 | (9 << 8), 0, 120),
            }),

        new("OoT Dungeon (generic)", "Plain treasure chest - opened-flag only", false,
@"WIRED LOGIC: chest treasure flag (self setter+reader, no external wire)

WIRING (chest/treasure flag 0x00)
  Actor: En_Box (0x000A) type (params>>12)&0xF = 0 = big chest, treasureFlag params&0x1F = 0 ->
          params 0x0000. Its open state = Flags_GetTreasure(0); opening it Flags_SetTreasure(0) so it
          stays open across reloads.

HOW IT WORKS
  A plain chest is its own setter/reader on the treasure namespace - no other actor is involved. Each
  chest in a scene needs a UNIQUE treasure flag (0-31) so they don't share an open state.

WHERE IT'S USED
  Every normal chest in the game.",
            new[] { new A("En_Box big chest (treasure flag 0)", EnBox, 0, 0, -200, 0, 0, unchecked((short)0x8000)) }),

        new("OoT Royal Family Tomb", "Song-triggered chest", false,
@"WIRED LOGIC: chest gated on a played song (not a flag index)

WIRING
  Actor: En_Box (0x000A) type (params>>12)&0xF = 10 = appears after SUN'S SONG (type 9 = ZELDA'S
         LULLABY), treasureFlag in params&0x1F -> params 0xA000.

HOW IT WORKS
  The 'wire' here is the played-song state, not a flag - the chest watches for the ocarina song and
  fades in. (Type 9/10 only.)

WHERE IT'S USED
  Royal Family's Tomb (Sun's Song), and lullaby chests around Hyrule.",
            new[] { new A("En_Box SONG chest (Sun's Song)", EnBox, 10 << 12, 0, -200, 0, 0, unchecked((short)0x8000)) }),

        // ── OoT: bombable / breakable setters ─────────────────────────────────
        new("OoT Dodongos Cavern", "Bombable wall - break sets a switch flag", false,
@"WIRED LOGIC: switch flag (bomb a wall -> open a gate)

WIRING (shared switch flag 0x12)
  Setter: Bg_Breakwall (0x0059) bombable wall, switchFlag params&0x3F = 0x12, wall type (params>>13)&3
          = 0 -> params 0x0012. On destruction Flags_SetSwitch(0x12); at Init it self-kills if the flag
          is already set (so it stays broken across reloads).
  Reader: any switch reader (here a Door_Shutter FRONT_SWITCH on flag 0x12 -> params 0x0092).

WHERE IT'S USED
  Dodongo's Cavern bombable walls, Bottom of the Well - bomb the wall, a barred door opens.",
            new[]
            {
                new A("Bg_Breakwall (sets flag 0x12)", Breakwall, 0x12, 0, 80),
                new A("Door_Shutter FRONT_SWITCH (flag 0x12)", DoorShutter, (2 << 6) | 0x12, 0, -330),
            }),

        new("OoT Dodongos Cavern", "Bombable boulder - break sets a switch flag", false,
@"WIRED LOGIC: switch flag (bomb a boulder -> trigger)

WIRING (switch flag 0x10)
  Setter: Obj_Bombiwa (0x0127) bombable boulder, switchFlag params&0x3F = 0x10 -> params 0x0010.
          Bombing it Flags_SetSwitch(0x10); self-kills at Init if already set.
  Reader: a Door_Shutter FRONT_SWITCH on flag 0x10 (params 0x0090).

WHERE IT'S USED
  Dodongo's Cavern and overworld grottos - clear the boulder to advance.",
            new[]
            {
                new A("Obj_Bombiwa (sets flag 0x10)", Bombiwa, 0x10, 0, 80),
                new A("Door_Shutter FRONT_SWITCH (flag 0x10)", DoorShutter, (2 << 6) | 0x10, 0, -330),
            }),

        new("OoT Death Mountain", "Bombable DMT stone - break sets a switch flag", false,
@"WIRED LOGIC: switch flag (Death Mountain bombable stone)

WIRING (switch flag 0x15)
  Setter: Bg_Spot16_Bombstone (0x00CD), switchFlag (params>>8)&0x3F = 0x15, type params&0xFF = 0 ->
          params 0x1500. Flags_SetSwitch on bomb hit.
  Reader: any switch reader sharing flag 0x15.

WHERE IT'S USED
  Death Mountain Trail - bomb the stone blocking the path/cave.",
            new[]
            {
                new A("Bg_Spot16_Bombstone (sets flag 0x15)", Bombstone, 0x15 << 8, 0, 60),
                new A("Door_Shutter FRONT_SWITCH (flag 0x15)", DoorShutter, (2 << 6) | 0x15, 0, -330),
            }),

        new("OoT Fire Temple", "Step switch (8-bit) - weighted step sets a flag", false,
@"WIRED LOGIC: switch flag, 8-bit (hammer-block step)

WIRING (switch flag 0x16)
  Setter: Bg_Hidan_Hamstep (0x0071), switchFlag (params>>8)&0xFF = 0x16 (8-bit), step index params&0xFF
          = 0 (the main step) -> params 0x1600. The main step Flags_SetSwitch when stepped on.
  Reader: Bg_Hidan_Kousi grate or a door on flag 0x16.

WHERE IT'S USED
  Fire Temple 'hammer block' staircase - pound/step the blocks to raise a grate.",
            new[]
            {
                new A("Bg_Hidan_Hamstep main step (flag 0x16)", HidanHamstep, 0x16 << 8, 0, 60),
                new A("Bg_Hidan_Kousi grate (flag 0x16)", HidanKousi, 0x16 << 8, 0, -300),
            }),

        new("OoT Jabu-Jabu", "Pressure switch - weight sets a flag", false,
@"WIRED LOGIC: switch flag (Jabu blue/yellow pressure switch)

WIRING (switch flag 0x17)
  Setter: Bg_Bdan_Switch (0x00E6), switchFlag (params>>8)&0x3F = 0x17, type params&0xFF = 0 (BLUE
          weight) -> params 0x1700. SetSwitch when weighted; YELLOW (type 2) unsets when released.
  Reader: a door/object on flag 0x17.

WHERE IT'S USED
  Jabu-Jabu's Belly - stand on (or drop a weight on) the coloured switch.",
            new[]
            {
                new A("Bg_Bdan_Switch BLUE (flag 0x17)", BdanSwitch, 0x17 << 8, 0, 60),
                new A("Door_Shutter FRONT_SWITCH (flag 0x17)", DoorShutter, (2 << 6) | 0x17, 0, -330),
            }),

        // ── OoT: trigger regions, story gates, special namespaces ─────────────
        new("OoT Dungeon (generic)", "Trigger region - proximity sets a flag (Elf_Msg)", false,
@"WIRED LOGIC: invisible trigger volume that sets a switch flag (+ shows a message)

WIRING (switch flag 0x1A)
  Actor: Elf_Msg (0x011B). switch-to-set (params>>8)&0x3F = 0x1A; message id base = params&0xFF;
         region cuboid/cylinder = params&0x4000 -> params 0x1A00. On enter/talk it
         Flags_SetSwitch(0x1A). An appear/kill CONDITION is packed in rot.y: rot.y == -1 kills on
         room-clear; 1..0x40 kills when switch (rot.y-1) is set; >= 0x41 activates when switch
         (rot.y-0x41) is set. Scale via rot.x / rot.z.
  Reader: any switch reader on flag 0x1A.

HOW IT WORKS
  The single richest 'wiring' actor: it BOTH gates on a flag (rot.y) AND sets a flag (params) when the
  player enters - a trigger_multiple with an input condition and an output flag in one. (Elf_Msg2,
  0x0173, is the same but fires when the textbox closes.)

WHERE IT'S USED
  Invisible message/trigger volumes throughout dungeons and the overworld.",
            new[]
            {
                new A("Elf_Msg trigger (sets flag 0x1A)", ElfMsg, 0x1A << 8, 0, 0),
                new A("Door_Shutter FRONT_SWITCH (flag 0x1A)", DoorShutter, (2 << 6) | 0x1A, 0, -330),
            }),

        new("OoT Ganons Castle", "Story barrier gate (eventChkInf, crosses scenes)", false,
@"WIRED LOGIC: story flag on the GLOBAL eventChkInf bus

WIRING
  Actor: Demo_Kekkai (0x01A7). params selects the barrier: 0 = Ganon's-tower barrier, 1-6 = the six
         trial barriers (Water/Light/Fire/Shadow/Spirit/Forest) -> params 0x0000 (tower). Reads the
         matching EVENTCHKINF flag; self-kills if already dispelled; the tower barrier
         Flags_SetEventChkInf(...) after its cutscene.

HOW IT WORKS
  eventChkInf is a GLOBAL flag array (flag>>4 word, bit 1<<(flag&0xF)) that persists across scenes -
  this is the story/quest bus, distinct from scene-local switch flags. So this wire spans the whole
  game, not one room.

WHERE IT'S USED
  Ganon's Castle - the central barrier and the six elemental trial doors.",
            new[] { new A("Demo_Kekkai tower barrier", DemoKekkai, 0, 0, -150) }),

        new("OoT Forest Temple", "Poe-painting / block puzzle gate", false,
@"WIRED LOGIC: switch flag persistence on a puzzle object

WIRING (switch flag 0x1B)
  Actor: Bg_Po_Event (0x0093), switchFlag params&0x3F = 0x1B, type (params>>8)&0xF = 0 -> params
         0x001B. Self-kills at Init if the flag is set; on solving the puzzle it sets the flag so it
         stays solved.

WHERE IT'S USED
  Forest Temple - the Poe-sisters paintings and the rotating-block puzzles.",
            new[] { new A("Bg_Po_Event (flag 0x1B)", PoEvent, 0x1B, 0, -150) }),

        new("OoT Any dungeon", "Gold Skulltula token (separate GS namespace)", false,
@"WIRED LOGIC: gold-skulltula flag (NOT a switch flag)

WIRING
  Actor: En_Sw (0x0095). GS flag = GET_GS_FLAGS((params&0x1F00)>>8) & (params&0xFF): group index in
         bits 8-12, bit-mask in bits 0-7; token type (params&0xE000)>>13 (0 wall, 1-2 hanging, 3-4
         flying). Example params 0x0001 = group 0, mask 1, wall token. Self-kills if already collected.

HOW IT WORKS
  Uses the gold-skulltula bitfield, a SEPARATE namespace from switch/chest/clear flags - the editor
  treats it as its own wire space (group+mask), so two tokens never collide with a switch.

WHERE IT'S USED
  Every area with gold skulltulas (100 across the game).",
            new[] { new A("En_Sw gold-skulltula token", EnSw, 0x0001, 0, -100, 120) }),

        new("OoT Gerudo Training Ground", "Silver-rupee group sets a flag on completion", false,
@"WIRED LOGIC: switch flag set when a silver-rupee set is collected

WIRING (switch flag 0x16)
  Setter: En_G_Switch (0x0117) silver-rupee, type (params>>12)&0xF = 0, switchFlag params&0x3F = 0x16,
          count (params>>6)&0x3F = 5 -> params 0x0156. The 'parent' tracker Flags_SetSwitch(0x16) once
          all silver rupees in the group are collected; children read the flag to despawn.
  Reader: a Door_Shutter FRONT_SWITCH / grate on flag 0x16.

WHERE IT'S USED
  Spirit/Shadow temples, Gerudo Training Ground, Ganon's Castle silver-rupee rooms.",
            new[]
            {
                new A("En_G_Switch silver rupees (sets flag 0x16)", GSwitch, (5 << 6) | 0x16, 0, 60, 60),
                new A("Door_Shutter FRONT_SWITCH (flag 0x16)", DoorShutter, (2 << 6) | 0x16, 0, -330),
            }),

        new("OoT Any dungeon", "Locked door (small key) records its opened state", false,
@"WIRED LOGIC: a door that is its own reader (locked?) and setter (opened)

WIRING (switch flag 0x08)
  Actor: En_Door (0x0009), doorType (params>>7)&7 = 1 = DOOR_LOCKED, switchFlag params&0x3F = 0x08 ->
         params 0x0088. The gameplay gate is a small key; once spent, the door Flags_SetSwitch(0x08)
         so it stays open across reloads (it reads the flag at load to know it is already open).

HOW IT WORKS
  The small key is the lock; the switch flag only RECORDS the opened state so you don't re-lock it.
  Door_Shutter has the same idea via doorType 0x0B (KEY_LOCKED), 0x05 (BOSS), 0x03 (BACK_LOCKED).

WHERE IT'S USED
  Every locked dungeon door.",
            new[] { new A("En_Door LOCKED (records flag 0x08)", EnDoor, (1 << 7) | 0x08, 0, -330) }),

        // ── OoT: scene-structure logic (NOT expressible with actor params alone) ─
        new("OoT Temple of Time", "Age gate (child vs adult) - scene setup layers", false,
@"WIRED LOGIC: alternate scene headers (0x18) - a SCENE-STRUCTURE feature, not an actor param

HOW IT WORKS
  A scene can carry up to four alternate headers (CHILD_DAY / CHILD_NIGHT / ADULT_DAY / ADULT_NIGHT)
  via scene command 0x18. Play_Init picks the layer from Link's age + the time of day and loads that
  layer's ACTOR LIST. So 'this actor only exists as adult' is authored by putting it in the adult
  setup, NOT by a per-actor field. (A per-actor alternative is a self-kill at Init: if LINK_IS_ADULT
  ... Actor_Kill.)

EDITOR SUPPORT
  Partially. The editor models scene SETUPS (it round-trips alternate headers and exports per-setup
  actor lists). Author the actor in the matching setup ('Loads under' layer). This demo's actor is a
  placeholder; switch its setup in the editor to test the age gate.

WHERE IT'S USED
  Temple of Time, Kakariko, Hyrule Field - the world changes between child and adult.",
            new[] { new A("Pot (placeholder - assign to a setup layer)", 0x0082, 0, 0, -150) }),

        new("OoT Hyrule Field", "Time gate (day vs night) - scene setup layers", false,
@"WIRED LOGIC: alternate scene headers (0x18), DAY vs NIGHT - scene-structure, not an actor param

HOW IT WORKS
  Same 0x18 layer mechanism as the age gate but keyed on day/night, so an actor list can differ by
  time (e.g. Stalchildren spawn at night in the fields, the market fills with ReDead at night). The
  per-actor alternative is an IS_DAY / night self-kill at Init.

EDITOR SUPPORT
  Partially (via setups, as above). Place the night-only actors in the night setup layer.

WHERE IT'S USED
  Hyrule Field Stalchildren; Market (day) vs ReDead Market (night).",
            new[] { new A("Pot (placeholder - assign to night setup)", 0x0082, 0, 0, -150) }),

        // ── MM: reuses OoT actors, adds three systems ─────────────────────────
        new("MM (any dungeon)", "Switch flag in MM (same actors as OoT)", true,
@"WIRED LOGIC: switch flag - identical to OoT

WIRING (shared switch flag 0x05)
  MM uses the SAME Flags_Get/SetSwitch bus (0-127 per scene), the SAME Door_Shutter (0x002E) and
  En_Box (0x000A) layouts, and the SAME Obj_Switch (0x012A). So every OoT switch/chest/clear/bombable
  pattern in this set works unchanged in MM's dungeons.
  Setter: Obj_Switch CRYSTAL flag 5 (params 0x0503). Reader: Door_Shutter FRONT_SWITCH flag 5
  (params 0x0085).

WHERE IT'S USED
  Woodfall, Snowhead, Great Bay and Stone Tower temples - all reuse the OoT puzzle vocabulary.",
            new[]
            {
                new A("Door_Shutter FRONT_SWITCH (flag 5)", DoorShutter, (2 << 6) | 5, 0, -330),
                new A("Obj_Switch CRYSTAL (sets flag 5)", ObjSwitch, 3 | (5 << 8), 0, 100),
            }),

        new("MM Clock Town", "HALFDAYBIT - actor exists only on a specific half-day", true,
@"WIRED LOGIC: per-actor half-day spawn gating (an existence MASK, not a flag bus)

WIRING
  An actor entry whose id has the top bits 0xE000 set carries PACKED data in its rotation fields
  instead of rotations:
    halfDaysBits = ((rot.x & 7) << 7) | (rot.z & 0x7F)   -- a 10-bit mask, one bit per half-day over
                                                            the 3-day cycle (DAY0_DAWN..DAY4_NIGHT)
    csId = rot.y & 0x7F ;  actual actor id = id & 0x1FFF
  This demo: a pot (0x0082) flagged 0xE000, with the mask set to a single half-day (e.g. Day-2 night
  bit) so it exists only then. A zero mask = present in all half-days.

HOW IT WORKS
  The engine spawns/kills each 0xE000-flagged actor at every dawn/dusk boundary by this mask. This is
  the lever for 'this NPC/object appears Day 2 night'. (For non-0xE000 actors MM stores facing as a
  binary angle, chosen per axis by id bits 0x8000/0x4000/0x2000.)

EDITOR SUPPORT
  Data round-trips (the editor already carries these rot fields), but a friendly half-day mask UI is a
  known gap - the bits are authored in the rotation fields directly here.

WHERE IT'S USED
  Clock Town and the field NPCs/objects that come and go by day across the three-day cycle.",
            new[] { new A("Pot, 0xE000-flagged, Day-2-night mask (rot.x/z)", (ushort)(0x0082), 0, 0, -150, 0, 0, 0, 0, 0xE000) }),

        new("MM Clock Town", "weekEventReg event-flag gate (quest/world-state bus)", true,
@"WIRED LOGIC: weekEventReg - MM's 800-flag quest/world-state bus

HOW IT WORKS
  weekEventReg[100] = 800 flags. PACK_WEEKEVENTREG_FLAG(index,mask) = (index<<8)|mask;
  CHECK_WEEKEVENTREG(flag) = WEEKEVENTREG(flag>>8) & (flag&0xFF). Many En_* actors and every scheduled
  NPC gate their spawn/behaviour on these (e.g. Anju checks PROMISED_MIDNIGHT_MEETING and despawns on
  HAD_MIDNIGHT_MEETING). It is the main quest/schedule/world-state bus, persisted across the 3-day
  reset by a per-flag persistence mask.

EDITOR SUPPORT / CAVEAT
  Which weekEventReg bits PERSIST across the cycle reset is baked into engine tables (z_sram, the
  DEFINE_SCENE persistence mask), NOT scene-file data - so authoring full MM world-state needs the
  custom-engine convention. The 2Ship fork already applies the editor's 'starting week-event' flags
  (SceneSettings.StartWeekEvents), which is the supported slice today.

WHERE IT'S USED
  The entire MM main quest, the Bombers' notebook, shop/bank state, and every scheduled NPC.",
            new[] { new A("Pot (placeholder - gate via StartWeekEvents)", 0x0082, 0, 0, -150) }),

        new("MM Clock Town", "Schedule-VM NPC (scripted by clock + day) - NOT param-authorable", true,
@"WIRED LOGIC: schedule bytecode VM (the deepest MM system)

HOW IT WORKS
  ~17 NPCs (Anju En_An, Kafei En_Test, postman En_Pm, Bombers, Gorman, shopkeepers...) run a u8[]
  bytecode of SCHEDULE_CMD_* ops - CHECK_TIME_RANGE, CHECK_BEFORE_TIME, CHECK_NOT_IN_DAY,
  CHECK_NOT_IN_SCENE, CHECK_WEEK_EVENT_REG, CHECK_MISC, RET_VAL, RET_TIME(t0,t1,result), RET_NONE,
  BRANCH. RET_TIME returns a behaviour code + a time window that drives path interpolation; a big
  per-NPC switch maps result -> position/path/animation. HALFDAYBIT decides EXISTENCE; the schedule
  decides BEHAVIOUR while present.

EDITOR SUPPORT / CAVEAT (NOT FULLY SUPPORTED)
  The schedule bytecode lives in the ACTOR OVERLAY's data segment, NOT in the room actor entry - there
  is no actor-entry field to supply a custom script. Placing En_An (here as the demo actor) spawns
  vanilla Anju with her vanilla schedule. Authoring a CUSTOM schedule needs overlay-patching or a
  custom-engine resource convention; the 2Ship fork's position-override is the current substitute.
  This is the one wired-logic class the editor cannot author from scene data alone.

WHERE IT'S USED
  The Anju/Kafei quest, the Bombers, the postman's rounds, town shopkeepers' hours.",
            new[] { new A("En_An (Anju) - vanilla schedule (id 0x0085, MM)", 0x0085, 0, 0, -150) }),
    };
}
