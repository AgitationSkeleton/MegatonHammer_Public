using MegatonHammer.Editor;
using MegatonHammer.Rom;

namespace MegatonHammer.Otr;

/// <summary>
/// Assembles a complete level as the set of libultraship OTR resources SoH/2Ship loads
/// natively: a scene resource (header commands), one room resource per room (mesh + actor
/// commands), a collision resource, and per-room geometry (ODLT display list + OVTX
/// vertices). All cross-resource links use the exact archive paths/hashes the runtime
/// resolves, so dropping these into a mod .o2r makes the level loadable with no change to
/// SoH's resource pipeline.
///
/// Scenes and rooms share one binary format: a uint32 command count followed by commands,
/// each <c>int32 cmdId</c> + payload (verified against SoH's ResourceFactoryBinarySceneV0,
/// which parses both the OROM scene and OROM room resources).
/// </summary>
public static class OtrSceneWriter
{
    private const uint OROM = 0x4F524F4D;   // scene AND room resource tag

    // Scene command ids
    private const int CmdStartPositionList = 0x00;
    private const int CmdActorList         = 0x01;
    private const int CmdCollisionHeader   = 0x03;
    private const int CmdRoomList          = 0x04;
    private const int CmdEntranceList      = 0x06;
    private const int CmdTransitionActorList = 0x0E;
    private const int CmdSpecialObjects    = 0x07;
    private const int CmdRoomBehavior      = 0x08;
    private const int CmdMesh              = 0x0A;
    private const int CmdObjectList        = 0x0B;
    private const int CmdLightingSettings  = 0x0F;
    private const int CmdSkyboxSettings    = 0x11;
    private const int CmdExitList          = 0x13;
    private const int CmdEnd               = 0x14;
    private const int CmdSoundSettings     = 0x15;   // reverb, natureAmbience, seqId (music)
    private const int CmdSetAnimatedMaterials = 0x1A; // MM animated material list (tile scroll / colour / cycle)

    private const ushort ObjectFieldKeep   = 0x0002;   // gameplay_field_keep (overworld global object)
    private const ushort ObjectDungeonKeep = 0x0003;   // gameplay_dangeon_keep (dungeon global object)
    private const ushort ActorPlayer     = 0x0000;

    public record OtrResource(string Path, byte[] Data);

    // Decomp-derived tables for resolving each actor's required object (so it spawns). PER-GAME:
    // an MM actor id maps to a DIFFERENT object than the same id in OoT, so using the OoT tables for an
    // MM scene listed the wrong (or no) objects → the actors never spawned in 2Ship (they did in PJ64,
    // whose N64 path already passed an mm-aware resolver). Cached per game, built on first use.
    private static ActorObjectTable? _aoOot, _aoMm;
    private static ObjectTable? _objOot, _objMm;
    private static (ActorObjectTable ao, ObjectTable obj) Tables(bool mm) => mm
        ? (_aoMm ??= ActorObjectTable.Build(mm: true), _objMm ??= ObjectTable.BuildNamesOnly(mm: true))
        : (_aoOot ??= ActorObjectTable.Build(), _objOot ??= ObjectTable.BuildNamesOnly());

    // Distinct object ids the room's actors need loaded (in id order). Actors won't spawn
    // without their object in the room's object list — this also satisfies bosses' object
    // requirement (D17). Unresolved/keep-only actors contribute nothing.
    private static List<ushort> ObjectsForRoom(ZRoom room, bool mm)
    {
        var (actorObjects, objectIds) = Tables(mm);
        var ids = new SortedSet<ushort>();
        foreach (var a in room.Actors)
        {
            if (a.IsTransitionActor || a.IsEditorOnly) continue;   // 0x0E door list / editor-only props aren't room actors
            string? objName = actorObjects.ObjectFor(a.Number);
            // Global keeps (gameplay/field/dungeon keep) load via the scene's 0x07 command, not the room list —
            // never emit them as room deps (a dungeon-keep actor in an overworld scene would otherwise list 0x0003).
            if (objName != null && !objName.Contains("keep") && objectIds.IdOf(objName) is int id && id > 0)
                ids.Add((ushort)id);
            // Spawn fix-up: e.g. force the pot onto OBJECT_TSUBO so it loads in any scene (see ActorExportFix).
            if (Export.ActorExportFix.ExtraRoomObject(mm, a.Number, a.Variable) is var eo && eo != 0)
                ids.Add(eo);
        }
        return ids.ToList();
    }

