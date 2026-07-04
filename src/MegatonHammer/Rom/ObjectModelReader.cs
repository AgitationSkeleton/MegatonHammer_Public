using MegatonHammer.Textures;
using OpenTK.Mathematics;

namespace MegatonHammer.Rom;

/// <summary>
/// Decodes an actor's 3D model from its object (zobj) file into world-space triangles for
/// the editor. Two paths: a single display list (static models, e.g. the Eyeball Frog), and
/// a SkelAnime skeleton drawn in its rest pose (jointTable all zero) by walking the limb
/// hierarchy and offsetting each limb's display list by its accumulated joint position.
/// Segment 6 = the object itself. Companion to <see cref="RoomMeshReader"/>.
/// </summary>
public sealed class ObjectModelReader
{
    private const byte G_VTX = 0x01, G_TRI1 = 0x05, G_TRI2 = 0x06;
    private const byte G_DL = 0xDE, G_ENDDL = 0xDF;
    private const byte G_SETTIMG = 0xFD, G_SETTILE = 0xF5, G_SETTILESIZE = 0xF2, G_LOADTLUT = 0xF0;
    private const byte G_LOADBLOCK = 0xF3, G_LOADTILE = 0xF4;
    private const byte G_GEOMETRYMODE = 0xD9;
    private const byte G_SETPRIMCOLOR = 0xFA, G_SETENVCOLOR = 0xFB, G_SETCOMBINE = 0xFC;
    private const byte G_MTX = 0xDA;
    private const uint G_LIGHTING = 0x00020000;   // geometry-mode bit: lit (vtx bytes 12-14 are a normal)

