using OpenTK.Mathematics;

namespace MegatonHammer.Rom;

/// <summary>
/// Per-scene animated-texture table: which CPU segment (0x08–0x0D) a scene scrolls, and how fast (UV per
/// second). The room DL binds a tile-scroll display list into that segment (gsSPDisplayList 0x0N000000)
/// before drawing the surfaces that use it, so RoomMeshReader tags those triangles with the segment and the
/// renderer offsets their UVs over time. OoT is code-driven (a hardcoded Scene_DrawConfig function per
/// scene — values transcribed from z_scene_table.c); MM is data-driven (the scene's AnimatedMaterial list,
/// scene command 0x1A) and is parsed from the ROM. See the animated-texture audit.
/// </summary>
public static class SceneTexAnim
{
    // A scroll step in the draw config's s10.2 tile-coordinate units per frame → UV/sec, for a 32-texel
    // tile (full tile = 128 units) at the ~20 fps the gameplay-frame counter advances.
    private const float Unit = 20f / 128f;   // ≈ 0.15625 UV/sec per (unit/frame)

    /// <summary>segment (0x08–0x0D) → UV scroll per second for this scene (empty if nothing animates).</summary>
    public static Dictionary<int, Vector2> Build(RomImage rom, ImportedScene scene)
    {
        var m = new Dictionary<int, Vector2>();
        if (rom.Game == RomGame.MM) BuildMm(rom, scene, m);
        else BuildOot(scene.SceneId, m);
        return m;
    }

    // OoT: keyed by scene id (the Scene_DrawConfig that scene selects in gSceneTable). Scroll steps are the
    // per-frame multipliers from z_scene_table.c; two-layer counter-scrolls are approximated by the dominant
    // layer. Tile assumed 32×32 (the scenes below all use 32-texel water/lava tiles).
    private static void BuildOot(int sceneId, Dictionary<int, Vector2> m)
    {
        void S(int seg, float u, float v) => m[seg] = new Vector2(u * Unit, v * Unit);
        switch (sceneId)
        {
            case 0x00: S(9, 0, 1); break;                                                   // Deku Tree
            case 0x01: S(9, 0, 1); break;                                                   // Dodongo's Cavern (lava scroll; flipbook TODO)
            case 0x03: S(9, 0, 1); S(0xA, 0, 1); break;                                      // Forest Temple (mist)
            case 0x04: S(8, -1, -1); S(9, 6, -3); break;                                     // Fire Temple (lava)
            case 0x05: S(8, 1, 0); S(9, 1, 0); S(0xA, 1, 0); S(0xB, 3, 0); S(0xC, 1, 1); S(0xD, 4, 0); break; // Water Temple
            case 0x44: S(8, 0, 6); S(9, 0, 3); S(0xA, 0, 1); break;                          // Chamber of the Sages (warp water/light)
            case 0x51: S(8, 0, 3); S(9, 0, 10); break;                                       // Hyrule Field / title
            case 0x57: S(8, 1, 1); S(9, -1, -1); break;                                      // Lake Hylia (water)
            case 0x61: S(8, 0, 1); break;                                                    // Death Mountain Crater (lava)
        }
    }

