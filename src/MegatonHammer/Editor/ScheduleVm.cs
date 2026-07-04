using System.Text;

namespace MegatonHammer.Editor;

/// <summary>
/// The real Majora's Mask NPC <b>schedule bytecode VM</b> (decomp: <c>z_schedule.c</c> / <c>z64schedule.h</c>),
/// authored in the editor instead of being baked into an actor overlay.
///
/// A schedule is a <c>u8[]</c> script the engine runs via <c>Schedule_RunScript</c>: a sequence of CHECK
/// commands (each branches by a relative byte offset when its condition holds) terminated by a RET command
/// that yields a <see cref="SchedOutput"/> (a value, a time range, or "nothing"). Checks test the clock,
/// the day, the scene, weekEventReg flags, or a few item/mask conditions.
///
/// This file is the complete, decomp-faithful system: a <see cref="ScheduleProgram"/> model (branches by
/// label, i.e. a target command index, so the author never hand-computes byte offsets), an
/// <see cref="Assemble"/> compiler that emits the exact bytecode the engine consumes, a
/// <see cref="Disassemble"/> reader (round-trips real game scripts), and a <see cref="Simulate"/> interpreter
/// that mirrors <c>Schedule_RunScript</c> so a schedule can be authored AND tested entirely in-editor.
///
/// Branch math (validated against En_Ah's D_80BD3DB0): when a CHECK branches, the engine does
/// <c>*script += offset</c> then the run loop does <c>script += cmdSize</c>, so the byte target of a branch
/// at byte B with size S is <c>B + S + offset</c>; hence <c>offset = target - (B + S)</c>.
/// </summary>
public enum SchedOp : byte
{
    CheckWeekEventRegS = 0x00,
    CheckWeekEventRegL = 0x01,
    CheckTimeRangeS    = 0x02,
    CheckTimeRangeL    = 0x03,
    RetValL            = 0x04,
    RetNone            = 0x05,
    RetEmpty           = 0x06,
    Nop                = 0x07,
    CheckMiscS         = 0x08,
    RetValS            = 0x09,
    CheckNotInSceneS   = 0x0A,
    CheckNotInSceneL   = 0x0B,
    CheckNotInDayS     = 0x0C,
    CheckNotInDayL     = 0x0D,
    RetTime            = 0x0E,
    CheckBeforeTimeS   = 0x0F,
    CheckBeforeTimeL   = 0x10,
    BranchS            = 0x11,
    BranchL            = 0x12,
}

/// <summary>One schedule command. Operand meaning depends on <see cref="Op"/> (see field comments).
/// Branch commands store <see cref="Target"/> = the index of the command to jump to when the check passes
/// (or for the unconditional BRANCH ops); -1 = "fall through to the next command".</summary>
public sealed class SchedCmd
{
    public SchedOp Op { get; set; }

    // Operands (only the ones relevant to Op are used; see EncodeOperands/DecodeOperands).
    public int Flag { get; set; }      // CHECK_WEEK_EVENT_REG: PACK_WEEKEVENTREG_FLAG = (byteIndex<<8)|bitMask
    public int StartHr { get; set; }   // CHECK_TIME_RANGE / RET_TIME / CHECK_BEFORE_TIME
    public int StartMin { get; set; }
    public int EndHr { get; set; }
    public int EndMin { get; set; }
    public int Which { get; set; }     // CHECK_MISC: 0=room key, 1=letter to Kafei, 2=Romani mask
    public int SceneId { get; set; }   // CHECK_NOT_IN_SCENE
    public int Day { get; set; }       // CHECK_NOT_IN_DAY (1/2/3)
    public int Result { get; set; }    // RET_VAL / RET_TIME result value
    public int Nop0 { get; set; }
    public int Nop1 { get; set; }
    public int Nop2 { get; set; }

    public int Target { get; set; } = -1;  // branch destination command index, -1 = none

    public SchedCmd Clone() => (SchedCmd)MemberwiseClone();
}

/// <summary>Result of running a schedule, mirroring the engine's <c>ScheduleOutput</c>.</summary>
public struct SchedOutput
{
    public bool HasResult;
    public int Result;          // RET_VAL_S/L or RET_TIME result byte
    public int Time0, Time1;    // RET_TIME: packed u16 times (only when the RET was RET_TIME)
    public bool IsTime;         // true when produced by RET_TIME
}

/// <summary>A complete authored schedule = an ordered list of commands. Branch targets are command
/// indices, resolved to byte offsets at <see cref="Assemble"/> time.</summary>
public sealed class ScheduleProgram
{
    public List<SchedCmd> Commands { get; set; } = new();

