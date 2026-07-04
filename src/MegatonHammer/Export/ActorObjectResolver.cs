using MegatonHammer.Rom;

namespace MegatonHammer.Export;

/// <summary>
/// Builds the actor-id → object-id mapping used to auto-generate a room's object dependency list
/// (header command 0x0B). Without that list, any placed actor whose object isn't in gameplay_keep
/// silently fails to spawn / crashes in-game. Both source tables are decomp-derived (no ROM needed):
/// the actor→object name from <see cref="ActorObjectTable"/>, the name→id from the object table.
/// </summary>
public static class ActorObjectResolver
{
    /// <summary>Returns a resolver: actor id → the object id the room must load for it, or null when
    /// the actor needs no dedicated object (gameplay_keep / field / dungeon keep are always resident).</summary>
    public static Func<ushort, ushort?> Build(bool mm)
    {
        var actorObjs = ActorObjectTable.Build(mm);
        var objTable = ObjectTable.BuildNamesOnly(mm);
        return actorId =>
        {
            string? name = actorObjs.ObjectFor(actorId);
            if (name == null) return null;
            // Keep objects are permanently loaded — never listed as a per-room dependency.
            if (name.Contains("gameplay_keep") || name.Contains("field_keep") || name.Contains("dangeon_keep"))
                return null;
            int? id = objTable.IdOf(name);
            return id is > 0 ? (ushort)id.Value : null;
        };
    }
}
