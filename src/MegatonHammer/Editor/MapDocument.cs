using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

public sealed class MapDocument
{
    /// <summary>All scenes in the project. A project always has at least one.</summary>
    public List<ZScene> Scenes { get; } = [new()];

    /// <summary>Index of the scene currently being edited.</summary>
    public int ActiveSceneIndex { get; private set; }

    /// <summary>The scene currently being edited (all tools/rendering work on this one).</summary>
    public ZScene Scene => Scenes[Math.Clamp(ActiveSceneIndex, 0, Scenes.Count - 1)];

    /// <summary>A ROM level loaded read-only for viewing/reference in the active scene.</summary>
    public ImportedLevel? Imported { get => Scene.Imported; set => Scene.Imported = value; }

    /// <summary>An imported OBJ/external mesh shown as an untextured reference backdrop.</summary>
    public List<Rom.MeshTri>? ReferenceMesh { get => Scene.ReferenceMesh; set => Scene.ReferenceMesh = value; }

    /// <summary>A "ghost": a whole vanilla ROM level loaded ONLY as a translucent reference to trace over
    /// (no rooms/actors/logic imported). It is document-level and deliberately TRANSIENT — never serialized
    /// into the .mhproj and never auto-loaded — so it evaporates when the project closes. Independent of
    /// <see cref="Imported"/> (an editable import's backdrop).</summary>
    public ImportedLevel? Ghost { get; set; }

    public event Action? Changed;

    /// <summary>Raised when the set of scenes or the active scene changes (for the scene picker UI).</summary>
    public event Action? ScenesChanged;

    /// <summary>True when this editing session targets Majora's Mask / 2Ship — drives game-specific
    /// new-scene defaults (Termina Field sky/lighting/music) and the new-room flow-of-time default.</summary>
    public bool IsMM { get; private set; }

    /// <summary>Default room time-speed for a new room: MM honours the persistent flow-of-time pref
    /// (0 = clock frozen, the MM editing default; 3 = normal flow); OoT keeps the legacy 0.</summary>
    public byte DefaultRoomTimeSpeed => (byte)(IsMM && EditorSettings.MmFlowOfTime ? 3 : 0);

    /// <summary>Called once at startup after the game is known. Records the game and applies its
    /// defaults to the pristine initial scene. Safe before any project load (load replaces scenes).</summary>
    public void InitGameDefaults(bool isMM)
    {
        IsMM = isMM;
        foreach (var sc in Scenes)
        {
            sc.Settings = SceneSettings.DefaultFor(isMM);
            foreach (var rm in sc.Rooms) rm.Settings.TimeSpeed = DefaultRoomTimeSpeed;
        }
    }

    // ── Scene management ───────────────────────────────────────────────────
    /// <summary>Adds a new empty scene and makes it active. Returns its index.</summary>
    public int AddScene(string? name = null)
    {
        var s = new ZScene(name ?? $"Scene {Scenes.Count}");
        s.Settings = SceneSettings.DefaultFor(IsMM);
        foreach (var rm in s.Rooms) rm.Settings.TimeSpeed = DefaultRoomTimeSpeed;
        Scenes.Add(s);
        ActiveSceneIndex = Scenes.Count - 1;
        ScenesChanged?.Invoke();
        Changed?.Invoke();
        return ActiveSceneIndex;
    }

