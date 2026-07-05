using System.Drawing;
using System.Drawing.Imaging;
using MegatonHammer.Editor;
using MegatonHammer.Rom;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MegatonHammer.Rendering;

/// <summary>
/// Renders a read-only <see cref="ImportedLevel"/> (geometry decoded from a ROM) as a
/// textured backdrop. OoT bakes lighting into per-vertex colours, so the shader is simply
/// texture × vertex colour. Triangles are batched per texture; textures decode lazily from
/// the ROM and are cached in this GL context. Per-room visibility is honoured (D14).
/// </summary>
public sealed class ImportedMeshRenderer : IDisposable
{
    private const string Vert = @"
#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aColor;
layout(location=2) in vec2 aUV;
uniform mat4 uMVP;
uniform vec2 uUVScroll;   // animated-texture scroll offset (UV units), already time-scaled
out vec3 vColor; out vec2 vUV;
void main() { gl_Position = uMVP * vec4(aPos,1.0); vColor=aColor; vUV=aUV + uUVScroll; }";

    private const string Frag = @"
#version 330 core
in vec3 vColor; in vec2 vUV;
out vec4 fragColor;
uniform sampler2D uTex; uniform int uUseTex; uniform int uXlu;
uniform vec3 uColorMul;   // animated colour-cycle prim modulation (white = none)
uniform float uGhostAlpha; // below 1 renders the whole mesh as a dimmed blended reference ghost
uniform int uOpaqueSurf;  // 1 = opacity is combiner/shade-driven, not the texel alpha (actor skin)
void main() {
    vec3 c = vColor * uColorMul;
    float a = 1.0;
    if (uUseTex == 1) { vec4 t = texture(uTex, vUV); a = t.a; c *= t.rgb; }
    // Character skin textures store RGB with a ~0 alpha channel (the N64 combiner drives opacity from
    // shade/prim, not the texel), so the texel alpha is near-0 and the alpha-test below would cut the
    // whole actor away (rendering it as a transparent silhouette). Force opaque when the caller says so.
    if (uOpaqueSurf == 1) a = 1.0;
    if (uGhostAlpha < 0.999) {                                              // ghost overlay (trace reference)
        if (uUseTex == 1 && a < 0.3) discard;                              // keep only fully-transparent texels cut
        fragColor = vec4(c, uGhostAlpha); return;
    }
    if (uXlu == 0) { if (a < 0.3) discard; fragColor = vec4(c, 1.0); }      // opaque (alpha-tested)
    else { fragColor = vec4(c, uUseTex == 1 ? a : 0.55); }                  // translucent (blended)
}";

    private readonly Shader _shader;
    private readonly int _vao, _vbo;
    private readonly Dictionary<long, int> _glTex = [];   // RomTexInfo key → GL handle
    private RomTextureSource? _texSource;
    private bool _disposed;

    /// <summary>Seconds elapsed, driving animated-texture (scroll) UV offsets. Set by the viewport each frame.</summary>
    public float AnimTime;
    private static float Frac(float v) => v - MathF.Floor(v);

    public ImportedMeshRenderer()
    {
        _shader = new Shader(Vert, Frag);
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        int stride = 8 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.BindVertexArray(0);
    }

    public void Render3D(ImportedLevel level, Camera3D cam, int w, int h, float ghostAlpha = 1f, bool depthWrite = true)
    {
        RefreshFiltersIfNeeded();
        _texSource ??= new RomTextureSource(level.Rom);
        _camYaw = cam.Yaw; _camPitch = cam.Pitch;
        var mvp = cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h);
        _shader.Use();
        GL.Uniform1(GL.GetUniformLocation(_shader.Handle, "uGhostAlpha"), ghostAlpha);   // below 1 = dimmed trace ghost
        GL.Uniform3(GL.GetUniformLocation(_shader.Handle, "uColorMul"), 1f, 1f, 1f);   // white default (colour-cycle off)
        _shader.SetMatrix4("uMVP", mvp);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Uniform1(GL.GetUniformLocation(_shader.Handle, "uTex"), 0);

