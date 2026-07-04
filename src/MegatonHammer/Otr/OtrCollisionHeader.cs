using MegatonHammer.Editor;
using MegatonHammer.Textures;
using OpenTK.Mathematics;

namespace MegatonHammer.Otr;

/// <summary>
/// Builds a level's collision as an OCOL (CollisionHeader) resource for SoH/2Ship. Mirrors
/// the raw-N64 <see cref="MegatonHammer.Export.CollisionBuilder"/> triangulation, but emits
/// the libultraship resource layout (separate length-prefixed arrays, little-endian, no
/// segment pointers — the runtime rebuilds pointers from the arrays it reads).
/// </summary>
public static class OtrCollisionHeader
{
    private record struct ColVtx(short X, short Y, short Z);
    private record struct ColPoly(ushort Type, ushort VtxA, ushort VtxB, ushort VtxC,
                                  short NormX, short NormY, short NormZ, short Dist, int IgnoreFlags);
    private record struct WaterBox(short XMin, short YSurface, short ZMin, short XLen, short ZLen, int Properties);

    // OoT waterbox.properties: room index lives in bits 13-18; 0x3F = "all rooms".
    private const int WaterAllRooms = 0x3F << 13;

    // CollisionPoly.flags_vIA top 3 bits (0xE000) are the ignore flags:
    // 1 = ignore camera, 2 = ignore actors/entities, 4 = ignore projectiles.
    private const int IgnoreCamera = 1, IgnoreEntity = 2, IgnoreProjectiles = 4;

