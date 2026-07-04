using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

public sealed class ZRoom
{
    private static readonly Vector3[] Palette =
    [
        new(0.40f, 0.62f, 0.85f),  // blue-gray
        new(0.40f, 0.80f, 0.52f),  // green
        new(0.85f, 0.62f, 0.30f),  // orange
        new(0.78f, 0.42f, 0.72f),  // pink-purple
        new(0.60f, 0.42f, 0.85f),  // purple
        new(0.38f, 0.78f, 0.72f),  // teal
        new(0.85f, 0.80f, 0.38f),  // yellow
        new(0.80f, 0.32f, 0.32f),  // red
    ];

    public string     Name     { get; set; }
    public List<Solid>  Geometry { get; } = [];
    public List<ZActor> Actors   { get; } = [];
    public List<Decal>  Decals   { get; } = [];
    public RoomSettings Settings { get; set; } = new();

    /// <summary>Imported Wavefront-OBJ mesh geometry (Blender / SharpOcarina) brought in as exportable
    /// textured level geometry, alongside the brush solids. Null until an OBJ is imported into the room.</summary>
    public ObjMesh? ObjMesh { get; set; }
    public bool IsActive { get; set; }

    /// <summary>Editor-only view toggle (the eye/checkbox in the room tree): when false this room's
    /// brushes, actors, and imported backdrop mesh are hidden from all viewports so you can focus on
    /// one or more rooms at a time. Never affects export — all rooms are always compiled. Not serialized.</summary>
    public bool Visible { get; set; } = true;

    // Per-room face color used by SolidRenderer
    public Vector3 Color { get; }

    public ZRoom(string name, int index = 0)
    {
        Name  = name;
        Color = Palette[index % Palette.Length];
    }
}
