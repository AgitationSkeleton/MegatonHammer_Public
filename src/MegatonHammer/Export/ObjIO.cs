using System.Globalization;
using System.Text;
using MegatonHammer.Editor;
using OpenTK.Mathematics;

namespace MegatonHammer.Export;

/// <summary>
/// Wavefront OBJ import/export — the universal interchange with Blender, and a practical
/// stand-in for ".blend" (Blenders read OBJ natively). Export writes brush geometry with
/// per-texture materials and the editor's UV mapping; import loads an OBJ as a read-only
/// reference mesh (triangles) so external geometry can be brought in for tracing/placement.
/// </summary>
public static class ObjIO
{
    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Exports the scene's brushes to an OBJ (+ sibling .mtl). Returns the OBJ text.</summary>
    public static void Export(ZScene scene, string objPath)
    {
        var obj = new StringBuilder();
        var mtl = new StringBuilder();
        string mtlName = Path.GetFileNameWithoutExtension(objPath) + ".mtl";
        obj.AppendLine($"# Megaton Hammer export — {scene.Name}");
        obj.AppendLine($"mtllib {mtlName}");

        var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int vbase = 1;
        foreach (var room in scene.Rooms)
            foreach (var solid in room.Geometry)
                foreach (var face in solid.Faces)
                {
                    var verts = face.Vertices;
                    if (verts.Count < 3) continue;
                    string mat = SanitizeMat(face.TextureName ?? "default");
                    materials.Add(mat);

                    foreach (var p in verts) obj.AppendLine($"v {F(p.X)} {F(p.Y)} {F(p.Z)}");
                    foreach (var p in verts) { var uv = face.UVAt(p); obj.AppendLine($"vt {F(uv.X)} {F(-uv.Y)}"); }
                    var n = face.Plane.Normal;
                    obj.AppendLine($"vn {F(n.X)} {F(n.Y)} {F(n.Z)}");

                    obj.AppendLine($"usemtl {mat}");
                    var sb = new StringBuilder("f");
                    for (int i = 0; i < verts.Count; i++)
                        sb.Append($" {vbase + i}/{vbase + i}/{vbase}");   // one normal index per face
                    obj.AppendLine(sb.ToString());
                    vbase += verts.Count;
                }

        foreach (var m in materials)
        {
            mtl.AppendLine($"newmtl {m}");
            mtl.AppendLine("Kd 0.8 0.8 0.8");
            mtl.AppendLine();
        }

        File.WriteAllText(objPath, obj.ToString());
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(objPath)!, mtlName), mtl.ToString());
    }

    /// <summary>Loads an OBJ as a list of world-space triangles (for a reference backdrop).</summary>
    public static List<(Vector3 a, Vector3 b, Vector3 c)> ImportTriangles(string objPath)
    {
        var verts = new List<Vector3>();
        var tris = new List<(Vector3, Vector3, Vector3)>();
        foreach (var raw in File.ReadLines(objPath))
        {
            var line = raw.Trim();
            if (line.StartsWith("v "))
            {
                var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 4 && TryF(t[1], out var x) && TryF(t[2], out var y) && TryF(t[3], out var z))
                    verts.Add(new Vector3(x, y, z));
            }
            else if (line.StartsWith("f "))
            {
                var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var idx = new List<int>();
                for (int i = 1; i < t.Length; i++)
                {
                    string vp = t[i].Split('/')[0];
                    if (int.TryParse(vp, out int vi))
                        idx.Add(vi > 0 ? vi - 1 : verts.Count + vi);   // OBJ is 1-based; negatives are relative
                }
                for (int i = 1; i + 1 < idx.Count; i++)
                    if (InRange(idx[0], verts.Count) && InRange(idx[i], verts.Count) && InRange(idx[i + 1], verts.Count))
                        tris.Add((verts[idx[0]], verts[idx[i]], verts[idx[i + 1]]));
            }
        }
        return tris;
    }

    /// <summary>Loads an OBJ as first-class textured mesh geometry: per-vertex UVs, materials resolved
    /// to bitmaps via the sibling .mtl's map_Kd, and the SharpOcarina/Blender group conventions
    /// (#nocollision / #nomesh / #door / #metallic). N-gons are fan-triangulated; OBJ 1-based and
    /// negative indices are handled. This makes a Blender / SharpOcarina scene OBJ importable as a
    /// playable, exportable level mesh — not just a tracing backdrop.</summary>
    public static ObjMesh ImportMesh(string objPath)
    {
        var mesh = new ObjMesh();
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        string dir = Path.GetDirectoryName(objPath) ?? ".";
        var mtlTex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // material → image path

        string curMat = "default";
        bool noColl = false, noMesh = false, door = false;   // current group's convention flags

        foreach (var raw in File.ReadLines(objPath))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "mtllib" when t.Length >= 2:
                    LoadMtl(Path.Combine(dir, string.Join(' ', t[1..])), dir, mtlTex);
                    break;
                case "usemtl" when t.Length >= 2:
                    curMat = string.Join(' ', t[1..]);
                    if (!mesh.Materials.ContainsKey(curMat)) mesh.Materials[curMat] = LoadBitmap(mtlTex.GetValueOrDefault(curMat));
                    break;
                case "g":
                case "o":
                {
                    // SharpOcarina/Blender convention tags live in the group/object name.
                    string name = line.ToLowerInvariant();
                    noColl = name.Contains("#nocollision") || name.Contains("nocollision");
                    noMesh = name.Contains("#nomesh") || name.Contains("nomesh");
                    door   = name.Contains("#door") || name.Contains("tag_door");
                    break;
                }
                case "v" when t.Length >= 4 && TryF(t[1], out var x) && TryF(t[2], out var y) && TryF(t[3], out var z):
                    verts.Add(new Vector3(x, y, z));
                    break;
                case "vt" when t.Length >= 3 && TryF(t[1], out var u) && TryF(t[2], out var v):
                    uvs.Add(new Vector2(u, -v));   // OBJ V is bottom-up; flip to match the renderer
                    break;
                case "f":
                {
                    var vi = new List<int>(); var ti = new List<int>();
                    for (int i = 1; i < t.Length; i++)
                    {
                        var parts = t[i].Split('/');
                        if (int.TryParse(parts[0], out int v0)) vi.Add(v0 > 0 ? v0 - 1 : verts.Count + v0);
                        ti.Add(parts.Length >= 2 && int.TryParse(parts[1], out int u0) ? (u0 > 0 ? u0 - 1 : uvs.Count + u0) : -1);
                    }
                    for (int i = 1; i + 1 < vi.Count; i++)
                    {
                        if (!InRange(vi[0], verts.Count) || !InRange(vi[i], verts.Count) || !InRange(vi[i + 1], verts.Count)) continue;
                        mesh.Tris.Add(new ObjMesh.Tri
                        {
                            P0 = verts[vi[0]], P1 = verts[vi[i]], P2 = verts[vi[i + 1]],
                            UV0 = Uv(uvs, ti[0]), UV1 = Uv(uvs, ti[i]), UV2 = Uv(uvs, ti[i + 1]),
                            Material = curMat, NoCollision = noColl, NoMesh = noMesh, Door = door,
                        });
                    }
                    if (!mesh.Materials.ContainsKey(curMat)) mesh.Materials[curMat] = LoadBitmap(mtlTex.GetValueOrDefault(curMat));
                    break;
                }
            }
        }
        return mesh;
    }

    private static Vector2 Uv(List<Vector2> uvs, int i) => i >= 0 && i < uvs.Count ? uvs[i] : Vector2.Zero;

    // Parses a .mtl, mapping each material name to its map_Kd image path (resolved relative to the OBJ).
    private static void LoadMtl(string mtlPath, string dir, Dictionary<string, string> outMap)
    {
        if (!File.Exists(mtlPath)) return;
        string cur = "";
        foreach (var raw in File.ReadLines(mtlPath))
        {
            var line = raw.Trim();
            if (line.StartsWith("newmtl ", StringComparison.OrdinalIgnoreCase)) cur = line[7..].Trim();
            else if (line.StartsWith("map_Kd ", StringComparison.OrdinalIgnoreCase) && cur.Length > 0)
            {
                string img = line[7..].Trim().Trim('"');
                string full = Path.IsPathRooted(img) ? img : Path.Combine(dir, img);
                if (File.Exists(full)) outMap[cur] = full;
            }
        }
    }

    private static System.Drawing.Bitmap? LoadBitmap(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try { using var img = System.Drawing.Image.FromFile(path); return new System.Drawing.Bitmap(img); }
        catch { return null; }
    }

    private static bool InRange(int i, int n) => i >= 0 && i < n;
    private static bool TryF(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    private static string SanitizeMat(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
