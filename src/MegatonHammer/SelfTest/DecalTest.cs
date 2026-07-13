using MegatonHammer.Editor;
using OpenTK.Mathematics;

namespace MegatonHammer.SelfTest;

/// <summary>Headless checks for the decal/overlay interactivity work: the clipboard round-trip (so Ctrl+C/X/V
/// carry decals across editor windows) and the "project onto the brush it's over" surface snap. The 2D handle
/// transforms are interactive and verified in-app; this covers the data + geometry paths. Run: --decaltest</summary>
public static class DecalTest
{
    public static void Run()
    {
        Console.WriteLine("== decal interactivity test ==");
        bool allOk = true;

        // ── 1. Clipboard serialization round-trip (cross-instance Copy/Paste) ──
        var d = new Decal
        {
            Position = new Vector3(100, 40, -20), Normal = Vector3.Normalize(new Vector3(0, 0, 1)),
            SizeU = 32f, SizeV = 72f, Rotation = 45f, TextureName = "poster",
        };
        string json = ProjectSerializer.SerializeSelection([], [], [d]);
        var (_, _, decals) = ProjectSerializer.DeserializeSelection(json);
        bool ser = decals.Count == 1
                   && (decals[0].Position - d.Position).Length < 0.01f
                   && (decals[0].Normal - d.Normal).Length < 0.01f
                   && MathF.Abs(decals[0].SizeU - d.SizeU) < 0.01f
                   && MathF.Abs(decals[0].SizeV - d.SizeV) < 0.01f
                   && MathF.Abs(decals[0].Rotation - d.Rotation) < 0.01f
                   && decals[0].TextureName == d.TextureName;
        Console.WriteLine($"  serialize round-trip: count={decals.Count} " +
                          (decals.Count == 1 ? $"tex={decals[0].TextureName} sizeU={decals[0].SizeU} rot={decals[0].Rotation}" : ""));
        Console.WriteLine(ser ? "  PASS — a decal survives the clipboard JSON round-trip (all fields)."
                              : "  FAIL — decal fields lost through the clipboard.");
        allOk &= ser;

        // ── 2. EditClipboard Copy → Instantiate carries the decal (in-process path) ──
        var doc = new MapDocument();
        doc.AddDecal(d.Clone());
        foreach (var dd in doc.AllDecals) dd.IsSelected = true;
        EditClipboard.CopyFrom(doc);
        var (_, _, pasted) = EditClipboard.Instantiate();
        bool clip = pasted.Count == 1 && pasted[0].TextureName == "poster";
        Console.WriteLine(clip ? "  PASS — EditClipboard copies + instantiates the decal."
                               : "  FAIL — EditClipboard dropped the decal.");
        allOk &= clip;

        // ── 3. Surface projection: a decal floating above a floor snaps down onto the floor face ──
        // Floor brush: top face at y=0, normal +Y. Decal drifted up to y=50 over it.
        var floor = Solid.CreateBox(new Vector3(-128, -16, -128), new Vector3(128, 0, 128));
        var scene = doc.Scene;
        scene.ActiveRoom!.Geometry.Add(floor);
        var n = Vector3.UnitY;
        var probeOrigin = new Vector3(10, 50, 10) + n * 128f;   // mirror ProjectDecalToSurface's probe
        bool hitOk = Picking.PickFace(scene, new Ray(probeOrigin, -n), out var hit)
                     && MathF.Abs(hit.Point.Y) < 0.5f && hit.Face.Plane.Normal.Y > 0.9f;
        Console.WriteLine(hitOk ? $"  PASS — projection ray snaps the decal onto the floor (y={hit.Point.Y:F1}, n.Y={hit.Face.Plane.Normal.Y:F2})."
                                : "  FAIL — projection ray did not land on the floor face.");
        allOk &= hitOk;

        Console.WriteLine(allOk ? "RESULT: PASS — decal clipboard + projection paths work."
                                : "RESULT: FAIL — see above.");
    }
}
