using MegatonHammer.Editor;
using OpenTK.Mathematics;

namespace MegatonHammer.Export;

/// <summary>
/// Converts all ZScene room geometry into the Zelda 64 collision binary block
/// (CollisionHeader + vertex array + polygon array + surface type table).
/// The returned byte array starts with the CollisionHeader at offset 0.
/// All internal segment pointers are pre-resolved using the caller-supplied
/// <paramref name="seg"/> and <paramref name="headerOffset"/> parameters.
/// </summary>
public static class CollisionBuilder
{
    private record struct ColVtx(short X, short Y, short Z);

    private record struct ColPoly(
        ushort TypeIdx,
        ushort VtxA, ushort VtxB, ushort VtxC,
        short NormX, short NormY, short NormZ,
        short Dist);

    /// <summary>The entrance each exit-trigger brush warps to, in collision-poly index order.
    /// Element i is exit index i+1; this is what the scene's 0x13 exit list must contain.</summary>
    public static List<ushort> ExitEntrances(ZScene scene) =>
        TriggerSolids(scene).Select(s => (ushort)s.ExitEntrance).ToList();

    // A brush is a warp trigger if its IsTrigger flag is set (Warp Properties dialog) OR any face wears the
    // WARP tool texture — the same texture-OR-flag pattern as WATERBOX. Both carry the brush's ExitEntrance.
    internal static bool IsWarpTrigger(Solid s) =>
        s.IsTrigger || s.Faces.Any(f => Textures.SpecialTextures.Classify(f.TextureName).HasFlag(Textures.SpecialKind.Warp));

    private static List<Solid> TriggerSolids(ZScene scene) =>
        scene.Rooms.SelectMany(r => r.Geometry).Where(IsWarpTrigger).ToList();

