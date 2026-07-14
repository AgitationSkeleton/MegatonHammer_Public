using MegatonHammer.Editor;

namespace MegatonHammer.Export;

/// <summary>
/// Builds a Zelda 64 room binary (.zmap) from a ZRoom.
/// Segment 0x03 is used for all internal room-file pointers.
/// </summary>
public static class RoomExporter
{
    private const byte SEG = 0x03;

    public static byte[] Build(ZRoom room, Func<string, System.Drawing.Bitmap?>? texResolver = null,
                               Func<ushort, ushort?>? objResolver = null, bool n64Hw = true,
                               SceneSettings? lighting = null, bool mm = false,
                               IReadOnlyList<TextureScroll>? scrolls = null, bool waterScroll = false,
                               bool scrollXluOnly = false)
    {
        var rs = room.Settings;
        // Transition actors → scene 0x0E list; editor-only props (dummy Link) are never compiled.
        var actors       = room.Actors.Where(a => !a.IsTransitionActor && !a.IsEditorOnly).ToList();
        bool hasActors   = actors.Count > 0;
        int  numActors   = actors.Count;

        // Object-dependency list (0x0B): the distinct objects this room's actors need, so they load
        // and spawn in-game (without it, any actor not in gameplay_keep silently fails / crashes).
        var objIds = ObjectIdsFor(actors, objResolver, mm);
        bool hasObjects = objIds.Count > 0;

        // ── Pass 1: compute fixed-layout offsets ──────────────────────────
        // Header commands: 6 always-present — behavior(0x08), time(0x10), skybox(0x12), echo(0x16),
        // mesh(0x0A), end(0x14) — plus the optional actor(0x01) and object(0x0B) lists.
        // (The end command was previously uncounted, leaving headerSize 8 bytes short and shifting all
        // room data — a latent off-by-8 that corrupted actors in from-scratch rooms.)
        int numCmds    = 6 + (hasActors ? 1 : 0) + (hasObjects ? 1 : 0);
        int headerSize = numCmds * 8;                // always a multiple of 8

        int actorListOff  = headerSize;              // 8-aligned
        int actorListSize = numActors * 16;          // multiple of 8

        int meshHdrOff    = actorListOff + actorListSize; // already 8-aligned
        int shapeEntryOff = meshHdrOff + 12;         // immediately after 12-byte MeshHeader
        int vtxDataOff    = shapeEntryOff + 8;       // immediately after 8-byte RoomShapeEntry

        // Build DL first (needs vtxDataOff for internal seg pointers). Texture data lands between the
        // vertex block and the DL, so the DL starts after both.
        var dl        = DisplayListBuilder.Build(room, SEG, vtxDataOff, texResolver, n64Hw, lighting, scrolls, waterScroll, scrollXluOnly);
        bool hasGeom  = dl.VertexData.Length > 0;
        bool hasWater = dl.XluDlCommands.Length > 0;
        int  texOff   = hasGeom ? AlignUp(vtxDataOff + dl.VertexData.Length, 8) : 0;
        int  dlOff    = hasGeom ? AlignUp(texOff + dl.TextureData.Length, 8) : 0;
        int  xluOff   = hasWater ? AlignUp(dlOff + dl.DlCommands.Length, 8) : 0;
        int  afterDls = hasWater ? xluOff + dl.XluDlCommands.Length : dlOff + dl.DlCommands.Length;
        int  meshEnd  = hasGeom ? AlignUp(afterDls, 4) : vtxDataOff;
        int  objListOff = hasObjects ? AlignUp(meshEnd, 4) : 0;   // object id array, appended at the end

        // ── Pass 2: write binary ──────────────────────────────────────────
        var w = new N64BinaryWriter();

        // ── Room header commands ──────────────────────────────────────────
        // 0x08: Room behavior  [08 type 00 00 | 00 00 showInvis 00]
        w.WriteU8(0x08); w.WriteU8(rs.BehaviorType); w.WriteU16(0);
        w.WriteU8(0); w.WriteU8(0); w.WriteU8((byte)(rs.ShowInvisibleActors ? 1 : 0)); w.WriteU8(0);

        // 0x10: Time settings  [10 00 00 00 | timeHi timeLo speed 00]
        w.WriteU8(0x10); w.WriteU8(0); w.WriteU16(0);
        w.WriteU16(rs.TimeOverride); w.WriteU8(rs.TimeSpeed); w.WriteU8(0);

        // 0x12: Skybox modifier [12 00 00 00 | disableSky disableSunMoon 00 00]
        w.WriteU8(0x12); w.WriteU8(0); w.WriteU16(0);
        w.WriteU8((byte)(rs.DisableSkybox ? 1 : 0));
        w.WriteU8((byte)(rs.DisableSunMoon ? 1 : 0));
        w.WriteU16(0);

        // 0x16: Sound/echo settings [16 00 00 00 | 00 00 00 echo]
        w.WriteU8(0x16); w.WriteU8(0); w.WriteU16(0);
        w.WriteU8(0); w.WriteU8(0); w.WriteU8(0); w.WriteU8(rs.Echo);

        // 0x0A: Set mesh header
        w.WriteU8(0x0A); w.WriteU8(0x00); w.WriteU16(0);
        w.WriteSegPtr(SEG, meshHdrOff);

        // 0x01: Actor list (only if actors present)
        if (hasActors)
        {
            w.WriteU8(0x01);
            w.WriteU8((byte)numActors);
            w.WriteU16(0);
            w.WriteSegPtr(SEG, actorListOff);
        }

        // 0x0B: Object dependency list (only if needed)
        if (hasObjects)
        {
            w.WriteU8(0x0B);
            w.WriteU8((byte)objIds.Count);
            w.WriteU16(0);
            w.WriteSegPtr(SEG, objListOff);
        }

        // 0x14: End of header
        w.WriteU64(0x1400000000000000UL);

        // ── Actor entries (16 bytes each) ─────────────────────────────────
        foreach (var a in actors)
        {
            w.WriteU16((ushort)(a.Number | a.IdFlags));   // IdFlags is 0 for OoT; carries MM spawn-condition bits
            w.WriteS16((short)a.XPos);
            w.WriteS16((short)a.YPos);
            w.WriteS16((short)a.ZPos);
            w.WriteS16(MmRot(a.XRot, mm, a.IdFlags, 0x4000));
            w.WriteS16(MmRot(a.YRot, mm, a.IdFlags, 0x8000));
            w.WriteS16(MmRot(a.ZRot, mm, a.IdFlags, 0x2000));
            w.WriteU16(ActorExportFix.Variable(mm, a.Number, a.Variable, lighting?.Dungeon ?? false));
        }

        // ── MeshHeader (type 0, 12 bytes) ─────────────────────────────────
        // MeshType=0 (no culling), numEntries=1, startPtr, endPtr
        w.WriteU8(0x00);                               // type = 0
        w.WriteU8(0x01);                               // numEntries
        w.WriteU16(0);                                 // pad
        w.WriteSegPtr(SEG, shapeEntryOff);             // startPtr
        w.WriteSegPtr(SEG, shapeEntryOff + 8);         // endPtr (1 × 8 bytes)

        // ── RoomShapeEntry (8 bytes: opaPtr + xluPtr) ────────────────────
        if (hasGeom) w.WriteSegPtr(SEG, dlOff);
        else         w.WriteU32(0);                    // opaPtr = null
        if (hasWater) w.WriteSegPtr(SEG, xluOff);      // xluPtr → translucent water DL
        else          w.WriteU32(0);                   // xluPtr = null

        // ── Vertex data ───────────────────────────────────────────────────
        w.WriteBytes(dl.VertexData);

        // ── Texture data (RGBA16, 8-aligned) then display list(s) ──────────
        if (hasGeom)
        {
            if (dl.TextureData.Length > 0) { w.AlignTo(8); w.WriteBytes(dl.TextureData); }
            w.AlignTo(8);
            w.WriteBytes(dl.DlCommands);
            if (hasWater) { w.AlignTo(8); w.WriteBytes(dl.XluDlCommands); }
        }

        // ── Object dependency id array (u16 each), at objListOff ───────────
        if (hasObjects) { w.AlignTo(4); foreach (var id in objIds) w.WriteU16(id); }

        return w.ToArray();
    }

