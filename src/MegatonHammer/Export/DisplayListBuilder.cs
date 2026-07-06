using System.Drawing;
using System.Drawing.Imaging;
using MegatonHammer.Editor;
using OpenTK.Mathematics;

namespace MegatonHammer.Export;

/// <summary>
/// Converts ZRoom brush + imported-OBJ geometry into an F3DEX2 display list, a raw vertex block, and a
/// texture-data block, ready to embed in a Zelda 64 room file. Triangles are grouped by texture; each
/// textured group emits a real SETTIMG + load-block sequence pointing at RGBA16 texture data appended
/// after the vertices (so ROM-injected brush levels are textured, not flat-shaded). Untextured groups
/// fall back to shade-only.
/// </summary>
public static class DisplayListBuilder
{
    private const byte G_VTX = 0x01, G_TRI1 = 0x05, G_TRI2 = 0x06;
    private const int VTX_BATCH = 30;

    // ── Water-surface rendering (faithful OoT translucent water) ───────────────────────────────────────
    // A WATERBOX brush renders its up-facing surface as real translucent water, matching how OoT draws Lake
    // Hylia / the Water Temple. Values taken verbatim from the OoT decomp (include/ultra64/gbi.h + z_rcp.c)
    // and cross-checked by reproducing the existing solid-wall combiner word bit-for-bit:
    //   render mode = G_RM_AA_ZB_XLU_SURF | G_RM_AA_ZB_XLU_SURF2  (AA, z-compare, NO z-write, alpha-blend)
    //   combiner    = G_CC_MODULATEI_PRIM  (colour = TEXEL0 x PRIM, alpha = PRIM)
    //   prim colour = white, alpha 128 (50% translucent, the Lake Hylia / Zora value)
    internal const string WaterKey = "__water__";
    private const float WaterTopThreshold = 0.7f;          // a face counts as the water surface if normal.Y ≥ this
    private const ulong RM_WATER_XLU = 0xE200001C005049D8UL;   // SetOtherMode_L, G_RM_AA_ZB_XLU_SURF|SURF2
    private const ulong COMBINE_WATER = 0xFC11FE23FFFFF7FBUL;  // SetCombine G_CC_MODULATEI_PRIM (both cycles)
    private const ulong PRIM_WATER = 0xFA000000FFFFFF80UL;     // SetPrimColor 255,255,255,128
    private const ulong PIPE_SYNC = 0xE700000000000000UL;

    // A brush is water if flagged IsWater OR painted with the WATERBOX special texture (mirrors CollisionBuilder).
    private static bool IsWaterBrush(Editor.Solid s) =>
        s.IsWater || s.Faces.Any(f => Textures.SpecialTextures.Classify(f.TextureName).HasFlag(Textures.SpecialKind.WaterSurface));

    /// <summary>True if any room in the scene has a water brush — drives the scene's water-scroll draw
    /// config (OoT SDC_CALM_WATER / MM MAT_ANIM) and the matching <c>waterScroll</c> flag, which must be
    /// set together so the water DL's segment-0x08 scroll call always has a bound segment.</summary>
    public static bool SceneHasWater(ZScene scene) => scene.Rooms.Any(r => r.Geometry.Any(IsWaterBrush));

    /// <summary>Diagnostic: the texture keys the last N64 Build(s) flagged as cutout (alpha-tested). The
    /// caller clears it before a build pass to inspect which textures will draw transparent.</summary>
    public static readonly HashSet<string> LastCutoutTextures = new();

    // 16-byte Zelda 64 Vtx (big-endian) — now carries s/t texture coords (S10.5 texel).
    private record struct Vtx(short X, short Y, short Z, short S, short T, byte R, byte G, byte B, byte A = 0xFF);

    /// <summary>VertexData + TextureData are laid out contiguously by the caller at
    /// <c>vtxSegOffset</c>; the DLs reference both by segment offset. <c>DlCommands</c> is the OPAQUE
    /// display list (room shape opaPtr); <c>XluDlCommands</c> is the translucent water surface (xluPtr),
    /// empty when the room has no water.</summary>
    public record DlResult(byte[] VertexData, byte[] TextureData, byte[] DlCommands, byte[] XluDlCommands);

