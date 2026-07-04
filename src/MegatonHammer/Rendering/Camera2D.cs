using OpenTK.Mathematics;

namespace MegatonHammer.Rendering;

public enum ViewAxis { Top, Front, Side }

public class Camera2D
{
    public ViewAxis Axis;
    public float PanX;
    public float PanY;
    public float Zoom = 0.25f;  // world units per pixel (smaller = more zoomed in)

    public Matrix4 GetViewMatrix() => Matrix4.Identity;

    public Matrix4 GetProjectionMatrix(int width, int height)
    {
        if (width == 0 || height == 0) return Matrix4.Identity;
        float halfW = width  * 0.5f * Zoom;
        float halfH = height * 0.5f * Zoom;
        return Matrix4.CreateOrthographicOffCenter(
            PanX - halfW, PanX + halfW,
            PanY - halfH, PanY + halfH,
            -200_000f, 200_000f);
    }

    public void Pan(float screenDX, float screenDY)
    {
        PanX -= screenDX * Zoom;
        PanY += screenDY * Zoom;
    }

    public void ZoomAt(float screenX, float screenY, int viewW, int viewH, float factor)
    {
        float worldX = PanX + (screenX - viewW * 0.5f) * Zoom;
        float worldY = PanY - (screenY - viewH * 0.5f) * Zoom;

        Zoom *= factor;
        Zoom  = MathHelper.Clamp(Zoom, 0.01f, 100f);

        PanX = worldX - (screenX - viewW * 0.5f) * Zoom;
        PanY = worldY + (screenY - viewH * 0.5f) * Zoom;
    }

    public (string horiz, string vert) AxisLabels => Axis switch
    {
        ViewAxis.Top   => ("X", "Z"),
        ViewAxis.Front => ("X", "Y"),
        ViewAxis.Side  => ("Z", "Y"),
        _              => ("?", "?")
    };

    public string AxisName => Axis switch
    {
        ViewAxis.Top   => "Top",
        ViewAxis.Front => "Front",
        ViewAxis.Side  => "Side",
        _              => "Unknown"
    };
}