    // Distinct object ids the room's actors require (excludes those the resolver maps to null — e.g.
    // gameplay_keep actors, or actors with no object). Capped at 255 (the 0x0B count is a u8).
    private static List<ushort> ObjectIdsFor(IReadOnlyList<ZActor> actors, Func<ushort, ushort?>? objResolver, bool mm = false)
    {
        if (objResolver == null) return [];
        var ids = new List<ushort>();
        void Add(ushort id) { if (id != 0 && !ids.Contains(id) && ids.Count < 255) ids.Add(id); }
        foreach (var a in actors)
        {
            if (objResolver(a.Number) is { } id) Add(id);
            Add(ActorExportFix.ExtraRoomObject(mm, a.Number, a.Variable));   // spawn fix-ups (pot→OBJECT_TSUBO, heart→GI_HEART)
        }
        return ids;
    }

    // ── Setups (alternate room headers, 0x18) ─────────────────────────────
    // Builds a room binary with one header per setup, sharing a single mesh: the primary header
    // (setup 0) carries a 0x18 command pointing to an alt-header list; layer i selects header i's
    // actor list. perSetupActors[i] = setup i's actors for this room (transitions excluded here).
    // Each header (primary + alternates) emits its own 0x0B object-dependency list, so an actor unique to
    // one setup still gets its object loaded. Header sizes therefore vary (only headers with objects carry
    // 0x0B; only the primary carries 0x18), so offsets are computed per-header rather than from a constant.
    public static byte[] BuildWithSetups(ZRoom room, IReadOnlyList<IReadOnlyList<ZActor>> perSetupActors,
                                         Func<string, System.Drawing.Bitmap?>? texResolver = null,
                                         Func<ushort, ushort?>? objResolver = null, bool n64Hw = true, SceneSettings? lighting = null,
                                         bool mm = false, bool waterScroll = false)
    {
        var rs = room.Settings;
        int n = perSetupActors.Count;
        var lists = perSetupActors.Select(l => l.Where(a => !a.IsTransitionActor && !a.IsEditorOnly).ToList()).ToList();

        // Per-setup object ids (empty when no resolver → no 0x0B, identical to the pre-0x0B layout).
        var objIdsPer = lists.Select(l => ObjectIdsFor(l, objResolver, mm)).ToList();

        // Header command count: behavior,time,skybox,echo,mesh,actor + end = 7; +1 for the primary's 0x18;
        // +1 for any header that carries a 0x0B object list.
        int HdrCmds(int i) => 7 + (i == 0 ? 1 : 0) + (objIdsPer[i].Count > 0 ? 1 : 0);
        var hdrOff = new int[n];
        { int o = 0; for (int i = 0; i < n; i++) { hdrOff[i] = o; o += HdrCmds(i) * 8; } }
        int hdrsSize = n > 0 ? hdrOff[n - 1] + HdrCmds(n - 1) * 8 : 0;

        int altListOff = hdrsSize;                    // N × u32 segment pointers
        int cur = altListOff + n * 4;
        var actorOff = new int[n];
        for (int i = 0; i < n; i++) { actorOff[i] = cur; cur += lists[i].Count * 16; }
        int meshHdrOff    = AlignUp(cur, 8);
        int shapeEntryOff = meshHdrOff + 12;
        int vtxDataOff    = shapeEntryOff + 8;

        var dl       = DisplayListBuilder.Build(room, SEG, vtxDataOff, texResolver, n64Hw, lighting, null, waterScroll);   // multi-setup brush scroll: TODO
        bool hasGeom = dl.VertexData.Length > 0;
        bool hasWater = dl.XluDlCommands.Length > 0;
        int texOff   = hasGeom ? AlignUp(vtxDataOff + dl.VertexData.Length, 8) : 0;
        int dlOff    = hasGeom ? AlignUp(texOff + dl.TextureData.Length, 8) : 0;
        int xluOff   = hasWater ? AlignUp(dlOff + dl.DlCommands.Length, 8) : 0;
        int afterDls = hasWater ? xluOff + dl.XluDlCommands.Length : dlOff + dl.DlCommands.Length;
        int meshEnd  = hasGeom ? AlignUp(afterDls, 4) : vtxDataOff;

        // Object id arrays land after the mesh; one per setup that needs objects (4-aligned u16 arrays).
        var objListOff = new int[n];
        { int oc = AlignUp(meshEnd, 4);
          for (int i = 0; i < n; i++)
          {
              if (objIdsPer[i].Count > 0) { objListOff[i] = oc; oc = AlignUp(oc + objIdsPer[i].Count * 2, 4); }
              else objListOff[i] = -1;
          } }

        var w = new N64BinaryWriter();
        for (int i = 0; i < n; i++)
            WriteRoomHeader(w, rs, meshHdrOff, actorOff[i], lists[i].Count,
                            i == 0 ? altListOff : -1, objListOff[i], objIdsPer[i].Count);   // primary carries 0x18

        // 0x18 list: layer 0 → primary (NULL falls back to the base header), layer i → alt header i.
        for (int i = 0; i < n; i++)
            w.WriteU32(i == 0 ? 0u : (uint)((SEG << 24) | hdrOff[i]));

        foreach (var list in lists)
            foreach (var a in list) WriteActor(w, a, mm, lighting?.Dungeon ?? false);

        w.AlignTo(8);   // → meshHdrOff
        w.WriteU8(0x00); w.WriteU8(0x01); w.WriteU16(0);
        w.WriteSegPtr(SEG, shapeEntryOff); w.WriteSegPtr(SEG, shapeEntryOff + 8);
        if (hasGeom) w.WriteSegPtr(SEG, dlOff); else w.WriteU32(0);
        if (hasWater) w.WriteSegPtr(SEG, xluOff); else w.WriteU32(0);   // xluPtr → translucent water DL
        w.WriteBytes(dl.VertexData);
        if (hasGeom)
        {
            if (dl.TextureData.Length > 0) { w.AlignTo(8); w.WriteBytes(dl.TextureData); }
            w.AlignTo(8); w.WriteBytes(dl.DlCommands);
            if (hasWater) { w.AlignTo(8); w.WriteBytes(dl.XluDlCommands); }
        }

        // Object-dependency arrays (one per setup that needs objects) — pointed at by each header's 0x0B.
        for (int i = 0; i < n; i++)
            if (objIdsPer[i].Count > 0) { w.AlignTo(4); foreach (var id in objIdsPer[i]) w.WriteU16(id); }

        return w.ToArray();
    }

