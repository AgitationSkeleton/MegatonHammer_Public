using System.Globalization;
using System.Text;
using MegatonHammer.Editor;
using OpenTK.Mathematics;

namespace MegatonHammer.Export;

/// <summary>
/// Valve Map Format (.vmf) export — opens the level's brushes directly in Valve Hammer for
/// round-tripping/reference. Each brush is a VMF solid with one side per face (plane from 3
/// face points, texture axes from the face's mapping). Texture/scale conventions differ from
/// Source, so texturing is approximate; geometry is exact.
/// </summary>
public static class VmfIO
{
    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Pt(Vector3 p) => $"({F(p.X)} {F(p.Y)} {F(p.Z)})";

    public static void Export(ZScene scene, string vmfPath)
    {
        var sb = new StringBuilder();
        int id = 0;
        int Next() => ++id;

        sb.AppendLine("versioninfo\n{");
        sb.AppendLine("\t\"editorversion\" \"400\"\n\t\"editorbuild\" \"8000\"\n\t\"mapversion\" \"1\"\n\t\"formatversion\" \"100\"\n\t\"prefab\" \"0\"\n}");
        sb.AppendLine("viewsettings\n{\n\t\"bSnapToGrid\" \"1\"\n\t\"bShowGrid\" \"1\"\n\t\"nGridSpacing\" \"64\"\n}");
        sb.AppendLine("world\n{");
        sb.AppendLine($"\t\"id\" \"{Next()}\"");
        sb.AppendLine("\t\"classname\" \"worldspawn\"");

        foreach (var room in scene.Rooms)
            foreach (var solid in room.Geometry)
            {
                sb.AppendLine("\tsolid\n\t{");
                sb.AppendLine($"\t\t\"id\" \"{Next()}\"");
                foreach (var face in solid.Faces)
                {
                    var v = face.Vertices;
                    if (v.Count < 3) continue;
                    var (u, uv) = face.TextureAxes();
                    string mat = (face.TextureName ?? "TOOLS/TOOLSNODRAW").ToUpperInvariant();
                    sb.AppendLine("\t\tside\n\t\t{");
                    sb.AppendLine($"\t\t\t\"id\" \"{Next()}\"");
                    // Hammer wants 3 plane points ordered clockwise looking at the front face.
                    sb.AppendLine($"\t\t\t\"plane\" \"{Pt(v[2])} {Pt(v[1])} {Pt(v[0])}\"");
                    sb.AppendLine($"\t\t\t\"material\" \"{mat}\"");
                    sb.AppendLine($"\t\t\t\"uaxis\" \"[{F(u.X)} {F(u.Y)} {F(u.Z)} {F(face.TexShiftS)}] {F(face.TexScaleS / 64f)}\"");
                    sb.AppendLine($"\t\t\t\"vaxis\" \"[{F(uv.X)} {F(uv.Y)} {F(uv.Z)} {F(face.TexShiftT)}] {F(face.TexScaleT / 64f)}\"");
                    sb.AppendLine($"\t\t\t\"rotation\" \"{F(face.TexRotation)}\"");
                    sb.AppendLine("\t\t\t\"lightmapscale\" \"16\"\n\t\t\t\"smoothing_groups\" \"0\"\n\t\t}");
                }
                sb.AppendLine("\t}");
            }

        sb.AppendLine("}");
        // Actors as point entities (info_target placeholders carrying the actor id/params).
        foreach (var room in scene.Rooms)
            foreach (var a in room.Actors)
            {
                sb.AppendLine("entity\n{");
                sb.AppendLine($"\t\"id\" \"{Next()}\"");
                sb.AppendLine("\t\"classname\" \"info_target\"");
                sb.AppendLine($"\t\"targetname\" \"actor_0x{a.Number:X4}\"");
                sb.AppendLine($"\t\"z64_actor\" \"0x{a.Number:X4}\"");
                sb.AppendLine($"\t\"z64_params\" \"0x{a.Variable:X4}\"");
                sb.AppendLine($"\t\"angles\" \"0 {a.YRot * 360f / 65536f:0.##} 0\"");
                sb.AppendLine($"\t\"origin\" \"{F(a.XPos)} {F(a.YPos)} {F(a.ZPos)}\"");
                sb.AppendLine("}");
            }

        File.WriteAllText(vmfPath, sb.ToString());
    }
}