    // MM: parse the scene's AnimatedMaterial list (scene command 0x1A → array of {s8 segment, s16 type,
    // void* params}; iterate while segment >= 0, last entry negated; bound segment = abs(segment)+7). Only
    // the two scroll types (0 single, 1 two-layer) are mapped to UV scroll here. (Color cycle / flipbook TODO.)
    private static void BuildMm(RomImage rom, ImportedScene scene, Dictionary<int, Vector2> m)
    {
        int listOff = scene.AnimatedMaterialOffset;
        if (listOff < 0) return;
        var sf = rom.GetFile(scene.SceneFileIndex);
        if (sf == null) return;
        for (int p = listOff; p + 8 <= sf.Length; p += 8)
        {
            sbyte seg = (sbyte)sf[p];
            int type = (short)((sf[p + 2] << 8) | sf[p + 3]);
            uint paramsAddr = U32(sf, p + 4);
            int segBound = Math.Abs((int)seg) + 7;   // cast to int: Math.Abs(sbyte) throws on -128 (0x80)
            if ((paramsAddr >> 24) == 0x02 && segBound is >= 8 and <= 0x0D)   // params point into the scene file (seg 2)
            {
                int pp = (int)(paramsAddr & 0xFFFFFF);
                if (type is 0 or 1 && pp + 4 <= sf.Length)
                {
                    sbyte xStep = (sbyte)sf[pp], yStep = (sbyte)sf[pp + 1];
                    int w = sf[pp + 2] == 0 ? 32 : sf[pp + 2], h = sf[pp + 3] == 0 ? 32 : sf[pp + 3];
                    // Gfx_TexScroll(xStep*step, -(yStep*step), w, h): full tile = w*4 units; V uses -yStep.
                    m[segBound] = new Vector2(xStep / (w * 4f) * 20f, -yStep / (h * 4f) * 20f);
                }
            }
            if (seg < 0) break;   // negative segment marks the last entry
        }
    }

    private static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

    /// <summary>A flipbook (texture-cycle) animation source: the distinct frame textures live at
    /// <see cref="FrameOffsets"/> in file <see cref="SrcFile"/>; <see cref="Indices"/> is the per-keyframe
    /// index into FrameOffsets (its length is the cycle period in frames), played at <see cref="Fps"/>.</summary>
    public sealed class FlipRaw
    {
        public int SrcFile;
        public int[] FrameOffsets = System.Array.Empty<int>();
        public byte[] Indices = System.Array.Empty<byte>();
        public float Fps = 20f;
    }

    /// <summary>A colour-cycle animation (MM AnimatedMaterial types 2/3/4): the surfaces drawn under the
    /// segment are modulated by a prim colour that cycles through <see cref="Colors"/> over <see cref="Period"/>
    /// frames. Type 2 steps; 3/4 interpolate between <see cref="KeyFrames"/> times.</summary>
    public sealed class ColorCycle
    {
        public int Type;
        public (float r, float g, float b)[] Colors = System.Array.Empty<(float, float, float)>();
        public ushort[] KeyFrames = System.Array.Empty<ushort>();
        public int Period = 1;
        public float Fps = 20f;
    }

    /// <summary>segment (0x08–0x0D) → colour cycle for this scene (MM AnimatedMaterial types 2/3/4).</summary>
    public static Dictionary<int, ColorCycle> BuildColor(RomImage rom, ImportedScene scene)
    {
        var m = new Dictionary<int, ColorCycle>();
        if (rom.Game != RomGame.MM || scene.AnimatedMaterialOffset < 0) return m;
        var sf = rom.GetFile(scene.SceneFileIndex);
        if (sf == null) return m;
        for (int p = scene.AnimatedMaterialOffset; p + 8 <= sf.Length; p += 8)
        {
            sbyte seg = (sbyte)sf[p];
            int type = (short)((sf[p + 2] << 8) | sf[p + 3]);
            uint paramsAddr = U32(sf, p + 4);
            int segBound = Math.Abs((int)seg) + 7;   // cast to int: Math.Abs(sbyte) throws on -128 (0x80)
            if (type is 2 or 3 or 4 && (paramsAddr >> 24) == 0x02 && segBound is >= 8 and <= 0x0D)
            {
                int pp = (int)(paramsAddr & 0xFFFFFF);
                // AnimatedMatColorParams: u16 keyFrameLength@0, u16 keyFrameCount@2, F3DPrimColor* primColors@4,
                // F3DEnvColor* envColors@8, u16* keyFrames@C. F3DPrimColor = {r,g,b,a,lodFrac} (5 bytes).
                if (pp + 0x10 <= sf.Length)
                {
                    int keyLen = (sf[pp] << 8) | sf[pp + 1];
                    int keyCount = (sf[pp + 2] << 8) | sf[pp + 3];
                    uint primA = U32(sf, pp + 4), kfA = U32(sf, pp + 0xC);
                    int count = type == 2 ? keyLen : keyCount;
                    if (count is > 0 and <= 256 && (primA >> 24) == 0x02)
                    {
                        int primOff = (int)(primA & 0xFFFFFF);
                        if (primOff + count * 5 <= sf.Length)
                        {
                            var colors = new (float, float, float)[count];
                            for (int i = 0; i < count; i++)
                            {
                                int o = primOff + i * 5;
                                colors[i] = (sf[o] / 255f, sf[o + 1] / 255f, sf[o + 2] / 255f);
                            }
                            var kf = System.Array.Empty<ushort>();
                            if (type != 2 && (kfA >> 24) == 0x02)
                            {
                                int kfOff = (int)(kfA & 0xFFFFFF);
                                if (kfOff + count * 2 <= sf.Length)
                                {
                                    kf = new ushort[count];
                                    for (int i = 0; i < count; i++) kf[i] = (ushort)((sf[kfOff + i * 2] << 8) | sf[kfOff + i * 2 + 1]);
                                }
                            }
                            m[segBound] = new ColorCycle { Type = type, Colors = colors, KeyFrames = kf, Period = Math.Max(1, keyLen), Fps = 20f };
                        }
                    }
                }
            }
            if (seg < 0) break;
        }
        return m;
    }

