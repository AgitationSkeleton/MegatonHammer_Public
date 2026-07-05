namespace MegatonHammer.Editor;

/// <summary>
/// Global, session-level view toggles shared by every viewport (so a single menu item
/// affects all panes at once). Separate flags for the 3D and 2D panes let entities be
/// shown in one and hidden in the other. Per-room visibility (for multi-room dungeons)
/// also lives here, keyed by a room reference.
/// </summary>
public static class ViewOptions
{
    /// <summary>Show entity markers/models in the perspective (3D) viewport.</summary>
    public static bool ShowEntities3D { get; set; } = true;

    /// <summary>Show entity markers in the orthographic (2D) viewports.</summary>
    public static bool ShowEntities2D { get; set; } = true;

    /// <summary>Draw logic-connection lines between actors that share a flag (setter→reader), Hammer's
    /// entity I/O wires — e.g. an eye switch to the shutter it unbars, a chest to its switch. ON by default
    /// so barred doors / orphaned-looking switches visibly show what they're tied to (#3/#4); toggle in View.</summary>
    public static bool ShowLogicConnections { get; set; } = true;

    /// <summary>The current project's game (true = OoT, false = MM). Set once at startup; used to pick
    /// the right actor-flag schema for the connection graph.</summary>
    public static bool IsOoT { get; set; } = true;

    /// <summary>Snap brush drawing/scaling/moving to the grid (Hammer's "Snap to Grid"; default on).</summary>
    public static bool SnapToGrid { get; set; } = true;

    /// <summary>Render the scene's sky in the 3D view (game-accurate per scene; on by default).</summary>
    public static bool ShowSky { get; set; } = true;

    /// <summary>Draw the ground reference grid in the 3D view (off by default — it clutters the scene).</summary>
    public static bool ShowGrid3D { get; set; } = false;

    /// <summary>Draw a prerendered-room's baked JFIF background image (Forest Temple / Deku-Tree-style scenes)
    /// as a camera-facing billboard behind the geometry. OFF by default — it's a fixed backdrop that only
    /// matches one camera angle and obscures the brushes you're editing (see Rooms with pre-rendered BGs).</summary>
    public static bool ShowPrerenderedBackground { get; set; } = false;

    // Trilinear (mipmap) texture filtering for world geometry. On → smooth; off → crisp, point-sampled
    // N64-style textures. Toggling bumps FilterEpoch so renderers re-apply filters to cached textures.
    private static bool _trilinear = true;
    public static bool TrilinearFilter
    {
        get => _trilinear;
        set { if (_trilinear == value) return; _trilinear = value; FilterEpoch++; NotifyChanged(); }
    }
    /// <summary>Incremented whenever <see cref="TrilinearFilter"/> changes, so renderers know to re-apply.</summary>
    public static int FilterEpoch { get; private set; }

    // Rooms hidden from rendering (multi-room dungeon visibility — D14). A room absent
    // from this set is visible. Keyed by the ZRoom instance.
    private static readonly HashSet<ZRoom> _hiddenRooms = [];

    public static bool IsRoomVisible(ZRoom room) => !_hiddenRooms.Contains(room);

    public static void SetRoomVisible(ZRoom room, bool visible)
    {
        if (visible) _hiddenRooms.Remove(room);
        else _hiddenRooms.Add(room);
    }

    /// <summary>Raised when any view option changes so viewports can invalidate/redraw.</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();
}
