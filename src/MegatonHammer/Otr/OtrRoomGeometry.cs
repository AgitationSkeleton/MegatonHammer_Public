using System.Drawing;
using System.Drawing.Imaging;
using MegatonHammer.Editor;
using MegatonHammer.Textures;
using OpenTK.Mathematics;

namespace MegatonHammer.Otr;

/// <summary>
/// Builds a room's geometry as the libultraship resources SoH/2Ship's renderer consumes: an OVTX
/// vertex resource, an ODLT display list, and one OTEX texture resource per distinct brush texture.
/// The display list references the vertices/textures through OTR-hash commands (CRC64 of the
/// resource path), how SoH resolves cross-resource pointers at draw time.
///
/// Faces are grouped by texture: a textured group emits the standard F3DEX2 load-texture sequence
/// (SETTIMG_OTR_HASH + SETTILE/LOADBLOCK/SETTILESIZE) with a TEXEL0×SHADE combiner and per-vertex
/// UVs from <see cref="SolidFace.UVAt"/>; an untextured group keeps the shade-only combiner.
/// </summary>
public static class OtrRoomGeometry
{
    // F3DEX2 opcodes
    private const byte G_TRI1 = 0x05, G_TRI2 = 0x06, G_ENDDL = 0xDF, G_DL = 0xDE;
    private const byte G_VTX_OTR_HASH = 0x32, G_SETTIMG_OTR_HASH = 0x20;
    private const byte UCODE_F3DEX2 = 0x04;
    private const uint TexType_RGBA16 = 2;   // libultraship TextureType.RGBA16bpp

    private const int VTX_BATCH = 30;        // F3DEX2 vertex cache is 32; reserve 2 for culling

    // Water-surface rendering (matches DisplayListBuilder / the OoT decomp; see that file for the derivation).
    private const string WaterKey = "__water__";
    private const float WaterTopThreshold = 0.7f;
    private static bool IsWaterBrush(Solid s) =>
        s.IsWater || s.Faces.Any(f => SpecialTextures.Classify(f.TextureName).HasFlag(SpecialKind.WaterSurface));

    private record struct Vtx(short X, short Y, short Z, short S, short T, byte R, byte G, byte B, byte A = 0xFF);

    public record TexRes(string Path, byte[] Data);
    /// <summary><c>Dl</c> = opaque display list (room mesh opa path); <c>XluDl</c> = the translucent water
    /// surface (mesh xlu path), empty when the room has no water. Both reference the shared <c>Vtx</c>.</summary>
    public record Result(byte[] Vtx, byte[] Dl, IReadOnlyList<TexRes> Textures, bool Empty, byte[] XluDl);