    /// <summary>segment (0x08–0x0D) → flipbook frames for this scene. MM only for now (AnimatedMaterial
    /// type 5, AnimatedMatTexCycleParams); OoT code-driven flipbooks (e.g. Dodongo lava) are TODO.</summary>
    public static Dictionary<int, FlipRaw> BuildFlip(RomImage rom, ImportedScene scene)
    {
        var m = new Dictionary<int, FlipRaw>();
        if (rom.Game != RomGame.MM || scene.AnimatedMaterialOffset < 0) return m;
        var sf = rom.GetFile(scene.SceneFileIndex);
        if (sf == null) return m;
        for (int p = scene.AnimatedMaterialOffset; p + 8 <= sf.Length; p += 8)
        {
            sbyte seg = (sbyte)sf[p];
            int type = (short)((sf[p + 2] << 8) | sf[p + 3]);
            uint paramsAddr = U32(sf, p + 4);
            int segBound = Math.Abs((int)seg) + 7;   // cast to int: Math.Abs(sbyte) throws on -128 (0x80)
            if (type == 5 && (paramsAddr >> 24) == 0x02 && segBound is >= 8 and <= 0x0D)
            {
                int pp = (int)(paramsAddr & 0xFFFFFF);
                // AnimatedMatTexCycleParams: u16 keyFrameLength@0, TexturePtr* textureList@4, u8* textureIndexList@8
                if (pp + 12 <= sf.Length)
                {
                    int period = (sf[pp] << 8) | sf[pp + 1];
                    uint texListA = U32(sf, pp + 4), idxListA = U32(sf, pp + 8);
                    if (period is > 0 and <= 256 && (texListA >> 24) == 0x02 && (idxListA >> 24) == 0x02)
                    {
                        int idxOff = (int)(idxListA & 0xFFFFFF), texListOff = (int)(texListA & 0xFFFFFF);
                        if (idxOff + period <= sf.Length)
                        {
                            var indices = new byte[period];
                            int maxIdx = 0;
                            for (int i = 0; i < period; i++) { indices[i] = sf[idxOff + i]; if (indices[i] > maxIdx) maxIdx = indices[i]; }
                            var frames = new int[maxIdx + 1];
                            bool ok = true;
                            for (int i = 0; i <= maxIdx && ok; i++)
                            {
                                int fp = texListOff + i * 4;
                                uint fa = fp + 4 <= sf.Length ? U32(sf, fp) : 0;
                                if ((fa >> 24) != 0x02) ok = false; else frames[i] = (int)(fa & 0xFFFFFF);
                            }
                            if (ok) m[segBound] = new FlipRaw { SrcFile = scene.SceneFileIndex, FrameOffsets = frames, Indices = indices, Fps = 20f };
                        }
                    }
                }
            }
            if (seg < 0) break;
        }
        return m;
    }
}
