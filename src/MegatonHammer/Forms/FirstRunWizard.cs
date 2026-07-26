using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MegatonHammer.Editor;
using MegatonHammer.Rom;

namespace MegatonHammer.Forms;

/// <summary>
/// First-run setup wizard. Detects whether the SoH / 2Ship / PJ64 playtest engines are installed and, if not,
/// downloads our CI-built (already-patched) binaries into a per-user folder — so a fresh download of just the
/// editor exe becomes fully equipped. Also prompts (skippably) for the MD5-verified game ROMs the engines and
/// the vanilla-N64 path need. Shown once (or from Options ▸ "Set up playtest engines…").
/// </summary>
public sealed class FirstRunWizard : Form
{
    private static readonly Color BgDark  = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);
    private static readonly Color FgDim    = Color.FromArgb(150, 150, 150);
    private static readonly Color Accent   = Color.FromArgb(0, 122, 204);
    private static readonly Color Ok       = Color.FromArgb(78, 201, 120);
    private static readonly Color Warn      = Color.FromArgb(224, 108, 117);
    private static readonly Color BtnGrey  = Color.FromArgb(60, 60, 65);

    private readonly Label[] _engineStatus = new Label[EngineProvisioner.Engines.Count];
    private readonly RomRow[] _romRows;
    private readonly ProgressBar _bar;
    private readonly Label _stageLabel;
    private readonly TextBox _log;
    private readonly Button _setupBtn, _skipBtn, _doneBtn;
    private CancellationTokenSource? _cts;
    private bool _busy;

    private sealed record RomRow(string Key, TextBox Box, Label Status, Func<string?> Get, Action<string?> Set,
                                 Func<string, bool> Accept, bool Optional);

    public FirstRunWizard()
    {
        Text = "Megaton Hammer — First-run setup";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        BackColor = BgDark; ForeColor = FgNormal;
        ClientSize = new Size(660, 620);
        Font = UiFonts.Get("Segoe UI", 9f);

        int y = 18;
        AddTitle("Welcome to Megaton Hammer", ref y, 15f, FontStyle.Bold);
        AddParagraph(
            "To play-test your levels, the editor uses three engines — Ship of Harkinian (OoT), 2Ship2Harkinian "
          + "(MM) and Project64 (vanilla N64). Any that are missing can be downloaded and installed for you now. "
          + "You can also point the editor at your game ROMs; they're verified by MD5 and never redistributed.",
            ref y);
        y += 6;

        AddSectionHeader("Playtest engines", ref y);
        for (int i = 0; i < EngineProvisioner.Engines.Count; i++)
        {
            var e = EngineProvisioner.Engines[i];
            AddLabel($"{e.DisplayName}  ·  {e.ShortName}" + (e.Optional ? "  (optional)" : ""), 28, y, 300, FgNormal);
            _engineStatus[i] = AddLabel("", 340, y, 290, FgDim);
            y += 24;
        }
        y += 8;

        AddSectionHeader("Game ROMs  ·  MD5-verified, kept private", ref y);
        _romRows =
        [
            MakeRomRow("Ocarina of Time (USA v1.0)", ref y,
                () => EditorSettings.OotRomPath, v => EditorSettings.OotRomPath = v,
                p => RomFingerprint.IsExpectedRom(p, oot: true), optional: false),
            MakeRomRow("Majora's Mask (USA)", ref y,
                () => EditorSettings.MmRomPath, v => EditorSettings.MmRomPath = v,
                p => RomFingerprint.IsExpectedRom(p, oot: false), optional: false),
            MakeRomRow("OoT MQ debug — for vanilla N64 (optional)", ref y,
                () => EditorSettings.OotDebugRomPath, v => EditorSettings.OotDebugRomPath = v,
                p => RomFingerprint.Md5(p) is { } h && (h == RomFingerprint.OotDebugMd5 || h == RomFingerprint.OotDebugAltMd5),
                optional: true),
        ];
        y += 10;

        // ── progress area ──
        _stageLabel = AddLabel("", 28, y, 604, FgDim); y += 20;
        _bar = new ProgressBar { Left = 28, Top = y, Width = 604, Height = 16, Style = ProgressBarStyle.Continuous, Maximum = 1000 };
        Controls.Add(_bar); y += 24;
        _log = new TextBox
        {
            Left = 28, Top = y, Width = 604, Height = 96, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BackColor = BgInput, ForeColor = FgDim, BorderStyle = BorderStyle.FixedSingle, Font = UiFonts.Get("Consolas", 8f),
        };
        Controls.Add(_log); y += 106;

        // ── buttons ──
        _skipBtn = MakeButton("Skip for now", 28, y, 110, BtnGrey);
        _skipBtn.Click += (_, _) => Finish(skipped: true);
        _setupBtn = MakeButton("Download && install missing", 372, y, 168, Accent, Color.White);
        _setupBtn.Click += SetupOrCancel;   // starts setup, or cancels while busy
        _doneBtn = MakeButton("Done", 552, y, 80, BtnGrey);
        _doneBtn.Click += (_, _) => Finish(skipped: false);

        AcceptButton = _setupBtn;
    }

    // Refresh once the window is shown, so the WinForms SynchronizationContext + control handles exist for
    // the async ROM MD5 checks (doing it in the constructor can race the message loop).
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshStatus();
    }

    // ── status / detection ──

    private void RefreshStatus()
    {
        for (int i = 0; i < EngineProvisioner.Engines.Count; i++)
        {
            bool ok = EngineProvisioner.IsInstalled(EngineProvisioner.Engines[i]);
            _engineStatus[i].Text = ok ? "●  Installed" : "○  Not installed";
            _engineStatus[i].ForeColor = ok ? Ok : FgDim;
        }
        foreach (var r in _romRows) UpdateRomStatus(r);

        if (!_busy)   // while busy the primary button is "Cancel" — don't fight it
        {
            bool anyEngineMissing = EngineProvisioner.Engines.Any(e => !EngineProvisioner.IsInstalled(e));
            _setupBtn.Enabled = anyEngineMissing;
            _setupBtn.Text = anyEngineMissing ? "Download && install missing" : "All engines installed";
        }
    }

    private void UpdateRomStatus(RomRow r)
    {
        var path = r.Get();
        r.Box.Text = path ?? "";
        if (string.IsNullOrWhiteSpace(path))
        {
            r.Status.Text = r.Optional ? "—  none (optional)" : "—  not set";
            r.Status.ForeColor = FgDim;
            return;
        }
        r.Status.Text = "checking…"; r.Status.ForeColor = FgDim;
        var box = r.Box; var status = r.Status; var accept = r.Accept;
        Task.Run(() => accept(path)).ContinueWith(t =>
        {
            bool ok = t.Status == TaskStatus.RanToCompletion && t.Result;
            status.Text = ok ? "✓  recognized" : "✗  MD5 doesn't match";
            status.ForeColor = ok ? Ok : Warn;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ── engine setup ──

    private async Task RunSetupAsync()
    {
        var missing = EngineProvisioner.Engines.Where(e => !EngineProvisioner.IsInstalled(e)).ToList();
        if (missing.Count == 0) { RefreshStatus(); return; }

        bool build = EngineProvisioner.CanBuildFromSource(out string why);
        if (build)
        {
            var ans = MessageBox.Show(this,
                "A developer source tree and build toolchain were detected.\n\n" +
                "Build the engines from source (slower, needs the toolchain), or download the prebuilt binaries?\n\n" +
                "Yes = build from source   ·   No = download prebuilt",
                "Build or download?", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (ans == DialogResult.Cancel) return;
            build = ans == DialogResult.Yes;
        }

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<BootstrapProgress>(OnProgress);
        try
        {
            for (int i = 0; i < missing.Count; i++)
            {
                var e = missing[i];
                _stageLabel.Text = $"Engine {i + 1} of {missing.Count}: {e.DisplayName}";
                _bar.Style = ProgressBarStyle.Marquee;
                // PJ64 has no in-tree source build; it always downloads even in dev mode.
                if (build && e.Engine != EngineProvisioner.Engine.Pj64)
                    await EngineProvisioner.BuildFromSourceAsync(e, progress, _cts.Token);
                else
                    await EngineProvisioner.DownloadAndInstallAsync(e, progress, _cts.Token);
                RefreshStatus();
            }
            _stageLabel.Text = "All engines are ready.";
            _bar.Style = ProgressBarStyle.Continuous; _bar.Value = _bar.Maximum;
        }
        catch (OperationCanceledException)
        {
            _stageLabel.Text = "Cancelled.";
            AppendLog("— cancelled —");
        }
        catch (Exception ex)
        {
            _stageLabel.Text = "Setup failed — see the log below. You can retry or skip.";
            AppendLog("ERROR: " + ex.Message);
            MessageBox.Show(this,
                "Could not finish setting up the engines:\n\n" + ex.Message +
                "\n\nCheck your internet connection and try again, or Skip and set them up later from Options.",
                "Setup incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _bar.Style = ProgressBarStyle.Continuous;
            SetBusy(false);
            RefreshStatus();
        }
    }

    private void OnProgress(BootstrapProgress p)
    {
        if (p.Stage != null) _stageLabel.Text = p.Stage;
        if (p.Fraction is { } f) { _bar.Style = ProgressBarStyle.Continuous; _bar.Value = (int)Math.Clamp(f * 1000, 0, 1000); }
        else if (_bar.Style != ProgressBarStyle.Marquee) _bar.Style = ProgressBarStyle.Marquee;
        if (!string.IsNullOrEmpty(p.Line)) AppendLog(p.Line!);
    }

    private void AppendLog(string line)
    {
        if (_log.TextLength > 24000) _log.Text = _log.Text[^12000..];
        _log.AppendText(line + Environment.NewLine);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _bar.Maximum = 1000;
        _skipBtn.Enabled = _doneBtn.Enabled = !busy;
        foreach (var r in _romRows) r.Box.Enabled = !busy;
        _setupBtn.Text = busy ? "Cancel" : "Download && install missing";
        _setupBtn.Enabled = true;
    }

    // While busy the primary button becomes Cancel; otherwise it starts setup. Wired once in SetBusy.
    private async void SetupOrCancel(object? s, EventArgs e)
    {
        if (_busy) { _cts?.Cancel(); return; }
        await RunSetupAsync();
    }

    private void Finish(bool skipped)
    {
        if (_busy) { _cts?.Cancel(); return; }
        EditorSettings.FirstRunBootstrapDone = true;
        DialogResult = skipped ? DialogResult.Cancel : DialogResult.OK;
        Close();
    }

    // ── layout helpers ──

    private void AddTitle(string text, ref int y, float size, FontStyle style)
    {
        Controls.Add(new Label { Text = text, Left = 26, Top = y, Width = 610, Height = 30, ForeColor = FgNormal,
            Font = UiFonts.Get("Segoe UI", size, style) });
        y += 34;
    }

    private void AddParagraph(string text, ref int y)
    {
        var l = new Label { Text = text, Left = 28, Top = y, Width = 604, ForeColor = FgDim, AutoSize = false,
            MaximumSize = new Size(604, 0), Font = UiFonts.Get("Segoe UI", 9f) };
        l.Height = TextRenderer.MeasureText(text, l.Font, new Size(604, 0), TextFormatFlags.WordBreak).Height + 4;
        Controls.Add(l);
        y += l.Height + 6;
    }

    private void AddSectionHeader(string text, ref int y)
    {
        Controls.Add(new Label { Text = text, Left = 26, Top = y, Width = 610, Height = 20, ForeColor = Accent,
            Font = UiFonts.Get("Segoe UI", 9.5f, FontStyle.Bold) });
        y += 24;
    }

    private Label AddLabel(string text, int x, int yy, int w, Color fg) =>
        AddLabelTo(text, x, yy, w, fg);

    private Label AddLabelTo(string text, int x, int yy, int w, Color fg)
    {
        var l = new Label { Text = text, Left = x, Top = yy, Width = w, Height = 20, ForeColor = fg,
            Font = UiFonts.Get("Segoe UI", 9f), TextAlign = ContentAlignment.MiddleLeft };
        Controls.Add(l);
        return l;
    }

    private RomRow MakeRomRow(string label, ref int y, Func<string?> get, Action<string?> set,
                              Func<string, bool> accept, bool optional)
    {
        AddLabel(label, 28, y, 604, FgNormal); y += 20;
        var box = new TextBox { Left = 28, Top = y, Width = 430, Height = 22, ReadOnly = true, BackColor = BgInput,
            ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle, AllowDrop = true };
        var browse = MakeButton("Browse…", 466, y - 1, 76, BtnGrey);
        var status = AddLabel("", 550, y, 100, FgDim);
        Controls.Add(box);
        var row = new RomRow(label, box, status, get, set, accept, optional);

        browse.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog { Filter = "N64 ROM (*.z64;*.n64)|*.z64;*.n64|All files (*.*)|*.*" };
            if (ofd.ShowDialog(this) == DialogResult.OK) { set(ofd.FileName); UpdateRomStatus(row); }
        };
        box.DragEnter += (_, ev) => ev.Effect = ev.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        box.DragDrop += (_, ev) =>
        {
            if (ev.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) { set(files[0]); UpdateRomStatus(row); }
        };
        y += 28;
        return row;
    }

    private Button MakeButton(string text, int x, int yy, int w, Color bg, Color? fg = null)
    {
        var b = new Button { Text = text, Left = x, Top = yy, Width = w, Height = 30, BackColor = bg,
            ForeColor = fg ?? FgNormal, FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 9f) };
        b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 85);
        Controls.Add(b);
        return b;
    }

    /// <summary>True if the wizard should be shown at startup: not yet completed/skipped, and something is
    /// actually missing (a fresh download-only build, or a dev tree before the engines are built).</summary>
    public static bool ShouldShowAtStartup()
    {
        if (EditorSettings.FirstRunBootstrapDone) return false;
        bool engineMissing = EngineProvisioner.Engines.Any(e => !e.Optional && !EngineProvisioner.IsInstalled(e));
        bool romMissing = !RomFingerprint.IsExpectedRom(EditorSettings.OotRomPath, true)
                       || !RomFingerprint.IsExpectedRom(EditorSettings.MmRomPath, false);
        return engineMissing || romMissing;
    }
}
