namespace MegatonHammer.Rom;

/// <summary>A collision exit-trigger region: the bounding box of the collision polygons that share
/// one exit index, plus the entrance it warps to.</summary>
/// <summary>A prerendered-room fixed camera (CAM_SET_PREREND_*): the position, rotation (binary
/// angles) and FOV (degrees) the room's JFIF background was captured from. FIXED rooms support one
/// angle; PIVOT rooms (0x1A) are fixed in position but free to yaw 360° (Market, house interiors).</summary>
public sealed class PrerenderCamera
{
    public int Setting;                  // 0x19 FIXED, 0x1A PIVOT, 0x1B SIDE_SCROLL
    public short PosX, PosY, PosZ;
    public short RotX, RotY, RotZ;       // binary angles (0..65535 = 0..360°): RotX pitch, RotY yaw
    public int Fov;                      // degrees
    public bool IsPivot => Setting == 0x1A;
}

public sealed class ImportedExit
{
    public int ExitIndex;            // 1-based index into the scene exit list
    public ushort EntranceIndex;     // destination entrance (gEntranceTable), 0 if unmapped
    public OpenTK.Mathematics.Vector3 Min, Max;
    public int PolyCount;
}

/// <summary>One actor/spawn placement parsed from a scene or room header.</summary>
public sealed class ImportedActor
{
    public ushort Id;            // real actor id (MM spawn-flag bits already masked off)
    public ushort IdFlags;       // MM only: the top-3-bit spawn-condition flags (0x2000/0x4000/0x8000)
    public short X, Y, Z;
    public short RX, RY, RZ;
    public ushort Params;
    // Transition-actor side links (0x0E entries only): the two rooms/cameras it bridges, 0xFF = exit.
    public byte FrontRoom = 0xFF, FrontEffect = 0xFF, BackRoom = 0xFF, BackEffect = 0xFF;
}

/// <summary>One room of an imported scene: its actors, object dependencies, and mesh pointer.</summary>
public sealed class ImportedRoom
{
    public int Index;
    public int FileIndex;
    public List<ImportedActor> Actors = [];
    public List<ushort> Objects = [];
    public int MeshHeaderOffset = -1;     // offset (in the room file) of the SetMesh header

    // Room-header settings (commands 0x08/0x10/0x12/0x16) — captured so a recompile keeps them
    // instead of resetting to editor defaults. Byte positions mirror RoomExporter exactly.
    public byte BehaviorType;             // 0x08
    public bool ShowInvisibleActors;      // 0x08
    public ushort TimeOverride = 0xFFFF;  // 0x10 (0xFFFF = inherit)
    public byte TimeSpeed;                // 0x10
    public bool DisableSkybox;            // 0x12
    public bool DisableSunMoon;           // 0x12
    public byte Echo;                     // 0x16
}

/// <summary>One scene path (0x0D): a waypoint polyline plus MM's two extra header fields
/// (additionalPathIndex / customValue), which OoT leaves as padding.</summary>
public sealed class ImportedPath
{
    public byte AdditionalPathIndex;
    public short CustomValue;
    public List<OpenTK.Mathematics.Vector3> Points = [];
}

/// <summary>An alternate scene setup (child/adult/cutscene header) — its own command list.</summary>
public sealed class ImportedSetup
{
    public string Name = "";
    public int HeaderOffset;              // offset (in the scene file) of this header's command list
    public List<ImportedActor> Spawns = [];
}

