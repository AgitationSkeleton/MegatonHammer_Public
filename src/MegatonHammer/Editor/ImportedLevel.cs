using MegatonHammer.Rom;

namespace MegatonHammer.Editor;

/// <summary>
/// A level loaded read-only from a ROM for viewing/reference: the parsed scene structure
/// plus decoded world geometry per room. The original ROM is never modified — edits and
/// "Save As" always produce a new ROM (see <see cref="RomSafety"/>). Held on the document
/// as a backdrop alongside the editable brush scene.
/// </summary>
public sealed class ImportedLevel
{
    public ImportedScene Scene { get; }
    public RomImage Rom { get; }

    /// <summary>Decoded triangles per room (index matches <see cref="ImportedScene.Rooms"/>).</summary>
    public IReadOnlyList<List<MeshTri>> RoomMeshes { get; }

    /// <summary>Decoded prerendered background image per room (null when the room isn't a
    /// ROOM_SHAPE_TYPE_IMAGE prerender — Market, ToT exterior, house interiors, shops…).</summary>
    public System.Drawing.Bitmap?[] RoomBackgrounds { get; }

    /// <summary>Visibility per room index (multi-room dungeon toggles, D14). Default all visible.</summary>
    public bool[] RoomVisible { get; }

    /// <summary>Animated CPU segment (0x08–0x0D) → UV scroll per second for this scene (water/lava/effects).
    /// Triangles drawn under such a segment carry it in <see cref="MeshTri.AnimSeg"/>; the renderer scrolls
    /// their UVs over time. Empty when the scene has no animated textures.</summary>
    public IReadOnlyDictionary<int, OpenTK.Mathematics.Vector2> SegScroll { get; }

    /// <summary>A flipbook (texture-cycle) animation per animated segment: the frame textures, the
    /// per-keyframe index into them, and the playback rate. Tris tagged with that segment swap texture.</summary>
    public sealed record Flipbook(MegatonHammer.Rom.RomTexInfo[] Frames, byte[] Indices, float Fps);
    public IReadOnlyDictionary<int, Flipbook> SegFlip { get; }

    /// <summary>A colour-cycle per animated segment (MM AnimatedMaterial 2/3/4): tris tagged with that
    /// segment have their colour modulated by the cycling prim colour over time.</summary>
    public IReadOnlyDictionary<int, MegatonHammer.Rom.SceneTexAnim.ColorCycle> SegColor { get; }

    // Lazily-built actor→model resolver (D5), shared by the renderer. The scene's keep object id
    // lets the resolver bind segment 5 so field props (grass, rocks, bushes…) render textured.
    private ActorModelResolver? _resolver;
    public ActorModelResolver Resolver => _resolver ??= new ActorModelResolver(Rom, Scene.KeepObjectId);

    // Lazily-built dialogue reader (D8): decodes the ROM's message table so imported message/NPC actors show
    // their real text in the entity dialog. Null when the ROM has no locatable table (e.g. MM, for now).
    private MegatonHammer.Rom.RomMessageReader? _romMessages;
    private bool _romMessagesTried;
    public MegatonHammer.Rom.RomMessageReader? RomMessages
    {
        get { if (!_romMessagesTried) { _romMessagesTried = true; _romMessages = MegatonHammer.Rom.RomMessageReader.Build(Rom); } return _romMessages; }
    }

    private ImportedLevel(ImportedScene scene, RomImage rom, List<List<MeshTri>> meshes,
                          System.Drawing.Bitmap?[] backgrounds, IReadOnlyDictionary<int, OpenTK.Mathematics.Vector2> segScroll,
                          IReadOnlyDictionary<int, Flipbook> segFlip,
                          IReadOnlyDictionary<int, MegatonHammer.Rom.SceneTexAnim.ColorCycle> segColor)
    {
        Scene = scene;
        Rom = rom;
        RoomMeshes = meshes;
        RoomBackgrounds = backgrounds;
        SegScroll = segScroll;
        SegFlip = segFlip;
        SegColor = segColor;
        RoomVisible = new bool[meshes.Count];
        Array.Fill(RoomVisible, true);
    }