    // MM actor-entry rotation: degrees (0-359) packed into the high 9 bits, ((deg & 0x1FF) << 7), with
    // the low bits (csId in rot.y, halfDaysBits in rot.x/rot.z) left 0 so the actor always spawns with
    // no cutscene. MM's Actor_SpawnEntry reads (rot >> 7) & 0x1FF as degrees and DEG_TO_BINANGs it back.
    //
    // EXCEPTION — packed spawn-condition fields: when the actor's id carries 0x4000 (rotX packed) /
    // 0x2000 (rotZ packed) / 0x8000 (rotY = csId), the editor (EntityConfigDialog half-day grid) has
    // already placed the half-day mask / csId into the LOW bits of that rotation field. Re-running the
    // degree pack would wipe those low bits (and 0x4000/0x2000 tell the engine the high-9 value is a
    // raw signed binang, not degrees), so the field must be written VERBATIM. axisFlag picks the bit.
    private static short MmActorRot(short binAngle, ushort idFlags = 0, ushort axisFlag = 0)
    {
        if (axisFlag != 0 && (idFlags & axisFlag) != 0) return binAngle;  // packed mask/csId — preserve low bits
        int deg = (int)MathF.Round((binAngle & 0xFFFF) * 360f / 65536f);   // binary angle -> degrees
        deg = ((deg % 360) + 360) % 360;
        return (short)((deg & 0x1FF) << 7);
    }

    /// <param name="basePath">Archive path stem, e.g. "scenes/nonmq/spot00_scene/spot00_scene".
    /// Rooms/collision/geometry derive their paths from it.</param>
    /// <param name="mm">True for Majora's Mask / 2Ship — changes the SetRoomBehavior layout
    /// (MM parses six int8 fields; OoT parses one u8 + one s32).</param>
    public static List<OtrResource> BuildLevel(ZScene scene, string basePath, bool mm = false,
                                               Func<string, System.Drawing.Bitmap?>? texResolver = null)
    {
        var res = new List<OtrResource>();
        var s = scene.Settings;

        // MM brush-authored animated textures (tile scroll). Faces whose texture is in this ordered list
        // bind a scrolling tile on segment 8+i; the scene ships an AnimatedMaterial list (cmd 0x1A) that
        // the engine's MAT_ANIM draw config animates each frame. OoT has no equivalent here.
        var scrolls = mm && s.TextureScrolls.Count > 0 ? s.TextureScrolls : null;
        var scrollNames = scrolls?.Select(sc => sc.Name).ToList();
        string? matAnimPath = scrolls != null ? $"{basePath}_matanim" : null;

        // ── Geometry + collision resources ──────────────────────────────────
        // Warp triggers: assign each distinct destination a 1-based exit index shared between the
        // collision surface types and the scene's exit-list command.
        var (exitEntrances, solidExit) = BuildExitTable(scene);
        string collisionPath = $"{basePath}_collision";
        res.Add(new OtrResource(collisionPath, OtrCollisionHeader.Build(scene, solidExit)));

        var roomPaths = new List<string>();
        for (int i = 0; i < scene.Rooms.Count; i++)
        {
            string roomPath = $"{basePath}_room_{i}";
            string dlPath  = $"{roomPath}_dl";
            string xluDlPath = $"{roomPath}_xludl";
            string vtxPath = $"{roomPath}_vtx";
            roomPaths.Add(roomPath);

            // OoT (SoH): emit the seg-0x08 scroll call in the water XLU DL — the fork's mh boot hook sets the
            // scene draw config to SDC_CALM_WATER (keyed off the same mh/info "waterScroll" flag), which binds
            // that segment. MM (2Ship) stays false for now (its MAT_ANIM scroll needs a cmd-0x1A water entry),
            // so its water DL never calls the unbound segment.
            bool waterScroll = !mm && Export.DisplayListBuilder.SceneHasWater(scene);
            var geom = OtrRoomGeometry.Build(scene.Rooms[i], vtxPath, roomPath, texResolver, scene.Settings.BakedShade, scrollNames, waterScroll);
            bool hasGeom = !geom.Empty;
            bool hasWater = geom.XluDl.Length > 0;
            if (hasGeom)
            {
                res.Add(new OtrResource(vtxPath, geom.Vtx));
                res.Add(new OtrResource(dlPath, geom.Dl));
                if (hasWater) res.Add(new OtrResource(xluDlPath, geom.XluDl));
                foreach (var tex in geom.Textures)
                    res.Add(new OtrResource(tex.Path, tex.Data));   // OTEX texture resources
            }
            res.Add(new OtrResource(roomPath, BuildRoom(scene.Rooms[i], hasGeom ? dlPath : null, hasWater ? xluDlPath : null, mm, s.Dungeon)));
        }

        // ── Animated-material resource (TSH_TexAnim) referenced by the scene's cmd 0x1A ──────
        if (scrolls != null)
            res.Add(new OtrResource(matAnimPath!, BuildTexAnim(scrolls, texResolver)));

        // ── Scene resource ──────────────────────────────────────────────────
        res.Add(new OtrResource(basePath, BuildScene(scene, roomPaths, collisionPath, exitEntrances, mm, matAnimPath)));
        return res;
    }