    /// <summary>texResolver maps a brush texture name → its bitmap (null = untextured).</summary>
    /// <param name="n64Hw">true = real N64 hardware (RDP); false = SoH/2Ship (libultraship Fast3D).
    /// Geometry + render mode (z-write, no-cull) are identical for both. The flag only governs texel
    /// alpha: N64 forces brush walls opaque (no alpha-compare set up, so stray-alpha ROM texels would
    /// otherwise render the wall see-through), while the OTR/Fast3D path preserves the source 1-bit
    /// alpha so genuine cutout textures (foliage/trees) keep their transparency.</param>
    public static DlResult Build(ZRoom room, byte seg, int vtxSegOffset, Func<string, Bitmap?>? texResolver = null,
                                 bool n64Hw = true, SceneSettings? lighting = null,
                                 IReadOnlyList<TextureScroll>? scrolls = null, bool waterScroll = false)
    {
        // `lighting` (the scene's SceneSettings) IS used below: a textured face bakes per-normal env shade
        // via lighting.BakedShade under LightingMethod >= 2 (parity with the OTR OtrRoomGeometry path, which
        // bakes the same BakedShade). Method 1 renders fullbright. SceneExporter passes scene.Settings here
        // on the N64 path (null on the OTR path, which shades in OtrRoomGeometry instead).
        // ── 1. Group triangles by texture bitmap (key "" = untextured) ──
        var order = new List<string>();
        var groups = new Dictionary<string, (Bitmap? bmp, List<(Vtx a, Vtx b, Vtx c)> tris)>();
        Vector3 col = room.Color;
        byte cr = (byte)(col.X * 255), cg = (byte)(col.Y * 255), cb = (byte)(col.Z * 255);

        // N64 RDP texture memory is 4KB — an RGBA16 texture bigger than 2048 texels (e.g. 64x64 = 4096)
        // overflows TMEM and loads as garbage/noise on real hardware (and PJ64), even though Fast3D
        // (SoH/2Ship) has no such limit and renders it fine. Downscale oversized textures to fit, once per
        // name. (SoH path is OtrRoomGeometry, which keeps full resolution.)
        var fitCache = new Dictionary<string, Bitmap?>();
        Bitmap? FitResolve(string name)
        {
            if (fitCache.TryGetValue(name, out var cached)) return cached;
            var b = texResolver?.Invoke(name);
            if (b != null) b = FitN64Tmem(b);
            fitCache[name] = b;
            return b;
        }

        void Add(string key, Bitmap? bmp, Vtx a, Vtx b, Vtx c)
        {
            if (!groups.TryGetValue(key, out var grp)) { groups[key] = grp = (bmp, []); order.Add(key); }
            grp.tris.Add((a, b, c));
        }

        // Emits one face's triangles into group <paramref name="key"/>. Shared by the solid pass and the
        // water pass below.
        void EmitFace(string key, Bitmap? bmp, SolidFace face, byte sr, byte sg, byte sb, bool selected)
        {
            var verts = face.Vertices;
            if (verts.Count < 3) return;
            // Vertex SHADE: WHITE for a textured face so the texture shows its true colour (the combiner
            // does TEXEL x SHADE). room.Color is only the editor's per-room 2D tint. PER-VERTEX shade,
            // matching OtrRoomGeometry so PJ64 renders the same as SoH/2Ship; painted vertex colours win,
            // untextured faces fall back to the face's own colour, selected faces keep the highlight.
            // #9: a textured face bakes scene env lighting (by normal) under lighting method 2; method 1
            // renders fullbright. (Water ignores shade entirely — its combiner is TEXEL0 x PRIM.)
            Vector3 litShade = Editor.EditorSettings.LightingMethod >= 2
                ? (lighting?.BakedShade(face.Plane.Normal) ?? Vector3.One)
                : Vector3.One;
            Vector3 fallback = bmp != null ? litShade : face.Color;
            int tw = bmp?.Width ?? 32, th = bmp?.Height ?? 32;
            (byte r, byte g, byte b) Shade(int vi)
            {
                if (selected) return (sr, sg, sb);
                var c = face.ColorAt(vi, fallback);
                return ((byte)(Math.Clamp(c.X, 0f, 1f) * 255),
                        (byte)(Math.Clamp(c.Y, 0f, 1f) * 255),
                        (byte)(Math.Clamp(c.Z, 0f, 1f) * 255));
            }
            // #6: centre each face's UVs on an integer-tile offset so a large wall's S/T doesn't overflow
            // the s16 vertex coords and shear/black the far end (wrap makes the shift invisible).
            var uvs = new Vector2[verts.Count];
            Vector2 sum = Vector2.Zero;
            for (int i = 0; i < verts.Count; i++) { uvs[i] = face.UVAt(verts[i]); sum += uvs[i]; }
            var off = new Vector2(MathF.Round(sum.X / verts.Count), MathF.Round(sum.Y / verts.Count));
            for (int i = 0; i < verts.Count; i++) uvs[i] -= off;

            // A shade-painted quad bakes its dense grid (local spray) instead of the 4-corner fan, so the
            // painted shading exports at full resolution. BUT the editor's grid can be up to 16×16 (512 tris)
            // per face — on N64 that overflows the fixed room buffer (a level with ~100 painted faces built a
            // ~480 KB room that hung the debug ROM on load). So on N64 the grid is DOWN-SAMPLED to at most
            // N64ShadeGridCap cells per axis (colours sampled from the full grid), configurable in Options and
            // 0 = OFF (painted faces render flat via the fallback path below). SoH/2Ship keep the full grid.
            int shadeCap = Math.Clamp(Editor.EditorSettings.N64ShadeGridCap, 0, 16);
            var grid = face.ShadePaint;
            if (shadeCap > 0 && grid != null && verts.Count == 4 && grid.Colors.Length == (grid.Nu + 1) * (grid.Nv + 1))
            {
                (byte r, byte g, byte b) GShade(Vector3 c) => selected ? (sr, sg, sb)
                    : ((byte)(Math.Clamp(c.X, 0f, 1f) * 255), (byte)(Math.Clamp(c.Y, 0f, 1f) * 255), (byte)(Math.Clamp(c.Z, 0f, 1f) * 255));
                Vtx GV(Vector3 p, Vector3 c) { var s = GShade(c); return ToVtx(p, face.UVAt(p) - off, tw, th, s.r, s.g, s.b); }
                int cnu = Math.Min(grid.Nu, shadeCap), cnv = Math.Min(grid.Nv, shadeCap);
                // Sample the stored (fine) grid's colour at the full-res node nearest this coarse node.
                Vector3 CoarseColor(int ci, int cj)
                {
                    int fi = Math.Clamp((int)MathF.Round((float)ci * grid.Nu / cnu), 0, grid.Nu);
                    int fj = Math.Clamp((int)MathF.Round((float)cj * grid.Nv / cnv), 0, grid.Nv);
                    return grid.Colors[grid.Index(fi, fj)];
                }
                for (int j = 0; j < cnv; j++)
                    for (int i = 0; i < cnu; i++)
                    {
                        Vector3 p00 = face.ShadeGridPos(i, j, cnu, cnv), p10 = face.ShadeGridPos(i + 1, j, cnu, cnv);
                        Vector3 p11 = face.ShadeGridPos(i + 1, j + 1, cnu, cnv), p01 = face.ShadeGridPos(i, j + 1, cnu, cnv);
                        Vector3 c00 = CoarseColor(i, j), c10 = CoarseColor(i + 1, j);
                        Vector3 c11 = CoarseColor(i + 1, j + 1), c01 = CoarseColor(i, j + 1);
                        Add(key, bmp, GV(p00, c00), GV(p10, c10), GV(p11, c11));
                        Add(key, bmp, GV(p00, c00), GV(p11, c11), GV(p01, c01));
                    }
                return;
            }
            for (int i = 1; i < verts.Count - 1; i++)
            {
                var s0 = Shade(0); var s1 = Shade(i); var s2 = Shade(i + 1);
                Add(key, bmp,
                    ToVtx(verts[0],     uvs[0],     tw, th, s0.r, s0.g, s0.b),
                    ToVtx(verts[i],     uvs[i],     tw, th, s1.r, s1.g, s1.b),
                    ToVtx(verts[i + 1], uvs[i + 1], tw, th, s2.r, s2.g, s2.b));
            }
        }

        // Optional compile-time face culling: skip render faces fully buried against a neighbouring brush
        // (Options ▸ "Cull unseen faces"). Render-only — collision (CollisionBuilder) keeps every face.
        bool cullUnseen = Editor.EditorSettings.CullUnseenFaces;

        // Pass 1: solid (non-water) brushes.
        foreach (var solid in room.Geometry)
        {
            if (IsWaterBrush(solid)) continue;   // handled in the water pass below
            // NB: brush selection is an EDITOR-ONLY highlight (the live GL viewport draws it). It must NEVER
            // be baked into the compiled/exported DL — doing so tinted the selected brush orange in-game.
            // Always export as unselected (selected: false); sr/sg/sb are unused when not selected.
            byte sr = cr, sg = cg, sb = cb;
            foreach (var face in solid.Faces)
            {
                if (face.Vertices.Count < 3) continue;
                if (Textures.SpecialTextures.IsNoRender(face.TextureName)) continue;
                if (cullUnseen && FaceCuller.IsObscured(face, solid, room.Geometry)) continue;
                string? tn = face.TextureName;
                Bitmap? bmp = tn != null && texResolver != null ? FitResolve(tn) : null;
                string key = bmp != null ? "b:" + tn : "";
                EmitFace(key, bmp, face, sr, sg, sb, false);
            }
        }

        // Pass 2: water brushes. Only the up-facing SURFACE renders (the sides/bottom of the water volume
        // are not drawn), as the real OoT water texture in a translucent XLU group emitted LAST so it draws
        // after the opaque geometry and alpha-blends over the pool floor. WATERBOX collision is unaffected
        // (CollisionBuilder turns these brushes into water boxes).
        Bitmap? waterBmp = null;
        foreach (var solid in room.Geometry)
        {
            if (!IsWaterBrush(solid)) continue;
            byte sr = cr, sg = cg, sb = cb;   // selection highlight is editor-only — never baked into export
            foreach (var face in solid.Faces)
            {
                if (face.Vertices.Count < 3) continue;
                if (face.Plane.Normal.Y < WaterTopThreshold) continue;   // surface only
                waterBmp ??= Textures.WaterTexture.Resolve();
                EmitFace(WaterKey, waterBmp, face, sr, sg, sb, false);
            }
        }
        // Pass 3: decals — bake each Hammer-style decal as its own small textured quad, floated a hair off
        // the surface it was placed on (Decal.Corners lifts along the normal), so it renders as an overlay on
        // the wall instead of retexturing the whole face. Emitted as a per-texture group like brush faces.
        foreach (var decal in room.Decals)
        {
            if (decal.TextureName == null || texResolver == null) continue;
            var bmp = FitResolve(decal.TextureName);
            if (bmp == null) continue;
            int tw = bmp.Width, th = bmp.Height;
            string key = "d:" + decal.TextureName;
            var c = decal.Corners();   // BL, BR, TR, TL, lifted off the surface
            // The whole texture maps ONCE across the quad. ToVtx expects NORMALISED (tile-unit) UVs — it
            // multiplies by tw*32 internally — so the corners span 0..1, NOT 0..tw. (Passing tw made s reach
            // tw*tw*32, overflowing s16 → the texture tiled ~tw times across the decal = "tiny tiling".)
            Vtx V(OpenTK.Mathematics.Vector3 p, float u, float v) => ToVtx(p, new Vector2(u, v), tw, th, 255, 255, 255);
            Add(key, bmp, V(c[0], 0, 1), V(c[1], 1, 1), V(c[2], 1, 0));
            Add(key, bmp, V(c[0], 0, 1), V(c[2], 1, 0), V(c[3], 0, 0));
        }

        if (room.ObjMesh is { } objMesh)
            foreach (var tri in objMesh.Tris)
            {
                if (tri.NoMesh) continue;
                if (!fitCache.TryGetValue("o:" + tri.Material, out var bmp))
                {
                    var rawBmp = objMesh.Materials.GetValueOrDefault(tri.Material);
                    bmp = rawBmp != null ? FitN64Tmem(rawBmp) : null;
                    fitCache["o:" + tri.Material] = bmp;
                }
                string key = bmp != null ? "o:" + tri.Material : "";
                int tw = bmp?.Width ?? 32, th = bmp?.Height ?? 32;
                Add(key, bmp, ToVtx(tri.P0, tri.UV0, tw, th, 255, 255, 255),
                              ToVtx(tri.P1, tri.UV1, tw, th, 255, 255, 255),
                              ToVtx(tri.P2, tri.UV2, tw, th, 255, 255, 255));
            }

        if (groups.Values.All(g => g.tris.Count == 0)) return new DlResult([], [], [], []);

        // ── 2. Per-group batching + vertex block ──
        var vw = new N64BinaryWriter();
        var texBlock = new List<(byte[] data, int w, int h)>();
        var texOffset = new Dictionary<string, int>();   // group key → texture seg offset
        var isCutout  = new Dictionary<string, bool>();   // group key → alpha-tested (TEX_EDGE) on N64

        // Texture data follows the vertex data; we don't know vertex length until built, so build the
        // vertex block first, then assign texture offsets after it.
        var dlPlan = new List<(string key, Bitmap? bmp, List<(int n, int vtxOff, List<(int, int, int)> tris)> batches)>();
        foreach (var key in order)
        {
            var (bmp, tris) = groups[key];
            if (tris.Count == 0) continue;
            var batches = new List<(int n, int vtxOff, List<(int, int, int)> tris)>();
            var bv = new List<Vtx>(VTX_BATCH); var bt = new List<(int, int, int)>();
            void Flush() { if (bv.Count == 0) return; int off = vw.Position; foreach (var v in bv) WriteVtx(vw, v); batches.Add((bv.Count, off, new(bt))); bv = new(VTX_BATCH); bt = new(); }
            foreach (var (a, b, c) in tris)
            {
                if (bv.Count + 3 > VTX_BATCH) Flush();
                int ia = AddVert(bv, a), ib = AddVert(bv, b), ic = AddVert(bv, c);
                bt.Add((ia, ib, ic));
            }
            Flush();
            dlPlan.Add((key, bmp, batches));
        }
        byte[] vtxData = vw.ToArray();

        // ── 3. Texture block (RGBA16), offsets relative to vtxSegOffset, after the vertex data ──
        int texBase = Align(vtxSegOffset + vtxData.Length, 8);
        var tw2 = new N64BinaryWriter();
        foreach (var (key, bmp, _) in dlPlan)
        {
            if (bmp == null || texOffset.ContainsKey(key)) continue;
            // Per-texture cutout opt-in: a texture with a meaningful fraction of transparent texels
            // (foliage / fences / tree backdrops) keeps its 1-bit alpha and draws alpha-tested; a solid
            // wall carrying only a few stray-alpha ROM texels stays force-opaque so it doesn't get holes.
            bool cut = n64Hw && IsCutoutTexture(bmp);
            isCutout[key] = cut;
            if (cut) LastCutoutTextures.Add(key);
            texOffset[key] = texBase + tw2.Position;
            tw2.WriteBytes(Encode5551(bmp, forceOpaque: n64Hw && !cut));
            tw2.AlignTo(8);
        }
        byte[] texData = tw2.ToArray();

        // ── 4. Display lists: opaque (solid geometry) + a separate XLU list for the water surface ─────
        // Water goes in its OWN display list (room shape xluPtr): translucent geometry belongs in the XLU
        // buffer, AND the scroll draw config (OoT SDC_CALM_WATER / MM MAT_ANIM) binds the scroll segment
        // 0x08 in POLY_XLU_DISP — an OPA-list water DL's segment-DL call would run before seg 8 is bound.
        const ulong RM_OPAQUE = 0xE200001C0C1841F8UL, RM_CUTOUT = 0xE200001C0C1871F8UL;
        const ulong GEOM_MODE = 0xD900000500000005UL;   // SetGeometryMode(ZBUFFER|SHADE), no back-face cull
        const ulong OTHERMODE_H = 0xE300081300082000UL; // 1-CYCLE + persp + bilerp + TT_NONE
        const ulong SP_TEXTURE  = 0xD7000002FFFFFFFFUL; // SPTexture enable, scale 1.0

        // Emits one group's vertex-load + triangle commands into <paramref name="dw"/>.
        void EmitBatches(N64BinaryWriter dw, List<(int n, int vtxOff, List<(int, int, int)> tris)> batches)
        {
            foreach (var (n, vtxOff, tris) in batches)
            {
                uint hi = ((uint)G_VTX << 24) | ((uint)n << 12) | (uint)(n * 2);
                dw.WriteU32(hi); dw.WriteU32(((uint)seg << 24) | (uint)((vtxSegOffset + vtxOff) & 0x00FFFFFF));
                int ti = 0;
                while (ti < tris.Count)
                {
                    if (ti + 1 < tris.Count)
                    {
                        var (a0, b0, c0) = tris[ti]; var (a1, b1, c1) = tris[ti + 1];
                        dw.WriteU8(G_TRI2);
                        dw.WriteU8((byte)(a0 * 2)); dw.WriteU8((byte)(b0 * 2)); dw.WriteU8((byte)(c0 * 2)); dw.WriteU8(0);
                        dw.WriteU8((byte)(a1 * 2)); dw.WriteU8((byte)(b1 * 2)); dw.WriteU8((byte)(c1 * 2));
                        ti += 2;
                    }
                    else
                    {
                        var (a0, b0, c0) = tris[ti];
                        dw.WriteU8(G_TRI1); dw.WriteU8((byte)(a0 * 2)); dw.WriteU8((byte)(b0 * 2)); dw.WriteU8((byte)(c0 * 2)); dw.WriteU32(0);
                        ti++;
                    }
                }
            }
        }

        // Opaque display list — every group except water.
        var dw = new N64BinaryWriter();
        dw.WriteU64(PIPE_SYNC);
        dw.WriteU64(GEOM_MODE);
        dw.WriteU64(RM_OPAQUE);
        ulong curMode = RM_OPAQUE;
        dw.WriteU64(OTHERMODE_H);
        dw.WriteU64(SP_TEXTURE);
        foreach (var (key, bmp, batches) in dlPlan)
        {
            if (key == WaterKey) continue;   // water is emitted in the XLU list below
            ulong wantMode = isCutout.GetValueOrDefault(key) ? RM_CUTOUT : RM_OPAQUE;
            if (wantMode != curMode) { dw.WriteU64(wantMode); curMode = wantMode; }
            if (bmp != null && texOffset.TryGetValue(key, out int tOff))
            {
                dw.WriteU64(0xFC121824FF33FFFFUL);          // MODULATE TEXEL0×SHADE
                EmitTextureLoad(dw, seg, tOff, bmp.Width, bmp.Height);
                // Brush-authored MM scroll: run the AnimatedMaterial tile-scroll DL bound on segment 8+i.
                int si = scrolls == null || !key.StartsWith("b:", StringComparison.Ordinal) ? -1
                       : IndexOfScroll(scrolls, key[2..]);
                if (si >= 0) { dw.WriteU32(0xDE000000u); dw.WriteU32((uint)(0x08 + si) << 24); }
            }
            else
                dw.WriteU64(0xFC00000000041104UL);          // SHADE, SHADE
            EmitBatches(dw, batches);
        }
        dw.WriteU64(0xDF00000000000000UL);   // EndDisplayList

        // XLU display list — the water surface (empty when the room has no water).
        byte[] xluData = [];
        var water = dlPlan.FirstOrDefault(p => p.key == WaterKey);
        if (water.batches != null && water.bmp != null && texOffset.TryGetValue(WaterKey, out int wOff))
        {
            var xw = new N64BinaryWriter();
            xw.WriteU64(PIPE_SYNC);
            xw.WriteU64(GEOM_MODE);
            xw.WriteU64(OTHERMODE_H);
            xw.WriteU64(SP_TEXTURE);
            xw.WriteU64(RM_WATER_XLU);
            xw.WriteU64(COMBINE_WATER);                     // G_CC_MODULATEI_PRIM (TEXEL0 × PRIM, alpha = PRIM)
            xw.WriteU64(PRIM_WATER);                        // prim = white, alpha 128 (translucent)
            EmitTextureLoad(xw, seg, wOff, water.bmp.Width, water.bmp.Height);
            // Scroll: invoke the per-frame tile-scroll DL the scene's draw config binds on segment 0x08
            // (OoT SDC_CALM_WATER / MM MAT_ANIM). Gated so a target without that draw config never calls an
            // unbound segment.
            if (waterScroll) { xw.WriteU32(0xDE000000u); xw.WriteU32(0x08000000u); }
            EmitBatches(xw, water.batches);
            xw.WriteU64(0xDF00000000000000UL);   // EndDisplayList
            xluData = xw.ToArray();
        }

        return new DlResult(vtxData, texData, dw.ToArray(), xluData);
    }