/// <summary>A scene loaded read-only from a ROM: structure, setups, rooms, and placements.</summary>
public sealed class ImportedScene
{
    public int SceneId;
    public string Name = "";
    public int SceneFileIndex;
    public List<ImportedRoom> Rooms = [];
    public List<ImportedActor> Spawns = [];     // start positions (default header)
    public List<ImportedActor> Transitions = [];// transition actors (0x0E) — doors / area loading planes
    public List<ImportedSetup> Setups = [];     // alternate headers (0x18)
    public byte SkyboxId;
    public byte DrawConfig;                      // scene-table draw config (per-frame animation: morph, mat-anim…)
    public int  AnimatedMaterialOffset = -1;     // MM scene cmd 0x1A: scene-file offset of the AnimatedMaterial[] (texture-anim list)
    public byte MusicSeq;                        // background sequence (0x15 command)
    public byte NightSfx;                        // ambient night SFX (0x15 command)
    public ushort KeepObjectId = 2;              // scene's special object @ seg 5 (0x07; field/dungeon keep)
    public byte AreaTextureIndex;                // MM only: skybox cmd data1 selects scene_texture_0N @ seg 6 (0=none)
    public List<PrerenderCamera> PrerenderCameras = [];   // CAM_SET_PREREND_* fixed cameras (for prerendered rooms)
    public List<ImportedExit> Exits = [];        // collision exit-trigger regions (walk-into warps)
    public List<EnvLight> Lights = [];
    public List<ImportedPath> Paths = [];        // path/waypoint lists (0x0D)
    public byte[]? CutsceneData;                 // raw cutscene block (0x17), retained verbatim for re-emit
    public int CutsceneOrigOff;                  // its original scene-file offset (for pointer relocation)
    public List<(short X, short Y, short Z, short XLen, short ZLen, int Room)> WaterBoxes = [];   // collision waterboxes

    internal int _altHeaderPtr = -1;            // scene-file offset of the 0x18 alt-header pointer list
}

/// <summary>Scene environment lighting entry (ambient + two directional lights + fog).</summary>
public sealed class EnvLight
{
    public byte AmbR, AmbG, AmbB;
    public sbyte L1x, L1y, L1z; public byte L1r, L1g, L1b;
    public sbyte L2x, L2y, L2z; public byte L2r, L2g, L2b;
    public byte FogR, FogG, FogB;
    public ushort FogNear, FogFar;
}

/// <summary>
/// Reads an existing scene + its rooms from an OoT ROM into a structured, read-only model:
/// the scene header (room list, start positions, alternate-header setups, lighting, skybox)
/// and each room header (actor list, object list, mesh pointer). The geometry mesh is
/// decoded separately by <see cref="RoomMeshReader"/>. Pure parsing — never writes the ROM.
/// </summary>
public static class SceneImporter
{
    // Scene/room header command ids.
    private const byte CmdSpawnList   = 0x00;
    private const byte CmdActorList   = 0x01;
    private const byte CmdCollision   = 0x03;   // collision header (vertices, polys, surface types)
    private const byte CmdRoomList     = 0x04;
    private const byte CmdExitList     = 0x13;   // exit list: maps surface exit indices to entrances
    private const byte CmdObjectList  = 0x0B;
    private const byte CmdMesh        = 0x0A;
    private const byte CmdSpecialObjs = 0x07;   // special objects: the keep object loaded at segment 5
    private const byte CmdTransition  = 0x0E;   // transition-actor list (doors / area loading planes)
    private const byte CmdLighting    = 0x0F;
    private const byte CmdSkybox      = 0x11;
    private const byte CmdSound       = 0x15;   // sound/music settings (music seq + night SFX)
    private const byte CmdAltHeaders  = 0x18;
    private const byte CmdPathList    = 0x0D;   // path/waypoint lists (moving platforms, NPC routes)
    private const byte CmdCutscene    = 0x17;   // cutscene script data
    private const byte CmdEnd         = 0x14;