    /// <param name="vtxResourcePath">Archive path of the OVTX resource this DL references.</param>
    /// <param name="texBasePath">Stem for this room's OTEX resource paths (e.g. the room path + "_texN").</param>
    /// <param name="texResolver">Resolves a brush texture name → its decoded bitmap, or null (untextured).</param>
    public static Result Build(ZRoom room, string vtxResourcePath, string texBasePath,
                               Func<string, Bitmap?>? texResolver, Func<Vector3, Vector3>? bakeShade = null,
                               IReadOnlyList<string>? scrollNames = null, bool waterScroll = false)
    {
        // ── 1. Group faces by texture (key "" = untextured); fan-triangulate with per-vertex UVs ──
        var order = new List<string>();
        var groups = new Dictionary<string, (Bitmap? bmp, List<(Vtx a, Vtx b, Vtx c)> tris)>();
        // Translucent/additive brush groups → their blend, so the DL emission routes them into the poly_xlu
        // list with the matching Fast3D render mode (mirrors the N64 DisplayListBuilder).
        var groupBlend = new Dictionary<string, Editor.BrushBlend>();

        // Optional compile-time face culling: skip render faces fully buried against a neighbouring brush
        // (Options ▸ "Cull unseen faces"). Render-only — collision keeps every face. Mirrors the N64
        // DisplayListBuilder so the setting behaves identically on SoH/2Ship. NB: this removes only faces
        // BURIED in an adjacent solid (brush-to-brush contact); it does NOT cull the interior faces of a
        // boundary wall that face the open play area (those are legitimately visible), and geometry is
        // exported double-sided (see the geometry-mode note below), so looking in from outside the arena
        // still shows interior faces — that is inherent to single-shell OoT geometry, not this setting.
        bool cullUnseen = Editor.EditorSettings.CullUnseenFaces;

        Bitmap? waterBmp = null;
        foreach (var solid in room.Geometry)
        {
            bool water = IsWaterBrush(solid);
            foreach (var face in solid.Faces)
            {
                var verts = face.Vertices;
                if (verts.Count < 3) continue;

                string key;
                Bitmap? bmp;
                byte alpha = 0xFF;
                if (water)
                {
                    // Water brushes render ONLY their up-facing surface, as the real water texture (the
                    // group is emitted last with a translucent XLU combiner below). Sides/bottom skipped.
                    if (face.Plane.Normal.Y < WaterTopThreshold) continue;
                    bmp = waterBmp ??= WaterTexture.Resolve();
                    key = WaterKey;
                }
                else
                {
                    if (SpecialTextures.IsNoRender(face.TextureName)) continue;   // NODRAW/CLIP
                    if (cullUnseen && Export.FaceCuller.IsObscured(face, solid, room.Geometry)) continue;
                    string? tn = face.TextureName;
                    bmp = tn != null && texResolver != null ? texResolver(tn) : null;
                    // Opaque keeps the raw texture name as its key (scroll matching + texture dedup rely on it);
                    // translucent/additive get a blend-tagged key so they don't merge with opaque geometry, and
                    // bake opacity into the vertex alpha (the xlu render mode blends by A_IN).
                    var blend = solid.Blend;
                    if (blend == Editor.BrushBlend.Opaque)
                        key = bmp != null ? tn! : "";
                    else
                    {
                        alpha = solid.Opacity;
                        string tag = blend == Editor.BrushBlend.Translucent ? "t" : "a";
                        key = bmp != null ? $"b{tag}:{tn}" : $"x{tag}:";
                        groupBlend[key] = blend;
                    }
                }
                if (!groups.TryGetValue(key, out var grp)) { groups[key] = grp = (bmp, []); order.Add(key); }

                // Vertex SHADE (which the combiner multiplies the TEXEL by). For a TEXTURED face this must
                // be WHITE so the texture shows its true colour — room.Color is only an editor visual aid
                // (the 2D-view per-room tint) and baking it in tinted every texture (e.g. blue for room 0).
                // For an untextured face the shade IS the flat surface colour (the face's own colour).
                // #9: a TEXTURED face bakes the scene's environment lighting (by face normal) instead of
                // fullbright white, so indoor rooms read dark in-game exactly as they do in the editor view.
                // (Water ignores shade — its combiner is TEXEL0 x PRIM — so keep it white.)
                Vector3 litShade = bakeShade?.Invoke(face.Plane.Normal) ?? Vector3.One;
                Vector3 fallback = water ? Vector3.One : (bmp != null ? litShade : face.Color);
                int tw = bmp?.Width ?? 32, th = bmp?.Height ?? 32;
                // #6: large faces (e.g. a 1200u dungeon wall with a 64px texture) push the per-vertex S/T
                // past the s16 vertex coord range, so the far end clamps while the near end doesn't and the
                // texture shears across the surface. Subtract a per-face INTEGER-tile offset so the coords
                // centre near 0 — the texture wraps, so an integer-tile shift is visually identical.
                var uvs = new Vector2[verts.Count];
                Vector2 sum = Vector2.Zero;
                for (int i = 0; i < verts.Count; i++) { uvs[i] = face.UVAt(verts[i]); sum += uvs[i]; }
                Vector2 ctr = sum / verts.Count;
                var off = new Vector2(MathF.Round(ctr.X), MathF.Round(ctr.Y));
                for (int i = 0; i < verts.Count; i++) uvs[i] -= off;

                // A shade-painted quad bakes its dense grid (local spray) instead of the 4-corner fan.
                var grid = face.ShadePaint;
                if (!water && grid != null && verts.Count == 4 && grid.Colors.Length == (grid.Nu + 1) * (grid.Nv + 1))
                {
                    Vtx GV(Vector3 p, Vector3 c) => ToVtx(p, c, face.UVAt(p) - off, tw, th, alpha);
                    for (int j = 0; j < grid.Nv; j++)
                        for (int i = 0; i < grid.Nu; i++)
                        {
                            Vector3 p00 = face.ShadeGridPos(i, j, grid.Nu, grid.Nv), p10 = face.ShadeGridPos(i + 1, j, grid.Nu, grid.Nv);
                            Vector3 p11 = face.ShadeGridPos(i + 1, j + 1, grid.Nu, grid.Nv), p01 = face.ShadeGridPos(i, j + 1, grid.Nu, grid.Nv);
                            Vector3 c00 = grid.Colors[grid.Index(i, j)], c10 = grid.Colors[grid.Index(i + 1, j)];
                            Vector3 c11 = grid.Colors[grid.Index(i + 1, j + 1)], c01 = grid.Colors[grid.Index(i, j + 1)];
                            grp.tris.Add((GV(p00, c00), GV(p10, c10), GV(p11, c11)));
                            grp.tris.Add((GV(p00, c00), GV(p11, c11), GV(p01, c01)));
                        }
                    continue;
                }
                for (int i = 1; i < verts.Count - 1; i++)
                    grp.tris.Add((
                        ToVtx(verts[0],     face.ColorAt(0,     fallback), uvs[0],     tw, th, alpha),
                        ToVtx(verts[i],     face.ColorAt(i,     fallback), uvs[i],     tw, th, alpha),
                        ToVtx(verts[i + 1], face.ColorAt(i + 1, fallback), uvs[i + 1], tw, th, alpha)));
            }
        }

        // ── Decals: bake each Hammer-style decal as a small textured quad floated off its surface, grouped
        // by texture like the brush faces above (so it overlays the wall instead of retexturing the face). ──
        foreach (var decal in room.Decals)
        {
            if (decal.TextureName == null || texResolver == null) continue;
            var bmp = texResolver(decal.TextureName);
            if (bmp == null) continue;
            if (!groups.TryGetValue(decal.TextureName, out var grp)) { groups[decal.TextureName] = grp = (bmp, []); order.Add(decal.TextureName); }
            int tw = bmp.Width, th = bmp.Height;
            var c = decal.Corners();   // BL, BR, TR, TL, lifted off the surface
            // ToVtx expects NORMALISED (tile-unit) UVs (it multiplies by tw*32), so the corners span 0..1 to
            // map the texture ONCE across the quad. Passing tw overflowed s16 → the texture tiled ~tw× ("tiny").
            Vtx V(Vector3 p, float u, float v) => ToVtx(p, Vector3.One, new Vector2(u, v), tw, th);
            grp.tris.Add((V(c[0], 0, 1), V(c[1], 1, 1), V(c[2], 1, 0)));
            grp.tris.Add((V(c[0], 0, 1), V(c[2], 1, 0), V(c[3], 0, 0)));
        }

        // ── Imported OBJ mesh geometry: grouped by material the same way (UVs already 0..1) ──
        if (room.ObjMesh is { } objMesh)
            foreach (var tri in objMesh.Tris)
            {
                if (tri.NoMesh) continue;   // collision-only group, not drawn
                var bmp = objMesh.Materials.GetValueOrDefault(tri.Material);
                string key = bmp != null ? "obj:" + tri.Material : "";
                if (!groups.TryGetValue(key, out var grp)) { groups[key] = grp = (bmp, []); order.Add(key); }
                Vector3 objShade = bmp != null ? Vector3.One : new Vector3(0.5f, 0.5f, 0.5f);
                int tw = bmp?.Width ?? 32, th = bmp?.Height ?? 32;
                grp.tris.Add((
                    ToVtx(tri.P0, objShade, tri.UV0, tw, th),
                    ToVtx(tri.P1, objShade, tri.UV1, tw, th),
                    ToVtx(tri.P2, objShade, tri.UV2, tw, th)));
            }

        if (groups.Values.All(g => g.tris.Count == 0))
            return new Result([], [], [], Empty: true, XluDl: []);

        // ── 2. Build the DL + OVTX (vertices concatenated across groups) + OTEX resources ──
        var allVerts = new List<Vtx>();
        var textures = new List<TexRes>();

        var dw = new OtrResourceWriter(OtrResType.DisplayList);
        dw.U8(UCODE_F3DEX2);
        dw.Align(8);

        WriteGfx(dw, 0xE7000000u, 0x00000000u);   // gsDPPipeSync
        // No back-face cull + z-write — the SoH/2Ship (Fast3D) twin of the N64 DisplayListBuilder fix.
        // The previous CULL_BACK culled inconsistently-wound brush faces ("face-culled / backwards"),
        // and render mode 0x0C1849D8 had no Z_UPD so far surfaces drew over near ones ("inside-out").
        WriteGfx(dw, 0xD9000005u, 0x00000005u);   // gsSPSetGeometryMode(ZBUFFER|SHADE) — double-sided
        WriteGfx(dw, 0xE200001Cu, 0x0C1841F8u);   // gsDPSetRenderMode (ZMODE_OPA + Z_UPD, z-write)
        // gsDPSetOtherMode_H: 1-CYCLE + texture-perspective + bilerp filter + TT_NONE. Our DL never set
        // othermode-H, so the cycle type / texture-LUT mode were whatever the previous draw left — a wrong
        // TT (palette) mode misreads our RGBA16 as CI and a non-1-cycle type breaks the 1-cycle combiner,
        // so the textures never sampled (rendered as flat white/SHADE). shift=4 len=20 covers the
        // cycle-type bits [21:20] (=0 -> 1-cycle); data 0x82000 = G_TP_PERSP | G_TF_BILERP, TT_NONE.
        WriteGfx(dw, 0xE3000813u, 0x00082000u);
        WriteGfx(dw, 0xD7000002u, 0xFFFFFFFFu);   // gsSPTexture(0xFFFF, 0xFFFF, 0, 0, on) — enable, scale 1.0

        ulong vtxHash = OtrCrc64.Hash(vtxResourcePath);
        int texIdx = 0;
        foreach (var key in order)
        {
            if (key == WaterKey) continue;              // water is emitted into the SEPARATE XLU list below
            if (groupBlend.ContainsKey(key)) continue;  // translucent/additive brushes go in the XLU list below
            var (bmp, tris) = groups[key];
            if (tris.Count == 0) continue;

            if (bmp != null)
            {
                // Textured group: OTEX resource + load sequence + modulate combiner.
                string texPath = $"{texBasePath}_tex{texIdx++}";
                textures.Add(new TexRes(texPath, BuildTextureResource(bmp)));
                WriteGfx(dw, 0xFC121824u, 0xFF33FFFFu);   // gsDPSetCombineMode(G_CC_MODULATERGBA, …) TEXEL0×SHADE
                EmitTextureLoad(dw, texPath, bmp.Width, bmp.Height);

                // MM animated texture: if this group's texture is a scrolling material, jump into the tile
                // bound on segment 8+i by the MAT_ANIM draw config (gsSPDisplayList(0x0(8+i)000000)). That
                // DL re-sets the tile size with the per-frame scroll offset before these tris draw.
                int si = -1;
                if (scrollNames != null)
                    for (int sj = 0; sj < scrollNames.Count; sj++) if (scrollNames[sj] == key) { si = sj; break; }
                if (si >= 0)
                    // gsSPDisplayList into the scroll tile bound on segment 8+i. NOTE: Fast3D's SegAddr
                    // treats w1 as a SEGMENTED address only when bit 0 is set (`if (w1 & 1)`); without it
                    // it dereferences 0x08000000 as a raw pointer → crash (N64 HW needs no such marker,
                    // which is why PJ64 scrolled but 2Ship crashed). So OR in 1.
                    WriteGfx(dw, (uint)G_DL << 24, (uint)(((0x08 + si) << 24) | 1));
            }
            else
            {
                WriteGfx(dw, 0xFC000000u, 0x00041104u);   // gsDPSetCombineMode(SHADE, SHADE)
            }

            EmitTris(dw, tris, allVerts, vtxHash);
        }

        WriteGfx(dw, (uint)G_ENDDL << 24, 0x00000000u);   // gsSPEndDisplayList

        // ── Water surface → a SEPARATE XLU display list (room mesh xlu path) ───────────────────────────
        // Translucent geometry belongs in the XLU buffer, AND the SoH/2Ship scroll draw config binds the
        // scroll segment 0x08 in POLY_XLU_DISP. Same F3DEX2 words as the N64 path (G_RM_AA_ZB_XLU_SURF|SURF2,
        // G_CC_MODULATEI_PRIM, prim white alpha 128); vertices append to the shared OVTX.
        byte[] xluDl = [];
        var xluKeys = order.Where(k => k == WaterKey || groupBlend.ContainsKey(k))
                           .Where(k => groups[k].tris.Count > 0).ToList();
        if (xluKeys.Count > 0)
        {
            var xw = new OtrResourceWriter(OtrResType.DisplayList);
            xw.U8(UCODE_F3DEX2);
            xw.Align(8);
            WriteGfx(xw, 0xE7000000u, 0x00000000u);   // gsDPPipeSync
            WriteGfx(xw, 0xD9000005u, 0x00000005u);   // gsSPSetGeometryMode(ZBUFFER|SHADE) — double-sided
            WriteGfx(xw, 0xE3000813u, 0x00082000u);   // gsDPSetOtherMode_H: 1-CYCLE + persp + bilerp + TT_NONE
            WriteGfx(xw, 0xD7000002u, 0xFFFFFFFFu);   // gsSPTexture enable, scale 1.0
            uint curX = 0;
            foreach (var key in xluKeys)
            {
                var (bmp, tris) = groups[key];
                if (key == WaterKey)
                {
                    if (bmp == null) continue;
                    if (curX != 0x005049D8u) { WriteGfx(xw, 0xE200001Cu, 0x005049D8u); curX = 0x005049D8u; }  // XLU alpha-blend
                    WriteGfx(xw, 0xFC11FE23u, 0xFFFFF7FBu);   // G_CC_MODULATEI_PRIM (TEXEL0×PRIM)
                    WriteGfx(xw, 0xFA000000u, 0xFFFFFF80u);   // prim white, alpha 128
                    string wTexPath = $"{texBasePath}_water";
                    textures.Add(new TexRes(wTexPath, BuildTextureResource(bmp)));
                    EmitTextureLoad(xw, wTexPath, bmp.Width, bmp.Height);
                    // Scroll: run the per-frame tile-scroll DL the scene draw config binds on seg 0x08.
                    if (waterScroll) WriteGfx(xw, (uint)G_DL << 24, (uint)(0x08000000 | 1));
                }
                else
                {
                    // Translucent = alpha-blend (0x005049D8); Additive = light-add (0x005A49D8). Opacity is in
                    // the vertex alpha, which the render mode reads via A_IN, so a plain MODULATE/SHADE combiner
                    // (alpha from vertex) suffices — no per-group prim needed.
                    uint rm = groupBlend[key] == Editor.BrushBlend.Additive ? 0x005A49D8u : 0x005049D8u;
                    if (curX != rm) { WriteGfx(xw, 0xE200001Cu, rm); curX = rm; }
                    if (bmp != null)
                    {
                        string texPath = $"{texBasePath}_tex{texIdx++}";
                        textures.Add(new TexRes(texPath, BuildTextureResource(bmp)));
                        WriteGfx(xw, 0xFC121824u, 0xFF33FFFFu);   // G_CC_MODULATERGBA (TEXEL0×SHADE, alpha from vertex)
                        EmitTextureLoad(xw, texPath, bmp.Width, bmp.Height);
                        // A scrolling xlu brush (Chamber-of-Sages-style water): bind its animated tile on seg 8+i.
                        int si = -1;
                        if (scrollNames != null)
                        {
                            string tn = key[(key.IndexOf(':') + 1)..];
                            for (int sj = 0; sj < scrollNames.Count; sj++) if (scrollNames[sj] == tn) { si = sj; break; }
                        }
                        if (si >= 0) WriteGfx(xw, (uint)G_DL << 24, (uint)(((0x08 + si) << 24) | 1));
                    }
                    else
                        WriteGfx(xw, 0xFC000000u, 0x00041104u);   // G_CC_SHADE (colour + alpha from vertex)
                }
                EmitTris(xw, tris, allVerts, vtxHash);
            }
            WriteGfx(xw, (uint)G_ENDDL << 24, 0x00000000u);
            xluDl = xw.ToArray();
        }

        // OVTX: count + 16-byte records, in the order the DL's offsets reference them.
        var vw = new OtrResourceWriter(OtrResType.Vertex);
        vw.U32((uint)allVerts.Count);
        foreach (var v in allVerts) WriteVtx(vw, v);

        return new Result(vw.ToArray(), dw.ToArray(), textures, Empty: false, XluDl: xluDl);
    }

