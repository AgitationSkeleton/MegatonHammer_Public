using MegatonHammer.Editor;

namespace MegatonHammer.Export;

/// <summary>
/// Assembles a complete Zelda 64 scene export:
///   {name}_scene.zscene — scene binary (segment 0x02)
///   {name}_room_{i}.zmap — one binary per ZRoom  (segment 0x03 each)
/// </summary>
public static class SceneExporter
{
    private const byte SEG_SCENE = 0x02;

    // ── Public entry point ────────────────────────────────────────────────

    public static void Export(ZScene scene, string outputDir, string sceneName)
    {
        Directory.CreateDirectory(outputDir);

        var (sceneData, rooms) = BuildBinaries(scene);
        for (int i = 0; i < rooms.Count; i++)
            File.WriteAllBytes(Path.Combine(outputDir, $"{sceneName}_room_{i}.zmap"), rooms[i]);
        File.WriteAllBytes(Path.Combine(outputDir, $"{sceneName}_scene.zscene"), sceneData);
    }

    /// <summary>Builds the scene + per-room binaries in memory (for ROM injection). texResolver maps a
    /// brush texture name → its bitmap so ROM-injected geometry is textured (null = untextured).</summary>
    /// <param name="n64Hw">true = real N64 hardware RDP tuning; false = SoH/2Ship (libultraship Fast3D).
    /// The OTR/O2R export path must pass false so the room display lists use the Fast3D-validated
    /// geometry/render-mode/alpha bytes rather than the N64-silicon-only z-write/no-cull/forced-opaque
    /// fixes (those break Fast3D's own depth/cull/texture handling — "inside-out / no textures").</param>
    public static (byte[] Scene, List<byte[]> Rooms) BuildBinaries(ZScene scene,
                                                                   Func<string, System.Drawing.Bitmap?>? texResolver = null,
                                                                   Func<ushort, ushort?>? objResolver = null,
                                                                   bool n64Hw = true, bool mm = false)
    {
        scene.CommitActiveSetup();

        bool hasWater = DisplayListBuilder.SceneHasWater(scene);

        // Multiple setups (alternate headers) → per-room alt actor lists + a scene 0x18. With 0 or 1
        // setup the scene is single-header and the original (audit-verified) path is used untouched.
        if (scene.Setups.Count >= 2)
        {
            // Multi-setup MM animmat scroll isn't wired (BuildWithSetups has no scroll list), so keep MM
            // water static here to avoid a seg-0x08 call with no animmat binding; OoT still scrolls (CALM_WATER).
            bool wsSetups = !mm && hasWater;
            var rooms = new List<byte[]>(scene.Rooms.Count);
            for (int ri = 0; ri < scene.Rooms.Count; ri++)
            {
                var perSetup = scene.Setups
                    .Select(su => (IReadOnlyList<ZActor>)(ri < su.RoomActors.Count ? su.RoomActors[ri] : []))
                    .ToList();
                rooms.Add(RoomExporter.BuildWithSetups(scene.Rooms[ri], perSetup, texResolver, objResolver, n64Hw,
                                                       n64Hw ? scene.Settings : null, mm, wsSetups));
            }
            return (BuildSceneWithSetups(scene), rooms);
        }

        // Water/texture scroll. OoT scrolls segment 0x08 via drawConfig SDC_CALM_WATER; MM scrolls it via a
        // MAT_ANIM animated material (cmd 0x1A). Either way the room DL calls seg 0x08.
        bool waterScroll = hasWater;

        // Two lists: animMat = MM cmd-0x1A entries (BuildScene). bindScrolls = which textures bind a scrolling
        // tile on seg 8+i in the room DL. MM: water (seg 8) + all authored scrolls. OoT: no data-driven scroll,
        // so bind only the FIRST authored scroll on seg 0x08 (rides CALM_WATER, set by the injector) — XLU-only.
        List<TextureScroll>? animMat = null, bindScrolls;
        if (mm)
        {
            var list = new List<TextureScroll>();
            if (hasWater) list.Add(new TextureScroll(DisplayListBuilder.WaterKey, 0.6f, 0.6f));
            list.AddRange(scene.Settings.TextureScrolls);
            animMat = bindScrolls = list.Count > 0 ? list : null;
        }
        else
        {
            bindScrolls = scene.Settings.TextureScrolls.Count > 0
                ? scene.Settings.TextureScrolls.Take(1).ToList() : null;
            if (bindScrolls != null) waterScroll = true;   // ensure the seg-0x08 call has a bound (CALM_WATER) segment
        }
        var rooms0 = scene.Rooms.Select(r => RoomExporter.Build(r, texResolver, objResolver, n64Hw,
                                                                n64Hw ? scene.Settings : null, mm, bindScrolls, waterScroll, scrollXluOnly: !mm)).ToList();
        var sceneData = BuildScene(scene, animMat, texResolver);
        return (sceneData, rooms0);
    }