    // Writes one room header's command list. altListOff < 0 omits the 0x18 (alt-headers) command;
    // objCount <= 0 omits the 0x0B (object-dependency list) command.
    private static void WriteRoomHeader(N64BinaryWriter w, RoomSettings rs, int meshHdrOff, int actorListOff, int numActors,
                                        int altListOff, int objListOff = -1, int objCount = 0)
    {
        w.WriteU8(0x08); w.WriteU8(rs.BehaviorType); w.WriteU16(0);
        w.WriteU8(0); w.WriteU8(0); w.WriteU8((byte)(rs.ShowInvisibleActors ? 1 : 0)); w.WriteU8(0);
        w.WriteU8(0x10); w.WriteU8(0); w.WriteU16(0);
        w.WriteU16(rs.TimeOverride); w.WriteU8(rs.TimeSpeed); w.WriteU8(0);
        w.WriteU8(0x12); w.WriteU8(0); w.WriteU16(0);
        w.WriteU8((byte)(rs.DisableSkybox ? 1 : 0)); w.WriteU8((byte)(rs.DisableSunMoon ? 1 : 0)); w.WriteU16(0);
        w.WriteU8(0x16); w.WriteU8(0); w.WriteU16(0);
        w.WriteU8(0); w.WriteU8(0); w.WriteU8(0); w.WriteU8(rs.Echo);
        w.WriteU8(0x0A); w.WriteU8(0); w.WriteU16(0); w.WriteSegPtr(SEG, meshHdrOff);
        w.WriteU8(0x01); w.WriteU8((byte)numActors); w.WriteU16(0); w.WriteSegPtr(SEG, actorListOff);
        if (objCount > 0) { w.WriteU8(0x0B); w.WriteU8((byte)objCount); w.WriteU16(0); w.WriteSegPtr(SEG, objListOff); }
        if (altListOff >= 0) { w.WriteU8(0x18); w.WriteU8(0); w.WriteU16(0); w.WriteSegPtr(SEG, altListOff); }
        w.WriteU64(0x1400000000000000UL);
    }

