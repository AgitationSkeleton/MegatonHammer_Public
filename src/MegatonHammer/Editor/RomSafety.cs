namespace MegatonHammer.Editor;

/// <summary>
/// Protects original game ROMs from being overwritten. By default the editor refuses to
/// write over any registered original ROM (and anything under a read-only ROM folder);
/// the user must explicitly disable the safety check in Options to allow it. All ROM
/// writes should go through <see cref="GuardWrite"/> first.
/// </summary>
public static class RomSafety
{
    /// <summary>When false (default), writing over a protected original ROM is blocked.</summary>
    public static bool AllowOverwriteOriginals { get; set; }

    // Absolute paths of original ROMs the user loaded (protected unless the toggle is on).
    private static readonly HashSet<string> _protected = new(StringComparer.OrdinalIgnoreCase);

    public static void Protect(string? romPath)
    {
        if (!string.IsNullOrWhiteSpace(romPath))
            _protected.Add(Normalize(romPath));
    }

    /// <summary>True if writing to <paramref name="path"/> would clobber a protected original.</summary>
    public static bool IsProtected(string path)
    {
        if (AllowOverwriteOriginals) return false;
        string n = Normalize(path);
        if (_protected.Contains(n)) return true;
        // Treat anything inside a "read only" ROM folder as protected too.
        return n.Replace('\\', '/').Contains("read_only", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Throws if writing to <paramref name="path"/> would overwrite a protected original ROM
    /// while the safety check is on. Call before any ROM write.
    /// </summary>
    public static void GuardWrite(string path)
    {
        if (IsProtected(path))
            throw new InvalidOperationException(
                "This is an original game ROM, which is read-only.\n\n" +
                "Megaton Hammer always produces a NEW ROM. Choose a different output path, " +
                "or disable “Allow overwriting original ROMs” in Options (not recommended).");
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }
}