    // Scene binary with N alternate headers (one per setup). All headers share the room list,
    // collision, entrance, transitions, exits and paths; each carries its own spawn / lighting /
    // skybox / sound. The primary (setup 0) header has the 0x18 alt-header list. Transitions are
    // taken from the (committed) live scene and shared across setups.
    private static byte[] BuildSceneWithSetups(ZScene scene)
    {
        int numRooms = scene.Rooms.Count;
        int n = scene.Setups.Count;
        var transitions = scene.Rooms.SelectMany(r => r.Actors).Where(a => a.IsTransitionActor && !a.IsEditorOnly).ToList();
        var exitList = CollisionBuilder.ExitEntrances(scene);
        var paths    = scene.Paths.Where(p => p.Points.Count > 0).ToList();
        bool hasTrans = transitions.Count > 0, hasExit = exitList.Count > 0, hasPaths = paths.Count > 0;
        bool hasLights = scene.PointLights.Count > 0;

        // Uniform header command count so every header is the same size (primary +1 for 0x18).
        // Base 9 (06,00,15,04,03,07,11,0F,14) + 1 for wind (0x05, always emitted) + conditionals.
        int altCmds   = 10 + (hasTrans ? 1 : 0) + (hasExit ? 1 : 0) + (hasPaths ? 1 : 0) + (hasLights ? 1 : 0);
        int primaryHdr = (altCmds + 1) * 8;
        int altHdr     = altCmds * 8;
        int hdrsSize   = primaryHdr + (n - 1) * altHdr;

        // ── Shared data layout ───────────────────────────────────────────
        int roomListOff  = hdrsSize;
        int entranceOff  = roomListOff + numRooms * 8;
        int cur = entranceOff + 2;
        int transOff = 0; if (hasTrans) { transOff = AlignUp(cur, 2); cur = transOff + transitions.Count * 16; }
        int exitOff  = 0; if (hasExit)  { exitOff  = AlignUp(cur, 2); cur = exitOff + exitList.Count * 2; }
        int pathHdrOff = 0; var pathPtOff = new int[paths.Count];
        if (hasPaths)
        {
            pathHdrOff = AlignUp(cur, 4);
            int pts = pathHdrOff + paths.Count * 8;
            for (int i = 0; i < paths.Count; i++) { pathPtOff[i] = pts; pts += paths[i].Points.Count * 6; }
            cur = pts;
        }
        int colOff = AlignUp(cur, 8);
        byte[] colData = CollisionBuilder.Build(scene, SEG_SCENE, colOff);
        cur = colOff + colData.Length;

        // ── Per-setup spawn + environment ─────────────────────────────────
        var spawnOff = new int[n]; var envOff = new int[n];
        for (int i = 0; i < n; i++)
        {
            spawnOff[i] = AlignUp(cur, 2); cur = spawnOff[i] + 16;
            envOff[i]   = AlignUp(cur, 4); cur = envOff[i] + 22;
        }
        int altListOff = AlignUp(cur, 4); cur = altListOff + n * 4;
        int lightOff = hasLights ? AlignUp(cur, 4) : 0;
        if (hasLights) cur = lightOff + scene.PointLights.Count * 14;

        // ── Write ─────────────────────────────────────────────────────────
        var w = new N64BinaryWriter();
        for (int i = 0; i < n; i++)
            WriteSceneHeader(w, scene.Setups[i].Settings, numRooms, spawnOff[i], envOff[i], roomListOff,
                colOff, entranceOff, hasTrans, transitions.Count, transOff, hasExit, exitOff, hasPaths, pathHdrOff,
                i == 0 ? altListOff : -1, hasLights ? scene.PointLights.Count : 0, lightOff);

        // shared room list (placeholder vroms, patched by the injector)
        for (int i = 0; i < numRooms; i++) { w.WriteU32(0); w.WriteU32(0); }
        // shared entrance
        w.WriteU8(0); w.WriteU8((byte)Math.Clamp(scene.Setups[0].Settings.SpawnRoom, 0, Math.Max(0, numRooms - 1)));
        // shared transitions
        if (hasTrans) { w.AlignTo(2); foreach (var t in transitions) WriteTransition(w, t); }
        // shared exit list
        if (hasExit) { w.AlignTo(2); foreach (var e in exitList) w.WriteU16(e); }
        // shared paths
        if (hasPaths)
        {
            w.AlignTo(4);
            for (int i = 0; i < paths.Count; i++)
            { w.WriteU8((byte)paths[i].Points.Count); w.WriteU8(paths[i].AdditionalPathIndex); w.WriteU16((ushort)paths[i].CustomValue); w.WriteSegPtr(SEG_SCENE, pathPtOff[i]); }
            foreach (var p in paths) foreach (var pt in p.Points)
            { w.WriteS16((short)MathF.Round(pt.X)); w.WriteS16((short)MathF.Round(pt.Y)); w.WriteS16((short)MathF.Round(pt.Z)); }
        }
        // shared collision
        w.AlignTo(8); w.WriteBytes(colData);
        // per-setup spawn + env
        for (int i = 0; i < n; i++)
        {
            var ss = scene.Setups[i].Settings;
            w.AlignTo(2);
            WriteSpawnActor(w, ss);
            w.AlignTo(4);
            WriteEnv(w, ss);
        }
        // 0x18 alt-header list: layer 0 → primary (NULL), layer i → alt header i.
        w.AlignTo(4);
        for (int i = 0; i < n; i++)
            w.WriteU32(i == 0 ? 0u : (uint)((SEG_SCENE << 24) | (primaryHdr + (i - 1) * altHdr)));

        // Shared point-light list (0x0C), appended at the end.
        if (hasLights) { w.AlignTo(4); foreach (var L in scene.PointLights) WriteLight(w, L); }

        return w.ToArray();
    }

