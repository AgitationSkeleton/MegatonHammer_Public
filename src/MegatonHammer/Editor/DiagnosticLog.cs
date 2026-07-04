namespace MegatonHammer.Editor;

/// <summary>
/// Lightweight append-only diagnostic log for multi-step operations (playtest export/inject/launch)
/// where any one step can fail. Writes timestamped lines to %AppData%\MegatonHammer\diagnostics.log
/// and keeps the most recent run's lines in memory so a failure dialog can show the trail. Each call
/// to <see cref="Begin"/> starts a fresh in-memory section.
/// </summary>
public static class DiagnosticLog
{
    private static readonly string Dir =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MegatonHammer");
    private static readonly string FilePath = System.IO.Path.Combine(Dir, "diagnostics.log");
    private static readonly object Gate = new();
    private static readonly List<string> _recent = [];

    /// <summary>Path of the on-disk log (shown to the user so they can attach it to a bug report).</summary>
    public static string Path => FilePath;

    /// <summary>Starts a new operation section. Clears the in-memory trail and writes a header.</summary>
    public static void Begin(string operation)
    {
        lock (Gate) _recent.Clear();
        Write($"===== {operation} =====");
    }

    /// <summary>Logs a step. Prefix conventions: nothing = info, "OK" / "FAIL" via the helpers.</summary>
    public static void Step(string message) => Write("  · " + message);
    public static void Ok(string message)   => Write("  ✓ " + message);
    public static void Fail(string message) => Write("  ✗ " + message);

    /// <summary>Logs an exception with its type and message (and inner, if any).</summary>
    public static void Error(string context, Exception ex)
    {
        Write($"  ✗ {context}: {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException != null)
            Write($"      inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    }

    /// <summary>The most recent run's lines, for surfacing in an error dialog.</summary>
    public static string RecentTrail()
    {
        lock (Gate) return string.Join(Environment.NewLine, _recent);
    }

    private static void Write(string line)
    {
        string stamped = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
        lock (Gate)
        {
            _recent.Add(stamped);
            try { Directory.CreateDirectory(Dir); File.AppendAllText(FilePath, stamped + Environment.NewLine); }
            catch { /* logging must never throw */ }
        }
        // Mirror every injection/diagnostic step into the active per-playtest session log (different gate,
        // so no deadlock). This is how the playtest log captures "every diagnostic step of the injection".
        try { PlaytestLog.Current?.Line(line.TrimStart(' ', '·', '✓', '✗')); } catch { }
    }
}
