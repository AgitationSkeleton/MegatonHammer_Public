using System.Globalization;
using MegatonHammer.Editor;
using MegatonHammer.Export;
using MegatonHammer.Rom;

namespace MegatonHammer.Forms;

/// <summary>
/// Injects the current scene into a base OoT ROM, producing a decompressed ROM that
/// warps to a chosen scene slot. Verified structurally; test the result in an emulator
/// (set the emulator to allow expanded/decompressed ROMs).
/// </summary>
public sealed class RomInjectDialog : Form
{
    private static readonly Color BgDark   = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput  = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);
    private static readonly Color Accent   = Color.FromArgb(0, 122, 204);

    private readonly ZScene _scene;
    private readonly Func<string, System.Drawing.Bitmap?>? _texResolver;
    private readonly TextBox _romBox;
    private readonly TextBox _slotBox;
    private readonly TextBox _outBox;

    public RomInjectDialog(ZScene scene, string? defaultRom, Func<string, System.Drawing.Bitmap?>? texResolver = null)
    {
        _scene = scene;
        _texResolver = texResolver;

        Text            = "Inject into ROM — Megaton Hammer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;
        ClientSize      = new Size(540, 250);
        BackColor       = BgDark;
        ForeColor       = FgNormal;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 5, Padding = new Padding(16),
            BackColor = BgDark,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

        // Base ROM
        layout.Controls.Add(Label("Base OoT ROM:"), 0, 0);
        _romBox = Input(IsRom(defaultRom) ? defaultRom! : "");
        layout.Controls.Add(_romBox, 1, 0);
        var romBrowse = Button("Browse…"); romBrowse.Click += (_, _) => BrowseOpen(_romBox);
        layout.Controls.Add(romBrowse, 2, 0);

        // Scene slot
        layout.Controls.Add(Label("Scene slot (hex):"), 0, 1);
        _slotBox = Input("10");
        layout.Controls.Add(_slotBox, 1, 1);

        // Output ROM
        layout.Controls.Add(Label("Output ROM:"), 0, 2);
        _outBox = Input(DefaultOut(defaultRom));
        layout.Controls.Add(_outBox, 1, 2);
        var outBrowse = Button("Browse…"); outBrowse.Click += (_, _) => BrowseSave(_outBox);
        layout.Controls.Add(outBrowse, 2, 2);

        // Info
        var info = new Label
        {
            Dock = DockStyle.Fill, ForeColor = Color.FromArgb(150, 150, 150),
            Font = new Font("Segoe UI", 8f), TextAlign = ContentAlignment.MiddleLeft,
            Text = $"Scene '{_scene.Name}' · {_scene.Rooms.Count} room(s). Produces a decompressed ROM; " +
                   "warp to the chosen scene to test. Back up your ROM first.",
        };
        layout.Controls.Add(info, 0, 3);
        layout.SetColumnSpan(info, 3);

        // Buttons
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, BackColor = BgDark };
        var cancel = Button("Cancel"); cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var inject = Button("Inject"); inject.BackColor = Accent; inject.Click += OnInject;
        row.Controls.Add(cancel);
        row.Controls.Add(inject);
        layout.Controls.Add(row, 0, 4);
        layout.SetColumnSpan(row, 3);

        Controls.Add(layout);
        AcceptButton = inject;
        CancelButton = cancel;
    }

    private void OnInject(object? sender, EventArgs e)
    {
        string romPath = _romBox.Text.Trim();
        string outPath = _outBox.Text.Trim();

        if (!File.Exists(romPath)) { Warn("Select a valid base ROM."); return; }
        if (string.IsNullOrWhiteSpace(outPath)) { Warn("Choose an output ROM path."); return; }
        if (!TryParseHex(_slotBox.Text, out int slot) || slot < 0 || slot > 0x6E)
        { Warn("Scene slot must be a hex value 0–6E."); return; }

        try
        {
            // Faithful path: when editing a vanilla scene WITHOUT adding new brush geometry, re-emit the
            // original scene/room bytes with only actor + header edits applied — this preserves collision
            // surface types, the object list (0x0B), the keep object (0x07) and prerender cameras that the
            // template rebuild drops. If the user added brushes, those need the (lossy) rebuild path.
            bool addedGeometry = _scene.Rooms.Any(r => r.Geometry.Count > 0);
            var retained = addedGeometry ? null : Export.RetainedSceneBuilder.TryBuild(_scene);
            // OoT-only dialog → build the actor→object resolver so new rooms get a 0x0B object list.
            var objResolver = Export.ActorObjectResolver.Build(mm: false);
            var (sceneBytes, roomBytes) = retained ?? SceneExporter.BuildBinaries(_scene, _texResolver, objResolver);
            string mode = retained != null
                ? "faithful vanilla round-trip (collision / object list / keep object / cameras preserved)"
                : "rebuilt from editor geometry";

            var baseRom = new RomImage(romPath);
            if (baseRom.Game != RomGame.OoT)
            { Warn("Scene-table repoint currently supports Ocarina of Time ROMs only."); return; }

            var result = RomInjector.Inject(baseRom, sceneBytes, roomBytes, slot, _scene.Settings.AreaName,
                                            Export.DisplayListBuilder.SceneHasWater(_scene));
            Editor.RomSafety.GuardWrite(outPath);   // never clobber an original ROM
            File.WriteAllBytes(outPath, result.Rom);

            MessageBox.Show(
                $"{result.Message}\nExport mode: {mode}.\n\nWrote {result.Rom.Length / 1024 / 1024} MB to:\n{outPath}\n\n" +
                $"Test in an emulator (enable expanded/decompressed ROM) and warp to scene 0x{slot:X2}.",
                "Injection Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Injection failed:\n{ex.Message}", "Inject into ROM",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool IsRom(string? p) =>
        !string.IsNullOrWhiteSpace(p) && File.Exists(p) &&
        Path.GetExtension(p).ToLowerInvariant() is ".z64" or ".n64" or ".v64";

    private static string DefaultOut(string? rom)
    {
        string dir = IsRom(rom) ? Path.GetDirectoryName(rom!)! :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MegatonHammer");
        return Path.Combine(dir, "custom_map.z64");
    }

    private void BrowseOpen(TextBox box)
    {
        using var dlg = new OpenFileDialog { Filter = "N64 ROM (*.z64;*.n64;*.v64)|*.z64;*.n64;*.v64|All files|*.*" };
        if (IsRom(box.Text)) dlg.FileName = box.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
    }

    private void BrowseSave(TextBox box)
    {
        using var dlg = new SaveFileDialog { Filter = "N64 ROM (*.z64)|*.z64", FileName = Path.GetFileName(box.Text) };
        if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
    }

    private void Warn(string msg) =>
        MessageBox.Show(msg, "Inject into ROM", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static bool TryParseHex(string s, out int v)
    {
        s = s.Trim(); if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    private static Label Label(string t) => new()
    {
        Text = t, Dock = DockStyle.Fill, ForeColor = FgNormal, Font = new Font("Segoe UI", 9f),
        TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(2, 4, 6, 4),
    };
    private static TextBox Input(string t) => new()
    {
        Text = t, Dock = DockStyle.Fill, BackColor = BgInput, ForeColor = FgNormal,
        BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9f), Margin = new Padding(2, 4, 2, 4),
    };
    private static Button Button(string t) => new()
    {
        Text = t, Height = 28, Width = 90, BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal,
        FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f), Margin = new Padding(4),
    };
}
