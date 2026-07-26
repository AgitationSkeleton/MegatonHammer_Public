namespace MegatonHammer.Editor;

/// <summary>Which playtest engine a non-vanilla optional item is available on.</summary>
public enum OptionalItemEngine { Soh }

/// <summary>
/// One optional, engine-exclusive (non-vanilla) item the user can enable in settings, add to the playtest
/// inventory, and place in chests / item-assignable actors. The editor is deliberately engine-agnostic: it
/// carries a stable <see cref="Key"/> (and a sentinel GetItem id for placement), and the SoH/2Ship fork maps
/// that key to its own internal item ids + grant path. Off by default; only surfaces when its settings gate
/// is on AND the launch engine matches <see cref="Engine"/>. See docs/optional-exclusive-items.md.
/// </summary>
public sealed record OptionalItem(
    string Key,                 // stable key emitted to the fork (inventory toggle + chest-give tag), e.g. "rocs_feather"
    string DisplayName,         // shown in settings / inventory / chest picker, e.g. "Roc's Feather"
    OptionalItemEngine Engine,  // the only engine that can grant it
    int ChestItemId,            // sentinel GetItem id used when placed in a chest / En_Item00 (fork maps it back)
    string EnableCVar,          // SoH enhancement CVar the fork sets to install the item's behavior
    Func<bool> IsEnabled);      // master + per-item settings gate

/// <summary>
/// Registry of optional engine-exclusive items. Add new ones here (e.g. the Fierce Deity's Mask from the
/// soh_fd fork) and wire the matching key on the fork side — the settings UI, playtest inventory and chest
/// picker all consume this list, so a new entry lights up everywhere automatically.
/// </summary>
public static class OptionalItems
{
    // Sentinel GetItem ids for optional items placed in chests/actors. Kept well above the vanilla GetItem
    // range (vanilla tops out ~0x7F) so they never collide; the fork recognises this range and grants the
    // mapped custom item instead of a normal get-item.
    public const int ChestSentinelBase = 0x0F00;

    public static readonly OptionalItem RocsFeather = new(
        Key:         "rocs_feather",
        DisplayName: "Roc's Feather",
        Engine:      OptionalItemEngine.Soh,
        ChestItemId: ChestSentinelBase + 0,
        EnableCVar:  "MhRocsFeather",
        IsEnabled:   () => EditorSettings.EnableSohExclusiveItems && EditorSettings.EnableRocsFeather);

    // Fierce Deity's Mask (soh_fd fork): the MM transformation mask/form, gated by the MhFierceDeity CVar the
    // fork checks (mirrors Roc's Feather). Needs the FD form + fd.o2r assets ported into the SoH fork.
    public static readonly OptionalItem FierceDeityMask = new(
        Key:         "fd_mask",
        DisplayName: "Fierce Deity's Mask",
        Engine:      OptionalItemEngine.Soh,
        ChestItemId: ChestSentinelBase + 1,
        EnableCVar:  "MhFierceDeity",
        IsEnabled:   () => EditorSettings.EnableSohExclusiveItems && EditorSettings.EnableFierceDeityMask);

    public static readonly IReadOnlyList<OptionalItem> All = [ RocsFeather, FierceDeityMask ];

    /// <summary>Items currently enabled for the given engine target (settings gate + engine match).</summary>
    public static IEnumerable<OptionalItem> Enabled(OptionalItemEngine engine) =>
        All.Where(i => i.Engine == engine && i.IsEnabled());

    /// <summary>Look up an optional item by its chest sentinel id (or null if not one).</summary>
    public static OptionalItem? ByChestId(int chestItemId) =>
        All.FirstOrDefault(i => i.ChestItemId == chestItemId);
}
