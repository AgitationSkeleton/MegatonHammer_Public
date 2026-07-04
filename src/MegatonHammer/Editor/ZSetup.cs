namespace MegatonHammer.Editor;

/// <summary>
/// One scene "setup" (variant) — the editor's first-class model of the game's alternate scene
/// headers (command 0x18): OoT child/adult × day/night, MM time-of-day/day variants (e.g. the
/// Stock Pot Inn). A setup is a named snapshot of everything that can differ between variants:
/// the scene settings (spawn / lighting / skybox / music), the lighting environments, and each
/// room's actor placements. Room geometry and structure are shared across all setups.
///
/// These are authored from scratch (Add Setup), switched between, renamed and deleted entirely in
/// the editor; switching commits the live scene into the active setup and loads the target one.
/// </summary>
public sealed class ZSetup
{
    public string Name { get; set; } = "Default";
    public SceneSettings Settings { get; set; } = new();
    public List<Rom.EnvLight> Environments { get; set; } = [];
    /// <summary>Actor placements per room, indexed parallel to the scene's room list.</summary>
    public List<List<ZActor>> RoomActors { get; set; } = [];

    /// <summary>What condition the engine loads this setup under (cmd 0x18 layer semantics). OoT picks a
    /// layer from Link's age × time of day, or a cutscene layer; MM picks via the cutscene index/event.
    /// Editor metadata that documents/labels each variant's purpose (and can drive export ordering).</summary>
    public SetupLayer Layer { get; set; } = SetupLayer.Default;
}

/// <summary>The alternate-header (0x18) selection condition for a setup. Default = the base header.</summary>
public enum SetupLayer { Default, ChildDay, ChildNight, AdultDay, AdultNight, Cutscene }
