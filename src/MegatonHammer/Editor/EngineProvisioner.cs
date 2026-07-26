using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MegatonHammer.Editor;

/// <summary>
/// First-run provisioning of the three playtest engines (Ship of Harkinian / 2Ship / Project64), so an
/// end-user only has to download and run the editor exe. The engines carry our mh_playtest + libultraship
/// patches, so upstream's stock binaries can't be used — instead the editor downloads OUR CI-built, already
/// patched binaries from the public repo's latest release and installs them under a writable per-user folder
/// (<see cref="AppPaths.EnginesDir"/>), then points <see cref="EditorSettings"/> at them.
///
/// A dev checkout that has the fork SOURCE trees (SoH/, 2Ship/ submodules) plus a build toolchain can instead
/// build from upstream via the existing scripts — that path is offered only when both are present.
/// </summary>
public static class EngineProvisioner
{
    public enum Engine { Soh, TwoShip, Pj64 }

    /// <param name="InstallSubdir">Folder under <see cref="AppPaths.EnginesDir"/> the engine installs into.</param>
    /// <param name="ExeName">The launcher exe filename.</param>
    /// <param name="Asset">The release asset (zip) name the CI publishes.</param>
    public sealed record EngineInfo(Engine Engine, string DisplayName, string ShortName,
                                    string InstallSubdir, string ExeName, string Asset, bool Optional);

    public static readonly IReadOnlyList<EngineInfo> Engines =
    [
        new(Engine.Soh,     "Ship of Harkinian",  "SoH (OoT)", "SoH",   "soh.exe",       "soh-win-x64.zip",   false),
        new(Engine.TwoShip, "2Ship2Harkinian",    "2Ship (MM)","2Ship", "2ship.exe",     "2ship-win-x64.zip", false),
        new(Engine.Pj64,    "Project64",          "PJ64 (N64)","pj64",  "Project64.exe", "pj64-win-x64.zip",  true),
    ];

    /// <summary>Base URL the engine bundles are fetched from. The public repo's "latest" release by default;
    /// overridable with the <c>MH_ENGINE_BASEURL</c> environment variable (e.g. to point at a staging build).</summary>
    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("MH_ENGINE_BASEURL") is { Length: > 0 } u
            ? u.TrimEnd('/')
            : "https://github.com/AgitationSkeleton/MegatonHammer_Public/releases/latest/download";

    // ── Detection ───────────────────────────────────────────────────────────

    /// <summary>The engine's exe if it's already usable — the configured settings path (if it exists on disk),
    /// else a previously-installed copy under <see cref="AppPaths.EnginesDir"/>. Null when not installed.</summary>
    public static string? ResolveExe(EngineInfo e)
    {
        string? configured = e.Engine switch
        {
            Engine.Soh     => EditorSettings.SohExePath,
            Engine.TwoShip => EditorSettings.TwoShipExePath,
            _              => EditorSettings.Project64Path,
        };
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return FindInstalledExe(e);
    }

    public static bool IsInstalled(EngineInfo e) => ResolveExe(e) != null;

    /// <summary>The exe inside this engine's install folder (top-level first, then a recursive search — the CI
    /// zip may or may not nest a subfolder), or null if not yet installed.</summary>
    private static string? FindInstalledExe(EngineInfo e)
    {
        var dir = AppPaths.EngineDir(e.InstallSubdir);
        if (!Directory.Exists(dir)) return null;
        var top = Path.Combine(dir, e.ExeName);
        if (File.Exists(top)) return top;
        try { return Directory.EnumerateFiles(dir, e.ExeName, SearchOption.AllDirectories).FirstOrDefault(); }
        catch { return null; }
    }

    private static void ApplyInstalledPath(EngineInfo e)
    {
        var exe = FindInstalledExe(e);
        if (exe == null) return;
        switch (e.Engine)
        {
            case Engine.Soh:     EditorSettings.SohExePath = exe; break;
            case Engine.TwoShip: EditorSettings.TwoShipExePath = exe; break;
            case Engine.Pj64:    EditorSettings.Project64Path = exe; break;
        }
    }

    // ── Download (the end-user path) ─────────────────────────────────────────

