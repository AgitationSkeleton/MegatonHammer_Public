using System.Linq;
using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

// ── Dungeon Mechanism Presets ─────────────────────────────────────────────────────────────────────
// One-click, PRE-WIRED groups of REAL vanilla actors that implement a recognisable dungeon puzzle
// (hit a switch → a gate opens, …). The whole point of the recreation plan (docs/dungeon-recreation-
// plan.md): a casual user gets a working mechanism without hand-packing params or hand-picking flags.
//
// VANILLA-COMPAT INVARIANT: a preset may ONLY emit standard actor placements + standard flag-bus
// wiring (one actor SETS a scene switch flag, another READS the same flag). Nothing here invents
// engine behaviour, so the authored level runs on unmodified OoT/MM carts AND on vanilla SoH/2Ship.
// Every actor id + param bit-layout is taken from ActorParamSchema, which is verified against the
// decomp — params are built correct-by-construction via Field.Set, never hand-packed here.

/// <summary>Build context handed to a preset: where to drop it, plus verified helpers for allocating a
/// free flag and setting an actor's schema field by name (so the preset never touches raw bits).</summary>
public sealed class PresetContext
{
    public MapDocument Doc { get; }
    public Vector3 At { get; }
    public bool IsOoT { get; }

    /// <summary>Brushes (Solids) the preset creates alongside its actors — e.g. a warp trigger pad.
    /// <see cref="DungeonMechanismPresets.Insert"/> adds and groups these with the actors.</summary>
    public List<Solid> Solids { get; } = new();

    public PresetContext(MapDocument doc, Vector3 at) { Doc = doc; At = at; IsOoT = !doc.IsMM; }

    /// <summary>An invisible WARP trigger pad (a box brush) at <see cref="At"/>+offset that, when the player
    /// walks into it, sends them to <paramref name="exitEntrance"/>. Standard scene-exit data (a trigger Solid
    /// whose faces carry the WARP tool texture) — no engine changes. The user sets the real destination after.</summary>
    public Solid WarpPad(Vector3 offset, Vector3 size, int exitEntrance)
    {
        var c = At + offset; var half = size * 0.5f;
        var pad = Solid.CreateBox(c - half, c + half);
        pad.IsTrigger = true;
        pad.ExitEntrance = exitEntrance;
        foreach (var f in pad.Faces) f.TextureName = Textures.SpecialTextures.Warp;
        Solids.Add(pad);
        return pad;
    }

    /// <summary>Lowest scene switch flag not used by any placed actor (the flag-bus channel allocator).</summary>
    public int AllocSwitchFlag() => Doc.NextFreeFlag(ActorParamSchema.FlagKind.Switch, IsOoT, 64);

    /// <summary>A new actor of <paramref name="id"/> at <see cref="At"/> + <paramref name="offset"/>,
    /// labelled from the schema. Params start at 0; set meaningful fields via <see cref="SetField"/>.</summary>
    public ZActor Make(ushort id, Vector3 offset)
    {
        var p = At + offset;
        return new ZActor
        {
            Number = id,
            XPos = p.X, YPos = p.Y, ZPos = p.Z,
            DisplayName = ActorParamSchema.For(IsOoT, id)?.Title ?? $"Actor 0x{id:X4}",
        };
    }

    /// <summary>Set a named schema field (e.g. "Switch flag") on the actor, packing into params — or into
    /// Rot Z for FromRotZ fields — using the field's verified shift/length. No-op if the field is unknown.</summary>
    public void SetField(ZActor a, string fieldName, int value)
    {
        var f = ActorParamSchema.For(IsOoT, a.Number)?.Fields.FirstOrDefault(x => x.Name == fieldName);
        if (f == null) return;
        if (f.FromRotZ)
            a.ZRot = (short)((a.ZRot & ~(f.Mask << f.Shift)) | ((value & f.Mask) << f.Shift));
        else
            a.Variable = f.Set(a.Variable, value);
    }
}

/// <summary>A named, game-scoped puzzle template. <see cref="Build"/> returns the actors to place
/// (already positioned + parameterised); <see cref="DungeonMechanismPresets.Insert"/> groups and adds them.</summary>
public sealed class MechanismPreset
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool OoT { get; init; } = true;
    public bool Mm  { get; init; }
    public Func<PresetContext, List<ZActor>> Build { get; init; } = _ => new();
}