    public static byte[] Build(ZScene scene, byte seg, int headerOffset)
    {
        // ── 1. Triangulate all geometry across every room ──────────────────
        var vtxDict = new Dictionary<ColVtx, int>();
        var vtxList = new List<ColVtx>();
        var polyList = new List<ColPoly>();

        // Surface-type table: every distinct (data[0], data[1]) used by a brush, so brushes export their
        // authored surface behaviour (floor void/damage, climbable/hookshot walls, conveyors, sound,
        // camera…) instead of all-default floor. Entry 0 is always plain floor (0,0). Exit-trigger
        // brushes OR a 1-based exit index into data[0] bits 8-12 so the walk-into warp fires; the
        // scene's 0x13 list maps that index to the entrance.
        var triggers = TriggerSolids(scene);
        var triggerIdx = new Dictionary<Solid, int>();
        for (int i = 0; i < triggers.Count; i++) triggerIdx[triggers[i]] = i + 1;

        var surfTypes = new List<(uint d0, uint d1)> { (0, 0) };
        var surfTypeIndex = new Dictionary<(uint, uint), int> { [(0, 0)] = 0 };
        ushort SurfTypeFor(Solid solid, SolidFace face)
        {
            uint d0 = solid.SurfaceData0, d1 = solid.SurfaceData1;
            if (triggerIdx.TryGetValue(solid, out int ex)) d0 |= (uint)((ex & 0x1F) << 8);   // exit index → data[0] bits 8-12
            // #7: void/lava tool textures on this face OR in their FloorProperty (void) / Material (lava) bits.
            if (Textures.SpecialTextures.SurfaceBits(face.TextureName) is { } sb) { d0 |= sb.data0; d1 |= sb.data1; }
            var key = (d0, d1);
            if (!surfTypeIndex.TryGetValue(key, out int idx))
            { idx = surfTypes.Count; surfTypes.Add(key); surfTypeIndex[key] = idx; }
            return (ushort)idx;
        }

        // Water brushes become WaterBoxes (not solid collision); their top face is the surface.
        var waterBoxes = new List<(short xMin, short ySurf, short zMin, short xLen, short zLen, int room)>();

        foreach (var room in scene.Rooms)
        foreach (var solid in room.Geometry)
        {
            if (IsWaterBrush(solid)) { waterBoxes.Add(WaterBoxOf(solid)); continue; }
            if (solid.NoCollision) continue;   // solidity: non-solid brush renders but emits no collision
            // A warp trigger is emitted as a NON-BLOCKING exit floor (below), not as its solid box: OoT fires a
            // scene exit only from the FLOOR poly the player stands on (data[0] bits 8-12), never from a wall
            // (z_player.c func_80839034). Emitting the box's vertical faces made the trigger a solid wall you
            // bumped into and never warped through — so skip them here and lay down a walk-over floor instead.
            if (triggerIdx.ContainsKey(solid)) continue;
            foreach (var face in solid.Faces)
        {
            var verts = face.Vertices;
            if (verts.Count < 3) continue;

            ushort typeIdx = SurfTypeFor(solid, face);

            // Normal from face plane (already unit-length from Solid.ComputeFaces)
            Vector3 n = face.Plane.Normal;
            short normX = (short)MathF.Round(n.X * 32767f);
            short normY = (short)MathF.Round(n.Y * 32767f);
            short normZ = (short)MathF.Round(n.Z * 32767f);

            for (int i = 1; i < verts.Count - 1; i++)
            {
                var va = SnapVtx(verts[0]);
                var vb = SnapVtx(verts[i]);
                var vc = SnapVtx(verts[i + 1]);

                int ia = GetOrAdd(vtxDict, vtxList, va);
                int ib = GetOrAdd(vtxDict, vtxList, vb);
                int ic = GetOrAdd(vtxDict, vtxList, vc);

                // Plane distance: the collision plane is (N·p)/32767 + dist == 0 for p on the plane,
                // so dist = -(N_Q15 · vertex) / 32767 (negated, and 1/32767 not the earlier >>14 which
                // both skipped the negation and doubled the magnitude → walls placed at the wrong
                // position so BgCheck never collided with them; floors at Y=0 gave dist 0 either way).
                long dot = (long)normX * va.X + (long)normY * va.Y + (long)normZ * va.Z;
                short dist = (short)Math.Round(-(double)dot / 32767.0);

                polyList.Add(new ColPoly(typeIdx, (ushort)ia, (ushort)ib, (ushort)ic,
                                         normX, normY, normZ, dist));
            }
        }
        }

        // ── Warp-trigger floors: a flat, upward-facing exit floor across each trigger brush's footprint, a
        // hair above its base, carrying the exit index. Walking onto it (floorPoly gains a nonzero exit index)
        // fires the entrance — the vanilla loading-zone mechanism — WITHOUT the box's walls blocking you. Sat a
        // couple units above the brush base so it reliably wins BgCheck's floor pick over an arena floor the
        // trigger rests on. ──
        foreach (var solid in triggers)
        {
            var (mn, mx) = solid.GetAABB();
            short y = (short)MathF.Round(mn.Y + 2f);
            // Surface type = base surface bits + this trigger's 1-based exit index in data[0] bits 8-12.
            uint d0 = solid.SurfaceData0 | (uint)((triggerIdx[solid] & 0x1F) << 8);
            var key = (d0, solid.SurfaceData1);
            if (!surfTypeIndex.TryGetValue(key, out int tIdx))
            { tIdx = surfTypes.Count; surfTypes.Add(key); surfTypeIndex[key] = tIdx; }
            var p0 = new Vector3(mn.X, y, mn.Z); var p1 = new Vector3(mx.X, y, mn.Z);
            var p2 = new Vector3(mx.X, y, mx.Z); var p3 = new Vector3(mn.X, y, mx.Z);
            void FloorTri(Vector3 a, Vector3 b, Vector3 c)
            {
                var va = SnapVtx(a); var vb = SnapVtx(b); var vc = SnapVtx(c);
                int ia = GetOrAdd(vtxDict, vtxList, va), ib = GetOrAdd(vtxDict, vtxList, vb), ic = GetOrAdd(vtxDict, vtxList, vc);
                short dist = (short)Math.Round(-(double)(32767 * va.Y) / 32767.0);   // upward normal (0,1,0) → dist = -y
                polyList.Add(new ColPoly((ushort)tIdx, (ushort)ia, (ushort)ib, (ushort)ic, 0, 32767, 0, dist));
            }
            // Wind CCW when viewed from above so the face normal points up (+Y).
            FloorTri(p0, p3, p2);
            FloorTri(p0, p2, p1);
        }

        // Imported OBJ mesh triangles also become collision, honouring the #nocollision group tag.
        foreach (var room in scene.Rooms)
            if (room.ObjMesh is { } objMesh)
                foreach (var tri in objMesh.Tris)
                {
                    if (tri.NoCollision) continue;
                    var va = SnapVtx(tri.P0); var vb = SnapVtx(tri.P1); var vc = SnapVtx(tri.P2);
                    var n = Vector3.Cross(tri.P1 - tri.P0, tri.P2 - tri.P0);
                    if (n.LengthSquared < 1e-6f) continue;
                    n.Normalize();
                    short nx = (short)MathF.Round(n.X * 32767f), ny = (short)MathF.Round(n.Y * 32767f), nz = (short)MathF.Round(n.Z * 32767f);
                    int ia = GetOrAdd(vtxDict, vtxList, va), ib = GetOrAdd(vtxDict, vtxList, vb), ic = GetOrAdd(vtxDict, vtxList, vc);
                    long dot = (long)nx * va.X + (long)ny * va.Y + (long)nz * va.Z;
                    polyList.Add(new ColPoly(0, (ushort)ia, (ushort)ib, (ushort)ic, nx, ny, nz,
                                             (short)Math.Round(-(double)dot / 32767.0)));
                }

        int numSurfTypes = surfTypes.Count;

        int numVerts = vtxList.Count;
        int numPolys = polyList.Count;

        // ── 2. Compute axis-aligned bounding box ──────────────────────────
        short minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
        if (numVerts > 0)
        {
            minX = maxX = vtxList[0].X;
            minY = maxY = vtxList[0].Y;
            minZ = maxZ = vtxList[0].Z;
            foreach (var v in vtxList)
            {
                if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
                if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
            }
        }

        // ── 3. Compute sub-block offsets ──────────────────────────────────
        // Layout: [CollisionHeader 44B][vtxArray N*6B][polyArray M*16B][surfTypeTable 8B]
        const int HeaderSize = 44;
        int vtxArrayOffset  = headerOffset + HeaderSize;                    // always 2-aligned (44 is even)
        int polyArrayOffset = vtxArrayOffset + numVerts * 6;               // 6 is even → still 2-aligned
        int surfTypeOffset  = polyArrayOffset + numPolys * 16;             // 16-byte multiple → aligned
        int waterBoxOffset  = surfTypeOffset + numSurfTypes * 8;           // WaterBox list (16B each)
        int bgCamOffset     = waterBoxOffset + waterBoxes.Count * 16;      // one BgCamInfo (8B)

        // ── 4. Write binary ───────────────────────────────────────────────
        var w = new N64BinaryWriter();

        // ── CollisionHeader (44 bytes) ────────────────────────────────────
        w.WriteS16(minX); w.WriteS16(minY); w.WriteS16(minZ);  // minBounds
        w.WriteS16(maxX); w.WriteS16(maxY); w.WriteS16(maxZ);  // maxBounds
        w.WriteS16((short)numVerts);
        w.WriteU16(0);                                          // pad
        w.WriteSegPtr(seg, vtxArrayOffset);                     // vtxList*
        w.WriteS16((short)numPolys);
        w.WriteU16(0);                                          // pad
        w.WriteSegPtr(seg, polyArrayOffset);                    // polyList*
        w.WriteSegPtr(seg, surfTypeOffset);                     // surfaceTypeList*
        // bgCamList* must be non-NULL: every floor's surface type carries a bgCamIndex, and the camera
        // reads bgCamList[index].setting with NO null check (z_bgcheck.c BgCheck_GetBgCamSettingImpl)
        // right after Link spawns — a NULL list reads address 0 → TLB exception → the scene load hangs.
        // Point it at a single default BgCamInfo (emitted after the waterbox list).
        w.WriteSegPtr(seg, bgCamOffset);                        // bgCamList*
        w.WriteS16((short)waterBoxes.Count);                    // numWaterBoxes
        w.WriteU16(0);                                          // pad
        if (waterBoxes.Count > 0) w.WriteSegPtr(seg, waterBoxOffset); else w.WriteU32(0);  // waterBoxList*
        // Total so far: 6+6+2+2+4+2+2+4+4+4+2+2+4 = 44 ✓

        // ── Vertex array (N × 6 bytes) ────────────────────────────────────
        foreach (var v in vtxList)
        {
            w.WriteS16(v.X);
            w.WriteS16(v.Y);
            w.WriteS16(v.Z);
        }

        // ── Polygon array (M × 16 bytes) ─────────────────────────────────
        foreach (var p in polyList)
        {
            w.WriteU16(p.TypeIdx);
            w.WriteU16(p.VtxA);
            w.WriteU16(p.VtxB);
            w.WriteU16(p.VtxC);
            w.WriteS16(p.NormX);
            w.WriteS16(p.NormY);
            w.WriteS16(p.NormZ);
            w.WriteS16(p.Dist);
        }

        // ── Surface type table (numSurfTypes × 8 bytes: data[0], data[1]) ──
        foreach (var (d0, d1) in surfTypes) { w.WriteU32(d0); w.WriteU32(d1); }

        // ── WaterBox list (N × 16 bytes) ─────────────────────────────────
        foreach (var (xMin, ySurf, zMin, xLen, zLen, room) in waterBoxes)
        {
            w.WriteS16(xMin); w.WriteS16(ySurf); w.WriteS16(zMin);
            w.WriteS16(xLen); w.WriteS16(zLen); w.WriteU16(0);   // s16 lengths + pad to the u32 boundary
            // properties: room in bits 13–18, lightIndex 0x1F (none) in bits 8–12.
            w.WriteU32((uint)(((room & 0x3F) << 13) | (0x1F << 8)));
        }

        // ── bgCamList: one default BgCamInfo (8 bytes) ────────────────────
        // { u16 setting = CAM_SET_NORMAL0 (1); s16 count = 0; Vec3s* bgCamFuncData = NULL }.
        // Floors with bgCamIndex 0 resolve here; a normal free camera, no spline/fixed data.
        w.WriteU16(0x0001);   // CAM_SET_NORMAL0
        w.WriteS16(0);        // count
        w.WriteU32(0);        // bgCamFuncData = NULL

        return w.ToArray();
    }

