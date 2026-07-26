using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

/// <summary>
/// Actors that IGNORE their scene-placed position and force themselves to a hardcoded / compile-time position
/// — either a single fixed point, or one chosen from a params-indexed table (the <see cref="MirrorBeams"/>
/// archetype). For these, the editor snaps the placed marker onto the position the actor will actually occupy
/// in-game, so the author isn't misled by a marker that the engine discards. Multi-entry actors additionally
/// expose the choices as a dropdown in the property sheet; single-fixed ones carry an explanatory Note.
///
/// Actor ids are per-GAME (OoT and MM reuse the same numbers for different actors — e.g. Mir_Ray is 0x00B7 on
/// OoT but 0x0062 on MM; MM Boss_07 is 0x12F where OoT 0x12F is En_Yabusame_Mark), so every lookup is
/// game-qualified. A full OoT+MM decomp sweep found Mir_Ray as the ONLY multi-position table; the remaining
/// hits are single-fixed-position bosses. All coordinates verified against the decomp Init functions.
/// </summary>
public static class AnchoredActors
{
    /// <summary>The world position actor <paramref name="id"/> snaps to for this game + params, or null when it
    /// isn't position-anchored (caller then leaves placement untouched). Verified vs the decomp.</summary>
    public static Vector3? PositionFor(bool isOoT, ushort id, ushort vars)
    {
        // Mir_Ray reflectable beam (both games, different id): params 0–9 → sMirRayData[params].source.
        if (MirrorBeams.Is(id, isOoT)) return MirrorBeams.TryGet(vars & 0xF)?.Source;

        if (isOoT)
        {
            // Boss_Sst — Bongo Bongo (0x00E9). The head (params BONGO_HEAD = 0) overwrites world.pos with a
            // ROOM_CENTER-based constant, discarding placement (z_boss_sst.c:284). Its hands are child-spawned
            // relative to the head, so only the head is a placement override. ROOM_CENTER=(-50,0,0), head at +(50,0,-650).
            if (id == 0x00E9 && (vars & 0xFF) == 0) return new Vector3(0, 0, -650);
        }
        else
        {
            // Boss_03 — Gyorg (0x12B). The main boss unconditionally sets world.pos = sGyorgInitialPos in Init
            // (z_boss_03.c:482), discarding placement. (Its seaweed sub-actors return earlier and are decorative.)
            if (id == 0x012B) return new Vector3(1216, 140, -1161);
        }
        return null;
    }

    /// <summary>True if the actor forces its own position (placement is ignored) — the marker should snap and
    /// the editor must not let the user drag it.</summary>
    public static bool IsAnchored(bool isOoT, ushort id, ushort vars) => PositionFor(isOoT, id, vars).HasValue;

    /// <summary>Whether an actor id is position-anchored regardless of its params (for the "immovable" gate /
    /// warning). Mir_Ray is always anchored; the bosses only when they carry their default form.</summary>
    public static bool IsAnchoredId(bool isOoT, ushort id) =>
        MirrorBeams.Is(id, isOoT) || (isOoT && id == 0x00E9) || (!isOoT && id == 0x012B);

    /// <summary>Human-readable warning for an anchored actor: what it needs to work, and why it can't be moved.
    /// Shown once (with a "don't show again" option) when such an actor is selected. Null = not anchored.</summary>
    public static string? Constraint(bool isOoT, ushort id)
    {
        if (MirrorBeams.Is(id, isOoT))
            return
                "This is a Mirror-Shield Light Beam (Mir_Ray).\n\n" +
                "WHERE IT WORKS\n" +
                "• It appears at a FIXED world position that is compiled into the game — one of 10 vanilla beams " +
                "(Spirit Temple rooms + Ganon's Castle Spirit Trial). Choose which with the “Beam location” dropdown.\n" +
                "• It spawns in ANY scene (the editor auto-adds OBJECT_MIR_RAY), but its position matches those " +
                "dungeons' coordinates — build your room geometry around the beam (the yellow gizmo shows it).\n\n" +
                "WHAT IT NEEDS TO FUNCTION\n" +
                "• The player must have the Mirror Shield (to reflect the beam).\n" +
                "• A Sun Switch (Obj_Lightswitch) placed where the reflected beam can reach, wired to a door/gate.\n\n" +
                "WHY IT CAN'T BE MOVED\n" +
                "• OoT/MM store the beam's geometry in the actor's code and ignore where you place it. It is therefore " +
                "immovable due to an engine limitation — move your level to the beam, not the beam to your level.";
        if (isOoT && id == 0x00E9)
            return
                "This is the boss Bongo Bongo (Boss_Sst).\n\n" +
                "• On spawn it forces itself to the arena centre (≈ 0, 0, -650) and spawns its own hands.\n" +
                "• It IGNORES where you place it and is therefore immovable — an OoT engine limitation.";
        if (!isOoT && id == 0x012B)
            return
                "This is the boss Gyorg (Boss_03).\n\n" +
                "• On spawn it forces itself to its fixed arena position (≈ 1216, 140, -1161).\n" +
                "• It IGNORES where you place it and is therefore immovable — an MM engine limitation.";
        return null;
    }
}
