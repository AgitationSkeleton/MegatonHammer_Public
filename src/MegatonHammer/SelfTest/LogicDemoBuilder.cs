using MegatonHammer.Editor;
using OpenTK.Mathematics;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Generates a demonstration set of .mhproj projects, one per distinct "wired logic" configuration used
/// in OoT/MM (a setter actor sets a flag; reader actors read it and change state). Each demo is a single
/// simple room with the wired actors hand-placed at their real param/flag values, foldered by the scene
/// it's representative of, with a README.txt describing what it does, how it works, and where it's used.
/// Run: MegatonHammer --logicdemos [outDir]
/// </summary>
public static class LogicDemoBuilder
{
    private const float R = 360f, Wall = 30f, Height = 360f;   // a 720x720 room, 360 tall

    /// <summary>One placed actor in a demo (offsets from room centre; rot fields carry overloaded flag data).</summary>
    public sealed record A(string Name, int Id, int Params, float Dx = 0, float Dz = 0,
                           float Dy = 0, short Rx = 0, short Ry = 0, short Rz = 0, int IdFlags = 0);

    /// <summary>One demo: a folder (the representative scene), title, game, README text, and the wired actors.</summary>
    public sealed record Demo(string Folder, string Title, bool Mm, string Doc, A[] Actors);

    public static void BuildAll(string baseDir)
    {
        Directory.CreateDirectory(baseDir);
        int n = 0;
        foreach (var d in LogicDemos.All)
        {
            try { Build(baseDir, d); n++; }
            catch (Exception ex) { Console.WriteLine($"[logicdemos] {d.Title}: {ex.Message}"); }
        }
        try { MmSystemsDemoBuilder.BuildInto(baseDir); n += 2; }
        catch (Exception ex) { Console.WriteLine($"[logicdemos] MM systems demos: {ex.Message}"); }
        WriteIndex(baseDir);
        Console.WriteLine($"[logicdemos] wrote {n} demos under {baseDir}");
    }

    private static void Build(string baseDir, Demo d)
    {
        var doc = new MapDocument();
        doc.InitGameDefaults(d.Mm);
        var scene = doc.Scene;
        scene.Name = d.Title;
        var room = scene.Rooms[0];
        room.Name = d.Title;

        var (floor, wall, ceil) = TestTempleBuilder.ForestTextures(d.Mm);
        AddBox(room, (-R, -30, -R), (R, 0, R), floor);                 // floor
        AddBox(room, (-R, Height, -R), (R, Height + 30, R), ceil);     // ceiling
        AddBox(room, (-R, 0, -R), (-R + Wall, Height, R), wall);       // west
        AddBox(room, (R - Wall, 0, -R), (R, Height, R), wall);         // east
        AddBox(room, (-R, 0, -R), (R, Height, -R + Wall), wall);       // north
        AddBox(room, (-R, 0, R - Wall), (R, Height, R), wall);         // south

        foreach (var a in d.Actors)
            room.Actors.Add(new ZActor
            {
                Number = (ushort)a.Id, Variable = (ushort)a.Params,
                XPos = a.Dx, YPos = a.Dy, ZPos = a.Dz,
                XRot = a.Rx, YRot = a.Ry, ZRot = a.Rz, IdFlags = (ushort)a.IdFlags,
            });

        scene.Settings.AreaName = d.Title;
        scene.Settings.SpawnPos = new Vector3(0, 0, R - 80);
        scene.Settings.SpawnYaw = unchecked((short)0x8000);   // face -Z (into the room)

        string dir = Path.Combine(baseDir, Safe(d.Folder));
        Directory.CreateDirectory(dir);
        string name = Safe(d.Title);
        ProjectSerializer.Save(doc, Path.Combine(dir, name + ".mhproj"));
        File.WriteAllText(Path.Combine(dir, name + ".txt"), d.Doc.Replace("\n", Environment.NewLine));
    }

    private static void AddBox(ZRoom room, (float x, float y, float z) lo, (float x, float y, float z) hi, string tex)
    {
        var s = Solid.CreateBox(new Vector3(lo.x, lo.y, lo.z), new Vector3(hi.x, hi.y, hi.z));
        foreach (var f in s.Faces) f.TextureName = tex;
        room.Geometry.Add(s);
    }

    private static void WriteIndex(string baseDir)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("WIRED-LOGIC DEMO SET");
        sb.AppendLine("====================");
        sb.AppendLine();
        sb.AppendLine("Each subfolder (named for the scene it's representative of) holds one .mhproj demonstrating");
        sb.AppendLine("a single wired-logic configuration, plus a .txt describing what it does, how it works, and");
        sb.AppendLine("where it appears in the game. OoT/MM logic is a shared flag bus: a SETTER actor sets a flag,");
        sb.AppendLine("any READER actors with the same flag index change state. The flag index is the wire.");
        sb.AppendLine();
        foreach (var d in LogicDemos.All.OrderBy(x => x.Folder).ThenBy(x => x.Title))
            sb.AppendLine($"  [{(d.Mm ? "MM" : "OoT")}] {d.Folder,-26} / {d.Title}");
        File.WriteAllText(Path.Combine(baseDir, "INDEX.txt"), sb.ToString());
    }

    private static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_').ToArray()).Trim();
}
