using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using MegatonHammer.Editor;
using MegatonHammer.Rom;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MegatonHammer.Rendering;

/// <summary>
/// Draws camera-facing sprite billboards. Used for "obsolete" entities (actor ids the editor
/// doesn't recognise): they show the Eyeball Frog "OBSOLETE" gag sprite so they're instantly
/// identifiable as loaded-but-unrecognised, regardless of game (D7). The sprite is a bundled
/// PNG, so it works for OoT and MM alike.
/// </summary>
public sealed class BillboardRenderer : IDisposable
{
    private const string Vert = @"
#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec2 aUV;
uniform mat4 uMVP;
out vec2 vUV;
void main() { gl_Position = uMVP * vec4(aPos,1.0); vUV = aUV; }";

    private const string Frag = @"
#version 330 core
in vec2 vUV; out vec4 fragColor;
uniform sampler2D uTex;
void main() { vec4 t = texture(uTex, vUV); if (t.a < 0.3) discard; fragColor = t; }";

    // Flat-colour billboard (no texture) — the unmodeled-actor fallback when the ROM has no item
    // icons (e.g. MM, whose icon_item_static layout differs from OoT's). A solid camera-facing quad.
    private const string FlatFrag = @"
#version 330 core
out vec4 fragColor;
uniform vec4 uColor;
void main() { fragColor = uColor; }";

    // #6: semi-transparent "hologram" — the item sprite floating above a chest. Same as the sprite shader
    // but multiplies the texel alpha by uAlpha so it reads as a translucent projection.
    private const string HoloFrag = @"
#version 330 core
in vec2 vUV; out vec4 fragColor;
uniform sampler2D uTex; uniform float uAlpha;
void main() { vec4 t = texture(uTex, vUV); if (t.a < 0.3) discard; fragColor = vec4(t.rgb, t.a * uAlpha); }";

    private readonly Shader _shader;
    private readonly Shader _flatShader;
    private Shader? _holoShader;
    private readonly int _vao, _vbo;
    private int _tex;
    private float _aspect = 1f;     // sprite width/height
    private bool _disposed;

    private const float SpriteHeight = 60f;   // world units tall

