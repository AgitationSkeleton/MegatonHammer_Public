using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

public sealed class ZActor
{
    public ushort Number   { get; set; }
    public ushort Variable { get; set; }

    /// <summary>MM only: the top-3 spawn-condition bits (0x2000/0x4000/0x8000) of the actor id word —
    /// time-of-day / event gating. Parsed off the id on import and re-OR'd into the id word on export
    /// so MM condition-gated actors keep spawning under the right conditions (vanilla round-trip).</summary>
    public ushort IdFlags  { get; set; }

    public float XPos { get; set; }
    public float YPos { get; set; }
    public float ZPos { get; set; }

    // Zelda binary angles (0x0000–0xFFFF ≈ 0–360°)
    public short XRot { get; set; }
    public short YRot { get; set; }
    public short ZRot { get; set; }

    public bool IsSelected { get; set; }

    /// <summary>
    /// True when the editor doesn't recognise this actor id (imported from a ROM but absent
    /// from the actor database). Rendered as an "obsolete entity" placeholder and preserved
    /// verbatim on save so unknown placements are never silently dropped. (D7)
    /// </summary>
    public bool IsObsolete { get; set; }

    // Resolved from ActorDatabase; set after creation
    public string DisplayName { get; set; } = "Unknown";

    /// <summary>Optional editor-side label (Hammer's "targetname"). Used by Find Entities,
    /// the Entity Report, and Paste Special's unique-naming. Not part of the N64 actor data.</summary>
    public string? Name { get; set; }

    /// <summary>Editor-only props (e.g. the Player Start scale dummy): drawn and selectable/movable in
    /// the viewports but NEVER written to a room actor list or the compiled scene. <see cref="IsSpawn"/>
    /// additionally syncs its placement back to the scene's spawn settings when moved.</summary>
    public bool IsEditorOnly { get; set; }
    public bool IsSpawn      { get; set; }

    /// <summary>Hammer group id (0 = ungrouped). Clicking any member selects the whole group.</summary>
    public int GroupId    { get; set; }
    /// <summary>Hammer visgroup id (0 = none/always-visible). Hidden when its visgroup is toggled off.</summary>
    public int VisGroupId { get; set; }

    /// <summary>True if this is a transition actor (a door / room-or-scene loading plane). Exported
    /// as a scene 0x0E entry, NOT a room 0x01 actor. The four side bytes link the two rooms/cameras
    /// it bridges (0xFF = "scene exit, not a room").</summary>
    public bool IsTransition { get; set; }
    public byte FrontRoom   { get; set; } = 0xFF;
    public byte FrontEffect { get; set; } = 0xFF;
    public byte BackRoom    { get; set; } = 0xFF;
    public byte BackEffect  { get; set; } = 0xFF;

    /// <summary>Locked-door lock side. Vanilla draws the small-key/boss lock on the door's local +Z (its
    /// front, set by the door's facing). A door works from both sides (EnDoor_Idle handles ±Z), so putting
    /// the lock on the OTHER side just means facing the door the other way — exported as a 180° rotation of
    /// the door's Y so it stays vanilla-compatible. True = lock on the back (−Z) side. Only meaningful for a
    /// locked En_Door / a KEY/BOSS Door_Shutter.</summary>
    public bool LockBack { get; set; }

    /// <summary>Y rotation as exported. Vanilla always draws a door's lock on its local +Z, and the door
    /// opens from either side, so a LockBack door is just the same door facing the other way — a 180° Y flip
    /// (0x8000 binary angle). Non-LockBack actors export their rotation unchanged.</summary>
    public short ExportYRot => LockBack ? (short)(YRot + 0x8000) : YRot;

    // The 180° flip reverses which physical side maps to which room (z_player.c picks the destination by
    // sides[(doorDirection > 0) ? 0 : 1], and doorDirection is the player's side in the door's LOCAL frame).
    // So a LockBack door must also SWAP its front/back sides to keep the same rooms — net: identical
    // transition, lock on the opposite side. Non-LockBack actors export their sides unchanged.
    public byte ExportFrontRoom   => LockBack ? BackRoom   : FrontRoom;
    public byte ExportFrontEffect => LockBack ? BackEffect : FrontEffect;
    public byte ExportBackRoom    => LockBack ? FrontRoom  : BackRoom;
    public byte ExportBackEffect  => LockBack ? FrontEffect : BackEffect;