    // Index of the scroll authored for texture <name>, or -1 (also its animated CPU segment = 8 + index).
    private static int IndexOfScroll(IReadOnlyList<TextureScroll> scrolls, string name)
    {
        for (int i = 0; i < scrolls.Count && i < 6; i++) if (scrolls[i].Name == name) return i;
        return -1;
    }

    // Standard F3DEX2 load-texture-block for an RGBA16 (fmt 0, siz 2) texture at seg:off.
    private static void EmitTextureLoad(N64BinaryWriter dw, byte seg, int off, int w, int h)
    {
        dw.WriteU32(0xFD100000u);                                   // SETTIMG fmt0 siz2 (16b) width-1=0
        dw.WriteU32(((uint)seg << 24) | (uint)(off & 0x00FFFFFF));
        dw.WriteU32(0xF5000000u | (2u << 19)); dw.WriteU32(7u << 24);   // SetTile LOADTILE
        dw.WriteU64(0xE600000000000000UL);                          // LoadSync
        int lrs = w * h - 1;
        dw.WriteU32(0xF3000000u); dw.WriteU32((7u << 24) | ((uint)lrs << 12) | (uint)CalcDxt(w));   // LoadBlock
        dw.WriteU64(0xE700000000000000UL);                          // PipeSync
        int line = ((w * 2) + 7) >> 3;
        dw.WriteU32(0xF5000000u | (2u << 19) | ((uint)line << 9));
        dw.WriteU32(((uint)Log2(h) << 14) | ((uint)Log2(w) << 4));  // SetTile RENDERTILE, mask = log2(dim)
        dw.WriteU32(0xF2000000u); dw.WriteU32(((uint)((w - 1) << 2) << 12) | (uint)((h - 1) << 2));   // SetTileSize
    }