    public BillboardRenderer()
    {
        _shader = new Shader(Vert, Frag);
        _flatShader = new Shader(Vert, FlatFrag);
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);
        _tex = LoadSprite("obsolete_entity.png");
    }

    /// <summary>Draws the obsolete sprite at each obsolete actor's position, facing the camera.</summary>
    public void RenderObsolete(IEnumerable<ZActor> actors, Camera3D cam, int w, int h, bool ignoreDepth = false)
    {
        if (_tex == 0) return;
        cam.UpdateVectors();
        Vector3 right = cam.Right, up = cam.Up;
        var mvp = cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h);

        _shader.Use();
        _shader.SetMatrix4("uMVP", mvp);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _tex);
        GL.Uniform1(GL.GetUniformLocation(_shader.Handle, "uTex"), 0);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(ignoreDepth ? DepthFunction.Always : DepthFunction.Less);   // renders draw billboards through walls
        GL.Disable(EnableCap.CullFace);

        float hh = SpriteHeight * 0.5f, hw = hh * _aspect;
        var buf = new List<float>();
        foreach (var a in actors)
        {
            Vector3 c = a.Position + new Vector3(0, hh, 0);   // sit the sprite above the point
            Vector3 r = right * hw, u = up * hh;
            Vector3 bl = c - r - u, br = c + r - u, tl = c - r + u, tr = c + r + u;
            // two triangles, V flipped (texture top = +up)
            Quad(buf, bl, br, tr, tl);
        }
        if (buf.Count == 0) return;

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, buf.Count * sizeof(float), buf.ToArray(), BufferUsageHint.StreamDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, buf.Count / 5);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>Draws a solid camera-facing quad at each unmodeled actor — the fallback when the ROM
    /// provides no item icons (MM). Semi-transparent, coloured by selection state, so the actor reads
    /// as a placed "sprite" entity rather than a bare cross. Companion to <see cref="RenderSprites"/>.</summary>
    public void RenderFlatSprites(IReadOnlyList<ZActor> actors, Camera3D cam, int w, int h, bool ignoreDepth = false)
    {
        if (actors.Count == 0) return;
        cam.UpdateVectors();
        Vector3 right = cam.Right, up = cam.Up;
        var mvp = cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h);

        _flatShader.Use();
        _flatShader.SetMatrix4("uMVP", mvp);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(ignoreDepth ? DepthFunction.Always : DepthFunction.Less);   // renders draw billboards through walls
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        float hh = SpriteHeight * 0.35f, hw = hh;
        foreach (var grp in actors.GroupBy(a => a.IsSelected))
        {
            _flatShader.SetVector4("uColor", grp.Key
                ? new Vector4(1.00f, 0.45f, 0.10f, 0.60f)    // selected — orange
                : new Vector4(1.00f, 0.82f, 0.15f, 0.50f));  // gold
            var buf = new List<float>();
            foreach (var a in grp)
            {
                Vector3 c = a.Position + new Vector3(0, hh, 0);
                Vector3 r = right * hw, u = up * hh;
                Quad(buf, c - r - u, c + r - u, c + r + u, c - r + u);
            }
            if (buf.Count == 0) continue;
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, buf.Count * sizeof(float), buf.ToArray(), BufferUsageHint.StreamDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, buf.Count / 5);
        }
        GL.BindVertexArray(0);
        GL.Disable(EnableCap.Blend);
    }

    private readonly Dictionary<int, int> _iconTex = new();   // item icon index → GL texture handle

    /// <summary>Draws an item/inventory sprite (icon index from <see cref="ActorSpriteMap"/>) at each
    /// unmodeled actor, facing the camera — the fallback for actors with no 3D model in the ROM.</summary>
    public void RenderSprites(IReadOnlyList<(ZActor actor, int icon)> items, ItemIconSource icons, Camera3D cam, int w, int h, bool ignoreDepth = false)
    {
        if (items.Count == 0 || !icons.Available) return;
        cam.UpdateVectors();
        Vector3 right = cam.Right, up = cam.Up;
        var mvp = cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h);

        _shader.Use();
        _shader.SetMatrix4("uMVP", mvp);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Uniform1(GL.GetUniformLocation(_shader.Handle, "uTex"), 0);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(ignoreDepth ? DepthFunction.Always : DepthFunction.Less);   // renders draw billboards through walls
        GL.Disable(EnableCap.CullFace);

        float hh = SpriteHeight * 0.5f, hw = hh;   // icons are square
        foreach (var grp in items.GroupBy(it => it.icon))   // bind each icon texture once
        {
            int tex = IconTexture(grp.Key, icons);
            if (tex == 0) continue;
            GL.BindTexture(TextureTarget.Texture2D, tex);
            var buf = new List<float>();
            foreach (var (a, _) in grp)
            {
                Vector3 c = a.Position + new Vector3(0, hh, 0);
                Vector3 r = right * hw, u = up * hh;
                Quad(buf, c - r - u, c + r - u, c + r + u, c - r + u);
            }
            if (buf.Count == 0) continue;
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, buf.Count * sizeof(float), buf.ToArray(), BufferUsageHint.StreamDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, buf.Count / 5);
        }
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>#6: draws each item's icon as a translucent hologram FLOATING ABOVE the actor (a chest),
    /// camera-facing — so a chest visibly previews its contents. Depth-tested (occluded by walls) and
    /// alpha-blended. Items map to (actor, iconIndex); a missing icon is simply skipped.</summary>
    public void RenderHologram(IReadOnlyList<(ZActor actor, int icon)> items, ItemIconSource icons,
                               Camera3D cam, int w, int h, float elevation = 95f, float alpha = 0.6f)
    {
        if (items.Count == 0 || !icons.Available) return;
        _holoShader ??= new Shader(Vert, HoloFrag);
        cam.UpdateVectors();
        Vector3 right = cam.Right, up = cam.Up;
        var mvp = cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h);

        _holoShader.Use();
        _holoShader.SetMatrix4("uMVP", mvp);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Uniform1(GL.GetUniformLocation(_holoShader.Handle, "uTex"), 0);
        GL.Uniform1(GL.GetUniformLocation(_holoShader.Handle, "uAlpha"), alpha);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        float hh = SpriteHeight * 0.4f, hw = hh;
        foreach (var grp in items.GroupBy(it => it.icon))
        {
            int tex = IconTexture(grp.Key, icons);
            if (tex == 0) continue;
            GL.BindTexture(TextureTarget.Texture2D, tex);
            var buf = new List<float>();
            foreach (var (a, _) in grp)
            {
                Vector3 c = a.Position + new Vector3(0, elevation, 0);
                Vector3 r = right * hw, u = up * hh;
                Quad(buf, c - r - u, c + r - u, c + r + u, c - r + u);
            }
            if (buf.Count == 0) continue;
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, buf.Count * sizeof(float), buf.ToArray(), BufferUsageHint.StreamDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, buf.Count / 5);
        }
        GL.Disable(EnableCap.Blend);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    // ── Path waypoint markers (billboard sprites) ───────────────────────────────────────────────────
    private int _pathGlyph = -1, _pathGlyphSel = -1;

    /// <summary>A drawn waypoint glyph (a diamond node on a small post) so path nodes read as billboard
    /// entities — double-clickable like any other sprite — instead of bare wire boxes.</summary>
    private static Bitmap MakePathGlyph(Color fill, Color edge)
    {
        var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        // post
        using (var pen = new Pen(edge, 2f)) g.DrawLine(pen, 16, 30, 16, 18);
        // diamond node
        var dia = new[] { new Point(16, 4), new Point(28, 15), new Point(16, 26), new Point(4, 15) };
        using (var br = new SolidBrush(fill)) g.FillPolygon(br, dia);
        using (var pen = new Pen(edge, 2f)) g.DrawPolygon(pen, dia);
        using (var br = new SolidBrush(Color.White)) g.FillEllipse(br, 13, 12, 6, 6);
        return bmp;
    }

    /// <summary>Draw a camera-facing waypoint sprite at each path node. Selected nodes use the orange glyph.</summary>
    public void RenderPathMarkers(IReadOnlyList<(Vector3 pos, bool sel)> nodes, Camera3D cam, int w, int h)
    {
        if (nodes.Count == 0) return;
        if (_pathGlyph < 0) _pathGlyph = UploadBitmap(MakePathGlyph(Color.FromArgb(70, 200, 235), Color.FromArgb(20, 80, 110)));
        if (_pathGlyphSel < 0) _pathGlyphSel = UploadBitmap(MakePathGlyph(Color.FromArgb(255, 150, 40), Color.FromArgb(120, 60, 10)));
        cam.UpdateVectors();
        Vector3 right = cam.Right, up = cam.Up;
        _holoShader ??= new Shader(Vert, HoloFrag);
        _holoShader.Use();
        _holoShader.SetMatrix4("uMVP", cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h));
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Uniform1(GL.GetUniformLocation(_holoShader.Handle, "uTex"), 0);
        GL.Uniform1(GL.GetUniformLocation(_holoShader.Handle, "uAlpha"), 1.0f);
        GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Less);
        GL.Disable(EnableCap.CullFace); GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        float hh = SpriteHeight * 0.30f, hw = hh;
        foreach (var grp in nodes.GroupBy(n => n.sel))
        {
            GL.BindTexture(TextureTarget.Texture2D, grp.Key ? _pathGlyphSel : _pathGlyph);
            var buf = new List<float>();
            foreach (var (p, _) in grp)
            {
                Vector3 c = p;   // centered on the node so it aligns with the ±16 double-click pick box
                Vector3 r = right * hw, u = up * hh;
                Quad(buf, c - r - u, c + r - u, c + r + u, c - r + u);
            }
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, buf.Count * sizeof(float), buf.ToArray(), BufferUsageHint.StreamDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, buf.Count / 5);
        }
        GL.Disable(EnableCap.Blend);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    private int IconTexture(int icon, ItemIconSource icons)
    {
        if (_iconTex.TryGetValue(icon, out int t)) return t;
        var bmp = icons.Icon(icon);
        t = bmp != null ? UploadBitmap(bmp) : 0;
        _iconTex[icon] = t;
        return t;
    }

    private static int UploadBitmap(Bitmap bmp)
    {
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, bmp.Width, bmp.Height, 0,
                      OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
        bmp.UnlockBits(data);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    private static void Quad(List<float> b, Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl)
    {
        void V(Vector3 p, float u, float v) { b.Add(p.X); b.Add(p.Y); b.Add(p.Z); b.Add(u); b.Add(v); }
        V(bl, 0, 1); V(br, 1, 1); V(tr, 1, 0);
        V(bl, 0, 1); V(tr, 1, 0); V(tl, 0, 0);
    }

    private int LoadSprite(string name)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream($"MegatonHammer.Assets.{name}");
            if (stream == null) return 0;
            using var bmp = new Bitmap(stream);
            _aspect = bmp.Width / (float)bmp.Height;

            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, bmp.Width, bmp.Height, 0,
                          OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
            bmp.UnlockBits(data);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            return tex;
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_tex != 0) GL.DeleteTexture(_tex);
        foreach (var t in _iconTex.Values) if (t != 0) GL.DeleteTexture(t);
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        _shader.Dispose();
        _flatShader.Dispose();
        _holoShader?.Dispose();
        _disposed = true;
    }
}
