using System.Globalization;
using MegatonHammer.Editor;
using OpenTK.Mathematics;

namespace MegatonHammer.Forms;

/// <summary>
/// Context-sensitive inspector: edits the selected actor, or (when nothing is
/// selected) the scene and active-room settings.
/// </summary>
public sealed class PropertiesPanel : UserControl
{
    private static readonly Color BgDark   = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput  = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(205, 205, 205);
    private static readonly Color HdrBg    = Color.FromArgb(45, 45, 48);
    private static readonly Color HdrFg    = Color.FromArgb(140, 190, 255);

    private readonly MapDocument   _doc;
    private readonly ActorDatabase _actorDb;
    private readonly bool          _nativeIsOoT;
    private readonly Panel         _host;
    private TableLayoutPanel       _table = null!;

    private bool _loading;
    private object? _shownKey;        // identity of what is currently displayed
    // #6: hover tooltip showing the full selected text on dropdowns whose selection truncates.
    private readonly ToolTip _comboTip = new() { AutoPopDelay = 12000, InitialDelay = 400, ReshowDelay = 100 };

    private void ComboTip(ComboBox cb)
    {
        void Upd() => _comboTip.SetToolTip(cb, cb.SelectedItem?.ToString() ?? "");
        cb.SelectedIndexChanged += (_, _) => Upd();
        Upd();
    }

    public event Action? Changed;     // bubbled so the form can redraw viewports

    public PropertiesPanel(MapDocument doc, ActorDatabase actorDb, bool nativeIsOoT, bool showHeader = true)
    {
        _doc         = doc;
        _actorDb     = actorDb;
        _nativeIsOoT = nativeIsOoT;

        BackColor = BgDark;
        Dock      = DockStyle.Fill;

        var header = new Label
        {
            Dock = DockStyle.Top, Height = 22, Text = "  PROPERTIES",
            BackColor = HdrBg, ForeColor = Color.FromArgb(180, 180, 180),
            Font = UiFonts.Get("Segoe UI", 7.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft,
        };

        _host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BgDark };

        Controls.Add(_host);
        if (showHeader) Controls.Add(header);

        _doc.Changed += OnDocChanged;
        Rebuild(force: true);
    }

    private bool _rebuildQueued;   // one pending coalesced Rebuild

    // Rebuild only when the displayed object changes, so typing in a field isn't interrupted by the
    // NotifyChanged it triggers. Coalesce: NotifyChanged fires on every mousemove during a drag, so queue
    // at most one pending rebuild instead of dozens (each an early-out when the selection is unchanged).
    private void OnDocChanged()
    {
        if (!IsHandleCreated || _rebuildQueued) return;
        _rebuildQueued = true;
        BeginInvoke(() => { _rebuildQueued = false; Rebuild(force: false); });
    }

    private void Rebuild(bool force)
    {
        var actor = _doc.SelectedActor;
        var solid = actor == null ? _doc.SelectedSolid : null;
        object key = actor ?? solid ?? (object)("scene:" + (_doc.Scene.ActiveRoom?.Name ?? ""));
        if (!force && Equals(key, _shownKey)) return;
        _shownKey = key;

        _loading = true;
        _host.SuspendLayout();
        // DISPOSE the previous table + its child controls, don't just detach them. Controls.Clear() only
        // removes controls from the collection; their Win32 window handles stay allocated until a GC that
        // may never come. The properties panel rebuilds on every selection change (each paste, each click),
        // so over a session the handles pile up until the process hits the ~10k USER-handle limit — then
        // the next menu/dropdown fails with "Error creating window handle" and the editor crashes.
        var stale = _host.Controls.Cast<Control>().ToArray();
        _host.Controls.Clear();
        foreach (var c in stale) c.Dispose();

        _table = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            BackColor = BgDark, Padding = new Padding(4, 4, 4, 12),
        };
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        if (actor != null)      BuildActor(actor);
        else if (solid != null) BuildSolid(solid);
        else                    BuildSceneAndRoom();