    /// <summary>Imports scene <paramref name="sceneId"/> from an OoT or MM ROM, or null if not found.</summary>
    public static ImportedScene? Import(RomImage rom, int sceneId)
    {
        bool mm = rom.Game == RomGame.MM;
        if (rom.Game == RomGame.Unknown) return null;
        if (mm ? !MmSceneFiles.IsValid(sceneId) : !OotSceneFiles.IsValid(sceneId)) return null;

        // Scene-table entry size differs: OoT 0x14 (with title-card RomFile), MM 0x10.
        int entrySize = mm ? 0x10 : SceneTableLocator.EntrySize;

        // Locate the scene table and read this scene's file VROM.
        byte[]? code = null; int tableOff = -1;
        foreach (var f in rom.Files)
        {
            if (!f.Exists || f.Size < entrySize * 14) continue;
            var bytes = rom.GetFile(f.Index);
            var loc = mm ? SceneTableLocator.FindMM(bytes, rom.Files, rom) : SceneTableLocator.Find(bytes, rom.Files);
            if (loc.Offset >= 0) { code = bytes; tableOff = loc.Offset; break; }
        }
        if (code == null) return null;

        int o = tableOff + sceneId * entrySize;
        if (o + 8 > code.Length) return null;
        uint sceneVrom = U32(code, o);
        if (sceneVrom == 0) return null;

        var fileByVrom = new Dictionary<uint, int>();
        foreach (var f in rom.Files) if (f.Exists) fileByVrom[f.VromStart] = f.Index;
        if (!fileByVrom.TryGetValue(sceneVrom, out int sceneIdx)) return null;

        var scene = new ImportedScene
        {
            SceneId = sceneId,
            Name = mm ? MmSceneFiles.Pretty(sceneId) : OotSceneNames.Pretty(sceneId),
            SceneFileIndex = sceneIdx,
            // Scene-table draw config (per-frame scene animation): OoT entry+0x11, MM entry+0x0B.
            DrawConfig = o + (mm ? 0x0B : 0x11) < code.Length ? code[o + (mm ? 0x0B : 0x11)] : (byte)0,
        };

        byte[] sceneData = rom.GetFile(sceneIdx);
        ParseSceneHeader(scene, sceneData, 0, rom, fileByVrom, isAlternate: false);

        // Alternate-header setups (child/adult/cutscene) — each is another command list.
        ParseAltHeaders(scene, sceneData, rom, fileByVrom);
        return scene;
    }

    private static void ParseSceneHeader(ImportedScene scene, byte[] d, int start, RomImage rom,
                                         Dictionary<uint, int> fileByVrom, bool isAlternate)
    {
        var roomVroms = new List<uint>();
        int altPtr = -1;
        int colOff = -1, exitOff = -1, pathOff = -1;

        for (int p = start; p + 8 <= d.Length; p += 8)
        {
            byte cmd = d[p];
            if (cmd == CmdEnd) break;
            int seg = SegOffset(d, p + 4, out int off);

            switch (cmd)
            {
                case CmdCollision:  if (seg == 2) colOff  = off; break;
                case CmdExitList:   if (seg == 2) exitOff = off; break;
                case CmdPathList:   if (seg == 2) pathOff = off; break;

                case CmdCutscene:
                    // Retain the cutscene script verbatim (it's a scripted command stream we don't
                    // re-synthesize). It sits at the end of the scene file, so capture to the end.
                    if (seg == 2 && off > 0 && off < d.Length && !isAlternate)
                    {
                        scene.CutsceneOrigOff = off;
                        scene.CutsceneData = d[off..];
                    }
                    break;

                case CmdRoomList:
                    int count = d[p + 1];
                    if (seg == 2)
                        for (int i = 0; i < count; i++)
                        {
                            int e = off + i * 8;
                            if (e + 4 <= d.Length) roomVroms.Add(U32(d, e));
                        }
                    break;

                case CmdSpawnList:
                    // Start positions for this header (the count comes from the spawn-list usage;
                    // read contiguous 16-byte entries until they stop looking valid).
                    if (seg == 2) ReadActorArray(d, off, 0, scene.Spawns, capByPlausibility: true, mm: rom.Game == RomGame.MM);
                    break;

                case CmdSpecialObjs:
                    // SCmdSpecialFiles: keepObjectId is a u32 at offset 0x04 — the object at segment 5.
                    scene.KeepObjectId = (ushort)U32(d, p + 4);
                    break;

                case CmdSkybox:
                    // SCmdSkyboxSettings: skyboxId is at offset 0x04 (cmd, 0, 0, 0, skyboxId, config, ...).
                    scene.SkyboxId = d[p + 4];
                    // MM: data1 (offset 0x01) selects a shared area-texture file (scene_texture_01..08)
                    // that the room geometry references at segment 6 — needed so MM interiors/regions
                    // (Stock Pot Inn, Clock Town houses, etc.) resolve their wall textures.
                    if (rom.Game == RomGame.MM) scene.AreaTextureIndex = d[p + 1];
                    break;

                case 0x1A:
                    // MM SCmdTextureAnimations: the data word (offset 0x04) is a segment-2 pointer to the
                    // scene's AnimatedMaterial[] list (texture scroll / cycle / colour). Capture its offset.
                    if (rom.Game == RomGame.MM && (U32(d, p + 4) >> 24) == 0x02)
                        scene.AnimatedMaterialOffset = (int)(U32(d, p + 4) & 0xFFFFFF);
                    break;

                case CmdSound:
                    // 0x15: cmd, reverb, 00, 00, 00, 00, nightSfxId, musicSeqId
                    scene.NightSfx = d[p + 6];
                    scene.MusicSeq = d[p + 7];
                    break;

                case CmdTransition:
                    if (seg == 2) ReadTransitionArray(d, off, d[p + 1], scene.Transitions, rom.Game == RomGame.MM);
                    break;

                case CmdLighting:
                    int n = d[p + 1];
                    if (seg == 2) ReadLights(d, off, n, scene.Lights);
                    break;

                case CmdAltHeaders:
                    if (seg == 2) altPtr = off;
                    break;
            }
        }

        // Only the primary header populates rooms (alternates share the same room files).
        if (!isAlternate)
        {
            for (int i = 0; i < roomVroms.Count; i++)
                if (fileByVrom.TryGetValue(roomVroms[i], out int rfi))
                    scene.Rooms.Add(ParseRoom(rom, rfi, i));

            if (colOff >= 0) ParseExits(scene, d, colOff, exitOff);
            if (colOff >= 0) ParseWaterBoxes(scene, d, colOff);
            if (colOff >= 0) ParsePrerenderCameras(scene, d, colOff);
            if (pathOff >= 0) ParsePaths(scene, d, pathOff);
        }

        if (altPtr >= 0) scene._altHeaderPtr = altPtr;
    }