public static class DungeonMechanismPresets
{
    // Obj_Switch types (z_obj_switch.c OBJSWITCH_TYPE): 0 Floor, 2 Eye, 3 Crystal. Subtype 0 = ONCE
    // (latches the flag permanently for this scene visit — a gate that stays open once triggered).
    private const int SwEye = 2, SwCrystal = 3, SwOnce = 0;
    private const ushort ObjSwitch = 0x012A;   // sets a scene switch flag
    private const ushort HidanKousi = 0x006F;  // grate/barrier: slides open while a switch flag is set (reader)
    private const ushort ObjSyokudai = 0x005E; // torch: sets a scene switch flag when lit
    private const ushort MmObjSwitch = 0x0093;  // MM switch: sets a scene switch flag
    private const ushort MmBgLadder  = 0x0163;  // MM ladder: hidden/non-climbable until a switch flag is set (reader)
    private const ushort EnGSwitch   = 0x0117;  // OoT silver-rupee tracker (role 0) + silver rupees (role 1)
    private const ushort EnAm        = 0x0054;  // Armos: params 0 = dormant statue (ACTOR_FLAG_26, holds floor switches)
    private const ushort HeartCont   = 0x005F;  // Item_B_Heart: boss heart-container reward
    private const int SwFloor = 0, SwHold = 2;  // Obj_Switch: FLOOR type, HOLD subtype (set while weighted)

    // A switch wired to a barred gate on a freshly-allocated switch flag. switchType picks how it's
    // triggered (Crystal = sword, Eye = arrow/slingshot) — the same setter→reader flag bus either way.
    private static Func<PresetContext, List<ZActor>> SwitchGate(int switchType, string label) => ctx =>
    {
        int flag = ctx.AllocSwitchFlag();
        ctx.Doc.SetFlagName(ActorParamSchema.FlagKind.Switch, flag, $"Gate{flag}");

        var sw = ctx.Make(ObjSwitch, new Vector3(-100, 0, 0));
        ctx.SetField(sw, "Switch type", switchType);
        ctx.SetField(sw, "Behaviour", SwOnce);
        ctx.SetField(sw, "Switch flag", flag);

        var gate = ctx.Make(HidanKousi, new Vector3(100, 0, 0));
        ctx.SetField(gate, "Switch flag", flag);

        return new List<ZActor> { sw, gate };
    };

    // Obj_Syokudai torch type (bits 12-13): 0 Permanent (starts lit, NOT player-lightable — the ignite path
    // in z_obj_syokudai.c is gated on torchType != 0), 1 Timed (player-lightable, sets its flag), 2 Decorative
    // (sets no flag). A "light it to trigger" puzzle MUST use Timed. Group size (bits 6-9) counts torches that
    // share the static sLitTorchCount; the flag sets once that many are lit. Group 1 = a single torch that
    // relights off its own flag (so it visually STAYS lit after you light it).
    private const int TorchTimed = 1;

    // A torch that sets a switch flag when lit, wired to a barred gate. Timed type (the only player-lightable
    // type that sets a flag) + group size 1 → light it once; it stays lit and the gate opens for good.
    private static List<ZActor> TorchGate(PresetContext ctx)
    {
        int flag = ctx.AllocSwitchFlag();
        ctx.Doc.SetFlagName(ActorParamSchema.FlagKind.Switch, flag, $"Torch{flag}");

        var torch = ctx.Make(ObjSyokudai, new Vector3(-100, 0, 0));
        ctx.SetField(torch, "Torch type", TorchTimed);
        ctx.SetField(torch, "Torch group size", 1);  // one-torch group: sets the flag and relights (stays lit)
        ctx.SetField(torch, "Switch flag", flag);

        var gate = ctx.Make(HidanKousi, new Vector3(100, 0, 0));
        ctx.SetField(gate, "Switch flag", flag);

        return new List<ZActor> { torch, gate };
    }

    // Light ALL N torches to open the gate — vanilla's real AND-gate. Each torch shares one switch flag and a
    // group size N; the static sLitTorchCount only sets the flag once all N are lit (z_obj_syokudai.c:220).
    private static Func<PresetContext, List<ZActor>> MultiTorchGate(int n) => ctx =>
    {
        int flag = ctx.AllocSwitchFlag();
        ctx.Doc.SetFlagName(ActorParamSchema.FlagKind.Switch, flag, $"Torches{flag}");

        var list = new List<ZActor>();
        for (int i = 0; i < n; i++)   // a row of torches
        {
            var torch = ctx.Make(ObjSyokudai, new Vector3(-140 + i * 120, 0, -120));
            ctx.SetField(torch, "Torch type", TorchTimed);
            ctx.SetField(torch, "Torch group size", n);   // flag sets only when all n are lit
            ctx.SetField(torch, "Switch flag", flag);
            list.Add(torch);
        }

        var gate = ctx.Make(HidanKousi, new Vector3(0, 0, 120));
        ctx.SetField(gate, "Switch flag", flag);
        list.Add(gate);
        return list;
    };