        // Batch triangles by texture AND animated segment, keeping opaque and translucent (XLU) tris
        // separate so the XLU pass can blend without occluding the level (Forest Temple's sky plane,
        // water surfaces…). The animated segment is folded into the key so each scroll layer (e.g. the
        // Chamber of Sages' three stacked water speeds on segs 8/9/A) batches and scrolls independently.
        //
        // The batched vertex buffers are STATIC (the imported geometry doesn't change frame-to-frame), so
        // they're cached per-level and rebuilt only when room visibility changes. Animation is applied at
        // draw time via uniforms (scroll/colour/flipbook), not baked into vertices — so the anim timer's
        // ~30fps repaints reuse these buffers instead of re-pushing every triangle each frame. This (with
        // the actor cache) is the main fix for the editor feeling laggy with a level loaded.
        var (opaque, xlu) = GetBackdropBatches(level);
        int scrollLoc = GL.GetUniformLocation(_shader.Handle, "uUVScroll");
        // The animated-texture UV offset for a batch: scroll-per-second × time, wrapped to 0..1 so the
        // float stays small. Static batches (anim 0) get (0,0).
        Vector2 ScrollFor(byte anim)
        {
            if (anim == 0 || !level.SegScroll.TryGetValue(anim, out var s)) return Vector2.Zero;
            return new Vector2(Frac(s.X * AnimTime), Frac(s.Y * AnimTime));
        }
        // The GL texture for a batch: a flipbook segment swaps to the current frame; else the bound texture.
        int GlTexFor(byte anim, RomTexInfo? baseTex)
        {
            if (anim != 0 && level.SegFlip.TryGetValue(anim, out var fb) && fb.Indices.Length > 0 && fb.Frames.Length > 0)
            {
                int k = (int)(AnimTime * fb.Fps) % fb.Indices.Length; if (k < 0) k += fb.Indices.Length;
                int fi = fb.Indices[k];
                if (fi >= 0 && fi < fb.Frames.Length) return GetGlTexture(fb.Frames[fi]);
            }
            return baseTex is { } ti ? GetGlTexture(ti) : 0;
        }
        int colorLoc = GL.GetUniformLocation(_shader.Handle, "uColorMul");
        // The colour-cycle prim modulation for a batch (white = none). Step (type 2) or interpolate (3/4).
        Vector3 ColorFor(byte anim)
        {
            if (anim == 0 || !level.SegColor.TryGetValue(anim, out var cc) || cc.Colors.Length == 0) return Vector3.One;
            int period = Math.Max(1, cc.Period);
            int cur = (int)(AnimTime * cc.Fps) % period; if (cur < 0) cur += period;
            if (cc.Type != 2 && cc.KeyFrames.Length == cc.Colors.Length && cc.KeyFrames.Length > 1)
            {
                int i = 0; while (i < cc.KeyFrames.Length - 1 && cc.KeyFrames[i + 1] <= cur) i++;
                int j = Math.Min(i + 1, cc.Colors.Length - 1);
                float span = MathF.Max(1f, cc.KeyFrames[j] - cc.KeyFrames[i]);
                float f = Math.Clamp((cur - cc.KeyFrames[i]) / span, 0f, 1f);
                var a = cc.Colors[i]; var b = cc.Colors[j];
                return new Vector3(a.r + (b.r - a.r) * f, a.g + (b.g - a.g) * f, a.b + (b.b - a.b) * f);
            }
            var c = cc.Colors[cur % cc.Colors.Length];
            return new Vector3(c.r, c.g, c.b);
        }

