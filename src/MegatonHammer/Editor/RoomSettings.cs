namespace MegatonHammer.Editor;

/// <summary>
/// Per-room configuration surfaced in the editor and consumed by RoomExporter.
/// </summary>
public sealed class RoomSettings
{
    // Time-of-day override: 0xFFFF means "inherit" (no override).
    public ushort TimeOverride { get; set; } = 0xFFFF;
    public byte   TimeSpeed    { get; set; }

    public byte   Echo         { get; set; }

    public bool   ShowInvisibleActors { get; set; }
    public bool   DisableSkybox       { get; set; }
    public bool   DisableSunMoon      { get; set; }

    // Behaviour/type byte written into the room behaviour header command.
    public byte   BehaviorType { get; set; }
}
