using System.Drawing;
using System.Drawing.Imaging;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using GLPixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace MegatonHammer.Rendering;

/// <summary>
/// Draws short strings as alpha-blended textured quads in screen pixel space — used for the
/// 2D-view selection dimension labels (Hammer shows width along the top, height down the left).
/// Each distinct string is rasterised once via GDI and cached as a GL texture.
/// </summary>
public sealed class GlTextRenderer : IDisposable
{
    private const string Vert = @"
#version 330 core
layout(location=0) in vec2 aPos;
layout(location=1) in vec2 aUV;
uniform mat4 uMVP;
out vec2 vUV;
void main() { gl_Position = uMVP * vec4(aPos, 0.0, 1.0); vUV = aUV; }";

    private const string Frag = @"
#version 330 core
in vec2 vUV;
out vec4 fragColor;
uniform sampler2D uTex;
uniform vec4 uColor;
void main() { float a = texture(uTex, vUV).a; fragColor = vec4(uColor.rgb, uColor.a * a); }";

    private readonly Shader _shader;
    private readonly int _vao, _vbo;
    private readonly Font _font = new("Segoe UI", 8.25f, FontStyle.Bold);
    private readonly Dictionary<string, (int tex, int w, int h)> _cache = new();
    private bool _disposed;

    public GlTextRenderer()
    {
        _shader = new Shader(Vert, Frag);
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);
    }

    /// <summary>Measured pixel size of a string (so callers can centre/right-align it).</summary>
    public (int w, int h) Measure(string s) { var e = Get(s); return (e.w, e.h); }

    /// <summary>Draws <paramref name="s"/> with its top-left at screen pixel (x,y). Origin is the
    /// viewport's top-left, y increasing downward.</summary>
    public void Draw(string s, float x, float y, int viewW, int viewH, Vector4 color)
    {
        if (string.IsNullOrEmpty(s) || viewW <= 0 || viewH <= 0) return;
        var (tex, w, h) = Get(s);

        var mvp = Matrix4.CreateOrthographicOffCenter(0, viewW, viewH, 0, -1, 1);
        float[] quad =
        [
            x,     y,     0f, 0f,
            x + w, y,     1f, 0f,
            x + w, y + h, 1f, 1f,
            x,     y,     0f, 0f,
            x + w, y + h, 1f, 1f,
            x,     y + h, 0f, 1f,
        ];

        bool blendWas = GL.IsEnabled(EnableCap.Blend);
        bool depthWas = GL.IsEnabled(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.DepthTest);

        _shader.Use();
        _shader.SetMatrix4("uMVP", mvp);
        _shader.SetVector4("uColor", color);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.Uniform1(GL.GetUniformLocation(_shader.Handle, "uTex"), 0);

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, quad.Length * sizeof(float), quad, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        if (!blendWas) GL.Disable(EnableCap.Blend);
        if (depthWas) GL.Enable(EnableCap.DepthTest);
    }

    private (int tex, int w, int h) Get(string s)
    {
        if (_cache.TryGetValue(s, out var e)) return e;
        if (_cache.Count > 256) { ClearCache(); }   // bound the cache (dimensions change constantly)

        SizeF size;
        using (var tmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(tmp))
            size = g.MeasureString(s, _font);
        int w = Math.Max(1, (int)Math.Ceiling(size.Width) + 2);
        int h = Math.Max(1, (int)Math.Ceiling(size.Height) + 2);

        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var br = new SolidBrush(Color.White);
            g.DrawString(s, _font, br, 1, 1);
        }

        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, w, h, 0, GLPixelFormat.Bgra, PixelType.UnsignedByte, bits.Scan0);
        bmp.UnlockBits(bits);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        e = (tex, w, h);
        _cache[s] = e;
        return e;
    }

    private void ClearCache()
    {
        foreach (var (tex, _, _) in _cache.Values) GL.DeleteTexture(tex);
        _cache.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        ClearCache();
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        _shader.Dispose();
        _font.Dispose();
        _disposed = true;
    }
}