    // F3DEX2 geometry mode. When G_LIGHTING is set, a vertex's bytes 12-14 are a signed normal (used
    // for shading), NOT an RGB colour — reading a normal as a colour is what made lit models (Link,
    // other characters) render in garbage rainbow colours. Static props keep the unlit default so
    // their baked vertex colours survive; characters are lit by default (see ReadSkeleton).
    private uint _geoMode;
    private static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(0.4f, 0.85f, 0.3f));

    private readonly byte[] _obj;
    private readonly int _objFileIndex;
    private readonly int _keepFileIndex;   // gameplay_keep (segment 4), or -1 — for shared textures
    private readonly Vtx[] _cache = new Vtx[32];
    private readonly List<MeshTri> _tris = [];

    // Current limb-to-model affine transform: modelPoint = _bx*v.X + _by*v.Y + _bz*v.Z + _t.
    // (_bx,_by,_bz are the basis columns; _t the translation.) Set per limb before running its
    // display list. With no animation the basis stays identity → translation-only rest pose.
    private Vector3 _bx = Vector3.UnitX, _by = Vector3.UnitY, _bz = Vector3.UnitZ, _t;

    // Flex-skeleton skinning. The game writes one matrix per limb-with-a-display-list, in pre-order,
    // into a buffer bound to segment 0x0D; a limb's DL then loads matrices from there by offset to
    // skin its vertices to several limbs. So a seg-0x0D offset maps to the Nth drawn limb (slot),
    // NOT the limb index. We precompute each limb's world transform and the slot→limb table, then on
    // G_MTX switch to the referenced limb's transform — otherwise Link's skinned arms/legs/head
    // detach. (matrix = 0x40 bytes.)
    private Affine[]? _limbXform;
    private int[]? _slotToLimb;
    private int _slotCount;
    private Affine[]? _seg0C;   // synthetic segment-0x0C body-segment matrix stack (Like-Like / Jabu tentacle)

    // Skin system (MM horses/En_Horse etc.): the skinned limbs' display lists load their vertices from
    // segment 0x08, a runtime buffer the game fills per frame. We precompute that buffer in bind pose
    // (each vertex = its limb-relative position + the limb's accumulated joint translation) into N64 Vtx
    // bytes and bind it here, so the existing DL runner draws the skinned mesh with no other changes.
    private byte[]? _skin8;

    // Each skin limb's full world transform (translation + frame-0 idle rotation), accumulated through
    // the limb hierarchy. The Skin system (Epona etc.) is animated by a normal SkelAnime joint table,
    // so without the idle pose's rotations the horse collapses into a bind-pose tangle. Null/identity
    // rotation reduces to the old translation-only bind pose.
    private Affine[]? _skinXform;

    private readonly struct Affine(Vector3 bx, Vector3 by, Vector3 bz, Vector3 t)
    {
        public readonly Vector3 Bx = bx, By = by, Bz = bz, T = t;
        public static readonly Affine Identity = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Zero);
        public Vector3 Apply(Vector3 v) => v.X * Bx + v.Y * By + v.Z * Bz + T;
        public Vector3 ApplyDir(Vector3 v) => v.X * Bx + v.Y * By + v.Z * Bz;
    }

    // _timg* is the pending image set by G_SETTIMG; it becomes the bound TEXTURE on G_LOADBLOCK/
    // G_LOADTILE and the bound PALETTE on G_LOADTLUT. Tracking the load (not just "last SETTIMG")
    // matters because some DLs (e.g. Link's hands) set the texture, load it, THEN set+load the
    // palette — so the last SETTIMG before drawing is the palette, not the texture.
    private int _timgFile = -1, _timgOff = -1, _texFile = -1, _texOff = -1, _palFile = -1, _palOff = -1;
    private int _fmt, _siz, _tw = 8, _th = 8;
    private int _eyeOff = -1, _mouthOff = -1;   // player face: seg 8 = eye, seg 9 = mouth base in the object
    private int _seg8Off = -1;                   // generic seg-8 texture base (boss door's per-temple emblem)
    // Overlay-mesh mode: when reading a display list embedded in an ACTOR OVERLAY file (Bg_Ganon_Otyuka's
    // platform, En_Kanban's sign…), its vertex/texture/branch pointers are VRAM-ABSOLUTE off the overlay's
    // load base, not segment-6. _ovlBase = that base (0 disables); a pointer in [_ovlBase, _ovlBase+_ovlLen)
    // resolves to file offset (ptr - _ovlBase) in the overlay bytes (which are _obj, at _objFileIndex).
    private uint _ovlBase, _ovlLen;
    // Per-actor texture-segment bindings (segment 0x08..0x0D → object-file offset). Many actors bind extra
    // textures to segments 8-D in their C draw code via gSPSegment (the tektite's carapace, enemy eyes, etc.);
    // the DL then SETTIMGs those segments. Without these the affected tris render untextured. -1 = unbound.
    private int[]? _segTex;
    private int[]? _segTexFile;   // per-seg8-D FILE index for CROSS-object binds (Bg_Mori_* → object_mori_tex); -1/null = own object
    private int _branchOff6 = -1;                // pending gsSPBranchLessZraw target (LOD detail DL, seg 6)
    private int _headLimb = -1, _headDl = -1;   // #4: composite NPC — draw this head DL at this limb (En_Hy)
    private bool _clampS, _clampT, _mirrorS, _mirrorT;   // current tile's wrap mode (clamp/mirror/repeat)
    // Per-tile descriptor state (F3DEX2 can render from any tile, not just tile 0).
    private readonly int[] _tileFmt = new int[8], _tileSiz = new int[8];
    private readonly bool[] _tileClampS = new bool[8], _tileClampT = new bool[8], _tileMirrorS = new bool[8], _tileMirrorT = new bool[8];
    private readonly int[] _tileShiftS = new int[8], _tileShiftT = new int[8];   // RDP UV shift per tile
    private readonly int[] _tileMaskS = new int[8], _tileMaskT = new int[8];     // masks/maskt per tile (wrap size = 1<<mask)
    private readonly int[] _tileLine = new int[8];   // 64-bit words per texel row (physical texture width source)
    private int _loadTexels;   // texels loaded by the last G_LOADBLOCK (physical height = texels / width)
    // The render tile's UV shift + upper-left offset, applied to texel coords in Emit like SoH's fast
    // interpreter (u/=1<<shift for 1..10, u*=1<<(16-shift) for 11..15, then u-=uls/4) — matches the
    // RoomMeshReader fix so actor textures using a tile shift map at the right scale/phase.
    private int _shiftS, _shiftT, _uls, _ult;

    // Colour-combiner state. Many actors tint their (often grayscale) textures with a primitive or
    // environment colour set in the display list — without applying it those surfaces render white/
    // grey. We approximate the RDP combiner: track prim/env colours and, from the combine mode,
    // whether they actually modulate the texel; if so multiply the surface colour by them. (Colours
    // an actor sets only in its C draw code — e.g. Link's tunic green — aren't in the data and can't
    // be captured here.) Defaults are white = no tint, so untinted models are unchanged.
    private Vector3 _primColor = Vector3.One, _envColor = Vector3.One;
    private bool _combineUsesPrim, _combineUsesEnv;

    private struct Vtx { public Vector3 P; public Vector3 C; public Vector2 T; }

    private ObjectModelReader(byte[] obj, int objFileIndex, int keepFileIndex = -1, int keep5FileIndex = -1)
    { _obj = obj; _objFileIndex = objFileIndex; _keepFileIndex = keepFileIndex; _keep5FileIndex = keep5FileIndex; }

    private readonly int _keep5FileIndex;   // the scene's keep object (segment 5: gameplay_field/dangeon_keep)

    /// <summary>Decodes a single display list at <paramref name="dlOffset"/> in the object.</summary>
    public static List<MeshTri> ReadDList(byte[] obj, int objFileIndex, int dlOffset, int keepFileIndex = -1, int keep5FileIndex = -1, int seg8Off = -1,
                                          IReadOnlyList<Vector3>? seg0CStack = null, int[]? segTex = null, int[]? segTexFile = null)
    {
        var r = new ObjectModelReader(obj, objFileIndex, keepFileIndex, keep5FileIndex) { _seg8Off = seg8Off, _segTex = segTex, _segTexFile = segTexFile };
        if (seg0CStack is { Count: > 0 })   // rest-pose translations for the segment-0x0C matrix stack
            r._seg0C = seg0CStack.Select(t => new Affine(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, t)).ToArray();
        if (ObjectSetsLighting(obj)) r._geoMode = G_LIGHTING;   // lit static prop (e.g. gossip stone)
        try { r.RunDl(dlOffset, 0); } catch { }
        return r._tris;
    }

    /// <summary>Decodes a display list embedded in an ACTOR OVERLAY file. <paramref name="vramBase"/> is
    /// the overlay's VRAM load base (gActorOverlayTable vramStart / the XML File BaseAddress); internal
    /// vertex/texture/branch pointers are VRAM-absolute off it. <paramref name="ovlFileIndex"/> is the
    /// overlay's ROM file index so its embedded textures decode.</summary>
    public static List<MeshTri> ReadOverlayDList(byte[] ovl, int ovlFileIndex, uint vramBase, int dlOffset)
    {
        var r = new ObjectModelReader(ovl, ovlFileIndex) { _ovlBase = vramBase, _ovlLen = (uint)ovl.Length };
        if (ObjectSetsLighting(ovl)) r._geoMode = G_LIGHTING;
        try { r.RunDl(dlOffset, 0); } catch { }
        return r._tris;
    }

    /// <summary>Runs several overlay display lists on ONE reader so texture/tile state set by a leading
    /// material DL carries into the geometry DL that follows it (the actor's Draw runs material then mesh
    /// as separate DLs). Offsets are executed in order; returns the accumulated triangles.</summary>
    public static List<MeshTri> ReadOverlayDLs(byte[] ovl, int ovlFileIndex, uint vramBase, params int[] offsets)
    {
        var r = new ObjectModelReader(ovl, ovlFileIndex) { _ovlBase = vramBase, _ovlLen = (uint)ovl.Length };
        if (ObjectSetsLighting(ovl)) r._geoMode = G_LIGHTING;
        foreach (int off in offsets) if (off >= 0) try { r.RunDl(off, 0); } catch { }
        return r._tris;
    }

    /// <summary>Offsets of an overlay's MATERIAL display lists — coherent, ENDDL-terminated DLs that bind a
    /// texture (G_SETTIMG) but emit no geometry (no G_VTX). A geometry DL is textured by running the nearest
    /// preceding material on the same reader (see <see cref="ReadOverlayDLs"/>).</summary>
    public static List<int> ScanOverlayMaterials(byte[] ovl, uint vramBase)
    {
        var mats = new List<int>();
        for (int p = 0; p + 8 <= ovl.Length; p += 8)
        {
            bool sawTimg = false, sawVtx = false; int end = -1;
            for (int q = p; q + 8 <= ovl.Length && q < p + 0x400; q += 8)
            {
                byte op = ovl[q];
                if (op == 0xDF) { end = q; break; }               // G_ENDDL
                if (!(op <= 0x07 || op >= 0xD7)) { end = -2; break; }
                if (op == 0x01) { sawVtx = true; break; }          // has geometry → not a pure material DL
                if (op == 0xFD) sawTimg = true;                    // G_SETTIMG
            }
            if (end >= 0 && sawTimg && !sawVtx) { mats.Add(p); p = end; }
        }
        return mats;
    }

    /// <summary>Version-independent scan of an actor overlay for its geometry display lists (the DL file
    /// offset differs per ROM revision, so we can't trust the decomp XML offsets). Returns each coherent,
    /// ENDDL-terminated DL that (a) references vertices in the overlay and (b) emits triangles, largest
    /// first. Nested sub-DLs reachable from a larger one are dropped so the caller gets the top-level draws.</summary>
    public static List<(int off, List<MeshTri> tris)> ScanOverlayGeometry(byte[] ovl, int ovlFileIndex, uint vramBase)
    {
        var found = new List<(int off, List<MeshTri> tris)>();
        uint len = (uint)ovl.Length;
        for (int p = 0; p + 8 <= ovl.Length; p += 8)
        {
            if (!IsCoherentDl(ovl, p, vramBase, len)) continue;
            var tris = ReadOverlayDList(ovl, ovlFileIndex, vramBase, p);
            if (tris.Count > 0) found.Add((p, tris));
        }
        // The coherence scan re-detects the SAME display list at many sub-offsets (any aligned start inside
        // it that still reaches ENDDL) — and on a big overlay a top-level DL that branches (G_DL) into
        // sub-DLs is ALSO detected at each sub-DL head. Collapse both: represent each DL by the SET of its
        // triangles (hashed by rounded vertex positions) and keep only MAXIMAL sets — a DL whose triangles
        // are all contained in a larger already-kept DL is a sub-DL / redundant re-detection and is dropped.
        static HashSet<long> TriSet(List<MeshTri> tris)
        {
            var s = new HashSet<long>();
            foreach (var t in tris)
            {
                long h = 17;
                foreach (var p in new[] { t.P0, t.P1, t.P2 })
                    h = h * 1000003 + (((long)MathF.Round(p.X) & 0xFFFF) << 32 | ((long)MathF.Round(p.Y) & 0xFFFF) << 16 | ((long)MathF.Round(p.Z) & 0xFFFF));
                s.Add(h);
            }
            return s;
        }
        var withSets = found.Select(f => (f.off, f.tris, set: TriSet(f.tris))).ToList();
        withSets.Sort((a, b) => b.set.Count - a.set.Count);   // largest first
        var kept = new List<(int off, List<MeshTri> tris)>();
        var keptSets = new List<HashSet<long>>();
        foreach (var f in withSets)
        {
            if (keptSets.Any(k => k.Count >= f.set.Count && f.set.IsSubsetOf(k))) continue;   // contained → drop
            kept.Add((f.off, f.tris)); keptSets.Add(f.set);
        }
        return kept;
    }

    // A run of F3DEX2 commands from `off` is coherent if every opcode is a real command (immediate 0x00-0x07
    // or RDP/DP 0xD7-0xFF), it terminates at G_ENDDL within a sane length, and at least one G_VTX points into
    // the overlay. That rejects decoding random .text/.data as a DL (which would emit garbage triangles).
    private static bool IsCoherentDl(byte[] d, int off, uint vramBase, uint len)
    {
        bool sawVtx = false;
        for (int p = off; p + 8 <= d.Length && p < off + 0x800; p += 8)
        {
            byte op = d[p];
            if (op == 0xDF) return sawVtx && p > off;    // G_ENDDL
            if (!(op <= 0x07 || op >= 0xD7)) return false;
            if (op == 0x01)   // G_VTX — its address must land inside the overlay
            {
                uint a = U32(d, p + 4);
                if (a < vramBase || a >= vramBase + len) return false;
                sawVtx = true;
            }
        }
        return false;
    }

    // Does the object's material setup turn lighting ON anywhere? A lit static model (no skeleton, so
    // it goes through ReadDList) keeps its normals in vtx bytes 12-14 and must be shaded, not read as
    // colour — otherwise the normals render as a rainbow gradient (the gossip stone). The material DL
    // that enables lighting is a separate DL the actor draws before the geometry, which we don't run,
    // so detect it by scanning for a G_GEOMETRYMODE (0xD9) whose set-word includes G_LIGHTING.
    private static bool ObjectSetsLighting(byte[] d)
    {
        for (int p = 0; p + 8 <= d.Length; p += 8)
            if (d[p] == 0xD9 && (U32(d, p + 4) & G_LIGHTING) != 0) return true;
        return false;
    }

    /// <summary>Runs several OBJECT display lists on ONE reader so texture/tile state set by a leading
    /// material DL carries into the geometry DL(s) that follow (e.g. the gossip stone's gGossipStoneMaterialDL
    /// then gGossipStoneDL — drawn as separate DLs, so reading the geometry alone gives untextured tris).</summary>
    public static List<MeshTri> ReadDLs(byte[] obj, int objFileIndex, int[] offsets, int keepFileIndex = -1, int keep5FileIndex = -1, int[]? segTex = null)
    {
        var r = new ObjectModelReader(obj, objFileIndex, keepFileIndex, keep5FileIndex) { _segTex = segTex };
        if (ObjectSetsLighting(obj)) r._geoMode = G_LIGHTING;
        foreach (int off in offsets) if (off >= 0) try { r.RunDl(off, 0); } catch { }
        return r._tris;
    }

    /// <summary>Decodes <paramref name="count"/> display lists laid out back-to-back from
    /// <paramref name="dlOffset"/> (e.g. a tree's trunk DL immediately followed by its leaves DL).</summary>
    public static List<MeshTri> ReadDListChain(byte[] obj, int objFileIndex, int dlOffset, int count, int keepFileIndex = -1, int keep5FileIndex = -1)
    {
        var r = new ObjectModelReader(obj, objFileIndex, keepFileIndex, keep5FileIndex);
        if (ObjectSetsLighting(obj)) r._geoMode = G_LIGHTING;
        int off = dlOffset;
        for (int i = 0; i < count && off >= 0 && off + 8 <= obj.Length; i++)
        {
            try { r.RunDl(off, 0); } catch { }
            off = EndOfDl(obj, off);     // the word after this DL's G_ENDDL
        }
        return r._tris;
    }

    // First G_ENDDL at/after off (sub-DLs are pointer-referenced, never inlined), +8 = next DL start.
    private static int EndOfDl(byte[] d, int off)
    {
        for (int p = off; p + 8 <= d.Length; p += 8)
            if (d[p] == G_ENDDL) return p + 8;
        return -1;
    }

    /// <summary>
    /// Best-effort model decode without knowing the actor's draw code: render the object's
    /// auto-detected skeleton in rest pose if one is found, else fall back to the explicit
    /// <paramref name="dlOffset"/> (if given). Returns null/empty if nothing renderable.
    /// </summary>
    public static List<MeshTri> ReadBestModel(byte[] obj, int objFileIndex, int? dlOffset = null,
                                              short[]? poseOverride = null, int keepFileIndex = -1,
                                              Vector3? envOverride = null, int eyeOff = -1, int mouthOff = -1,
                                              int keep5FileIndex = -1, int animFrame0Offset = -1,
                                              int headLimb = -1, int headDl = -1, int[]? segTex = null,
                                              int skelOffset = -1, int[]? segTexFile = null, byte[]? animObj = null)
    {
        // skelOffset overrides the auto-detected skeleton — for actors with MORE THAN ONE skeleton whose
        // resting form isn't the first one FindSkeleton picks (e.g. Kaepora Gaebora perches on its second,
        // gOwlPerchingSkel, not the flying gOwlFlyingSkel that scores highest).
        int skel = skelOffset >= 0 ? skelOffset : FindSkeleton(obj);
        if (skel >= 0)
        {
            var tris = ReadSkeleton(obj, objFileIndex, skel, poseOverride, keepFileIndex, envOverride, eyeOff, mouthOff, keep5FileIndex, animFrame0Offset, headLimb, headDl, segTex, animObj);
            if (tris.Count > 0) return tris;
        }
        // Skin skeleton (MM horses & other skinned actors have no standard SkelAnime skeleton).
        int skinSkel = FindSkinSkeleton(obj);
        if (skinSkel >= 0)
        {
            var tris = ReadSkinModel(obj, objFileIndex, skinSkel, keepFileIndex, keep5FileIndex, animFrame0Offset);
            if (tris.Count > 0) return tris;
        }
        if (dlOffset is int off)
        {
            var tris = ReadDList(obj, objFileIndex, off, keepFileIndex, keep5FileIndex, segTex: segTex, segTexFile: segTexFile);
            if (tris.Count > 0) return tris;
        }
        // No skeleton and no usable hint (the common case for MM, whose render DB is sparse):
        // auto-detect the object's largest static display list and render that.
        int auto = FindBestDisplayList(obj);
        if (auto >= 0) return ReadDList(obj, objFileIndex, auto, keepFileIndex, keep5FileIndex, segTex: segTex, segTexFile: segTexFile);
        return [];
    }

    // ── Skin system (MM horses & other skinned actors) ──────────────────────────────────────────────
    // A Skin skeleton's limbs are SkinLimb (size 0x10): jointPos[6], child, sibling, s32 segmentType,
    // void* segment. The skinned limbs (type 4) own a SkinAnimatedLimbData whose display list reads its
    // vertices from a runtime segment-8 buffer; we rebuild that buffer in bind pose. FindSkinSkeleton
    // scans for a skeleton header whose limbs validate as SkinLimb.
    public static List<MeshTri> ReadSkinModel(byte[] obj, int objFileIndex, int skelHeaderOffset,
                                              int keepFileIndex = -1, int keep5FileIndex = -1, int animFrame0Offset = -1)
    {
        var r = new ObjectModelReader(obj, objFileIndex, keepFileIndex, keep5FileIndex);
        try { r.RunSkin(skelHeaderOffset, animFrame0Offset); } catch { }
        return r._tris;
    }

    private void RunSkin(int skelHeaderOffset, int animFrame0Offset = -1)
    {
        if (skelHeaderOffset < 0 || skelHeaderOffset + 5 > _obj.Length) return;
        int limbArr = Seg6(_obj, skelHeaderOffset);
        int limbCount = _obj[skelHeaderOffset + 4];
        if (limbArr < 0 || limbCount < 1 || limbCount > 64) return;

        // Resolve the limb pointers + read each SkinLimb.
        var limbOff = new int[limbCount];
        for (int i = 0; i < limbCount; i++) limbOff[i] = Seg6(_obj, limbArr + i * 4);

        var jointPos = new Vector3[limbCount];
        var child = new int[limbCount];
        var sibling = new int[limbCount];
        var segType = new int[limbCount];
        var segOff = new int[limbCount];
        for (int i = 0; i < limbCount; i++)
        {
            int o = limbOff[i];
            if (o < 0 || o + 0x10 > _obj.Length) return;
            jointPos[i] = new Vector3((short)U16(_obj, o), (short)U16(_obj, o + 2), (short)U16(_obj, o + 4));
            child[i] = _obj[o + 6]; sibling[i] = _obj[o + 7];
            segType[i] = (int)U32(_obj, o + 8);
            segOff[i] = Seg6(_obj, o + 0xC);
        }

        // Pose the skin skeleton with frame 0 of its idle animation — the Skin system (Epona) is driven
        // by an ordinary SkelAnime joint table, so in bind pose (all rotations zero) the horse's legs and
        // neck never stand up and she collapses into a tangle, exactly like a humanoid SkelAnime skeleton.
        // Prefer the hand-pinned idle anim (gEponaIdleAnim), then an auto-detected one, then bind pose.
        short[]? joints = animFrame0Offset >= 0 ? ReadAnimFrame0(_obj, animFrame0Offset, limbCount) : null;
        joints ??= FindFrame0JointTable(_obj, limbCount);

        // Each limb's full world transform (translation + ZYX rotation), accumulated through the hierarchy
        // — mirrors SkelAnime_DrawFlex. Root translation comes from the animation (jointTable[0]); child
        // limbs use their static SkinLimb jointPos. With no joints (identity rotation) this reduces to the
        // old translation-only bind pose, so non-posable skinned actors are unaffected.
        var xform = new Affine[limbCount];
        var stack = new Stack<(int limb, Affine parent)>();
        stack.Push((0, Affine.Identity));
        var seen = new bool[limbCount];
        while (stack.Count > 0)
        {
            var (li, parent) = stack.Pop();
            if (li < 0 || li >= limbCount || seen[li]) continue;
            seen[li] = true;
            Vector3 pos = (li == 0 && joints != null)
                ? new Vector3(joints[0], joints[1], joints[2])
                : jointPos[li];
            Affine a = Compose(parent, pos, Rot(joints, li));
            xform[li] = a;
            if (sibling[li] != 0xFF) stack.Push((sibling[li], parent));
            if (child[li] != 0xFF) stack.Push((child[li], a));
        }
        _skinXform = xform;

        for (int i = 0; i < limbCount; i++)
        {
            if (segType[i] == SKIN_LIMB_TYPE_ANIMATED && segOff[i] >= 0)
                DrawSkinAnimatedLimb(segOff[i]);
            else if (segType[i] == SKIN_LIMB_TYPE_NORMAL && segOff[i] >= 0)
            {
                // A plain display list drawn at the limb's full (posed) transform.
                _skin8 = null;
                SetXform(xform[i]);
                _geoMode = 0;
                RunDl(segOff[i], 0);
            }
        }
    }

    private const int SKIN_LIMB_TYPE_ANIMATED = 4, SKIN_LIMB_TYPE_NORMAL = 11;

    private void DrawSkinAnimatedLimb(int dataOff)
    {
        if (dataOff + 0xC > _obj.Length) return;
        int totalVtx = U16(_obj, dataOff);
        int modifCount = U16(_obj, dataOff + 2);
        int modifs = Seg6(_obj, dataOff + 4);
        int dlist = Seg6(_obj, dataOff + 8);
        if (totalVtx < 1 || totalVtx > 4096 || modifs < 0 || dlist < 0) return;

        var buf = new byte[totalVtx * 16];
        for (int m = 0; m < modifCount; m++)
        {
            int mo = modifs + m * 0x10;
            if (mo + 0x10 > _obj.Length) break;
            int vtxCount = U16(_obj, mo);
            int transformCount = U16(_obj, mo + 2);
            int skinVerts = Seg6(_obj, mo + 8);
            int transforms = Seg6(_obj, mo + 0xC);
            if (skinVerts < 0 || transforms < 0 || transformCount < 1) continue;

            // Vertex position = its limb-relative offset(s) placed by the limb's world matrix (the game
            // transforms each SkinTransformation position by limbMatrices[limbIndex]). With identity
            // rotation this reduces to (x,y,z) + the limb's translation — the old bind behaviour.
            Vector3 pos;
            if (transformCount == 1)
            {
                int t = transforms;
                int li = _obj[t];
                pos = SkinLimbApply(li, new Vector3((short)U16(_obj, t + 2), (short)U16(_obj, t + 4), (short)U16(_obj, t + 6)));
            }
            else
            {
                pos = Vector3.Zero;
                for (int k = 0; k < transformCount; k++)
                {
                    int t = transforms + k * 0xA;
                    if (t + 0xA > _obj.Length) break;
                    int li = _obj[t];
                    float scale = _obj[t + 8] * 0.01f;
                    var local = SkinLimbApply(li, new Vector3((short)U16(_obj, t + 2), (short)U16(_obj, t + 4), (short)U16(_obj, t + 6)));
                    pos += local * scale;
                }
            }

            for (int v = 0; v < vtxCount; v++)
            {
                int so = skinVerts + v * 0xA;
                if (so + 0xA > _obj.Length) break;
                int idx = U16(_obj, so);
                if (idx >= totalVtx) continue;
                int e = idx * 16;
                short px = (short)Math.Clamp(MathF.Round(pos.X), -32768, 32767);
                short py = (short)Math.Clamp(MathF.Round(pos.Y), -32768, 32767);
                short pz = (short)Math.Clamp(MathF.Round(pos.Z), -32768, 32767);
                buf[e] = (byte)(px >> 8); buf[e + 1] = (byte)px;
                buf[e + 2] = (byte)(py >> 8); buf[e + 3] = (byte)py;
                buf[e + 4] = (byte)(pz >> 8); buf[e + 5] = (byte)pz;
                buf[e + 8] = _obj[so + 2]; buf[e + 9] = _obj[so + 3];   // s
                buf[e + 10] = _obj[so + 4]; buf[e + 11] = _obj[so + 5]; // t
                buf[e + 12] = 0xFF; buf[e + 13] = 0xFF; buf[e + 14] = 0xFF; buf[e + 15] = _obj[so + 9];
            }
        }

        _skin8 = buf;
        SetXform(Affine.Identity);
        _geoMode = 0;   // unlit: TEXEL0 x white shade shows the texture's true colour
        RunDl(dlist, 0);
        _skin8 = null;
    }

    // Place a skin limb's local offset by that limb's full world transform (translation + idle rotation).
    private Vector3 SkinLimbApply(int li, Vector3 local) =>
        (_skinXform != null && li >= 0 && li < _skinXform.Length) ? _skinXform[li].Apply(local) : local;

    /// <summary>Offset of a Skin skeleton header (limbs validate as SkinLimb), or -1. A SkinLimb has a
    /// segmentType field (offset 8) that is 0, 4 (animated) or 11 (normal) — distinct from SkelAnime
    /// limbs whose offset-8 is a segment-6 display-list pointer (high byte 0x06).</summary>
    public static int FindSkinSkeleton(byte[] obj)
    {
        int best = -1, bestMesh = 0;
        for (int p = 0; p + 5 <= obj.Length; p += 4)
        {
            uint segPtr = U32(obj, p);
            if ((segPtr >> 24) != 6) continue;
            int arr = (int)(segPtr & 0xFFFFFF);
            int limbCount = obj[p + 4];
            if (arr <= 0 || arr + limbCount * 4 > obj.Length || limbCount < 3 || limbCount > 64) continue;
            bool ok = true; int mesh = 0;
            for (int i = 0; i < limbCount && ok; i++)
            {
                int lp = Seg6(obj, arr + i * 4);
                if (lp < 0 || lp + 0x10 > obj.Length) { ok = false; break; }
                int st = (int)U32(obj, lp + 8);
                // Valid SkinLimb segmentTypes: 0 = root/none, 4 = animated, 11 = normal (a Gfx*), and 5 =
                // a non-drawing joint node MM's Skin_DrawImpl skips (observed in Epona). Reject pointers /
                // large values — those are SkelAnime limbs, not skin.
                if (st is not (0 or 4 or 5 or SKIN_LIMB_TYPE_NORMAL)) { ok = false; break; }
                if (st is SKIN_LIMB_TYPE_ANIMATED or SKIN_LIMB_TYPE_NORMAL) mesh++;
            }
            // Keep the candidate with the most drawable (animated|normal) limbs; require a handful so a
            // coincidental small match can't win.
            if (ok && mesh > bestMesh) { best = p; bestMesh = mesh; }
        }
        return bestMesh >= 3 ? best : -1;
    }

    /// <summary>
    /// Finds the offset of the object's largest clean F3DEX2 display list, for objects whose draw
    /// entry point we don't know (no skeleton, no render-DB hint). Scans every 8-aligned offset and
    /// keeps the one that decodes to the most triangles. A "clean" DL uses only valid opcodes
    /// (0x00–0x07 or 0xD7–0xFF), has sane G_VTX counts, and ends at a G_ENDDL — random object data
    /// (vertices, textures, skeleton tables) trips the opcode check almost immediately.
    /// </summary>
    public static int FindBestDisplayList(byte[] d)
    {
        int best = -1, bestTris = 1;     // accept a clean 2-tri quad (portcullis, lilypad, firewall…);
                                         // ScoreDl already requires a valid VTX+TRI+ENDDL structure
        for (int p = 0; p + 8 <= d.Length; p += 8)
        {
            int tris = ScoreDl(d, p);
            if (tris > bestTris) { bestTris = tris; best = p; }
        }
        return best;
    }

    // Triangle count if [off..G_ENDDL] is a clean display list, else -1. Interior starts of a real
    // DL score lower than its true start, so picking the max naturally lands on the DL head.
    private static int ScoreDl(byte[] d, int off)
    {
        int tris = 0, verts = 0;
        for (int p = off; p + 8 <= d.Length; p += 8)
        {
            byte op = d[p];
            if (op > 0x07 && op < 0xD7) return -1;             // not a valid F3DEX2 opcode
            switch (op)
            {
                case G_ENDDL: return verts > 0 && tris > 0 ? tris : -1;
                case G_VTX:
                    int n = (int)((U32(d, p) >> 12) & 0xFF);
                    if (n == 0 || n > 32) return -1;
                    verts += n;
                    break;
                case G_TRI1: tris++; break;
                case G_TRI2: case 0x07: tris += 2; break;   // 0x07 = G_QUAD
            }
            if (p - off > 0x6000) return -1;                  // runaway: no terminator in range
        }
        return -1;
    }

    /// <summary>
    /// Scans the object for a plausible (Flex)SkeletonHeader: a seg-6 pointer to an array of N
    /// seg-6 limb pointers (N = the following byte), each limb being a valid LodLimb/StandardLimb.
    /// Returns the header offset with the most valid limbs, or -1. Avoids needing the actor's code.
    /// </summary>
    public static int FindSkeleton(byte[] obj)
    {
        int best = -1, bestScore = 0;
        for (int p = 0; p + 8 <= obj.Length; p += 4)
        {
            int arr = Seg6(obj, p);
            int count = obj[p + 4];
            // Allow large boss skeletons: Queen Gohma's gGohmaSkel has 85 limbs (King Dodongo /
            // Barinade are also big). FindSkeleton still requires EVERY limb to validate and scores by
            // renderable limbs first, so a high cap won't pick up small sub-models or noise.
            if (arr < 0 || count < 3 || count > 127) continue;          // sane limb count
            if (arr + count * 4 > obj.Length) continue;

            // Every limb POINTER must be in range (cheap structural gate).
            bool ptrsOk = true;
            for (int i = 0; i < count; i++)
            {
                int limb = Seg6(obj, arr + i * 4);
                if (limb < 0 || limb + 12 > obj.Length) { ptrsOk = false; break; }
            }
            if (!ptrsOk) continue;

            // Validate by HIERARCHY TRAVERSAL, exactly as SkelAnime draws: walk child/sibling links from
            // limb 0 and require only the REACHABLE limbs to be well-formed. Some objects over-declare
            // limbCount with a trailing unreferenced garbage slot (e.g. object_fsn's 18th limb pointer
            // aliases the pointer array, giving child=50/sibling=28); the game never traverses it, so the
            // old "every limb must validate" linear check wrongly threw the whole valid skeleton away and
            // the actor (the Curiosity Shop man) rendered as an unposed tangle. Score by reachable limbs
            // that carry a display list, so an empty structural match still loses to the true skeleton.
            var seen = new bool[count];
            var stack = new Stack<int>();
            stack.Push(0);
            int reachable = 0, withDl = 0; bool ok = true;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (i == 0xFF || i >= count || seen[i]) continue;
                seen[i] = true; reachable++;
                int limb = Seg6(obj, arr + i * 4);
                byte child = obj[limb + 6], sibling = obj[limb + 7];
                uint dl = U32(obj, limb + 8);
                if (dl != 0 && (dl >> 24) != 6) { ok = false; break; }       // a reachable limb's DL must be seg-6
                if (dl != 0 && (int)(dl & 0xFFFFFF) < obj.Length) withDl++;
                if (child != 0xFF) { if (child >= count) { ok = false; break; } stack.Push(child); }
                if (sibling != 0xFF) { if (sibling >= count) { ok = false; break; } stack.Push(sibling); }
            }
            // Prefer the candidate whose reachable limbs actually carry display lists (the real renderable
            // skeleton), then the most reachable limbs — so a stray empty header (withDl == 0, e.g. the
            // 85-limb noise match in object_goma) loses to the true skeleton (gGohmaSkel / gFsnSkel).
            if (ok && reachable >= 3)
            {
                int score = withDl * 1000 + reachable;
                if (score > bestScore) { bestScore = score; best = p; }
            }
        }
        return best;
    }

    /// <summary>
    /// Decodes a SkelAnime skeleton in rest pose. <paramref name="skelHeaderOffset"/> points at a
    /// (Flex)SkeletonHeader: {seg ptr to limb-pointer array; u8 limbCount}. Each limb is
    /// {Vec3s jointPos; u8 child; u8 sibling; u32 dListSeg}.
    /// </summary>
    /// <summary>Diagnostic counters (self-test): skeletons drawn, and how many were posed with a
    /// detected animation rather than the rest-pose fallback.</summary>
    public static int SkeletonsRead, SkeletonsPosedWithAnim;

    public static List<MeshTri> ReadSkeleton(byte[] obj, int objFileIndex, int skelHeaderOffset,
                                             short[]? poseOverride = null, int keepFileIndex = -1,
                                             Vector3? envOverride = null, int eyeOff = -1, int mouthOff = -1,
                                             int keep5FileIndex = -1, int animFrame0Offset = -1,
                                             int headLimb = -1, int headDl = -1, int[]? segTex = null, byte[]? animObj = null)
    {
        // envOverride seeds the environment colour the way an actor's C draw code does before drawing
        // (e.g. the player sets it to the tunic colour) — the combiner then tints the cloth limbs.
        var r = new ObjectModelReader(obj, objFileIndex, keepFileIndex, keep5FileIndex)
        { _geoMode = G_LIGHTING, _envColor = envOverride ?? Vector3.One, _eyeOff = eyeOff, _mouthOff = mouthOff,
          _headLimb = headLimb, _headDl = headDl, _segTex = segTex };  // characters are lit
        try
        {
            if (skelHeaderOffset + 5 > obj.Length) return r._tris;
            int limbArr = Seg6(obj, skelHeaderOffset);
            int limbCount = obj[skelHeaderOffset + 4];
            if (limbArr < 0 || limbCount <= 0 || limbCount > 127) return r._tris;   // Queen Gohma = 85 limbs
            SkeletonsRead++;

            // Pose the skeleton with frame 0 of its idle animation, exactly as the game's SkelAnime
            // draw does — the bind pose (all rotations zero) is a meaningless tangle for humanoid
            // skeletons (their bones are authored along +X and only the animation's rotations stand
            // them up). poseOverride supplies the frame-0 joint table directly (used for the player,
            // whose idle animation lives in the external link_animetion file, not object_link_boy);
            // otherwise look for an animation embedded in this object. The flattened joint format is
            // [rootPos, limb0rot, … limbN-1rot] = (limbCount+1) Vec3s.
            // Pose priority: explicit joint table (player) > explicit idle-anim offset (hand-pinned
            // actors) > auto-detected animation. Each falls back to the next if it yields nothing, so a
            // wrong pin can't make a posable skeleton worse than the auto-detect.
            short[]? joints = poseOverride is { } po && po.Length >= (limbCount + 1) * 3 ? po : null;
            // The idle animation may live in a DIFFERENT object than the skeleton (En_Ossan's Kokiri/Goron/
            // Zora shopkeepers: skeleton in object_km1/of1d/zo, anim in object_masterkokiri/golon/zoora).
            joints ??= animFrame0Offset >= 0 ? ReadAnimFrame0(animObj ?? obj, animFrame0Offset, limbCount) : null;
            joints ??= FindFrame0JointTable(obj, limbCount);
            if (joints != null) SkeletonsPosedWithAnim++;

            // Precompute every limb's world transform and the flex slot→limb table (drawn-limb order),
            // so a skinned DL's G_MTX (segment 0x0D) can resolve to the right limb's matrix.
            r._limbXform = new Affine[limbCount];
            r._slotToLimb = new int[limbCount];
            r.ComputeLimbXforms(limbArr, limbCount, 0, Affine.Identity, joints, new bool[limbCount]);

            r.DrawRoot(limbArr, limbCount, joints, new bool[limbCount]);
        }
        catch { }
        return r._tris;
    }

    // Pre-pass mirroring the game's flex draw: walk the hierarchy (child before sibling) storing each
    // limb's world transform, and assign a skinning slot to every limb that has a display list — the
    // game writes its matrices to segment 0x0D in exactly this order. index 0 = root (uses the
    // animation's root translation).
    private void ComputeLimbXforms(int limbArr, int limbCount, int index, Affine parent, short[]? joints, bool[] visited)
    {
        while (index != 0xFF && index < limbCount)
        {
            if (visited[index]) return;
            visited[index] = true;
            int ptrSlot = limbArr + index * 4;
            if (ptrSlot + 4 > _obj.Length) return;
            int limb = Seg6(_obj, ptrSlot);
            if (limb < 0 || limb + 12 > _obj.Length) return;

            Vector3 pos = index == 0 && joints != null
                ? new Vector3(joints[0], joints[1], joints[2])
                : new Vector3((short)U16(_obj, limb), (short)U16(_obj, limb + 2), (short)U16(_obj, limb + 4));
            Affine a = Compose(parent, pos, Rot(joints, index));
            _limbXform![index] = a;

            if (Seg6(_obj, limb + 8) >= 0 && _slotToLimb != null && _slotCount < _slotToLimb.Length)
                _slotToLimb[_slotCount++] = index;   // this limb consumes the next seg-0x0D matrix slot

            byte child = _obj[limb + 6], sibling = _obj[limb + 7];
            if (child != 0xFF) ComputeLimbXforms(limbArr, limbCount, child, a, joints, visited);
            index = sibling;
        }
    }

    // Root limb (skeleton[0]): its translation is jointTable[0] and rotation jointTable[1]; child
    // limbs use their static jointPos plus their own rotation. Mirrors SkelAnime_DrawOpa.
    private void DrawRoot(int limbArr, int limbCount, short[]? joints, bool[] visited)
    {
        int limb = Seg6(_obj, limbArr);
        if (limb < 0 || limb + 12 > _obj.Length) return;
        visited[0] = true;
        Vector3 pos = joints != null
            ? new Vector3(joints[0], joints[1], joints[2])
            : new Vector3((short)U16(_obj, limb), (short)U16(_obj, limb + 2), (short)U16(_obj, limb + 4));
        Affine a = Compose(Affine.Identity, pos, Rot(joints, 0));
        SetXform(a);
        int dl = Seg6(_obj, limb + 8);
        if (dl >= 0) RunDl(dl, 0);
        byte child = _obj[limb + 6];
        if (child != 0xFF) DrawLimb(limbArr, limbCount, child, a, joints, visited);
    }

    // Recursively draw a non-root limb and its siblings/children with full affine transforms
    // (translation + frame-0 rotation). <paramref name="visited"/> guards against cyclic
    // child/sibling indices (a mis-detected skeleton would otherwise recurse forever).
    private void DrawLimb(int limbArr, int limbCount, int index, Affine parent, short[]? joints, bool[] visited)
    {
        while (index != 0xFF && index < limbCount)
        {
            if (visited[index]) return;     // cycle — stop
            visited[index] = true;

            int ptrSlot = limbArr + index * 4;
            if (ptrSlot + 4 > _obj.Length) return;
            int limb = Seg6(_obj, ptrSlot);
            if (limb < 0 || limb + 12 > _obj.Length) return;

            var pos = new Vector3((short)U16(_obj, limb), (short)U16(_obj, limb + 2), (short)U16(_obj, limb + 4));
            byte child = _obj[limb + 6], sibling = _obj[limb + 7];
            Affine a = Compose(parent, pos, Rot(joints, index));
            // #4: composite NPC head swap — at the head limb draw the variant's head DL (En_Hy picks a head
            // per type that the game's OverrideLimbDraw substitutes here) instead of the skeleton's default.
            if (index == _headLimb && _headDl >= 0) { SetXform(a); RunDl(_headDl, 0); }
            else { int dl = Seg6(_obj, limb + 8); if (dl >= 0) { SetXform(a); RunDl(dl, 0); } }

            if (child != 0xFF) DrawLimb(limbArr, limbCount, child, a, joints, visited);
            index = sibling;
        }
    }

    private void SetXform(Affine a) { _bx = a.Bx; _by = a.By; _bz = a.Bz; _t = a.T; }

    // Rotation (binang Vec3s) for limb `limbIndex`, read from joint-table entry limbIndex+1.
    private static Vector3 Rot(short[]? joints, int limbIndex)
    {
        if (joints == null) return Vector3.Zero;
        int j = 3 * (limbIndex + 1);
        return j + 2 < joints.Length ? new Vector3(joints[j], joints[j + 1], joints[j + 2]) : Vector3.Zero;
    }

    private const float BinangToRad = (float)(Math.PI * 2.0 / 65536.0);

    // child = parent · Translate(pos) · Rz · Ry · Rx (Matrix_TranslateRotateZYX). The rotation is
    // built as R = Rz·Ry·Rx so that R·v applies X first, matching the decomp's ZYX convention.
    private static Affine Compose(Affine parent, Vector3 pos, Vector3 binangRot)
    {
        float x = binangRot.X * BinangToRad, y = binangRot.Y * BinangToRad, z = binangRot.Z * BinangToRad;
        float sx = MathF.Sin(x), cx = MathF.Cos(x), sy = MathF.Sin(y), cy = MathF.Cos(y), sz = MathF.Sin(z), cz = MathF.Cos(z);
        var rc0 = new Vector3(cz * cy, sz * cy, -sy);
        var rc1 = new Vector3(cz * sy * sx - sz * cx, sz * sy * sx + cz * cx, cy * sx);
        var rc2 = new Vector3(cz * sy * cx + sz * sx, sz * sy * cx - cz * sx, cy * cx);
        return new Affine(parent.ApplyDir(rc0), parent.ApplyDir(rc1), parent.ApplyDir(rc2), parent.Apply(pos));
    }

    /// <summary>
    /// Scans the object for a SkelAnime AnimationHeader { s16 frameCount; s16 pad; seg* frameData;
    /// seg* jointIndices; u16 staticIndexMax } and returns its frame-0 joint table (Vec3s ×
    /// (limbCount+1), flattened: entry 0 = root translation, entries 1..limbCount = limb rotations),
    /// or null if no convincing animation is embedded. Conservative: every joint index must address
    /// valid frame data and the match must have several animated (dynamic) joints, so we never apply
    /// a mis-detected pose — the rest-pose fallback covers everything else.
    /// </summary>
    /// <summary>Reads frame 0's joint table from a SPECIFIC AnimationHeader offset (used when the
    /// auto-detector can't single out the right idle animation — e.g. Queen Gohma's gGohmaStandAnim).</summary>
    public static short[]? ReadAnimFrame0(byte[] obj, int animOffset, int limbCount)
    {
        int entries = limbCount + 1;
        if (animOffset < 0 || animOffset + 16 > obj.Length) return null;
        int fd = Seg6(obj, animOffset + 4), ji = Seg6(obj, animOffset + 8);
        if (fd < 0 || ji < 0 || ji + entries * 6 > obj.Length) return null;
        var joints = new short[entries * 3];
        for (int k = 0; k < entries; k++)
            for (int ax = 0; ax < 3; ax++)
            {
                int idx = (int)U16(obj, ji + (k * 3 + ax) * 2);
                int at = fd + idx * 2;   // frame 0: the static value, or the first dynamic frame
                joints[k * 3 + ax] = at + 2 <= obj.Length ? (short)U16(obj, at) : (short)0;
            }
        return joints;
    }

    public static short[]? FindFrame0JointTable(byte[] obj, int limbCount)
    {
        int entries = limbCount + 1;
        int bestOff = -1, bestScore = 2;     // need >2 dynamic joints to beat coincidence
        for (int p = 0; p + 16 <= obj.Length; p += 4)
        {
            short frameCount = (short)U16(obj, p);
            if (frameCount < 1 || frameCount > 4000 || U16(obj, p + 2) != 0) continue;
            int frameData = Seg6(obj, p + 4), jointIdx = Seg6(obj, p + 8), staticMax = (int)U16(obj, p + 12);
            if (frameData < 0 || jointIdx < 0 || jointIdx + entries * 6 > obj.Length || frameData + 2 > obj.Length) continue;
            int frameWords = (obj.Length - frameData) / 2;
            bool ok = true;
            int dynamic = 0;
            for (int k = 0; k < entries && ok; k++)
                for (int ax = 0; ax < 3; ax++)
                {
                    int idx = (int)U16(obj, jointIdx + (k * 3 + ax) * 2);
                    int maxNeeded = idx >= staticMax ? idx + frameCount : idx;
                    if (maxNeeded >= frameWords) { ok = false; break; }
                    if (idx >= staticMax) dynamic++;
                }
            if (ok && dynamic > bestScore) { bestScore = dynamic; bestOff = p; }
        }
        if (bestOff < 0) return null;

        int fd = Seg6(obj, bestOff + 4), ji = Seg6(obj, bestOff + 8), sm = (int)U16(obj, bestOff + 12);
        var joints = new short[entries * 3];
        for (int k = 0; k < entries; k++)
            for (int ax = 0; ax < 3; ax++)
            {
                int idx = (int)U16(obj, ji + (k * 3 + ax) * 2);
                int at = fd + (idx >= sm ? idx /* + frame 0 */ : idx) * 2;
                joints[k * 3 + ax] = at + 2 <= obj.Length ? (short)U16(obj, at) : (short)0;
            }
        return joints;
    }

    private void RunDl(int off, int depth)
    {
        if (depth > 16 || off < 0) return;
        var d = _obj;
        for (int p = off; p + 8 <= d.Length; p += 8)
        {
            byte op = d[p];
            switch (op)
            {
                case G_ENDDL: return;
                case G_VTX:
                {
                    uint w0 = U32(d, p);
                    int numv = (int)((w0 >> 12) & 0xFF);
                    int dst = (int)((w0 >> 1) & 0x7F) - numv;
                    uint vaddr = U32(d, p + 4);
                    if ((vaddr >> 24) == 0x08 && _skin8 != null)
                        LoadVertsSkin((int)(vaddr & 0xFFFFFF), numv, dst);   // skinned mesh: seg 8 = computed buffer
                    else
                        LoadVerts(VtxSeg(vaddr), numv, dst);
                    break;
                }
                case G_TRI1: Emit(d[p + 1] / 2, d[p + 2] / 2, d[p + 3] / 2); break;
                case G_TRI2:
                case 0x07:   // G_QUAD (gsSP1Quadrangle) — two triangles, same byte layout as G_TRI2
                    Emit(d[p + 1] / 2, d[p + 2] / 2, d[p + 3] / 2);
                    Emit(d[p + 5] / 2, d[p + 6] / 2, d[p + 7] / 2);
                    break;
                case G_DL:
                {
                    int sub = OvlOrSeg6(p + 4);
                    bool branch = d[p + 1] != 0;
                    if (sub >= 0) RunDl(sub, depth + 1);
                    if (branch) return;
                    break;
                }
                case 0xE1:   // G_RDPHALF_1 — branch target for the following G_BRANCH_Z (LOD)
                    _branchOff6 = OvlOrSeg6(p + 4);
                    break;
                case 0x04:   // G_BRANCH_Z (gsSPBranchLessZraw) — take the high-detail branch, then end this DL
                {
                    int bo = _branchOff6; _branchOff6 = -1;   // consume the target
                    if (bo >= 0) RunDl(bo, depth + 1);
                    return;
                }
                case G_GEOMETRYMODE:
                    // F3DEX2: w0 low-24 = ~clearbits, w1 = setbits. Track the lighting bit so we
                    // know whether a vertex's bytes 12-14 are a colour or a (signed) normal.
                    _geoMode = (_geoMode & (0xFF000000u | (U32(d, p) & 0x00FFFFFFu))) | U32(d, p + 4);
                    break;
                case G_MTX:
                {
                    // Load a flex limb-skinning matrix from segment 0x0D: offset/0x40 = slot = the Nth
                    // drawn limb (see _slotToLimb). Switch to that limb's transform for the next verts.
                    uint addr = U32(d, p + 4);
                    if ((addr >> 24) == 0x0D && _slotToLimb != null && _limbXform != null)
                    {
                        int slot = (int)((addr & 0xFFFFFF) / 0x40);
                        if (slot >= 0 && slot < _slotCount) SetXform(_limbXform[_slotToLimb[slot]]);
                    }
                    // Synthetic segment-0x0C matrix stack: some actors (Like-Like En_Rr, the Jabu tentacle
                    // En_Bx) draw one DL through a runtime array of body-segment matrices bound to segment
                    // 0x0C — absent at static read time, so the segments collapse. When the caller supplies
                    // the rest-pose stack, load slot = offset/0x40 so the body stands up in the editor.
                    else if ((addr >> 24) == 0x0C && _seg0C != null)
                    {
                        int slot = (int)((addr & 0xFFFFFF) / 0x40);
                        if (slot >= 0 && slot < _seg0C.Length) SetXform(_seg0C[slot]);
                    }
                    break;
                }
                case G_SETPRIMCOLOR: _primColor = RgbOf(U32(d, p + 4)); break;
                case G_SETENVCOLOR:  _envColor = RgbOf(U32(d, p + 4)); break;
                case G_SETCOMBINE:
                {
                    // Does the combine mode use PRIM (input 3) or ENV (input 5) as a colour source?
                    // Check the A (4-bit) and C (5-bit) colour inputs of both cycles plus the D adds.
                    uint w0 = U32(d, p), w1 = U32(d, p + 4);
                    int a0 = (int)((w0 >> 20) & 0xF), c0 = (int)((w0 >> 15) & 0x1F);
                    int a1 = (int)((w0 >> 5) & 0xF),  c1 = (int)(w0 & 0x1F);
                    int dd0 = (int)((w1 >> 15) & 0x7), dd1 = (int)((w1 >> 6) & 0x7);
                    _combineUsesPrim = a0 == 3 || c0 == 3 || a1 == 3 || c1 == 3 || dd0 == 3 || dd1 == 3;
                    _combineUsesEnv  = a0 == 5 || c0 == 5 || a1 == 5 || c1 == 5 || dd0 == 5 || dd1 == 5;
                    break;
                }
                case G_SETTIMG: (_timgFile, _timgOff) = ResolveTexSeg(U32(d, p + 4)); break;
                case G_LOADBLOCK:
                    // The pending SETTIMG image is loaded into TMEM as the render texture. w1's lrs field is
                    // (texel count - 1) — capture it so SETTILESIZE can derive the physical height (texels/width)
                    // and not decode a wrap region's oversized render span.
                    _texFile = _timgFile; _texOff = _timgOff;
                    _loadTexels = (int)((U32(d, p + 4) >> 12) & 0xFFF) + 1;
                    break;
                case G_LOADTILE:
                    _texFile = _timgFile; _texOff = _timgOff;
                    _loadTexels = 0;   // LOADTILE gives an explicit rect via SETTILESIZE; don't height-clamp
                    break;
                case G_LOADTLUT:
                    // The pending SETTIMG image is loaded as the palette (TLUT). CI textures need this
                    // to decode in colour instead of grayscale.
                    _palFile = _timgFile; _palOff = _timgOff;
                    break;
                case G_SETTILE:
                {
                    // Track per tile descriptor — F3DEX2 may render from a non-zero tile, so capturing
                    // only tile 0 left those textures with a stale format (→ untextured/garbled actors).
                    uint w0 = U32(d, p), w1 = U32(d, p + 4);
                    int tile = (int)((w1 >> 24) & 7);
                    _tileFmt[tile] = (int)((w0 >> 21) & 7);
                    _tileSiz[tile] = (int)((w0 >> 19) & 3);
                    // Wrap flags (cmS bits 8-9, cmT bits 18-19; bit0 = G_TX_MIRROR, bit1 = G_TX_CLAMP).
                    _tileClampS[tile] = ((w1 >> 9) & 1) != 0; _tileClampT[tile] = ((w1 >> 19) & 1) != 0;
                    _tileMirrorS[tile] = ((w1 >> 8) & 1) != 0; _tileMirrorT[tile] = ((w1 >> 18) & 1) != 0;
                    _tileShiftS[tile] = (int)((w1 >> 0) & 0xF);    // shiftS bits [0..3]
                    _tileShiftT[tile] = (int)((w1 >> 10) & 0xF);   // shiftT bits [10..13]
                    _tileMaskS[tile] = (int)((w1 >> 4) & 0xF);     // masks bits [4..7]  (wrap size = 1<<mask)
                    _tileMaskT[tile] = (int)((w1 >> 14) & 0xF);    // maskt bits [14..17]
                    _tileLine[tile] = (int)((w0 >> 9) & 0x1FF);    // line bits [9..17] = 64-bit words per row
                    break;
                }
                case G_SETTILESIZE:
                {
                    // The tile that gets a size set is the render tile — adopt its format + wrap flags.
                    uint w0 = U32(d, p), w1 = U32(d, p + 4);
                    int tile = (int)((w1 >> 24) & 7);
                    _fmt = _tileFmt[tile]; _siz = _tileSiz[tile];
                    _clampS = _tileClampS[tile]; _clampT = _tileClampT[tile];
                    _mirrorS = _tileMirrorS[tile]; _mirrorT = _tileMirrorT[tile];
                    _shiftS = _tileShiftS[tile]; _shiftT = _tileShiftT[tile];
                    int uls = (int)((w0 >> 12) & 0xFFF), ult = (int)(w0 & 0xFFF);
                    int lrs = (int)((w1 >> 12) & 0xFFF), lrt = (int)(w1 & 0xFFF);
                    _tw = ((lrs - uls) >> 2) + 1; _th = ((lrt - ult) >> 2) + 1;
                    // A SETTILESIZE span is the render rectangle, which for a repeating/mirroring tile can be
                    // LARGER than the physically-loaded texture (the RDP wraps it). The real size is 1 << mask.
                    // Decode the physical size, not the span — otherwise a wrap region reads far past the
                    // texture into neighbouring data (Kaepora Gaebora's rainbow-confetti belly). Only when the
                    // tile actually wraps in that axis (clamp tiles render their true size, mask 0 or not).
                    int mw = _tileMaskS[tile], mh = _tileMaskT[tile];
                    if (mw is >= 1 and <= 8 && (1 << mw) < _tw && (!_clampS || _tw > 256)) _tw = 1 << mw;
                    if (mh is >= 1 and <= 8 && (1 << mh) < _th && (!_clampT || _th > 256)) _th = 1 << mh;
                    // The physical loaded texture is (line words/row) wide and (loadedTexels/width) tall. When
                    // the render span exceeds that, it's a wrap/mirror region — decode only the real texels so
                    // we don't read past the texture into neighbouring data (Kaepora Gaebora's belly garbage).
                    int physW = _tileLine[tile] > 0 ? (_tileLine[tile] << 4) >> _siz : 0;   // words*8 / bytesPerTexel
                    if (physW > 0 && physW < _tw) _tw = physW;
                    // Height = loadedTexels / width, but LOADBLOCK counts texels in 16-bpp load words, so this
                    // only matches the render height for 16/32-bpp textures. For 4/8-bpp (CI etc.) the block is
                    // loaded at a different width than rendered, so skip the height clamp (keep the span) to
                    // avoid halving them (Kaepora's CI8 eye). Width (from tile line) is reliable for all sizes.
                    if (_loadTexels > 0 && _tw > 0 && _siz >= 2)
                    {
                        int physH = _loadTexels / _tw;
                        if (physH > 0 && physH < _th) _th = physH;
                    }
                    _uls = uls; _ult = ult;
                    break;
                }
            }
        }
    }

    // Skinned-mesh vertex load: the source is the precomputed segment-8 buffer (_skin8), already in
    // model space, so no limb basis/translation is applied. Bytes 12-14 are stored as a white colour
    // (the buffer is built unlit) so TEXEL0 x SHADE shows the texture's true colour.
    private void LoadVertsSkin(int off, int numv, int dst)
    {
        var s = _skin8!;
        for (int i = 0; i < numv && dst + i < 32; i++)
        {
            int e = off + i * 16;
            if (e + 16 > s.Length) break;
            _cache[dst + i] = new Vtx
            {
                P = new Vector3((short)U16(s, e), (short)U16(s, e + 2), (short)U16(s, e + 4)),
                T = new Vector2((short)U16(s, e + 8) / 32f, (short)U16(s, e + 10) / 32f),
                C = new Vector3(s[e + 12] / 255f, s[e + 13] / 255f, s[e + 14] / 255f),
            };
        }
    }

    private void LoadVerts(int off, int numv, int dst)
    {
        if (off < 0) return;
        for (int i = 0; i < numv && dst + i < 32; i++)
        {
            int e = off + i * 16;
            if (e + 16 > _obj.Length) break;
            var lp = new Vector3((short)U16(_obj, e), (short)U16(_obj, e + 2), (short)U16(_obj, e + 4));

            // Bytes 12-14: a signed normal when lit (shade it), or a baked RGB colour when unlit.
            // The normal is limb-local, so rotate it by the current limb basis before lighting.
            Vector3 c;
            if ((_geoMode & G_LIGHTING) != 0)
            {
                var n = new Vector3((sbyte)_obj[e + 12], (sbyte)_obj[e + 13], (sbyte)_obj[e + 14]);
                var nw = n.X * _bx + n.Y * _by + n.Z * _bz;
                if (nw.LengthSquared > 1e-3f) nw.Normalize();
                // Brighter ambient floor so lit characters show their texture COLOUR clearly instead of
                // reading as dim/grayscale (the darkest side was 0.35 → near-black × texel). Keep max at 1.0.
                float shade = Math.Clamp(Vector3.Dot(nw, LightDir) * 0.4f + 0.68f, 0.55f, 1f);
                c = new Vector3(shade, shade, shade);
            }
            else
            {
                c = new Vector3(_obj[e + 12] / 255f, _obj[e + 13] / 255f, _obj[e + 14] / 255f);
            }

            _cache[dst + i] = new Vtx
            {
                P = lp.X * _bx + lp.Y * _by + lp.Z * _bz + _t,
                T = new Vector2((short)U16(_obj, e + 8) / 32f, (short)U16(_obj, e + 10) / 32f),
                C = c,
            };
        }
    }

    private void Emit(int a, int b, int c)
    {
        if (a >= 32 || b >= 32 || c >= 32) return;
        var va = _cache[a]; var vb = _cache[b]; var vc = _cache[c];
        var tex = CurrentTexture();
        if (tex != null) DiagTexTris++; else DiagNoTexTris++;
        float tw = tex?.Width ?? 1, th = tex?.Height ?? 1;
        if (tw < 1) tw = 1; if (th < 1) th = 1;
        // Apply the combiner tint: prim (preferred) or env colour, only when the combine mode
        // actually uses it. White defaults leave the surface unchanged.
        Vector3 tint = _combineUsesPrim ? _primColor : _combineUsesEnv ? _envColor : Vector3.One;
        // RDP tile UV shift + upper-left offset (SoH fast-interpreter parity), matching RoomMeshReader.
        float sfx = _uls * 0.25f, sfy = _ult * 0.25f;
        Vector2 Uv(Vector2 t) => new((ShiftCoord(t.X, _shiftS) - sfx) / tw, (ShiftCoord(t.Y, _shiftT) - sfy) / th);
        _tris.Add(new MeshTri
        {
            P0 = va.P, P1 = vb.P, P2 = vc.P,
            C0 = va.C * tint, C1 = vb.C * tint, C2 = vc.C * tint,
            T0 = Uv(va.T),
            T1 = Uv(vb.T),
            T2 = Uv(vc.T),
            Texture = tex,
        });
    }

    // RDP tile UV shift (SoH fast-interpreter parity): 1..10 divide (texture larger), 11..15 scale up.
    private static float ShiftCoord(float coord, int shift)
        => shift == 0 ? coord : shift <= 10 ? coord / (1 << shift) : coord * (1 << (16 - shift));

    // Resolve a texture/palette segmented pointer to (file, offset). Actors bind their own object to
    // segment 6 and the shared gameplay_keep to segment 4; textures in any other segment are dynamic
    // (set by C draw code) and can't be resolved here.
    // Resolves a G_VTX segmented pointer to an in-object offset. Vertices are read from the bytes we
    // were given (_obj), so this only yields an offset when the segment maps to THIS file — which
    // covers seg 6 (the object itself) and, crucially, seg 4/5 when the object we're reading IS that
    // keep (e.g. the wooden door, whose panels live in gameplay_keep and reference their verts via
    // seg 4). Without this the door loaded zero vertices and collapsed to a point.
    private int VtxSeg(uint segAddr)
    {
        if (_ovlBase != 0 && segAddr >= _ovlBase && segAddr < _ovlBase + _ovlLen)
            return (int)(segAddr - _ovlBase);   // overlay-embedded verts: VRAM-absolute → file offset
        int seg = (int)(segAddr >> 24), off = (int)(segAddr & 0xFFFFFF);
        int file = seg switch { 6 => _objFileIndex, 4 => _keepFileIndex, 5 => _keep5FileIndex, _ => -1 };
        return file == _objFileIndex && off >= 0 ? off : -1;
    }

    // Resolve a G_DL/branch pointer, overlay-aware: VRAM-absolute in the overlay range → file offset,
    // else the usual segment-6 (object self) pointer. Other segments aren't reachable at static read time.
    private int OvlOrSeg6(int o)
    {
        if (o + 4 > _obj.Length) return -1;
        uint v = U32(_obj, o);
        if (_ovlBase != 0 && v >= _ovlBase && v < _ovlBase + _ovlLen) return (int)(v - _ovlBase);
        return (v >> 24) == 6 ? (int)(v & 0x00FFFFFF) : -1;
    }

    // Texture-binding diagnostics (which SETTIMG segments an object uses, and how many tris end up with
    // no texture) — for auditing actors that render untextured. Reset per-actor by the render harness.
    public static readonly Dictionary<int, int> DiagTimgSegs = new();
    public static int DiagTexTris, DiagNoTexTris;
    public static int DiagNullNoTex, DiagNullFmt, DiagNullDims;   // why CurrentTexture returned null
    public static void ResetTexDiag() { DiagTimgSegs.Clear(); DiagTexTris = DiagNoTexTris = 0; DiagNullNoTex = DiagNullFmt = DiagNullDims = 0; }

    private (int file, int off) ResolveTexSeg(uint segAddr)
    {
        if (_ovlBase != 0 && segAddr >= _ovlBase && segAddr < _ovlBase + _ovlLen)
            return (_objFileIndex, (int)(segAddr - _ovlBase));   // overlay-embedded texture (sPlatformTex…)
        int seg = (int)(segAddr >> 24), off = (int)(segAddr & 0xFFFFFF);
        DiagTimgSegs[seg] = DiagTimgSegs.GetValueOrDefault(seg) + 1;
        // A per-actor gSPSegment binding (segments 8-D) wins — the base offset into the object the actor's
        // draw code bound to this segment, plus the DL's SETTIMG offset.
        if (seg is >= 8 and <= 0xD && _segTex is { } st && seg < st.Length && st[seg] >= 0)
            return (_segTexFile is { } sf && seg < sf.Length && sf[seg] >= 0 ? sf[seg] : _objFileIndex, st[seg] + off);
        return seg switch
        {
            6 => (_objFileIndex, off),
            4 => (_keepFileIndex, off),
            5 => (_keep5FileIndex, off),   // the scene's keep object (field/dungeon props: grass, rocks…)
            // Segments 8/9 are bound by C draw code to the actor's current eye/mouth texture; for the
            // player we resolve them to the default open-eye / neutral-mouth textures in the object.
            // The boss door binds seg 8 to its per-temple emblem texture (gBossDoor*Tex) via _seg8Off.
            8 => _seg8Off >= 0 ? (_objFileIndex, _seg8Off + off)
                 : _eyeOff >= 0 ? (_objFileIndex, _eyeOff + off) : (-1, -1),
            9 => _mouthOff >= 0 ? (_objFileIndex, _mouthOff + off) : (-1, -1),
            _ => (-1, -1),
        };
    }

    private RomTexInfo? CurrentTexture()
    {
        if (_texOff < 0 || _texFile < 0) { DiagNullNoTex++; return null; }
        var type = MapType(_fmt, _siz);
        if (type == null) { DiagNullFmt++; return null; }
        if (_tw < 1 || _th < 1 || _tw > 256 || _th > 256) { DiagNullDims++; return null; }
        // CI (colour-indexed) textures need their TLUT (in the same file/segment as the texture).
        // Without it they fall back to grayscale — the reason Link rendered grey. The decoder only
        // reads the palette when PaletteFileIndex >= 0.
        return new RomTexInfo(_texFile, _texOff, type.Value, _tw, _th,
                              _palOff, _palOff >= 0 ? _palFile : -1, _clampS, _clampT, _mirrorS, _mirrorT);
    }

    // RGB (0..1) of a packed RGBA32 colour word (the prim/env colour payload).
    private static Vector3 RgbOf(uint rgba) => new(((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f);

    // Segment-6 (object self) pointer → offset, or -1 for other segments (not resolved here).
    private static int Seg6(byte[] d, int o)
    {
        if (o + 4 > d.Length) return -1;
        uint v = U32(d, o);
        return (v >> 24) == 6 ? (int)(v & 0x00FFFFFF) : -1;
    }

    private static N64TexType? MapType(int fmt, int siz) => (fmt, siz) switch
    {
        (0, 2) => N64TexType.RGBA16bpp, (0, 3) => N64TexType.RGBA32bpp,
        (2, 0) => N64TexType.Palette4bpp, (2, 1) => N64TexType.Palette8bpp,
        (3, 0) => N64TexType.GrayscaleAlpha4bpp, (3, 1) => N64TexType.GrayscaleAlpha8bpp,
        (3, 2) => N64TexType.GrayscaleAlpha16bpp,
        (4, 0) => N64TexType.Grayscale4bpp, (4, 1) => N64TexType.Grayscale8bpp,
        _ => null,
    };

    private static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    private static ushort U16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);
}