    /// <summary>Imports scene <paramref name="sceneId"/> (geometry + structure), or null if unavailable.</summary>
    public static ImportedLevel? Load(RomImage rom, int sceneId)
    {
        var scene = SceneImporter.Import(rom, sceneId);
        if (scene == null) return null;

        // Resolve the keep objects so room textures referencing segments 4/5 decode correctly:
        //   seg 4 = gameplay_keep (shared), seg 5 = this scene's field/dungeon keep (0x07 command).
        var objects = MegatonHammer.Rom.ObjectTable.Build(rom);
        int keep4 = KeepFileIndex(rom, objects, objects.IdOf("object_gameplay_keep") ?? 1);
        int keep5 = KeepFileIndex(rom, objects, scene.KeepObjectId);

        // MM: the skybox command's data1 selects one of 8 shared "area texture" files
        // (scene_texture_01..08, dmadata indices 1114..1121 in US rev1) that the room geometry
        // binds at segment 6. Without it MM interiors/regions render their walls untextured (gray).
        int keep6 = -1;
        if (rom.Game == MegatonHammer.Rom.RomGame.MM && scene.AreaTextureIndex is >= 1 and <= 8)
            keep6 = SceneTextureFileIndex + (scene.AreaTextureIndex - 1);

        var segScroll = MegatonHammer.Rom.SceneTexAnim.Build(rom, scene);       // animated water/lava/effect segments (scroll)
        var segColor = MegatonHammer.Rom.SceneTexAnim.BuildColor(rom, scene);   // colour-cycle segments
        var segFlipRaw = MegatonHammer.Rom.SceneTexAnim.BuildFlip(rom, scene);  // flipbook (texture-cycle) segments
        var flipBuilt = new Dictionary<int, (MegatonHammer.Rom.RomTexInfo[] frames, byte[] indices, float fps)>();
        // Segments tagged via gsSPDisplayList (scroll + colour cycle) — the detection set for the reader.
        var animSegSet = new HashSet<int>(segScroll.Keys); animSegSet.UnionWith(segColor.Keys);

        var meshes = new List<List<MeshTri>>(scene.Rooms.Count);
        var backgrounds = new System.Drawing.Bitmap?[scene.Rooms.Count];
        byte[] sceneFile = rom.GetFile(scene.SceneFileIndex);
        for (int i = 0; i < scene.Rooms.Count; i++)
        {
            var room = scene.Rooms[i];
            var light = PickDaytimeLight(scene.Lights);
            meshes.Add(RoomMeshReader.Read(rom, scene.SceneFileIndex, room, keep4, keep5, keep6, light, animSegSet, segFlipRaw, flipBuilt));
            // Prerendered (type-1) rooms: decode the JFIF background so the editor can show what the
            // room actually looks like instead of just its sparse floor geometry.
            try
            {
                byte[] roomFile = rom.GetFile(room.FileIndex);
                byte[] Seg(int s) => s == 2 ? sceneFile : s == 3 ? roomFile : [];
                var bgs = MegatonHammer.Rom.PrerenderBackground.FromRoom(roomFile, room.MeshHeaderOffset, Seg);
                if (bgs.Count > 0) backgrounds[i] = bgs[0].Decode();
            }
            catch { /* leave null */ }
        }

        // "Reflective" OoT scenes (Water Temple, Lake Hylia) apply their scroll segments to OPAQUE surfaces
        // as an env-color two-texture REFLECTION — a static base modulated by a scrolling overlay. The editor's
        // single-texture renderer can't reproduce that, so UV-sliding the opaque base makes the whole wall/floor
        // appear to scroll ("everything scrolls"). Keep only the genuine translucent (XLU) water scrolling and
        // render the opaque reflective bases static — much closer to the in-game look. (Decomp: Water Temple
        // draw config func_8009B0FC binds segs 8/9/A/B on POLY_OPA and only C/D on POLY_XLU.)
        if (rom.Game != MegatonHammer.Rom.RomGame.MM && ReflectiveOpaqueStatic.Contains(sceneId))
            foreach (var mesh in meshes)
                for (int t = 0; t < mesh.Count; t++)
                    if (mesh[t].AnimSeg != 0 && !mesh[t].Xlu) { var tri = mesh[t]; tri.AnimSeg = 0; mesh[t] = tri; }

        var segFlip = flipBuilt.ToDictionary(kv => kv.Key, kv => new Flipbook(kv.Value.frames, kv.Value.indices, kv.Value.fps));
        return new ImportedLevel(scene, rom, meshes, backgrounds, segScroll, segFlip, segColor);
    }

    // OoT scenes whose animated-texture scroll is applied to OPAQUE reflective surfaces (env-color two-tex
    // reflection) rather than to genuine moving water. For these, the editor keeps only the translucent (XLU)
    // scroll and leaves opaque bases static (see Load). Water Temple (0x05) verified from the decomp draw
    // config; Lake Hylia (0x57) is the other reflective water scene with a scroll config.
    private static readonly HashSet<int> ReflectiveOpaqueStatic = new() { 0x05, 0x57 };

    // dmadata index of scene_texture_01 (US rev1); 02..08 follow contiguously. The MM ROM matches
    // this ordering (verified: icon_item_static_yar = 19 in both ROM and the rev1 filelist).
    private const int SceneTextureFileIndex = 1114;

    // Object id → its dmadata file index (via the gObjectTable VROM), or -1.
    private static int KeepFileIndex(RomImage rom, MegatonHammer.Rom.ObjectTable objects, int objectId)
    {
        if (objects.ResolveId(objectId) is not { } v) return -1;
        foreach (var f in rom.Files) if (f.Exists && f.VromStart == v.start) return f.Index;
        return -1;
    }

    // A scene lists several environment-light settings (time-of-day / situational). Using index 0 blindly
    // tinted whole scenes with a NON-daytime setting: Kokiri Forest / Sacred Meadow / Lost Woods index 0 is
    // a warm/RED light (sun 255,125,125), so the green forest rendered autumn-red. Pick the setting that
    // best represents bright daytime — the WHITEST, BRIGHTEST directional sun (a neutral white sun is noon,
    // a coloured one is dawn/dusk/night) — which selects the neutral daytime setting instead.
    private static MegatonHammer.Rom.EnvLight? PickDaytimeLight(List<MegatonHammer.Rom.EnvLight> lights)
    {
        if (lights.Count == 0) return null;
        int Score(MegatonHammer.Rom.EnvLight l) =>
            Math.Min(l.L1r, Math.Min(l.L1g, l.L1b)) * 2   // whiteness of the sun (a coloured sun scores low)
            + l.L1r + l.L1g + l.L1b                        // sun brightness
            + l.AmbR + l.AmbG + l.AmbB;                    // ambient brightness
        var best = lights[0]; int bestScore = Score(best);
        foreach (var l in lights) { int s = Score(l); if (s > bestScore) { best = l; bestScore = s; } }
        return best;
    }

    public int TriangleCount => RoomMeshes.Sum(m => m.Count);
    public int ActorCount    => Scene.Rooms.Sum(r => r.Actors.Count);
}