    // MM: a switch wired to a ladder that only appears (and becomes climbable) once the flag is set.
    // MM has no generic Fire-Temple grate; Bg_Ladder is the clean, self-contained switch-gated unlock.
    private static List<ZActor> MmSwitchLadder(PresetContext ctx)
    {
        int flag = ctx.AllocSwitchFlag();
        ctx.Doc.SetFlagName(ActorParamSchema.FlagKind.Switch, flag, $"Ladder{flag}");

        var sw = ctx.Make(MmObjSwitch, new Vector3(-100, 0, 0));
        ctx.SetField(sw, "Switch type", SwCrystal);
        ctx.SetField(sw, "Behaviour", SwOnce);       // latch permanently so the ladder stays up
        ctx.SetField(sw, "Switch flag", flag);

        var ladder = ctx.Make(MmBgLadder, new Vector3(100, 0, 0));
        ctx.SetField(ladder, "Size", 1);             // 16 rungs
        ctx.SetField(ladder, "Appear switch flag", flag);

        return new List<ZActor> { sw, ladder };
    }

    // OoT silver-rupee room: an invisible tracker + N silver rupees on one switch flag, wired to a gate.
    // Collect all N rupees → the tracker sets the flag → the gate opens. (En_G_Switch role 0 vs role 1.)
    private static List<ZActor> SilverRupeeRoom(PresetContext ctx)
    {
        const int N = 5;
        int flag = ctx.AllocSwitchFlag();
        ctx.Doc.SetFlagName(ActorParamSchema.FlagKind.Switch, flag, $"Silver{flag}");

        var list = new List<ZActor>();

        var tracker = ctx.Make(EnGSwitch, new Vector3(0, 0, 0));   // invisible counter
        ctx.SetField(tracker, "Role", 0);
        ctx.SetField(tracker, "Silver count", N);
        ctx.SetField(tracker, "Switch flag", flag);
        list.Add(tracker);

        for (int i = 0; i < N; i++)   // N collectible rupees in a ring
        {
            float ang = i * (2f * System.MathF.PI / N);
            var rupee = ctx.Make(EnGSwitch, new Vector3(System.MathF.Cos(ang) * 150f, 40f, System.MathF.Sin(ang) * 150f));
            ctx.SetField(rupee, "Role", 1);
            ctx.SetField(rupee, "Switch flag", flag);
            list.Add(rupee);
        }

        var gate = ctx.Make(HidanKousi, new Vector3(300, 0, 0));   // opens when all N are collected
        ctx.SetField(gate, "Switch flag", flag);
        list.Add(gate);

        return list;
    }

    // Push a dormant Armos statue onto a floor switch to hold it down and open a gate. The statue (En_Am
    // params 0) is a heavy ACTOR_FLAG_26 BG object, so resting on the FLOOR/HOLD switch keeps its flag set.
    private static List<ZActor> ArmosSwitchGate(PresetContext ctx)
    {
        int flag = ctx.AllocSwitchFlag();
        ctx.Doc.SetFlagName(ActorParamSchema.FlagKind.Switch, flag, $"Weight{flag}");

        var armos = ctx.Make(EnAm, new Vector3(-80, 0, 90));   // Variable 0 = dormant statue to push

        var sw = ctx.Make(ObjSwitch, new Vector3(-80, 0, 0));  // floor switch it rests on
        ctx.SetField(sw, "Switch type", SwFloor);
        ctx.SetField(sw, "Behaviour", SwHold);                 // set while weighted
        ctx.SetField(sw, "Switch flag", flag);

        var gate = ctx.Make(HidanKousi, new Vector3(120, 0, 0));
        ctx.SetField(gate, "Switch flag", flag);

        return new List<ZActor> { armos, sw, gate };
    }

