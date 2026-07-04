using MegatonHammer.Editor;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Validates the schedule bytecode assembler/disassembler/VM against a REAL decomp script: En_Ah's
/// D_80BD3DB0 (z_en_ah.c). The byte offsets in that file's comments (/* 0x00 */ … /* 0x37 */) are the
/// ground truth; if our Assemble() reproduces them exactly, the branch math is correct. Run: --schedvmtest
/// </summary>
public static class ScheduleVmSelfTest
{
    public static void Run()
    {
        // En_Ah D_80BD3DB0, authored by label (target = command index). Scene/flag ids are placeholders
        // (irrelevant to byte layout); the structure + branch targets are exact.
        var prog = new ScheduleProgram { Commands = new()
        {
            /*  0 @0x00 */ new SchedCmd { Op = SchedOp.CheckNotInSceneS, SceneId = 0x0036, Target = 10 },
            /*  1 @0x04 */ new SchedCmd { Op = SchedOp.CheckNotInDayS,   Day = 3, Target = 3 },
            /*  2 @0x08 */ new SchedCmd { Op = SchedOp.RetValL,          Result = 1 },
            /*  3 @0x0B */ new SchedCmd { Op = SchedOp.CheckNotInDayS,   Day = 2, Target = 9 },
            /*  4 @0x0F */ new SchedCmd { Op = SchedOp.CheckTimeRangeS,  StartHr = 21, StartMin = 0, EndHr = 23, EndMin = 0, Target = 8 },
            /*  5 @0x15 */ new SchedCmd { Op = SchedOp.CheckWeekEventRegS, Flag = 0x4A20, Target = 7 },
            /*  6 @0x19 */ new SchedCmd { Op = SchedOp.RetValL,          Result = 1 },
            /*  7 @0x1C */ new SchedCmd { Op = SchedOp.RetNone },
            /*  8 @0x1D */ new SchedCmd { Op = SchedOp.RetValL,          Result = 3 },
            /*  9 @0x20 */ new SchedCmd { Op = SchedOp.RetNone },
            /* 10 @0x21 */ new SchedCmd { Op = SchedOp.CheckNotInSceneS, SceneId = 0x0037, Target = 16 },
            /* 11 @0x25 */ new SchedCmd { Op = SchedOp.CheckNotInDayS,   Day = 3, Target = 15 },
            /* 12 @0x29 */ new SchedCmd { Op = SchedOp.CheckTimeRangeS,  StartHr = 18, StartMin = 0, EndHr = 6, EndMin = 0, Target = 14 },
            /* 13 @0x2F */ new SchedCmd { Op = SchedOp.RetNone },
            /* 14 @0x30 */ new SchedCmd { Op = SchedOp.RetTime,          StartHr = 18, StartMin = 0, EndHr = 6, EndMin = 0, Result = 2 },
            /* 15 @0x36 */ new SchedCmd { Op = SchedOp.RetNone },
            /* 16 @0x37 */ new SchedCmd { Op = SchedOp.RetNone },
        }};

        byte[] a = prog.Assemble();
        bool ok = true;

        // 1) Total size must be 0x38 (the next byte after the last RET_NONE @0x37).
        Check(ref ok, a.Length == 0x38, $"length 0x{a.Length:X2} expected 0x38");

        // 2) Command byte starts must match the decomp comments exactly.
        int[] expectStarts = { 0x00, 0x04, 0x08, 0x0B, 0x0F, 0x15, 0x19, 0x1C, 0x1D, 0x20, 0x21, 0x25, 0x29, 0x2F, 0x30, 0x36, 0x37 };
        int acc = 0;
        for (int i = 0; i < prog.Commands.Count; i++) { Check(ref ok, acc == expectStarts[i], $"cmd #{i} @0x{acc:X2} expected 0x{expectStarts[i]:X2}"); acc += ScheduleProgram.Size(prog.Commands[i].Op); }

        // 3) Specific branch offset operands from the decomp (e.g. cmd0 = 0x21-0x04 = 0x1D).
        Check(ref ok, (sbyte)a[0x03] == 0x1D, $"cmd0 offset 0x{a[0x03]:X2} expected 0x1D");
        Check(ref ok, (sbyte)a[0x04 + 3] == (0x0B - 0x08), $"cmd1 offset {(sbyte)a[0x07]} expected 3");
        Check(ref ok, (sbyte)a[0x0F + 5] == (0x1D - 0x15), $"cmd4 offset {(sbyte)a[0x14]} expected {(0x1D - 0x15)}");

        // 4) Disassemble → reassemble must be byte-identical (round-trip).
        var prog2 = ScheduleProgram.Disassemble(a);
        byte[] b = prog2.Assemble();
        Check(ref ok, a.AsSpan().SequenceEqual(b), "round-trip bytes differ");
        Check(ref ok, prog2.Commands.Count == prog.Commands.Count, "round-trip command count differs");

        // 5) VM behaviour, traced through the real control flow:
        //    • Day 3, scene 0x36: cmd0 falls through (in scene), cmd1 "day!=3" is false → falls to RET_VAL_L(1).
        var outDay3 = prog.Simulate(new SchedInputs { Day = 3, Hour = 12, Min = 0, SceneId = 0x0036, WeekFlag = _ => false });
        Check(ref ok, outDay3.HasResult && outDay3.Result == 1, $"day3 sim got hasResult={outDay3.HasResult} result={outDay3.Result}, expected 1");

        //    • Day 1, scene 0x36: cmd1 branches (not day 3) → cmd3 branches (not day 2) → RET_NONE.
        var outDay1 = prog.Simulate(new SchedInputs { Day = 1, Hour = 12, Min = 0, SceneId = 0x0036, WeekFlag = _ => false });
        Check(ref ok, !outDay1.HasResult, $"day1 sim hasResult={outDay1.HasResult}, expected false (RET_NONE)");

        //    • Day 2, 22:00, scene 0x36: reaches the 21:00-23:00 range check → branches to RET_VAL_L(3).
        var outDay2 = prog.Simulate(new SchedInputs { Day = 2, Hour = 22, Min = 0, SceneId = 0x0036, WeekFlag = _ => false });
        Check(ref ok, outDay2.HasResult && outDay2.Result == 3, $"day2-night sim got hasResult={outDay2.HasResult} result={outDay2.Result}, expected 3");

        Console.WriteLine(ok ? "[schedvmtest] PASS — assembler matches En_Ah D_80BD3DB0 byte-for-byte; round-trip + VM OK"
                             : "[schedvmtest] FAIL");
        Console.WriteLine(prog.ToListing());
    }

    private static void Check(ref bool ok, bool cond, string msg)
    {
        if (!cond) { ok = false; Console.WriteLine("  [schedvmtest] FAIL: " + msg); }
    }
}