    // Emits a group's triangles as ≤VTX_BATCH-vertex G_VTX_OTR_HASH loads + TRI commands, appending
    // the (deduped-per-batch) vertices to the shared OVTX list.
    private static void EmitTris(OtrResourceWriter dw, List<(Vtx a, Vtx b, Vtx c)> tris, List<Vtx> allVerts, ulong vtxHash)
    {
        var batches = new List<(List<Vtx> verts, List<(int a, int b, int c)> tris)>();
        var bv = new List<Vtx>(VTX_BATCH);
        var bt = new List<(int, int, int)>();
        foreach (var (va, vb, vc) in tris)
        {
            if (bv.Count + 3 > VTX_BATCH && bv.Count > 0) { batches.Add((new(bv), new(bt))); bv.Clear(); bt.Clear(); }
            int ia = AddVert(bv, va), ib = AddVert(bv, vb), ic = AddVert(bv, vc);
            bt.Add((ia, ib, ic));
        }
        if (bv.Count > 0) batches.Add((new(bv), new(bt)));

        foreach (var (verts, btris) in batches)
        {
            int n = verts.Count;
            int baseOff = allVerts.Count * 16;
            allVerts.AddRange(verts);

            uint w0 = ((uint)G_VTX_OTR_HASH << 24) | ((uint)n << 12) | ((uint)n << 1);
            WriteGfx(dw, w0, (uint)baseOff);
            WriteGfx(dw, (uint)(vtxHash >> 32), (uint)(vtxHash & 0xFFFFFFFF));

            int ti = 0;
            while (ti < btris.Count)
            {
                if (ti + 1 < btris.Count)
                {
                    var (a0, b0, c0) = btris[ti]; var (a1, b1, c1) = btris[ti + 1];
                    // gsSP2Triangles: opcode in bits 24-31; vertex indices (×2) packed big-end-first
                    // WITHIN each word. Must go through WriteGfx (two LE u32) so the opcode lands in the
                    // high byte — writing the bytes individually reverses them and the interpreter reads a
                    // vertex index as the opcode.
                    WriteGfx(dw,
                        ((uint)G_TRI2 << 24) | ((uint)(a0 * 2) << 16) | ((uint)(b0 * 2) << 8) | (uint)(c0 * 2),
                                               ((uint)(a1 * 2) << 16) | ((uint)(b1 * 2) << 8) | (uint)(c1 * 2));
                    ti += 2;
                }
                else
                {
                    var (a0, b0, c0) = btris[ti];
                    WriteGfx(dw,
                        ((uint)G_TRI1 << 24) | ((uint)(a0 * 2) << 16) | ((uint)(b0 * 2) << 8) | (uint)(c0 * 2),
                        0u);
                    ti++;
                }
            }
        }
    }

