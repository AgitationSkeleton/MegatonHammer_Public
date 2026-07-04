using MegatonHammer.Editor;
using MegatonHammer.Export;

namespace MegatonHammer.Forms;

public sealed class ExportDialog : Form
{
    private static readonly Color BgDark   = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput  = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);
    private static readonly Color Accent   = Color.FromArgb(0, 122, 204);

    private readonly ZScene _scene;
    private readonly TextBox  _nameBox;
    private readonly TextBox  _dirBox;

    public ExportDialog(ZScene scene)
    {
        _scene = scene;

        Text            = "Build & Export — Megaton Hammer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;
        ClientSize      = new Size(480, 200);
        BackColor       = BgDark;
        ForeColor       = FgNormal;

        var layout = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            RowCount    = 4,
            ColumnCount = 3,
            Padding     = new Padding(16),
            BackColor   = BgDark,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

        // Scene name row
        layout.Controls.Add(DarkLabel("Scene name:"), 0, 0);
        _nameBox = DarkTextBox(scene.Name);
        layout.Controls.Add(_nameBox, 1, 0);
        layout.SetColumnSpan(_nameBox, 2);

        // Output folder row
        layout.Controls.Add(DarkLabel("Output folder:"), 0, 1);
        _dirBox = DarkTextBox(GetDefaultOutputDir());
        layout.Controls.Add(_dirBox, 1, 1);

        var browseBtn = AccentButton("Browse…");
        browseBtn.Click += OnBrowse;
        layout.Controls.Add(browseBtn, 2, 1);

        // Info label
        var infoLabel = new Label
        {
            Text      = $"Rooms: {scene.Rooms.Count}  |  Files: {scene.Rooms.Count + 1} (.zmap × {scene.Rooms.Count} + .zscene)",
            ForeColor = Color.FromArgb(150, 150, 150),
            Font      = new Font("Segoe UI", 8f),
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        layout.Controls.Add(infoLabel, 0, 2);
        layout.SetColumnSpan(infoLabel, 3);

        // Button row
        var btnRow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor     = BgDark,
        };

        var cancelBtn = AccentButton("Cancel");
        cancelBtn.BackColor = Color.FromArgb(60, 60, 65);
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var exportBtn = AccentButton("Export [F9]");
        exportBtn.BackColor = Accent;
        exportBtn.Click += OnExport;

        btnRow.Controls.Add(cancelBtn);
        btnRow.Controls.Add(exportBtn);
        layout.Controls.Add(btnRow, 0, 3);
        layout.SetColumnSpan(btnRow, 3);

        Controls.Add(layout);
        AcceptButton = exportBtn;
        CancelButton = cancelBtn;
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description         = "Select export output folder",
            SelectedPath        = _dirBox.Text,
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _dirBox.Text = dlg.SelectedPath;
    }

    private void OnExport(object? sender, EventArgs e)
    {
        string sceneName = _nameBox.Text.Trim();
        string outputDir = _dirBox.Text.Trim();

        if (string.IsNullOrEmpty(sceneName))
        { MessageBox.Show("Scene name cannot be empty.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        if (string.IsNullOrEmpty(outputDir))
        { MessageBox.Show("Output folder cannot be empty.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        try
        {
            SceneExporter.Export(_scene, outputDir, sceneName);
            int fileCount = _scene.Rooms.Count + 1;
            MessageBox.Show(
                $"Exported {fileCount} file(s) to:\n{outputDir}",
                "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed:\n{ex.Message}", "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string GetDefaultOutputDir()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "MegatonHammer", "Export");
    }

    private static Label DarkLabel(string text) => new()
    {
        Text      = text,
        ForeColor = FgNormal,
        Font      = new Font("Segoe UI", 9f),
        TextAlign = ContentAlignment.MiddleRight,
        Dock      = DockStyle.Fill,
    };

    private static TextBox DarkTextBox(string text) => new()
    {
        Text      = text,
        BackColor = BgInput,
        ForeColor = FgNormal,
        Font      = new Font("Segoe UI", 9f),
        BorderStyle = BorderStyle.FixedSingle,
        Dock      = DockStyle.Fill,
    };

    private static Button AccentButton(string text) => new()
    {
        Text      = text,
        Height    = 28,
        Width     = 110,
        BackColor = Color.FromArgb(60, 60, 65),
        ForeColor = FgNormal,
        FlatStyle = FlatStyle.Flat,
        Font      = new Font("Segoe UI", 9f),
    };
}