    /// <summary>MM NPC schedule (editor + custom-engine convention, never compiled into vanilla scene
    /// data): a list of "be at position P facing Yaw during time window [Start,End] on Day D" rules.
    /// Emitted into the playtest mod O2R as <c>mh/schedules</c>; the 2Ship fork applies it by overriding
    /// the actor's position/facing by the in-game clock. Null/empty = the actor uses its own AI.</summary>
    public List<ScheduleRule>? Schedule { get; set; }

    /// <summary>MM only: a real <b>schedule bytecode VM</b> program (decomp <c>z_schedule.c</c>) authored in
    /// the editor. Compiled to engine bytecode on export and run by the 2Ship fork via the engine's own
    /// <c>Schedule_RunScript</c>; its result selects one of <see cref="SchedulePoses"/> (or hides the actor
    /// when the script returns nothing). Null = no VM schedule. Distinct from the simpler <see cref="Schedule"/>
    /// position-rule layer.</summary>
    public ScheduleProgram? ScheduleVm { get; set; }

    /// <summary>Poses the <see cref="ScheduleVm"/> result indexes into (result 0 → pose 0, …). Each pose is
    /// (X,Y,Z,Yaw). Empty = the actor isn't moved (the VM result is still observable in logs).</summary>
    public List<SchedulePose>? SchedulePoses { get; set; }

    /// <summary>Door-type actors that MUST be spawned from the scene's transition-actor list (0x0E),
    /// not the room actor list (0x01): their draw code indexes play->transiActorCtx.list[params>>10]
    /// (z_en_door.c / z_door_shutter.c). As a plain room actor that index is out of range → the engine
    /// dereferences garbage and crashes (En_Door crash in SoH). So they must export as transitions.</summary>
    public static bool IsDoorActor(ushort id) => id is 0x0009 or 0x002E or 0x0081 or 0x0124;
    //                                                  En_Door  Door_Shutter Door_Toki Door_Killer

    /// <summary>Exported as a scene 0x0E transition entry (explicit transition placements + door actors).</summary>
    public bool IsTransitionActor => IsTransition || IsDoorActor(Number);

    public Vector3 Position
    {
        get => new(XPos, YPos, ZPos);
        set { XPos = value.X; YPos = value.Y; ZPos = value.Z; }
    }

    /// <summary>Deep copy (for clipboard / duplicate). New instance, same placement + label.</summary>
    public ZActor Clone() => new()
    {
        Number = Number, Variable = Variable, IdFlags = IdFlags,
        XPos = XPos, YPos = YPos, ZPos = ZPos,
        XRot = XRot, YRot = YRot, ZRot = ZRot,
        IsObsolete = IsObsolete, DisplayName = DisplayName, Name = Name,
        IsTransition = IsTransition, FrontRoom = FrontRoom, FrontEffect = FrontEffect,
        BackRoom = BackRoom, BackEffect = BackEffect, LockBack = LockBack,
        GroupId = GroupId, VisGroupId = VisGroupId,
        Schedule = Schedule?.Select(r => r.Clone()).ToList(),
        ScheduleVm = ScheduleVm?.Clone(),
        SchedulePoses = SchedulePoses?.Select(p => p.Clone()).ToList(),
        IsSelected = false,
    };
}

/// <summary>One pose a <see cref="ScheduleProgram"/> result can place an NPC at.</summary>
public sealed class SchedulePose
{
    public float X, Y, Z;
    public short Yaw;
    public SchedulePose Clone() => (SchedulePose)MemberwiseClone();
}

/// <summary>One MM schedule rule: during [StartHour:StartMin, EndHour:EndMin] on Day (0 = any day,
/// 1/2/3 = that cycle day), the NPC is at (X,Y,Z) facing Yaw (binary angle).</summary>
public sealed class ScheduleRule
{
    public byte  Day;                 // 0 = any, else cycle day 1/2/3
    public byte  StartHour, StartMin; // window start (24h)
    public byte  EndHour = 23, EndMin = 59;
    public float X, Y, Z;             // target position
    public short Yaw;                 // target facing (binary angle)
    public ScheduleRule Clone() => (ScheduleRule)MemberwiseClone();
}
