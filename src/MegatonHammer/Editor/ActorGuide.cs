namespace MegatonHammer.Editor;

/// <summary>
/// Per-entity placement guidance, with a focus on bosses/minibosses that have invariants
/// their original room guarantees. Matching is by the actor's database name (keyword), so
/// it's robust to id differences and covers families. The notes explain what an actor needs
/// to spawn and survive without crashing when placed in a custom room — vanilla-compatible
/// (place stock actors with the right setup; never change actor code). (D17)
/// </summary>
public static class ActorGuide
{
    private readonly record struct Rule(string[] Keywords, string Note);

    // Specific notes first (more specific keywords), then general boss/miniboss fallbacks.
    private static readonly Rule[] Rules =
    [
        new(["morpha"],
            "Morpha (Water Temple boss): needs a collision WATERBOX covering the arena — it lives in water. " +
            "Place over a water brush. Load its object; give the room a flat arena and a blue-warp on defeat."),
        new(["volvagia", "flare", "fire temple boss"],
            "Volvagia (Fire Temple boss): expects lava floor collision with hole positions and its object loaded. " +
            "Without the lava-hole setup it can't surface correctly. Provide a flat arena + blue-warp."),
        new(["phantom", "ganon"],
            "Phantom Ganon / Ganon(dorf): expects a large arena and (Phantom Ganon) the painting-chase setup; " +
            "Ganon battles assume specific tower/collapse rooms and cutscene actors. Camera and cutscenes may " +
            "misbehave outside their arena — place in a large empty room and load the object + boss objects."),
        new(["barinade"],
            "Barinade (Jabu-Jabu boss): expects a round arena; it anchors to the ceiling. Load its object and give " +
            "a flat circular room with a blue-warp on defeat."),
        new(["gohma", "goma"],
            "Queen Gohma (Deku Tree boss): needs its object, a tall room (it climbs walls/ceiling), and a blue-warp. " +
            "Spawns from the ceiling — ensure vertical space."),
        new(["bongo"],
            "Bongo Bongo (Shadow Temple boss): expects the drum-floor arena (bouncy floor collision) and darkness. " +
            "Lens-of-Truth visibility and the drum surface are assumed; provide the special floor + its object."),
        new(["twinrova", "kotake", "koume"],
            "Twinrova (Spirit Temple boss): expects the mirror arena; the fight uses reflected-magic mechanics. " +
            "Load its object; provide a flat arena + blue-warp."),
        new(["dark link", "torch2"],
            "Dark Link (En_Torch2): expects the illusory water-floor room; it mirrors the player. Place on a flat " +
            "floor in an enclosed room. Uses the player's object (no extra object), but lighting/water set the mood."),
        new(["iron knuckle", "ik "],
            "Iron Knuckle (miniboss): heavy melee enemy; needs its object loaded and enough floor space to charge. " +
            "Set the correct variant (throne/standard) via params."),
        new(["stalfos", "white wolfos", "wolfos"],
            "Miniboss enemy: load its object and place on open floor. Some variants gate a room clear flag — set " +
            "params accordingly if it should lock doors until defeated."),
        new(["boss"],
            "Boss actor: bosses assume their home arena — a correctly-sized room, their object(s) in the room " +
            "object list, often a specific collision/waterbox, camera and a blue-warp on defeat. Place in a " +
            "dedicated room and satisfy these or it may crash/soft-lock. Keep it vanilla: stock actor + setup only."),
    ];

    /// <summary>Returns guidance for an actor (by display name), or null if none applies.</summary>
    public static string? For(string? actorName)
    {
        if (string.IsNullOrEmpty(actorName)) return null;
        string n = actorName.ToLowerInvariant();
        foreach (var r in Rules)
            if (r.Keywords.Any(k => n.Contains(k)))
                return r.Note;
        return null;
    }
}
