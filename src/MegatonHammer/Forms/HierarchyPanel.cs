using MegatonHammer.Editor;
using MegatonHammer.Tools;

namespace MegatonHammer.Forms;

/// <summary>
/// Right-side panel: scene/room tree + actor-type selector.
/// </summary>
public sealed class HierarchyPanel : UserControl
{
    private static readonly Color BgDark    = Color.FromArgb(37, 37, 38);
    private static readonly Color BgDarker  = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal  = Color.FromArgb(200, 200, 200);
    private static readonly Color FgActive  = Color.FromArgb(100, 180, 255);
    private static readonly Color Accent    = Color.FromArgb(0, 122, 204);

    private readonly MapDocument   _doc;
    private readonly EntityTool    _entityTool;
    private readonly ActorDatabase _actorDb;
    private readonly bool          _mm;

    private readonly TreeView  _tree;
    private readonly ComboBox  _actorCombo;
    private readonly Label     _actorCountLabel;
    private readonly Button    _addRoomBtn;
    private readonly Button    _removeRoomBtn;
    private readonly ComboBox  _sceneCombo;
    private bool _loadingScenes;
    private bool _treeRefreshQueued;   // one pending coalesced RefreshTree (see the Changed handler)

    public event Action? ActiveRoomChanged;
    public event Action? DocumentStructureChanged;

    /// <summary>A room's visibility checkbox was toggled — the host repaints the viewports.</summary>
    public event Action? RoomVisibilityChanged;
    private bool _settingChecks;   // true while we set node.Checked programmatically (suppresses AfterCheck)

    /// <summary>Raised when the active scene is switched (the host refreshes everything).</summary>
    public event Action? ActiveSceneChanged;

    public HierarchyPanel(MapDocument doc, EntityTool entityTool, ActorDatabase actorDb, bool showHeader = true, bool mm = false)
    {
        _doc        = doc;
        _entityTool = entityTool;
        _actorDb    = actorDb;
        _mm         = mm;

        BackColor = BgDark;
        Width     = 230;
        Dock      = DockStyle.Right;

        // ── Hierarchy section header ─────────────────────────────────────
        var hdrScene = MakeHeader("SCENE / ROOMS");

        // ── TreeView ─────────────────────────────────────────────────────
        _tree = new TreeView
        {
            Dock            = DockStyle.Fill,
            BackColor       = BgDarker,
            ForeColor       = FgNormal,
            Font            = new Font("Segoe UI", 9f),
            BorderStyle     = BorderStyle.None,
            ShowLines       = true,
            ShowPlusMinus   = true,
            ShowRootLines   = true,
            FullRowSelect   = true,
            HideSelection   = false,
            CheckBoxes      = true,   // per-room visibility toggle (checked = shown)
        };
        _tree.NodeMouseDoubleClick += OnTreeNodeDoubleClick;
        _tree.NodeMouseClick       += OnTreeNodeClick;
        _tree.AfterCheck           += OnTreeAfterCheck;

        // ── Room management buttons ───────────────────────────────────────
        _addRoomBtn = DarkButton("+ Room");
        _addRoomBtn.Click += (_, _) =>
        {
            var room = _doc.Scene.AddRoom();
            room.Settings.TimeSpeed = _doc.DefaultRoomTimeSpeed;
            _doc.NotifyChanged();
            DocumentStructureChanged?.Invoke();
        };

        _removeRoomBtn = DarkButton("− Room");
        _removeRoomBtn.Click += (_, _) =>
        {
            if (_tree.SelectedNode?.Tag is ZRoom room)
            {
                if (_doc.Scene.RemoveRoom(room))
                {
                    _doc.Scene.RenumberRooms();
                    _doc.NotifyChanged();
                    DocumentStructureChanged?.Invoke();
                }
            }
        };

        var btnPanel = new FlowLayoutPanel
        {
            Dock        = DockStyle.Bottom,
            Height      = 34,
            BackColor   = BgDark,
            Padding     = new Padding(4, 4, 4, 0),
            FlowDirection = FlowDirection.LeftToRight,
        };
        btnPanel.Controls.Add(_addRoomBtn);
        btnPanel.Controls.Add(_removeRoomBtn);

        // ── Actor section ────────────────────────────────────────────────
        var hdrActor = MakeHeader("PLACE ACTOR");

        _actorCombo = new ComboBox
        {
            Dock          = DockStyle.Top,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor     = BgDarker,
            ForeColor     = FgNormal,
            Font          = new Font("Segoe UI", 8.5f),
            FlatStyle     = FlatStyle.Flat,
            Height        = 24,
        };
        _actorCombo.SelectedIndexChanged += OnActorComboChanged;

        _actorCountLabel = new Label
        {
            Dock      = DockStyle.Top,
            Height    = 18,
            ForeColor = Color.FromArgb(120, 120, 120),
            Font      = new Font("Segoe UI", 7.5f),
            Text      = "Actors: 0",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(6, 0, 0, 0),
        };

        var actorSection = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 78,
            BackColor = BgDark,
        };
        actorSection.Controls.Add(_actorCountLabel);
        actorSection.Controls.Add(_actorCombo);
        actorSection.Controls.Add(hdrActor);

