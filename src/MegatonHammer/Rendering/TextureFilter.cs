using MegatonHammer.Editor;
using OpenTK.Graphics.OpenGL4;

namespace MegatonHammer.Rendering;

/// <summary>
/// Maps the <see cref="ViewOptions.TrilinearFilter"/> toggle to OpenGL min/mag filters and
/// applies them. Trilinear on → smooth (linear mipmap + linear mag); off → crisp, point-sampled
/// N64-style textures (nearest mipmap + nearest mag). World-geometry renderers cache the filter
/// epoch and re-apply when the user flips the option, so it updates live.
/// </summary>
internal static class TextureFilter
{
    private static (int min, int mag) Current() => ViewOptions.TrilinearFilter
        ? ((int)TextureMinFilter.LinearMipmapLinear,  (int)TextureMagFilter.Linear)
        : ((int)TextureMinFilter.NearestMipmapNearest, (int)TextureMagFilter.Nearest);

    /// <summary>Applies the current min/mag filter to the currently-bound Texture2D.</summary>
    public static void ApplyToBound()
    {
        var (min, mag) = Current();
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, min);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, mag);
    }
}