    // A boss "ending": a Heart Container reward + an invisible warp pad that exits the dungeon. The warp's
    // destination entrance is a placeholder (0) for the user to set; both are standard vanilla scene data.
    private static List<ZActor> BossExit(PresetContext ctx)
    {
        var heart = ctx.Make(HeartCont, new Vector3(0, 0, 0));            // Item_B_Heart (collectible flag 0x1F)
        ctx.WarpPad(new Vector3(0, 0, 130), new Vector3(160, 80, 40), exitEntrance: 0);   // exit loading zone
        return new List<ZActor> { heart };
    }

    /// <summary>All presets (both games). Filter with <see cref="For"/>.</summary>
    public static readonly IReadOnlyList<MechanismPreset> All = new List<MechanismPreset>
    {
        new()
        {
            Id = "crystal_switch_gate", Name = "Crystal switch → gate", OoT = true, Mm = false,
            Description = "Strike a crystal switch to permanently open a barred gate. " +
                          "Obj_Switch (crystal, latch-once) sets a switch flag; the gate opens while it's set.",
            Build = SwitchGate(SwCrystal, "crystal"),
        },
        new()
        {
            Id = "eye_switch_gate", Name = "Eye switch → gate", OoT = true, Mm = false,
            Description = "Shoot an eye switch (arrow/slingshot) to open a barred gate — the ranged variant of the same wiring.",
            Build = SwitchGate(SwEye, "eye"),
        },
        new()
        {
            Id = "torch_gate", Name = "Torch → gate", OoT = true, Mm = false,
            Description = "Light a torch (Din's Fire / fire arrow / lit Deku stick) to open a barred gate. " +
                          "Obj_Syokudai sets a switch flag when lit; the gate opens while it's set.",
            Build = TorchGate,
        },
        new()
        {
            Id = "multi_torch_gate", Name = "Light all torches → gate", OoT = true, Mm = false,
            Description = "Light all 3 torches to open a barred gate — vanilla's real AND-gate. The torches share " +
                          "one switch flag and a group count; the flag only sets once every torch in the group is lit.",
            Build = MultiTorchGate(3),
        },
        new()
        {
            Id = "silver_rupee_room", Name = "Silver-rupee room → gate", OoT = true, Mm = false,
            Description = "Collect all 5 silver rupees to open a gate. An invisible tracker counts them and " +
                          "sets a switch flag; the rupees share it so they stay collected. (One silver puzzle per room.)",
            Build = SilverRupeeRoom,
        },
        new()
        {
            Id = "armos_switch_gate", Name = "Push statue onto switch → gate", OoT = true, Mm = false,
            Description = "Shove a dormant Armos statue onto a floor switch to hold it down and open a gate. " +
                          "The statue is heavy (ACTOR_FLAG_26); the switch stays pressed while it rests there.",
            Build = ArmosSwitchGate,
        },
        new()
        {
            Id = "boss_exit", Name = "Boss reward + exit", OoT = true, Mm = false,
            Description = "Drops a Heart Container reward and an invisible warp pad that leaves the dungeon. " +
                          "Set the warp's destination entrance in its properties after placing.",
            Build = BossExit,
        },
        new()
        {
            Id = "mm_switch_ladder", Name = "Switch → ladder appears", OoT = false, Mm = true,
            Description = "Strike a crystal switch to make a climbable ladder fade in. " +
                          "Obj_Switch sets a switch flag; Bg_Ladder stays hidden until it's set.",
            Build = MmSwitchLadder,
        },
    };

    /// <summary>Presets available for the current game.</summary>
    public static IEnumerable<MechanismPreset> For(bool isMM) => All.Where(p => isMM ? p.Mm : p.OoT);

    public static MechanismPreset? ById(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>Records undo, builds the preset at <paramref name="at"/>, groups multi-actor presets under
    /// one fresh group id (click any member → whole mechanism selects), adds them to the active room, and
    /// returns them (caller may resolve display names / select the group). Vanilla-only actor data.</summary>
    public static IReadOnlyList<ZActor> Insert(MapDocument doc, MechanismPreset p, Vector3 at)
    {
        doc.RecordUndo();
        var ctx = new PresetContext(doc, at);
        var actors = p.Build(ctx);
        if (actors.Count + ctx.Solids.Count > 1)   // group multi-part mechanisms as one selectable unit
        {
            int g = doc.NextGroupId();
            foreach (var a in actors)     a.GroupId = g;
            foreach (var s in ctx.Solids) s.GroupId = g;
        }
        foreach (var a in actors)     doc.AddActor(a);
        foreach (var s in ctx.Solids) doc.AddSolid(s);
        return actors;
    }
}
