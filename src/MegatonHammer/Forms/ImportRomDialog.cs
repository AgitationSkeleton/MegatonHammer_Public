using MegatonHammer.Rom;

namespace MegatonHammer.Forms;

/// <summary>What to bring in from an imported scene.</summary>
public enum ImportScope
{
    Everything,    // geometry + textures AND actors/entities/scripting
    GeometryOnly,  // room meshes, textures, lighting/sky and water — no actors/logic
    ActorsOnly,    // actors, transitions, exits, paths, cutscene — no backdrop geometry
}

/// <summary>Pick a scene to import (read-only) from the loaded OoT or MM ROM, and choose how
/// much of it to bring in (whole level, geometry-only, or actors/logic-only). When cross-game
/// sources are enabled, a scene from the other game can be imported instead.</summary>
public sealed class ImportRomDialog : Form
{
    private readonly ComboBox _scenes = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly List<int> _ids = [];   // scene id per combo row (handles MM's gaps)
    private readonly RadioButton _rbAll    = new() { Text = "Whole level (geometry + actors)", AutoSize = true, Checked = true };
    private readonly RadioButton _rbGeom   = new() { Text = "Geometry only (mesh, textures, lighting, water)", AutoSize = true };
    private readonly RadioButton _rbActors = new() { Text = "Actors / entities / scripting only", AutoSize = true };
    private readonly CheckBox _crossGame;
    private readonly bool _nativeMm;

    /// <summary>Selected scene id, valid after an OK result.</summary>
    public int SceneId { get; private set; } = -1;

    /// <summary>How much of the scene to import.</summary>
    public ImportScope Scope =>
        _rbGeom.Checked ? ImportScope.GeometryOnly :
        _rbActors.Checked ? ImportScope.ActorsOnly : ImportScope.Everything;

    /// <summary>True when the user chose to import from the OTHER game's ROM.</summary>
    public bool CrossGame => _crossGame.Checked;

    /// <summary>Whether the scene being imported is from MM (accounts for the cross-game toggle).</summary>
    public bool ImportMm => _crossGame.Checked ? !_nativeMm : _nativeMm;

    /// <param name="hideScope">Hide the whole-level/geometry/actors scope picker — for the ghost reference,
    /// which only ever loads geometry, so the options would be misleading.</param>
    /// <param name="defaultScope">Which scope radio is pre-selected. When a ghost reference is already
    /// loaded the geometry is redundant, so callers pass ActorsOnly to avoid a solid mesh drawing over it.</param>
    public ImportRomDialog(bool nativeMm, bool crossAvailable, bool hideScope = false,
        ImportScope defaultScope = ImportScope.Everything)
    {
        _nativeMm = nativeMm;
        Text = "Import Level from ROM";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        BackColor = Color.FromArgb(45, 45, 48);
        ForeColor = Color.FromArgb(220, 220, 220);

        var label = new Label { Text = "Scene:", AutoSize = true, Location = new Point(12, 16) };
        _scenes.SetBounds(60, 12, 320, 24);

        _crossGame = new CheckBox
        {
            Text = $"Import from {(nativeMm ? "OoT" : "MM")} (cross-game)",
            AutoSize = true, Location = new Point(60, 42),
            Enabled = crossAvailable,
        };
        if (!crossAvailable)
            _toolTip.SetToolTip(_crossGame,
                "Enable cross-game textures and configure the other game's ROM (Options) to import across games.");
        _crossGame.CheckedChanged += (_, _) => PopulateScenes(ImportMm);

        // Pre-select the requested scope (the ghost path forces GeometryOnly via hideScope).
        _rbAll.Checked    = !hideScope && defaultScope == ImportScope.Everything;
        _rbGeom.Checked   = hideScope  || defaultScope == ImportScope.GeometryOnly;
        _rbActors.Checked = !hideScope && defaultScope == ImportScope.ActorsOnly;
        if (defaultScope == ImportScope.ActorsOnly)
            _toolTip.SetToolTip(_rbActors, "A ghost reference is loaded, so the geometry is already shown. "
                + "Import only the actors to keep the ghost visible instead of covering it with solid geometry.");

        int btnY = hideScope ? 74 : 178;
        var ok = new Button { Text = hideScope ? "Load" : "Import", DialogResult = DialogResult.OK, Location = new Point(214, btnY), Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(300, btnY), Width = 80 };
        ok.Click += (_, _) => SceneId = _scenes.SelectedIndex >= 0 && _scenes.SelectedIndex < _ids.Count
            ? _ids[_scenes.SelectedIndex] : -1;

        Controls.AddRange([label, _scenes, _crossGame, ok, cancel]);
        if (!hideScope)
        {
            var scopeBox = new GroupBox
            {
                Text = "Import", ForeColor = ForeColor,
                Location = new Point(12, 70), Size = new Size(368, 96),
            };
            _rbAll.Location    = new Point(12, 22);
            _rbGeom.Location   = new Point(12, 44);
            _rbActors.Location = new Point(12, 66);
            scopeBox.Controls.AddRange([_rbAll, _rbGeom, _rbActors]);
            Controls.Add(scopeBox);
        }
        ClientSize = new Size(400, btnY + 34);
        AcceptButton = ok;
        CancelButton = cancel;

        PopulateScenes(nativeMm);
    }

    private readonly ToolTip _toolTip = new();

    // Fills the scene combo for the target game (rebuilt when the cross-game toggle flips).
    private void PopulateScenes(bool mm)
    {
        _ids.Clear();
        _scenes.Items.Clear();
        if (mm)
            foreach (var (id, name) in MmSceneFiles.All) { _ids.Add(id); _scenes.Items.Add($"0x{id:X2}  {name}"); }
        else
            for (int id = 0; id < OotSceneFiles.Count; id++)
                if (OotSceneFiles.IsValid(id)) { _ids.Add(id); _scenes.Items.Add($"0x{id:X2}  {OotSceneNames.Pretty(id)}"); }
        if (_scenes.Items.Count > 0) _scenes.SelectedIndex = mm ? 0 : Math.Min(0x51, _scenes.Items.Count - 1);
    }
}
