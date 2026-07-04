using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

/// <summary>
/// A scene path — an ordered polyline of waypoints (the 0x0D path list). Moving platforms (e.g.
/// Stone Tower blocks) and time/action-driven NPC routes (Gorman, Anju) follow a path by its
/// index. Editable in the spirit of Hammer's func_tracktrain track: add/move/remove waypoints.
/// </summary>
public sealed class ZPath
{
    public string Name { get; set; } = "Path";
    public List<Vector3> Points { get; } = [];
    public bool IsSelected { get; set; }

    /// <summary>Editor loop flag (Hammer path "closed" track): draws a closing segment from the last
    /// waypoint back to the first. A visualization aid for paths an actor traverses as a loop — the
    /// exported 0x0D point list is unchanged (looping is the following actor's behavior, not the data).</summary>
    public bool Closed { get; set; }

    /// <summary>MM-only path-header fields (OoT pads these): additionalPathIndex chains to another
    /// path, customValue distinguishes paths an actor can pick between. Preserved verbatim.</summary>
    public byte  AdditionalPathIndex { get; set; }
    public short CustomValue { get; set; }

    public ZPath() { }
    public ZPath(IEnumerable<Vector3> pts) { Points.AddRange(pts); }
}
