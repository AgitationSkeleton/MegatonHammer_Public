using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MegatonHammer.Rendering;

/// <summary>Draws a full-screen vertical gradient as the 3D-view sky background.</summary>
public sealed class SkyRenderer : IDisposable
{
    private static readonly string Vert = @"
#version 330 core
layout(location=0) in vec2 aPos;
out float vT;
void main() { vT = aPos.y * 0.5 + 0.5; gl_Position = vec4(aPos, 0.0, 1.0); }";

    private static readonly string Frag = @"
#version 330 core
in float vT;
out vec4 fragColor;
uniform vec3 uTop;
uniform vec3 uBottom;
void main() { fragColor = vec4(mix(uBottom, uTop, vT), 1.0); }";

    private readonly Shader _shader;
    private readonly int _vao, _vbo;
    private bool _disposed;

    public SkyRenderer()
    {
        _shader = new Shader(Vert, Frag);
        float[] quad = [ -1, -1,  3, -1, -1, 3 ];   // single oversized triangle
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, quad.Length * sizeof(float), quad, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);
    }

    public void Draw(Vector3 top, Vector3 bottom)
    {
        bool depth = GL.IsEnabled(EnableCap.DepthTest);
        GL.Disable(EnableCap.DepthTest);
        _shader.Use();
        _shader.SetVector3("uTop",    top);
        _shader.SetVector3("uBottom", bottom);
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.BindVertexArray(0);
        if (depth) GL.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        if (_disposed) return;
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        _shader.Dispose();
        _disposed = true;
    }
}