    /// <summary>Downloads the engine's CI-built bundle and extracts it into its per-user install folder, then
    /// points the settings at it. Reports byte progress while downloading. Throws on network / HTTP / extract
    /// failure (the caller shows the message and lets the user retry or skip).</summary>
    public static async Task DownloadAndInstallAsync(EngineInfo e, IProgress<BootstrapProgress> progress, CancellationToken ct)
    {
        var url = $"{BaseUrl}/{e.Asset}";
        var zipPath = Path.Combine(AppPaths.CacheDir, e.Asset);

        progress.Report(new($"Downloading {e.DisplayName}", 0, url));
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buf = new byte[131072];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                double? frac = total is > 0 ? (double)read / total.Value : null;
                string mb = total is > 0
                    ? $"{read / 1048576.0:0.0} / {total.Value / 1048576.0:0.0} MB"
                    : $"{read / 1048576.0:0.0} MB";
                progress.Report(new($"Downloading {e.DisplayName}", frac, mb));
            }
        }

        progress.Report(new($"Installing {e.DisplayName}", null, "extracting…"));
        var dir = AppPaths.EngineDir(e.InstallSubdir);
        if (Directory.Exists(dir)) { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
        Directory.CreateDirectory(dir);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, dir, overwriteFiles: true), ct);
        try { File.Delete(zipPath); } catch { }

        if (FindInstalledExe(e) == null)
            throw new InvalidOperationException($"{e.Asset} extracted but {e.ExeName} was not found inside it.");
        ApplyInstalledPath(e);
        progress.Report(new($"Installed {e.DisplayName}", 1.0, ResolveExe(e)));
    }

    // ── Build-from-source (dev checkout only) ────────────────────────────────

    /// <summary>The repo root of a dev checkout (the folder holding the SoH/2Ship submodules + forks/), located
    /// by walking up from the exe; null in a public/download-only build with no source tree.</summary>
    public static string? RepoRoot
    {
        get
        {
            var forks = AppPaths.Probe("forks");
            return forks != null ? Directory.GetParent(forks)?.FullName : null;
        }
    }

    /// <summary>True only when this is a dev checkout with the fork sources AND a usable build toolchain, so the
    /// "build from source" option can be offered. <paramref name="detail"/> explains what's missing otherwise.</summary>
    public static bool CanBuildFromSource(out string detail)
    {
        var root = RepoRoot;
        if (root == null || !Directory.Exists(Path.Combine(root, "forks")))
        { detail = "no fork source tree (download-only build)"; return false; }
        var missing = new List<string>();
        if (!OnPath("git")) missing.Add("git");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VCPKG_ROOT"))) missing.Add("VCPKG_ROOT env var");
        if (missing.Count > 0) { detail = "missing " + string.Join(", ", missing); return false; }
        detail = "ready";
        return true;
    }

    /// <summary>Builds the SoH/2Ship submodule engines from source using the repo's existing scripts (submodule
    /// init → apply-mh-patches → mh_configure → mh_build), streaming build output as progress lines. Dev-only.
    /// PJ64 has no in-tree source build here (its delta is overlaid onto a separate checkout), so it always
    /// downloads.</summary>
    public static async Task BuildFromSourceAsync(EngineInfo e, IProgress<BootstrapProgress> progress, CancellationToken ct)
    {
        var root = RepoRoot ?? throw new InvalidOperationException("No dev source tree to build from.");
        if (e.Engine == Engine.Pj64)
            throw new NotSupportedException("Project64 is provided as a prebuilt bundle; use download.");

        string forkDir = Path.Combine(root, e.InstallSubdir);   // SoH / 2Ship
        progress.Report(new($"Building {e.DisplayName}", null, "fetching submodule…"));
        await RunAsync("git", $"submodule update --init --recursive \"{e.InstallSubdir}\"", root, progress, ct);
        await RunAsync("cmd.exe", $"/c \"{Path.Combine(root, "forks", "apply-mh-patches.cmd")}\"", root, progress, ct);

        progress.Report(new($"Building {e.DisplayName}", null, "configuring (cmake)…"));
        await RunAsync("cmd.exe", $"/c \"{Path.Combine(forkDir, "mh_configure.cmd")}\"", forkDir, progress, ct);
        progress.Report(new($"Building {e.DisplayName}", null, "compiling (this can take a while)…"));
        await RunAsync("cmd.exe", $"/c \"{Path.Combine(forkDir, "mh_build.cmd")}\"", forkDir, progress, ct);

        // Point settings at the freshly built exe under the fork's Release dir.
        string built = Path.Combine(forkDir, "x64", "Release", e.ExeName);
        if (!File.Exists(built)) built = Directory.EnumerateFiles(forkDir, e.ExeName, SearchOption.AllDirectories).FirstOrDefault()
                                         ?? throw new InvalidOperationException($"Build finished but {e.ExeName} not found under {forkDir}.");
        if (e.Engine == Engine.Soh) EditorSettings.SohExePath = built; else EditorSettings.TwoShipExePath = built;
        progress.Report(new($"Built {e.DisplayName}", 1.0, built));
    }

    // ── Process + PATH helpers ───────────────────────────────────────────────

    private static async Task RunAsync(string exe, string args, string cwd, IProgress<BootstrapProgress> progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, ev) => { if (ev.Data != null) progress.Report(new(null!, null, ev.Data)); };
        p.ErrorDataReceived  += (_, ev) => { if (ev.Data != null) progress.Report(new(null!, null, ev.Data)); };
        if (!p.Start()) throw new InvalidOperationException($"Could not start {exe}.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} {args} exited with code {p.ExitCode}.");
    }

    private static bool OnPath(string tool)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("where", tool)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(4000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}

/// <summary>A single progress update from a bootstrap step. <see cref="Stage"/> names the current phase (null =
/// keep the current stage, this is just a log line); <see cref="Fraction"/> is 0..1 for a determinate bar (null =
/// marquee); <see cref="Line"/> is a status/log detail to show under the bar.</summary>
public sealed record BootstrapProgress(string? Stage, double? Fraction, string? Line);
