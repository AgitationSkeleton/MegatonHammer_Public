namespace MegatonHammer.Editor;

/// <summary>
/// Shared grid math so the VISIBLE 2D grid and the SNAP used when drawing/scaling brushes
/// are always the same spacing — like Valve Hammer, where "what you see is what you snap to".
/// The base grid size (set by the [ and ] keys) is the reference spacing, but the EFFECTIVE step
/// follows the zoom in BOTH directions: when zoomed out so base-size cells would be too dense the
/// step coarsens (×2); when zoomed IN so base-size cells get large the step refines (÷2, down to 1
/// unit). So you can draw/snap on the fine cells you actually see when zoomed in, without lowering
/// the grid-size button — and the cells stay a comfortable on-screen size at any zoom.
/// </summary>
public static class GridSnap
{
    // Target on-screen cell spacing band (px): coarsen below MinPixels (zoomed out), refine above
    // MaxPixels (zoomed in). MaxPixels >= 2*MinPixels so the two loops never fight (hysteresis).
    private const float MinPixels = 6f;
    private const float MaxPixels = 40f;

    /// <summary>
    /// The effective grid step at the current zoom. <paramref name="worldPerPixel"/> is the
    /// 2D camera's Zoom (world units per screen pixel). Adapts to keep cells in the pixel band;
    /// never finer than 1 unit.
    /// </summary>
    public static int EffectiveStep(int baseGrid, float worldPerPixel)
    {
        int s = baseGrid < 1 ? 1 : baseGrid;
        if (worldPerPixel <= 0f) return s;
        int guard = 0;
        // Zoomed out: base cells too dense -> coarsen until legible.
        while (s / worldPerPixel < MinPixels && guard++ < 32) s *= 2;
        // Zoomed in: base cells too large -> refine so the snap follows the fine cells you can see.
        while (s > 1 && s / worldPerPixel > MaxPixels && guard++ < 64) s /= 2;
        return s < 1 ? 1 : s;
    }

    /// <summary>
    /// Live check (injected by the app) for whether snapping is momentarily suspended — Valve
    /// Hammer suspends the grid for as long as Ctrl is held during a drag, so the dragged item
    /// moves freely and re-snaps the instant Ctrl is released. Null = never suspended.
    /// </summary>
    public static Func<bool>? SnapSuspended;

    /// <summary>True when the grid snap is currently in effect (on, and not Ctrl-suspended).</summary>
    public static bool SnappingActive => ViewOptions.SnapToGrid && SnapSuspended?.Invoke() != true;

    /// <summary>
    /// The step the brush tools should snap to: the visible grid when snapping is on (and not
    /// Ctrl-suspended), else 1 (free placement). Honours <see cref="ViewOptions.SnapToGrid"/>.
    /// </summary>
    public static int ActiveStep(int baseGrid, float worldPerPixel)
        => SnappingActive ? EffectiveStep(baseGrid, worldPerPixel) : 1;

    /// <summary>Snaps a scalar to the given step.</summary>
    public static float Snap(float v, int step) => step < 1 ? v : MathF.Round(v / step) * step;
}
