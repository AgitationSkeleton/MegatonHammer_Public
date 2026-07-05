using System.Drawing;
using System.Globalization;
using MegatonHammer.Editor;
using MegatonHammer.Export;
using MegatonHammer.Rom;

namespace MegatonHammer.Forms;

/// <summary>
/// Exports the level as a plain <c>.o2r</c> a REGULAR (vanilla) SoH loads — no Megaton Hammer fork needed.
/// The level's native OTR resources are written at a chosen vanilla scene's resource path, so SoH renders it
/// in that scene's place; the player just walks into that scene in-game. "Add to existing .o2r" merges the
/// level into an archive already holding other overrides, making a multi-level pack (confirming before it
/// overwrites an override for the same scene).
/// </summary>
public sealed class ExportO2RDialog : Form
{
    private static readonly Color BgDark   = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput  = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);

    private readonly ZScene _scene;
    private readonly bool _mm;
    private readonly Func<string, Bitmap?>? _texResolver;

    private readonly ComboBox _sceneCombo;
    private readonly List<int> _sceneIds = new();
    private readonly RadioButton _newMode, _addMode;
    private readonly CheckBox _mq;
    private readonly TextBox _pathBox;

    public ExportO2RDialog(ZScene scene, bool mm, Func<string, Bitmap?>? texResolver = null)
    {
        _scene = scene; _mm = mm; _texResolver = texResolver;

        Text = "Export as .o2r (vanilla SoH)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(540, 340);
        BackColor = BgDark; ForeColor = FgNormal;
        Font = new Font("Segoe UI", 9f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        int y = 14;
        Controls.Add(new Label { Left = 14, Top = y, Width = 512, Height = 34, ForeColor = Color.FromArgb(170, 170, 170),
            Font = new Font("Segoe UI", 8.25f),
            Text = "Packs this level into a .o2r that a regular (unmodified) Ship of Harkinian loads — it replaces the "
                 + "scene you pick below. Drop the .o2r in SoH's mods folder and enter that area in-game." });
        y += 40;

        Controls.Add(Header("OVERRIDES WHICH SCENE", y)); y += 22;
        _sceneCombo = new ComboBox { Left = 14, Top = y, Width = 512, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, MaxDropDownItems = 20 };
        Controls.Add(_sceneCombo); y += 30;

        _mq = new CheckBox { Left = 14, Top = y, Width = 512, Height = 20, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
            Text = "Master Quest scene version (OoT dungeons only)", Visible = !_mm };
        _mq.CheckedChanged += (_, _) => PopulateScenes();
        if (!_mm) { Controls.Add(_mq); y += 26; }

        Controls.Add(Header("OUTPUT", y)); y += 22;
        _newMode = Radio("New .o2r file", 14, y, true);
        _addMode = Radio("Add to an existing .o2r (make/extend a level pack)", 160, y, false);
        foreach (var r in new[] { _newMode, _addMode })
        { r.CheckedChanged += (_, _) => { if (r.Checked) UpdateModeHint(); }; Controls.Add(r); }
        y += 28;

        _pathBox = new TextBox { Left = 14, Top = y, Width = 424, BackColor = BgInput, ForeColor = FgNormal,
            BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(_pathBox);
        var browse = Btn("Browse…", 446, y - 1, 80, 26, Browse);
        Controls.Add(browse); y += 34;

        _hint = new Label { Left = 14, Top = y, Width = 512, Height = 30, ForeColor = Color.FromArgb(150, 150, 150),
            Font = new Font("Segoe UI", 8f) };
        Controls.Add(_hint); y += 34;

        var export = Btn("Export", 340, ClientSize.Height - 38, 90, 28, DoExport);
        export.BackColor = Color.FromArgb(0, 122, 204);
        var cancel = Btn("Cancel", 438, ClientSize.Height - 38, 88, 28, () => { DialogResult = DialogResult.Cancel; Close(); });
        Controls.Add(export); Controls.Add(cancel);

        PopulateScenes();
        UpdateModeHint();
    }

    private readonly Label _hint;

    private void UpdateModeHint() =>
        _hint.Text = _addMode.Checked
            ? "The level is added to the chosen .o2r. If that pack already overrides this same scene, you'll be asked to confirm."
            : "Writes a fresh .o2r containing just this level.";

    private void PopulateScenes()
    {
        int prev = _sceneCombo.SelectedIndex >= 0 && _sceneCombo.SelectedIndex < _sceneIds.Count ? _sceneIds[_sceneCombo.SelectedIndex] : -1;
        _sceneIds.Clear();
        _sceneCombo.Items.Clear();
        if (_mm)
            foreach (var (id, name) in MmSceneFiles.All) { _sceneIds.Add(id); _sceneCombo.Items.Add($"0x{id:X2}  {name}"); }
        else
            for (int id = 0; id < OotSceneFiles.Count; id++)
                if (OotSceneFiles.IsValid(id))
                {
                    _sceneIds.Add(id);
                    string tag = OotSceneFiles.WeaponsDisabled(id) ? "   ⚠ weapons disabled here" : "";
                    _sceneCombo.Items.Add($"0x{id:X2}  {OotSceneNames.Pretty(id)}{tag}");
                }
        int remembered = prev >= 0 ? prev : (_mm ? MmSceneFiles.All.FirstOrDefault().Id
                                                  : Editor.EditorSettings.LastReplaceSceneOoT);
        int idx = _sceneIds.FindIndex(s => s == remembered);
        if (idx < 0 && !_mm) idx = _sceneIds.FindIndex(s => s == 0x51);
        if (_sceneCombo.Items.Count > 0) _sceneCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void Browse()
    {
        if (_addMode.Checked)
        {
            using var ofd = new OpenFileDialog { Filter = "SoH archive (*.o2r)|*.o2r|All files|*.*", Title = "Add to which .o2r" };
            if (!string.IsNullOrWhiteSpace(_pathBox.Text)) { try { ofd.InitialDirectory = Path.GetDirectoryName(_pathBox.Text); } catch { } }
            if (ofd.ShowDialog(this) == DialogResult.OK) _pathBox.Text = ofd.FileName;
        }
        else
        {
            using var sfd = new SaveFileDialog { Filter = "SoH archive (*.o2r)|*.o2r", Title = "Export .o2r as",
                FileName = SanitizeFileName(_scene.Name) + ".o2r", DefaultExt = "o2r", AddExtension = true, OverwritePrompt = false };
            if (sfd.ShowDialog(this) == DialogResult.OK) _pathBox.Text = sfd.FileName;
        }
    }

    private void DoExport()
    {
        int idx = _sceneCombo.SelectedIndex;
        if (idx < 0 || idx >= _sceneIds.Count) { Warn("Pick a scene to override."); return; }
        int sceneId = _sceneIds[idx];
        string path = _pathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path)) { Warn("Choose an output .o2r path (Browse…)."); return; }
        if (!path.EndsWith(".o2r", StringComparison.OrdinalIgnoreCase)) path += ".o2r";
        bool merge = _addMode.Checked;
        if (merge && !File.Exists(path)) { Warn("That .o2r doesn't exist. Pick an existing pack, or use \"New .o2r file\"."); return; }
        if (!merge && File.Exists(path) &&
            MessageBox.Show(this, $"{Path.GetFileName(path)} already exists. Overwrite it with a fresh single-level .o2r?",
                "Export as .o2r", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        try
        {
            var res = O2RPacker.BuildVanillaSceneResources(_scene, sceneId, _mm, !_mm && _mq.Checked, _texResolver);

            // Merge conflict check: does the existing pack already override this scene (same resource paths)?
            if (merge)
            {
                var existing = O2RPacker.ListEntries(path);
                bool sameScene = res.Any(r => existing.Contains(r.Path));
                if (sameScene &&
                    MessageBox.Show(this,
                        $"This .o2r already overrides scene 0x{sceneId:X2} ({SceneName(sceneId)}).\n\n"
                      + "Replacing it will overwrite that level's data in the pack. Continue?",
                        "Overwrite existing override?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }

            var overwritten = O2RPacker.WriteLevelO2R(path, res, merge);

            string msg = merge
                ? $"Added to pack:\n{path}\n\nScene 0x{sceneId:X2} ({SceneName(sceneId)}) — "
                    + (overwritten.Count > 0 ? "replaced the existing override." : "new override added.")
                : $"Wrote:\n{path}\n\nOverrides scene 0x{sceneId:X2} ({SceneName(sceneId)}).";
            msg += "\n\nCopy the .o2r into Ship of Harkinian's mods folder, then enter that area in-game.";
            if (!_mm && OotSceneFiles.WeaponsDisabled(sceneId))
                msg += "\n\n⚠ This scene disables weapons in vanilla SoH (hardcoded) — a combat map should use a different slot.";
            MessageBox.Show(this, msg, "Export as .o2r", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\n{ex.Message}", "Export as .o2r", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string SceneName(int id) => _mm
        ? (MmSceneFiles.All.FirstOrDefault(s => s.Id == id).Name ?? $"0x{id:X2}")
        : OotSceneNames.Pretty(id);

    private static string SanitizeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "level" : s.Trim();
    }

    private void Warn(string m) => MessageBox.Show(this, m, "Export as .o2r", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static Label Header(string t, int y) => new()
    { Text = t, Left = 12, Top = y, Width = 512, Height = 18, ForeColor = Color.FromArgb(140, 190, 255),
      Font = new Font("Segoe UI", 7.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };

    private static RadioButton Radio(string t, int x, int y, bool chk) => new()
    { Text = t, Left = x, Top = y, AutoSize = true, Checked = chk, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };

    private Button Btn(string t, int x, int y, int w, int h, Action onClick)
    {
        var b = new Button { Text = t, Left = x, Top = y, Width = w, Height = h, BackColor = Color.FromArgb(60, 60, 65),
            ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f) };
        b.Click += (_, _) => onClick();
        return b;
    }
}