        // ── Scene selector + management (multi-scene projects) ────────────
        _sceneCombo = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgDarker,
            ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f),
        };
        _sceneCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_loadingScenes || _sceneCombo.SelectedIndex < 0) return;
            _doc.SwitchScene(_sceneCombo.SelectedIndex);
            ActiveSceneChanged?.Invoke();
        };
        var tips = new ToolTip();
        var addSceneBtn = DarkButton("+"); addSceneBtn.Width = 28; tips.SetToolTip(addSceneBtn, "Add scene");
        addSceneBtn.Click += (_, _) => { _doc.AddScene(); ActiveSceneChanged?.Invoke(); };
        var delSceneBtn = DarkButton("−"); delSceneBtn.Width = 28; tips.SetToolTip(delSceneBtn, "Delete current scene");
        delSceneBtn.Click += (_, _) =>
        {
            if (_doc.Scenes.Count <= 1) return;
            if (MessageBox.Show($"Delete scene \"{_doc.Scene.Name}\"?", "Delete Scene",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            _doc.RemoveScene(_doc.ActiveSceneIndex);
            ActiveSceneChanged?.Invoke();
        };
        var sceneBar = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = BgDark, Padding = new Padding(4, 2, 4, 0) };
        var sceneRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = BgDark };
        sceneRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sceneRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        sceneRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        sceneRow.Controls.Add(_sceneCombo, 0, 0);
        sceneRow.Controls.Add(addSceneBtn, 1, 0);
        sceneRow.Controls.Add(delSceneBtn, 2, 0);
        sceneBar.Controls.Add(sceneRow);

        // ── Assemble ─────────────────────────────────────────────────────
        Controls.Add(_tree);
        Controls.Add(btnPanel);
        Controls.Add(actorSection);
        Controls.Add(sceneBar);
        if (showHeader) Controls.Add(hdrScene);

        // ── Wire document changes ─────────────────────────────────────────
        // Coalesce tree rebuilds: NotifyChanged fires on every brush click AND every mousemove during a
        // drag, so an un-coalesced BeginInvoke(RefreshTree) queued dozens of full TreeView rebuilds per
        // second → visible click/drag lag. Queue at most ONE pending refresh (the latest state) instead.
        _doc.Changed += () =>
        {
            if (!IsHandleCreated || _treeRefreshQueued) return;
            _treeRefreshQueued = true;
            BeginInvoke(() => { _treeRefreshQueued = false; RefreshTree(); });
        };
        _doc.ScenesChanged += () =>
        {
            if (IsHandleCreated) BeginInvoke(RefreshScenes);
        };

        PopulateActorCombo();
        RefreshScenes();
        RefreshTree();
    }

    private void RefreshScenes()
    {
        _loadingScenes = true;
        _sceneCombo.Items.Clear();
        for (int i = 0; i < _doc.Scenes.Count; i++)
            _sceneCombo.Items.Add($"{i}: {_doc.Scenes[i].Name}");
        _sceneCombo.SelectedIndex = Math.Clamp(_doc.ActiveSceneIndex, 0, _sceneCombo.Items.Count - 1);
        _loadingScenes = false;
        RefreshTree();
    }

    // ── Population ────────────────────────────────────────────────────────

    private void RefreshTree()
    {
        _tree.BeginUpdate();
        _settingChecks = true;   // setting node.Checked below must not be treated as user toggles
        _tree.Nodes.Clear();

        var sceneNode = new TreeNode($"Scene: {_doc.Scene.Name}")
        {
            ForeColor = FgActive,
            NodeFont  = new Font("Segoe UI", 9f, FontStyle.Bold),
            Checked   = _doc.Scene.Rooms.All(r => r.Visible),   // root acts as show/hide-all
        };

        foreach (var room in _doc.Scene.Rooms)
        {
            string label  = room.Name;
            if (room.IsActive) label += "  ✦";   // active marker
            var node = new TreeNode(label) { Tag = room, Checked = room.Visible };
            if (room.IsActive)
                node.ForeColor = FgActive;
            sceneNode.Nodes.Add(node);
        }

        _tree.Nodes.Add(sceneNode);
        sceneNode.Expand();
        _settingChecks = false;

        int totalActors = _doc.Scene.Rooms.Sum(r => r.Actors.Count);
        _actorCountLabel.Text = $"Actors placed: {totalActors}";

        _tree.EndUpdate();
        UpdateButtonState();
    }

    /// <summary>Re-populate the placement-entity combo (e.g. after the default actor changes in Options).</summary>
    public void RefreshActorCombo() => PopulateActorCombo();
    private void PopulateActorCombo()
    {
        _actorCombo.Items.Clear();
        // The editor-only insertable dummy Link (scale reference) sits at the top, then the real actors.
        _actorCombo.Items.Add(new ActorComboItem(EntityTool.EditorDummyLinkId, "Dummy Link (editor-only scale)"));
        foreach (var info in _actorDb.All)
        {
            if (info.Id == 0x0000) continue;   // Player/Link isn't a placeable actor — it's the movable Player Start marker
            _actorCombo.Items.Add(new ActorComboItem(info.Id, info.Name));
        }

        // Default placement actor: the per-game configured default (Options ▸ General), defaulting to the
        // dummy Link. Falls back to the first item if the configured id isn't present.
        ushort want = (ushort)Editor.EditorSettings.DefaultActor(_mm);
        int def = 0;
        for (int i = 0; i < _actorCombo.Items.Count; i++)
            if (_actorCombo.Items[i] is ActorComboItem it && it.Id == want) { def = i; break; }
        _actorCombo.SelectedIndex = def;
    }

    private void UpdateButtonState()
    {
        _removeRoomBtn.Enabled = _doc.Scene.Rooms.Count > 1;
    }

    // ── Events ────────────────────────────────────────────────────────────

    private void OnTreeNodeDoubleClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Node.Tag is ZRoom room)
        {
            _doc.Scene.ActiveRoom = room;
            _doc.NotifyChanged();
            ActiveRoomChanged?.Invoke();
        }
    }

    private void OnTreeNodeClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        // Single-click: highlight but don't switch active room
    }

    // A visibility checkbox was toggled. Room node → that room; the scene root → show/hide every room.
    // Guarded so programmatic check-setting during RefreshTree doesn't recurse.
    private void OnTreeAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_settingChecks || e.Node == null) return;

        if (e.Node.Tag is ZRoom room)
        {
            room.Visible = e.Node.Checked;
            // Keep the root's tri-state-ish check in sync (checked only when every room is visible).
            if (e.Node.Parent is { } root)
            {
                _settingChecks = true;
                root.Checked = _doc.Scene.Rooms.All(r => r.Visible);
                _settingChecks = false;
            }
        }
        else   // the scene root: apply to all rooms and their child nodes
        {
            bool vis = e.Node.Checked;
            _settingChecks = true;
            foreach (var r in _doc.Scene.Rooms) r.Visible = vis;
            foreach (TreeNode child in e.Node.Nodes) child.Checked = vis;
            _settingChecks = false;
        }

        _doc.SyncImportedRoomVisibility();   // mirror onto the imported backdrop mesh
        RoomVisibilityChanged?.Invoke();
    }

    private void OnActorComboChanged(object? sender, EventArgs e)
    {
        if (_actorCombo.SelectedItem is ActorComboItem item)
            _entityTool.ActiveActorId = item.Id;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static Label MakeHeader(string text) => new()
    {
        Dock      = DockStyle.Top,
        Height    = 22,
        BackColor = Color.FromArgb(45, 45, 48),
        ForeColor = Color.FromArgb(180, 180, 180),
        Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
        Text      = $"  {text}",
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private static Button DarkButton(string text) => new()
    {
        Text      = text,
        Width     = 80,
        Height    = 24,
        BackColor = Color.FromArgb(60, 60, 65),
        ForeColor = Color.FromArgb(210, 210, 210),
        FlatStyle = FlatStyle.Flat,
        Font      = new Font("Segoe UI", 8.5f),
    };

    // Combo item wrapper so SelectedItem is typed
    private sealed class ActorComboItem(ushort id, string name)
    {
        public ushort Id   { get; } = id;
        public string Name { get; } = name;
        public override string ToString() => $"0x{Id:X4}  {Name}";
    }
}