    // Downscale a texture so it fits the N64's 4KB texture memory as RGBA16 (≤ 2048 texels). Rounds the
    // dimensions down to powers of two (so the F3DEX2 LoadBlock / tile-mask setup stays valid) and halves
    // the larger side until w*h ≤ 2048. Returns the original bitmap unchanged when it already fits — the
    // common case, so most textures pay nothing. N64-only; the SoH/Fast3D path keeps full resolution.
    private static Bitmap FitN64Tmem(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int nw = Pow2Floor(w), nh = Pow2Floor(h);
        while (nw * nh > 2048) { if (nw >= nh) nw = Math.Max(1, nw >> 1); else nh = Math.Max(1, nh >> 1); }
        if (nw == w && nh == h) return bmp;
        var dst = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(bmp, 0, 0, nw, nh);
        return dst;
    }

    private static int Pow2Floor(int v) { int p = 1; while (p * 2 <= v) p <<= 1; return p; }

    private static Vtx ToVtx(Vector3 p, Vector2 uv, int tw, int th, byte r, byte g, byte b)
    {
        // S10.5 texel coords; clamp so heavy tiling doesn't overflow s16.
        int s = (int)MathF.Round(uv.X * tw * 32f), t = (int)MathF.Round(uv.Y * th * 32f);
        return new Vtx((short)MathF.Round(p.X), (short)MathF.Round(p.Y), (short)MathF.Round(p.Z),
                       (short)Math.Clamp(s, -32768, 32767), (short)Math.Clamp(t, -32768, 32767), r, g, b);
    }