    public ScheduleProgram Clone() => new() { Commands = Commands.Select(c => c.Clone()).ToList() };

    public bool IsEmpty => Commands.Count == 0;

    // ── command sizes (bytes), indexed by opcode; mirrors sScheduleCmdSizes ──────────────────────────
    public static int Size(SchedOp op) => op switch
    {
        SchedOp.CheckWeekEventRegS => 4,
        SchedOp.CheckWeekEventRegL => 5,
        SchedOp.CheckTimeRangeS    => 6,
        SchedOp.CheckTimeRangeL    => 7,
        SchedOp.RetValL            => 3,
        SchedOp.RetNone            => 1,
        SchedOp.RetEmpty           => 1,
        SchedOp.Nop                => 4,
        SchedOp.CheckMiscS         => 3,
        SchedOp.RetValS            => 2,
        SchedOp.CheckNotInSceneS   => 4,
        SchedOp.CheckNotInSceneL   => 5,
        SchedOp.CheckNotInDayS     => 4,
        SchedOp.CheckNotInDayL     => 5,
        SchedOp.RetTime            => 6,
        SchedOp.CheckBeforeTimeS   => 4,
        SchedOp.CheckBeforeTimeL   => 5,
        SchedOp.BranchS            => 2,
        SchedOp.BranchL            => 3,
        _ => 1,
    };

    public static bool IsLongBranch(SchedOp op) => op is SchedOp.CheckWeekEventRegL or SchedOp.CheckTimeRangeL
        or SchedOp.CheckNotInSceneL or SchedOp.CheckNotInDayL or SchedOp.CheckBeforeTimeL or SchedOp.BranchL;

    public static bool HasBranch(SchedOp op) => op is SchedOp.CheckWeekEventRegS or SchedOp.CheckWeekEventRegL
        or SchedOp.CheckTimeRangeS or SchedOp.CheckTimeRangeL or SchedOp.CheckMiscS or SchedOp.CheckNotInSceneS
        or SchedOp.CheckNotInSceneL or SchedOp.CheckNotInDayS or SchedOp.CheckNotInDayL or SchedOp.CheckBeforeTimeS
        or SchedOp.CheckBeforeTimeL or SchedOp.BranchS or SchedOp.BranchL;

