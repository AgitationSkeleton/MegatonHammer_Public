using MegatonHammer.Textures;
using OpenTK.Mathematics;

namespace MegatonHammer.Rom;

/// <summary>One triangle of decoded world geometry: positions, vertex shade colours, and
/// texture coordinates, plus the texture bound when it was drawn (null = untextured).</summary>
public struct MeshTri
{
    public Vector3 P0, P1, P2;
    public Vector3 C0, C1, C2;        // per-vertex shade (0..1)
    public Vector2 T0, T1, T2;        // texture coords (tile units)
    public RomTexInfo? Texture;       // bound texture, or null
    public bool Xlu;                  // from the translucent (XLU) display list → alpha-blend, don't occlude
    public byte AnimSeg;              // 0 = static; 8..0x0D = drawn under that animated CPU segment (scene
                                      // draw-config / AnimatedMaterial scrolls/cycles that segment) → see ImportedLevel.SegAnim
}

/// <summary>
/// Walks an OoT room's mesh display lists (F3DEX2) and produces a flat list of world-space
/// triangles for rendering — the geometry the game draws. Resolves vertices and textures
/// across segments (2 = scene file, 3 = room file). Matrices are ignored: room geometry is
/// authored in world space. Companion to <see cref="SceneImporter"/>.
/// </summary>
public sealed class RoomMeshReader
{
    private readonly RomImage _rom;
    private readonly byte[] _sceneFile;
    private readonly byte[] _roomFile;
    private readonly byte[] _keep4File;   // gameplay_keep (segment 4) — common/shared textures
    private readonly byte[] _keep5File;   // scene keep (segment 5) — field/dungeon keep textures
    private readonly byte[] _keep6File;   // MM area textures (segment 6) — scene_texture_0N shared region textures

    // F3DEX2 opcodes
    private const byte G_VTX = 0x01, G_TRI1 = 0x05, G_TRI2 = 0x06, G_QUAD = 0x07;
    private const byte G_BRANCH_Z = 0x04, G_RDPHALF_1 = 0xE1;
    private int _branchSeg = -1, _branchOff = -1;   // pending gsSPBranchLessZraw target (LOD detail DL)
    private IReadOnlySet<int>? _animSegs;   // animated CPU segments (8..0xD): scroll or colour-cycle (run via gsSPDisplayList)
    private byte _pendingAnimSeg;            // segment whose anim DL is currently bound → tag emitted tris
    private IReadOnlyDictionary<int, SceneTexAnim.FlipRaw>? _segFlip;                       // flipbook segments
    private Dictionary<int, (RomTexInfo[] frames, byte[] indices, float fps)>? _flipOut;    // built frame textures (per segment)
    private const byte G_DL = 0xDE, G_ENDDL = 0xDF, G_GEOMETRYMODE = 0xD9;
    private const byte G_MTX = 0xDA, G_POPMTX = 0xD8;
    private const byte G_SETTIMG = 0xFD, G_SETTILE = 0xF5, G_SETTILESIZE = 0xF2, G_LOADTLUT = 0xF0;
    private const byte G_SETPRIMCOLOR = 0xFA, G_SETENVCOLOR = 0xFB, G_SETCOMBINE = 0xFC;
    private const uint G_LIGHTING = 0x00020000;   // geometry-mode bit: lit (vtx bytes 12-14 are a normal)

    // Modelview matrix stack (F3DEX2 gsSPMatrix / gsSPPopMatrix). Room display lists position embedded
    // sub-objects (e.g. Jabu's rear-room props) by pushing a matrix, so vertices loaded under it are in
    // object space and must be transformed; without this they collapse to the world origin.
    private Matrix4 _mtx = Matrix4.Identity;
    private bool _mtxIsIdentity = true;
    private readonly Stack<Matrix4> _mtxStack = new();