        int xluLoc = GL.GetUniformLocation(_shader.Handle, "uXlu");
        int useLoc = GL.GetUniformLocation(_shader.Handle, "uUseTex");
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);

        // Opaque pass. A ghost overlay dims + blends everything. When depthWrite is false (ghost X-ray) it
        // depth-TESTS but doesn't depth-WRITE, so brushes/actors drawn afterward are never occluded by the
        // ghost's walls — you see your work through it. depthWrite is restored before this method returns.
        bool ghost = ghostAlpha < 0.999f;
        if (ghost) { GL.Enable(EnableCap.Blend); GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); }
        else GL.Disable(EnableCap.Blend);
        GL.DepthMask(depthWrite);
        GL.Uniform1(xluLoc, 0);
        foreach (var b in opaque)
        {
            int gl = GlTexFor(b.anim, b.tex);
            GL.Uniform1(useLoc, gl != 0 ? 1 : 0);
            var sc = ScrollFor(b.anim); GL.Uniform2(scrollLoc, sc.X, sc.Y);
            var cm = ColorFor(b.anim); GL.Uniform3(colorLoc, cm.X, cm.Y, cm.Z);
            if (gl != 0) GL.BindTexture(TextureTarget.Texture2D, gl);
            DrawArr(b.verts);
        }
        GL.Uniform2(scrollLoc, 0f, 0f);
        GL.Uniform3(colorLoc, 1f, 1f, 1f);

        // Translucent pass (blended, depth-tested but no depth write → doesn't occlude).
        if (xlu.Count > 0)
        {
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);
            GL.Uniform1(xluLoc, 1);
            foreach (var b in xlu)
            {
                int gl = GlTexFor(b.anim, b.tex);
                GL.Uniform1(useLoc, gl != 0 ? 1 : 0);
                var sc = ScrollFor(b.anim); GL.Uniform2(scrollLoc, sc.X, sc.Y);
                var cm = ColorFor(b.anim); GL.Uniform3(colorLoc, cm.X, cm.Y, cm.Z);
                if (gl != 0) GL.BindTexture(TextureTarget.Texture2D, gl);
                DrawArr(b.verts);
            }
            GL.Uniform2(scrollLoc, 0f, 0f);
            GL.Uniform3(colorLoc, 1f, 1f, 1f);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
            GL.Uniform1(xluLoc, 0);
        }
        GL.BindTexture(TextureTarget.Texture2D, 0);

        if (Editor.ViewOptions.ShowPrerenderedBackground) RenderBackgrounds(level, useLoc);   // View ▸ toggle, off by default
        GL.DepthMask(true);   // restore: a depthWrite:false (X-ray) pass must not leave depth-writes off for the brushes
        if (ghost) { GL.Disable(EnableCap.Blend); GL.Uniform1(GL.GetUniformLocation(_shader.Handle, "uGhostAlpha"), 1f); }   // don't leak to later draws
    }

    private readonly Dictionary<System.Drawing.Bitmap, int> _bgTex = [];

    /// <summary>Draws each room's imported OBJ mesh geometry textured (per-material), so a Blender /
    /// SharpOcarina import is visible as the level it will become. #nomesh groups are collision-only
    /// and skipped here.</summary>
    public void RenderObjMeshes(Editor.ZScene scene, Camera3D cam, int w, int h)
    {
        bool any = scene.Rooms.Any(r => r.ObjMesh is { Tris.Count: > 0 });
        if (!any) return;
        var mvp = cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h);
        _shader.Use();
        GL.Uniform3(GL.GetUniformLocation(_shader.Handle, "uColorMul"), 1f, 1f, 1f);   // white default (colour-cycle off)
        _shader.SetMatrix4("uMVP", mvp);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Uniform1(GL.GetUniformLocation(_shader.Handle, "uTex"), 0);
        int useLoc = GL.GetUniformLocation(_shader.Handle, "uUseTex");
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);

        foreach (var room in scene.Rooms)
        {
            if (room.ObjMesh is not { } mesh) continue;
            // Batch by material so each texture binds once.
            foreach (var matGroup in mesh.Tris.Where(t => !t.NoMesh).GroupBy(t => t.Material))
            {
                var bmp = mesh.Materials.GetValueOrDefault(matGroup.Key);
                var buf = new List<float>();
                var white = Vector3.One;
                foreach (var t in matGroup)
                {
                    Push(buf, t.P0, white, t.UV0); Push(buf, t.P1, white, t.UV1); Push(buf, t.P2, white, t.UV2);
                }
                int gl = bmp != null ? ObjTexture(bmp) : 0;   // REPEAT wrap so tiling UVs (>1) tile, not clamp
                GL.Uniform1(useLoc, gl != 0 ? 1 : 0);
                if (gl != 0) GL.BindTexture(TextureTarget.Texture2D, gl);
                Draw(buf);
            }
        }
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>Draws each prerendered room's decoded JFIF background as a camera-facing billboard at
    /// the room's centre, so the editor shows the scene's real appearance (Market, ToT exterior,
    /// houses, shops) instead of just its sparse floor geometry.</summary>
    private void RenderBackgrounds(ImportedLevel level, int useLoc)
    {
        if (level.RoomBackgrounds is not { } bgs) return;
        // Camera basis for a billboard that always faces the viewer.
        float yaw = MathHelper.DegreesToRadians(_camYaw), pitch = MathHelper.DegreesToRadians(_camPitch);
        var front = Vector3.Normalize(new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Sin(yaw)));
        var right = Vector3.Normalize(Vector3.Cross(front, Vector3.UnitY));
        var up    = Vector3.Normalize(Vector3.Cross(right, front));

        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Uniform1(useLoc, 1);
        for (int ri = 0; ri < bgs.Length && ri < level.RoomMeshes.Count; ri++)
        {
            if (bgs[ri] is not { } bmp) continue;
            if (ri < level.RoomVisible.Length && !level.RoomVisible[ri]) continue;

            // Room geometry bounds → billboard centre + size.
            Vector3 mn = new(1e9f), mx = new(-1e9f);
            foreach (var t in level.RoomMeshes[ri])
            { foreach (var p in new[] { t.P0, t.P1, t.P2 }) { mn = Vector3.ComponentMin(mn, p); mx = Vector3.ComponentMax(mx, p); } }
            if (mn.X > mx.X) continue;
            var center = (mn + mx) * 0.5f;
            float extent = MathF.Max(80f, (mx - mn).Length * 0.5f);
            float halfW = extent * 0.9f, halfH = halfW * bmp.Height / MathF.Max(1, bmp.Width);
            center.Y += halfH * 0.5f;   // stand the image up from the floor

            var bl = center - right * halfW - up * halfH;
            var br2 = center + right * halfW - up * halfH;
            var tl = center - right * halfW + up * halfH;
            var tr = center + right * halfW + up * halfH;
            var white = Vector3.One;
            var buf = new List<float>(48);
            // Image top-left = UV(0,0); two triangles.
            Push(buf, tl, white, new Vector2(0, 0)); Push(buf, bl, white, new Vector2(0, 1)); Push(buf, br2, white, new Vector2(1, 1));
            Push(buf, tl, white, new Vector2(0, 0)); Push(buf, br2, white, new Vector2(1, 1)); Push(buf, tr, white, new Vector2(1, 0));

            GL.BindTexture(TextureTarget.Texture2D, BgTexture(bmp));
            Draw(buf);
        }
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    private float _camYaw, _camPitch;

    private readonly Dictionary<System.Drawing.Bitmap, int> _objTex = [];

    // Like BgTexture but REPEAT-wrapped, for imported OBJ materials whose UVs tile past 0..1.
    private int ObjTexture(System.Drawing.Bitmap bmp)
    {
        if (_objTex.TryGetValue(bmp, out int h)) return h;
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, bmp.Width, bmp.Height, 0,
                      OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
        bmp.UnlockBits(data);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        TextureFilter.ApplyToBound();
        GL.BindTexture(TextureTarget.Texture2D, 0);
        _objTex[bmp] = tex;
        return tex;
    }

    private int BgTexture(System.Drawing.Bitmap bmp)
    {
        if (_bgTex.TryGetValue(bmp, out int h)) return h;
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, bmp.Width, bmp.Height, 0,
                      OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
        bmp.UnlockBits(data);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        TextureFilter.ApplyToBound();
        GL.BindTexture(TextureTarget.Texture2D, 0);
        _bgTex[bmp] = tex;
        return tex;
    }

    /// <summary>
    /// Renders placed actors as their real models (resolved + cached by <paramref name="resolver"/>),
    /// each transformed to world space by the actor's position, Y-rotation and draw scale. Actors
    /// with no resolvable model are skipped (the cross marker still locates them).
    /// </summary>
    // Cached actor geometry: the transformed/batched vertex buffers, rebuilt ONLY when the actor set/poses
    // (or door theme, which changes models) change. Without this the anim timer's ~30fps repaints re-resolved
    // and re-transformed EVERY actor's every triangle each frame — the main cause of the editor feeling
    // sluggish with an imported level loaded. Now unchanged frames just re-draw the cached buffers.
    // Per-slot so different callers (the main actor pass, the pot-content ghosts) each keep their own cache
    // and don't invalidate each other every frame.
    private readonly Dictionary<int, (long sig, List<(RomTexInfo? tex, float[] verts)> batches)> _actorCaches = new();

    public void RenderActors(IEnumerable<ZActor> actors, ActorModelResolver resolver, bool adult,
                             RomImage rom, Camera3D cam, int w, int h, int cacheSlot = 0)
    {
        _texSource ??= new RomTextureSource(rom);
        var mvp = cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h);
        _shader.Use();
        GL.Uniform3(GL.GetUniformLocation(_shader.Handle, "uColorMul"), 1f, 1f, 1f);   // white default (colour-cycle off)
        GL.Uniform2(GL.GetUniformLocation(_shader.Handle, "uUVScroll"), 0f, 0f);
        GL.Uniform1(GL.GetUniformLocation(_shader.Handle, "uOpaqueSurf"), 1);   // actor skin: opacity is combiner-driven, ignore texel alpha
        // Force the OPAQUE, non-ghost path: character skin textures store RGB with a ~0 texel alpha, so if
        // uXlu/uGhostAlpha leak in from the prior room draw the actor writes vec4(colour, 0) — correct colour
        // but a fully-transparent framebuffer alpha (the offscreen gallery then reads a transparent cut-out).
        GL.Uniform1(GL.GetUniformLocation(_shader.Handle, "uXlu"), 0);
        GL.Uniform1(GL.GetUniformLocation(_shader.Handle, "uGhostAlpha"), 1f);
        GL.Disable(EnableCap.Blend);
        _shader.SetMatrix4("uMVP", mvp);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Uniform1(GL.GetUniformLocation(_shader.Handle, "uTex"), 0);

        var list = actors as IReadOnlyList<ZActor> ?? actors.ToList();
        long sig = ActorSig(list, adult, resolver);
        if (!_actorCaches.TryGetValue(cacheSlot, out var entry) || entry.sig != sig)
        {
            var cache = new List<(RomTexInfo? tex, float[] verts)>();
            var batches = new Dictionary<long, (RomTexInfo? tex, List<float> buf)>();
            foreach (var a in list)
            {
                var model = resolver.Resolve(a, adult);
                if (model == null) continue;

                float ang = model.IgnoreYaw ? 0f : a.YRot * (MathF.PI / 32768f);   // binary angle → radians
                float cs = MathF.Cos(ang), sn = MathF.Sin(ang);
                Vector3 pos = a.Position + resolver.ModelDrawOffset(a, model);   // per-actor model draw offset (audit)
                float s = model.Scale;
                // Per-actor upright correction (object space), applied before the placement yaw.
                var br = model.BaseRotationDeg;
                bool hasBase = br != Vector3.Zero;
                Matrix3 baseM = hasBase
                    ? Matrix3.CreateRotationZ(MathHelper.DegreesToRadians(br.Z))
                      * Matrix3.CreateRotationY(MathHelper.DegreesToRadians(br.Y))
                      * Matrix3.CreateRotationX(MathHelper.DegreesToRadians(br.X))
                    : Matrix3.Identity;

                foreach (var t in model.Tris)
                {
                    long key = t.Texture is { } ti ? Key(ti) : -1;
                    if (!batches.TryGetValue(key, out var b)) batches[key] = b = (t.Texture, []);
                    var p0 = hasBase ? baseM * t.P0 : t.P0;
                    var p1 = hasBase ? baseM * t.P1 : t.P1;
                    var p2 = hasBase ? baseM * t.P2 : t.P2;
                    Push(b.buf, Xform(p0, pos, cs, sn, s), t.C0, t.T0);
                    Push(b.buf, Xform(p1, pos, cs, sn, s), t.C1, t.T1);
                    Push(b.buf, Xform(p2, pos, cs, sn, s), t.C2, t.T2);
                }
            }
            foreach (var (_, b) in batches) cache.Add((b.tex, b.buf.ToArray()));
            entry = (sig, cache);
            _actorCaches[cacheSlot] = entry;
        }

        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        int useLoc = GL.GetUniformLocation(_shader.Handle, "uUseTex");
        foreach (var (tex, verts) in entry.batches)
        {
            int gl = tex is { } ti ? GetGlTexture(ti) : 0;
            GL.Uniform1(useLoc, gl != 0 ? 1 : 0);
            if (gl != 0) GL.BindTexture(TextureTarget.Texture2D, gl);
            DrawArr(verts);
        }
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.Uniform1(GL.GetUniformLocation(_shader.Handle, "uOpaqueSurf"), 0);   // reset so room/backdrop keep alpha-cut
    }

    // Cheap content hash of the actor render inputs — the cache rebuilds only when it changes.
    private static long ActorSig(IReadOnlyList<ZActor> actors, bool adult, ActorModelResolver r)
    {
        long h = 1469598103934665603L;
        void M(long v) { h = (h ^ v) * 1099511628211L; }
        M(adult ? 1 : 0); M((int)r.DoorStyle); M((int)r.BossDoorTheme); M(actors.Count);
        foreach (var a in actors)
        {
            M(a.Number); M(a.Variable);
            M((long)MathF.Round(a.XPos)); M((long)MathF.Round(a.YPos)); M((long)MathF.Round(a.ZPos));
            M(a.YRot); M(a.LockBack ? 1 : 0);
        }
        return h;
    }

    private void DrawArr(float[] verts)
    {
        if (verts.Length == 0) return;
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StreamDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, verts.Length / 8);
        GL.BindVertexArray(0);
    }

    // Per-level cache of the static backdrop batches. Keyed by the level object (the ghost overlay is a
    // second level, so both keep their own entry) and rebuilt only when room visibility changes.
    private readonly Dictionary<ImportedLevel,
        (long visSig, List<(RomTexInfo? tex, byte anim, float[] verts)> opaque,
                      List<(RomTexInfo? tex, byte anim, float[] verts)> xlu)> _backdropCache = new();

    private (List<(RomTexInfo? tex, byte anim, float[] verts)> opaque,
             List<(RomTexInfo? tex, byte anim, float[] verts)> xlu) GetBackdropBatches(ImportedLevel level)
    {
        long visSig = 1469598103934665603L;
        void Mv(long v) { visSig = (visSig ^ v) * 1099511628211L; }
        Mv(level.RoomMeshes.Count);
        for (int i = 0; i < level.RoomVisible.Length; i++) if (level.RoomVisible[i]) Mv(i + 1);
        if (_backdropCache.TryGetValue(level, out var e) && e.visSig == visSig) return (e.opaque, e.xlu);

        var opaqueMap = new Dictionary<long, (RomTexInfo? tex, byte anim, List<float> buf)>();
        var xluMap    = new Dictionary<long, (RomTexInfo? tex, byte anim, List<float> buf)>();
        for (int ri = 0; ri < level.RoomMeshes.Count; ri++)
        {
            if (ri < level.RoomVisible.Length && !level.RoomVisible[ri]) continue;
            foreach (var t in level.RoomMeshes[ri])
            {
                var batches = t.Xlu ? xluMap : opaqueMap;
                long key = (t.Texture is { } ti ? Key(ti) : -1) * 16 + t.AnimSeg;
                if (!batches.TryGetValue(key, out var b)) batches[key] = b = (t.Texture, t.AnimSeg, []);
                Push(b.buf, t.P0, t.C0, t.T0); Push(b.buf, t.P1, t.C1, t.T1); Push(b.buf, t.P2, t.C2, t.T2);
            }
        }
        var opaque = opaqueMap.Values.Select(b => (b.tex, b.anim, verts: b.buf.ToArray())).ToList();
        var xlu    = xluMap.Values.Select(b => (b.tex, b.anim, verts: b.buf.ToArray())).ToList();
        _backdropCache[level] = (visSig, opaque, xlu);
        return (opaque, xlu);
    }

    // Object-space vertex → world: scale, rotate about Y by the actor's heading, translate.
    private static Vector3 Xform(Vector3 p, Vector3 pos, float cs, float sn, float s)
    {
        float x = p.X * s, y = p.Y * s, z = p.Z * s;
        return new Vector3(pos.X + (x * cs + z * sn), pos.Y + y, pos.Z + (-x * sn + z * cs));
    }

    /// <summary>Draws a plain untextured triangle list (imported OBJ reference mesh).</summary>
    public void RenderTris(IReadOnlyList<MeshTri> tris, Camera3D cam, int w, int h)
    {
        if (tris.Count == 0) return;
        var mvp = cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h);
        _shader.Use();
        GL.Uniform3(GL.GetUniformLocation(_shader.Handle, "uColorMul"), 1f, 1f, 1f);   // white default (colour-cycle off)
        _shader.SetMatrix4("uMVP", mvp);
        GL.Uniform1(GL.GetUniformLocation(_shader.Handle, "uUseTex"), 0);
        var buf = new List<float>(tris.Count * 24);
        var col = new Vector3(0.7f, 0.72f, 0.78f);
        foreach (var t in tris)
        { Push(buf, t.P0, col, Vector2.Zero); Push(buf, t.P1, col, Vector2.Zero); Push(buf, t.P2, col, Vector2.Zero); }
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        Draw(buf);
    }

    private void Draw(List<float> buf)
    {
        if (buf.Count == 0) return;
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, buf.Count * sizeof(float), buf.ToArray(), BufferUsageHint.StreamDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, buf.Count / 8);
        GL.BindVertexArray(0);
    }

    private static void Push(List<float> b, Vector3 p, Vector3 c, Vector2 uv)
    {
        b.Add(p.X); b.Add(p.Y); b.Add(p.Z);
        b.Add(c.X); b.Add(c.Y); b.Add(c.Z);
        b.Add(uv.X); b.Add(uv.Y);
    }

    private int GetGlTexture(RomTexInfo info)
    {
        long key = Key(info);
        if (_glTex.TryGetValue(key, out int handle)) return handle;

        int tex = 0;
        try
        {
            using Bitmap bmp = _texSource!.Decode(info);
            tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, bmp.Width, bmp.Height, 0,
                          OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
            bmp.UnlockBits(data);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            // Honour the N64 tile wrap mode: clamp (faces/decals don't tile past the edge), mirror
            // (symmetric chainmail/leggings flip alternate tiles — plain repeat garbles the seams),
            // else repeat. Clamp wins over mirror, as on the RDP.
            static TextureWrapMode Wrap(bool clamp, bool mirror) =>
                clamp ? TextureWrapMode.ClampToEdge : mirror ? TextureWrapMode.MirroredRepeat : TextureWrapMode.Repeat;
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)Wrap(info.ClampS, info.MirrorS));
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)Wrap(info.ClampT, info.MirrorT));
            TextureFilter.ApplyToBound();
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }
        catch { tex = 0; }

        _glTex[key] = tex;
        return tex;
    }

    // Re-applies the texture filter to cached textures after the trilinear toggle changes.
    private int _filterEpoch = -1;
    private void RefreshFiltersIfNeeded()
    {
        if (_filterEpoch == ViewOptions.FilterEpoch) return;
        _filterEpoch = ViewOptions.FilterEpoch;
        foreach (var h in _glTex.Values)
        {
            if (h == 0) continue;
            GL.BindTexture(TextureTarget.Texture2D, h);
            TextureFilter.ApplyToBound();
        }
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    private static long Key(RomTexInfo t) =>
        ((long)t.FileIndex << 40) ^ ((long)t.Offset << 12) ^ ((long)t.Type * 977 + t.Width * 31 + t.Height)
        ^ ((t.ClampS ? 1L : 0L) << 61) ^ ((t.ClampT ? 1L : 0L) << 62)
        ^ ((t.MirrorS ? 1L : 0L) << 59) ^ ((t.MirrorT ? 1L : 0L) << 60);

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var h in _glTex.Values) if (h != 0) GL.DeleteTexture(h);
        foreach (var h in _bgTex.Values) if (h != 0) GL.DeleteTexture(h);
        foreach (var h in _objTex.Values) if (h != 0) GL.DeleteTexture(h);
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        _shader.Dispose();
        _texSource?.Dispose();
        _disposed = true;
    }
}