    private static void WriteActor(N64BinaryWriter w, ZActor a, bool mm, bool isDungeon = false)
    {
        w.WriteU16((ushort)(a.Number | a.IdFlags));   // IdFlags carries MM spawn-condition bits (0 for OoT)
        w.WriteS16((short)a.XPos); w.WriteS16((short)a.YPos); w.WriteS16((short)a.ZPos);
        w.WriteS16(MmRot(a.XRot, mm, a.IdFlags, 0x4000));
        w.WriteS16(MmRot(a.YRot, mm, a.IdFlags, 0x8000));
        w.WriteS16(MmRot(a.ZRot, mm, a.IdFlags, 0x2000));
        w.WriteU16(ActorExportFix.Variable(mm, a.Number, a.Variable, isDungeon));
    }

    // MM's Actor_SpawnEntry reads the actor-entry rotation as DEGREES in the high 9 bits and packs csId
    // (rot.y) / halfDaysBits (rot.x,rot.z) into the low bits — NOT a plain binary angle like OoT. A raw
    // angle there sets garbage halfDaysBits so the actor never spawns (e.g. a chest at yaw 0xC000). Encode
    // (deg & 0x1FF) << 7, low bits 0 (halfDaysBits 0 = always spawn, csId 0). OoT keeps the raw angle.
    //
    // axisFlag (0x4000 rotX / 0x2000 rotZ / 0x8000 rotY): when set in IdFlags the editor has already
    // packed a half-day mask / csId into this field's low bits, so write it verbatim — re-deriving
    // degrees would wipe those bits (and the id flag tells MM the high-9 value is a raw binang anyway).
    private static short MmRot(short binAngle, bool mm, ushort idFlags = 0, ushort axisFlag = 0)
    {
        if (!mm) return binAngle;
        if (axisFlag != 0 && (idFlags & axisFlag) != 0) return binAngle;   // packed mask/csId — preserve
        int deg = (int)MathF.Round((binAngle & 0xFFFF) * 360f / 65536f);
        deg = ((deg % 360) + 360) % 360;
        return (short)((deg & 0x1FF) << 7);
    }

    private static int AlignUp(int v, int align) => (v + align - 1) & ~(align - 1);
}