    // Standard F3DEX2 load-texture-block sequence, but with the SETTIMG replaced by SETTIMG_OTR_HASH so
    // SoH binds the OTEX resource by its path hash. RGBA16 (fmt 0, siz 2).
    private static void EmitTextureLoad(OtrResourceWriter dw, string texPath, int w, int h)
    {
        // SETTIMG_OTR_HASH: w0 = opcode | fmt(0)<<21 | siz_LOAD_BLOCK(2)<<19 | (width 1 - 1 = 0); w1 = 0; + hash.
        WriteGfx(dw, ((uint)G_SETTIMG_OTR_HASH << 24) | (2u << 19), 0u);
        ulong th = OtrCrc64.Hash(texPath);
        WriteGfx(dw, (uint)(th >> 32), (uint)(th & 0xFFFFFFFF));

        // gsDPSetTile(fmt 0, siz_LOAD_BLOCK 2, line 0, tmem 0, tile LOADTILE 7, …)
        WriteGfx(dw, 0xF5000000u | (2u << 19), 7u << 24);
        WriteGfx(dw, 0xE6000000u, 0u);   // gsDPLoadSync

        // gsDPLoadBlock(LOADTILE, 0, 0, w*h-1, CALC_DXT(w, 2))
        int lrs = w * h - 1;
        WriteGfx(dw, 0xF3000000u, (7u << 24) | ((uint)lrs << 12) | (uint)CalcDxt(w));
        WriteGfx(dw, 0xE7000000u, 0u);   // gsDPPipeSync

        // gsDPSetTile(fmt 0, siz 2, line, tmem 0, tile RENDERTILE 0, pal 0, cmt/cms WRAP, mask = log2(dim))
        int line = ((w * 2) + 7) >> 3;
        int masks = Log2(w), maskt = Log2(h);
        WriteGfx(dw, 0xF5000000u | (2u << 19) | ((uint)line << 9),
                 ((uint)maskt << 14) | ((uint)masks << 4));

        // gsDPSetTileSize(RENDERTILE, 0, 0, (w-1)<<2, (h-1)<<2)
        WriteGfx(dw, 0xF2000000u, ((uint)((w - 1) << 2) << 12) | (uint)((h - 1) << 2));
    }