    private static int AddVert(List<Vtx> batch, Vtx v)
    {
        for (int i = 0; i < batch.Count; i++) if (batch[i] == v) return i;
        batch.Add(v); return batch.Count - 1;
    }

    private static void WriteVtx(N64BinaryWriter w, Vtx v)
    {
        w.WriteS16(v.X); w.WriteS16(v.Y); w.WriteS16(v.Z); w.WriteU16(0);
        w.WriteS16(v.S); w.WriteS16(v.T);
        w.WriteU8(v.R); w.WriteU8(v.G); w.WriteU8(v.B); w.WriteU8(v.A);
    }

    private static int Align(int v, int a) => (v + a - 1) & ~(a - 1);
    private static int Log2(int v) { int n = 0; while ((1 << n) < v && n < 15) n++; return n; }
    private static int CalcDxt(int w)
    {
        int bytesPerLine = w * 2;   // RGBA16
        if (bytesPerLine == 0) return 0;
        int wordsPerLine = (bytesPerLine + 7) >> 3;
        return wordsPerLine == 0 ? 0 : (2048 + wordsPerLine - 1) / wordsPerLine;
    }

    // A texture is a "cutout" (should draw alpha-tested) when a meaningful fraction of its texels are
    // (near-)transparent — genuine foliage / fence / tree-backdrop textures. A solid wall that merely
    // carries a handful of stray-alpha ROM texels stays well under the threshold and renders opaque, so a
    // blanket alpha-test never holes solid geometry (the original reason force-opaque existed). ≥12%.
    private static bool IsCutoutTexture(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w == 0 || h == 0) return false;
        long transparent = 0, total = (long)w * h;
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int px = System.Runtime.InteropServices.Marshal.ReadInt32(bd.Scan0, y * bd.Stride + x * 4);
                    if (((px >> 24) & 0xFF) < 128) transparent++;
                }
        }
        finally { bmp.UnlockBits(bd); }
        return total > 0 && (double)transparent / total >= 0.12;
    }

    /// <param name="forceOpaque">true (N64): set every texel's 1-bit alpha so solid brush walls stay
    /// opaque even when the source ROM texture carries stray transparent texels (banners/foliage).
    /// false (SoH): preserve the source alpha (alpha&lt;128 → transparent), the Fast3D-validated behaviour
    /// and what genuine cutout textures (foliage/trees) need.</param>
    private static byte[] Encode5551(Bitmap bmp, bool forceOpaque)
    {
        int w = bmp.Width, h = bmp.Height;
        var outp = new byte[w * h * 2];
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int px = System.Runtime.InteropServices.Marshal.ReadInt32(bd.Scan0, y * bd.Stride + x * 4);
                    int bb = px & 0xFF, gg = (px >> 8) & 0xFF, rr = (px >> 16) & 0xFF, aa = (px >> 24) & 0xFF;
                    int a1 = forceOpaque || aa >= 128 ? 1 : 0;
                    ushort p = (ushort)(((rr >> 3) << 11) | ((gg >> 3) << 6) | ((bb >> 3) << 1) | a1);
                    int o = (y * w + x) * 2; outp[o] = (byte)(p >> 8); outp[o + 1] = (byte)(p & 0xFF);
                }
        }
        finally { bmp.UnlockBits(bd); }
        return outp;
    }
}