    /// <summary>Removes a scene (keeps at least one). Returns true if removed.</summary>
    public bool RemoveScene(int index)
    {
        if (Scenes.Count <= 1 || index < 0 || index >= Scenes.Count) return false;
        Scenes.RemoveAt(index);
        if (ActiveSceneIndex >= Scenes.Count) ActiveSceneIndex = Scenes.Count - 1;
        ScenesChanged?.Invoke();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Switches the active scene.</summary>
    public void SwitchScene(int index)
    {
        if (index < 0 || index >= Scenes.Count || index == ActiveSceneIndex) return;
        ActiveSceneIndex = index;
        ScenesChanged?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Re-fires the scene-list-changed event (e.g. after a scene is renamed on import).</summary>
    public void NotifyScenesChanged() => ScenesChanged?.Invoke();

    /// <summary>Replaces all scenes (used by project load).</summary>
    internal void RebuildScenes(IReadOnlyList<ZScene> scenes, int active)
    {
        Scenes.Clear();
        Scenes.AddRange(scenes.Count > 0 ? scenes : [new ZScene("New Scene")]);
        ActiveSceneIndex = Math.Clamp(active, 0, Scenes.Count - 1);
        ScenesChanged?.Invoke();
    }

    // All solids across all rooms as a flat list (kept for backward compatibility)
    public IReadOnlyList<Solid> Solids =>
        Scene.Rooms.SelectMany(r => r.Geometry).ToList();

    // All actors across all rooms (unfiltered — used for selection, grouping, export).
    public IEnumerable<ZActor> AllActors =>
        Scene.Rooms.SelectMany(r => r.Actors);

    // Actors only in rooms the user hasn't hidden via the room-tree eye toggle (ZRoom.Visible).
    // This is the render/pick source so hidden rooms drop out of every viewport; export still uses AllActors.
    public IEnumerable<ZActor> RoomVisibleActors =>
        Scene.Rooms.Where(r => r.Visible).SelectMany(r => r.Actors);

    /// <summary>Every decal across all rooms (Hammer-style overlays baked onto surfaces at compile).</summary>
    public IEnumerable<Decal> AllDecals => Scene.Rooms.SelectMany(r => r.Decals);
    /// <summary>Decals in visible rooms — the render/pick source.</summary>
    public IEnumerable<Decal> VisibleDecals => Scene.Rooms.Where(r => r.Visible).SelectMany(r => r.Decals);
    public void AddDecal(Decal d) { ActiveRoom.Decals.Add(d); Changed?.Invoke(); }
    public void RemoveDecal(Decal d) { foreach (var r in Scene.Rooms) r.Decals.Remove(d); Changed?.Invoke(); }

    /// <summary>Mirror each room's editor visibility toggle onto the imported backdrop mesh (index-aligned),
    /// so hiding a room in the tree also hides its imported geometry. No-op when nothing is imported.</summary>
    public void SyncImportedRoomVisibility()
    {
        if (Imported is not { } imp) return;
        for (int i = 0; i < Scene.Rooms.Count && i < imp.RoomVisible.Length; i++)
            imp.RoomVisible[i] = Scene.Rooms[i].Visible;
    }

    /// <summary>The scene's Player Start — the REAL in-game Link spawn. Its placement IS compiled: it
    /// maps to <see cref="SceneSettings.SpawnPos"/>/<see cref="SceneSettings.SpawnYaw"/>, written to the
    /// scene header's player-entry list (cmd 0x00) on export. It's surfaced as a persistent, selectable,
    /// movable marker (Link 0x0000) — dragging it edits the spawn (see <see cref="SyncSpawnFromMarker"/>).
    /// IsEditorOnly only means it isn't a room 0x01 actor (the spawn is exported via the header, not the
    /// room actor list); it is NOT a throwaway dummy. (OoT scene cmd 0x00 supports multiple spawns, but
    /// the editor exposes a single entrance, so one movable Player Start is the right model.)</summary>
    private ZActor? _spawnMarker;
    public ZActor SpawnMarker()
    {
        var s = Scene.Settings;
        _spawnMarker ??= new ZActor { Number = 0x0000, Variable = 0x0FFF, IsEditorOnly = true, IsSpawn = true };
        _spawnMarker.DisplayName = "Player Start (Link spawn)";
        if (!_spawnMarkerDragging)   // pull from settings unless the user is actively dragging it
        {
            _spawnMarker.XPos = s.SpawnPos.X; _spawnMarker.YPos = s.SpawnPos.Y; _spawnMarker.ZPos = s.SpawnPos.Z;
            _spawnMarker.YRot = s.SpawnYaw;
        }
        return _spawnMarker;
    }
    private bool _spawnMarkerDragging;

    /// <summary>Writes the (moved) Player Start marker's placement back into the scene spawn settings.
    /// Call after a SelectTool drag/rotate that touched <see cref="ZActor.IsSpawn"/>.</summary>
    public void SyncSpawnFromMarker()
    {
        if (_spawnMarker == null) return;
        Scene.Settings.SpawnPos = _spawnMarker.Position;
        Scene.Settings.SpawnYaw = _spawnMarker.YRot;
    }
    /// <summary>SelectTool sets this while dragging the spawn so SpawnMarker() doesn't snap it back to settings mid-drag.</summary>
    public bool SpawnMarkerDragging { get => _spawnMarkerDragging; set => _spawnMarkerDragging = value; }

    /// <summary>Whether the Player Start marker is drawn (off for per-actor render audits so Link's
    /// model doesn't overlap the actor under review).</summary>
    public bool ShowSpawnMarker { get; set; } = true;

    /// <summary>Actors to draw in the viewports: every placed actor plus (optionally) the spawn marker,
    /// minus any in a hidden visgroup.</summary>
    public IEnumerable<ZActor> ActorsToRender =>
        (ShowSpawnMarker ? RoomVisibleActors.Append(SpawnMarker()) : RoomVisibleActors).Where(a => !IsHidden(a));

    /// <summary>Actors eligible for selection/picking: placed actors (in visible rooms) plus the movable
    /// Player Start. Hidden rooms drop out so you can't accidentally grab an actor you can't see.</summary>
    public IEnumerable<ZActor> PickableActors => ShowSpawnMarker ? RoomVisibleActors.Append(SpawnMarker()) : RoomVisibleActors;

    // ── Selection accessors (first selected of each kind) ──────────────────
    public ZActor? SelectedActor => PickableActors.FirstOrDefault(a => a.IsSelected);
    public Solid?  SelectedSolid => Solids.FirstOrDefault(s => s.IsSelected);

    /// <summary>Faces currently selected in the Face Edit tool (multi-face texture editing).</summary>
    public IEnumerable<SolidFace> SelectedFaces => Solids.SelectMany(s => s.Faces).Where(f => f.FaceSelected);

    public void ClearFaceSelection()
    {
        foreach (var s in Solids) foreach (var f in s.Faces) f.FaceSelected = false;
    }

    // ── Mutation ──────────────────────────────────────────────────────────

    public void AddSolid(Solid s)
    {
        ActiveRoom.Geometry.Add(s);
        Changed?.Invoke();
    }

    // Backward-compatible alias used by BrushTool
    public void Add(Solid s) => AddSolid(s);

    public void AddActor(ZActor a)
    {
        ActiveRoom.Actors.Add(a);
        Changed?.Invoke();
    }

    public void Remove(Solid s)
    {
        foreach (var r in Scene.Rooms) r.Geometry.Remove(s);
        Changed?.Invoke();
    }

    /// <summary>Replaces a solid in its room with zero or more replacements (used by clip).</summary>
    public void ReplaceSolid(Solid old, IReadOnlyList<Solid> replacements)
    {
        foreach (var r in Scene.Rooms)
        {
            int idx = r.Geometry.IndexOf(old);
            if (idx < 0) continue;
            r.Geometry.RemoveAt(idx);
            r.Geometry.InsertRange(idx, replacements);
            Changed?.Invoke();
            return;
        }
    }

    public void NotifyChanged() => Changed?.Invoke();

    // ── Scene setups (alternate headers 0x18: OoT age, MM time-of-day) ─────
    /// <summary>Adds a setup. The first call promotes the current single-header scene to "Default",
    /// then a new setup (cloned from the active one) is added and made active. Returns its index.</summary>
    public int AddSetup(string? name = null)
    {
        RecordUndo();
        var scene = Scene;
        if (scene.Setups.Count == 0)
        {
            scene.Setups.Add(new ZSetup { Name = "Default" });
            scene.ActiveSetup = 0;
            scene.CommitActiveSetup();          // capture the existing scene as the default variant
        }
        else scene.CommitActiveSetup();

        var src = scene.Setups[scene.ActiveSetup];
        scene.Setups.Add(new ZSetup
        {
            Name = name ?? $"Setup {scene.Setups.Count}",
            Settings = src.Settings.Clone(),
            Environments = [.. src.Environments],
            RoomActors = src.RoomActors.Select(l => l.Select(a => a.Clone()).ToList()).ToList(),
        });
        scene.LoadSetup(scene.Setups.Count - 1);
        ClearSelection();
        Changed?.Invoke();
        return scene.ActiveSetup;
    }

    /// <summary>Makes a setup active: commits the live scene into the current setup, then loads the
    /// target's settings/lighting/per-room actors. Geometry is shared and untouched.</summary>
    public void SwitchSetup(int index)
    {
        var scene = Scene;
        if (index < 0 || index >= scene.Setups.Count || index == scene.ActiveSetup) return;
        scene.CommitActiveSetup();
        scene.LoadSetup(index);
        ClearSelection();
        Changed?.Invoke();
    }

    /// <summary>Removes a setup; collapses back to a single-header scene if only one survives.</summary>
    public bool RemoveSetup(int index)
    {
        var scene = Scene;
        if (index < 0 || index >= scene.Setups.Count) return false;
        RecordUndo();
        if (index != scene.ActiveSetup) scene.CommitActiveSetup();
        scene.Setups.RemoveAt(index);
        if (scene.Setups.Count == 0) { scene.ActiveSetup = 0; Changed?.Invoke(); return true; }
        int newActive = scene.ActiveSetup > index ? scene.ActiveSetup - 1
                      : scene.ActiveSetup == index ? Math.Min(index, scene.Setups.Count - 1)
                      : scene.ActiveSetup;
        scene.LoadSetup(Math.Clamp(newActive, 0, scene.Setups.Count - 1));
        if (scene.Setups.Count == 1) { scene.Setups.Clear(); scene.ActiveSetup = 0; }   // lone survivor → single header
        ClearSelection();
        Changed?.Invoke();
        return true;
    }

    public void RenameSetup(int index, string name)
    {
        var scene = Scene;
        if (index < 0 || index >= scene.Setups.Count) return;
        scene.Setups[index].Name = name;
        Changed?.Invoke();
    }

    // ── Undo / redo (document snapshots) ───────────────────────────────────
    private const int MaxHistory = 64;
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private bool _restoring;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Captures the current state before a mutating operation. Call at gesture start.</summary>
    public void RecordUndo()
    {
        if (_restoring) return;
        _undo.Add(ProjectSerializer.Serialize(this));
        if (_undo.Count > MaxHistory) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void Undo() => Step(_undo, _redo);
    public void Redo() => Step(_redo, _undo);

    /// <summary>Discards undo/redo history (after New / Close / load — there's nothing to undo to).</summary>
    public void ClearUndo() { _undo.Clear(); _redo.Clear(); }

    private void Step(List<string> from, List<string> to)
    {
        if (from.Count == 0) return;
        to.Add(ProjectSerializer.Serialize(this));
        string state = from[^1];
        from.RemoveAt(from.Count - 1);

        _restoring = true;
        try { ProjectSerializer.Deserialize(this, state); }
        finally { _restoring = false; }
        Changed?.Invoke();
    }

    /// <summary>Resets to a single empty scene/room and default settings (File ▸ New / Close).</summary>
    public void Reset()
    {
        Scenes.Clear();
        Scenes.Add(new ZScene("New Scene"));
        ActiveSceneIndex = 0;
        ClearUndo();
        ScenesChanged?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Drops the imported reference geometry (ROM scene mesh) without touching brushes.</summary>
    public void ClearImported()
    {
        Imported = null;
        ReferenceMesh = null;
    }

    /// <summary>True if the active scene has no geometry, actors, or imported mesh.</summary>
    public bool ActiveSceneIsEmpty =>
        Scene.Imported == null && Scene.ReferenceMesh == null &&
        Scene.Rooms.All(r => r.Geometry.Count == 0 && r.Actors.Count == 0);

    /// <summary>Clears just the active scene to a single empty room (leaves other scenes intact).</summary>
    public void ResetActiveScene()
    {
        var s = Scene;
        s.Name     = "New Scene";
        s.Settings = SceneSettings.DefaultFor(IsMM);
        s.Rooms.Clear();
        s.AddRoom();
        foreach (var rm in s.Rooms) rm.Settings.TimeSpeed = DefaultRoomTimeSpeed;
        s.Imported = null;
        s.ReferenceMesh = null;
        Changed?.Invoke();
    }

    public void ClearSelection()
    {
        foreach (var r in Scene.Rooms)
        {
            foreach (var s in r.Geometry) s.IsSelected = false;
            foreach (var a in r.Actors)   a.IsSelected = false;
            foreach (var d in r.Decals)   d.IsSelected = false;
        }
        if (_spawnMarker != null) _spawnMarker.IsSelected = false;   // not in any room
    }

    // ── Grouping (Hammer Ctrl+G / Ctrl+U) ─────────────────────────────────
    /// <summary>Group the current selection under a fresh group id (≥2 objects). Clicking any member
    /// then selects the whole group (see <see cref="SelectGroup"/>).</summary>
    public void GroupSelection()
    {
        var solids = Solids.Where(s => s.IsSelected).ToList();
        var actors = AllActors.Where(a => a.IsSelected && !a.IsEditorOnly).ToList();
        if (solids.Count + actors.Count < 2) return;
        RecordUndo();
        int g = 1 + Math.Max(
            Solids.Select(s => s.GroupId).DefaultIfEmpty(0).Max(),
            AllActors.Select(a => a.GroupId).DefaultIfEmpty(0).Max());
        foreach (var s in solids) s.GroupId = g;
        foreach (var a in actors) a.GroupId = g;
        NotifyChanged();
    }

    /// <summary>A fresh group id above every existing one (for programmatic grouping, e.g. mechanism presets
    /// that drop several wired actors as one selectable unit). Mirrors <see cref="GroupSelection"/>'s allocator.</summary>
    public int NextGroupId() => 1 + Math.Max(
        Solids.Select(s => s.GroupId).DefaultIfEmpty(0).Max(),
        AllActors.Select(a => a.GroupId).DefaultIfEmpty(0).Max());

    /// <summary>Clear the group of every selected object.</summary>
    public void UngroupSelection()
    {
        var solids = Solids.Where(s => s.IsSelected && s.GroupId != 0).ToList();
        var actors = AllActors.Where(a => a.IsSelected && a.GroupId != 0).ToList();
        if (solids.Count + actors.Count == 0) return;
        RecordUndo();
        foreach (var s in solids) s.GroupId = 0;
        foreach (var a in actors) a.GroupId = 0;
        NotifyChanged();
    }

    // ── Named flag channels (the Hammer "targetname" for the OoT/MM flag bus) ──
    /// <summary>Editor-only friendly names for flag channels, keyed by "<kind>:<index>" (e.g.
    /// "Switch:5" → "GateA"). Never compiled — the scene just uses the integer index — but it lets the
    /// connection graph read like Hammer's named entity wiring. Serialized in the project.</summary>
    public Dictionary<string, string> FlagNames { get; } = new();
    public static string FlagKey(ActorParamSchema.FlagKind k, int index) => $"{k}:{index}";
    public string? FlagName(ActorParamSchema.FlagKind k, int index) => FlagNames.GetValueOrDefault(FlagKey(k, index));
    public void SetFlagName(ActorParamSchema.FlagKind k, int index, string? name)
    {
        var key = FlagKey(k, index);
        if (string.IsNullOrWhiteSpace(name)) FlagNames.Remove(key);
        else FlagNames[key] = name.Trim();
        NotifyChanged();
    }

    /// <summary>Lowest unused index in a flag namespace across all placed actors — the channel allocator
    /// (Hammer's "pick a fresh targetname"). <paramref name="max"/> = namespace size (e.g. 64 switch).</summary>
    public int NextFreeFlag(ActorParamSchema.FlagKind k, bool isOoT, int max)
    {
        var used = new HashSet<int>();
        foreach (var g in FlagConnectionAnalyzer.Analyze(AllActors, isOoT))
            if (g.Kind == k) used.Add(g.Index);
        for (int i = 0; i < max; i++) if (!used.Contains(i)) return i;
        return 0;
    }

    /// <summary>Ensures every chest (En_Box) has a UNIQUE treasure flag (params bits [0:5]). Chests sharing a
    /// treasure flag share opened-state — opening/collecting one marks them ALL opened, so on a fresh playtest
    /// the later ones spawn already-open (the reported "chest was already open"). Keeps the first user of each
    /// flag and reassigns any later collision to a free flag. Returns how many it fixed. Run on load + paste,
    /// so a project made before chest auto-flagging (or with pasted/cloned chests) self-heals.</summary>
    public int NormalizeChestFlags()
    {
        ushort chestId = IsMM ? (ushort)0x0006 : (ushort)0x000A;
        var used = new bool[32];
        int fixedCount = 0;
        foreach (var c in AllActors)
        {
            if (c.Number != chestId) continue;
            int f = c.Variable & 0x1F;
            if (!used[f]) { used[f] = true; continue; }
            int free = -1;
            for (int i = 0; i < 32; i++) if (!used[i]) { free = i; break; }
            if (free < 0) break;   // >32 chests: beyond the vanilla flag budget anyway
            c.Variable = (ushort)((c.Variable & ~0x1F) | free);
            used[free] = true;
            fixedCount++;
        }
        return fixedCount;
    }

    /// <summary>Select every member of a group (called by SelectTool when a grouped object is clicked).</summary>
    public void SelectGroup(int groupId)
    {
        if (groupId == 0) return;
        foreach (var s in Solids)    if (s.GroupId == groupId) s.IsSelected = true;
        foreach (var a in AllActors) if (a.GroupId == groupId) a.IsSelected = true;
    }

    // ── Editor cameras (Hammer camera gizmos) ─────────────────────────────
    /// <summary>A saved 3D viewpoint placed/dragged in the 2D views (eye + look point). Editor-only —
    /// these are viewport bookmarks, never exported.</summary>
    public sealed class EditorCamera { public Vector3 Eye; public Vector3 Look; }
    public List<EditorCamera> Cameras { get; } = [];
    public int ActiveCameraIndex { get; set; } = -1;

    // ── Visgroups (Hammer show/hide layers) ───────────────────────────────
    public sealed class VisGroup { public int Id; public string Name = ""; public bool Visible = true; }
    /// <summary>User visgroups. Membership (VisGroupId) is serialized per-object; the defs are rebuilt
    /// from the ids present on load (auto-named, all visible) and toggled live during a session.</summary>
    public List<VisGroup> VisGroups { get; } = [];

    /// <summary>Rebuild visgroup defs for any membership ids not yet represented (e.g. after load).</summary>
    public void RefreshVisGroups() => EnsureVisGroupsForMembership();
    private void EnsureVisGroupsForMembership()
    {
        foreach (int id in Solids.Select(s => s.VisGroupId).Concat(AllActors.Select(a => a.VisGroupId)).Distinct())
            if (id != 0 && VisGroups.All(v => v.Id != id))
                VisGroups.Add(new VisGroup { Id = id, Name = $"Visgroup {id}", Visible = true });
    }

    public bool IsHidden(Solid s) => IsVisGroupHidden(s.VisGroupId);
    public bool IsHidden(ZActor a) => IsVisGroupHidden(a.VisGroupId);
    private bool IsVisGroupHidden(int vg) => vg != 0 && VisGroups.Any(v => v.Id == vg && !v.Visible);

    /// <summary>Actors eligible for drawing AND picking, minus any in a hidden visgroup.</summary>
    public IEnumerable<ZActor> VisibleActors => PickableActors.Where(a => !IsHidden(a));

    /// <summary>Put the current selection into a new visgroup (Hammer "create visgroup from selection").</summary>
    public VisGroup CreateVisGroupFromSelection(string name)
    {
        EnsureVisGroupsForMembership();
        int id = 1 + (VisGroups.Count == 0 ? 0 : VisGroups.Max(v => v.Id));
        var vg = new VisGroup { Id = id, Name = name, Visible = true };
        VisGroups.Add(vg);
        foreach (var s in Solids.Where(s => s.IsSelected)) s.VisGroupId = id;
        foreach (var a in AllActors.Where(a => a.IsSelected && !a.IsEditorOnly)) a.VisGroupId = id;
        NotifyChanged();
        return vg;
    }

    public void ToggleVisGroup(int id)
    {
        var vg = VisGroups.FirstOrDefault(v => v.Id == id);
        if (vg != null) { vg.Visible = !vg.Visible; NotifyChanged(); }
    }

    public void ShowAllVisGroups()
    {
        foreach (var v in VisGroups) v.Visible = true;
        NotifyChanged();
    }

    public void DeleteSelected()
    {
        bool hasSel = Scene.Rooms.Any(r => r.Geometry.Any(s => s.IsSelected) || r.Actors.Any(a => a.IsSelected) || r.Decals.Any(d => d.IsSelected));
        if (hasSel) RecordUndo();

        bool any = false;
        foreach (var r in Scene.Rooms)
        {
            any |= r.Geometry.RemoveAll(s => s.IsSelected) > 0;
            any |= r.Actors.RemoveAll(a => a.IsSelected) > 0;
            any |= r.Decals.RemoveAll(d => d.IsSelected) > 0;
        }
        if (any) Changed?.Invoke();
    }

    private ZRoom ActiveRoom => Scene.ActiveRoom ?? Scene.Rooms[0];

    public int Count      => Scene.Rooms.Sum(r => r.Geometry.Count);
    public int ActorCount => Scene.Rooms.Sum(r => r.Actors.Count);
}