    private static byte[] BuildTextureResource(Bitmap bmp)
    {
        byte[] data = Encode5551(bmp);
        var rw = new OtrResourceWriter(OtrResType.Texture);
        rw.U32(TexType_RGBA16);
        rw.U32((uint)bmp.Width);
        rw.U32((uint)bmp.Height);
        rw.U32((uint)data.Length);
        rw.Bytes(data);
        return rw.ToArray();
    }

    // 32-bit ARGB bitmap → N64 RGBA16 (5-5-5-1), big-endian.
    private static byte[] Encode5551(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var outp = new byte[w * h * 2];
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* src = (byte*)bd.Scan0;
                int stride = bd.Stride;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        byte* px = src + y * stride + x * 4;   // BGRA
                        int b = px[0], g = px[1], r = px[2], a = px[3];
                        ushort p = (ushort)(((r >> 3) << 11) | ((g >> 3) << 6) | ((b >> 3) << 1) | (a >= 128 ? 1 : 0));
                        int o = (y * w + x) * 2;
                        outp[o] = (byte)(p >> 8); outp[o + 1] = (byte)(p & 0xFF);
                    }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return outp;
    }

    // ── helpers ─────────────────────────────────────────────────────────────
    private static void WriteGfx(OtrResourceWriter w, uint w0, uint w1) { w.U32(w0); w.U32(w1); }

    private static Vtx ToVtx(Vector3 p, Vector3 col, Vector2 uv, int texW, int texH, byte a = 0xFF)
        => new((short)MathF.Round(p.X), (short)MathF.Round(p.Y), (short)MathF.Round(p.Z),
               (short)Math.Clamp(uv.X * texW * 32f, -32768f, 32767f),
               (short)Math.Clamp(uv.Y * texH * 32f, -32768f, 32767f),
               (byte)(Math.Clamp(col.X, 0f, 1f) * 255), (byte)(Math.Clamp(col.Y, 0f, 1f) * 255),
               (byte)(Math.Clamp(col.Z, 0f, 1f) * 255), a);

    private static int AddVert(List<Vtx> batch, Vtx v)
    {
        for (int i = 0; i < batch.Count; i++) if (batch[i] == v) return i;
        batch.Add(v);
        return batch.Count - 1;
    }

    private static void WriteVtx(OtrResourceWriter w, Vtx v)
    {
        w.S16(v.X); w.S16(v.Y); w.S16(v.Z);
        w.U16(0);                 // flag
        w.S16(v.S); w.S16(v.T);   // texture coords (S10.5)
        w.U8(v.R); w.U8(v.G); w.U8(v.B); w.U8(v.A);
    }

    private static int Log2(int n) { int l = 0; while ((1 << l) < n) l++; return l; }
    private static int CalcDxt(int w) { int words = Math.Max(1, w * 2 / 8); return (2048 + words - 1) / words; }
}