    // Distinct trigger destinations → the exit list (1-based) + a brush→exit-index map.
    private static (List<ushort> entrances, Dictionary<Editor.Solid, int> solidExit) BuildExitTable(ZScene scene)
    {
        var entrances = new List<ushort>();
        var byEntrance = new Dictionary<int, int>();
        var solidExit = new Dictionary<Editor.Solid, int>();
        foreach (var room in scene.Rooms)
            foreach (var solid in room.Geometry)
            {
                // A brush is a warp trigger via its IsTrigger flag OR a WARP tool-texture face (parity with
                // the N64 CollisionBuilder.IsWarpTrigger); either way its ExitEntrance is the target.
                if (!Export.CollisionBuilder.IsWarpTrigger(solid) || solid.ExitEntrance < 0) continue;
                int e = solid.ExitEntrance;
                if (!byEntrance.TryGetValue(e, out int idx))
                {
                    entrances.Add((ushort)e);
                    idx = entrances.Count;       // 1-based
                    byEntrance[e] = idx;
                }
                solidExit[solid] = idx;
            }
        return (entrances, solidExit);
    }

    // ── Scene header ────────────────────────────────────────────────────────
    private static byte[] BuildScene(ZScene scene, List<string> roomPaths, string collisionPath, List<ushort> exitEntrances,
                                     bool mm, string? matAnimPath = null)
    {
        var s = scene.Settings;
        var cmds = new List<Action<OtrResourceWriter>>();

        // Keep object: dungeons load gameplay_dangeon_keep (dungeon pots/doors/keys/etc.), overworld loads
        // field keep. The dungeon-keep pot (Obj_Tsubo with params bit8=0, default) is killed if its keep is
        // absent → invisible pots (#14), hence keying this off the scene's Dungeon flag.
        ushort keepObj = s.Dungeon ? ObjectDungeonKeep : ObjectFieldKeep;
        cmds.Add(w => { Cmd(w, CmdSpecialObjects); w.U8(0); w.S16((short)keepObj); });

        cmds.Add(w =>
        {
            Cmd(w, CmdRoomList);
            w.S32(roomPaths.Count);
            foreach (var rp in roomPaths) { w.Str(rp); w.S32(0); w.S32(0); }   // vromStart/End unused (loaded by path)
        });

        cmds.Add(w => { Cmd(w, CmdCollisionHeader); w.Str(collisionPath); });

        // One entrance: spawn 0, into the configured spawn room.
        cmds.Add(w => { Cmd(w, CmdEntranceList); w.U32(1); w.U8(0); w.U8((byte)Math.Clamp(s.SpawnRoom, 0, 255)); });

        // Transition-actor list (0x0E): room/scene-loading planes AND doors (En_Door/Door_Shutter).
        // Doors must live here, not in a room actor list: the engine spawns them via
        // Actor_SpawnTransitionActors, which sets params = (index<<0xA)+params so EnDoor_OverrideLimbDraw's
        // play->transiActorCtx.list[params>>0xA] resolves — and the 0x0E command handler is what populates
        // transiActorCtx.list in the first place, so a door in this list is self-consistent.
        var transitions = scene.Rooms.SelectMany(r => r.Actors)
                               .Where(a => a.IsTransitionActor && !a.IsEditorOnly).ToList();
        if (transitions.Count > 0)
            cmds.Add(w =>
            {
                Cmd(w, CmdTransitionActorList);
                w.U32((uint)transitions.Count);
                foreach (var t in transitions)
                {
                    w.U8(t.ExportFrontRoom); w.U8(t.ExportFrontEffect); w.U8(t.ExportBackRoom); w.U8(t.ExportBackEffect);
                    w.S16((short)t.Number);
                    w.S16((short)MathF.Round(t.XPos)); w.S16((short)MathF.Round(t.YPos)); w.S16((short)MathF.Round(t.ZPos));
                    w.S16(t.ExportYRot);
                    w.U16(t.Variable);
                }
            });

        // Exit list: entrance index for each warp-trigger exit (collision surface types index into it).
        if (exitEntrances.Count > 0)
            cmds.Add(w =>
            {
                Cmd(w, CmdExitList);
                w.U32((uint)exitEntrances.Count);
                foreach (var e in exitEntrances) w.U16(e);
            });

        // Link's start position.
        cmds.Add(w =>
        {
            Cmd(w, CmdStartPositionList);
            w.U32(1);
            w.U16(ActorPlayer);
            w.S16((short)MathF.Round(s.SpawnPos.X));
            w.S16((short)MathF.Round(s.SpawnPos.Y));
            w.S16((short)MathF.Round(s.SpawnPos.Z));
            w.S16(0); w.S16(s.SpawnYaw); w.S16(0);
            w.U16(0x0FFF);   // params: default spawn
        });

        cmds.Add(w =>
        {
            Cmd(w, CmdSkyboxSettings);
            w.U8(0);                                  // unk
            w.U8(s.SkyboxId);                         // skybox id
            w.U8((byte)(s.Cloudy ? 1 : 0));           // weather
            // envLightMode/indoors: 1 = LIGHT_MODE_SETTINGS (use THIS scene's env light command directly),
            // 0 = LIGHT_MODE_TIME (outdoor day/night cycle). A custom scene has no time-of-day env table,
            // so LIGHT_MODE_TIME left lightCtx garbage — fogNear=0 (<980) triggered Environment_Draw-
            // SkyboxFilters' full-screen fog-colour fill at alpha 1.0 with a garbage (red) fog colour
            // (the "solid red sky/Link" in SoH). IndoorLighting=true (the editor default) → write 1 so the
            // scene's own ambient/diffuse/fog are used as-is. (Was hardcoded to 0 — `? 0 : 0` — a bug.)
            w.U8((byte)(s.IndoorLighting ? 1 : 0));
        });

        cmds.Add(w =>
        {
            Cmd(w, CmdLightingSettings);
            w.S32(1);
            w.U8(s.Ambient.R); w.U8(s.Ambient.G); w.U8(s.Ambient.B);
            w.U8((byte)s.Light1DirX); w.U8((byte)s.Light1DirY); w.U8((byte)s.Light1DirZ);
            w.U8(s.Light1Col.R); w.U8(s.Light1Col.G); w.U8(s.Light1Col.B);
            w.U8((byte)s.Light2DirX); w.U8((byte)s.Light2DirY); w.U8((byte)s.Light2DirZ);
            w.U8(s.Light2Col.R); w.U8(s.Light2Col.G); w.U8(s.Light2Col.B);
            w.U8(s.FogColor.R); w.U8(s.FogColor.G); w.U8(s.FogColor.B);
            w.S16((short)s.FogNear); w.U16(s.FogFar);
        });

        // Background music (0x15 SetSoundSettings): reverb, nature-ambience (night SFX), seqId. Without this
        // the scene plays no music. A CROSS-GAME track is injected as an OSEQ resource claiming a valid vanilla
        // host seqId (SequenceInjector.HostSeqId) that the fork boot hook force-maps, so point 0x15 there.
        byte seqId = s.MusicCrossGame ? (byte)Export.SequenceInjector.HostSeqId(mm) : s.MusicSeq;
        cmds.Add(w => { Cmd(w, CmdSoundSettings); w.U8(0); w.U8(s.NightSfx); w.U8(seqId); });

        // MM animated materials (cmd 0x1A): point at the TSH_TexAnim resource. The mh_append scene table
        // entry uses SCENE_DRAW_CFG_MAT_ANIM so the engine animates play->sceneMaterialAnims each frame.
        if (matAnimPath != null)
            cmds.Add(w => { Cmd(w, CmdSetAnimatedMaterials); w.Str(matAnimPath); });

        cmds.Add(w => Cmd(w, CmdEnd));
        return Emit(OROM, cmds);
    }

