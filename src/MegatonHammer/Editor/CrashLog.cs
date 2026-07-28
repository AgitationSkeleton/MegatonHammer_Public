using System.Runtime.InteropServices;

namespace MegatonHammer.Editor;

/// <summary>
/// Robust crash + first-chance exception capture for the editor. Earlier crashes (e.g. "Error creating
/// window handle" from a control/handle leak, or an exception swallowed inside a WinForms event) produced
/// NO usable log, which made them undiagnosable. This captures:
///   • a short ring of UI "breadcrumbs" (what the user was doing),
///   • the process USER-object count (window handles) — so handle exhaustion is obvious,
///   • every first-chance exception (the moment it is thrown, before any catch), rate-limited,
///   • full fatal-crash detail, written to SEVERAL findable places (LocalAppData\...\logs\crash.log,
///     next to the exe, and the diagnostics log the user already checks).
/// Every method is exception-proof and recursion-guarded so logging can never itself crash the app.
/// </summary>
public static class CrashLog
{
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
    private static uint UserObjects()
    {
        try { return GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 1); }
        catch { return 0; }
    }

    private static readonly object Gate = new();
    private static readonly Queue<string> Crumbs = new();
    private static readonly string CrashPath = AppPaths.Log("crash.log");
    private static readonly string FirstChancePath = AppPaths.Log("firstchance.log");
    [ThreadStatic] private static bool _inLog;   // recursion guard (logging must not re-enter itself)
    private static string _lastSig = "";
    private static int _repeat;

    /// <summary>Record what the user is doing, so a crash log can say "while &lt;breadcrumb&gt;". Cheap;
    /// call it from any UI action that has crashed before or touches native handles.</summary>
    public static void Breadcrumb(string what)
    {
        try
        {
            lock (Gate)
            {
                Crumbs.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {what}");
                while (Crumbs.Count > 16) Crumbs.Dequeue();
            }
        }
        catch { }
    }

    /// <summary>Wire the process-wide handlers. Call once at startup before the message loop.</summary>
    public static void Install()
    {
        try
        {
            AppDomain.CurrentDomain.FirstChanceException += (_, e) => FirstChance(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal("UnhandledException", e.ExceptionObject);
            System.Windows.Forms.Application.ThreadException += (_, e) => Fatal("ThreadException (UI)", e.Exception);
            System.Windows.Forms.Application.SetUnhandledExceptionMode(
                System.Windows.Forms.UnhandledExceptionMode.CatchException);
        }
        catch { }
    }

    /// <summary>Log a fatal crash with full detail + handle count + breadcrumbs, to every findable place.</summary>
    public static void Fatal(string source, object? ex)
    {
        if (_inLog) return;
        _inLog = true;
        try
        {
            string trail;
            lock (Gate) trail = Crumbs.Count == 0 ? "(none)" : string.Join(Environment.NewLine + "    ", Crumbs);
            uint handles = UserObjects();
            string hint = handles > 8000
                ? "  ** window-handle pool near exhaustion (~10000 cap) — this is a control/handle LEAK **" + Environment.NewLine
                : "";
            string body =
                $"===== CRASH [{source}]  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  =====" + Environment.NewLine +
                $"  USER objects (window handles): {handles}" + Environment.NewLine + hint +
                "  Recent actions:" + Environment.NewLine + "    " + trail + Environment.NewLine +
                "  Exception:" + Environment.NewLine + "  " + ex + Environment.NewLine + Environment.NewLine;

            foreach (var p in CrashDestinations())
                try { System.IO.File.AppendAllText(p, body); } catch { }
            try { DiagnosticLog.Fail($"CRASH [{source}] handles={handles} — {Short(ex)} (see crash.log)"); } catch { }
        }
        catch { }
        finally { _inLog = false; }
    }

    /// <summary>Log a first-chance exception (thrown, not necessarily unhandled). Rate-limited so a
    /// repeated/benign exception can't spam the file, and recursion-guarded.</summary>
    public static void FirstChance(Exception ex)
    {
        if (_inLog) return;
        _inLog = true;
        try
        {
            string sig = ex.GetType().Name + "|" + ex.Message;
            lock (Gate)
            {
                if (sig == _lastSig)
                {
                    if (++_repeat > 3) return;   // collapse floods of the identical exception
                }
                else { _lastSig = sig; _repeat = 0; }
            }
            string frame = FirstFrame(ex);
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] h={UserObjects()} {ex.GetType().FullName}: {ex.Message}"
                          + (frame.Length > 0 ? "  @ " + frame : "") + Environment.NewLine;
            try { System.IO.File.AppendAllText(FirstChancePath, line); } catch { }
        }
        catch { }
        finally { _inLog = false; }
    }

    private static IEnumerable<string> CrashDestinations()
    {
        yield return CrashPath;
        string near = "";
        try { near = System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"); } catch { }
        if (near.Length > 0) yield return near;
    }

    private static string Short(object? ex) =>
        ex is Exception e ? $"{e.GetType().Name}: {e.Message}" : ex?.ToString() ?? "?";

    private static string FirstFrame(Exception ex)
    {
        try
        {
            var st = ex.StackTrace;
            if (string.IsNullOrEmpty(st)) return "";
            int nl = st.IndexOf('\n');
            return (nl < 0 ? st : st[..nl]).Trim();
        }
        catch { return ""; }
    }
}