        _host.Controls.Add(_table);
        _host.ResumeLayout();
        _loading = false;
    }

    // ── Actor inspector ────────────────────────────────────────────────────

    private void BuildActor(ZActor a)
    {
        AddHeader("ACTOR");

        // Basic vs Advanced. When simplification is DISABLED (global setting) the layout is CLASSIC: every field
        // shown, no toggle. When ENABLED (default): basic mode hides the technical fields (raw Actor ID/Variable
        // + logic flags) behind a persistent "Show Advanced Options" toggle. The friendly per-actor settings
        // (chest Contents, beamos Sight range, …) are always visible; the editor auto-manages the hidden logic.
        bool simplified = Editor.EditorSettings.SimplifiedActorProperties;
        bool showAdv = !simplified || Editor.EditorSettings.ShowAdvancedActorOptions;

        var nameLabel = AddReadonly("Name", ResolveName(a.Number));
        if (showAdv)
        {
            AddHex("Actor ID", 4, () => a.Number, v => { a.Number = (ushort)v; nameLabel.Text = ResolveName((ushort)v); Bubble(); });
            AddHex("Variable", 4, () => a.Variable, v => { a.Variable = (ushort)v; Bubble(); });
        }

        AddSchemaFields(a, showAdv);
        if (simplified) AddAdvancedToggle();

        AddHeader("POSITION");
        AddInt("X", () => (int)MathF.Round(a.XPos), v => { a.XPos = v; Bubble(); });
        AddInt("Y", () => (int)MathF.Round(a.YPos), v => { a.YPos = v; Bubble(); });
        AddInt("Z", () => (int)MathF.Round(a.ZPos), v => { a.ZPos = v; Bubble(); });

        AddHeader("ROTATION (binary angle)");
        AddInt("Rot X", () => a.XRot, v => { a.XRot = (short)v; Bubble(); });
        AddInt("Rot Y", () => a.YRot, v => { a.YRot = (short)v; Bubble(); });
        AddInt("Rot Z", () => a.ZRot, v => { a.ZRot = (short)v; Bubble(); });
    }

    // The "Show/Hide Advanced Options" expander in the actor panel (simplified mode only). Persists its state.
    private void AddAdvancedToggle()
    {
        bool on = Editor.EditorSettings.ShowAdvancedActorOptions;
        var btn = new Button
        {
            Text = on ? "▴  Hide Advanced Options" : "▾  Show Advanced Options",
            Dock = DockStyle.Fill, Height = 24, FlatStyle = FlatStyle.Flat, BackColor = BgInput, ForeColor = FgNormal,
            Font = UiFonts.Get("Segoe UI", 8f), Margin = new Padding(2, 6, 2, 2), TextAlign = ContentAlignment.MiddleLeft,
        };
        btn.FlatAppearance.BorderColor = HdrBg;
        btn.Click += (_, _) => { Editor.EditorSettings.ShowAdvancedActorOptions = !on; ForceRefresh(); };
        int row = _table.RowCount;
        _table.Controls.Add(btn, 0, row);
        _table.SetColumnSpan(btn, 2);
        _table.RowCount = row + 1;
    }

    // Render an actor's decoded param schema (Contents dropdown, chest type, switch flag, …) inline in the
    // panel, mirroring the double-click EntityConfigDialog so common edits need no dialog.
    private void AddSchemaFields(Editor.ZActor a, bool showAdv)
    {
        var def = Editor.ActorParamSchema.For(_nativeIsOoT, a.Number);
        if (def == null) return;
        // Basic mode hides the technical (advanced) fields; the editor auto-manages the logic behind them.
        var fields = def.Fields.Where(f => showAdv || !f.IsAdvanced).ToList();
        if (fields.Count == 0) return;
        AddHeader("SETTINGS");
        foreach (var f in fields)
        {
            int Cur() => f.Get(f.FromRotZ ? (ushort)a.ZRot : a.Variable);
            void Put(int v)
            {
                if (f.FromRotZ) a.ZRot = (short)f.Set((ushort)a.ZRot, v);
                else a.Variable = f.Set(a.Variable, v);
                Bubble();
            }
            if (f.Kind == Editor.ActorParamSchema.FieldKind.Enum && f.Options is { Count: > 0 })
            {
                var combo = new ComboBox
                {
                    Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgInput,
                    ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 8f),
                    Margin = new Padding(2), MaxDropDownItems = 24, Tag = f.Desc,
                };
                foreach (var opt in f.Options) combo.Items.Add(opt);
                int cur = Cur();
                int idx = cur >= 0 && cur < f.Options.Count ? cur : -1;
                if (idx < 0) { combo.Items.Add($"Custom ({cur})"); idx = combo.Items.Count - 1; }
                combo.SelectedIndex = idx;
                combo.SelectedIndexChanged += (_, _) =>
                { if (_loading) return; int i = combo.SelectedIndex; if (i >= 0 && i < f.Options.Count) Put(i); };
                // NB: no ComboTip here — the shared ToolTip retains references to disposed combos, and the
                // actor panel rebuilds a fresh combo per selection, so tooltipping every schema field over a
                // long session accumulates handle pressure (the "Error creating window handle" crash). The
                // dropdown items are self-explanatory (e.g. "Fairy Bow"), so a tooltip adds nothing.
                AddRow(f.Name, combo);
            }
            else
            {
                AddInt(f.Name, Cur, Put);   // Int / Flag / Message → a number field
            }
        }
    }

    // ── Brush / trigger inspector ──────────────────────────────────────────

    private void BuildSolid(Editor.Solid solid)
    {
        AddHeader("BRUSH");
        var (mn, mx) = solid.GetAABB();
        AddReadonly("Size", $"{(int)(mx.X-mn.X)} × {(int)(mx.Y-mn.Y)} × {(int)(mx.Z-mn.Z)}");

        AddHeader("WARP TRIGGER");
        // A brush is a warp trigger via the IsTrigger flag OR a WARP tool-texture face; either exposes the
        // destination entrance so a WARP-painted brush is configurable without also toggling the flag.
        bool warpByTexture = Export.CollisionBuilder.IsWarpTrigger(solid) && !solid.IsTrigger;
        AddCheck("Is warp trigger", () => solid.IsTrigger, v =>
        {
            solid.IsTrigger = v;
            if (v && solid.ExitEntrance < 0) solid.ExitEntrance = 0;
            ForceRefresh();        // show/hide the destination field
            Bubble();
        });
        if (warpByTexture) AddReadonly("Trigger source", "WARP tool texture");
        if (solid.IsTrigger || warpByTexture)
            AddHex("Dest entrance", 4, () => solid.ExitEntrance < 0 ? 0 : solid.ExitEntrance,
                   v => { solid.ExitEntrance = v; Bubble(); });

        AddHeader("WATER");
        AddCheck("Is water box", () => solid.IsWater, v => { solid.IsWater = v; ForceRefresh(); Bubble(); });
        if (solid.IsWater)
            AddInt("Water room (0x3F = all)", () => solid.WaterRoom, v => { solid.WaterRoom = Clamp(v, 0, 0x3F); Bubble(); });

        // Solidity: a non-solid brush still renders but emits no collision (walk-through / fake wall).
        AddHeader("SOLIDITY");
        AddCheck("Non-solid (visual only — no collision)", () => solid.NoCollision,
                 v => { solid.NoCollision = v; Bubble(); });

        // Collision surface — Hammer-style individual fields, each mapping to the exact decomp SurfaceType
        // bits (so OoT/MM/SoH/2Ship behave identically). Quick presets + raw words remain below for power use.
        AddHeader("COLLISION SURFACE");
        AddOptionCombo("Floor property", Editor.SurfaceType.FloorProperties,
            () => Editor.SurfaceType.FloorProperty(solid.SurfaceData0),
            v => { solid.SurfaceData0 = Editor.SurfaceType.WithFloorProperty(solid.SurfaceData0, v); ForceRefresh(); Bubble(); });
        // Hurt / lava floors: the FloorType field carries the vanilla contact-damage + fire hazards (see
        // SurfaceType.FloorHazards). Set this to make a brush's floor damage the player, like a lava pit.
        AddOptionCombo("Floor hazard (fire / damage)", Editor.SurfaceType.FloorHazards,
            () => Editor.SurfaceType.FloorType(solid.SurfaceData0),
            v => { solid.SurfaceData0 = Editor.SurfaceType.WithFloorType(solid.SurfaceData0, v); ForceRefresh(); Bubble(); });
        AddOptionCombo("Wall type", Editor.SurfaceType.WallTypes,
            () => Editor.SurfaceType.WallType(solid.SurfaceData0),
            v => { solid.SurfaceData0 = Editor.SurfaceType.WithWallType(solid.SurfaceData0, v); ForceRefresh(); Bubble(); });
        AddOptionCombo("Footstep material", Editor.SurfaceType.Materials,
            () => Editor.SurfaceType.Material(solid.SurfaceData1),
            v => { solid.SurfaceData1 = Editor.SurfaceType.WithMaterial(solid.SurfaceData1, v); ForceRefresh(); Bubble(); });
        AddCheck("Hookshot-able", () => Editor.SurfaceType.Hookshot(solid.SurfaceData1),
            v => { solid.SurfaceData1 = Editor.SurfaceType.WithHookshot(solid.SurfaceData1, v); ForceRefresh(); Bubble(); });
        AddCheck("Soft / sinking floor", () => Editor.SurfaceType.Soft(solid.SurfaceData0),
            v => { solid.SurfaceData0 = Editor.SurfaceType.WithSoft(solid.SurfaceData0, v); ForceRefresh(); Bubble(); });
        AddCheck("Horse can't cross", () => Editor.SurfaceType.HorseBlocked(solid.SurfaceData0),
            v => { solid.SurfaceData0 = Editor.SurfaceType.WithHorseBlocked(solid.SurfaceData0, v); ForceRefresh(); Bubble(); });
        AddInt("Conveyor speed (0 = off)", () => Editor.SurfaceType.ConveyorSpeed(solid.SurfaceData1),
            v => { solid.SurfaceData1 = Editor.SurfaceType.WithConveyorSpeed(solid.SurfaceData1, Clamp(v, 0, 7)); ForceRefresh(); Bubble(); });
        AddInt("Conveyor direction (0-63)", () => Editor.SurfaceType.ConveyorDirection(solid.SurfaceData1),
            v => { solid.SurfaceData1 = Editor.SurfaceType.WithConveyorDirection(solid.SurfaceData1, Clamp(v, 0, 63)); ForceRefresh(); Bubble(); });

        AddHeader("SURFACE TYPE (presets / raw)");
        AddSurfaceTypeCombo(solid);
        AddHex("data[0] (raw)", 8, () => (int)solid.SurfaceData0, v => { solid.SurfaceData0 = (uint)v; ForceRefresh(); Bubble(); });
        AddHex("data[1] (raw)", 8, () => (int)solid.SurfaceData1, v => { solid.SurfaceData1 = (uint)v; ForceRefresh(); Bubble(); });

        BuildSceneAndRoom();
    }

    // A labelled dropdown bound to an (int value → friendly label) option list (Hammer-style property field).
    private void AddOptionCombo(string label, (int Value, string Label)[] options, Func<int> get, Action<int> set)
    {
        var combo = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgInput,
            ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 8f), Margin = new Padding(2),
            MaxDropDownItems = 16,
        };
        foreach (var (val, lbl) in options) combo.Items.Add(lbl);
        int cur = get();
        int sel = Array.FindIndex(options, o => o.Value == cur);
        if (sel < 0) { combo.Items.Add($"Other ({cur})"); sel = combo.Items.Count - 1; }
        combo.SelectedIndex = sel;
        ComboTip(combo);
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            int i = combo.SelectedIndex;
            if (i >= 0 && i < options.Length) set(options[i].Value);
        };
        AddRow(label, combo);
    }

    private void AddSurfaceTypeCombo(Editor.Solid solid)
    {
        var combo = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgInput,
            ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 8f), Margin = new Padding(2),
            MaxDropDownItems = 16,
        };
        foreach (var p in Editor.SurfaceTypePresets.All) combo.Items.Add(p.Name);
        int idx = Editor.SurfaceTypePresets.IndexOf(solid.SurfaceData0, solid.SurfaceData1);
        if (idx < 0) { combo.Items.Add($"Custom (0x{solid.SurfaceData0:X8} / 0x{solid.SurfaceData1:X8})"); idx = combo.Items.Count - 1; }
        combo.SelectedIndex = idx;
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            int i = combo.SelectedIndex;
            if (i >= 0 && i < Editor.SurfaceTypePresets.All.Length)
            {
                var p = Editor.SurfaceTypePresets.All[i];
                solid.SurfaceData0 = p.Data0; solid.SurfaceData1 = p.Data1;
                ForceRefresh(); Bubble();   // refresh the raw hex fields
            }
        };
        AddRow("Preset", combo);
    }

    // ── Scene + room inspector ─────────────────────────────────────────────

    private void BuildSceneAndRoom()
    {
        var s = _doc.Scene.Settings;

        AddHeader("SCENE");
        AddText("Name", () => _doc.Scene.Name, v => { _doc.Scene.Name = v; Bubble(); });
        AddInt ("Skybox", () => s.SkyboxId, v => { s.SkyboxId = (byte)Clamp(v, 0, 255); Bubble(); });
        AddDrawConfigCombo(s);
        AddCheck("Indoor light", () => s.IndoorLighting, v => { s.IndoorLighting = v; Bubble(); });
        AddCheck("Cloudy", () => s.Cloudy, v => { s.Cloudy = v; Bubble(); });
        AddCheck("Dungeon", () => s.Dungeon, v => { s.Dungeon = v; Bubble(); });   // loads dungeon keep (pots/keys/doors)
        if (_nativeIsOoT)   // door theme: render a Door_Shutter as that dungeon's door + lattice grille
        {
            AddOptionCombo("Door style", new (int, string)[]
            {
                (0, "Default (dungeon + lattice)"), (1, "Deku Tree"), (2, "Dodongo's Cavern"), (3, "Jabu-Jabu"),
                (5, "Fire Temple"), (6, "Water Temple"), (7, "Spirit Temple"), (8, "Shadow Temple"), (10, "Gerudo Training"),
            }, () => s.DoorStyle, v => { s.DoorStyle = (byte)v; Bubble(); });
            // Boss door emblem (seg-8). Scene-wide in OoT (the engine derives it from the scene), so every
            // SHUTTER_BOSS door in this scene shows the chosen temple's door texture.
            AddOptionCombo("Boss door", new (int, string)[]
            {
                (0, "Default"), (1, "Fire Temple"), (2, "Water Temple"), (3, "Shadow Temple"),
                (4, "Ganon's Castle"), (5, "Forest Temple"), (6, "Spirit Temple"),
            }, () => s.BossDoorTheme, v => { s.BossDoorTheme = (byte)v; Bubble(); });
        }
        AddSkyCombo(s);
        AddMusicCombo(s);
        AddInt ("Night SFX", () => s.NightSfx, v => { s.NightSfx = (byte)Clamp(v, 0, 255); Bubble(); });
        AddSetupControls();

        AddHeader("COLLISION SUBDIVISION");
        AddInt("Subdiv X", () => s.SubdivX, v => { s.SubdivX = (byte)Clamp(v, 1, 64); Bubble(); });
        AddInt("Subdiv Y", () => s.SubdivY, v => { s.SubdivY = (byte)Clamp(v, 1, 64); Bubble(); });
        AddInt("Subdiv Z", () => s.SubdivZ, v => { s.SubdivZ = (byte)Clamp(v, 1, 64); Bubble(); });

        // Wind (cmd 0x05) — direction (−128..127) + speed; 0 = no wind. Drives grass/particle drift.
        AddInt("Wind dir X",  () => s.WindX, v => { s.WindX = (sbyte)Clamp(v, -128, 127); Bubble(); });
        AddInt("Wind dir Y",  () => s.WindY, v => { s.WindY = (sbyte)Clamp(v, -128, 127); Bubble(); });
        AddInt("Wind dir Z",  () => s.WindZ, v => { s.WindZ = (sbyte)Clamp(v, -128, 127); Bubble(); });
        AddInt("Wind speed",  () => s.WindSpeed, v => { s.WindSpeed = (byte)Clamp(v, 0, 255); Bubble(); });

        // MM: starting weekEventReg flags set on playtest boot (hex, comma-separated; each =
        // (byteIndex<<8)|bitMask). Lets the scene start in a world-state (e.g. a temple cleared).
        if (!_nativeIsOoT)
        {
            var weBox = MakeBox(string.Join(", ", s.StartWeekEvents.Select(v => "0x" + v.ToString("X4"))));
            weBox.TextChanged += (_, _) =>
            {
                if (_loading) return;
                var list = new List<int>();
                foreach (var tok in weBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string t = tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? tok[2..] : tok;
                    if (int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) list.Add(v & 0xFFFF);
                }
                s.StartWeekEvents = list; Bubble();
            };
            AddRow("Start week-events", weBox);
        }

        // Playtest time-of-day (normalized gamestate): one u16 over a 24h day from midnight —
        // 0x4000=6:00, 0x8000=noon, 0xC000=18:00. Applied identically by SoH (dayTime), 2Ship
        // (save.time) and PJ64 (gSaveContext dayTime), so every engine starts at the same time.
        AddHex("Playtest time (8000=noon)", 4, () => s.PlaytestTimeOfDay, v => { s.PlaytestTimeOfDay = (ushort)v; Bubble(); });

        AddHeader("SPAWN");
        AddInt("Spawn X", () => (int)MathF.Round(s.SpawnPos.X), v => { s.SpawnPos = new Vector3(v, s.SpawnPos.Y, s.SpawnPos.Z); Bubble(); });
        AddInt("Spawn Y", () => (int)MathF.Round(s.SpawnPos.Y), v => { s.SpawnPos = new Vector3(s.SpawnPos.X, v, s.SpawnPos.Z); Bubble(); });
        AddInt("Spawn Z", () => (int)MathF.Round(s.SpawnPos.Z), v => { s.SpawnPos = new Vector3(s.SpawnPos.X, s.SpawnPos.Y, v); Bubble(); });
        AddInt("Spawn yaw", () => s.SpawnYaw, v => { s.SpawnYaw = (short)v; Bubble(); });
        AddInt("Spawn room", () => s.SpawnRoom, v => { s.SpawnRoom = Clamp(v, 0, Math.Max(0, _doc.Scene.Rooms.Count - 1)); Bubble(); });

        // ── Point lights (cmd 0x0C) — glowing light sources on top of the env lighting ──
        AddHeader($"POINT LIGHTS ({_doc.Scene.PointLights.Count})");
        var lbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(1) };
        Button LBtn(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(1), BackColor = BgInput,
            ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 8f) };
        var addL = LBtn("+ Add (at spawn)"); var delL = LBtn("Remove last");
        addL.Click += (_, _) => { _doc.Scene.PointLights.Add(new ScenePointLight { X = s.SpawnPos.X, Y = s.SpawnPos.Y + 40, Z = s.SpawnPos.Z }); Bubble(); ForceRefresh(); };
        delL.Click += (_, _) => { var pl = _doc.Scene.PointLights; if (pl.Count > 0) { pl.RemoveAt(pl.Count - 1); Bubble(); ForceRefresh(); } };
        lbar.Controls.AddRange([addL, delL]);
        AddRow("Lights", lbar);
        for (int li = 0; li < _doc.Scene.PointLights.Count; li++)
        {
            var L = _doc.Scene.PointLights[li];
            AddInt($"L{li}  X",   () => (int)MathF.Round(L.X), v => { L.X = v; Bubble(); });
            AddInt($"L{li}  Y",   () => (int)MathF.Round(L.Y), v => { L.Y = v; Bubble(); });
            AddInt($"L{li}  Z",   () => (int)MathF.Round(L.Z), v => { L.Z = v; Bubble(); });
            AddInt($"L{li}  radius", () => L.Radius, v => { L.Radius = (short)Clamp(v, 1, 32767); Bubble(); });
            AddInt($"L{li}  R",   () => L.R, v => { L.R = (byte)Clamp(v, 0, 255); Bubble(); });
            AddInt($"L{li}  G",   () => L.G, v => { L.G = (byte)Clamp(v, 0, 255); Bubble(); });
            AddInt($"L{li}  B",   () => L.B, v => { L.B = (byte)Clamp(v, 0, 255); Bubble(); });
        }

        AddHeader("ENVIRONMENT");
        AddColor("Ambient",  () => s.Ambient,   v => { s.Ambient = v;   Bubble(); });
        AddColor("Light 1",  () => s.Light1Col, v => { s.Light1Col = v; Bubble(); });
        AddColor("Light 2",  () => s.Light2Col, v => { s.Light2Col = v; Bubble(); });
        AddColor("Fog",      () => s.FogColor,  v => { s.FogColor = v;  Bubble(); });
        AddHex  ("Fog near", 4, () => s.FogNear, v => { s.FogNear = (ushort)v; Bubble(); });
        AddHex  ("Fog far",  4, () => s.FogFar,  v => { s.FogFar = (ushort)v;  Bubble(); });

        var room = _doc.Scene.ActiveRoom;
        if (room != null)
        {
            var r = room.Settings;
            AddHeader($"ROOM — {room.Name}");
            AddText ("Name", () => room.Name, v => { room.Name = v; Bubble(); });
            AddInt  ("Echo", () => r.Echo, v => { r.Echo = (byte)Clamp(v, 0, 255); Bubble(); });
            AddHex  ("Time override", 4, () => r.TimeOverride, v => { r.TimeOverride = (ushort)v; Bubble(); });
            AddInt  ("Time speed", () => r.TimeSpeed, v => { r.TimeSpeed = (byte)Clamp(v, 0, 255); Bubble(); });
            // Friendly toggle for the common case: time speed 0 freezes the day/night clock in this
            // room (OoT Market / MM Lost Woods Intro & The Moon); unchecking restores the normal rate.
            // On MM the choice is remembered as the default for the next new room/map (most MM maps want
            // a frozen clock while editing), so devs don't have to re-set it on consecutive maps.
            AddCheck("Freeze time of day", () => r.TimeSpeed == 0, v =>
            {
                r.TimeSpeed = (byte)(v ? 0 : 3);
                if (!_nativeIsOoT) Editor.EditorSettings.MmFlowOfTime = !v;
                Bubble();
            });
            AddCheck("Show invisible actors", () => r.ShowInvisibleActors, v => { r.ShowInvisibleActors = v; Bubble(); });
            AddCheck("Disable skybox", () => r.DisableSkybox, v => { r.DisableSkybox = v; Bubble(); });
            AddCheck("Disable sun/moon", () => r.DisableSunMoon, v => { r.DisableSunMoon = v; Bubble(); });
        }
    }

    // ── Row builders ───────────────────────────────────────────────────────

    private void AddHeader(string text)
    {
        var lbl = new Label
        {
            Text = text, Dock = DockStyle.Fill, Height = 22, Margin = new Padding(0, 8, 0, 2),
            BackColor = HdrBg, ForeColor = HdrFg, TextAlign = ContentAlignment.MiddleLeft,
            Font = UiFonts.Get("Segoe UI", 7.5f, FontStyle.Bold), Padding = new Padding(4, 0, 0, 0),
        };
        int row = _table.RowCount;
        _table.Controls.Add(lbl, 0, row);
        _table.SetColumnSpan(lbl, 2);
        _table.RowCount = row + 1;
    }

    private Label AddReadonly(string label, string value)
    {
        var v = new Label
        {
            Text = value, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(150, 150, 150),
            Font = UiFonts.Get("Segoe UI", 8f), TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true, Margin = new Padding(2),
        };
        AddRow(label, v);
        return v;
    }

    private void AddText(string label, Func<string> get, Action<string> set)
    {
        var box = MakeBox(get());
        box.TextChanged += (_, _) => { if (!_loading) set(box.Text); };
        AddRow(label, box);
    }

    private void AddInt(string label, Func<int> get, Action<int> set)
    {
        var box = MakeBox(get().ToString(CultureInfo.InvariantCulture));
        box.TextChanged += (_, _) =>
        {
            if (_loading) return;
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) set(v);
        };
        AddRow(label, box);
    }

    private void AddHex(string label, int digits, Func<int> get, Action<int> set)
    {
        var box = MakeBox("0x" + get().ToString("X" + digits));
        box.TextChanged += (_, _) =>
        {
            if (_loading) return;
            string t = box.Text.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
            if (int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) set(v);
        };
        AddRow(label, box);
    }

    // 3D-view sky selector: None (interior/dungeon), Day (blue), or Cloudy. Drives the editor's
    // sky render and is inherited from the ROM on import.
    // Scene draw-config picker. Named presets (per game) for the engine's per-frame scene routine —
    // material scroll, the Deku Tree death texture-morph, and the reflective water/floor effect
    // (Water Temple, Chamber of the Sages, Zora's Domain, Great Bay Temple). Sets DrawConfig.
    private void AddDrawConfigCombo(SceneSettings s)
    {
        var presets = Editor.DrawConfigPresets.For(_nativeIsOoT);
        var combo = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgInput,
            ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 8f), Margin = new Padding(2),
            MaxDropDownItems = 24,
        };
        foreach (var p in presets) combo.Items.Add($"{p.Id,2}  {p.Name}");

        int idx = -1;
        for (int i = 0; i < presets.Count; i++) if (presets[i].Id == s.DrawConfig) { idx = i; break; }
        if (idx < 0) { combo.Items.Add($"Custom (0x{s.DrawConfig:X2})"); idx = combo.Items.Count - 1; }
        combo.SelectedIndex = idx;

        combo.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            int i = combo.SelectedIndex;
            if (i >= 0 && i < presets.Count) { s.DrawConfig = presets[i].Id; Bubble(); }
        };
        AddRow("Draw config", combo);
    }

    private void AddSkyCombo(SceneSettings s)
    {
        var combo = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgInput,
            ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 8.5f), Margin = new Padding(2),
        };
        combo.Items.AddRange(["None (interior)", "Day (blue sky)", "Cloudy"]);
        combo.SelectedIndex = (int)s.Sky;
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            s.Sky = (SkyMode)combo.SelectedIndex;
            Bubble();
        };
        AddRow("Sky", combo);
    }

    // Scene-music picker. Shows the native game's songs and, when cross-game music is enabled in
    // Options (with an opposite-game source), a divider and the other game's compatible songs. The
    // chosen song's source game is remembered in SceneSettings.MusicCrossGame.
    private void AddMusicCombo(SceneSettings s)
    {
        var entries = Editor.CrossGameAudio.BuildList(_nativeIsOoT);
        if (entries.Count == 0)
        {
            AddInt("Music seq", () => s.MusicSeq, v => { s.MusicSeq = (byte)Clamp(v, 0, 255); Bubble(); });
            return;
        }

        var combo = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgInput,
            ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 8f), Margin = new Padding(2),
            MaxDropDownItems = 24,
        };
        foreach (var e in entries) combo.Items.Add(e);

        int Match() // current selection: id + which game it belongs to
        {
            for (int i = 0; i < entries.Count; i++)
                if (!entries[i].IsDivider && entries[i].Id == s.MusicSeq && entries[i].Opposite == s.MusicCrossGame)
                    return i;
            return -1;
        }
        int lastValid = Match();
        if (lastValid >= 0) combo.SelectedIndex = lastValid;

        // One-shot/jingle/intro sequences (e.g. 0x1E "Opening ~Lost in a Dark Wood~") play once and stop;
        // they have no loop point, so as scene BGM they fall silent after one pass. Warn so the author can
        // pick a looping track instead. Only meaningful for the native game's own seqs (not cross-game).
        var warn = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, Height = 26, ForeColor = Color.FromArgb(230, 170, 70),
            Font = UiFonts.Get("Segoe UI", 7.5f), Margin = new Padding(2, 0, 2, 2), Text = "",
            TextAlign = ContentAlignment.TopLeft, Visible = false,
        };
        void UpdateWarn()
        {
            bool oneShot = !s.MusicCrossGame && IsNonLoopingSeq(s.MusicSeq, _nativeIsOoT);
            warn.Visible = oneShot;
            warn.Text = oneShot ? "⚠ This is a one-shot/jingle and won't loop as background music — pick a looping track." : "";
        }
        UpdateWarn();

        combo.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            if (combo.SelectedItem is not Editor.MusicEntry e) return;
            if (e.IsDivider) { _loading = true; combo.SelectedIndex = lastValid; _loading = false; return; }
            lastValid = combo.SelectedIndex;
            s.MusicSeq = e.Id;
            s.MusicCrossGame = e.Opposite;
            UpdateWarn();
            Bubble();
        };
        ComboTip(combo);   // #6: hover shows the full (often-truncated) track name
        AddRow("Music", combo);
        AddRow("", warn);
    }

    // Sequence ids that play once and stop (intros, fanfares, item/song jingles, cutscene stings, ocarina
    // playbacks, "Ptr to" pointers, music-practice) — they carry no loop point, so they go silent if used
    // as scene BGM. Used only to surface a UX warning; the seq is still allowed.
    private static readonly HashSet<byte> MmNonLooping = new()
    {
        0x00, 0x08, 0x09, 0x19, 0x1E, 0x20, 0x21, 0x22, 0x24, 0x28, 0x2B, 0x32, 0x33, 0x34, 0x35, 0x37,
        0x39, 0x3F, 0x41, 0x47, 0x48, 0x49, 0x4A, 0x51, 0x52, 0x55, 0x56, 0x58, 0x59, 0x5B, 0x5C, 0x5D,
        0x5E, 0x5F, 0x60, 0x61, 0x62, 0x63, 0x64, 0x6C, 0x6D, 0x6E, 0x70, 0x74, 0x75, 0x77, 0x78, 0x79,
        0x7A, 0x7C, 0x7E, 0x7F,
    };
    // OoT one-shots: genuine fanfares / get-jingles / cutscene stings / ocarina song melodies that carry NO
    // loop point (NA_BGM ids, names cross-checked against the fast64 seqId enum). The OLD list wrongly flagged
    // ~20 LOOPING area/boss/dungeon themes (Jabu 0x26, Mini-Boss 0x38, Kokiri 0x3C, Lost Woods 0x3E, Zora's
    // 0x50, Temple of Time 0x3A, Kakariko 0x19, Ganondorf/Ganon 0x64/0x65, Fire Boss 0x6B, field segments,
    // …) → false warnings on tracks that DO loop. This set is only the tracks that truly play once and stop.
    private static readonly HashSet<byte> OotNonLooping = new()
    {
        // Fanfares / get-jingles / cutscene stings
        0x20, // Game Over
        0x21, // Boss Clear
        0x22, // Item Get
        0x24, // Heart Get
        0x2B, // Open Treasure Chest
        0x32, // Spiritual Stone Get
        0x39, // Obtain Small Item
        0x3B, // Escape from Lon Lon Ranch (event-clear jingle)
        0x3D, // Obtain Fairy Ocarina
        0x41, // Horse Race Goal
        0x43, // Obtain Medallion
        0x51, // Enter Zelda (Appear)
        0x52, // Goodbye to Zelda
        0x53, // Master Sword
        0x54, // Ganon Intro
        0x59, // Open Door of Temple of Time
        0x66, // Seal of Six Sages (End Demo)
        // Ocarina song melodies — play the tune once, no loop
        0x25, // Prelude of Light
        0x33, // Bolero of Fire
        0x34, // Minuet of Forest
        0x35, // Serenade of Water
        0x36, // Requiem of Spirit
        0x37, // Nocturne of Shadow
        0x44, // Saria's Song
        0x45, // Epona's Song
        0x46, // Zelda's Lullaby
        0x47, // Sun's Song
        0x48, // Song of Time
        0x49, // Song of Storms
        0x5E, // Ocarina of Time
    };
    private static bool IsNonLoopingSeq(byte id, bool oot) => (oot ? OotNonLooping : MmNonLooping).Contains(id);

    // Scene setups (alternate headers 0x18: OoT age, MM time-of-day) — a variant switcher with
    // Add / Rename / Delete, so multiple lighting/spawn/actor variants are authored from scratch.
    private void AddSetupControls()
    {
        var scene = _doc.Scene;
        var combo = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgInput,
            ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 8.5f), Margin = new Padding(2),
        };
        if (scene.Setups.Count == 0) combo.Items.Add("(single header)");
        else foreach (var su in scene.Setups) combo.Items.Add(su.Name);
        combo.SelectedIndex = scene.Setups.Count == 0 ? 0 : Math.Clamp(scene.ActiveSetup, 0, scene.Setups.Count - 1);
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || scene.Setups.Count == 0) return;
            _doc.SwitchSetup(combo.SelectedIndex);
        };
        AddRow("Setup", combo);

        // Semantic layer (cmd 0x18): documents what condition loads this setup (OoT age×time / cutscene).
        if (scene.Setups.Count > 0)
        {
            var layer = new ComboBox
            {
                Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgInput,
                ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 8.5f), Margin = new Padding(2),
            };
            foreach (var v in Enum.GetNames(typeof(SetupLayer))) layer.Items.Add(v);
            layer.SelectedIndex = (int)scene.Setups[Math.Clamp(scene.ActiveSetup, 0, scene.Setups.Count - 1)].Layer;
            layer.SelectedIndexChanged += (_, _) =>
            {
                if (_loading || scene.Setups.Count == 0) return;
                scene.Setups[scene.ActiveSetup].Layer = (SetupLayer)layer.SelectedIndex;
                _doc.NotifyChanged();
            };
            AddRow("Loads under", layer);
        }

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(1) };
        Button Btn(string t) => new()
        {
            Text = t, AutoSize = true, Margin = new Padding(1), BackColor = BgInput, ForeColor = FgNormal,
            FlatStyle = FlatStyle.Flat, Font = UiFonts.Get("Segoe UI", 8f),
        };
        var add = Btn("+ Add"); var ren = Btn("Rename"); var del = Btn("Delete");
        add.Click += (_, _) => { var n = Prompt("New setup", scene.Setups.Count == 0 ? "Night" : $"Setup {scene.Setups.Count}"); if (n != null) _doc.AddSetup(n); };
        ren.Click += (_, _) => { if (scene.Setups.Count > 0) { var n = Prompt("Rename setup", scene.Setups[scene.ActiveSetup].Name); if (n != null) _doc.RenameSetup(scene.ActiveSetup, n); } };
        del.Click += (_, _) => { if (scene.Setups.Count > 0) _doc.RemoveSetup(scene.ActiveSetup); };
        bar.Controls.AddRange([add, ren, del]);
        AddRow("Variants", bar);
    }

    private string? Prompt(string title, string initial)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(280, 92), MaximizeBox = false, MinimizeBox = false, BackColor = BgDark, ForeColor = FgNormal,
        };
        var tb = new TextBox { Text = initial, Left = 12, Top = 14, Width = 256, BackColor = BgInput, ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 112, Top = 50, Width = 72, BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 192, Top = 50, Width = 76, BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        f.Controls.AddRange([tb, ok, cancel]); f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK && tb.Text.Trim().Length > 0 ? tb.Text.Trim() : null;
    }

    private void AddCheck(string label, Func<bool> get, Action<bool> set)
    {
        // #11: put the label ON the checkbox (AutoSize) spanning both columns, so long captions like
        // "Freeze time of day" / "Disable skybox" aren't truncated by the narrow label column.
        var chk = new CheckBox
        {
            Text = label, Checked = get(), AutoSize = true, Margin = new Padding(2),
            ForeColor = FgNormal, BackColor = BgDark,
        };
        chk.CheckedChanged += (_, _) => { if (!_loading) set(chk.Checked); };
        int row = _table.RowCount;
        _table.Controls.Add(chk, 0, row);
        _table.SetColumnSpan(chk, 2);
        _table.RowCount = row + 1;
    }

    private void AddColor(string label, Func<RgbColor> get, Action<RgbColor> set)
    {
        var c = get();
        var swatch = new Panel { Width = 22, Height = 18, BackColor = Color.FromArgb(c.R, c.G, c.B),
                                 BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(2), Cursor = Cursors.Hand };
        var box = MakeBox($"{c.R:X2}{c.G:X2}{c.B:X2}");
        box.Width = 70;
        void Commit()
        {
            if (_loading) return;
            string t = box.Text.Trim().TrimStart('#');
            if (t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            {
                var rc = RgbColor.From((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
                swatch.BackColor = Color.FromArgb(rc.R, rc.G, rc.B);
                set(rc);
            }
        }
        box.TextChanged += (_, _) => Commit();
        swatch.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = swatch.BackColor, FullOpen = true };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _loading = true; box.Text = $"{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}"; _loading = false;
                swatch.BackColor = dlg.Color;
                set(RgbColor.From(dlg.Color.R, dlg.Color.G, dlg.Color.B));
            }
        };

        var holder = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        holder.Controls.Add(swatch);
        holder.Controls.Add(box);
        AddRow(label, holder);
    }

    private void AddRow(string label, Control control)
    {
        int row = _table.RowCount;
        var lbl = new Label
        {
            Text = label, Dock = DockStyle.Fill, ForeColor = FgNormal, Font = UiFonts.Get("Segoe UI", 8f),
            TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(2),
        };
        _table.Controls.Add(lbl, 0, row);
        _table.Controls.Add(control, 1, row);
        _table.RowCount = row + 1;
    }

    private static TextBox MakeBox(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, BackColor = BgInput, ForeColor = FgNormal,
        BorderStyle = BorderStyle.FixedSingle, Font = UiFonts.Get("Consolas", 8.5f), Margin = new Padding(2),
    };

    // ── Helpers ───────────────────────────────────────────────────────────

    private void Bubble() => Changed?.Invoke();

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    private string ResolveName(ushort id)
    {
        var info = _actorDb.All.FirstOrDefault(a => a.Id == id);
        return info != null ? info.Name : $"Actor 0x{id:X4}";
    }

    /// <summary>Forces a full rebuild (e.g. after load or active-room change). DEFERRED via BeginInvoke: the
    /// surface/collision combos call this from their own SelectedIndexChanged, and Rebuild disposes every panel
    /// control — disposing the combo synchronously from inside its own event (while its dropdown is still
    /// closing) is a hard crash. Posting the rebuild to the message queue lets the event finish first. Coalesced
    /// with a queued flag so a burst of changes rebuilds once. When the handle isn't up yet (initial load) run
    /// it inline — there's no live control to dispose mid-event then.</summary>
    public void ForceRefresh()
    {
        if (!IsHandleCreated) { Rebuild(force: true); return; }
        if (_forceQueued) return;
        _forceQueued = true;
        BeginInvoke(() => { _forceQueued = false; Rebuild(force: true); });
    }
    private bool _forceQueued;
}
