using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MegatonHammer.Rendering;

public sealed class Shader : IDisposable
{
    public int Handle { get; private set; }
    private bool _disposed;

    public Shader(string vertSrc, string fragSrc)
    {
        int vert = Compile(ShaderType.VertexShader, vertSrc);
        int frag = Compile(ShaderType.FragmentShader, fragSrc);

        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, vert);
        GL.AttachShader(Handle, frag);
        GL.LinkProgram(Handle);
        GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0)
            throw new Exception($"Shader link error: {GL.GetProgramInfoLog(Handle)}");

        GL.DetachShader(Handle, vert);
        GL.DetachShader(Handle, frag);
        GL.DeleteShader(vert);
        GL.DeleteShader(frag);
    }

    private static int Compile(ShaderType type, string src)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, src);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
            throw new Exception($"Shader compile error ({type}): {GL.GetShaderInfoLog(shader)}");
        return shader;
    }

    public void Use() => GL.UseProgram(Handle);

    public int GetUniform(string name) => GL.GetUniformLocation(Handle, name);

    public void SetMatrix4(string name, ref Matrix4 mat) =>
        GL.UniformMatrix4(GL.GetUniformLocation(Handle, name), false, ref mat);

    public void SetMatrix4(string name, Matrix4 mat) =>
        GL.UniformMatrix4(GL.GetUniformLocation(Handle, name), false, ref mat);

    public void SetVector4(string name, Vector4 v) =>
        GL.Uniform4(GL.GetUniformLocation(Handle, name), v);

    public void SetVector3(string name, Vector3 v) =>
        GL.Uniform3(GL.GetUniformLocation(Handle, name), v);

    public void SetFloat(string name, float f) =>
        GL.Uniform1(GL.GetUniformLocation(Handle, name), f);

    public void Dispose()
    {
        if (_disposed) return;
        GL.DeleteProgram(Handle);
        _disposed = true;
    }
}
