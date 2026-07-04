using System.Security.Cryptography;
using MegatonHammer.Editor;

namespace MegatonHammer.Rom;

/// <summary>
/// #12b: known-good asset fingerprints + startup auto-detection. The N64 playtest needs SPECIFIC base
/// ROMs (the OoT gc-eu-mq-dbg debug ROM and the MM US-retail ROM), so we validate a configured/located
/// ROM by MD5 rather than accepting any .z64. Forks (SoH/2Ship/PJ64) are detected by their known build
/// locations (their exe hashes change on every rebuild, so a hash check there would be noise) — but the
/// current exe hash is recorded for reference. All hashes are kept in the megaton-hammer memory; update
/// them there if the canonical assets ever change.
/// </summary>
public static class RomFingerprint
{
    // Canonical base-ROM MD5s (uppercase, no separators). Computed from the validated working assets.
    public const string OotDebugMd5  = "230F62C994FF072A1434DF6E65E2DBE0";  // ZELOOTMA.Z64, "THE LEGEND OF DEBUG", gc-eu-mq-dbg, 67,125,760 B
    public const string MmUsRetailMd5 = "2A0A8ACB61538235BC1094D297FB6556"; // mm.z64, "ZELDA MAJORA'S MASK" (USA), 33,554,432 B

    /// <summary>A second OoT debug build we also accept ("THE LEGEND OF ZELDA", 64 MB decompressed). The
    /// auto-boot path still requires the DEBUG-named one, but this passes ROM validation.</summary>
    public const string OotDebugAltMd5 = "3C10B67A76616AE2C162DEF7528724CF"; // ZELOOTD.z64

    /// <summary>Retail OoT USA v1.0 (32 MB) — the ROM the SoH fork extracts its assets from, and what the
    /// "OoT ROM" slot normally points at. Valid for the OoT slot; just NOT the debug ROM the N64 PJ64
    /// playtest needs (that path checks the internal name for "DEBUG" separately).</summary>
    public const string OotRetailUsaMd5 = "5BD1FE107BF8106B2AB6650ABECD54D6";

