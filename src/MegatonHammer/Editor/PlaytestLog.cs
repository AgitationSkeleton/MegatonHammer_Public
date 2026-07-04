using System.Collections;
using System.Reflection;
using System.Text;
using System.Diagnostics;

namespace MegatonHammer.Editor;

/// <summary>
/// One self-contained log file per playtest launch, for ANY engine (SoH / 2Ship / PJ64). It records
/// the complete state of the launch — every scene & room setting (even ones left at their default),
/// the playtest config, the full inventory + debug-mode values, the injection manifest, and every
/// diagnostic step — then maintains a live link to the launched engine: it tails the engine's own log
/// file(s), captures crash output, and stops automatically when the engine process fully closes.
///
/// Files live in a FIXED location so they're easy to find after the fact:
///   %AppData%\MegatonHammer\logs\playtest_&lt;engine&gt;_&lt;yyyyMMdd_HHmmss&gt;.log
/// (and the newest is always copied to ...\logs\playtest_latest.log).
/// </summary>
public sealed class PlaytestLog
{
    /// <summary>The fixed logs directory: %AppData%\MegatonHammer\logs.</summary>
    public static readonly string LogDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MegatonHammer", "logs");

    /// <summary>The currently-open session, if a playtest is running. DiagnosticLog mirrors into it.</summary>
    public static PlaytestLog? Current { get; private set; }

    /// <summary>Full path of this session's log file.</summary>
    public string Path { get; }

    private readonly object _gate = new();
    private readonly string _latest;
    private readonly bool _disabled;
    private bool _stopped;
    private Thread? _linkThread;

