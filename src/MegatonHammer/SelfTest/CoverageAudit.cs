using MegatonHammer.Editor;
using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>Whole-game actor model-coverage audit: for every actor id the database knows, does it
/// resolve a real 3D model? Categorises gaps. Run: MegatonHammer --coverage [oot|mm]</summary>
public static class CoverageAudit
{
    private static readonly string OotRom = Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
    private static readonly string MmRom  = Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).z64");

    public static void Run(string[] args)
    {
        string mode = args.Length >= 2 ? args[1].ToLowerInvariant() : "both";
        if (mode != "mm") One("OoT", OotRom, true);
        if (mode != "oot") One("MM", MmRom, false);
    }

    private static void One(string label, string romPath, bool oot)
    {
        if (!File.Exists(romPath)) { Console.WriteLine($"{label}: ROM missing"); return; }
        var rom = new RomImage(romPath);
        var db = ActorDatabase.Load(oot);
        var aot = ActorObjectTable.Build(mm: !oot);
        var rdb = ActorRenderDb.Load(oot);
        var resolver = new ActorModelResolver(rom);

        int total = 0, model = 0, hasObjNoModel = 0, noObj = 0;
        var noObjList = new List<(ushort id, string name)>();
        var objNoModelList = new List<(ushort id, string name)>();
        foreach (var info in db.All)
        {
            ushort id = info.Id;
            total++;
            var za = new ZActor { Number = id, Variable = 0 };
            var m = resolver.Resolve(za, adult: true);
            if (m != null && m.Tris.Count > 0) { model++; continue; }
            bool hasObj = aot.ObjectFor(id) != null || rdb.Resolve(id, 0) != null;
            if (hasObj) { hasObjNoModel++; objNoModelList.Add((id, info.Name)); }
            else { noObj++; noObjList.Add((id, info.Name)); }
        }
        Console.WriteLine($"\n==== {label}: {total} actors ====");
        Console.WriteLine($"  resolve a model:        {model}");
        Console.WriteLine($"  object but NO model:    {hasObjNoModel}");
        Console.WriteLine($"  no object mapping:      {noObj}");
        Console.WriteLine($"\n  -- NO OBJECT (would sprite/marker), first 60: --");
        foreach (var (id, name) in noObjList.Take(60)) Console.WriteLine($"    0x{id:X4} {name}");
        Console.WriteLine($"\n  -- OBJECT but NO MODEL (resolve fails), first 30: --");
        foreach (var (id, name) in objNoModelList.Take(30)) Console.WriteLine($"    0x{id:X4} {name}");
    }
}