    private static void WriteSceneHeader(N64BinaryWriter w, SceneSettings s, int numRooms, int spawnOff, int envOff,
        int roomListOff, int colOff, int entranceOff, bool hasTrans, int nTrans, int transOff, bool hasExit, int exitOff,
        bool hasPaths, int pathHdrOff, int altListOff, int nLights = 0, int lightOff = 0)
    {
        // 0x06 (entrance list) MUST precede 0x00 (player entry list): the 0x00 handler dereferences
        // play->spawnList, which the 0x06 handler sets. Wrong order → garbage spawn → load hang.
        w.WriteU8(0x06); w.WriteU8(0); w.WriteU16(0); w.WriteSegPtr(SEG_SCENE, entranceOff);
        w.WriteU8(0x00); w.WriteU8(1); w.WriteU16(0); w.WriteSegPtr(SEG_SCENE, spawnOff);
        w.WriteU8(0x15); w.WriteU8(0); w.WriteU16(0); w.WriteU8(0); w.WriteU8(0); w.WriteU8(s.NightSfx); w.WriteU8(s.MusicSeq);
        w.WriteU8(0x04); w.WriteU8((byte)numRooms); w.WriteU16(0); w.WriteSegPtr(SEG_SCENE, roomListOff);
        w.WriteU8(0x03); w.WriteU8(s.SubdivX); w.WriteU8(s.SubdivY); w.WriteU8(s.SubdivZ); w.WriteSegPtr(SEG_SCENE, colOff);
        // 0x05 wind: bytes 4–6 = dir x/y/z (s8), byte 7 = speed. Always emitted (0 = no wind) so every
        // header stays the same size for the 0x18 alt-header list.
        w.WriteU8(0x05); w.WriteU8(0); w.WriteU16(0); w.WriteU8((byte)s.WindX); w.WriteU8((byte)s.WindY); w.WriteU8((byte)s.WindZ); w.WriteU8(s.WindSpeed);
        if (hasTrans) { w.WriteU8(0x0E); w.WriteU8((byte)nTrans); w.WriteU16(0); w.WriteSegPtr(SEG_SCENE, transOff); }
        if (hasExit)  { w.WriteU8(0x13); w.WriteU8(0); w.WriteU16(0); w.WriteSegPtr(SEG_SCENE, exitOff); }
        if (hasPaths) { w.WriteU8(0x0D); w.WriteU8(0); w.WriteU16(0); w.WriteSegPtr(SEG_SCENE, pathHdrOff); }
        if (nLights > 0) { w.WriteU8(0x0C); w.WriteU8((byte)nLights); w.WriteU16(0); w.WriteSegPtr(SEG_SCENE, lightOff); }
        // 0x07 special objects: keep object id in the low 16 bits. Dungeons load gameplay_dangeon_keep
        // (0x0003) so dungeon-keep actors (pots, keys, doors) render; overworld leaves it 0 (#14).
        w.WriteU64(s.Dungeon ? 0x0700000000000003UL : 0x0700000000000000UL);
        w.WriteU8(0x11); w.WriteU8(0); w.WriteU16(0); w.WriteU8(s.SkyboxId); w.WriteU8((byte)(s.Cloudy ? 1 : 0)); w.WriteU8((byte)(s.IndoorLighting ? 1 : 0)); w.WriteU8(0);
        w.WriteU8(0x0F); w.WriteU8(1); w.WriteU16(0); w.WriteSegPtr(SEG_SCENE, envOff);
        if (altListOff >= 0) { w.WriteU8(0x18); w.WriteU8(0); w.WriteU16(0); w.WriteSegPtr(SEG_SCENE, altListOff); }
        w.WriteU64(0x1400000000000000UL);
    }

