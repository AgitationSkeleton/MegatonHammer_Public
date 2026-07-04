using System.Text.Json;
using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

/// <summary>
/// Saves/loads the whole editor document to a human-readable JSON project file
/// (.mhproj). Brush geometry is stored as clip planes (the canonical representation)
/// plus per-face texture/colour attributes, then rebuilt on load.
/// </summary>
public static class ProjectSerializer
{
    public const string Extension = ".mhproj";
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        IncludeFields = true,
    };

    // ── Save ────────────────────────────────────────────────────────────────

    public static void Save(MapDocument doc, string path, string? game = null) => File.WriteAllText(path, Serialize(doc, game));

    /// <summary>Serializes the whole document (all scenes) to a JSON string (save + undo snapshots).
    /// <paramref name="game"/> ("oot"/"mm") is recorded only for the recent-files colour coding.</summary>
    public static string Serialize(MapDocument doc, string? game = null)
    {
        var dto = new ProjectDto
        {
            Version     = CurrentVersion,
            Game        = game,
            ActiveScene = doc.ActiveSceneIndex,
            Scenes      = doc.Scenes.Select(SceneToDto).ToList(),
            FlagNames   = doc.FlagNames.Count > 0 ? new Dictionary<string, string>(doc.FlagNames) : null,
        };
        return JsonSerializer.Serialize(dto, Opts);
    }

    private static SceneDto SceneToDto(ZScene scene)
    {
        scene.CommitActiveSetup();   // keep the active setup's snapshot in sync with the live scene
        return new()
    {
        Name       = scene.Name,
        Settings   = ToDto(scene.Settings),
        ActiveRoom = scene.Rooms.FindIndex(r => r.IsActive),
        Rooms      = scene.Rooms.Select(ToDto).ToList(),
        Environments = scene.Environments.Select(ToDto).ToList(),
        Setups       = scene.Setups.Select(ToDto).ToList(),
        ActiveSetup  = scene.ActiveSetup,
        CutsceneB64  = scene.CutsceneData != null ? Convert.ToBase64String(scene.CutsceneData) : null,
        CutsceneOff  = scene.CutsceneOrigOff,
        Paths      = scene.Paths.Select(p => new PathDto
        {
            Name = p.Name, AddIdx = p.AdditionalPathIndex, Custom = p.CustomValue, Closed = p.Closed,
            Pts  = p.Points.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray(),
        }).ToList(),
        Lights = scene.PointLights.Count > 0 ? scene.PointLights.Select(L => new PointLightDto
        {
            X = L.X, Y = L.Y, Z = L.Z, R = L.R, G = L.G, B = L.B, Radius = L.Radius, Glow = L.Glow,
        }).ToList() : null,
        Messages = scene.Messages.Count > 0 ? scene.Messages.Select(m => new MessageDto
        {
            Id = m.Id, Text = m.Text, Box = m.BoxType, Pos = m.YPos, Icon = m.Icon,
            Kind = (int)m.Kind, Choice1 = m.Choice1, Choice2 = m.Choice2,
            Out1 = ToDto(m.Outcome1), Out2 = ToDto(m.Outcome2),
            DoneFlag = m.DoneFlag, AfterMsgId = m.AfterMsgId, Sfx = m.Sfx, Gesture = m.Gesture,
        }).ToList() : null,
        };
    }

    private static OutcomeDto ToDto(MhOutcome o) => new()
    { NextMsgId = o.NextMsgId, FireFlag = o.FireFlag, GiveItem = o.GiveItem, ChargeRupees = o.ChargeRupees, RupeeCost = o.RupeeCost };

    private static MhOutcome FromDto(OutcomeDto? d) => d == null ? new MhOutcome()
        : new MhOutcome { NextMsgId = d.NextMsgId, FireFlag = d.FireFlag, GiveItem = d.GiveItem, ChargeRupees = d.ChargeRupees, RupeeCost = d.RupeeCost };

    private static EnvDto ToDto(Rom.EnvLight e) => new()
    {
        AmbR = e.AmbR, AmbG = e.AmbG, AmbB = e.AmbB,
        L1x = e.L1x, L1y = e.L1y, L1z = e.L1z, L1r = e.L1r, L1g = e.L1g, L1b = e.L1b,
        L2x = e.L2x, L2y = e.L2y, L2z = e.L2z, L2r = e.L2r, L2g = e.L2g, L2b = e.L2b,
        FogR = e.FogR, FogG = e.FogG, FogB = e.FogB, FogNear = e.FogNear, FogFar = e.FogFar,
    };

    private static Rom.EnvLight FromDto(EnvDto d) => new()
    {
        AmbR = d.AmbR, AmbG = d.AmbG, AmbB = d.AmbB,
        L1x = d.L1x, L1y = d.L1y, L1z = d.L1z, L1r = d.L1r, L1g = d.L1g, L1b = d.L1b,
        L2x = d.L2x, L2y = d.L2y, L2z = d.L2z, L2r = d.L2r, L2g = d.L2g, L2b = d.L2b,
        FogR = d.FogR, FogG = d.FogG, FogB = d.FogB, FogNear = d.FogNear, FogFar = d.FogFar,
    };

    // ── Load ────────────────────────────────────────────────────────────────

    public static void Load(MapDocument doc, string path) => Deserialize(doc, File.ReadAllText(path));

    /// <summary>Replaces the document's scenes from a JSON string (used for load and undo).</summary>
    public static void Deserialize(MapDocument doc, string json)
    {
        var dto = JsonSerializer.Deserialize<ProjectDto>(json, Opts)
                  ?? throw new InvalidDataException("Empty or invalid project data.");

        // New multi-scene format, or fall back to the legacy single-scene fields.
        var sceneDtos = dto.Scenes is { Count: > 0 } s
            ? s
            : [new SceneDto { Name = dto.Name, Settings = dto.Scene, ActiveRoom = dto.ActiveRoom, Rooms = dto.Rooms }];

        var scenes = sceneDtos.Select(SceneFromDto).ToList();
        doc.RebuildScenes(scenes, dto.ActiveScene);
        doc.FlagNames.Clear();
        if (dto.FlagNames != null) foreach (var kv in dto.FlagNames) doc.FlagNames[kv.Key] = kv.Value;
        doc.NotifyChanged();
    }

    private static ZScene SceneFromDto(SceneDto sd)
    {
        var scene = new ZScene(sd.Name ?? "Loaded Scene") { Settings = FromDto(sd.Settings) };
        scene.Rooms.Clear();
        for (int i = 0; i < (sd.Rooms?.Count ?? 0); i++)
        {
            var rd = sd.Rooms![i];
            var room = new ZRoom(rd.Name ?? $"Room {i}", i) { Settings = FromDto(rd.Settings) };
            foreach (var solidDto in rd.Solids ?? [])
                room.Geometry.Add(FromDto(solidDto));
            foreach (var ad in rd.Actors ?? [])
                room.Actors.Add(FromDto(ad));
            foreach (var dd in rd.Decals ?? [])
                room.Decals.Add(FromDto(dd));
            scene.Rooms.Add(room);
        }
        if (scene.Rooms.Count == 0) scene.AddRoom();
        scene.ActiveRoom = scene.Rooms[Math.Clamp(sd.ActiveRoom, 0, scene.Rooms.Count - 1)];
        scene.Environments = (sd.Environments ?? []).Select(FromDto).ToList();
        scene.Paths = (sd.Paths ?? []).Select(pd =>
        {
            var zp = new ZPath { Name = pd.Name ?? "Path", AdditionalPathIndex = pd.AddIdx, CustomValue = pd.Custom, Closed = pd.Closed };
            var a = pd.Pts ?? [];
            for (int i = 0; i + 2 < a.Length; i += 3) zp.Points.Add(new Vector3(a[i], a[i + 1], a[i + 2]));
            return zp;
        }).ToList();
        scene.PointLights = (sd.Lights ?? []).Select(L => new ScenePointLight
        {
            X = L.X, Y = L.Y, Z = L.Z, R = L.R, G = L.G, B = L.B, Radius = L.Radius, Glow = L.Glow,
        }).ToList();
        scene.Messages = (sd.Messages ?? []).Select(m => new MhMessage
        {
            Id = m.Id, Text = m.Text ?? "", BoxType = m.Box, YPos = m.Pos, Icon = m.Icon,
            Kind = (MhMsgKind)m.Kind, Choice1 = m.Choice1 ?? "Yes", Choice2 = m.Choice2 ?? "No",
            Outcome1 = FromDto(m.Out1), Outcome2 = FromDto(m.Out2),
            DoneFlag = m.DoneFlag, AfterMsgId = m.AfterMsgId, Sfx = m.Sfx, Gesture = m.Gesture,
        }).ToList();
        scene.Setups = (sd.Setups ?? []).Select(FromDto).ToList();
        scene.ActiveSetup = scene.Setups.Count > 0 ? Math.Clamp(sd.ActiveSetup, 0, scene.Setups.Count - 1) : 0;
        scene.CutsceneData = string.IsNullOrEmpty(sd.CutsceneB64) ? null : Convert.FromBase64String(sd.CutsceneB64);
        scene.CutsceneOrigOff = sd.CutsceneOff;
        return scene;
    }

    // ── Mapping: model → dto ─────────────────────────────────────────────────

    private static SceneSettingsDto ToDto(SceneSettings s) => new()
    {
        AreaName = s.AreaName,
        SkyboxId = s.SkyboxId, DrawConfig = s.DrawConfig, IndoorLighting = s.IndoorLighting, Cloudy = s.Cloudy, Sky = (int)s.Sky,
        Dungeon = s.Dungeon, DoorStyle = s.DoorStyle, BossDoorTheme = s.BossDoorTheme,
        MusicSeq = s.MusicSeq, MusicCrossGame = s.MusicCrossGame, NightSfx = s.NightSfx,
        SubdivX = s.SubdivX, SubdivY = s.SubdivY, SubdivZ = s.SubdivZ,
        WindX = s.WindX, WindY = s.WindY, WindZ = s.WindZ, WindSpeed = s.WindSpeed,
        StartWeekEvents = s.StartWeekEvents.Count > 0 ? new List<int>(s.StartWeekEvents) : null,
        PersistentWeekEvents = s.PersistentWeekEvents.Count > 0 ? new List<int>(s.PersistentWeekEvents) : null,
        TextureScrolls = s.TextureScrolls.Count > 0 ? new List<TextureScroll>(s.TextureScrolls) : null,
        PlaytestTimeOfDay = s.PlaytestTimeOfDay,
        SpawnX = s.SpawnPos.X, SpawnY = s.SpawnPos.Y, SpawnZ = s.SpawnPos.Z,
        SpawnYaw = s.SpawnYaw, SpawnRoom = s.SpawnRoom,
        Ambient = Pack(s.Ambient), Light1Col = Pack(s.Light1Col), Light2Col = Pack(s.Light2Col), FogColor = Pack(s.FogColor),
        L1dx = s.Light1DirX, L1dy = s.Light1DirY, L1dz = s.Light1DirZ,
        L2dx = s.Light2DirX, L2dy = s.Light2DirY, L2dz = s.Light2DirZ,
        FogNear = s.FogNear, FogFar = s.FogFar,
    };

    private static RoomSettingsDto ToDto(RoomSettings r) => new()
    {
        TimeOverride = r.TimeOverride, TimeSpeed = r.TimeSpeed, Echo = r.Echo,
        ShowInvisibleActors = r.ShowInvisibleActors, DisableSkybox = r.DisableSkybox,
        DisableSunMoon = r.DisableSunMoon, BehaviorType = r.BehaviorType,
    };

    private static RoomDto ToDto(ZRoom room) => new()
    {
        Name     = room.Name,
        Settings = ToDto(room.Settings),
        Solids   = room.Geometry.Select(ToDto).ToList(),
        Actors   = room.Actors.Select(ToDto).ToList(),
        Decals   = room.Decals.Select(ToDto).ToList(),
    };

    private static DecalDto ToDto(Decal d) => new()
    {
        Px = d.Position.X, Py = d.Position.Y, Pz = d.Position.Z,
        Nx = d.Normal.X, Ny = d.Normal.Y, Nz = d.Normal.Z,
        SizeU = d.SizeU, SizeV = d.SizeV, Rotation = d.Rotation, Texture = d.TextureName,
        GroupId = d.GroupId, VisGroupId = d.VisGroupId,
    };

    private static Decal FromDto(DecalDto d) => new()
    {
        Position = new Vector3(d.Px, d.Py, d.Pz), Normal = new Vector3(d.Nx, d.Ny, d.Nz),
        SizeU = d.SizeU <= 0 ? 48f : d.SizeU, SizeV = d.SizeV <= 0 ? 48f : d.SizeV, Rotation = d.Rotation,
        TextureName = d.Texture, GroupId = d.GroupId, VisGroupId = d.VisGroupId,
    };

    private static ActorDto ToDto(ZActor a) => new()
    {
        Number = a.Number, Variable = a.Variable, IdFlags = a.IdFlags,
        X = a.XPos, Y = a.YPos, Z = a.ZPos, Rx = a.XRot, Ry = a.YRot, Rz = a.ZRot,
        Name = a.Name,
        IsTransition = a.IsTransition,
        FrontRoom = a.FrontRoom, FrontEffect = a.FrontEffect, BackRoom = a.BackRoom, BackEffect = a.BackEffect,
        LockBack = a.LockBack,
        GroupId = a.GroupId, VisGroupId = a.VisGroupId, IsSelected = a.IsSelected,
        Schedule = a.Schedule,
        ScheduleVm = a.ScheduleVm, SchedulePoses = a.SchedulePoses,
    };

    private static ZActor FromDto(ActorDto ad) => new()
    {
        Number = (ushort)ad.Number, Variable = (ushort)ad.Variable, IdFlags = (ushort)ad.IdFlags,
        XPos = ad.X, YPos = ad.Y, ZPos = ad.Z,
        XRot = (short)ad.Rx, YRot = (short)ad.Ry, ZRot = (short)ad.Rz,
        Name = ad.Name,
        IsTransition = ad.IsTransition,
        FrontRoom = (byte)ad.FrontRoom, FrontEffect = (byte)ad.FrontEffect,
        BackRoom = (byte)ad.BackRoom, BackEffect = (byte)ad.BackEffect,
        LockBack = ad.LockBack,
        GroupId = ad.GroupId, VisGroupId = ad.VisGroupId, IsSelected = ad.IsSelected,
        Schedule = ad.Schedule,
        ScheduleVm = ad.ScheduleVm, SchedulePoses = ad.SchedulePoses,
    };

    private static SetupDto ToDto(ZSetup su) => new()
    {
        Name = su.Name,
        Layer = (int)su.Layer,
        Settings = ToDto(su.Settings),
        Environments = su.Environments.Select(ToDto).ToList(),
        RoomActors = su.RoomActors.Select(list => list.Select(ToDto).ToList()).ToList(),
    };

    private static ZSetup FromDto(SetupDto sd) => new()
    {
        Name = sd.Name ?? "Setup",
        Layer = (SetupLayer)sd.Layer,
        Settings = FromDto(sd.Settings),
        Environments = (sd.Environments ?? []).Select(FromDto).ToList(),
        RoomActors = (sd.RoomActors ?? []).Select(list => (list ?? []).Select(FromDto).ToList()).ToList(),
    };

    private static SolidDto ToDto(Solid s) => new()
    {
        IsTrigger = s.IsTrigger, ExitEntrance = s.ExitEntrance, IsWater = s.IsWater, WaterRoom = s.WaterRoom,
        NoCollision = s.NoCollision, SurfaceData0 = s.SurfaceData0, SurfaceData1 = s.SurfaceData1,
        GroupId = s.GroupId, VisGroupId = s.VisGroupId, IsSelected = s.IsSelected,
        Planes = s.Planes.Select(p => new PlaneDto { Nx = p.Normal.X, Ny = p.Normal.Y, Nz = p.Normal.Z, D = p.Distance }).ToList(),
        Faces  = s.Faces.Select(f => new FaceDto
        {
            PlaneIndex = f.PlaneIndex, Tex = f.TextureName, TexScale = f.TextureScale,
            TexScaleT = f.TexScaleT, TexShiftS = f.TexShiftS, TexShiftT = f.TexShiftT,
            TexRotation = f.TexRotation, AlignToFace = f.AlignToFace,
            // Explicit texture axes (texture lock) — persisted so a rotated/flipped mapping survives
            // save/load. Absent in older projects → left zero → lazily re-derived (identical to before).
            UAxis = f.UAxis != OpenTK.Mathematics.Vector3.Zero ? new[] { f.UAxis.X, f.UAxis.Y, f.UAxis.Z } : null,
            VAxis = f.VAxis != OpenTK.Mathematics.Vector3.Zero ? new[] { f.VAxis.X, f.VAxis.Y, f.VAxis.Z } : null,
            Color = Pack(f.Color),
            VColors = f.VertexColors?.Select(Pack).ToArray(),
            ShadeNu = f.ShadePaint?.Nu ?? 0,
            ShadeNv = f.ShadePaint?.Nv ?? 0,
            ShadeColors = f.ShadePaint?.Colors.Select(Pack).ToArray(),
        }).ToList(),
    };

    // ── Mapping: dto → model ─────────────────────────────────────────────────

    private static SceneSettings FromDto(SceneSettingsDto? d)
    {
        d ??= new SceneSettingsDto();
        return new SceneSettings
        {
            AreaName = d.AreaName ?? "",
            SkyboxId = d.SkyboxId, DrawConfig = d.DrawConfig, IndoorLighting = d.IndoorLighting, Cloudy = d.Cloudy, Sky = (SkyMode)d.Sky,
            Dungeon = d.Dungeon, DoorStyle = d.DoorStyle, BossDoorTheme = d.BossDoorTheme,
            MusicSeq = d.MusicSeq, MusicCrossGame = d.MusicCrossGame, NightSfx = d.NightSfx,
            WindX = d.WindX, WindY = d.WindY, WindZ = d.WindZ, WindSpeed = d.WindSpeed,
            StartWeekEvents = d.StartWeekEvents ?? [],
            PersistentWeekEvents = d.PersistentWeekEvents ?? [],
            TextureScrolls = d.TextureScrolls ?? [],
            PlaytestTimeOfDay = d.PlaytestTimeOfDay ?? 0x8000,   // old projects (null) default to noon
            SubdivX = d.SubdivX == 0 ? (byte)16 : d.SubdivX,
            SubdivY = d.SubdivY == 0 ? (byte)4  : d.SubdivY,
            SubdivZ = d.SubdivZ == 0 ? (byte)16 : d.SubdivZ,
            SpawnPos = new Vector3(d.SpawnX, d.SpawnY, d.SpawnZ),
            SpawnYaw = d.SpawnYaw, SpawnRoom = d.SpawnRoom,
            Ambient = Unpack(d.Ambient), Light1Col = Unpack(d.Light1Col),
            Light2Col = Unpack(d.Light2Col), FogColor = Unpack(d.FogColor),
            Light1DirX = d.L1dx, Light1DirY = d.L1dy, Light1DirZ = d.L1dz,
            Light2DirX = d.L2dx, Light2DirY = d.L2dy, Light2DirZ = d.L2dz,
            FogNear = d.FogNear, FogFar = d.FogFar,
        };
    }

    private static RoomSettings FromDto(RoomSettingsDto? d)
    {
        d ??= new RoomSettingsDto();
        return new RoomSettings
        {
            TimeOverride = d.TimeOverride, TimeSpeed = d.TimeSpeed, Echo = d.Echo,
            ShowInvisibleActors = d.ShowInvisibleActors, DisableSkybox = d.DisableSkybox,
            DisableSunMoon = d.DisableSunMoon, BehaviorType = d.BehaviorType,
        };
    }

    private static Solid FromDto(SolidDto sd)
    {
        var planes = (sd.Planes ?? [])
            .Select(p => new Plane3D(new Vector3(p.Nx, p.Ny, p.Nz), p.D)).ToArray();

        var solid = new Solid { IsTrigger = sd.IsTrigger, ExitEntrance = sd.ExitEntrance, IsWater = sd.IsWater, WaterRoom = sd.WaterRoom == 0 ? 0x3F : sd.WaterRoom, NoCollision = sd.NoCollision, SurfaceData0 = sd.SurfaceData0, SurfaceData1 = sd.SurfaceData1, GroupId = sd.GroupId, VisGroupId = sd.VisGroupId, IsSelected = sd.IsSelected };
        solid.RestorePlanes(planes);     // rebuilds faces (each tagged with PlaneIndex)

        if (sd.Faces != null)
        {
            var byPlane = sd.Faces.ToDictionary(f => f.PlaneIndex);
            foreach (var face in solid.Faces)
                if (byPlane.TryGetValue(face.PlaneIndex, out var fd))
                {
                    face.TextureName  = fd.Tex;
                    face.TexScaleS    = MathF.Abs(fd.TexScale)  < 1e-3f ? 64f : fd.TexScale;   // keep negative (mirror)
                    face.TexScaleT    = MathF.Abs(fd.TexScaleT) < 1e-3f ? face.TexScaleS : fd.TexScaleT;
                    face.TexShiftS    = fd.TexShiftS; face.TexShiftT = fd.TexShiftT;
                    face.TexRotation  = fd.TexRotation; face.AlignToFace = fd.AlignToFace;
                    if (fd.UAxis is { Length: 3 } ua && fd.VAxis is { Length: 3 } va)
                    {
                        face.UAxis = new OpenTK.Mathematics.Vector3(ua[0], ua[1], ua[2]);
                        face.VAxis = new OpenTK.Mathematics.Vector3(va[0], va[1], va[2]);
                    }
                    face.Color        = Unpack01(fd.Color);
                    face.VertexColors = fd.VColors != null && fd.VColors.Length == face.Vertices.Count
                        ? fd.VColors.Select(Unpack01).ToArray() : null;
                    if (fd.ShadeColors != null && fd.ShadeNu > 0 && fd.ShadeNv > 0
                        && fd.ShadeColors.Length == (fd.ShadeNu + 1) * (fd.ShadeNv + 1))
                        face.ShadePaint = new SolidFace.ShadeGrid
                        { Nu = fd.ShadeNu, Nv = fd.ShadeNv, Colors = fd.ShadeColors.Select(Unpack01).ToArray() };
                }
        }
        return solid;
    }

    // ── Colour packing ───────────────────────────────────────────────────────

    private static int Pack(RgbColor c) => (c.R << 16) | (c.G << 8) | c.B;
    private static int Pack(Vector3 c) =>
        ((int)(Math.Clamp(c.X, 0, 1) * 255) << 16) |
        ((int)(Math.Clamp(c.Y, 0, 1) * 255) << 8)  |
         (int)(Math.Clamp(c.Z, 0, 1) * 255);

    private static RgbColor Unpack(int v) => RgbColor.From((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
    private static Vector3 Unpack01(int v) => new(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);

    // ── DTOs ──────────────────────────────────────────────────────────────────

    public sealed class ProjectDto
    {
        public int Version { get; set; }
        // "oot" | "mm": the game this project targets, recorded for the recent-files colour coding.
        // Declared early so the recent menu can read it from just the first bytes of a (possibly large) file.
        public string? Game { get; set; }
        // Multi-scene projects: the full list + the active index.
        public List<SceneDto>? Scenes { get; set; }
        public int ActiveScene { get; set; }
        // Named flag channels ("Switch:5" → "GateA"); editor-only, never compiled.
        public Dictionary<string, string>? FlagNames { get; set; }
        // Legacy single-scene fields (still read for old .mhproj files).
        public string? Name { get; set; }
        public SceneSettingsDto? Scene { get; set; }
        public int ActiveRoom { get; set; }
        public List<RoomDto>? Rooms { get; set; }
    }

    public sealed class SceneDto
    {
        public string? Name { get; set; }
        public SceneSettingsDto? Settings { get; set; }
        public int ActiveRoom { get; set; }
        public List<RoomDto>? Rooms { get; set; }
        public List<EnvDto>? Environments { get; set; }
        public List<PathDto>? Paths { get; set; }
        public List<PointLightDto>? Lights { get; set; }
        public List<MessageDto>? Messages { get; set; }
        public List<SetupDto>? Setups { get; set; }
        public int ActiveSetup { get; set; }
        public string? CutsceneB64 { get; set; }   // retained 0x17 cutscene block
        public int CutsceneOff { get; set; }
    }

    public sealed class MessageDto
    {
        public int Id { get; set; }
        public string? Text { get; set; }
        public int Box { get; set; }
        public int Pos { get; set; }
        public int Icon { get; set; }
        // Dialogue extensions (omitted for plain legacy messages -> defaults keep them backward-compatible).
        public int Kind { get; set; }                    // 0 = Message, 1 = Prompt
        public string? Choice1 { get; set; }
        public string? Choice2 { get; set; }
        public OutcomeDto? Out1 { get; set; }
        public OutcomeDto? Out2 { get; set; }
        public int DoneFlag { get; set; } = -1;
        public int AfterMsgId { get; set; } = -1;
        public int Sfx { get; set; } = -1;
        public int Gesture { get; set; } = -1;
    }

    public sealed class OutcomeDto
    {
        public int NextMsgId { get; set; } = -1;
        public int FireFlag { get; set; } = -1;
        public int GiveItem { get; set; } = -1;
        public bool ChargeRupees { get; set; }
        public int RupeeCost { get; set; }
    }

    public sealed class SetupDto
    {
        public string? Name { get; set; }
        public int Layer { get; set; }
        public SceneSettingsDto? Settings { get; set; }
        public List<EnvDto>? Environments { get; set; }
        public List<List<ActorDto>>? RoomActors { get; set; }
    }

    public sealed class PathDto
    {
        public string? Name { get; set; }
        public byte AddIdx { get; set; }       // MM additionalPathIndex
        public short Custom { get; set; }       // MM customValue
        public bool Closed { get; set; }       // editor loop flag (draws closing segment)
        public float[]? Pts { get; set; }   // flattened x,y,z per waypoint
    }

    public sealed class PointLightDto
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public byte R { get; set; } = 255;
        public byte G { get; set; } = 255;
        public byte B { get; set; } = 255;
        public short Radius { get; set; } = 200;
        public bool Glow { get; set; } = true;
    }

    public sealed class EnvDto
    {
        public byte AmbR { get; set; } public byte AmbG { get; set; } public byte AmbB { get; set; }
        public sbyte L1x { get; set; } public sbyte L1y { get; set; } public sbyte L1z { get; set; }
        public byte L1r { get; set; } public byte L1g { get; set; } public byte L1b { get; set; }
        public sbyte L2x { get; set; } public sbyte L2y { get; set; } public sbyte L2z { get; set; }
        public byte L2r { get; set; } public byte L2g { get; set; } public byte L2b { get; set; }
        public byte FogR { get; set; } public byte FogG { get; set; } public byte FogB { get; set; }
        public ushort FogNear { get; set; } public ushort FogFar { get; set; }
    }

    public sealed class SceneSettingsDto
    {
        public string? AreaName { get; set; }
        public byte SkyboxId { get; set; }
        public byte DrawConfig { get; set; }
        public bool IndoorLighting { get; set; }
        public bool Cloudy { get; set; }
        public bool Dungeon { get; set; }
        public byte DoorStyle { get; set; }
        public byte BossDoorTheme { get; set; }
        public byte MusicSeq { get; set; }
        public bool MusicCrossGame { get; set; }
        public byte NightSfx { get; set; }
        public int Sky { get; set; } = (int)SkyMode.Day;
        public sbyte WindX { get; set; }
        public sbyte WindY { get; set; }
        public sbyte WindZ { get; set; }
        public byte WindSpeed { get; set; }
        public List<int>? StartWeekEvents { get; set; }
        public List<int>? PersistentWeekEvents { get; set; }
        public List<TextureScroll>? TextureScrolls { get; set; }
        public ushort? PlaytestTimeOfDay { get; set; }   // nullable: old projects default to noon (0x8000)
        public byte SubdivX { get; set; }
        public byte SubdivY { get; set; }
        public byte SubdivZ { get; set; }
        public float SpawnX { get; set; }
        public float SpawnY { get; set; }
        public float SpawnZ { get; set; }
        public short SpawnYaw { get; set; }
        public int SpawnRoom { get; set; }
        public int Ambient { get; set; }
        public int Light1Col { get; set; }
        public int Light2Col { get; set; }
        public int FogColor { get; set; }
        public sbyte L1dx { get; set; }
        public sbyte L1dy { get; set; }
        public sbyte L1dz { get; set; }
        public sbyte L2dx { get; set; }
        public sbyte L2dy { get; set; }
        public sbyte L2dz { get; set; }
        public ushort FogNear { get; set; }
        public ushort FogFar { get; set; }
    }

    public sealed class RoomSettingsDto
    {
        public ushort TimeOverride { get; set; } = 0xFFFF;
        public byte TimeSpeed { get; set; }
        public byte Echo { get; set; }
        public bool ShowInvisibleActors { get; set; }
        public bool DisableSkybox { get; set; }
        public bool DisableSunMoon { get; set; }
        public byte BehaviorType { get; set; }
    }

    public sealed class RoomDto
    {
        public string? Name { get; set; }
        public RoomSettingsDto? Settings { get; set; }
        public List<SolidDto>? Solids { get; set; }
        public List<ActorDto>? Actors { get; set; }
        public List<DecalDto>? Decals { get; set; }
    }

    public sealed class DecalDto
    {
        public float Px { get; set; } public float Py { get; set; } public float Pz { get; set; }
        public float Nx { get; set; } public float Ny { get; set; } public float Nz { get; set; }
        public float SizeU { get; set; } public float SizeV { get; set; } public float Rotation { get; set; }
        public string? Texture { get; set; }
        public int GroupId { get; set; } public int VisGroupId { get; set; }
    }

    public sealed class SolidDto
    {
        public bool IsTrigger { get; set; }
        public int ExitEntrance { get; set; } = -1;
        public bool IsWater { get; set; }
        public int WaterRoom { get; set; } = 0x3F;
        public bool NoCollision { get; set; }
        public uint SurfaceData0 { get; set; }
        public uint SurfaceData1 { get; set; }
        public int GroupId { get; set; }
        public int VisGroupId { get; set; }
        public bool IsSelected { get; set; }   // #30: persist so undo/redo restores selection
        public List<PlaneDto>? Planes { get; set; }
        public List<FaceDto>? Faces { get; set; }
    }

    public sealed class PlaneDto
    {
        public float Nx { get; set; }
        public float Ny { get; set; }
        public float Nz { get; set; }
        public float D { get; set; }
    }

    public sealed class FaceDto
    {
        public float TexScaleT { get; set; }
        public float TexShiftS { get; set; }
        public float TexShiftT { get; set; }
        public float TexRotation { get; set; }
        public bool  AlignToFace { get; set; }
        public float[]? UAxis { get; set; }   // explicit texture axes (texture lock); null = derive
        public float[]? VAxis { get; set; }
        public int PlaneIndex { get; set; }
        public string? Tex { get; set; }
        public float TexScale { get; set; }
        public int Color { get; set; }
        public int[]? VColors { get; set; }   // painted per-vertex shade (null = unpainted)
        public int ShadeNu { get; set; }      // shade-spray grid resolution (0 = none)
        public int ShadeNv { get; set; }
        public int[]? ShadeColors { get; set; }   // shade-spray grid node colours (row-major)
    }

    public sealed class ActorDto
    {
        public int Number { get; set; }
        public int Variable { get; set; }
        public int IdFlags { get; set; }   // MM spawn-condition / HALFDAYBIT bits (0xE000); 0 for OoT/old projects
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public int Rx { get; set; }
        public int Ry { get; set; }
        public int Rz { get; set; }
        public int GroupId { get; set; }
        public int VisGroupId { get; set; }
        public bool IsSelected { get; set; }   // #30: persist so undo/redo restores selection
        public List<ScheduleRule>? Schedule { get; set; }
        public ScheduleProgram? ScheduleVm { get; set; }   // MM schedule bytecode VM program
        public List<SchedulePose>? SchedulePoses { get; set; }
        public string? Name { get; set; }
        // Transition-actor (scene 0x0E door) data; 0xFF side = scene exit (not a room).
        public bool IsTransition { get; set; }
        public int FrontRoom { get; set; } = 0xFF;
        public int FrontEffect { get; set; } = 0xFF;
        public int BackRoom { get; set; } = 0xFF;
        public int BackEffect { get; set; } = 0xFF;
        public bool LockBack { get; set; }
    }
}