    // ── assemble: commands → engine bytecode ────────────────────────────────────────────────────────
    /// <summary>Compile to the exact <c>u8[]</c> the engine's <c>Schedule_RunScript</c> consumes.</summary>
    public byte[] Assemble()
    {
        // Pass 1: byte offset of each command.
        var byteAt = new int[Commands.Count + 1];
        for (int i = 0; i < Commands.Count; i++) byteAt[i + 1] = byteAt[i] + Size(Commands[i].Op);
        int total = byteAt[Commands.Count];

        // Pass 2: emit.
        var outp = new byte[total];
        int p = 0;
        for (int i = 0; i < Commands.Count; i++)
        {
            var c = Commands[i];
            int b = byteAt[i], size = Size(c.Op);
            outp[p++] = (byte)c.Op;
            switch (c.Op)
            {
                case SchedOp.CheckWeekEventRegS:
                    outp[p++] = (byte)(c.Flag >> 8); outp[p++] = (byte)c.Flag;
                    outp[p++] = (byte)(sbyte)BranchOffset(c, b, size, byteAt);
                    break;
                case SchedOp.CheckWeekEventRegL:
                    outp[p++] = (byte)(c.Flag >> 8); outp[p++] = (byte)c.Flag;
                    WriteS16(outp, ref p, BranchOffset(c, b, size, byteAt));
                    break;
                case SchedOp.CheckTimeRangeS:
                    outp[p++] = (byte)c.StartHr; outp[p++] = (byte)c.StartMin;
                    outp[p++] = (byte)c.EndHr; outp[p++] = (byte)c.EndMin;
                    outp[p++] = (byte)(sbyte)BranchOffset(c, b, size, byteAt);
                    break;
                case SchedOp.CheckTimeRangeL:
                    outp[p++] = (byte)c.StartHr; outp[p++] = (byte)c.StartMin;
                    outp[p++] = (byte)c.EndHr; outp[p++] = (byte)c.EndMin;
                    WriteS16(outp, ref p, BranchOffset(c, b, size, byteAt));
                    break;
                case SchedOp.RetValL:
                    outp[p++] = (byte)(c.Result >> 8); outp[p++] = (byte)c.Result; break;
                case SchedOp.RetNone:
                case SchedOp.RetEmpty:
                    break;
                case SchedOp.Nop:
                    outp[p++] = (byte)c.Nop0; outp[p++] = (byte)c.Nop1; outp[p++] = (byte)c.Nop2; break;
                case SchedOp.CheckMiscS:
                    outp[p++] = (byte)c.Which; outp[p++] = (byte)(sbyte)BranchOffset(c, b, size, byteAt); break;
                case SchedOp.RetValS:
                    outp[p++] = (byte)c.Result; break;
                case SchedOp.CheckNotInSceneS:
                    outp[p++] = (byte)(c.SceneId >> 8); outp[p++] = (byte)c.SceneId;
                    outp[p++] = (byte)(sbyte)BranchOffset(c, b, size, byteAt); break;
                case SchedOp.CheckNotInSceneL:
                    outp[p++] = (byte)(c.SceneId >> 8); outp[p++] = (byte)c.SceneId;
                    WriteS16(outp, ref p, BranchOffset(c, b, size, byteAt)); break;
                case SchedOp.CheckNotInDayS:
                    outp[p++] = (byte)(c.Day >> 8); outp[p++] = (byte)c.Day;
                    outp[p++] = (byte)(sbyte)BranchOffset(c, b, size, byteAt); break;
                case SchedOp.CheckNotInDayL:
                    outp[p++] = (byte)(c.Day >> 8); outp[p++] = (byte)c.Day;
                    WriteS16(outp, ref p, BranchOffset(c, b, size, byteAt)); break;
                case SchedOp.RetTime:
                    outp[p++] = (byte)c.StartHr; outp[p++] = (byte)c.StartMin;
                    outp[p++] = (byte)c.EndHr; outp[p++] = (byte)c.EndMin; outp[p++] = (byte)c.Result; break;
                case SchedOp.CheckBeforeTimeS:
                    outp[p++] = (byte)c.StartHr; outp[p++] = (byte)c.StartMin;
                    outp[p++] = (byte)(sbyte)BranchOffset(c, b, size, byteAt); break;
                case SchedOp.CheckBeforeTimeL:
                    outp[p++] = (byte)c.StartHr; outp[p++] = (byte)c.StartMin;
                    WriteS16(outp, ref p, BranchOffset(c, b, size, byteAt)); break;
                case SchedOp.BranchS:
                    outp[p++] = (byte)(sbyte)BranchOffset(c, b, size, byteAt); break;
                case SchedOp.BranchL:
                    WriteS16(outp, ref p, BranchOffset(c, b, size, byteAt)); break;
            }
        }
        return outp;
    }

    private int BranchOffset(SchedCmd c, int b, int size, int[] byteAt)
    {
        if (c.Target < 0 || c.Target > Commands.Count) return 0;
        int targetByte = byteAt[c.Target];
        return targetByte - (b + size);   // engine: target = b + size + offset
    }

    private static void WriteS16(byte[] o, ref int p, int v)
    {
        short s = (short)v; o[p++] = (byte)(s >> 8); o[p++] = (byte)s;
    }