    /// <summary>MD5 of a file as an uppercase hex string (no separators); null if unreadable.</summary>
    public static string? Md5(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var md5 = MD5.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(md5.ComputeHash(fs));
        }
        catch { return null; }
    }

    /// <summary>True if the path is a base ROM matching the expected game's known-good MD5.</summary>
    public static bool IsExpectedRom(string? path, bool oot)
    {
        var h = Md5(path);
        if (h == null) return false;
        return oot ? (h == OotDebugMd5 || h == OotDebugAltMd5 || h == OotRetailUsaMd5) : h == MmUsRetailMd5;
    }

    /// <summary>Validates a configured base ROM; returns (ok, human message). ok=false is a warning,
    /// not a hard block — an unrecognized ROM may still work, but the user should know it's off-spec.</summary>
    public static (bool ok, string detail) CheckRom(string? path, bool oot)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return (false, "no ROM configured");
        var h = Md5(path);
        if (h == null) return (false, "could not read ROM");
        if (IsExpectedRom(path, oot)) return (true, $"MD5 {h} — recognized {(oot ? "OoT debug" : "MM US retail")} ROM");
        string want = oot ? $"{OotDebugMd5} (gc-eu-mq-dbg)" : $"{MmUsRetailMd5} (MM USA)";
        return (false, $"MD5 {h} does not match the expected {(oot ? "OoT" : "MM")} ROM {want}");
    }

    // ── Auto-detection ──────────────────────────────────────────────────────

    // Folders worth scanning for the base ROMs (most-likely first). Kept here so the search stays
    // in one place; relative entries are resolved against the working dir.
    private static IEnumerable<string> RomSearchDirs()
    {
        yield return @"D:\Copilot_OOT\READ_ONLY_GameROMs";
        yield return Path.Combine(AppContext.BaseDirectory, "roms");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "roms");
        yield return @"D:\Copilot_OOT\WorkFolders\MegatonHammer\2Ship\OTRExporter"; // mm.z64 lives here
        yield return Directory.GetCurrentDirectory();
    }

    // Known fork/emulator build locations (absolute first, then relative to the working dir).
    private static readonly string[] SohCandidates =
    [
        @"D:\Copilot_OOT\WorkFolders\MegatonHammer\SoH\x64\Release\soh.exe",
        @"SoH\x64\Release\soh.exe",
    ];
    private static readonly string[] TwoShipCandidates =
    [
        @"D:\Copilot_OOT\WorkFolders\MegatonHammer\2Ship\x64\Release\2ship.exe",
        @"2Ship\x64\Release\2ship.exe",
    ];
    private static readonly string[] Pj64Candidates =
    [
        @"D:\Copilot_OOT\WorkFolders\MegatonHammer\pj64run\Project64.exe",
        @"pj64run\Project64.exe",
    ];

    /// <summary>Result of a startup auto-detect pass, for an optional one-line status to the user.</summary>
    public sealed record DetectResult(List<string> Found, List<string> Mismatched);

    /// <summary>Fills any UNSET asset path in EditorSettings from the known locations (ROMs validated by
    /// MD5; forks by existence at their build path). Never overrides a path the user already chose. Safe
    /// to call every launch. Returns what it found / what looked wrong, for a status line.</summary>
    public static DetectResult AutoDetect()
    {
        var found = new List<string>();
        var mismatched = new List<string>();

        // Base ROMs: only accept a file whose MD5 matches; scan each dir for any .z64/.n64.
        if (string.IsNullOrWhiteSpace(EditorSettings.OotRomPath))
        {
            var p = FindRom(oot: true, out bool sawWrong);
            if (p != null) { EditorSettings.OotRomPath = p; found.Add($"OoT ROM: {p}"); }
            else if (sawWrong) mismatched.Add("OoT ROM (a .z64 was found but its MD5 didn't match gc-eu-mq-dbg)");
        }
        if (string.IsNullOrWhiteSpace(EditorSettings.MmRomPath))
        {
            var p = FindRom(oot: false, out bool sawWrong);
            if (p != null) { EditorSettings.MmRomPath = p; found.Add($"MM ROM: {p}"); }
            else if (sawWrong) mismatched.Add("MM ROM (a .z64 was found but its MD5 didn't match MM USA)");
        }

        // Forks/emulator: first existing candidate path wins (only if unset).
        if (string.IsNullOrWhiteSpace(EditorSettings.SohExePath) && FirstExisting(SohCandidates) is { } soh)
        { EditorSettings.SohExePath = soh; found.Add($"SoH fork: {soh}"); }
        if (string.IsNullOrWhiteSpace(EditorSettings.TwoShipExePath) && FirstExisting(TwoShipCandidates) is { } two)
        { EditorSettings.TwoShipExePath = two; found.Add($"2Ship fork: {two}"); }
        if (string.IsNullOrWhiteSpace(EditorSettings.Project64Path) && FirstExisting(Pj64Candidates) is { } pj)
        { EditorSettings.Project64Path = pj; found.Add($"PJ64 fork: {pj}"); }

        return new DetectResult(found, mismatched);
    }

    /// <summary>
    /// Finds the gc-eu-mq-dbg debug ROM ("THE LEGEND OF DEBUG", 67,125,760 B) for the N64 OoT playtest.
    /// This is a DIFFERENT ROM from the retail OoT the editor's general <c>OotRomPath</c> points at (that
    /// one feeds SoH asset extraction). The N64 OoT playtest needs the debug build specifically: only it
    /// has the seamless auto-boot layout (<see cref="OotDebugAutoBoot"/>) and the no-dmadata inject path.
    /// Scans the same dirs as auto-detect, cheaply (size-gate + a 4-byte layout probe, no full MD5 / no
    /// 64 MB read) and returns the first match, else null. Prefers an EXPLICIT debug ROM over the generic
    /// OoT slot so a retail OoT in <c>OotRomPath</c> no longer steals the N64 playtest.
    /// </summary>
    public static string? FindOotDebugRom()
    {
        const long GcEuMqDbgSize = 67_125_760;          // gc-eu-mq-dbg uncompressed size
        const int  PrologueOff   = 0xB3B250;            // TitleSetup_InitImpl[0] — see OotDebugAutoBoot
        const uint Prologue      = 0x27BDFFE8;          // addiu $sp,$sp,-0x18

        byte[] probe = new byte[4];
        foreach (var dir in RomSearchDirs())
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            IEnumerable<string> roms;
            try { roms = Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".z64", StringComparison.OrdinalIgnoreCase)
                                                              || f.EndsWith(".n64", StringComparison.OrdinalIgnoreCase)); }
            catch { continue; }
            foreach (var f in roms)
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length != GcEuMqDbgSize) continue;       // cheap size gate (skips retail 32 MB, etc.)
                    using var fs = File.OpenRead(f);
                    fs.Seek(PrologueOff, SeekOrigin.Begin);
                    if (fs.Read(probe, 0, 4) != 4) continue;
                    uint v = (uint)(probe[0] << 24 | probe[1] << 16 | probe[2] << 8 | probe[3]);
                    if (v == Prologue) return f;                    // layout matches gc-eu-mq-dbg
                }
                catch { /* unreadable — skip */ }
            }
        }
        return null;
    }

    private static string? FindRom(bool oot, out bool sawWrongHash)
    {
        sawWrongHash = false;
        foreach (var dir in RomSearchDirs())
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            IEnumerable<string> roms;
            try { roms = Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".z64", StringComparison.OrdinalIgnoreCase)
                                                              || f.EndsWith(".n64", StringComparison.OrdinalIgnoreCase)); }
            catch { continue; }
            foreach (var f in roms)
            {
                if (IsExpectedRom(f, oot)) return f;
                sawWrongHash = true;
            }
        }
        return null;
    }

    private static string? FirstExisting(string[] candidates)
    {
        foreach (var c in candidates)
        {
            string full = Path.IsPathRooted(c) ? c : Path.Combine(Directory.GetCurrentDirectory(), c);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