    private static void WriteSpawnActor(N64BinaryWriter w, SceneSettings s)
    {
        w.WriteU16(0x0000);
        w.WriteS16((short)MathF.Round(s.SpawnPos.X)); w.WriteS16((short)MathF.Round(s.SpawnPos.Y)); w.WriteS16((short)MathF.Round(s.SpawnPos.Z));
        w.WriteS16(0); w.WriteS16(s.SpawnYaw); w.WriteS16(0); w.WriteU16(0x0FFF);
    }

    private static void WriteEnv(N64BinaryWriter w, SceneSettings s)
    {
        w.WriteU8(s.Ambient.R); w.WriteU8(s.Ambient.G); w.WriteU8(s.Ambient.B);
        w.WriteS8(s.Light1DirX); w.WriteS8(s.Light1DirY); w.WriteS8(s.Light1DirZ);
        w.WriteU8(s.Light1Col.R); w.WriteU8(s.Light1Col.G); w.WriteU8(s.Light1Col.B);
        w.WriteS8(s.Light2DirX); w.WriteS8(s.Light2DirY); w.WriteS8(s.Light2DirZ);
        w.WriteU8(s.Light2Col.R); w.WriteU8(s.Light2Col.G); w.WriteU8(s.Light2Col.B);
        w.WriteU8(s.FogColor.R); w.WriteU8(s.FogColor.G); w.WriteU8(s.FogColor.B);
        w.WriteU16(s.FogNear); w.WriteU16(s.FogFar);
    }

    private static void WriteTransition(N64BinaryWriter w, ZActor t)
    {
        w.WriteU8(t.ExportFrontRoom); w.WriteU8(t.ExportFrontEffect); w.WriteU8(t.ExportBackRoom); w.WriteU8(t.ExportBackEffect);
        w.WriteU16(t.Number);
        w.WriteS16((short)MathF.Round(t.XPos)); w.WriteS16((short)MathF.Round(t.YPos)); w.WriteS16((short)MathF.Round(t.ZPos));
        w.WriteS16(t.ExportYRot); w.WriteU16(t.Variable);
    }

    // ── Scene binary builder ──────────────────────────────────────────────