    // ── Room header ───────────────────────────────────────────────────────
    private static byte[] BuildRoom(ZRoom room, string? dlPath, string? xluPath, bool mm, bool isDungeon = false)
    {
        var cmds = new List<Action<OtrResourceWriter>>();

        // Mesh (polygon type 0): one entry pointing at the room display lists (opa + optional xlu water).
        cmds.Add(w =>
        {
            Cmd(w, CmdMesh);
            w.U8(0);                    // data
            w.U8(0);                    // meshHeaderType = 0
            w.U8(1);                    // polyNum = 1
            w.U8(0);                    // polyType (unused)
            w.Str(dlPath ?? "");        // opa display list path ("" if room is empty)
            w.Str(xluPath ?? "");       // xlu display list path (the translucent water surface)
        });

        // Objects required by this room's actors (deduplicated) — without these, actors won't spawn.
        var objects = ObjectsForRoom(room, mm);
        cmds.Add(w =>
        {
            Cmd(w, CmdObjectList);
            w.U32((uint)objects.Count);
            foreach (var id in objects) w.U16(id);
        });

        // Actors placed in the room.
        cmds.Add(w =>
        {
            // Door/transition actors are NOT room actors — they go in the scene's transition-actor
            // list (0x0E). Spawning a door as a room actor makes EnDoor_OverrideLimbDraw index
            // play->transiActorCtx.list[params>>10] out of range → crash. Exclude them here.
            var roomActors = room.Actors.Where(a => !a.IsTransitionActor && !a.IsEditorOnly).ToList();
            Cmd(w, CmdActorList);
            w.U32((uint)roomActors.Count);
            foreach (var a in roomActors)
            {
                // IdFlags carries MM spawn-condition bits (0x8000 rotY=csId, 0x4000/0x2000 rotX/rotZ packed);
                // 0 for OoT. Previously dropped here, which made the half-day/csId editor a no-op on 2Ship.
                w.U16((ushort)(a.Number | a.IdFlags));
                w.S16((short)MathF.Round(a.XPos));
                w.S16((short)MathF.Round(a.YPos));
                w.S16((short)MathF.Round(a.ZPos));
                // Rotation encoding is PER-GAME. OoT stores a plain binary angle. MM's Actor_SpawnEntry
                // instead reads the rotation in DEGREES from the high 9 bits ((rot>>7)&0x1FF) and
                // repurposes the low bits: rot.y[6:0]=csId, and halfDaysBits=((rot.x&7)<<7)|(rot.z&0x7F).
                // Writing a raw binary angle put garbage in those low bits — e.g. a chest at yaw 0xC000
                // gave (rot.x&7)<<7 = 0x100 halfDaysBits, which matches NO half-day, so En_Box never
                // spawned (En_Horse at rot 0 → halfDaysBits 0 = always, so it did). Encode MM as
                // (degrees<<7) with low bits 0 → halfDaysBits 0 (always spawn), csId 0, and the facing
                // round-trips through MM's DEG_TO_BINANG correctly.
                if (mm)
                {
                    w.S16(MmActorRot(a.XRot, a.IdFlags, 0x4000));
                    w.S16(MmActorRot(a.YRot, a.IdFlags, 0x8000));
                    w.S16(MmActorRot(a.ZRot, a.IdFlags, 0x2000));
                }
                else
                {
                    w.S16(a.XRot); w.S16(a.YRot); w.S16(a.ZRot);
                }
                w.U16(Export.ActorExportFix.Variable(mm, a.Number, a.Variable, isDungeon));
            }
        });

        // SetRoomBehavior payload differs by game. OoT (SoH): u8 behaviorType + s32 flags.
        // MM (2Ship): six int8 — gameplayFlags + five room/effect fields we leave at 0.
        cmds.Add(w =>
        {
            Cmd(w, CmdRoomBehavior);
            if (mm)
            {
                w.U8(room.Settings.BehaviorType);   // gameplayFlags
                w.U8(0); w.U8(0); w.U8(0); w.U8(0); w.U8(0);
            }
            else
            {
                w.U8(room.Settings.BehaviorType);
                w.S32(0);
            }
        });

        cmds.Add(w => Cmd(w, CmdEnd));
        return Emit(OROM, cmds);
    }

