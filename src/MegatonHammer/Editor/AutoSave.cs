namespace MegatonHammer.Editor;

/// <summary>
/// Timed auto-save + crash recovery. Periodically writes rolling backup copies of the project to
/// %AppData%\MegatonHammer\autosave\, keeps an explicit crash backup when the app dies on an
/// unhandled exception, and uses a session-lock file so the next launch can detect that the
/// previous run didn't exit cleanly and offer the recovered backup.
/// </summary>
public static class AutoSave
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MegatonHammer", "autosave");

    private static string LockPath  => Path.Combine(Dir, "session.lock");
    private static string CrashPath => Path.Combine(Dir, "crash-recovery" + ProjectSerializer.Extension);
    private const string AutoPrefix = "autosave_";

    /// <summary>Folder backups live in (shown to the user).</summary>
    public static string Folder => Dir;

    // ── Session lifecycle ───────────────────────────────────────────────────

    /// <summary>True if the previous run left a session lock behind (crash / hard kill). Call this
    /// BEFORE <see cref="BeginSession"/>, which takes the lock for the new run.</summary>
    public static bool PreviousSessionEndedBadly() => File.Exists(LockPath);

    /// <summary>Marks a session as running (drops the lock file).</summary>
    public static void BeginSession()
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(LockPath, DateTime.Now.ToString("o")); }
        catch { /* best effort */ }
    }

    /// <summary>Clean shutdown: clears the lock and the crash backup (nothing to recover).</summary>
    public static void EndSessionCleanly()
    {
        TryDelete(LockPath);
        TryDelete(CrashPath);
    }

    /// <summary>The file to offer for recovery after a bad exit: the crash backup if present,
    /// else the newest rolling auto-save, else null.</summary>
    public static string? RecoveryFile()
    {
        if (File.Exists(CrashPath)) return CrashPath;
        return NewestAutoSave();
    }

    // ── Saving ──────────────────────────────────────────────────────────────

    /// <summary>Writes a timestamped rolling backup and prunes to <paramref name="keep"/> copies.</summary>
    public static void WriteAutoSave(MapDocument doc, int keep)
    {
        Directory.CreateDirectory(Dir);
        string path = Path.Combine(Dir, $"{AutoPrefix}{DateTime.Now:yyyyMMdd_HHmmss}{ProjectSerializer.Extension}");
        File.WriteAllText(path, ProjectSerializer.Serialize(doc));
        Prune(keep);
    }

    /// <summary>Best-effort save of the document to the crash backup (called from a crash handler).</summary>
    public static void WriteCrashBackup(MapDocument doc)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(CrashPath, ProjectSerializer.Serialize(doc)); }
        catch { /* dying anyway */ }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string? NewestAutoSave()
    {
        if (!Directory.Exists(Dir)) return null;
        return Directory.GetFiles(Dir, AutoPrefix + "*" + ProjectSerializer.Extension)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static void Prune(int keep)
    {
        try
        {
            var stale = Directory.GetFiles(Dir, AutoPrefix + "*" + ProjectSerializer.Extension)
                .OrderByDescending(File.GetLastWriteTimeUtc).Skip(Math.Max(1, keep)).ToList();
            foreach (var f in stale) TryDelete(f);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