    // Reads the collision header, finds polygons whose surface type carries an exit index, and
    // groups them into per-exit bounding regions (the walk-into warp trigger zones). The exit list
    // maps each 1-based exit index to a destination entrance.
    private static void ParseExits(ImportedScene scene, byte[] d, int colOff, int exitOff)
    {
        if (colOff + 0x2C > d.Length) return;
        int numPolys = U16(d, colOff + 0x14);
        int vtxOff   = Seg2(d, colOff + 0x10);
        int polyOff  = Seg2(d, colOff + 0x18);
        int surfOff  = Seg2(d, colOff + 0x1C);
        if (numPolys <= 0 || numPolys > 0x4000 || vtxOff < 0 || polyOff < 0 || surfOff < 0) return;

        var byExit = new Dictionary<int, ImportedExit>();
        for (int i = 0; i < numPolys; i++)
        {
            int p = polyOff + i * 0x10;
            if (p + 0x10 > d.Length) break;
            int typeIdx = U16(d, p);
            int sOff = surfOff + typeIdx * 8;
            if (sOff + 4 > d.Length) continue;
            int exitIndex = (int)((U32(d, sOff) >> 8) & 0x1F);
            if (exitIndex == 0) continue;

            if (!byExit.TryGetValue(exitIndex, out var ex))
            {
                ushort entrance = 0;
                if (exitOff >= 0)
                {
                    int e = exitOff + (exitIndex - 1) * 2;     // exit list is 1-indexed
                    if (e + 2 <= d.Length) entrance = U16(d, e);
                }
                ex = new ImportedExit
                {
                    ExitIndex = exitIndex, EntranceIndex = entrance,
                    Min = new(float.MaxValue, float.MaxValue, float.MaxValue),
                    Max = new(float.MinValue, float.MinValue, float.MinValue),
                };
                byExit[exitIndex] = ex;
            }
            ex.PolyCount++;
            for (int v = 0; v < 3; v++)
            {
                int vi = U16(d, p + 2 + v * 2) & 0x1FFF;
                int ve = vtxOff + vi * 6;
                if (ve + 6 > d.Length) continue;
                var pt = new OpenTK.Mathematics.Vector3((short)U16(d, ve), (short)U16(d, ve + 2), (short)U16(d, ve + 4));
                ex.Min = OpenTK.Mathematics.Vector3.ComponentMin(ex.Min, pt);
                ex.Max = OpenTK.Mathematics.Vector3.ComponentMax(ex.Max, pt);
            }
        }
        scene.Exits.AddRange(byExit.Values.OrderBy(e => e.ExitIndex));
    }