    private static byte[] BuildScene(ZScene scene, IReadOnlyList<TextureScroll>? scrolls = null,
                                     Func<string, System.Drawing.Bitmap?>? texResolver = null)
    {
        var s = scene.Settings;
        int numRooms = scene.Rooms.Count;
        bool hasScroll = scrolls is { Count: > 0 };   // MM brush-authored animated textures (cmd 0x1A)

        // Transition actors live in the rooms (as IsTransition placements) but export as a scene-level
        // 0x0E list — the walk-into doors / room-and-scene loading planes.
        var transitions = scene.Rooms.SelectMany(r => r.Actors).Where(a => a.IsTransitionActor && !a.IsEditorOnly).ToList();
        bool hasTrans = transitions.Count > 0;
        // Exit-trigger brushes → a 0x13 exit list mapping each exit index to a destination entrance.
        var exitList  = CollisionBuilder.ExitEntrances(scene);
        bool hasExit  = exitList.Count > 0;
        // Scene paths (0x0D): waypoint polylines for moving platforms / NPC routes.
        var paths     = scene.Paths.Where(p => p.Points.Count > 0).ToList();
        bool hasPaths = paths.Count > 0;
        bool hasCutscene = scene.CutsceneData is { Length: > 0 };
        bool hasLights   = scene.PointLights.Count > 0;

        // ── Pass 1: compute offsets ───────────────────────────────────────
        // Base header: 10 commands (00,15,04,03,05,06,07,11,0F,14); +1 each for 0x0E/0x13/0x0D/0x17/0x0C.
        int numCmds   = 10 + (hasTrans ? 1 : 0) + (hasExit ? 1 : 0) + (hasPaths ? 1 : 0) + (hasCutscene ? 1 : 0) + (hasLights ? 1 : 0) + (hasScroll ? 1 : 0);
        int headerSize = numCmds * 8;

        int spawnListOff    = headerSize;
        int spawnListSize   = 16;                                      // 1 actor entry
        int roomListOff     = spawnListOff + spawnListSize;
        int roomListSize    = numRooms * 8;                           // {vromStart,vromEnd}×N
        int entranceListOff = roomListOff + roomListSize;
        int cursor          = entranceListOff + 2;                     // entrance list = 1×u16
        int transOff        = 0;
        if (hasTrans) { transOff = cursor; cursor += transitions.Count * 16; }
        int exitOff         = 0;
        if (hasExit) { exitOff = AlignUp(cursor, 2); cursor = exitOff + exitList.Count * 2; }
        int pathHdrOff      = 0;
        var pathPtOff       = new int[paths.Count];
        if (hasPaths)
        {
            pathHdrOff = AlignUp(cursor, 4);
            int pts = pathHdrOff + paths.Count * 8;                    // headers, then point arrays
            for (int i = 0; i < paths.Count; i++) { pathPtOff[i] = pts; pts += paths[i].Points.Count * 6; }
            cursor = pts;
        }
        int envCount        = Math.Max(1, scene.Environments.Count);   // multi-environment lighting
        int envOff          = AlignUp(cursor, 4);
        const int EnvSize   = 22;
        int colOff          = AlignUp(envOff + envCount * EnvSize, 8);

        byte[] colData = CollisionBuilder.Build(scene, SEG_SCENE, colOff);

        // Cutscene script appended after collision; its seg-2 pointers are relocated by the delta.
        int csOff = hasCutscene ? AlignUp(colOff + colData.Length, 16) : 0;
        byte[] csData = hasCutscene ? RelocateCutscene(scene.CutsceneData!, scene.CutsceneOrigOff, csOff) : [];

        // Point-light list (0x0C) appended at the very end (append-only → no other offsets shift).
        int lightOff = hasLights ? AlignUp(hasCutscene ? csOff + csData.Length : colOff + colData.Length, 4) : 0;

        // AnimatedMaterial list (0x1A, MM) appended last: K entries (8 bytes) then K param structs (4 bytes).
        int animMatOff = 0, animScrollK = hasScroll ? Math.Min(scrolls!.Count, 6) : 0;
        if (hasScroll)
        {
            int dataEnd = hasLights ? lightOff + scene.PointLights.Count * 14
                        : hasCutscene ? csOff + csData.Length : colOff + colData.Length;
            animMatOff = AlignUp(dataEnd, 4);
        }

        int spawnRoom = Math.Clamp(s.SpawnRoom, 0, Math.Max(0, numRooms - 1));

        // ── Pass 2: write binary ──────────────────────────────────────────
        var w = new N64BinaryWriter();

        // 0x06: Entrance list           [06 00 00 00 | 02 off]
        // MUST precede 0x00: Scene_CommandPlayerEntryList (the 0x00 handler) dereferences
        // play->spawnList — which the 0x06 handler (Scene_CommandSpawnList) sets. Emitting 0x00 first
        // leaves play->spawnList unset → garbage spawn lookup → corruption → the scene load hangs.
        w.WriteU8(0x06); w.WriteU8(0); w.WriteU16(0);
        w.WriteSegPtr(SEG_SCENE, entranceListOff);

        // 0x00: Spawn point actor list  [00 numSpawns 00 00 | 02 off]
        w.WriteU8(0x00); w.WriteU8(1); w.WriteU16(0);
        w.WriteSegPtr(SEG_SCENE, spawnListOff);

        // 0x15: Sound/music settings    [15 reverb 00 00 | 00 00 nightSfx music]
        w.WriteU8(0x15); w.WriteU8(0); w.WriteU16(0);
        w.WriteU8(0); w.WriteU8(0); w.WriteU8(s.NightSfx); w.WriteU8(s.MusicSeq);

        // 0x04: Room list (map list)    [04 numRooms 00 00 | 02 off]
        w.WriteU8(0x04); w.WriteU8((byte)numRooms); w.WriteU16(0);
        w.WriteSegPtr(SEG_SCENE, roomListOff);

        // 0x03: Collision header        [03 subdivX subdivY subdivZ | 02 off]
        w.WriteU8(0x03); w.WriteU8(s.SubdivX); w.WriteU8(s.SubdivY); w.WriteU8(s.SubdivZ);
        w.WriteSegPtr(SEG_SCENE, colOff);

        // 0x05: Wind settings           [05 00 00 00 | dirX dirY dirZ speed]   (always; 0 = no wind)
        w.WriteU8(0x05); w.WriteU8(0); w.WriteU16(0);
        w.WriteU8((byte)s.WindX); w.WriteU8((byte)s.WindY); w.WriteU8((byte)s.WindZ); w.WriteU8(s.WindSpeed);

        // 0x0E: Transition actor list   [0E count 00 00 | 02 off]   (only when present)
        if (hasTrans)
        {
            w.WriteU8(0x0E); w.WriteU8((byte)transitions.Count); w.WriteU16(0);
            w.WriteSegPtr(SEG_SCENE, transOff);
        }

        // 0x13: Exit list               [13 00 00 00 | 02 off]   (only when present)
        if (hasExit)
        {
            w.WriteU8(0x13); w.WriteU8(0); w.WriteU16(0);
            w.WriteSegPtr(SEG_SCENE, exitOff);
        }

        // 0x0D: Path list               [0D count 00 00 | 02 off]   (only when present)
        if (hasPaths)
        {
            w.WriteU8(0x0D); w.WriteU8((byte)paths.Count); w.WriteU16(0);
            w.WriteSegPtr(SEG_SCENE, pathHdrOff);
        }

        // 0x17: Cutscene data           [17 00 00 00 | 02 off]   (only when present)
        if (hasCutscene)
        {
            w.WriteU8(0x17); w.WriteU8(0); w.WriteU16(0);
            w.WriteSegPtr(SEG_SCENE, csOff);
        }

        // 0x0C: Positional light list   [0C count 00 00 | 02 off]   (only when present)
        if (hasLights)
        {
            w.WriteU8(0x0C); w.WriteU8((byte)scene.PointLights.Count); w.WriteU16(0);
            w.WriteSegPtr(SEG_SCENE, lightOff);
        }

        // 0x1A: Animated material list (MM brush-authored scrolling textures)  [1A 00 00 00 | 02 off]
        if (hasScroll)
        {
            w.WriteU8(0x1A); w.WriteU8(0); w.WriteU16(0);
            w.WriteSegPtr(SEG_SCENE, animMatOff);
        }

        // 0x07: Special objects — dungeons load gameplay_dangeon_keep (0x0003) so dungeon-keep actors
        // (pots, keys, doors) render; overworld leaves it 0 (#14).  [07 00 00 00 | 00 00 00 keep]
        w.WriteU64(s.Dungeon ? 0x0700000000000003UL : 0x0700000000000000UL);

        // 0x11: Skybox/lighting         [11 00 00 00 | skybox cloudy indoor 00]
        w.WriteU8(0x11); w.WriteU8(0); w.WriteU16(0);
        w.WriteU8(s.SkyboxId);
        w.WriteU8((byte)(s.Cloudy ? 1 : 0));
        w.WriteU8((byte)(s.IndoorLighting ? 1 : 0));
        w.WriteU8(0);

        // 0x0F: Environments            [0F count 00 00 | 02 off]
        w.WriteU8(0x0F); w.WriteU8((byte)envCount); w.WriteU16(0);
        w.WriteSegPtr(SEG_SCENE, envOff);

        // 0x14: End of scene header
        w.WriteU64(0x1400000000000000UL);

        // ── Spawn point (1 × 16 bytes: Link actor) ───────────────────────
        // Actor entry format: u16 id, s16 posX/Y/Z, s16 rotX/Y/Z, u16 params
        w.WriteU16(0x0000);                       // id: Link
        w.WriteS16((short)MathF.Round(s.SpawnPos.X));
        w.WriteS16((short)MathF.Round(s.SpawnPos.Y));
        w.WriteS16((short)MathF.Round(s.SpawnPos.Z));
        w.WriteS16(0);                            // rotX
        w.WriteS16(s.SpawnYaw);                   // rotY (facing)
        w.WriteS16(0);                            // rotZ
        w.WriteU16(0x0FFF);                       // params: valid for all rooms

        // ── Room list (N × 8 bytes: vromStart, vromEnd pairs) ────────────
        // Filled with zeros; actual ROM addresses are patched by ROM-injector.
        for (int i = 0; i < numRooms; i++)
        {
            w.WriteU32(0x00000000);  // vromStart (placeholder)
            w.WriteU32(0x00000000);  // vromEnd   (placeholder)
        }

        // ── Entrance list (1 × 2 bytes: {spawnIndex, roomIndex}) ─────────
        w.WriteU8(0);                  // spawnIndex (into spawn list)
        w.WriteU8((byte)spawnRoom);    // roomIndex

        // ── Transition actor list (N × 16 bytes) ─────────────────────────
        // { frontRoom, frontEffect, backRoom, backEffect, id(u16), posX/Y/Z(s16), rotY(s16), params(u16) }
        foreach (var t in transitions)
        {
            w.WriteU8(t.ExportFrontRoom); w.WriteU8(t.ExportFrontEffect); w.WriteU8(t.ExportBackRoom); w.WriteU8(t.ExportBackEffect);
            w.WriteU16(t.Number);
            w.WriteS16((short)MathF.Round(t.XPos));
            w.WriteS16((short)MathF.Round(t.YPos));
            w.WriteS16((short)MathF.Round(t.ZPos));
            w.WriteS16(t.ExportYRot);
            w.WriteU16(t.Variable);
        }

        // ── Exit list (N × u16 entrance ids) ─────────────────────────────
        foreach (var entrance in exitList) w.WriteU16(entrance);

        // ── Path list: N headers {count, pad×3, points*} then each path's Vec3s points ──
        if (hasPaths)
        {
            w.AlignTo(4);
            for (int i = 0; i < paths.Count; i++)
            {
                // {count, additionalPathIndex, customValue, points*} — MM uses the middle fields; OoT pads.
                w.WriteU8((byte)paths[i].Points.Count);
                w.WriteU8(paths[i].AdditionalPathIndex);
                w.WriteU16((ushort)paths[i].CustomValue);
                w.WriteSegPtr(SEG_SCENE, pathPtOff[i]);
            }
            foreach (var p in paths)
                foreach (var pt in p.Points)
                {
                    w.WriteS16((short)MathF.Round(pt.X));
                    w.WriteS16((short)MathF.Round(pt.Y));
                    w.WriteS16((short)MathF.Round(pt.Z));
                }
        }

        w.AlignTo(4);                  // pad to 4-byte boundary before env data

        // ── Environment (lighting) settings (envCount × 22 bytes) ────────
        // Entry 0 comes from the editable Settings; any further imported entries are preserved.
        w.WriteU8(s.Ambient.R); w.WriteU8(s.Ambient.G); w.WriteU8(s.Ambient.B);
        w.WriteS8(s.Light1DirX); w.WriteS8(s.Light1DirY); w.WriteS8(s.Light1DirZ);
        w.WriteU8(s.Light1Col.R); w.WriteU8(s.Light1Col.G); w.WriteU8(s.Light1Col.B);
        w.WriteS8(s.Light2DirX); w.WriteS8(s.Light2DirY); w.WriteS8(s.Light2DirZ);
        w.WriteU8(s.Light2Col.R); w.WriteU8(s.Light2Col.G); w.WriteU8(s.Light2Col.B);
        w.WriteU8(s.FogColor.R); w.WriteU8(s.FogColor.G); w.WriteU8(s.FogColor.B);
        w.WriteU16(s.FogNear);
        w.WriteU16(s.FogFar);
        // 3+3+3+3+3+3+2+2 = 22 bytes ✓
        for (int i = 1; i < envCount; i++)
        {
            var e = scene.Environments[i];
            w.WriteU8(e.AmbR); w.WriteU8(e.AmbG); w.WriteU8(e.AmbB);
            w.WriteS8(e.L1x); w.WriteS8(e.L1y); w.WriteS8(e.L1z);
            w.WriteU8(e.L1r); w.WriteU8(e.L1g); w.WriteU8(e.L1b);
            w.WriteS8(e.L2x); w.WriteS8(e.L2y); w.WriteS8(e.L2z);
            w.WriteU8(e.L2r); w.WriteU8(e.L2g); w.WriteU8(e.L2b);
            w.WriteU8(e.FogR); w.WriteU8(e.FogG); w.WriteU8(e.FogB);
            w.WriteU16(e.FogNear); w.WriteU16(e.FogFar);
        }

        w.AlignTo(8);

        // ── Collision data ────────────────────────────────────────────────
        w.WriteBytes(colData);

        // ── Cutscene script (retained verbatim, pointers relocated) ───────
        if (hasCutscene) { w.AlignTo(16); w.WriteBytes(csData); }

        // ── Positional lights (14-byte LightInfo each, at lightOff) ───────
        if (hasLights) { w.AlignTo(4); foreach (var L in scene.PointLights) WriteLight(w, L); }

        // ── AnimatedMaterial list (0x1A) + tex-scroll params, at animMatOff ───
        if (hasScroll)
        {
            w.AlignTo(4);
            int paramsOff = animMatOff + animScrollK * 8;
            for (int i = 0; i < animScrollK; i++)
            {
                int segVal = i + 1;                               // bound CPU segment 8+i ⇒ field magnitude (i+1)+...
                w.WriteS8((sbyte)(i == animScrollK - 1 ? -segVal : segVal));   // last entry negated (list terminator)
                w.WriteU8(0); w.WriteU16(0);                      // pad, type = 0 (single texture scroll)
                w.WriteSegPtr(SEG_SCENE, paramsOff + i * 4);      // → AnimatedMatTexScrollParams
            }
            for (int i = 0; i < animScrollK; i++)
            {
                var sc = scrolls![i];
                var bmp = texResolver?.Invoke(sc.Name);
                int width = Math.Clamp(bmp?.Width ?? 32, 1, 255), height = Math.Clamp(bmp?.Height ?? 32, 1, 255);
                // editor scroll (tiles/sec) → s10.2 step/frame: U·w·4/20 = U·w/5 (V uses −yStep, the MM convention).
                int xStep = Math.Clamp((int)MathF.Round(sc.U * width / 5f), -128, 127);
                int yStep = Math.Clamp((int)MathF.Round(-sc.V * height / 5f), -128, 127);
                w.WriteS8((sbyte)xStep); w.WriteS8((sbyte)yStep); w.WriteU8((byte)width); w.WriteU8((byte)height);
            }
        }

        return w.ToArray();
    }