    private uint _geoMode = G_LIGHTING;   // F3DEX2 geometry mode; room geometry is lit by default
    private static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(0.4f, 0.85f, 0.3f));

    private readonly Vtx[] _cache = new Vtx[32];
    private readonly List<MeshTri> _tris = [];
    private bool _xlu;   // true while walking a translucent (XLU) display list

    // current texture state
    private int _timgSeg = -1, _timgOff;
    private int _prevTimgSeg = -1, _prevTimgOff;   // SETTIMG before the current one (the real texture when current = a TLUT palette)
    private int _fmt, _siz, _tw = 32, _th = 32;
    private readonly int[] _tileFmt = new int[8], _tileSiz = new int[8];   // fmt/siz per tile descriptor
    private readonly int[] _tileMaskS = new int[8], _tileMaskT = new int[8]; // masks/maskt per tile (wrap size = 1<<mask)
    private readonly int[] _tileShiftS = new int[8], _tileShiftT = new int[8]; // shifts/shiftt per tile (RDP UV shift)
    // The render tile's UV shift + upper-left offset (from SETTILE/SETTILESIZE), applied to texel coords in
    // Emit exactly like SoH's fast interpreter: u/=1<<shift (shift 1..10) or u*=1<<(16-shift) (11..15), then
    // subtract uls/ult. Water Temple uses shiftS/shiftT heavily — without it its wall/floor UVs mis-scale.
    private int _shiftS, _shiftT, _uls, _ult;
    private int _palSeg = -1, _palOff;

    // Combiner tint: many N64 textures are intensity (I/IA) maps coloured by the primitive/environment
    // colour through the combiner (e.g. Stone Tower's brown brick). Track the colour and whether the
    // combine mode actually uses it, then modulate the vertex colour so the texture isn't left gray.
    private Vector3 _primColor = Vector3.One, _envColor = Vector3.One;
    private bool _combineUsesPrim, _combineUsesEnv;

    private struct Vtx { public Vector3 P; public Vector3 C; public Vector2 T; }

    private RoomMeshReader(RomImage rom, int sceneFileIndex, int roomFileIndex, int keep4Index, int keep5Index, int keep6Index)
    {
        _rom = rom;
        _sceneFileIndex = sceneFileIndex;
        _roomFileIndex  = roomFileIndex;
        _keep4Index = keep4Index;
        _keep5Index = keep5Index;
        _keep6Index = keep6Index;
        _sceneFile = Safe(sceneFileIndex);
        _roomFile  = Safe(roomFileIndex);
        _keep4File = Safe(keep4Index);
        _keep5File = Safe(keep5Index);
        _keep6File = Safe(keep6Index);
    }

    private byte[] Safe(int idx) { try { return idx >= 0 ? _rom.GetFile(idx) : []; } catch { return []; } }

    /// <summary>Decodes the triangles of one imported room, or an empty list on failure.
    /// <paramref name="keep4Index"/>/<paramref name="keep5Index"/> are the gameplay_keep and scene-keep
    /// file indices, so textures the room references from segments 4/5 resolve (instead of rendering as
    /// rainbow garbage).</summary>
    public static List<MeshTri> Read(RomImage rom, int sceneFileIndex, ImportedRoom room,
                                     int keep4Index = -1, int keep5Index = -1, int keep6Index = -1,
                                     EnvLight? light = null, IReadOnlySet<int>? animSegs = null,
                                     IReadOnlyDictionary<int, SceneTexAnim.FlipRaw>? segFlip = null,
                                     Dictionary<int, (RomTexInfo[] frames, byte[] indices, float fps)>? flipOut = null)
    {
        if (room.MeshHeaderOffset < 0) return [];
        var r = new RoomMeshReader(rom, sceneFileIndex, room.FileIndex, keep4Index, keep5Index, keep6Index);
        if (animSegs is { Count: > 0 }) r._animSegs = animSegs;
        if (segFlip is { Count: > 0 }) { r._segFlip = segFlip; r._flipOut = flipOut; }
        if (light != null)
        {
            // Tint lit geometry by the scene's environment lighting so intensity (grayscale) textures
            // pick up the scene's colour — Stone Tower's brown/orange brick is an I4 texture coloured by
            // its ambient + directional light, which a flat white shade left looking desaturated/gray.
            r._ambient = new Vector3(light.AmbR, light.AmbG, light.AmbB) / 255f;
            r._diffuse = new Vector3(light.L1r, light.L1g, light.L1b) / 255f;
            r._hasSceneLight = true;
        }
        try { r.ReadMeshHeader(room.MeshHeaderOffset); } catch { /* return whatever decoded */ }
        return r._tris;
    }

    private Vector3 _ambient = Vector3.One, _diffuse = Vector3.One;
    private bool _hasSceneLight;

    public static readonly List<string> DiagMeshTypes = new();   // per-room shape diagnostic

    // Mesh header: type 0 = list of {opa,xlu} DL pointers; type 2 = {pos, opa, xlu}; type 1 = bg image.
    private void ReadMeshHeader(int off)
    {
        var d = _roomFile;
        if (off + 12 > d.Length) return;
        byte type = d[off];
        byte count = d[off + 1];
        if (DiagTrace)
        {
            string extra = type == 1
                ? $" amount={d[off + 1]} src=0x{U32(d, off + 8):X8} {U16(d, off + 0x14)}x{U16(d, off + 0x16)} fmt={d[off + 0x18]} siz={d[off + 0x19]}"
                : $" count={count}";
            DiagMeshTypes.Add($"type={type}{extra}");
        }

        if (type == 0 || type == 2)
        {
            int listSeg = Seg(d, off + 4, out int listOff);
            int stride = type == 0 ? 8 : 16;
            int dlPair = type == 0 ? 0 : 8;     // type 2 entries start with 8 bytes of position
            var src = SegData(listSeg);
            for (int i = 0; i < count; i++)
            {
                int e = listOff + i * stride;
                if (e + dlPair + 8 > src.Length) break;
                int opaSeg = Seg(src, e + dlPair, out int opaOff);
                int xluSeg = Seg(src, e + dlPair + 4, out int xluOff);
                int before = _tris.Count;
                _pendingAnimSeg = 0;   // a scroll binding doesn't carry across culling groups
                if (opaSeg > 0) { _xlu = false; RunDl(opaSeg, opaOff, 0); }
                if (xluSeg > 0) { _xlu = true;  RunDl(xluSeg, xluOff, 0); _xlu = false; }
                if (DiagTrace) DiagMeshTypes.Add($"  entry{i}: opa=seg{opaSeg}:{opaOff:X} xlu=seg{xluSeg}:{xluOff:X} tris={_tris.Count - before}");
            }
        }
        else if (type == 1)
        {
            // Prerendered-background room (OoT Market, Castle Town, ToT exterior, houses…). The visible
            // backdrop is a JFIF image, but the room still has real DL geometry (floor, stairs, props)
            // pointed to by a RoomShapeDListsEntry at offset 0x04. Run it so the room isn't blank.
            // Single (amountType 1) and multi (2) share the same {opa,xlu} entry pointer here.
            int entrySeg = Seg(d, off + 4, out int entryOff);
            var src = SegData(entrySeg);
            if (entryOff + 8 <= src.Length)
            {
                int opaSeg = Seg(src, entryOff, out int opaOff);
                int xluSeg = Seg(src, entryOff + 4, out int xluOff);
                if (opaSeg > 0) { _xlu = false; RunDl(opaSeg, opaOff, 0); }
                if (xluSeg > 0) { _xlu = true;  RunDl(xluSeg, xluOff, 0); _xlu = false; }
            }
        }
    }

    public static bool DiagTrace;            // one-shot opcode trace of the first DL that emits untextured tris
    public static readonly List<string> DiagTraceLog = new();

    private void RunDl(int seg, int off, int depth)
    {
        if (depth > 12) return;
        var d = SegData(seg);
        for (int p = off; p + 8 <= d.Length; p += 8)
        {
            byte op = d[p];
            if (DiagTrace && DiagTraceLog.Count < 200000 &&
                (op == G_SETTIMG || op == G_LOADTLUT || op == G_DL || op == G_TRI1 || op == G_TRI2 || op == 0xDF || op == G_SETTILESIZE))
                DiagTraceLog.Add($"{new string(' ', depth * 2)}seg{seg}:{p:X5} op={op:X2} timgSeg={_timgSeg} fmt={_fmt}/{_siz} {_tw}x{_th} pendAnim={_pendingAnimSeg}");
            switch (op)
            {
                case G_ENDDL: return;

                case G_VTX:
                {
                    uint w0 = U32(d, p);
                    int numv = (int)((w0 >> 12) & 0xFF);
                    int end  = (int)((w0 >> 1) & 0x7F);
                    int dst  = end - numv;
                    int vseg = Seg(d, p + 4, out int voff);
                    LoadVerts(vseg, voff, numv, dst);
                    break;
                }

                case G_TRI1:
                    Emit(d[p + 1] / 2, d[p + 2] / 2, d[p + 3] / 2);
                    break;

                case G_TRI2:
                case G_QUAD:   // gsSP1Quadrangle — two triangles, same byte layout as G_TRI2
                    Emit(d[p + 1] / 2, d[p + 2] / 2, d[p + 3] / 2);
                    Emit(d[p + 5] / 2, d[p + 6] / 2, d[p + 7] / 2);
                    break;

                case G_DL:
                {
                    int dseg = Seg(d, p + 4, out int doff);
                    bool branch = d[p + 1] != 0;     // 0 = call (return here), 1 = branch (replace)
                    // The room binds a tile-scroll DL into an animated segment (8..0xD) and runs it via
                    // gsSPDisplayList(0x0N000000) just before the surfaces that use it; the segment itself
                    // isn't loaded here (SegData empty), so tag subsequent tris with it for the renderer.
                    if (_animSegs != null && _animSegs.Contains(dseg)) _pendingAnimSeg = (byte)dseg;
                    if (dseg > 0) RunDl(dseg, doff, depth + 1);
                    if (branch) return;
                    break;
                }

                case G_RDPHALF_1:
                    // Sets the branch target for the following G_BRANCH_Z (gsSPBranchLessZraw): w1 = the
                    // detail display list's segmented address.
                    _branchSeg = Seg(d, p + 4, out _branchOff);
                    break;

                case G_BRANCH_Z:
                {
                    // gsSPBranchLessZraw: an LOD branch — render the high-detail DL the prior G_RDPHALF_1
                    // points to (the editor always shows full detail), then end this DL (a branch replaces
                    // it). MM town/field geometry hides most of its polys behind these, so without this the
                    // room imports almost empty (e.g. West Clock Town: 167 → full).
                    int bs = _branchSeg, bo = _branchOff;
                    _branchSeg = -1;   // consume — a later G_BRANCH_Z must set its own target first
                    if (bs > 0) RunDl(bs, bo, depth + 1);
                    return;
                }

                case G_MTX:
                {
                    // F3DEX2: param byte = w0 & 0xFF, XOR'd with G_MTX_PUSH on encode.
                    int param = d[p + 3] ^ 0x01;       // bit0 PUSH, bit1 LOAD, bit2 PROJECTION
                    if ((param & 0x04) != 0) break;     // ignore projection matrices (don't move geometry)
                    int mseg = Seg(d, p + 4, out int moff);
                    var md = SegData(mseg);
                    var nm = DecodeMtx(md, moff);
                    if (nm == null) break;
                    if (DiagTrace && DiagVtxLog.Count < 4000)
                    {
                        var m = nm.Value;
                        float maxLin = MathF.Max(MathF.Max(MathF.Abs(m.M11), MathF.Abs(m.M22)), MathF.Abs(m.M33));
                        DiagVtxLog.Add($"MTX param={param:X} push={(param & 1)} load={(param >> 1) & 1} trans=({m.M41:F0},{m.M42:F0},{m.M43:F0}) diag=({m.M11:F2},{m.M22:F2},{m.M33:F2}) maxLin={maxLin:F2}");
                    }
                    if ((param & 0x01) != 0) _mtxStack.Push(_mtx);                 // PUSH
                    _mtx = (param & 0x02) != 0 ? nm.Value : nm.Value * _mtx;       // LOAD replaces, MUL composes
                    _mtxIsIdentity = false;
                    break;
                }

                case G_POPMTX:
                    _mtx = _mtxStack.Count > 0 ? _mtxStack.Pop() : Matrix4.Identity;
                    _mtxIsIdentity = _mtx == Matrix4.Identity;
                    break;

                case G_GEOMETRYMODE:
                    // F3DEX2: w0 low-24 = ~clearbits, w1 = setbits. Track the lighting bit so we
                    // know whether a vertex's bytes 12-14 are a colour or a (signed) normal.
                    _geoMode = (_geoMode & (0xFF000000u | (U32(d, p) & 0x00FFFFFFu))) | U32(d, p + 4);
                    break;

                case G_SETTIMG:
                    // Remember the prior texture image: OoT scene DLs bind the texture, then point
                    // SETTIMG at the palette right before G_LOADTLUT. Keeping the prior binding lets
                    // us restore the real texture once the TLUT load consumes the palette pointer.
                    _prevTimgSeg = _timgSeg; _prevTimgOff = _timgOff;
                    _timgSeg = Seg(d, p + 4, out _timgOff);
                    break;

                case G_LOADTLUT:
                    // The current SETTIMG was the palette; record it, then RESTORE the texture image
                    // bound before it (don't drop the binding — that left CI textures untextured/gray).
                    _palSeg = _timgSeg; _palOff = _timgOff;
                    _timgSeg = _prevTimgSeg; _timgOff = _prevTimgOff;
                    break;

                case G_SETPRIMCOLOR: _primColor = RgbOf(U32(d, p + 4)); break;
                case G_SETENVCOLOR:  _envColor = RgbOf(U32(d, p + 4)); break;
                case G_SETCOMBINE:
                {
                    // Does the combine use PRIM (input 3) or ENV (input 5) as a colour source? Check the
                    // A/C colour inputs of both cycles plus the D adds (mirrors ObjectModelReader).
                    uint cw0 = U32(d, p), cw1 = U32(d, p + 4);
                    int a0 = (int)((cw0 >> 20) & 0xF), c0 = (int)((cw0 >> 15) & 0x1F);
                    int a1 = (int)((cw0 >> 5) & 0xF),  c1 = (int)(cw0 & 0x1F);
                    int dd0 = (int)((cw1 >> 15) & 0x7), dd1 = (int)((cw1 >> 6) & 0x7);
                    _combineUsesPrim = a0 == 3 || c0 == 3 || a1 == 3 || c1 == 3 || dd0 == 3 || dd1 == 3;
                    _combineUsesEnv  = a0 == 5 || c0 == 5 || a1 == 5 || c1 == 5 || dd0 == 5 || dd1 == 5;
                    break;
                }

                case G_SETTILE:
                {
                    // Track fmt/siz per tile descriptor — F3DEX2 can render from any tile, not just
                    // tile 0 (e.g. a load tile 7 + a render tile 1). Capturing only tile 0 left
                    // non-tile-0 textures with a stale format (I/IA mislabelled as RGBA16 → noise).
                    uint w0 = U32(d, p), w1s = U32(d, p + 4);
                    int tile = (int)((w1s >> 24) & 7);
                    _tileFmt[tile] = (int)((w0 >> 21) & 7);
                    _tileSiz[tile] = (int)((w0 >> 19) & 3);
                    // masks/maskt give the texture's power-of-2 size for wrapping (1 << mask). The
                    // physical texture can be far smaller than the rendered span when it tiles.
                    _tileMaskS[tile] = (int)((w1s >> 4) & 0xF);
                    _tileMaskT[tile] = (int)((w1s >> 14) & 0xF);
                    _tileShiftS[tile] = (int)((w1s >> 0) & 0xF);    // shiftS bits [0..3]
                    _tileShiftT[tile] = (int)((w1s >> 10) & 0xF);   // shiftT bits [10..13]
                    if (_tileShiftS[tile] != 0) DiagShiftS++;
                    if (_tileShiftT[tile] != 0) DiagShiftT++;
                    break;
                }

                case G_SETTILESIZE:
                {
                    // The tile that gets a size set is the render tile — adopt its format/dimensions.
                    uint w0 = U32(d, p), w1 = U32(d, p + 4);
                    int tile = (int)((w1 >> 24) & 7);
                    _fmt = _tileFmt[tile]; _siz = _tileSiz[tile];
                    int uls = (int)((w0 >> 12) & 0xFFF), ult = (int)(w0 & 0xFFF);
                    int lrs = (int)((w1 >> 12) & 0xFFF), lrt = (int)(w1 & 0xFFF);
                    // Adopt the render tile's UV shift + upper-left offset for the following tris (see Emit).
                    _shiftS = _tileShiftS[tile]; _shiftT = _tileShiftT[tile];
                    _uls = uls; _ult = ult;
                    if (uls != 0) DiagUls++; if (ult != 0) DiagUlt++;
                    _tw = ((lrs - uls) >> 2) + 1; _th = ((lrt - ult) >> 2) + 1;
                    // A SETTILESIZE span > 256 is a wrap/mirror region, not the real texture size —
                    // the physical texture is 1 << mask. Use that so the texture decodes (and the UV
                    // normalization wraps) instead of being rejected as out-of-range (→ gray).
                    int mw = _tileMaskS[tile], mh = _tileMaskT[tile];
                    if (_tw > 256 && mw is >= 1 and <= 8) _tw = 1 << mw;
                    if (_th > 256 && mh is >= 1 and <= 8) _th = 1 << mh;
                    break;
                }
            }
        }
    }

    public static readonly List<string> DiagVtxLog = new();

    private void LoadVerts(int seg, int off, int numv, int dst)
    {
        var d = SegData(seg);
        if (DiagTrace && DiagVtxLog.Count < 4000)
        {
            string first = off + 6 <= d.Length
                ? $"v0=({(short)U16(d, off)},{(short)U16(d, off + 2)},{(short)U16(d, off + 4)})" : "v0=oob";
            DiagVtxLog.Add($"VTX seg{seg} off=0x{off:X} num={numv} segLen=0x{d.Length:X} {first}");
        }
        for (int i = 0; i < numv && dst + i < 32; i++)
        {
            int e = off + i * 16;
            if (e + 16 > d.Length) break;
            // Bytes 12-14 are a vertex colour when unlit, or a signed normal when lit. Reading a
            // normal as a colour is what made lit terrain look rainbow — shade it instead.
            Vector3 c;
            if ((_geoMode & G_LIGHTING) != 0)
            {
                var nrm = new Vector3((sbyte)d[e + 12], (sbyte)d[e + 13], (sbyte)d[e + 14]);
                if (nrm.LengthSquared > 1e-3f) nrm.Normalize();
                float shade = Math.Clamp(Vector3.Dot(nrm, LightDir) * 0.5f + 0.55f, 0.35f, 1f);
                // Colour the shade by the scene light (ambient fills shadow, directional adds on top) so
                // grayscale intensity textures take on the scene's hue; plain white when no light given.
                c = _hasSceneLight
                    ? Vector3.Clamp(_ambient * 0.55f + _diffuse * shade, Vector3.Zero, Vector3.One)
                    : new Vector3(shade, shade, shade);
            }
            else
            {
                c = new Vector3(d[e + 12] / 255f, d[e + 13] / 255f, d[e + 14] / 255f);
            }
            var rawP = new Vector3((short)U16(d, e), (short)U16(d, e + 2), (short)U16(d, e + 4));
            // Apply the active modelview matrix so matrix-positioned sub-objects land in world space
            // (skipped fast-path when it's identity, the common case for plain world-space geometry).
            var pos = _mtxIsIdentity ? rawP : Vector3.TransformPosition(rawP, _mtx);
            _cache[dst + i] = new Vtx
            {
                P = pos,
                T = new Vector2((short)U16(d, e + 8) / 32f, (short)U16(d, e + 10) / 32f),
                C = c,
            };
        }
    }

    private void Emit(int a, int b, int c)
    {
        if (a >= 32 || b >= 32 || c >= 32) return;
        var va = _cache[a]; var vb = _cache[b]; var vc = _cache[c];
        var tex = CurrentTexture();
        // Vertex s/t are texel coordinates (already /32); normalize by the bound tile size so
        // UVs are 0..1 per tile (matches how fast64/the RDP scales them). Untextured: dims=1.
        float tw = tex?.Width ?? 1, th = tex?.Height ?? 1;
        if (tw < 1) tw = 1; if (th < 1) th = 1;
        // Tint an intensity texture by the combiner's prim/env colour (white default = unchanged).
        Vector3 tint = _combineUsesPrim ? _primColor : _combineUsesEnv ? _envColor : Vector3.One;
        // Apply the render tile's RDP UV shift + upper-left offset before normalizing — identical to SoH's
        // fast interpreter (u/=1<<shift for 1..10, u*=1<<(16-shift) for 11..15, then u-=uls/4). Without this
        // the Water Temple's shifted wall/floor tiles render at the wrong scale/phase (reported misalignment).
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
            Xlu = _xlu,
            AnimSeg = _pendingAnimSeg,
        });
    }

    // RDP tile UV shift (SoH fast-interpreter parity): shifts 1..10 divide the coord (texture appears larger),
    // 11..15 are negative shifts that scale it up (texture smaller / more tiled). 0 = no change.
    private static float ShiftCoord(float coord, int shift)
        => shift == 0 ? coord : shift <= 10 ? coord / (1 << shift) : coord * (1 << (16 - shift));

    // ── Diagnostics: tally WHY a triangle ends up untextured (gray-wall investigation). ──
    public static int DiagNoTimg, DiagSegUnresolved, DiagBadFormat, DiagBadDims, DiagTextured;
    public static int DiagShiftS, DiagShiftT, DiagUls, DiagUlt;   // tile shift / offset usage probe
    public static readonly Dictionary<int, int> DiagUnresolvedSegs = new();   // seg id -> count
    public static void ResetDiag() { DiagNoTimg = DiagSegUnresolved = DiagBadFormat = DiagBadDims = DiagTextured = 0; DiagShiftS = DiagShiftT = DiagUls = DiagUlt = 0; DiagUnresolvedSegs.Clear(); }

    private RomTexInfo? CurrentTexture()
    {
        if (_timgSeg < 0) { DiagNoTimg++; return null; }
        // Flipbook segment: the scene's AnimatedMaterial cycles the bound texture, so the segment isn't
        // loaded here. Bind the first frame (so the surface shows textured) and record every frame for the
        // renderer to swap over time. Format/dimensions come from the room's tile state, as in-game.
        if (_segFlip != null && _segFlip.TryGetValue(_timgSeg, out var flip) && flip.FrameOffsets.Length > 0)
        {
            var ft = MapType(_fmt, _siz);
            if (ft == null || _tw < 1 || _th < 1 || _tw > 256 || _th > 256) { DiagBadFormat++; return null; }
            int pf = _palSeg < 0 ? -1 : FileForSeg(_palSeg);
            int po = _palSeg < 0 ? -1 : _palOff;
            if (_flipOut != null && !_flipOut.ContainsKey(_timgSeg))
            {
                var frames = new RomTexInfo[flip.FrameOffsets.Length];
                for (int i = 0; i < frames.Length; i++) frames[i] = new RomTexInfo(flip.SrcFile, flip.FrameOffsets[i], ft.Value, _tw, _th, po, pf);
                _flipOut[_timgSeg] = (frames, flip.Indices, flip.Fps);
            }
            _pendingAnimSeg = (byte)_timgSeg;
            DiagTextured++;
            int f0 = flip.Indices.Length > 0 ? flip.Indices[0] : 0;
            return new RomTexInfo(flip.SrcFile, flip.FrameOffsets[Math.Min(f0, flip.FrameOffsets.Length - 1)], ft.Value, _tw, _th, po, pf);
        }
        int file = FileForSeg(_timgSeg);
        if (file < 0) { DiagSegUnresolved++; DiagUnresolvedSegs[_timgSeg] = DiagUnresolvedSegs.GetValueOrDefault(_timgSeg) + 1; return null; }
        var type = MapType(_fmt, _siz);
        if (type == null) { DiagBadFormat++; return null; }
        if (_tw < 1 || _th < 1 || _tw > 256 || _th > 256) { DiagBadDims++; return null; }
        DiagTextured++;
        // The CI palette may live in a different segment/file than the texture (cross-file TLUT).
        int palFile = _palSeg < 0 ? -1 : FileForSeg(_palSeg);
        int palOff  = _palSeg < 0 ? -1 : _palOff;
        return new RomTexInfo(file, _timgOff, type.Value, _tw, _th, palOff, palFile);
    }

    // Segment → backing file bytes. Scene/room geometry plus the two keep objects (4/5) the
    // room's textures reference; everything else is unresolved.
    private byte[] SegData(int seg) => seg switch
    {
        2 => _sceneFile, 3 => _roomFile, 4 => _keep4File, 5 => _keep5File, 6 => _keep6File, _ => []
    };
    private int FileForSeg(int seg) => seg switch
    {
        2 => _sceneFileIndex, 3 => _roomFileIndex, 4 => _keep4Index, 5 => _keep5Index, 6 => _keep6Index, _ => -1
    };

    private int _sceneFileIndex = -1, _roomFileIndex = -1, _keep4Index = -1, _keep5Index = -1, _keep6Index = -1;

    private static int Seg(byte[] d, int o, out int off)
    {
        if (o + 4 > d.Length) { off = 0; return -1; }
        uint v = U32(d, o);
        off = (int)(v & 0x00FFFFFF);
        return (int)(v >> 24);
    }

    /// <summary>Decodes an N64 fixed-point Mtx (64 bytes: 16 s16 integer parts then 16 u16 fraction
    /// parts, row-major) into an OpenTK row-vector Matrix4, or null if out of bounds.</summary>
    private static Matrix4? DecodeMtx(byte[] d, int off)
    {
        if (off < 0 || off + 64 > d.Length) return null;
        Span<float> m = stackalloc float[16];
        for (int k = 0; k < 16; k++)
        {
            short intPart = (short)U16(d, off + k * 2);
            ushort frac = U16(d, off + 32 + k * 2);
            m[k] = intPart + frac / 65536f;
        }
        return new Matrix4(m[0], m[1], m[2], m[3],
                           m[4], m[5], m[6], m[7],
                           m[8], m[9], m[10], m[11],
                           m[12], m[13], m[14], m[15]);
    }

    // F3DEX2 colour word → RGB (0..1), ignoring alpha.
    private static Vector3 RgbOf(uint w) =>
        new(((w >> 24) & 0xFF) / 255f, ((w >> 16) & 0xFF) / 255f, ((w >> 8) & 0xFF) / 255f);

    private static N64TexType? MapType(int fmt, int siz) => (fmt, siz) switch
    {
        (0, 2) => N64TexType.RGBA16bpp,  (0, 3) => N64TexType.RGBA32bpp,
        (2, 0) => N64TexType.Palette4bpp, (2, 1) => N64TexType.Palette8bpp,
        (3, 0) => N64TexType.GrayscaleAlpha4bpp, (3, 1) => N64TexType.GrayscaleAlpha8bpp,
        (3, 2) => N64TexType.GrayscaleAlpha16bpp,
        (4, 0) => N64TexType.Grayscale4bpp, (4, 1) => N64TexType.Grayscale8bpp,
        _ => null,
    };

    private static uint U32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    private static ushort U16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);
}
