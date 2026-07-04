using MegatonHammer.Editor;
using OpenTK.Mathematics;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Demo rooms for the two MM engine systems the editor now supports: the schedule bytecode VM and
/// weekEventReg cross-cycle persistence. Written alongside the wired-logic demos (foldered under their
/// own scene names) by <see cref="LogicDemoBuilder"/>.
/// </summary>
public static class MmSystemsDemoBuilder
{
    private const float R = 360f, Wall = 30f, Height = 360f;

    public static void BuildInto(string baseDir)
    {
        BuildScheduleVmDemo(baseDir);
        BuildPersistenceDemo(baseDir);
    }

    /// <summary>A clean, single-room MM scroll test: just a scrolling floor, no actors. (The old
    /// --makescrolltest reused the OoT TestTemple, whose OoT actor ids reinterpret as nonsense MM actors —
    /// Gorons/butterflies/etc. — and whose OoT door ids don't exist in MM, so it had no inter-room doors.)</summary>
    public static void BuildScrollTest(string path)
    {
        var doc = NewRoom("MM Animated Floor Test");
        var (floor, _, _) = TestTempleBuilder.ForestTextures(true);
        // Vertical tile scroll on the floor texture (U=0, V=0.5 tiles/sec).
        doc.Scene.Settings.TextureScrolls.Add(new TextureScroll(floor, 0f, 0.5f));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ProjectSerializer.Save(doc, path);
    }

    // ── Schedule VM: Anju's Mother (En_Ah 0x253) running her REAL schedule D_80BD3DB0 ────────────────
    private static void BuildScheduleVmDemo(string baseDir)
    {
        var doc = NewRoom("Schedule VM - Anjus Mother");
        var room = doc.Scene.Rooms[0];

        // En_Ah's actual schedule script (z_en_ah.c D_80BD3DB0), authored by label.
        var prog = new ScheduleProgram { Commands = new()
        {
            /*  0 */ new SchedCmd { Op = SchedOp.CheckNotInSceneS, SceneId = 0x0008, Target = 10 }, // not Inn → Stock Pot path
            /*  1 */ new SchedCmd { Op = SchedOp.CheckNotInDayS,   Day = 3, Target = 3 },
            /*  2 */ new SchedCmd { Op = SchedOp.RetValL,          Result = 1 },                     // day 3 at inn → pose 1
            /*  3 */ new SchedCmd { Op = SchedOp.CheckNotInDayS,   Day = 2, Target = 9 },
            /*  4 */ new SchedCmd { Op = SchedOp.CheckTimeRangeS,  StartHr = 21, EndHr = 23, Target = 8 },
            /*  5 */ new SchedCmd { Op = SchedOp.CheckWeekEventRegS, Flag = 0x4A20, Target = 7 },
            /*  6 */ new SchedCmd { Op = SchedOp.RetValL,          Result = 1 },
            /*  7 */ new SchedCmd { Op = SchedOp.RetNone },
            /*  8 */ new SchedCmd { Op = SchedOp.RetValL,          Result = 3 },                     // day 2, 21-23h → pose 3
            /*  9 */ new SchedCmd { Op = SchedOp.RetNone },
            /* 10 */ new SchedCmd { Op = SchedOp.CheckNotInSceneS, SceneId = 0x0009, Target = 16 },
            /* 11 */ new SchedCmd { Op = SchedOp.CheckNotInDayS,   Day = 3, Target = 15 },
            /* 12 */ new SchedCmd { Op = SchedOp.CheckTimeRangeS,  StartHr = 18, EndHr = 6, Target = 14 },
            /* 13 */ new SchedCmd { Op = SchedOp.RetNone },
            /* 14 */ new SchedCmd { Op = SchedOp.RetTime,          StartHr = 18, EndHr = 6, Result = 2 },
            /* 15 */ new SchedCmd { Op = SchedOp.RetNone },
            /* 16 */ new SchedCmd { Op = SchedOp.RetNone },
        }};

        room.Actors.Add(new ZActor
        {
            Number = 0x0253, DisplayName = "En_Ah (Anju's Mother)",
            XPos = 0, YPos = 0, ZPos = 0,
            ScheduleVm = prog,
            // result indexes here: 1 = behind the counter, 2 = (RET_TIME demo) doorway, 3 = night spot.
            SchedulePoses = new()
            {
                new SchedulePose { X = 0,    Y = 0, Z = 0,    Yaw = 0 },                       // 0 (unused result)
                new SchedulePose { X = 0,    Y = 0, Z = -120,  Yaw = 0 },                       // 1 day-3 counter
                new SchedulePose { X = 160,  Y = 0, Z = 0,     Yaw = unchecked((short)0xC000) },// 2 RET_TIME doorway
                new SchedulePose { X = -160, Y = 0, Z = 120,   Yaw = unchecked((short)0x4000) },// 3 night spot
            },
        });

        string dir = Path.Combine(baseDir, "MM Schedule VM");
        Directory.CreateDirectory(dir);
        ProjectSerializer.Save(doc, Path.Combine(dir, "Anjus Mother schedule.mhproj"));
        File.WriteAllText(Path.Combine(dir, "Anjus Mother schedule.txt"), ScheduleVmDoc(prog));
    }