    // Segment-2 pointer → offset within the scene file, or -1 for other segments.
    private static int Seg2(byte[] d, int o)
    {
        if (o + 4 > d.Length) return -1;
        uint v = U32(d, o);
        return (v >> 24) == 2 ? (int)(v & 0x00FFFFFF) : -1;
    }

    private static void ParseAltHeaders(ImportedScene scene, byte[] d, RomImage rom, Dictionary<uint, int> fileByVrom)
    {
        if (scene._altHeaderPtr < 0) return;
        // The 0x18 command points at a variable-length array of header pointers indexed by
        // SceneLayer: 0-3 = child/adult × day/night, 4+ = cutscene layers (z64save.h SceneLayer).
        string[] names = ["Child Day", "Child Night", "Adult Day", "Adult Night"];
        const int MaxLayers = 20;           // generous bound; stops at the first non-seg-2 entry
        for (int i = 0; i < MaxLayers; i++)
        {
            int e = scene._altHeaderPtr + i * 4;
            if (e + 4 > d.Length) break;
            uint raw = U32(d, e);
            if (raw == 0) continue;          // a null layer (e.g. no adult-night variant) — keep scanning
            int seg = (int)(raw >> 24), hoff = (int)(raw & 0x00FFFFFF);
            if (seg != 2) break;             // end of the pointer list
            if (hoff <= 0 || hoff >= d.Length) continue;

            var setup = new ImportedSetup
            {
                HeaderOffset = hoff,
                Name = i < names.Length ? names[i] : $"Cutscene Setup {i - 3}",
            };
            ReadHeaderSpawns(d, hoff, setup.Spawns, rom.Game == RomGame.MM);
            scene.Setups.Add(setup);
        }
    }

    private static ImportedRoom ParseRoom(RomImage rom, int fileIndex, int roomIndex)
    {
        var room = new ImportedRoom { Index = roomIndex, FileIndex = fileIndex };
        byte[] d; try { d = rom.GetFile(fileIndex); } catch { return room; }

        for (int p = 0; p + 8 <= d.Length; p += 8)
        {
            byte cmd = d[p];
            if (cmd == CmdEnd) break;
            int seg = SegOffset(d, p + 4, out int off);

            switch (cmd)
            {
                case CmdActorList:
                    int count = d[p + 1];
                    if (seg == 3) ReadActorArray(d, off, count, room.Actors, capByPlausibility: false, mm: rom.Game == RomGame.MM);
                    break;
                case CmdObjectList:
                    int oc = d[p + 1];
                    if (seg == 3)
                        for (int i = 0; i < oc; i++)
                        {
                            int e = off + i * 2;
                            if (e + 2 <= d.Length) room.Objects.Add(U16(d, e));
                        }
                    break;
                case CmdMesh:
                    if (seg == 3) room.MeshHeaderOffset = off;
                    break;

                // Room-header settings (inline commands) — positions mirror RoomExporter so they
                // round-trip. 0x08 behaviour [08 type 00 00|00 00 showInvis 00];
                // 0x10 time [10 00 00 00|timeHi timeLo speed 00]; 0x12 skybox-mod
                // [12 00 00 00|disSky disSunMoon 00 00]; 0x16 echo [16 00 00 00|00 00 00 echo].
                case 0x08:
                    room.BehaviorType = d[p + 1];
                    room.ShowInvisibleActors = d[p + 6] != 0;
                    break;
                case 0x10:
                    room.TimeOverride = U16(d, p + 4);
                    room.TimeSpeed = d[p + 6];
                    break;
                case 0x12:
                    room.DisableSkybox = d[p + 4] != 0;
                    room.DisableSunMoon = d[p + 5] != 0;
                    break;
                case 0x16:
                    room.Echo = d[p + 7];
                    break;
            }
        }
        return room;
    }

    // Reads spawn placements referenced by a header's 0x00 command (used for alternate setups).
    private static void ReadHeaderSpawns(byte[] d, int headerOff, List<ImportedActor> outList, bool mm)
    {
        for (int p = headerOff; p + 8 <= d.Length; p += 8)
        {
            byte cmd = d[p];
            if (cmd == CmdEnd) break;
            if (cmd == CmdSpawnList)
            {
                int seg = SegOffset(d, p + 4, out int off);
                if (seg == 2) ReadActorArray(d, off, 0, outList, capByPlausibility: true, mm: mm);
                return;
            }
        }
    }

