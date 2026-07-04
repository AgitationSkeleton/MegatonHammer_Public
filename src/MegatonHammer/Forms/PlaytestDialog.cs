using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using MegatonHammer.Editor;
using MegatonHammer.Export;
using MegatonHammer.Rom;

namespace MegatonHammer.Forms;

/// <summary>
/// Playtest launcher: packs the scene into a mod O2R for SoH/2Ship and launches it,
/// with Link's age and a starting inventory (empty / custom checklist / debug save).
/// </summary>
public sealed class PlaytestDialog : Form
{
    private static readonly Color BgDark   = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput  = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);
    private static readonly Color Accent   = Color.FromArgb(0, 122, 204);

    private readonly ZScene _scene;
    private readonly bool _mm;
    private readonly TextBox _exeBox;
    private readonly ComboBox _sceneCombo;     // which existing scene to replace (friendly names)
    private readonly List<int> _sceneIds = []; // scene id per combo row
    private readonly RadioButton _replaceMode, _appendMode;
    private readonly RadioButton _child, _adult;
    private readonly RadioButton _invEmpty, _invCustom, _invDebug;
    private readonly Button _editInv;
    private readonly Label _invSummary;
    private PlaytestInventory _inv;   // the custom starting inventory (persisted per game)
    private readonly Func<string, Bitmap?>? _texResolver;   // brush texture name → bitmap, for textured export
    private readonly IReadOnlyList<ZScene> _allScenes;      // every scene in the document (for multi-level packing)
    private CheckBox? _multiScene;
    private string? _musicPath;                             // chosen custom-music .seq, or null
    private Label? _musicLabel;

    // When set, this dialog drives the N64/PJ64 playtest instead of the SoH/2Ship O2R pack: OnLaunch builds
    // the same PlaytestConfig (append/replace + inventory) and hands it to this callback. Lets the N64 path
    // offer the SAME append-vs-overwrite + inventory choices as SoH/2Ship (they were previously skipped, so
    // N64 always ran overwrite mode with no prompt).
    private readonly Action<PlaytestConfig>? _n64Launch;
    private bool N64 => _n64Launch != null;

    public PlaytestDialog(ZScene scene, string? defaultExe, bool mm = false, Func<string, Bitmap?>? texResolver = null,
                          IReadOnlyList<ZScene>? allScenes = null, Action<PlaytestConfig>? n64Launch = null)
    {
        _scene = scene;
        _mm    = mm;
        _texResolver = texResolver;
        _allScenes = allScenes ?? new[] { scene };
        _n64Launch = n64Launch;

        Text            = n64Launch != null ? "Playtest — Megaton Hammer (N64 / Project64)"
                        : mm ? "Playtest — Megaton Hammer (MM / 2Ship)" : "Playtest — Megaton Hammer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterParent;
        MaximizeBox     = false; MinimizeBox = false;
        ClientSize      = new Size(520, 548);
        BackColor       = BgDark; ForeColor = FgNormal;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        int y = 14;
        // N64 mode uses Project64 (configured in Options); the SoH/2Ship exe box is hidden/ignored there.
        Controls.Add(Label(N64 ? "Target: Project64 (N64) — configured in Options → Playtest"
                                : "Game build (soh.exe / 2ship.exe):", 14, y)); y += 20;
        _exeBox = Input(GuessExe(defaultExe, mm), 14, y, 400);
        var browse = Button("Browse…", 420, y - 2, 84); browse.Click += (_, _) => BrowseExe();
        if (!N64) { Controls.Add(_exeBox); Controls.Add(browse); y += 34; }
        else y += 6;

        // Append the level as a NEW scene (written into a spare/unused dev-test slot, so no real game
        // scene is clobbered and there's no inherited intro), or REPLACE a chosen existing scene
        // (picked by friendly name — no more raw hex slot, which is how a flat-plane test accidentally
        // overrode Kakariko Village + played its intro).
        // Each radio set lives in its OWN panel: WinForms makes RadioButtons that share an immediate
        // parent mutually exclusive, so without separate containers the scene-mode, age and inventory
        // groups would all behave as one group.
        var modePanel = GroupPanel(y, 26);
        // Scene mode is REMEMBERED across sessions for all engines/games (EditorSettings.PlaytestAppend).
        // First-ever use falls back to the per-context default: MM-on-N64 → Replace (overwrite Termina, the
        // long-proven path), everything else → Append (OoT-N64 appends into the disposable SCENE_TEST01 slot,
        // OTR into SCENE_MH_APPEND — both non-destructive). Both modes work on N64 now.
        bool appendDefault = EditorSettings.PlaytestAppend ?? !(N64 && _mm);
        _appendMode  = Radio("Append as new scene", 24, 2, appendDefault);
        _replaceMode = Radio("Replace existing scene:", 220, 2, !appendDefault);
        foreach (var r in new[] { _appendMode, _replaceMode })
        { r.CheckedChanged += (_, _) => _sceneCombo!.Enabled = _replaceMode.Checked; modePanel.Controls.Add(r); }
        y += 26;

        _sceneCombo = new ComboBox
        {
            Left = 24, Top = y, Width = 482, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f),
            MaxDropDownItems = 24, Enabled = false,
        };
        PopulateSceneCombo(mm);
        Controls.Add(_sceneCombo); y += 30;

        // Age — OoT only. MM has no child/adult; its "adult" form is Fierce Deity, chosen via mask/form,
        // not here. Keep the radios (default Child = human) so downstream code is unchanged, just hide the UI.
        // Age is remembered across launches (EditorSettings.PlaytestAdult) so it isn't re-picked every time.
        _child = Radio("Child", 24, 2, !EditorSettings.PlaytestAdult);
        _adult = Radio("Adult", 130, 2, EditorSettings.PlaytestAdult);
        if (!mm)
        {
            Controls.Add(Header("LINK'S AGE", y)); y += 24;
            var agePanel = GroupPanel(y, 26);
            agePanel.Controls.Add(_child); agePanel.Controls.Add(_adult); y += 30;
        }

        // Inventory — persisted per game, edited via the full inventory editor.
        _inv = PlaytestInventory.FromJson(EditorSettings.GetInventoryJson(mm));
        if (_inv.Toggles.Count == 0 && _inv.Tiers.Count == 0 && EditorSettings.GetInventoryJson(mm) == null)
            _inv = PlaytestInventory.Default(!mm);   // first run → game's default loadout

        Controls.Add(Header("INVENTORY", y)); y += 24;
        var invPanel = GroupPanel(y, 26);
        _invEmpty  = Radio("Empty", 24, 2, false);
        _invCustom = Radio("Custom", 120, 2, true);
        _invDebug  = Radio("Use Debug Inventory", 230, 2, false);
        foreach (var r in new[] { _invEmpty, _invCustom, _invDebug })
        { r.CheckedChanged += (_, _) => UpdateItemListEnabled(); invPanel.Controls.Add(r); }
        y += 28;

        _editInv = Button("Edit Inventory…", 24, y, 130); _editInv.Click += (_, _) => EditInventory();
        Controls.Add(_editInv);
        _invSummary = new Label
        {
            Left = 164, Top = y + 4, Width = 340, Height = 90, ForeColor = Color.FromArgb(170, 200, 170),
            Font = new Font("Segoe UI", 8f), Text = "",
        };
        Controls.Add(_invSummary);
        y += 96;

        // ── Level set & music ──
        Controls.Add(Header("LEVEL SET & MUSIC", y)); y += 24;
        _multiScene = new CheckBox
        {
            Left = 24, Top = y, Width = 482, Height = 20, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5f),
            Text = $"Pack all {_allScenes.Count} scenes as a multi-level set (append mode)",
            Enabled = _allScenes.Count > 1,
            Checked = _allScenes.Count > 1 && !N64,   // N64 playtest injects a single scene only
        };
        if (!N64) { Controls.Add(_multiScene); y += 26; }   // multi-level packing is SoH/2Ship-only

        var musicBtn = Button("Custom music (.seq)…", 24, y, 150); musicBtn.Click += (_, _) => PickMusic();
        Controls.Add(musicBtn);
        _musicLabel = new Label { Left = 184, Top = y + 4, Width = 320, Height = 16, ForeColor = Color.FromArgb(170, 200, 170),
            Font = new Font("Segoe UI", 8f), Text = "none (uses the scene's selected vanilla sequence)" };
        Controls.Add(_musicLabel); y += 30;

        var note = new Label
        {
            Left = 14, Top = y, Width = 492, Height = 16, ForeColor = Color.FromArgb(150,150,150),
            Font = new Font("Segoe UI", 7.5f), Text = "Packs a mod O2R next to the exe and launches it; the engine boots straight into the level (no intro). Requires the Megaton-Hammer-modified engine.",
        };
        Controls.Add(note); y += 22;

        var launch = Button("Launch Playtest", 300, y, 120); launch.BackColor = Accent; launch.ForeColor = Color.White;
        launch.Click += OnLaunch;
        var cancel = Button("Cancel", 426, y, 80); cancel.Click += (_, _) => Close();
        Controls.Add(launch); Controls.Add(cancel);
        AcceptButton = launch; CancelButton = cancel;

        UpdateItemListEnabled();
    }

    private void UpdateItemListEnabled()
    {
        _editInv.Enabled = _invCustom.Checked;
        UpdateInventorySummary();
    }

    private void UpdateInventorySummary()
    {
        if (!_invCustom.Checked) { _invSummary.Text = _invDebug.Checked ? "Engine debug inventory (full loadout)." : "Empty inventory (fresh save)."; return; }
        bool oot = !_mm;
        var tierBits = InventoryCatalog.Tiers(oot)
            .Where(t => _inv.Tier(t.Key) > 0)
            .Select(t => $"{t.Label}: {t.Options[_inv.Tier(t.Key)]}");
        int toggles = _inv.Toggles.Count;
        _invSummary.Text = $"{_inv.Hearts} hearts · {toggles} item(s)/mask(s)/song(s)\n"
            + string.Join(", ", tierBits.Take(6))
            + (tierBits.Count() > 6 ? " …" : "");
    }

    private void PickMusic()
    {
        using var ofd = new OpenFileDialog { Filter = "N64 sequence (*.seq;*.aseq;*.zseq)|*.seq;*.aseq;*.zseq|All files (*.*)|*.*", Title = "Custom playtest music" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        _musicPath = ofd.FileName;
        _musicLabel!.Text = $"{Path.GetFileName(_musicPath)} → seqId 0x{SequenceInjector.HostSeqId(_mm):X2}";
    }

    private void EditInventory()
    {
        using var dlg = new InventoryDialog(_mm, _inv);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _inv = dlg.Result;
            EditorSettings.SetInventoryJson(_mm, _inv.ToJson());   // remember across restarts
            UpdateInventorySummary();
        }
    }

    private void OnLaunch(object? sender, EventArgs e)
    {
        DiagnosticLog.Begin($"Playtest — {(_mm ? "2Ship (MM)" : "SoH (OoT)")}");

        string exe = _exeBox.Text.Trim();
        if (!N64)
        {
            DiagnosticLog.Step($"engine exe: {exe}");
            if (!File.Exists(exe)) { DiagnosticLog.Fail("engine exe not found"); Warn("Select a valid soh.exe / 2ship.exe."); return; }
        }
        bool append = _appendMode.Checked;
        EditorSettings.PlaytestAppend = append;   // remember scene mode across sessions (all engines/games)
        int slot;
        if (append)
        {
            slot = AppendSlotId();   // a spare/unused dev-test scene slot — no real scene clobbered
            DiagnosticLog.Step($"append as new scene (spare slot 0x{slot:X2})");
        }
        else
        {
            int idx = _sceneCombo.SelectedIndex;
            if (idx < 0 || idx >= _sceneIds.Count) { DiagnosticLog.Fail("no scene selected"); Warn("Pick a scene to replace."); return; }
            slot = _sceneIds[idx];
            DiagnosticLog.Step($"replace scene: 0x{slot:X2} ({_sceneCombo.SelectedItem})");
        }

        // #20: the name shown at the tail of the engine's debug warp list — prefer the scene's area name,
        // then its scene name, else the "Megaton Project" default.
        string display = !string.IsNullOrWhiteSpace(_scene.Settings.AreaName) ? _scene.Settings.AreaName.Trim()
                       : !string.IsNullOrWhiteSpace(_scene.Name) ? _scene.Name.Trim() : "Megaton Project";
        var cfg = new PlaytestConfig
        {
            TargetSceneId = slot,
            Append = append,
            Adult = _adult.Checked,
            Inventory = _invDebug.Checked ? "debug" : _invCustom.Checked ? "custom" : "empty",
            DisplayName = display,
        };
        if (_invCustom.Checked)
        {
            cfg.InventoryPayload = _inv.ToPayloadJson();
            EditorSettings.SetInventoryJson(_mm, _inv.ToJson());   // persist the loadout used
        }
        if (!_mm) EditorSettings.PlaytestAdult = _adult.Checked;   // remember the age for next launch (OoT only)
        DiagnosticLog.Step($"age={(cfg.Adult ? "adult" : "child")} inventory={cfg.Inventory} hearts={_inv.Hearts} toggles={_inv.Toggles.Count} rooms={_scene.Rooms.Count}");

        // N64/PJ64: hand the config (append/replace + inventory) to the Project64 launcher and close. The
        // launcher does its own decompress/inject/CRC + PJ64 launch + logging (Project64Playtest.Launch).
        if (N64)
        {
            DiagnosticLog.Step($"N64 playtest: {(append ? $"APPEND (spare slot, entrance-redirect)" : "overwrite")} inventory={cfg.Inventory}");
            try { _n64Launch!(cfg); DialogResult = DialogResult.OK; Close(); }
            catch (Exception ex) { DiagnosticLog.Fail($"N64 launch: {ex.Message}"); Warn($"Project64 playtest failed:\n{ex.Message}"); }
            return;
        }

        // Per-playtest session log: records the COMPLETE launch state (every scene/room setting incl.
        // defaults, the playtest config, the full inventory + mode, the injection manifest via mirrored
        // DiagnosticLog steps) and then links to the engine's own log. Location is fixed: see PlaytestLog.LogDir.
        var plog = PlaytestLog.Begin(_mm ? "2Ship-MM" : "SoH-OoT");
        plog.Section("PLAYTEST CONFIG");
        plog.Kv("engine exe", exe);
        plog.Kv("game", _mm ? "MM (2Ship)" : "OoT (SoH)");
        plog.Kv("mode", append ? "append (new scene)" : "replace");
        plog.Kv("target scene slot", $"0x{slot:X2}");
        plog.Kv("multi-scene set", (_multiScene?.Checked ?? false) && _allScenes.Count > 1 && append);
        plog.Kv("custom music", _musicPath ?? "(none)");
        plog.DumpObject("PLAYTEST CONFIG (raw)", cfg);
        plog.DumpInventory(cfg.Inventory, _inv, !_mm);
        plog.DumpScene(_scene, _scene.Rooms.Select(r => (object)r.Settings), _scene.Settings);

        try
        {
            string exeDir = Path.GetDirectoryName(exe)!;

            // The engine can't boot our mod O2R without its base game archive (oot.o2r / mm.o2r),
            // which SoH/2Ship generate the first time they run against your ROM. If it's missing,
            // offer to launch the engine so it can generate it, rather than failing cryptically.
            bool archive = EditorSettings.GameArchiveInDir(exeDir, _mm);
            DiagnosticLog.Step($"game archive present: {archive}");
            if (!archive)
            {
                var r = MessageBox.Show(this,
                    $"No game archive ({(_mm ? "mm.o2r" : "oot.o2r")}) was found next to the executable.\n\n" +
                    "Ship of Harkinian / 2Ship generate this the first time they run, by importing your ROM.\n\n" +
                    "Launch the engine now so it can generate the archive?  (Then run the playtest again.)\n\n" +
                    "Yes = launch engine · No = pack and launch anyway · Cancel = abort",
                    "Game Archive Missing", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (r == DialogResult.Cancel) { DiagnosticLog.Step("user cancelled (no archive)"); return; }
                if (r == DialogResult.Yes)
                {
                    DiagnosticLog.Step("launching engine to generate archive");
                    EditorSettings.SetEngineExe(_mm, exe);
                    Process.Start(new ProcessStartInfo { FileName = exe, WorkingDirectory = exeDir, UseShellExecute = true });
                    DiagnosticLog.Ok("engine launched (archive-generation pass)");
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }
                DiagnosticLog.Step("user chose to pack/launch anyway");
            }

            string modsDir = Path.Combine(exeDir, "mods");
            string o2r = Path.Combine(modsDir, "mh_playtest.o2r");
            bool multi = (_multiScene?.Checked ?? false) && _allScenes.Count > 1 && append;
            DiagnosticLog.Step($"packing mod O2R: {o2r} ({(multi ? $"multi-level ×{_allScenes.Count}" : "single scene")})");
            if (multi)
                O2RPacker.PackOtrMulti(_allScenes, o2r, cfg, mm: _mm, texResolver: _texResolver);
            else
                O2RPacker.PackOtr(_scene, o2r, cfg, mm: _mm, texResolver: _texResolver);   // native OTR resources override the target scene

            // Music injection (OTR): a user-picked sequence file, OR a cross-game track extracted from the
            // OTHER game's ROM. Both are wrapped as an OSEQ resource claiming a valid vanilla host seqId that
            // the fork boot hook force-maps; the scene's 0x15 references that same id (see OtrSceneWriter).
            int hostSeq = SequenceInjector.HostSeqId(_mm);
            byte[]? seqData = null; string seqSrc = "";
            if (_scene.Settings.MusicCrossGame)
            {
                string oppRom = EditorSettings.OppositeRomPath(!_mm) ?? "";   // native OoT -> MM ROM, native MM -> OoT ROM
                if (!string.IsNullOrEmpty(oppRom) && File.Exists(oppRom))
                {
                    try
                    {
                        seqData = AudioSeqExtractor.Extract(new RomImage(oppRom), _scene.Settings.MusicSeq);
                        seqSrc = $"cross-game seq 0x{_scene.Settings.MusicSeq:X2} from {(_mm ? "OoT" : "MM")} ROM";
                    }
                    catch (Exception mex) { DiagnosticLog.Error("cross-game music extract failed", mex); }
                }
                else DiagnosticLog.Step("cross-game music: opposite-game ROM not configured");
            }
            else if (_musicPath != null && File.Exists(_musicPath))
            {
                try { seqData = File.ReadAllBytes(_musicPath); seqSrc = Path.GetFileName(_musicPath); }
                catch (Exception mex) { DiagnosticLog.Error("music file read failed", mex); }
            }
            if (seqData != null && seqData.Length > 0)
            {
                try { SequenceInjector.PackInto(o2r, hostSeq, seqData); DiagnosticLog.Ok($"injected sequence ({seqSrc}) -> host seqId 0x{hostSeq:X2} ({seqData.Length}B)"); }
                catch (Exception mex) { DiagnosticLog.Error("music inject failed", mex); }
            }
            long size = File.Exists(o2r) ? new FileInfo(o2r).Length : -1;
            DiagnosticLog.Ok($"packed mod O2R ({size} bytes)");

            EditorSettings.SetEngineExe(_mm, exe);          // remember this build for next time
            DiagnosticLog.Step("starting engine process");
            var proc = Process.Start(new ProcessStartInfo { FileName = exe, WorkingDirectory = exeDir, UseShellExecute = true });
            DiagnosticLog.Ok("engine launched");

            // Maintain the live logging link with the engine: tail its boot log + crash log into the
            // session log, capture any crash output, and stop when the engine process fully closes.
            string bootLog  = Path.Combine(exeDir, "mh_playtest_boot.log");
            string crashLog = Path.Combine(exeDir, "logs", _mm ? "2 Ship 2 Harkinian.log" : "Ship of Harkinian.log");
            plog.LinkEngine(proc, [bootLog, crashLog]);
            DiagnosticLog.Ok($"playtest log: {plog.Path}");

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("playtest failed", ex);
            plog.Stop($"playtest launch failed: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show($"Playtest failed:\n{ex.Message}\n\nDiagnostic trail:\n{DiagnosticLog.RecentTrail()}\n\nFull log: {DiagnosticLog.Path}\nPlaytest log: {plog.Path}",
                "Playtest", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string GuessExe(string? def, bool mm)
    {
        if (def != null && File.Exists(def)) return def;
        // Prefer the build configured in Options.
        var configured = EditorSettings.EngineExe(mm);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured!;
        foreach (var p in new[]
        {
            @"D:\Copilot_OOT\WorkFolders\MegatonHammer\SoH\x64\Release\soh.exe",
            @"D:\Copilot_OOT\WorkFolders\MegatonHammer\2Ship\x64\Release\2ship.exe",
        }) if (File.Exists(p)) return p;
        return "";
    }

    private void BrowseExe()
    {
        using var dlg = new OpenFileDialog { Filter = "Game executable (*.exe)|*.exe" };
        if (File.Exists(_exeBox.Text)) dlg.FileName = _exeBox.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) _exeBox.Text = dlg.FileName;
    }

    private void Warn(string m) => MessageBox.Show(m, "Playtest", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // The reserved scene id added by the Megaton Hammer fork to each game's grown scene table to hold
    // appended levels — OoT: SCENE_MH_APPEND (0x6E), MM: SCENE_MH_APPEND (0x71). The boot hook warps in
    // via the matching reserved entrance, so no existing scene or entrance is touched.
    private int AppendSlotId() => _mm ? 0x71 : 0x6E;

    // Fills the replace-scene combo with friendly names per game; defaults to Hyrule Field (OoT).
    private void PopulateSceneCombo(bool mm)
    {
        _sceneIds.Clear();
        _sceneCombo.Items.Clear();
        if (mm)
            foreach (var (id, name) in MmSceneFiles.All) { _sceneIds.Add(id); _sceneCombo.Items.Add($"0x{id:X2}  {name}"); }
        else
            for (int id = 0; id < OotSceneFiles.Count; id++)
                if (OotSceneFiles.IsValid(id)) { _sceneIds.Add(id); _sceneCombo.Items.Add($"0x{id:X2}  {OotSceneNames.Pretty(id)}"); }
        int def = mm ? 0 : _sceneIds.FindIndex(s => s == 0x51);   // 0x51 = spot00 = Hyrule Field
        if (_sceneCombo.Items.Count > 0) _sceneCombo.SelectedIndex = def >= 0 ? def : 0;
    }

    private static bool TryHex(string s, out int v)
    {
        s = s.Trim(); if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    private static Label Label(string t, int x, int y) => new()
    { Text = t, Left = x, Top = y, AutoSize = true, ForeColor = FgNormal, Font = new Font("Segoe UI", 9f) };

    private static Label Header(string t, int y) => new()
    { Text = t, Left = 14, Top = y, Width = 492, Height = 20, ForeColor = Color.FromArgb(140,190,255),
      Font = new Font("Segoe UI", 7.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };

    private static RadioButton Radio(string t, int x, int y, bool chk) => new()
    { Text = t, Left = x, Top = y, AutoSize = true, Checked = chk, ForeColor = FgNormal, Font = new Font("Segoe UI", 9f) };

    // A full-width borderless container that isolates a set of RadioButtons into their own mutually-
    // exclusive group (radios are grouped by immediate parent in WinForms).
    private Panel GroupPanel(int y, int h)
    {
        var p = new Panel { Left = 0, Top = y, Width = ClientSize.Width, Height = h, BackColor = BgDark };
        Controls.Add(p);
        return p;
    }

    private static TextBox Input(string t, int x, int y, int w) => new()
    { Text = t, Left = x, Top = y, Width = w, BackColor = BgInput, ForeColor = FgNormal,
      BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9f) };

    private static Button Button(string t, int x, int y, int w) => new()
    { Text = t, Left = x, Top = y, Width = w, Height = 26, BackColor = Color.FromArgb(60,60,65),
      ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f) };
}
