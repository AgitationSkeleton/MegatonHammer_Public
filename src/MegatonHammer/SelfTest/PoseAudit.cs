using MegatonHammer.Editor;
using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>--poseaudit [mm] : resolves EVERY actor id (var 0) and flags those that render a flex skeleton
/// at BIND POSE — i.e. a skeleton was read but NO idle animation/pose was applied. For a humanoid skeleton
/// (bones authored along +X, stood up only by the animation's rotations) that's a crumpled tangle. Fix by
/// pinning ObjectAnimOffset[object] (idle-anim offset) + SkelOverride if the object has several skeletons.
/// The gold standard (Link/Dark Link/Stalfos) auto-detects its idle anim, so it won't appear here.</summary>
public static class PoseAudit
{
    private static readonly string Oot = Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
    private static readonly string Mm  = Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).z64");

    public static void Run(string[] args)
    {
        bool mm = args.Length >= 2 && args[1].Equals("mm", System.StringComparison.OrdinalIgnoreCase);
        string path = mm ? Mm : Oot;
        if (!System.IO.File.Exists(path)) { System.Console.WriteLine($"[poseaudit] ROM not found: {path}"); return; }
        var rom = new RomImage(path);
        var resolver = new ActorModelResolver(rom);
        var names = ActorObjectTable.Build(mm);

        var crumpled = new System.Collections.Generic.List<(int id, string obj, int read, int posed)>();
        int skeletal = 0, posedOk = 0;
        for (int id = 0; id <= 0x1D7; id++)
        {
            int read0 = ObjectModelReader.SkeletonsRead, posed0 = ObjectModelReader.SkeletonsPosedWithAnim;
            var za = new ZActor { Number = (ushort)id, Variable = 0, XPos = 0, YPos = 0, ZPos = 0 };
            try { if (resolver.Resolve(za, adult: true) == null) continue; } catch { continue; }
            int readD = ObjectModelReader.SkeletonsRead - read0;
            int posedD = ObjectModelReader.SkeletonsPosedWithAnim - posed0;
            if (readD <= 0) continue;   // no skeleton (static DL / billboard) — not a pose case
            skeletal++;
            if (posedD >= readD) { posedOk++; continue; }
            crumpled.Add((id, names.ObjectFor(id) ?? "?", readD, posedD));
        }

        System.Console.WriteLine($"[poseaudit] {(mm ? "MM" : "OoT")}: {skeletal} skeletal actors, {posedOk} posed by an anim, {crumpled.Count} at BIND POSE (crumple risk):");
        foreach (var c in crumpled)
            System.Console.WriteLine($"  0x{c.id:X4} {c.obj,-24} skelRead={c.read} posed={c.posed}");
    }
}