    /// <param name="solidExit">Maps a trigger brush to its 1-based exit index (so its collision
    /// polygons carry the exit index in their surface type); null/absent = no exit.</param>
    public static byte[] Build(ZScene scene, IReadOnlyDictionary<Solid, int>? solidExit = null)
    {
        var vtxDict = new Dictionary<ColVtx, int>();
        var vtxList = new List<ColVtx>();
        var polyList = new List<ColPoly>();
        var waterBoxes = new List<WaterBox>();
        // Exit indices occupy surface types 0..maxExit; #7 void/lava faces get extra types appended after
        // them, deduplicated by their (data0,data1) bits. Precompute maxExit so those indices are stable.
        int maxExit = solidExit is { Count: > 0 } ? solidExit.Values.Max() : 0;
        var customTypes = new List<(uint d0, uint d1)>();
        var customTypeIdx = new Dictionary<(uint, uint), int>();
        int CustomSurfaceType(uint d0, uint d1)
        {
            if (!customTypeIdx.TryGetValue((d0, d1), out int idx))
            {
                idx = maxExit + 1 + customTypes.Count;
                customTypes.Add((d0, d1));
                customTypeIdx[(d0, d1)] = idx;
            }
            return idx;
        }

        foreach (var room in scene.Rooms)
        foreach (var solid in room.Geometry)
        {
            // A water brush (the IsWater flag, or any WATERBOX-textured face) becomes a swimmable waterbox.
            if (solid.IsWater || solid.Faces.Any(f => SpecialTextures.Classify(f.TextureName).HasFlag(SpecialKind.WaterSurface)))
            {
                if (TryWaterBox(solid, out var wb)) waterBoxes.Add(wb);
                continue;
            }
            if (solid.NoCollision) continue;   // solidity: non-solid brush renders but emits no collision

            // A trigger brush's polygons carry an exit index (surface type = the exit index).
            int exitIdx = solidExit?.GetValueOrDefault(solid) ?? 0;

            foreach (var face in solid.Faces)
            {
                var verts = face.Vertices;
                if (verts.Count < 3) continue;

                // CLIP = player wall projectiles pass through (ignore projectiles + camera).
                // BLOCKPROJECTILE / NODRAW remain fully solid (blocks all). WATERBOX handled above.
                var kind = SpecialTextures.Classify(face.TextureName);
                int ignore = kind.HasFlag(SpecialKind.PlayerClip) ? IgnoreProjectiles | IgnoreCamera : 0;
                // #7: a void/lava face gets its own surface type (void-out/soft-void FloorProperty, lava
                // Material); otherwise the poly's type IS the exit index.
                var surfBits = SpecialTextures.SurfaceBits(face.TextureName);
                int polyType = surfBits is { } sb2 ? CustomSurfaceType(sb2.data0, sb2.data1) : exitIdx;

                Vector3 n = face.Plane.Normal;
                short nx = (short)MathF.Round(n.X * 32767f);
                short ny = (short)MathF.Round(n.Y * 32767f);
                short nz = (short)MathF.Round(n.Z * 32767f);

                for (int i = 1; i < verts.Count - 1; i++)
                {
                    var tri = OrderTri(Snap(verts[0]), Snap(verts[i]), Snap(verts[i + 1]), n);
                    int ia = GetOrAdd(vtxDict, vtxList, tri.a);
                    int ib = GetOrAdd(vtxDict, vtxList, tri.b);
                    int ic = GetOrAdd(vtxDict, vtxList, tri.c);
                    long dot = (long)nx * tri.a.X + (long)ny * tri.a.Y + (long)nz * tri.a.Z;
                    // Surface type index = the exit index (entry k holds exit index k); 0 = default.
                    // Plane distance MUST match the N64 CollisionBuilder formula (-dot/32767): BgCheck's
                    // floor raycast uses it to compute the hit Y, and the old `dot>>14` (wrong sign + ~2x
                    // scale) put the floor plane at the wrong height, so Link fell through it / voided out.
                    polyList.Add(new ColPoly((ushort)polyType, (ushort)ia, (ushort)ib, (ushort)ic, nx, ny, nz, (short)Math.Round(-(double)dot / 32767.0), ignore));
                }
            }
        }

        // #11/#32: imported OBJ-mesh triangles must ALSO become collision, exactly like the N64
        // CollisionBuilder does — otherwise an OBJ-imported level renders (OtrRoomGeometry draws ObjMesh)
        // but has NO floor in SoH/2Ship, so Link spawns and falls into the void forever. Honours the
        // #nocollision group tag (drawn but not solid); #nomesh tris are collision-only and kept.
        foreach (var room in scene.Rooms)
            if (room.ObjMesh is { } objMesh)
                foreach (var tri in objMesh.Tris)
                {
                    if (tri.NoCollision) continue;
                    var n = Vector3.Cross(tri.P1 - tri.P0, tri.P2 - tri.P0);
                    if (n.LengthSquared < 1e-6f) continue;
                    n.Normalize();
                    short nx = (short)MathF.Round(n.X * 32767f);
                    short ny = (short)MathF.Round(n.Y * 32767f);
                    short nz = (short)MathF.Round(n.Z * 32767f);
                    var tdi = OrderTri(Snap(tri.P0), Snap(tri.P1), Snap(tri.P2), n);
                    int ia = GetOrAdd(vtxDict, vtxList, tdi.a);
                    int ib = GetOrAdd(vtxDict, vtxList, tdi.b);
                    int ic = GetOrAdd(vtxDict, vtxList, tdi.c);
                    long dot = (long)nx * tdi.a.X + (long)ny * tdi.a.Y + (long)nz * tdi.a.Z;
                    polyList.Add(new ColPoly(0, (ushort)ia, (ushort)ib, (ushort)ic, nx, ny, nz, (short)Math.Round(-(double)dot / 32767.0), 0));
                }

        short minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
        if (vtxList.Count > 0)
        {
            minX = maxX = vtxList[0].X; minY = maxY = vtxList[0].Y; minZ = maxZ = vtxList[0].Z;
            foreach (var v in vtxList)
            {
                if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
                if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
            }
        }

        var w = new OtrResourceWriter(0x4F434F4C /* OCOL */);
        w.S16(minX); w.S16(minY); w.S16(minZ);
        w.S16(maxX); w.S16(maxY); w.S16(maxZ);

        w.S32(vtxList.Count);
        foreach (var v in vtxList) { w.S16(v.X); w.S16(v.Y); w.S16(v.Z); }

        w.U32((uint)polyList.Count);
        foreach (var p in polyList)
        {
            w.U16(p.Type);
            // flags_vIA: low 13 bits = vertex index, top 3 bits (0xE000) = ignore flags.
            w.U16((ushort)((p.VtxA & 0x1FFF) | ((p.IgnoreFlags & 7) << 13)));
            w.U16(p.VtxB); w.U16(p.VtxC);
            w.U16((ushort)p.NormX); w.U16((ushort)p.NormY); w.U16((ushort)p.NormZ);
            w.U16((ushort)p.Dist);
        }

        // Surface types: entry 0 = default (no flags); entry k = exit index k in data[0] bits [12:8],
        // so a poly with Type k triggers exit k. #7: custom void/lava types follow (their FloorProperty /
        // Material bits). Note the reader takes data[1] then data[0].
        w.U32((uint)(maxExit + 1 + customTypes.Count));
        for (int k = 0; k <= maxExit; k++)
        {
            w.U32(0);                          // data[1]
            w.U32(k == 0 ? 0u : (uint)(k << 8)); // data[0]: exit index in bits [12:8]
        }
        foreach (var (d0, d1) in customTypes)
        {
            w.U32(d1);   // data[1]: Material (lava) etc.
            w.U32(d0);   // data[0]: FloorProperty (void) etc.
        }

        // Camera data: emit ONE inert entry. Every surface type references camera-data index 0
        // (low byte of data[0] = 0), so the list must have at least one element — an empty list
        // made the engine null-deref cameraDataList[0] in the camera/bgcheck code on the first
        // frame after the scene loaded. cameraSType 0 (= CAM_SET_NONE) makes the camera fall back
        // to its default behaviour. Layout per entry: cameraSType u16, numCameras s16, camPosIdx s32.
        w.U32(1);                          // camDataCount = 1
        w.U16(0); w.S16(0); w.S32(-1);     // CamData[0]: sType=CAM_SET_NONE, 0 cameras, no posData
        w.S32(0);                          // camPosCount

        w.S32(waterBoxes.Count);
        foreach (var wb in waterBoxes)
        {
            w.S16(wb.XMin); w.S16(wb.YSurface); w.S16(wb.ZMin);
            w.S16(wb.XLen); w.S16(wb.ZLen);
            w.S32(wb.Properties);
        }

        return w.ToArray();
    }