    // ── disassemble: engine bytecode → commands (round-trips real game scripts) ───────────────────────
    public static ScheduleProgram Disassemble(byte[] script)
    {
        // Pass 1: collect command byte boundaries.
        var prog = new ScheduleProgram();
        var byteToIndex = new Dictionary<int, int>();
        var starts = new List<int>();
        int q = 0;
        while (q < script.Length)
        {
            var op = (SchedOp)script[q];
            byteToIndex[q] = starts.Count; starts.Add(q);
            q += Size(op);
        }
        byteToIndex[q] = starts.Count;  // end sentinel

        // Pass 2: decode operands; resolve branch byte target → command index.
        for (int i = 0; i < starts.Count; i++)
        {
            int b = starts[i]; var op = (SchedOp)script[b]; int size = Size(op);
            var c = new SchedCmd { Op = op };
            int o = b + 1;
            switch (op)
            {
                case SchedOp.CheckWeekEventRegS:
                    c.Flag = (script[o] << 8) | script[o + 1]; c.Target = Resolve(b, size, (sbyte)script[o + 2], byteToIndex); break;
                case SchedOp.CheckWeekEventRegL:
                    c.Flag = (script[o] << 8) | script[o + 1]; c.Target = Resolve(b, size, S16(script, o + 2), byteToIndex); break;
                case SchedOp.CheckTimeRangeS:
                    c.StartHr = script[o]; c.StartMin = script[o + 1]; c.EndHr = script[o + 2]; c.EndMin = script[o + 3];
                    c.Target = Resolve(b, size, (sbyte)script[o + 4], byteToIndex); break;
                case SchedOp.CheckTimeRangeL:
                    c.StartHr = script[o]; c.StartMin = script[o + 1]; c.EndHr = script[o + 2]; c.EndMin = script[o + 3];
                    c.Target = Resolve(b, size, S16(script, o + 4), byteToIndex); break;
                case SchedOp.RetValL: c.Result = (script[o] << 8) | script[o + 1]; break;
                case SchedOp.RetNone: case SchedOp.RetEmpty: break;
                case SchedOp.Nop: c.Nop0 = script[o]; c.Nop1 = script[o + 1]; c.Nop2 = script[o + 2]; break;
                case SchedOp.CheckMiscS: c.Which = script[o]; c.Target = Resolve(b, size, (sbyte)script[o + 1], byteToIndex); break;
                case SchedOp.RetValS: c.Result = script[o]; break;
                case SchedOp.CheckNotInSceneS:
                    c.SceneId = (script[o] << 8) | script[o + 1]; c.Target = Resolve(b, size, (sbyte)script[o + 2], byteToIndex); break;
                case SchedOp.CheckNotInSceneL:
                    c.SceneId = (script[o] << 8) | script[o + 1]; c.Target = Resolve(b, size, S16(script, o + 2), byteToIndex); break;
                case SchedOp.CheckNotInDayS:
                    c.Day = (script[o] << 8) | script[o + 1]; c.Target = Resolve(b, size, (sbyte)script[o + 2], byteToIndex); break;
                case SchedOp.CheckNotInDayL:
                    c.Day = (script[o] << 8) | script[o + 1]; c.Target = Resolve(b, size, S16(script, o + 2), byteToIndex); break;
                case SchedOp.RetTime:
                    c.StartHr = script[o]; c.StartMin = script[o + 1]; c.EndHr = script[o + 2]; c.EndMin = script[o + 3]; c.Result = script[o + 4]; break;
                case SchedOp.CheckBeforeTimeS:
                    c.StartHr = script[o]; c.StartMin = script[o + 1]; c.Target = Resolve(b, size, (sbyte)script[o + 2], byteToIndex); break;
                case SchedOp.CheckBeforeTimeL:
                    c.StartHr = script[o]; c.StartMin = script[o + 1]; c.Target = Resolve(b, size, S16(script, o + 2), byteToIndex); break;
                case SchedOp.BranchS: c.Target = Resolve(b, size, (sbyte)script[o], byteToIndex); break;
                case SchedOp.BranchL: c.Target = Resolve(b, size, S16(script, o), byteToIndex); break;
            }
            prog.Commands.Add(c);
        }
        return prog;
    }

    private static int Resolve(int b, int size, int offset, Dictionary<int, int> byteToIndex)
        => byteToIndex.TryGetValue(b + size + offset, out int idx) ? idx : -1;

    private static short S16(byte[] s, int o) => (short)((s[o] << 8) | s[o + 1]);

    // ── simulate: mirror Schedule_RunScript so a schedule can be tested in-editor ─────────────────────
    /// <summary>Run the schedule under the given world state. <paramref name="weekFlagSet"/> tests a packed
    /// weekEventReg flag ((byte&lt;&lt;8)|mask). Returns the same output the engine would produce.</summary>
    public SchedOutput Simulate(SchedInputs s, List<string>? trace = null)
        => SimulateBytes(Assemble(), s, trace);