    // Reads an array of 16-byte ActorEntry records. If count==0, reads until entries stop
    // looking like plausible placements (used for spawn lists whose count isn't in the command).
    // MM packs spawn-condition flags in the top 3 bits of the id field (0x8000/0x4000/0x2000);
    // the real actor id is id & 0x1FFF (mm-main z_actor.c). OoT ids are used verbatim.
    private static void ReadActorArray(byte[] d, int off, int count, List<ImportedActor> outList,
                                       bool capByPlausibility, bool mm)
    {
        int max = count > 0 ? count : 64;
        for (int i = 0; i < max; i++)
        {
            int e = off + i * 16;
            if (e + 16 > d.Length) break;
            ushort rawId = U16(d, e);
            var a = new ImportedActor
            {
                Id      = (ushort)(mm ? rawId & 0x1FFF : rawId),
                IdFlags = (ushort)(mm ? rawId & 0xE000 : 0),
                X       = (short)U16(d, e + 2),
                Y       = (short)U16(d, e + 4),
                Z       = (short)U16(d, e + 6),
                RX      = (short)U16(d, e + 8),
                RY      = (short)U16(d, e + 10),
                RZ      = (short)U16(d, e + 12),
                Params  = U16(d, e + 14),
            };
            // Spawn (start-position) lists have no count in the command and are always Player
            // (actor id 0) — stop at the first non-Player entry rather than reading into garbage.
            if (capByPlausibility && count == 0 && a.Id != 0x0000) break;
            outList.Add(a);
        }
    }

    // Reads an array of 16-byte TransitionActorEntry records (the area/room loading-plane actors).
    // Layout: sides[2] (4 bytes), id (s16 @4), pos (3×s16 @6), rotY (s16 @C), params (s16 @E).
    private static void ReadTransitionArray(byte[] d, int off, int count, List<ImportedActor> outList, bool mm)
    {
        for (int i = 0; i < count; i++)
        {
            int e = off + i * 16;
            if (e + 16 > d.Length) break;
            ushort rawId = U16(d, e + 4);
            outList.Add(new ImportedActor
            {
                // TransitionActorEntry: { {room,effect}×2 sides, id, posX/Y/Z, rotY, params }.
                FrontRoom = d[e + 0], FrontEffect = d[e + 1], BackRoom = d[e + 2], BackEffect = d[e + 3],
                Id      = (ushort)(mm ? rawId & 0x1FFF : rawId),
                IdFlags = (ushort)(mm ? rawId & 0xE000 : 0),
                X       = (short)U16(d, e + 6),
                Y       = (short)U16(d, e + 8),
                Z       = (short)U16(d, e + 10),
                RY      = (short)U16(d, e + 12),
                Params  = U16(d, e + 14),
            });
        }
    }

    // Reads the collision header's WaterBox list (numWaterBoxes @ +0x24, list ptr @ +0x28). Each
    // WaterBox is {s16 xMin, ySurface, zMin, xLength, zLength; u32 properties} (room in bits 13–18).
    private static void ParseWaterBoxes(ImportedScene scene, byte[] d, int colOff)
    {
        if (colOff + 0x2C > d.Length) return;
        int num = U16(d, colOff + 0x24);
        int wbOff = Seg2(d, colOff + 0x28);
        if (num <= 0 || num > 256 || wbOff < 0) return;
        for (int i = 0; i < num; i++)
        {
            int e = wbOff + i * 16;
            if (e + 16 > d.Length) break;
            uint props = U32(d, e + 12);
            scene.WaterBoxes.Add(((short)U16(d, e), (short)U16(d, e + 2), (short)U16(d, e + 4),
                                  (short)U16(d, e + 6), (short)U16(d, e + 8), (int)((props >> 13) & 0x3F)));
        }
    }

