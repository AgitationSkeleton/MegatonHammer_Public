using OpenTK.Mathematics;

namespace MegatonHammer.Rendering;

public class Camera3D
{
    public Vector3 Position = new(0f, 128f, 512f);
    public float Yaw   = -90f;
    public float Pitch = -10f;
    public float MoveSpeed = 300f;
    public float MouseSensitivity = 0.15f;
    public float Fov = 70f;
    // Clip planes. The near plane dominates depth-buffer precision: keeping near as far out as the
    // scene allows (and far as close in) avoids z-fighting on coplanar floor/water/decal surfaces.
    public float Near = 4f;
    public float Far  = 200_000f;

    private Vector3 _front = -Vector3.UnitZ;
    private Vector3 _right = Vector3.UnitX;
    private Vector3 _up    = Vector3.UnitY;

    /// <summary>Camera right/up vectors (call UpdateVectors first) — for screen-facing billboards.</summary>
    public Vector3 Right => _right;
    public Vector3 Up    => _up;

    public void UpdateVectors()
    {
        float yawR   = MathHelper.DegreesToRadians(Yaw);
        float pitchR = MathHelper.DegreesToRadians(MathHelper.Clamp(Pitch, -89f, 89f));

        _front = Vector3.Normalize(new Vector3(
            MathF.Cos(pitchR) * MathF.Cos(yawR),
            MathF.Sin(pitchR),
            MathF.Cos(pitchR) * MathF.Sin(yawR)));

        _right = Vector3.Normalize(Vector3.Cross(_front, Vector3.UnitY));
        _up    = Vector3.Normalize(Vector3.Cross(_right, _front));
    }

    public Matrix4 GetViewMatrix()
    {
        UpdateVectors();
        return Matrix4.LookAt(Position, Position + _front, Vector3.UnitY);
    }

    public Matrix4 GetProjectionMatrix(int width, int height)
    {
        if (width == 0 || height == 0) return Matrix4.Identity;
        return Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(Fov),
            width / (float)height,
            MathF.Max(0.5f, Near), MathF.Max(Near + 1f, Far));
    }

    public void ProcessMouseDelta(float dx, float dy)
    {
        Yaw   += dx * MouseSensitivity;
        Pitch -= dy * MouseSensitivity;
        Pitch  = MathHelper.Clamp(Pitch, -89f, 89f);
    }

    public void MoveForward(float dt) => Position += _front * MoveSpeed * dt;
    public void MoveBack(float dt)    => Position -= _front * MoveSpeed * dt;
    public void MoveLeft(float dt)    => Position -= _right * MoveSpeed * dt;
    public void MoveRight(float dt)   => Position += _right * MoveSpeed * dt;
    public void MoveUp(float dt)      => Position += Vector3.UnitY * MoveSpeed * dt;
    public void MoveDown(float dt)    => Position -= Vector3.UnitY * MoveSpeed * dt;
}