    public static SchedOutput SimulateBytes(byte[] script, SchedInputs s, List<string>? trace = null)
    {
        var output = new SchedOutput();
        int pc = 0, guard = 0;
        ushort now = ToTime(s.Hour, s.Min);
        while (pc < script.Length && guard++ < 4096)
        {
            var op = (SchedOp)script[pc]; int size = Size(op); int o = pc + 1; bool branch = false; bool stop = false;
            switch (op)
            {
                case SchedOp.CheckWeekEventRegS:
                case SchedOp.CheckWeekEventRegL:
                    branch = s.WeekFlag != null && s.WeekFlag((script[o] << 8) | script[o + 1]); break;
                case SchedOp.CheckTimeRangeS:
                case SchedOp.CheckTimeRangeL:
                {
                    ushort start = ToTime(script[o], script[o + 1]);
                    ushort end = (ushort)(ToTime(script[o + 2], script[o + 3]) - 1);
                    branch = start <= now && now <= end; break;
                }
                case SchedOp.RetValL: output.Result = ((script[o] << 8) | script[o + 1]) & 0xFF; output.HasResult = true; stop = true; break;
                case SchedOp.RetNone: output.HasResult = false; stop = true; break;
                case SchedOp.RetEmpty: output.HasResult = true; stop = true; break;
                case SchedOp.Nop: break;
                case SchedOp.CheckMiscS:
                {
                    int which = script[o];
                    branch = (which == 0 && s.HasRoomKey) || (which == 1 && s.HasLetterToKafei) || (which == 2 && s.HasRomaniMask);
                    break;
                }
                case SchedOp.RetValS: output.Result = script[o]; output.HasResult = true; stop = true; break;
                case SchedOp.CheckNotInSceneS:
                case SchedOp.CheckNotInSceneL: branch = ((script[o] << 8) | script[o + 1]) != s.SceneId; break;
                case SchedOp.CheckNotInDayS:
                case SchedOp.CheckNotInDayL: branch = ((script[o] << 8) | script[o + 1]) != s.Day; break;
                case SchedOp.RetTime:
                    output.Time0 = ToTime(script[o], script[o + 1]);
                    output.Time1 = (ushort)(ToTime(script[o + 2], script[o + 3]) - 1);
                    output.Result = script[o + 4]; output.IsTime = true; output.HasResult = true; stop = true; break;
                case SchedOp.CheckBeforeTimeS:
                case SchedOp.CheckBeforeTimeL: branch = now < ToTime(script[o], script[o + 1]); break;
                case SchedOp.BranchS:
                case SchedOp.BranchL: branch = true; break;
            }
            trace?.Add($"@0x{pc:X2} {op}{(branch ? " → branch" : "")}{(stop ? " (stop)" : "")}");
            if (stop) break;
            if (branch)
            {
                int off = IsLongBranch(op)
                    ? (op == SchedOp.BranchL ? S16(script, o) : S16(script, o + size - 3))
                    : (sbyte)script[pc + size - 1];
                pc += size + off;
            }
            else pc += size;
        }
        return output;
    }

    /// <summary>Engine time domain: a day is 0..0xFFFF; SCRIPT_CALC_TIME(hr,min) = (hr*60+min)*0x10000/1440.</summary>
    public static ushort ToTime(int hr, int min) => (ushort)(((hr * 60 + min) * 0x10000) / 1440);

    // ── pretty-print (for docs / disassembly listings) ───────────────────────────────────────────────
    public string ToListing()
    {
        var byteAt = new int[Commands.Count + 1];
        for (int i = 0; i < Commands.Count; i++) byteAt[i + 1] = byteAt[i] + Size(Commands[i].Op);
        var sb = new StringBuilder();
        for (int i = 0; i < Commands.Count; i++)
        {
            var c = Commands[i];
            sb.Append($"/* 0x{byteAt[i]:X2} */ {c.Op}");
            switch (c.Op)
            {
                case SchedOp.CheckWeekEventRegS: case SchedOp.CheckWeekEventRegL:
                    sb.Append($"(flag byte {c.Flag >> 8} mask 0x{c.Flag & 0xFF:X2} → #{c.Target})"); break;
                case SchedOp.CheckTimeRangeS: case SchedOp.CheckTimeRangeL:
                    sb.Append($"({c.StartHr:D2}:{c.StartMin:D2}-{c.EndHr:D2}:{c.EndMin:D2} → #{c.Target})"); break;
                case SchedOp.CheckBeforeTimeS: case SchedOp.CheckBeforeTimeL:
                    sb.Append($"(before {c.StartHr:D2}:{c.StartMin:D2} → #{c.Target})"); break;
                case SchedOp.CheckNotInSceneS: case SchedOp.CheckNotInSceneL:
                    sb.Append($"(scene!=0x{c.SceneId:X2} → #{c.Target})"); break;
                case SchedOp.CheckNotInDayS: case SchedOp.CheckNotInDayL:
                    sb.Append($"(day!={c.Day} → #{c.Target})"); break;
                case SchedOp.CheckMiscS: sb.Append($"(which={c.Which} → #{c.Target})"); break;
                case SchedOp.BranchS: case SchedOp.BranchL: sb.Append($"(→ #{c.Target})"); break;
                case SchedOp.RetValS: case SchedOp.RetValL: sb.Append($"({c.Result})"); break;
                case SchedOp.RetTime: sb.Append($"({c.StartHr:D2}:{c.StartMin:D2}-{c.EndHr:D2}:{c.EndMin:D2} val {c.Result})"); break;
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

/// <summary>World state fed to <see cref="ScheduleProgram.Simulate"/>.</summary>
public struct SchedInputs
{
    public int Day;          // 1/2/3
    public int Hour, Min;    // 24h clock
    public int SceneId;      // MM scene id
    public bool HasRoomKey, HasLetterToKafei, HasRomaniMask;
    public Func<int, bool>? WeekFlag;   // predicate on packed (byte<<8)|mask
}
