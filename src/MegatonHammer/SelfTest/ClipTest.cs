using MegatonHammer.Editor;
using OpenTK.Mathematics;

namespace MegatonHammer.SelfTest;

/// <summary>#9: headless check that the clip/slice geometry pipeline (Solid.Split → valid halves)
/// works, isolating a real bug from a UI/UX problem. Run: MegatonHammer --testclip</summary>
public static class ClipTest
{
    public static void Run()
    {
        Console.WriteLine("== clip/slice pipeline test ==");
        var box = Solid.CreateBox(new Vector3(-64, -64, -64), new Vector3(64, 64, 64));
        Console.WriteLine($"box: {box.Faces.Count} faces, {box.GetUniqueVertices().Count} verts");

        // A vertical plane through the origin (x = 0): normal +X, distance 0 — slices the box in half.
        var cut = new Plane3D(new Vector3(1, 0, 0), 0f);
        var (front, back) = box.Split(cut);
        Console.WriteLine($"x=0 cut: front={(front == null ? "null" : front.Faces.Count + " faces")}, " +
                          $"back={(back == null ? "null" : back.Faces.Count + " faces")}");
        if (front != null) { var (mn, mx) = front.GetAABB(); Console.WriteLine($"  front AABB X {mn.X:F0}..{mx.X:F0}"); }
        if (back  != null) { var (mn, mx) = back.GetAABB();  Console.WriteLine($"  back  AABB X {mn.X:F0}..{mx.X:F0}"); }

        // A plane that misses the box (x = 500): one side is the whole box, the other empty.
        var miss = new Plane3D(new Vector3(1, 0, 0), 500f);
        var (mf, mb) = box.Split(miss);
        Console.WriteLine($"x=500 (outside) cut: front={(mf == null ? "null" : "solid")}, back={(mb == null ? "null" : "solid")}");

        bool ok = front != null && back != null
                  && MathF.Abs(front.GetAABB().max.X) < 1f && MathF.Abs(back.GetAABB().min.X) < 1f;
        Console.WriteLine(ok ? "RESULT: PASS — Split cuts a crossing plane into two valid halves."
                             : "RESULT: FAIL — Split did not produce two valid halves.");

        // The cut face must NOT go blank — it inherits the brush's texture (was: blank once sliced).
        var tex = Solid.CreateBox(new Vector3(-64, -64, -64), new Vector3(64, 64, 64));
        foreach (var f in tex.Faces) { f.TextureName = "wall"; f.TexScaleS = f.TexScaleT = 32f; }
        // A diagonal plane → one half is a wedge whose new slant face is the cut face.
        var diag = new Plane3D(Vector3.Normalize(new Vector3(1, 1, 0)), 0f);
        var (wf, wb) = tex.Split(diag);
        bool inherit = true;
        foreach (var half in new[] { wf, wb })
        {
            if (half == null) continue;
            foreach (var f in half.Faces)
                if (string.IsNullOrEmpty(f.TextureName)) { inherit = false; Console.WriteLine($"  BLANK face (plane {f.PlaneIndex}) after slice"); }
        }
        Console.WriteLine(inherit
            ? "RESULT: PASS — sliced halves keep a texture on every face (cut face inherited)."
            : "RESULT: FAIL — a sliced face went blank.");
    }
}