    private static string ScheduleVmDoc(ScheduleProgram prog) =>
($@"MM SCHEDULE BYTECODE VM — Anju's Mother (En_Ah, actor 0x0253)
============================================================

WHAT IT IS
  Majora's Mask NPCs decide where they are / what they do by running a tiny BYTECODE SCRIPT through the
  engine's Schedule_RunScript (decomp: z_schedule.c). The script is a list of CHECK commands (each
  branches when its condition holds) ending in a RET that yields a result. In vanilla this bytecode is
  compiled into the actor's overlay; Megaton Hammer lets you AUTHOR it per placed actor instead.

THE SCRIPT (this is En_Ah's real vanilla schedule, D_80BD3DB0):
{prog.ToListing()}
HOW IT RUNS
  Each frame the engine runs the script with the current day, clock, scene and weekEventReg flags:
    • CHECK_NOT_IN_SCENE / NOT_IN_DAY / TIME_RANGE / BEFORE_TIME / WEEK_EVENT_REG / MISC — branch if true.
    • RET_VAL n        → result = n
    • RET_TIME a-b val → result = val plus a time window
    • RET_NONE         → ""not present right now""
  The editor compiles this to the exact engine u8[] (validated byte-for-byte against the game) and the
  2Ship fork runs it via the engine's own Schedule_RunScript. The returned result indexes the actor's
  POSE TABLE (result 1 → pose 1, result 3 → pose 3, …); RET_NONE hides the NPC. You can also SIMULATE
  the schedule in-editor (set a day/time/scene and see the output) before ever launching the game.

WHERE IT'S USED
  Every scheduled MM townsperson (Anju, her mother, the Bombers, the Mayor, Kafei, the Business Scrub,
  Romani, …). This is the backbone of MM's living-town clock: the same NPC is in different places, or
  absent, depending on day + time, all driven by one of these scripts.
");

    // ── weekEventReg cross-cycle persistence ─────────────────────────────────────────────────────────
    private static void BuildPersistenceDemo(string baseDir)
    {
        var doc = NewRoom("Cross-Cycle Persistence");
        var s = doc.Scene.Settings;

        // Start with a heart piece + a wallet upgrade already earned, and mark them persistent so they
        // SURVIVE a Song-of-Time reset (these are real persistent flags in the vanilla table).
        int RECEIVED_BANK_WALLET_UPGRADE = (10 << 8) | 0x04;   // WEEKEVENTREG_RECEIVED_BANK_WALLET_UPGRADE (byte 10)
        int RECEIVED_EVAN_HEART_PIECE    = (39 << 8) | 0x40;   // byte 39
        int A_NON_PERSISTENT_STORY_FLAG  = (5 << 8)  | 0x01;   // a transient story bit (cleared each cycle)

        s.StartWeekEvents = new() { RECEIVED_BANK_WALLET_UPGRADE, RECEIVED_EVAN_HEART_PIECE, A_NON_PERSISTENT_STORY_FLAG };
        s.PersistentWeekEvents = new() { RECEIVED_BANK_WALLET_UPGRADE, RECEIVED_EVAN_HEART_PIECE };

        string dir = Path.Combine(baseDir, "MM Cross-Cycle Persistence");
        Directory.CreateDirectory(dir);
        ProjectSerializer.Save(doc, Path.Combine(dir, "Cross-cycle persistence.mhproj"));
        File.WriteAllText(Path.Combine(dir, "Cross-cycle persistence.txt"), PersistenceDoc());
    }

    private static string PersistenceDoc() =>
@"MM weekEventReg CROSS-CYCLE PERSISTENCE
=======================================

WHAT IT IS
  weekEventReg is MM's 800-flag world-state register (gSaveContext...weekEventReg[100]). When Link plays
  the Song of Time the 3-day cycle RESETS and MOST of these flags are wiped — that's why a sidequest you
  half-finished is undone. But SOME flags must survive (heart pieces, wallet/quiver upgrades, bottles,
  permanent map purchases). The decomp controls this with a table, sPersistentCycleWeekEventRegs
  (z_sram_NES.c): each flag has a 2-bit slot; non-zero = ""don't clear on reset"".

WHAT THIS DEMO DOES
  Scene settings author two lists (Properties ▸ MM playtest):
    • StartWeekEvents       — flags pre-set when the scene boots.
    • PersistentWeekEvents  — flags that SURVIVE a Song-of-Time reset.
  Here the scene starts with a Bank wallet upgrade, Evan's heart piece, and one transient story flag.
  The two upgrades are marked persistent; the story flag is not. After a cycle reset in-game the upgrades
  remain set and the story flag is cleared — exactly matching vanilla behaviour.

HOW IT WORKS (engine side)
  The 2Ship fork OR's each PersistentWeekEvents flag into the engine's OWN sPersistentCycleWeekEventRegs
  table at boot — slot = (3 << (2 * shiftOfMask)) for byte (flag>>8). Because we extend the engine's real
  table, MM's stock reset code preserves them; no custom reset hook is needed. A flag value is
  PACK_WEEKEVENTREG_FLAG(byteIndex, bitMask) = (byteIndex<<8) | bitMask.

WHERE IT'S USED
  Any MM scene where authored progress must outlive the 3-day loop: a temple stays cleared, a traded
  item stays delivered, a one-time reward stays earned.
";

    private static MapDocument NewRoom(string title)
    {
        var doc = new MapDocument();
        doc.InitGameDefaults(true);
        var scene = doc.Scene;
        scene.Name = title; scene.Rooms[0].Name = title; scene.Settings.AreaName = title;
        var room = scene.Rooms[0];
        var (floor, wall, ceil) = TestTempleBuilder.ForestTextures(true);
        Box(room, (-R, -30, -R), (R, 0, R), floor);
        Box(room, (-R, Height, -R), (R, Height + 30, R), ceil);
        Box(room, (-R, 0, -R), (-R + Wall, Height, R), wall);
        Box(room, (R - Wall, 0, -R), (R, Height, R), wall);
        Box(room, (-R, 0, -R), (R, Height, -R + Wall), wall);
        Box(room, (-R, 0, R - Wall), (R, Height, R), wall);
        scene.Settings.SpawnPos = new Vector3(0, 0, R - 80);
        scene.Settings.SpawnYaw = unchecked((short)0x8000);
        return doc;
    }

    private static void Box(ZRoom room, (float x, float y, float z) lo, (float x, float y, float z) hi, string tex)
    {
        var sld = Solid.CreateBox(new Vector3(lo.x, lo.y, lo.z), new Vector3(hi.x, hi.y, hi.z));
        foreach (var f in sld.Faces) f.TextureName = tex;
        room.Geometry.Add(sld);
    }
}