    // LightInfo: u8 type (2=point glow / 0=no glow), pad, then LightPoint {s16 x,y,z; u8 col[3]; u8 drawGlow; s16 radius}.
    private static void WriteLight(N64BinaryWriter w, ScenePointLight L)
    {
        w.WriteU8((byte)(L.Glow ? 2 : 0)); w.WriteU8(0);
        w.WriteS16((short)MathF.Round(L.X)); w.WriteS16((short)MathF.Round(L.Y)); w.WriteS16((short)MathF.Round(L.Z));
        w.WriteU8(L.R); w.WriteU8(L.G); w.WriteU8(L.B);
        w.WriteU8((byte)(L.Glow ? 1 : 0));
        w.WriteS16(L.Radius);
    }

    // Re-emits a retained cutscene block at a new scene offset, fixing its internal segment-2
    // pointers (0x02xxxxxx) that referenced offsets inside the original block by the relocation
    // delta. Cutscene scripts are self-contained, so this preserves their actor cue / camera lists.
    private static byte[] RelocateCutscene(byte[] cs, int origOff, int newOff)
    {
        int delta = newOff - origOff;
        if (delta == 0) return cs;
        var r = (byte[])cs.Clone();
        for (int p = 0; p + 4 <= r.Length; p += 4)
        {
            uint v = (uint)((r[p] << 24) | (r[p + 1] << 16) | (r[p + 2] << 8) | r[p + 3]);
            if ((v >> 24) != 0x02) continue;
            int o = (int)(v & 0x00FFFFFF);
            if (o < origOff || o >= origOff + cs.Length) continue;   // only pointers into the block itself
            uint nv = (uint)(0x02000000 | ((o + delta) & 0x00FFFFFF));
            r[p] = (byte)(nv >> 24); r[p + 1] = (byte)(nv >> 16); r[p + 2] = (byte)(nv >> 8); r[p + 3] = (byte)nv;
        }
        return r;
    }

    private static int AlignUp(int v, int align) => (v + align - 1) & ~(align - 1);
}
