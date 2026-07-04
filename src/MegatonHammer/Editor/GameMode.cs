namespace MegatonHammer.Editor;

public enum GameMode
{
    OcarinaOfTime,
    MajorasMask,
    ShipOfHarkinian,
    TwoShip2Harkinian,
    CustomOoT,
    CustomMM
}

public class GameConfig
{
    public GameMode Mode { get; set; }
    public string? RomPath { get; set; }
    public string? GameDirectory { get; set; }

    public bool IsVanilla => Mode is GameMode.OcarinaOfTime or GameMode.MajorasMask;
    public bool IsOoTBased => Mode is GameMode.OcarinaOfTime or GameMode.ShipOfHarkinian or GameMode.CustomOoT;
    public bool IsMMBased  => Mode is GameMode.MajorasMask  or GameMode.TwoShip2Harkinian or GameMode.CustomMM;

    public string DisplayName => Mode switch
    {
        GameMode.OcarinaOfTime    => "Ocarina of Time (Vanilla ROM)",
        GameMode.MajorasMask      => "Majora's Mask (Vanilla ROM)",
        GameMode.ShipOfHarkinian  => "Ship of Harkinian (OoT) — Megaton Hammer fork",
        GameMode.TwoShip2Harkinian => "2Ship2Harkinian (MM) — Megaton Hammer fork",
        GameMode.CustomOoT        => "Custom OoT Base",
        GameMode.CustomMM         => "Custom MM Base",
        _                         => "Unknown"
    };
}
