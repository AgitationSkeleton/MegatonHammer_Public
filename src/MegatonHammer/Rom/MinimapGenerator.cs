using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using MegatonHammer.Editor;
using OpenTK.Mathematics;

namespace MegatonHammer.Rom;

/// <summary>
/// Auto-generates a top-down minimap image of a level by rasterising its geometry footprint
/// onto the XZ plane (like the dungeon map OcarinaSharp produces). Useful as the editor's own
/// overview and as the basis for the in-game pause map. Pure image generation; registering the
/// map with the game (map data + markers) is a separate step. (D15)
/// </summary>
public static class MinimapGenerator
{
    /// <summary>Renders the editable scene's brushes to a minimap bitmap of the given size.</summary>
    public static Bitmap FromScene(ZScene scene, int size = 192)
    {
        var tris = new List<(Vector2 a, Vector2 b, Vector2 c)>();
        foreach (var room in scene.Rooms)
            foreach (var solid in room.Geometry)
                foreach (var face in solid.Faces)
                {
                    var v = face.Vertices;
                    for (int i = 1; i < v.Count - 1; i++)
                        tris.Add((Xz(v[0]), Xz(v[i]), Xz(v[i + 1])));
                }
        return Rasterize(tris, size);
    }

    /// <summary>Renders an imported ROM level's decoded geometry to a minimap bitmap.</summary>
    public static Bitmap FromImported(ImportedLevel level, int size = 192)
    {
        var tris = new List<(Vector2 a, Vector2 b, Vector2 c)>();
        for (int ri = 0; ri < level.RoomMeshes.Count; ri++)
        {
            if (ri < level.RoomVisible.Length && !level.RoomVisible[ri]) continue;
            foreach (var t in level.RoomMeshes[ri])
                tris.Add((Xz(t.P0), Xz(t.P1), Xz(t.P2)));
        }
        return Rasterize(tris, size);
    }

    private static Vector2 Xz(Vector3 p) => new(p.X, p.Z);

    private static Bitmap Rasterize(List<(Vector2 a, Vector2 b, Vector2 c)> tris, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (tris.Count == 0) return bmp;

        // Fit the footprint into the image with a small margin, preserving aspect.
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var (a, b, c) in tris)
            foreach (var p in new[] { a, b, c })
            {
                minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
                minZ = MathF.Min(minZ, p.Y); maxZ = MathF.Max(maxZ, p.Y);
            }
        float w = MathF.Max(1, maxX - minX), h = MathF.Max(1, maxZ - minZ);
        const float margin = 8f;
        float scale = (size - margin * 2) / MathF.Max(w, h);
        float offX = margin + (size - margin * 2 - w * scale) * 0.5f;
        float offZ = margin + (size - margin * 2 - h * scale) * 0.5f;

        PointF P(Vector2 v) => new(offX + (v.X - minX) * scale, offZ + (v.Y - minZ) * scale);

        // Fill the footprint, then trace edges for a clean map outline.
        using var fill = new SolidBrush(Color.FromArgb(170, 70, 110, 170));   // translucent blue, OoT-map-ish
        using var pen  = new Pen(Color.FromArgb(220, 200, 220, 255), 1f);
        foreach (var (a, b, c) in tris)
        {
            var pts = new[] { P(a), P(b), P(c) };
            g.FillPolygon(fill, pts);
        }
        foreach (var (a, b, c) in tris)
        {
            var pts = new[] { P(a), P(b), P(c) };
            g.DrawPolygon(pen, pts);
        }
        return bmp;
    }

    /// <summary>Saves a minimap PNG next to the project (helper for the editor action).</summary>
    public static void Save(Bitmap map, string path) => map.Save(path, ImageFormat.Png);
}