    // A water brush → a waterbox covering its XZ footprint, surface at the brush's top.
    private static bool TryWaterBox(Solid solid, out WaterBox wb)
    {
        wb = default;
        bool any = false;
        float minX = 0, minZ = 0, maxX = 0, maxZ = 0, top = 0;
        foreach (var face in solid.Faces)
            foreach (var v in face.Vertices)
            {
                if (!any) { minX = maxX = v.X; minZ = maxZ = v.Z; top = v.Y; any = true; }
                if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
                if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
                if (v.Y > top) top = v.Y;
            }
        if (!any) return false;

        wb = new WaterBox(
            (short)MathF.Round(minX), (short)MathF.Round(top), (short)MathF.Round(minZ),
            (short)MathF.Round(maxX - minX), (short)MathF.Round(maxZ - minZ),
            WaterAllRooms);
        return true;
    }

    // Orders a triangle's vertices the way OoT's collision expects (fast64): the minimum-Y
    // vertex first (avoids a CollisionPoly_GetMinY edge case), then counter-clockwise around
    // the surface normal (required for correct dynapoly behaviour).
    private static (ColVtx a, ColVtx b, ColVtx c) OrderTri(ColVtx v0, ColVtx v1, ColVtx v2, Vector3 normal)
    {
        // Rotate so the lowest-Y vertex leads (preserves winding).
        if (v1.Y < v0.Y && v1.Y <= v2.Y) (v0, v1, v2) = (v1, v2, v0);
        else if (v2.Y < v0.Y && v2.Y < v1.Y) (v0, v1, v2) = (v2, v0, v1);

        // Ensure CCW around the normal; swap the trailing pair if the cross faces away.
        var e1 = new Vector3(v1.X - v0.X, v1.Y - v0.Y, v1.Z - v0.Z);
        var e2 = new Vector3(v2.X - v0.X, v2.Y - v0.Y, v2.Z - v0.Z);
        if (Vector3.Dot(Vector3.Cross(e1, e2), normal) < 0f) (v1, v2) = (v2, v1);
        return (v0, v1, v2);
    }

    private static ColVtx Snap(Vector3 p) => new((short)MathF.Round(p.X), (short)MathF.Round(p.Y), (short)MathF.Round(p.Z));

    private static int GetOrAdd(Dictionary<ColVtx, int> d, List<ColVtx> l, ColVtx v)
    {
        if (d.TryGetValue(v, out int i)) return i;
        i = l.Count; l.Add(v); d[v] = i; return i;
    }
}