    // Reads the collision header's bgCamList (ptr @ +0x20) and keeps the prerendered-room fixed
    // cameras (CAM_SET_PREREND_FIXED 0x19 / _PIVOT 0x1A / _SIDE_SCROLL 0x1B). Each BgCamInfo is
    // {u16 setting, s16 count, Vec3s* funcData}; the funcData is a BgCamFuncData
    // {Vec3s pos, Vec3s rot, s16 fov, ...}. The list length isn't stored, so read until an entry
    // stops looking like a valid BgCamInfo.
    private static void ParsePrerenderCameras(ImportedScene scene, byte[] d, int colOff)
    {
        if (colOff + 0x24 > d.Length) return;
        int listOff = Seg2(d, colOff + 0x20);
        if (listOff < 0) return;
        for (int i = 0; i < 64; i++)
        {
            int e = listOff + i * 8;
            if (e + 8 > d.Length) break;
            int setting = U16(d, e);
            if (setting > 0x40) break;                       // past the end of the list
            int funcPtr = Seg2(d, e + 4);
            bool isPrerender = setting == 0x19 || setting == 0x1A || setting == 0x1B;
            if (isPrerender && funcPtr >= 0 && funcPtr + 0x0E <= d.Length)
            {
                int fovRaw = (short)U16(d, funcPtr + 0x0C);
                int fov = fovRaw == -1 ? 60 : fovRaw > 360 ? fovRaw / 100 : fovRaw;
                scene.PrerenderCameras.Add(new PrerenderCamera
                {
                    Setting = setting,
                    PosX = (short)U16(d, funcPtr), PosY = (short)U16(d, funcPtr + 2), PosZ = (short)U16(d, funcPtr + 4),
                    RotX = (short)U16(d, funcPtr + 6), RotY = (short)U16(d, funcPtr + 8), RotZ = (short)U16(d, funcPtr + 10),
                    Fov = fov,
                });
            }
        }
    }

    // Reads the 0x0D path list — an array of {u8 count, pad[3], Vec3s* points} headers. The array
    // length isn't stored (actors index paths by number), so it ends at the first invalid header.
    // Each path is a polyline of waypoints used by moving platforms and time/action-driven NPC routes.
    private static void ParsePaths(ImportedScene scene, byte[] d, int pathListOff)
    {
        for (int i = 0; i < 64; i++)
        {
            int h = pathListOff + i * 8;
            if (h + 8 > d.Length) break;
            int count = d[h];
            int ptr = Seg2(d, h + 4);
            if (count <= 0 || ptr < 0 || ptr + count * 6 > d.Length) break;
            // MM uses bytes +1/+2 as additionalPathIndex/customValue (OoT pads them).
            var ip = new ImportedPath { AdditionalPathIndex = d[h + 1], CustomValue = (short)U16(d, h + 2) };
            for (int j = 0; j < count; j++)
            {
                int e = ptr + j * 6;
                ip.Points.Add(new OpenTK.Mathematics.Vector3((short)U16(d, e), (short)U16(d, e + 2), (short)U16(d, e + 4)));
            }
            scene.Paths.Add(ip);
        }
    }

    private static void ReadLights(byte[] d, int off, int count, List<EnvLight> outList)
    {
        for (int i = 0; i < count; i++)
        {
            int e = off + i * 22;
            if (e + 22 > d.Length) break;
            outList.Add(new EnvLight
            {
                AmbR = d[e], AmbG = d[e + 1], AmbB = d[e + 2],
                L1x = (sbyte)d[e + 3], L1y = (sbyte)d[e + 4], L1z = (sbyte)d[e + 5],
                L1r = d[e + 6], L1g = d[e + 7], L1b = d[e + 8],
                L2x = (sbyte)d[e + 9], L2y = (sbyte)d[e + 10], L2z = (sbyte)d[e + 11],
                L2r = d[e + 12], L2g = d[e + 13], L2b = d[e + 14],
                FogR = d[e + 15], FogG = d[e + 16], FogB = d[e + 17],
                FogNear = U16(d, e + 18), FogFar = U16(d, e + 20),
            });
        }
    }

    // Segmented pointer at d[o..o+4] → (segment, offset-within-segment).
    private static int SegOffset(byte[] d, int o, out int off)
    {
        uint v = U32(d, o);
        off = (int)(v & 0x00FFFFFF);
        return (int)(v >> 24);
    }

    private static uint U32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    private static ushort U16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);
}