    private PlaytestLog(string engine)
    {
        string safe = new string(engine.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        _latest = Path_Combine("playtest_latest.log");

        // Max-logs setting: 0 = logging off. Otherwise create the dir + (timestamped or one-file) target.
        int max = EditorSettings.PlaytestLogMax;
        if (max == 0)
        {
            _disabled = true;
            Path = Path_Combine($"playtest_{safe}.log");   // nominal path; nothing is written
            return;
        }
        Directory.CreateDirectory(LogDir);

        if (EditorSettings.PlaytestLogOneFile)
        {
            // One fixed file per engine — overwrite each launch.
            Path = Path_Combine($"playtest_{safe}.log");
            try { File.Delete(Path); } catch { }
        }
        else
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            Path = Path_Combine($"playtest_{safe}_{stamp}.log");
        }
        PruneOldLogs(max);

        WriteRaw($"================ MEGATON HAMMER PLAYTEST LOG ================\n");
        WriteRaw($"engine : {engine}\n");
        WriteRaw($"started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        WriteRaw($"file   : {Path}\n");
        WriteRaw($"============================================================\n");
    }

    private string Path_Combine(string leaf) => System.IO.Path.Combine(LogDir, leaf);

    /// <summary>Begins a new session (closing any prior one). Becomes <see cref="Current"/>.</summary>
    public static PlaytestLog Begin(string engine)
    {
        try { Current?.Stop("superseded by a new playtest launch"); } catch { }
        var log = new PlaytestLog(engine);
        Current = log;
        return log;
    }

    // ── Writing ───────────────────────────────────────────────────────────

    /// <summary>A titled section header.</summary>
    public void Section(string title)
    {
        WriteRaw($"\n----- {title} -----\n");
    }

    /// <summary>A timestamped line.</summary>
    public void Line(string text) => WriteRaw($"[{DateTime.Now:HH:mm:ss.fff}] {text}\n");

    /// <summary>A raw key=value pair (no timestamp), aligned for the config dump.</summary>
    public void Kv(string key, object? value) => WriteRaw($"    {key,-26} = {Fmt(value)}\n");

    private void WriteRaw(string s)
    {
        if (_disabled) return;
        lock (_gate)
        {
            try
            {
                File.AppendAllText(Path, s);
                File.Copy(Path, _latest, overwrite: true);   // keep "latest" current for quick lookup
            }
            catch { /* logging must never throw */ }
        }
    }

    // Keep at most `max` per-launch logs (newest by write time); -1 = unlimited (no pruning). The fixed
    // playtest_latest.log and the one-file-per-engine targets are excluded from the count/deletion.
    private void PruneOldLogs(int max)
    {
        if (max < 0) return;
        try
        {
            var files = Directory.GetFiles(LogDir, "playtest_*.log")
                .Where(f => !System.IO.Path.GetFileName(f).Equals("playtest_latest.log", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Equals(Path, StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .ToList();
            // We're about to add one (this launch), so keep max-1 of the existing.
            for (int i = Math.Max(0, max - 1); i < files.Count; i++)
                try { files[i].Delete(); } catch { }
        }
        catch { }
    }

    // ── Full config / settings dump ───────────────────────────────────────

    /// <summary>
    /// Dumps EVERY public field/property of an object (so nothing is omitted as a project grows),
    /// including values left at their defaults. Used for scene + room settings and the playtest config.
    /// </summary>
    public void DumpObject(string label, object? obj)
    {
        Section(label);
        if (obj == null) { WriteRaw("    (null)\n"); return; }
        foreach (var (name, val) in Members(obj))
            Kv(name, val);
    }

    private static IEnumerable<(string, object?)> Members(object obj)
    {
        var t = obj.GetType();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object? v; try { v = p.GetValue(obj); } catch { v = "<err>"; }
            yield return (p.Name, v);
        }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object? v; try { v = f.GetValue(obj); } catch { v = "<err>"; }
            yield return (f.Name, v);
        }
    }

    private static string Fmt(object? v)
    {
        switch (v)
        {
            case null: return "(unset)";
            case bool b: return b ? "true" : "false";
            case string s: return s.Length == 0 ? "(empty)" : s;
            case sbyte or short or int or long:
            {
                long n = Convert.ToInt64(v);
                return n < 0 ? $"{n}" : $"{n} (0x{n:X})";
            }
            case byte or ushort or uint or ulong:
                return $"{v} (0x{Convert.ToUInt64(v):X})";
            case IEnumerable en and not string:
            {
                var items = en.Cast<object?>().Select(Fmt).ToList();
                return items.Count == 0 ? "(none)" : $"[{string.Join(", ", items)}] (count={items.Count})";
            }
            default:
            {
                string s2 = v.ToString() ?? "(?)";
                // A value type with no ToString override prints its type name — reflect its members instead
                // so e.g. RgbColor shows {R=.., G=.., B=..} rather than "MegatonHammer.Editor.RgbColor".
                if (s2 == v.GetType().FullName)
                    return "{" + string.Join(", ", Members(v).Select(m => $"{m.Item1}={Fmt(m.Item2)}")) + "}";
                return s2;
            }
        }
    }

    /// <summary>Dumps the inventory exhaustively — every tier (even unselected) and every toggle
    /// (on AND off), per the game catalogue — plus the mode and the exact payload the engine receives.</summary>
    public void DumpInventory(string mode, PlaytestInventory? inv, bool oot)
    {
        Section("INVENTORY");
        Kv("mode", mode);
        if (mode == "debug") WriteRaw("    (debug inventory — engine grants its full debug loadout; custom values below ignored)\n");
        if (inv == null) { WriteRaw("    (no inventory object)\n"); return; }
        Kv("hearts", inv.Hearts);
        Kv("payload (engine receives)", inv.ToPayloadJson());
        WriteRaw("    tiers (ALL, including unselected):\n");
        foreach (var t in InventoryCatalog.Tiers(oot))
        {
            int v = inv.Tier(t.Key);
            string opt = v >= 0 && v < t.Options.Length ? t.Options[v] : "?";
            WriteRaw($"      {t.Key,-14} = {v,-2} ({t.Label}: {opt})\n");
        }
        WriteRaw("    toggles (ALL, on/off):\n");
        foreach (var g in InventoryCatalog.Groups(oot))
        {
            WriteRaw($"      [{g.Name}]\n");
            foreach (var (key, label) in g.Items)
                WriteRaw($"        {(inv.Has(key) ? "[x]" : "[ ]")} {key,-22} {label}\n");
        }
    }

    /// <summary>Dumps the scene's settings and EVERY room's settings (all fields, defaults included).</summary>
    public void DumpScene(object scene, IEnumerable<object> roomSettings, object sceneSettings)
    {
        DumpObject("SCENE SETTINGS", sceneSettings);
        int i = 0;
        foreach (var rs in roomSettings)
            DumpObject($"ROOM {i++} SETTINGS", rs);
    }

    // ── Engine link: tail the engine's logs until the process exits ────────

    /// <summary>
    /// After the engine is launched, mirror its own log file(s) into this session and watch the process.
    /// When it exits (or, for a detached launch, when its log goes idle after the window closes), the
    /// session is finalized. Crash markers in the tailed output are highlighted.
    /// </summary>
    public void LinkEngine(Process? proc, IEnumerable<string> engineLogPaths, int idleCloseSeconds = 8)
    {
        if (_disabled) return;
        var paths = engineLogPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        Section("ENGINE LINK");
        Kv("process id", proc?.Id);
        foreach (var p in paths) Kv("tailing", p);

        _linkThread = new Thread(() => LinkLoop(proc, paths, idleCloseSeconds)) { IsBackground = true, Name = "mh-playtest-log-link" };
        _linkThread.Start();
    }

    private static readonly string[] CrashMarkers =
        ["Exception:", "CrashHandler", "Unhandled", "ASSERT", "assert", "EXCEPTION_", "Access violation",
         "Fatal", "FATAL", "abort()", "segfault", "RIP:", "BadVAddr", "MhLogCrash", "[mh] CRASH"];

    // High-signal markers kept from PJ64's verbose Project64.log (everything else at ",Debug," level is
    // dropped). Errors/warnings (lines without ",Debug,") are always kept.
    private static readonly string[] Pj64Keep =
        ["EmulationStarting", "uCode", "CIC", "RDRAM", "Expansion", "Initiate", "Initialize", "LoadN64Image",
         "LoadFileImage", "Stopping emulation", "plugin", "rdb", "SaveType", "Rom Loaded", "Game starting",
         "Game done", "StartEmulation"];

    private static bool KeepPj64TraceLine(string line)
    {
        // Keep all non-Debug lines (Error/Warning/Info), and the few Debug lines that carry boot/plugin/
        // emulation context worth seeing in a playtest log.
        if (!line.Contains(",Debug,", StringComparison.Ordinal)) return true;
        return Pj64Keep.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private void LinkLoop(Process? proc, List<string> paths, int idleCloseSeconds)
    {
        var pos = new Dictionary<string, long>();
        foreach (var p in paths) pos[p] = SafeLen(p);   // start at current end; only mirror NEW output
        var lastActivity = DateTime.Now;
        bool sawProcess = proc != null;

        while (true)
        {
            bool anyNew = false;
            foreach (var p in paths)
            {
                string tag = System.IO.Path.GetFileNameWithoutExtension(p);
                bool isPj64Trace = tag.Equals("Project64", StringComparison.OrdinalIgnoreCase);
                foreach (var line in ReadNew(p, pos))
                {
                    bool crash = CrashMarkers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase));
                    // PJ64's own Project64.log is extremely verbose at Debug level — keep only the
                    // playtest-relevant lines (errors/warnings + boot/plugin/emulation markers, and any
                    // crash) so the trace is communicative without flooding the log.
                    if (isPj64Trace && !crash && !KeepPj64TraceLine(line)) continue;
                    anyNew = true;
                    WriteRaw(crash ? $"  !! [{tag}] {line}\n" : $"     [{tag}] {line}\n");
                }
            }
            if (anyNew) lastActivity = DateTime.Now;

            bool processGone = sawProcess && SafeHasExited(proc);
            // Detached launches (UseShellExecute) sometimes hand back a short-lived shim process; if we
            // never had a live process, fall back to closing once the engine logs go idle for a while.
            bool idleClosed = !sawProcess && (DateTime.Now - lastActivity).TotalSeconds > idleCloseSeconds && pos.Values.Any(v => v > 0);

            if (processGone || idleClosed || _stopped)
            {
                // Final drain.
                foreach (var p in paths)
                {
                    string tag = System.IO.Path.GetFileNameWithoutExtension(p);
                    bool isPj64Trace = tag.Equals("Project64", StringComparison.OrdinalIgnoreCase);
                    foreach (var line in ReadNew(p, pos))
                    {
                        bool crash = CrashMarkers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase));
                        if (isPj64Trace && !crash && !KeepPj64TraceLine(line)) continue;
                        WriteRaw(crash ? $"  !! [{tag}] {line}\n" : $"     [{tag}] {line}\n");
                    }
                }
                Stop(processGone ? $"engine process exited (code {SafeExitCode(proc)})"
                                 : idleClosed ? "engine log idle — assumed closed" : "stopped");
                return;
            }
            Thread.Sleep(400);
        }
    }

    private static long SafeLen(string p) { try { return new FileInfo(p).Exists ? new FileInfo(p).Length : 0; } catch { return 0; } }
    private static bool SafeHasExited(Process? p) { try { return p == null || p.HasExited; } catch { return true; } }
    private static int SafeExitCode(Process? p) { try { return p?.ExitCode ?? -1; } catch { return -1; } }

    private static IEnumerable<string> ReadNew(string path, Dictionary<string, long> pos)
    {
        var lines = new List<string>();
        try
        {
            if (!File.Exists(path)) return lines;
            long start = pos.GetValueOrDefault(path);
            long len = new FileInfo(path).Length;
            if (len < start) start = 0;   // file was truncated/recreated at engine start
            if (len == start) return lines;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string txt = sr.ReadToEnd();
            pos[path] = len;
            foreach (var l in txt.Split('\n'))
            {
                var t = l.TrimEnd('\r');
                if (t.Length > 0) lines.Add(t);
            }
        }
        catch { /* file locked this tick — try next */ }
        return lines;
    }

    /// <summary>Finalizes the session (idempotent).</summary>
    public void Stop(string reason)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
        }
        WriteRaw($"\n[{DateTime.Now:HH:mm:ss.fff}] ===== PLAYTEST LOG ENDED: {reason} =====\n");
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