    // ── helpers ───────────────────────────────────────────────────────────
    private static void Cmd(OtrResourceWriter w, int id) => w.S32(id);

    // ── Animated-material (TSH_TexAnim "OTAN") resource: the binary form 2Ship's TextureAnimationFactory
    // reads. One entry per scroll; segment = i+1 (LAST negated so the engine's draw loop terminates —
    // segmentAbs = ABS(segment)+7 binds N64 segment 8+i, matching the room DL's gsSPDisplayList branch).
    // Single-texture scroll only (type 0); params mirror the proven N64 path (U·w/5, V uses −yStep). ──
    private static byte[] BuildTexAnim(IReadOnlyList<TextureScroll> scrolls,
                                       Func<string, System.Drawing.Bitmap?>? texResolver)
    {
        int k = Math.Min(scrolls.Count, 6);
        var w = new OtrResourceWriter(OtrResType.TexAnim);
        w.U32((uint)k);
        for (int i = 0; i < k; i++)
        {
            var sc = scrolls[i];
            var bmp = texResolver?.Invoke(sc.Name);
            int width = Math.Clamp(bmp?.Width ?? 32, 1, 255), height = Math.Clamp(bmp?.Height ?? 32, 1, 255);
            int xStep = Math.Clamp((int)MathF.Round(sc.U * width / 5f), -128, 127);
            int yStep = Math.Clamp((int)MathF.Round(-sc.V * height / 5f), -128, 127);
            int segVal = i + 1;
            w.U8((byte)(sbyte)(i == k - 1 ? -segVal : segVal));   // int8 segment (last negated)
            w.S16(0);                                              // int16 type = SingleScroll
            w.U8((byte)(sbyte)xStep);                             // int8 xStep
            w.U8((byte)(sbyte)yStep);                             // int8 yStep
            w.U8((byte)width);                                   // u8 width
            w.U8((byte)height);                                  // u8 height
        }
        return w.ToArray();
    }

    private static byte[] Emit(uint tag, List<Action<OtrResourceWriter>> cmds)
    {
        var w = new OtrResourceWriter(tag);
        w.U32((uint)cmds.Count);
        foreach (var c in cmds) c(w);
        return w.ToArray();
    }
}
