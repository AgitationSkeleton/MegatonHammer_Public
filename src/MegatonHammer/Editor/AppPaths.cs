using System;
using System.IO;
using System.Linq;

namespace MegatonHammer.Editor;

/// <summary>
/// Central path resolver so no machine-specific absolute path is baked into the source. Everything
/// resolves at RUNTIME from (1) an environment variable, (2) a walk-up probe of the surrounding folder
/// tree, or (3) a writable per-user directory — and degrades gracefully (null / a missing path) when a
/// data source isn't present. That keeps the dev tree working with zero hardcoded drive paths while a
/// public checkout (no decomp/ROM trees) simply falls back to the built-in data.
/// </summary>
public static class AppPaths
{
    /// <summary>The directory the app runs from.</summary>
    public static string BaseDir => AppContext.BaseDirectory;

    // Walk up from BaseDir looking for an ancestor that contains a child folder named `name`; returns that
    // child's full path, else null. Lets READ_ONLY_SourceCodes / READ_ONLY_GameROMs resolve on the dev
    // machine without any hardcoded drive path, while a public build (no such ancestor) just gets null.
    private static string? ProbeUp(string name)
    {
        var dir = new DirectoryInfo(BaseDir);
        for (int i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, name);
            if (Directory.Exists(cand)) return cand;
        }
        return null;
    }

    /// <summary>Public walk-up probe: the full path of the nearest ancestor-child folder named
    /// <paramref name="name"/> (e.g. the live "SoH" submodule in the dev tree), or null.</summary>
    public static string? Probe(string name) => ProbeUp(name);

    private static string? _sources; private static bool _sourcesDone;
    /// <summary>Base of the read-only reference sources (SharpOcarina XML, OoT/MM decomp). Env
    /// <c>MH_SOURCES</c> wins; else a walk-up probe for a "READ_ONLY_SourceCodes" folder; else null
    /// (name/schema features fall back to the built-in curated data).</summary>
    public static string? Sources
    {
        get
        {
            if (!_sourcesDone)
            {
                _sources = Environment.GetEnvironmentVariable("MH_SOURCES");
                if (string.IsNullOrEmpty(_sources) || !Directory.Exists(_sources))
                    _sources = ProbeUp("READ_ONLY_SourceCodes");
                _sourcesDone = true;
            }
            return _sources;
        }
    }

    /// <summary>Full path to a file under <see cref="Sources"/>, or null if Sources is unavailable or the
    /// file is missing (caller should degrade to built-ins).</summary>
    public static string? SourceFile(params string[] parts)
    {
        if (Sources == null) return null;
        var p = Path.Combine(new[] { Sources }.Concat(parts).ToArray());
        return File.Exists(p) ? p : null;
    }

    /// <summary>Full path to a directory under <see cref="Sources"/>, or null if unavailable/missing.</summary>
    public static string? SourceDir(params string[] parts)
    {
        if (Sources == null) return null;
        var p = Path.Combine(new[] { Sources }.Concat(parts).ToArray());
        return Directory.Exists(p) ? p : null;
    }

    private static string? _roms; private static bool _romsDone;
    /// <summary>Where the user's ROMs live. Env <c>MH_ROMS</c> wins; else a walk-up probe for
    /// "READ_ONLY_GameROMs"; else a "roms" folder beside the app. May not exist.</summary>
    public static string Roms
    {
        get
        {
            if (!_romsDone)
            {
                _roms = Environment.GetEnvironmentVariable("MH_ROMS");
                if (string.IsNullOrEmpty(_roms)) _roms = ProbeUp("READ_ONLY_GameROMs") ?? Path.Combine(BaseDir, "roms");
                _romsDone = true;
            }
            return _roms!;
        }
    }
    public static string Rom(string file) => Path.Combine(Roms, file);

    private static string? _logDir;
    /// <summary>A writable per-user directory for logs (never the source tree).</summary>
    public static string LogDir
    {
        get
        {
            if (_logDir == null)
            {
                _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MegatonHammer", "logs");
                try { Directory.CreateDirectory(_logDir); } catch { _logDir = Path.GetTempPath(); }
            }
            return _logDir;
        }
    }
    public static string Log(string file) => Path.Combine(LogDir, file);
}