    // A brush is treated as a WaterBox if it's explicitly flagged "Is water box" OR any of its faces uses
    // the WATERBOX special texture (SpecialKind.WaterSurface) — the design intent of that swatch is "this
    // brush is water". Without this, a brush the mapper painted with WATERBOX exported as SOLID collision
    // (walkable) instead of swimmable water. Several such brushes at the same surface Y form one coherent
    // in-game pool (the engine returns the first water box whose XZ contains the player; no merging needed).
    private static bool IsWaterBrush(Solid s) =>
        s.IsWater || s.Faces.Any(f => Textures.SpecialTextures.Classify(f.TextureName).HasFlag(Textures.SpecialKind.WaterSurface));

    // A water brush → WaterBox: its XZ bounding box and top (max-Y) surface.
    private static (short xMin, short ySurf, short zMin, short xLen, short zLen, int room) WaterBoxOf(Solid s)
    {
        float minX = 1e9f, minZ = 1e9f, maxX = -1e9f, maxZ = -1e9f, maxY = -1e9f;
        foreach (var v in s.GetUniqueVertices())
        {
            if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
            if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
            if (v.Y > maxY) maxY = v.Y;
        }
        return ((short)MathF.Round(minX), (short)MathF.Round(maxY), (short)MathF.Round(minZ),
                (short)MathF.Round(maxX - minX), (short)MathF.Round(maxZ - minZ), s.WaterRoom);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ColVtx SnapVtx(Vector3 p)
        => new((short)MathF.Round(p.X), (short)MathF.Round(p.Y), (short)MathF.Round(p.Z));

    private static int GetOrAdd(Dictionary<ColVtx, int> dict, List<ColVtx> list, ColVtx v)
    {
        if (dict.TryGetValue(v, out int idx)) return idx;
        idx = list.Count;
        list.Add(v);
        dict[v] = idx;
        return idx;
    }
}
