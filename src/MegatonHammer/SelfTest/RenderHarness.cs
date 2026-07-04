using System.Text;
using MegatonHammer.Editor;
using MegatonHammer.Forms;
using MegatonHammer.Rom;
using MegatonHammer.Rendering;
using MegatonHammer.Textures;
using OpenTK.Mathematics;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Headless render + actor-display audit. Spins up an off-screen GLViewport (same GL pipeline the
/// editor's 3D view uses), then for the Test Temple project and an assortment of vanilla OoT/MM
/// scenes it: (1) writes isometric + top-down PNG renders, and (2) audits how every placed actor
/// displays — real 3D model, item-icon billboard, or obsolete marker — plus geometry texture
/// coverage. Writes the renders + a report under the output dir.
/// Run: MegatonHammer --renderlevel [outDir]
/// </summary>
public static class RenderHarness
{
    private const string OotRom = @"D:\Copilot_OOT\READ_ONLY_GameROMs\Legend of Zelda, The - Ocarina of Time (USA).z64";
    private const string MmRom  = @"D:\Copilot_OOT\READ_ONLY_GameROMs\Legend of Zelda, The - Majora's Mask (USA).z64";

    // Vanilla scenes to sample, picked by name substring so we don't hard-code fragile ids.
    private static readonly string[] OotPicks = ["Deku Tree", "Forest Temple", "Water Temple", "Jabu", "Hyrule Field", "Kokiri Forest"];
    private static readonly string[] MmPicks  = ["Clock Town", "Stone Tower", "Great Bay", "Stock Pot", "Woodfall", "Snowhead"];

    public static void Run(string[] args)
    {
        string outDir = args.Length >= 2 ? args[1] : @"D:\Copilot_OOT\WorkFolders\MegatonHammer\out\renders";
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        report.AppendLine("==================== RENDER + ACTOR-DISPLAY AUDIT ====================");

        // A hidden off-screen host form. Each scene gets a FRESH GLViewport (so its renderers'
        // ROM-specific caches — texture source, GL textures, item icons — never bleed between scenes
        // or games; that bug made MM scenes decode OoT textures, magenta-corrupting them).
        using var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000),
            Size = new Size(64, 64),
        };
        form.Show();
        Application.DoEvents();

        // ── Test Temple (brush-built demo dungeon) ──
        try
        {
            string tempDir = Path.Combine(outDir, "_testtemple_src");
            Directory.CreateDirectory(tempDir);
            TestTempleBuilder.Build(tempDir);
            string proj = Path.Combine(tempDir, "Test_Temple.mhproj");
            if (File.Exists(proj) && File.Exists(OotRom))
            {
                var rom = new RomImage(OotRom);
                var lib = new TextureLibrary();
                LoadTextures(rom, lib);
                var doc = new MapDocument();
                ProjectSerializer.Load(doc, proj);
                var resolver = new ActorModelResolver(rom);
                RenderJob(report, form, doc, lib, resolver, rom, "Test_Temple", outDir);
            }
            else report.AppendLine($"Test Temple: skipped (proj={File.Exists(proj)}, oot rom={File.Exists(OotRom)})");
        }
        catch (Exception ex) { report.AppendLine($"Test Temple: EXCEPTION {ex.Message}"); }

        // ── Vanilla assortment ──
        RenderVanillaPicks(report, form, "OoT", OotRom, OotPicks, outDir, isMm: false);
        RenderVanillaPicks(report, form, "MM",  MmRom,  MmPicks,  outDir, isMm: true);

        form.Hide();
        string reportPath = Path.Combine(outDir, "render-audit.txt");
        try { File.WriteAllText(reportPath, report.ToString()); Console.WriteLine(report.ToString()); }
        catch { Console.WriteLine(report.ToString()); }
        Console.WriteLine($"\nRenders + report under: {outDir}");
    }

    /// <summary>Renders EVERY scene/map in both games to levelout/{oot|mm}/ as iso + top-down PNGs,
    /// plus a per-scene geometry texture-coverage report (to flag gray/untextured walls).
    /// Run: MegatonHammer --renderlevels [outDir]</summary>
    public static void RenderAllLevels(string[] args)
    {
        string baseDir = args.Length >= 2 ? args[1] : @"D:\Copilot_OOT\WorkFolders\MegatonHammer\levelout";
        Directory.CreateDirectory(baseDir);
        var report = new StringBuilder();
        report.AppendLine("==================== ALL-LEVELS RENDER + TEXTURE-COVERAGE AUDIT ====================");

        using var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(64, 64),
        };
        form.Show(); Application.DoEvents();

        RenderAllScenesForGame(report, form, "OoT", OotRom, Path.Combine(baseDir, "oot"), isMm: false);
        RenderAllScenesForGame(report, form, "MM",  MmRom,  Path.Combine(baseDir, "mm"),  isMm: true);

        form.Hide();
        string reportPath = Path.Combine(baseDir, "levels-audit.txt");
        try { File.WriteAllText(reportPath, report.ToString()); } catch { }
        Console.WriteLine(report.ToString());
        Console.WriteLine($"\nLevel renders + report under: {baseDir}");
    }

    private static void RenderAllScenesForGame(StringBuilder report, Form form, string label, string romPath,
                                               string outDir, bool isMm)
    {
        if (!File.Exists(romPath)) { report.AppendLine($"{label}: ROM not found, skipped"); return; }
        Directory.CreateDirectory(outDir);
        RomImage rom;
        try { rom = new RomImage(romPath); } catch (Exception ex) { report.AppendLine($"{label}: {ex.Message}"); return; }

        var lib = new TextureLibrary();
        LoadTextures(rom, lib);

        IEnumerable<(int id, string name)> all = rom.Game == RomGame.MM
            ? MmSceneFiles.All
            : Enumerable.Range(0, 128).Where(OotSceneFiles.IsValid).Select(i => (i, OotSceneNames.Pretty(i)));

        report.AppendLine($"---------- {label} ----------");
        int ok = 0, fail = 0;
        foreach (var (id, sname) in all)
        {
            try
            {
                var level = ImportedLevel.Load(rom, id);
                if (level == null) { report.AppendLine($"{label} [{id:X2}] {sname}: import null (test/empty scene)"); fail++; continue; }
                var doc = new MapDocument();
                doc.Imported = level;
                var scene = doc.Scene;
                for (int i = scene.Rooms.Count; i < Math.Max(1, level.Scene.Rooms.Count); i++) scene.AddRoom();
                for (int i = 0; i < level.Scene.Rooms.Count; i++)
                    foreach (var a in level.Scene.Rooms[i].Actors)
                        scene.Rooms[i].Actors.Add(new ZActor
                        {
                            Number = a.Id, Variable = a.Params,
                            XPos = a.X, YPos = a.Y, ZPos = a.Z, XRot = a.RX, YRot = a.RY, ZRot = a.RZ,
                        });
                // Flag oversized actors — a mis-scaled model renders giant (user-reported: giant Ganondorf,
                // Poe bosses in Forest Temple, room-shape actors in Stone Tower). Log id + size so we can
                // spot bad scale resolutions without eyeballing every render. Threshold 700u: normal actors
                // are <~200u; bosses ~300-500u; anything past 700 is suspect (or a legit room/scenery actor).
                foreach (var room in scene.Rooms)
                    foreach (var a in room.Actors)
                        if (level.Resolver.ModelWorldBounds(a, true) is (var bmn, var bmx))
                        {
                            var sz = bmx - bmn;
                            float mx = MathF.Max(sz.X, MathF.Max(sz.Y, sz.Z));
                            if (mx > 700f)
                                report.AppendLine($"  HUGE {label} [{id:X2}] {sname}: actor 0x{a.Number:X4} v{a.Variable:X4} maxdim={mx:F0}u ({sz.X:F0}x{sz.Y:F0}x{sz.Z:F0})");
                        }
                string safe = new string(sname.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                RenderJob(report, form, doc, lib, level.Resolver, rom, $"{id:X2}_{safe}", outDir);
                ok++;
            }
            catch (Exception ex) { report.AppendLine($"{label} [{id:X2}] {sname}: EXCEPTION {ex.Message}"); fail++; }
        }
        report.AppendLine($"{label}: rendered {ok} scenes, {fail} skipped/failed -> {outDir}");
    }

    /// <summary>Diagnose WHY a scene's walls render untextured (gray). Loads scenes matching a name
    /// substring and dumps the per-reason tally of null-texture triangles.
    /// Run: MegatonHammer --graydiag [oot|mm] [nameSubstr]</summary>
    public static void GrayDiag(string[] args)
    {
        bool mm = args.Length >= 2 && args[1].Equals("mm", StringComparison.OrdinalIgnoreCase);
        string sub = args.Length >= 3 ? args[2] : (mm ? "Great Bay" : "Zora");
        string romPath = mm ? MmRom : OotRom;
        if (!File.Exists(romPath)) { Console.WriteLine("ROM not found"); return; }
        var rom = new RomImage(romPath);
        IEnumerable<(int id, string name)> all = rom.Game == RomGame.MM
            ? MmSceneFiles.All
            : Enumerable.Range(0, 128).Where(OotSceneFiles.IsValid).Select(i => (i, OotSceneNames.Pretty(i)));

        foreach (var (id, sname) in all.Where(t => t.name.Contains(sub, StringComparison.OrdinalIgnoreCase)))
        {
            RoomMeshReader.ResetDiag();
            RoomMeshReader.DiagTrace = args.Length >= 4 && args[3] == "trace";
            RoomMeshReader.DiagTraceLog.Clear();
            RoomMeshReader.DiagMeshTypes.Clear();
            var level = ImportedLevel.Load(rom, id);
            if (level == null) { Console.WriteLine($"[{id:X2}] {sname}: null"); continue; }
            int animTris = level.RoomMeshes.Sum(m => m.Count(t => t.AnimSeg != 0));
            if (level.SegScroll.Count > 0 || level.SegFlip.Count > 0 || level.SegColor.Count > 0 || animTris > 0)
                Console.WriteLine($"    [anim] scroll={{{string.Join(",", level.SegScroll.Select(kv => $"seg{kv.Key:X}"))}}} color={{{string.Join(",", level.SegColor.Select(kv => $"seg{kv.Key:X}:t{kv.Value.Type}/{kv.Value.Colors.Length}c"))}}} flip={{{string.Join(",", level.SegFlip.Select(kv => $"seg{kv.Key:X}"))}}} animTris={animTris}");
            int tot = RoomMeshReader.DiagTextured + RoomMeshReader.DiagNoTimg + RoomMeshReader.DiagSegUnresolved
                    + RoomMeshReader.DiagBadFormat + RoomMeshReader.DiagBadDims;
            Console.WriteLine($"\n[{id:X2}] {sname}: {tot} tris");
            Console.WriteLine($"    textured        = {RoomMeshReader.DiagTextured}");
            Console.WriteLine($"    null:no-SETTIMG  = {RoomMeshReader.DiagNoTimg}");
            Console.WriteLine($"    null:seg-unresolv= {RoomMeshReader.DiagSegUnresolved}  segs={string.Join(",", RoomMeshReader.DiagUnresolvedSegs.Select(kv => $"seg{kv.Key}:{kv.Value}"))}");
            Console.WriteLine($"    null:bad-format  = {RoomMeshReader.DiagBadFormat}");
            Console.WriteLine($"    null:bad-dims    = {RoomMeshReader.DiagBadDims}");
            Console.WriteLine($"    tile shiftS={RoomMeshReader.DiagShiftS} shiftT={RoomMeshReader.DiagShiftT} uls!=0:{RoomMeshReader.DiagUls} ult!=0:{RoomMeshReader.DiagUlt}");
            // Decode every distinct bound texture to see how many fall back to the magenta placeholder.
            RomTextureSource.ResetDecodeDiag();
            using (var ts = new RomTextureSource(rom))
            {
                var seen = new HashSet<long>();
                int shown = 0;
                foreach (var mesh in level.RoomMeshes)
                    foreach (var t in mesh)
                        if (t.Texture is { } ti)
                        {
                            long k = ((long)ti.FileIndex << 40) ^ ((long)ti.Offset << 12) ^ ((long)ti.Type * 977 + ti.Width * 31 + ti.Height);
                            if (!seen.Add(k)) continue;
                            int before = RomTextureSource.DiagDecodeOverrun;
                            ts.Decode(ti).Dispose();
                            if (RomTextureSource.DiagDecodeOverrun > before && shown++ < 12)
                                Console.WriteLine($"      OVERRUN file={ti.FileIndex} off=0x{ti.Offset:X} {ti.Type} {ti.Width}x{ti.Height} fileLen=0x{rom.GetFile(ti.FileIndex).Length:X}");
                            if (RoomMeshReader.DiagTrace)
                            {
                                bool ci = ti.Type is N64TexType.Palette4bpp or N64TexType.Palette8bpp;
                                using var tb = ts.Decode(ti);
                                // Mean saturation, to tell a coloured texture from a gray one.
                                double sat = 0; int n = 0;
                                for (int yy = 0; yy < tb.Height; yy += 4) for (int xx = 0; xx < tb.Width; xx += 4)
                                { var px = tb.GetPixel(xx, yy); int mx2 = Math.Max(px.R, Math.Max(px.G, px.B)), mn2 = Math.Min(px.R, Math.Min(px.G, px.B)); sat += mx2 == 0 ? 0 : (mx2 - mn2) / (double)mx2; n++; }
                                Console.WriteLine($"      TEX {ti.Type} {ti.Width}x{ti.Height} file={ti.FileIndex} off=0x{ti.Offset:X} sat={(n > 0 ? sat / n : 0):F2}" + (ci ? $" palFile={ti.PaletteFileIndex} palOff=0x{ti.PaletteOffset:X}" : ""));
                                if (shown < 6) { tb.Save($@"D:\Copilot_OOT\WorkFolders\MegatonHammer\levelout\tex_{id:X2}_{shown}.png"); }
                                shown++;
                            }
                        }
            }
            Console.WriteLine($"    decode ok={RomTextureSource.DiagDecodeOk} overrun(magenta)={RomTextureSource.DiagDecodeOverrun} exception(magenta)={RomTextureSource.DiagDecodeException}");
            if (RoomMeshReader.DiagTrace)
                for (int rm = 0; rm < RoomMeshReader.DiagMeshTypes.Count; rm++)
                    Console.WriteLine($"    room{rm}: {RoomMeshReader.DiagMeshTypes[rm]}");
            if (RoomMeshReader.DiagTrace)
                File.WriteAllLines($@"D:\Copilot_OOT\WorkFolders\MegatonHammer\levelout\trace_{id:X2}.txt", RoomMeshReader.DiagTraceLog);
        }
    }

    /// <summary>Scan all scenes for prerendered (type-1) rooms, decode each JFIF background and save
    /// it as a PNG to verify the decoder. Run: MegatonHammer --prerender [oot|mm] [outDir]</summary>
    public static void Prerender(string[] args)
    {
        bool mm = args.Length >= 2 && args[1].Equals("mm", StringComparison.OrdinalIgnoreCase);
        string outDir = args.Length >= 3 ? args[2] : @"D:\Copilot_OOT\WorkFolders\MegatonHammer\levelout\prerender";
        Directory.CreateDirectory(outDir);
        string romPath = mm ? MmRom : OotRom;
        if (!File.Exists(romPath)) { Console.WriteLine("ROM not found"); return; }
        var rom = new RomImage(romPath);
        IEnumerable<(int id, string name)> all = rom.Game == RomGame.MM
            ? MmSceneFiles.All
            : Enumerable.Range(0, 128).Where(OotSceneFiles.IsValid).Select(i => (i, OotSceneNames.Pretty(i)));

        int found = 0, decoded = 0;
        foreach (var (id, sname) in all)
        {
            var scene = SceneImporter.Import(rom, id);
            if (scene == null) continue;
            byte[] sceneFile = rom.GetFile(scene.SceneFileIndex);
            for (int r = 0; r < scene.Rooms.Count; r++)
            {
                var room = scene.Rooms[r];
                byte[] roomFile;
                try { roomFile = rom.GetFile(room.FileIndex); } catch { continue; }
                byte[] Seg(int s) => s == 2 ? sceneFile : s == 3 ? roomFile : [];
                var bgs = PrerenderBackground.FromRoom(roomFile, room.MeshHeaderOffset, Seg);
                foreach (var bg in bgs)
                {
                    found++;
                    string safe = new string(sname.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                    using var bmp = bg.Decode();
                    string status = bmp != null ? $"{bmp.Width}x{bmp.Height} OK" : "DECODE FAIL";
                    if (bmp != null) { bmp.Save(Path.Combine(outDir, $"{id:X2}_{safe}_r{r}.png")); decoded++; }
                    Console.WriteLine($"  [{id:X2}] {sname} room {r}: bg {bg.Width}x{bg.Height} jfif={bg.Jfif.Length}B -> {status}");
                }
                foreach (var c in scene.PrerenderCameras)
                {
                    string kind = c.Setting == 0x19 ? "FIXED" : c.Setting == 0x1A ? "PIVOT" : "SIDESCROLL";
                    Console.WriteLine($"      cam {kind}: pos=({c.PosX},{c.PosY},{c.PosZ}) rot=({c.RotX},{c.RotY},{c.RotZ})=yaw{c.RotY * 360.0 / 65536:F0} fov={c.Fov}");
                }
            }
        }
        Console.WriteLine($"\n{(mm ? "MM" : "OoT")}: {found} prerendered backgrounds, {decoded} decoded -> {outDir}");
    }

    /// <summary>Dump per-room actor lists + model-resolve status for scenes matching a name.
    /// Run: MegatonHammer --actordump [oot|mm] [nameSubstr] [optionalHexActorId]</summary>
    public static void ActorDump(string[] args)
    {
        bool mm = args.Length >= 2 && args[1].Equals("mm", StringComparison.OrdinalIgnoreCase);
        string sub = args.Length >= 3 ? args[2] : "Water Temple";
        bool verbose = args.Length >= 4 && args[3].Equals("all", StringComparison.OrdinalIgnoreCase);
        int onlyId = (args.Length >= 4 && !verbose) ? Convert.ToInt32(args[3], 16) : -1;
        string romPath = mm ? MmRom : OotRom;
        if (!File.Exists(romPath)) { Console.WriteLine("ROM not found"); return; }
        var rom = new RomImage(romPath);
        IEnumerable<(int id, string name)> all = rom.Game == RomGame.MM
            ? MmSceneFiles.All
            : Enumerable.Range(0, 128).Where(OotSceneFiles.IsValid).Select(i => (i, OotSceneNames.Pretty(i)));

        foreach (var (id, sname) in all.Where(t => t.name.Contains(sub, StringComparison.OrdinalIgnoreCase)))
        {
            var level = ImportedLevel.Load(rom, id);
            if (level == null) { Console.WriteLine($"[{id:X2}] {sname}: null"); continue; }
            Console.WriteLine($"\n[{id:X2}] {sname}: {level.Scene.Rooms.Count} rooms, {level.RoomMeshes.Count} meshes");
            for (int r = 0; r < level.Scene.Rooms.Count; r++)
            {
                var room = level.Scene.Rooms[r];
                int tris = r < level.RoomMeshes.Count ? level.RoomMeshes[r].Count : -1;
                // Geometry bounds, to spot rooms whose mesh is empty/duplicated/off-screen.
                var mn = new OpenTK.Mathematics.Vector3(1e9f); var mx = new OpenTK.Mathematics.Vector3(-1e9f);
                var sum = OpenTK.Mathematics.Vector3.Zero; int np = 0;
                if (r < level.RoomMeshes.Count)
                    foreach (var t in level.RoomMeshes[r])
                        foreach (var p in new[] { t.P0, t.P1, t.P2 })
                        { mn = OpenTK.Mathematics.Vector3.ComponentMin(mn, p); mx = OpenTK.Mathematics.Vector3.ComponentMax(mx, p); sum += p; np++; }
                var ctr = np > 0 ? sum / np : OpenTK.Mathematics.Vector3.Zero;
                string bounds = mn.X > mx.X ? "(no geo)" : $"({mn.X:F0},{mn.Y:F0},{mn.Z:F0})..({mx.X:F0},{mx.Y:F0},{mx.Z:F0}) ctr=({ctr.X:F0},{ctr.Y:F0},{ctr.Z:F0})";
                // Raw mesh-header DL pointers, to spot rooms reading a shared/wrong display list.
                string dls = "";
                try
                {
                    byte[] rf = rom.GetFile(room.FileIndex);
                    int mh = room.MeshHeaderOffset;
                    if (mh >= 0 && mh + 8 <= rf.Length && rf[mh] == 0)
                    {
                        uint listPtr = U32(rf, mh + 4); int lo = (int)(listPtr & 0xFFFFFF);
                        if (lo + 8 <= rf.Length)
                            dls = $" listPtr=0x{listPtr:X8} opa=0x{U32(rf, lo):X8} xlu=0x{U32(rf, lo + 4):X8}";
                    }
                }
                catch { }
                Console.WriteLine($"  Room {r}: {room.Actors.Count} actors, geoTris={tris}, file={room.FileIndex}, meshHdr=0x{room.MeshHeaderOffset:X} {bounds}{dls}");
                foreach (var a in room.Actors)
                {
                    if (onlyId >= 0 && a.Id != onlyId) continue;
                    var za = new ZActor { Number = a.Id, Variable = a.Params, XPos = a.X, YPos = a.Y, ZPos = a.Z };
                    bool model = level.Resolver.Resolve(za, true) != null;
                    if (verbose || onlyId >= 0 || !model)
                        Console.WriteLine($"      0x{a.Id:X4} var=0x{a.Params:X4} @({a.X},{a.Y},{a.Z}) model={(model ? "YES" : "billboard")}");
                }
            }
        }
    }

    private static uint U32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

    /// <summary>Imports an OBJ as room mesh geometry and renders it textured to a PNG, to verify the
    /// OBJ-import pipeline. Run: MegatonHammer --renderobj &lt;file.obj&gt; [out.png]</summary>
    public static void RenderObj(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: --renderobj <file.obj> [out.png]"); return; }
        string objPath = args[1];
        string outPath = args.Length >= 3 ? args[2] : @"D:\Copilot_OOT\WorkFolders\MegatonHammer\out\objimport.png";
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var mesh = Export.ObjIO.ImportMesh(objPath);
        Console.WriteLine($"Imported {mesh.Tris.Count} tris, {mesh.Materials.Count} materials ({mesh.Materials.Values.Count(b => b != null)} textured)");

        using var form = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(64, 64) };
        form.Show(); Application.DoEvents();
        var doc = new MapDocument();
        doc.Scene.Rooms[0].ObjMesh = mesh;
        var vp = new GLViewport(ViewportType.Perspective3D);
        form.Controls.Add(vp); Application.DoEvents();
        try
        {
            vp.Document = doc;
            var (mn, mx) = mesh.Bounds();
            var center = (mn + mx) * 0.5f;
            float radius = MathF.Max(40f, (mx - mn).Length * 0.5f);
            var img = vp.RenderToImage(Cam(center, radius, -45f, -30f, 40f), 1000, 1000, showActors: false, Color.FromArgb(20, 20, 28));
            if (img != null) { img.Save(outPath); img.Dispose(); Console.WriteLine($"wrote {outPath}"); }
        }
        finally { form.Controls.Remove(vp); vp.Dispose(); form.Hide(); }
    }

    /// <summary>Dump the G_VTX loads (segment/offset/first vertex) for one room's mesh, to diagnose
    /// misplaced geometry. Run: MegatonHammer --roomdl [oot|mm] [sceneHex] [room]</summary>
    public static void RoomDl(string[] args)
    {
        bool mm = args.Length >= 2 && args[1].Equals("mm", StringComparison.OrdinalIgnoreCase);
        int sceneId = args.Length >= 3 ? Convert.ToInt32(args[2], 16) : 2;
        int roomIdx = args.Length >= 4 ? int.Parse(args[3]) : 0;
        var rom = new RomImage(mm ? MmRom : OotRom);
        var scene = SceneImporter.Import(rom, sceneId);
        if (scene == null || roomIdx >= scene.Rooms.Count) { Console.WriteLine("bad scene/room"); return; }
        var objects = ObjectTable.Build(rom);
        int keep4 = -1, keep5 = -1;
        RoomMeshReader.DiagTrace = true; RoomMeshReader.DiagVtxLog.Clear();
        var tris = RoomMeshReader.Read(rom, scene.SceneFileIndex, scene.Rooms[roomIdx], keep4, keep5, -1);
        Console.WriteLine($"Scene {sceneId:X2} room {roomIdx}: {tris.Count} tris, {RoomMeshReader.DiagVtxLog.Count} VTX loads");
        foreach (var l in RoomMeshReader.DiagVtxLog.Take(60)) Console.WriteLine("  " + l);
        RoomMeshReader.DiagTrace = false;
    }

    private static void RenderVanillaPicks(StringBuilder report, Form form, string label, string romPath,
                                           string[] picks, string outDir, bool isMm)
    {
        if (!File.Exists(romPath)) { report.AppendLine($"{label}: ROM not found, skipped"); return; }
        RomImage rom;
        try { rom = new RomImage(romPath); } catch (Exception ex) { report.AppendLine($"{label}: {ex.Message}"); return; }

        var lib = new TextureLibrary();
        LoadTextures(rom, lib);

        IEnumerable<(int id, string name)> all = rom.Game == RomGame.MM
            ? MmSceneFiles.All
            : Enumerable.Range(0, 128).Where(OotSceneFiles.IsValid).Select(i => (i, OotSceneNames.Pretty(i)));

        foreach (var pick in picks)
        {
            var match = all.FirstOrDefault(t => t.name.Contains(pick, StringComparison.OrdinalIgnoreCase));
            if (match.name == null) { report.AppendLine($"{label}: '{pick}' not found in scene table"); continue; }
            try
            {
                var level = ImportedLevel.Load(rom, match.id);
                if (level == null) { report.AppendLine($"{label} [{match.id:X2}] {match.name}: import returned null (test scene?)"); continue; }
                var doc = new MapDocument();
                doc.Imported = level;
                // Mirror the placed actors into the document so they render + audit.
                var scene = doc.Scene;
                for (int i = scene.Rooms.Count; i < Math.Max(1, level.Scene.Rooms.Count); i++) scene.AddRoom();
                for (int i = 0; i < level.Scene.Rooms.Count; i++)
                    foreach (var a in level.Scene.Rooms[i].Actors)
                        scene.Rooms[i].Actors.Add(new ZActor
                        {
                            Number = a.Id, Variable = a.Params,
                            XPos = a.X, YPos = a.Y, ZPos = a.Z, XRot = a.RX, YRot = a.RY, ZRot = a.RZ,
                        });
                string safe = new string(match.name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                RenderJob(report, form, doc, lib, level.Resolver, rom, $"{label}_{match.id:X2}_{safe}", outDir);
            }
            catch (Exception ex) { report.AppendLine($"{label} '{pick}': EXCEPTION {ex.Message}"); }
        }
    }

    // Renders one document to iso + top PNGs and appends a display audit for it. A FRESH GLViewport
    // is created per call so its renderers' ROM-specific caches start clean (no cross-scene/-game
    // texture bleed).
    private static void RenderJob(StringBuilder report, Form form, MapDocument doc, TextureLibrary lib,
                                  ActorModelResolver resolver, RomImage rom, string name, string outDir)
    {
        var vp = new GLViewport(ViewportType.Perspective3D);
        form.Controls.Add(vp);   // adding to the shown form creates the handle → GL context + renderers
        Application.DoEvents();
        try
        {
            RenderJobInner(report, vp, doc, lib, resolver, rom, name, outDir);
        }
        finally
        {
            form.Controls.Remove(vp);
            vp.Dispose();
        }
    }

    private static void RenderJobInner(StringBuilder report, GLViewport vp, MapDocument doc, TextureLibrary lib,
                                       ActorModelResolver resolver, RomImage rom, string name, string outDir)
    {
        vp.Document = doc;
        vp.Textures = lib;
        vp.FallbackResolver = resolver;
        vp.FallbackRom = rom;

        // Framing bounds. Drive the frame off the LEVEL GEOMETRY (brushes + imported room meshes); the
        // camera then sizes to the playable space. Actors are included only when they sit within a margin
        // of that geometry, so a far sky-actor (e.g. the Termina-field Moon hovering at y≈11700) doesn't
        // blow the frame out and shrink the whole level to a dot. When a scene has no geometry at all, fall
        // back to framing on the actors themselves.
        Vector3 gmn = new(1e9f), gmx = new(-1e9f);
        void IncGeo(Vector3 p) { gmn = Vector3.ComponentMin(gmn, p); gmx = Vector3.ComponentMax(gmx, p); }
        foreach (var s in doc.Solids) { var (a, b) = s.GetAABB(); IncGeo(a); IncGeo(b); }
        if (doc.Imported != null) foreach (var m in doc.Imported.RoomMeshes) foreach (var t in m) { IncGeo(t.P0); IncGeo(t.P1); IncGeo(t.P2); }

        Vector3 mn = new(1e9f), mx = new(-1e9f);
        void Inc(Vector3 p) { mn = Vector3.ComponentMin(mn, p); mx = Vector3.ComponentMax(mx, p); }
        bool hasGeo = gmn.X <= gmx.X;
        if (hasGeo)
        {
            Inc(gmn); Inc(gmx);
            // Only count actors near the geometry (within 1.5x its extent of its centre) so distant
            // sky/cutscene actors don't dominate the framing.
            var gc = (gmn + gmx) * 0.5f; var gext = (gmx - gmn) * 0.5f;
            float allow = MathF.Max(gext.Length * 1.5f, 600f);
            foreach (var act in doc.AllActors)
            {
                var p = new Vector3(act.XPos, act.YPos, act.ZPos);
                if ((p - gc).Length <= allow) Inc(p);
            }
        }
        else
            foreach (var act in doc.AllActors) Inc(new Vector3(act.XPos, act.YPos, act.ZPos));
        if (mn.X > mx.X) { report.AppendLine($"{name}: nothing to render"); return; }

        var center = (mn + mx) * 0.5f;
        float radius = MathF.Max(1f, (mx - mn).Length * 0.5f);
        var bg = Color.FromArgb(20, 20, 24);

        // Prerendered rooms (Market, ToT exterior, shops, houses) are authored for a specific fixed
        // camera, so render them from the game's own camera(s) — a single shot for FIXED cameras and
        // four cardinal shots for PIVOT cameras (which spin 360° in-game, e.g. Link's House) — instead
        // of the generic iso/top angles, which don't match the backdrop.
        var cams = doc.Imported?.Scene.PrerenderCameras;
        if (cams is { Count: > 0 })
        {
            float far = radius * 4f + 2000f;
            int shot = 0;
            foreach (var c in cams)
            {
                var camPos = new Vector3(c.PosX, c.PosY, c.PosZ);
                if (c.IsPivot)
                {
                    // A PIVOT camera spins 360° in-game (Market, Link's House). Its position is the
                    // room centre, so orbit four cardinal viewpoints around the centroid looking in.
                    string[] dir = ["N", "E", "S", "W"];
                    float back = radius * 1.4f;
                    var dirs = new[] { new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(-1, 0, 0) };
                    for (int q = 0; q < 4; q++)
                    {
                        var eye = center + dirs[q] * back + new Vector3(0, radius * 0.3f, 0);
                        var cam = LookAtCam(eye, center, c.Fov, far);
                        var img = vp.RenderToImage(cam, 1600, 1200, showActors: true, bg);
                        if (img != null) { img.Save(Path.Combine(outDir, $"{name}_pivot{shot}_{dir[q]}.png")); img.Dispose(); }
                    }
                }
                else
                {
                    // FIXED camera: render from the game's exact position, aimed at the room centroid
                    // (robust against the rotation-angle convention) with the game's FOV.
                    var cam = LookAtCam(camPos, center, c.Fov, far);
                    var img = vp.RenderToImage(cam, 1600, 1200, showActors: true, bg);
                    if (img != null) { img.Save(Path.Combine(outDir, $"{name}_cam{shot}.png")); img.Dispose(); }
                }
                shot++;
            }
        }
        else
        {
            var iso = vp.RenderToImage(Cam(center, radius, -45f, -32f, 34f), 1600, 1600, showActors: true, bg);
            var top = vp.RenderToImage(Cam(center, radius, -90f, -89f, 38f), 1600, 1600, showActors: true, bg);
            if (iso != null) { iso.Save(Path.Combine(outDir, $"{name}_iso.png")); iso.Dispose(); }
            if (top != null) { top.Save(Path.Combine(outDir, $"{name}_top.png")); top.Dispose(); }
        }

        // ── Actor display audit ──
        int model = 0, sprite = 0, obsolete = 0, flatSprite = 0;
        var iconSrc = new ItemIconSource(rom);
        foreach (var a in doc.AllActors)
        {
            if (a.IsObsolete) { obsolete++; continue; }
            if (resolver.Resolve(a, adult: true) != null) { model++; continue; }
            // No model → a billboard either way: item-icon (OoT) or a flat colour quad (MM).
            if (iconSrc.Available) sprite++;
            else flatSprite++;
        }
        // Geometry texture coverage.
        int tris = 0, texd = 0;
        if (doc.Imported != null)
            foreach (var m in doc.Imported.RoomMeshes) foreach (var t in m) { tris++; if (t.Texture != null) texd++; }
        foreach (var s in doc.Solids) tris += 0;   // brush faces are textured via the library by name

        int acts = doc.AllActors.Count();
        string render = cams is { Count: > 0 } ? $"prerender×{cams.Count}" : "iso+top";
        report.AppendLine($"{name,-28} {render,-12} actors={acts,4} (model={model} sprite={sprite} obsolete={obsolete} flatSprite={flatSprite})"
            + (tris > 0 ? $"  geoTris={tris} textured={100 * texd / Math.Max(1, tris)}%" : ""));
    }

    /// <summary>Renders a close-up of specific actors on a floor so their models can be eyeballed.
    /// Run: MegatonHammer --renderactors [outDir]</summary>
    public static void RenderActors(string[] args)
    {
        bool mm = args.Any(a => a.Equals("mm", StringComparison.OrdinalIgnoreCase));
        string outDir = args.Length >= 2 && !args[1].Equals("mm", StringComparison.OrdinalIgnoreCase)
            ? args[1] : (mm ? @"D:\Copilot_OOT\WorkFolders\MegatonHammer\out\actorcheck_mm"
                            : @"D:\Copilot_OOT\WorkFolders\MegatonHammer\out\actorcheck");
        Directory.CreateDirectory(outDir);
        string romPath = mm ? MmRom : OotRom;
        if (!File.Exists(romPath)) { Console.WriteLine($"[renderactors] {(mm ? "MM" : "OoT")} ROM not found"); return; }

        using var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(64, 64),
        };
        form.Show(); Application.DoEvents();

        var rom = new RomImage(romPath);
        var lib = new TextureLibrary(); LoadTextures(rom, lib);
        int sceneKeep = 0;

        // MM actor-model checks (variable-form dungeon objects).
        var mmPicks = new (ushort id, ushort var, string label)[]
        {
            (0x0093, 0x0000, "MM Switch Floor"),
            (0x0093, 0x0001, "MM Switch Rusty"),
            (0x0093, 0x0002, "MM Switch Eye Gold"),
            (0x0093, 0x0012, "MM Switch Eye Silver"),
            (0x0093, 0x0003, "MM Switch Crystal"),
            (0x0093, 0x0005, "MM Switch Floor Large"),
            (0x0007, 0x0000, "Gekko"),
            (0x00AF, 0x0000, "MM Owl"),
            (0x009F, 0x0000, "MM Gerudo Ge1"),
        };

        // (id, var, label) — the actors under review plus a couple of known-good references.
        var picks = mm ? mmPicks : new (ushort id, ushort var, string label)[]
        {
            (0x0002, 0x0000, "Stalfos (ref)"),
            (0x00B6, 0x00FF, "Flobbery Muscle Block"),
            (0x0018, 0x0006, "Recovery Fairy"),
            (0x00DD, 0x0000, "Like-Like"),
            (0x00DF, 0x0001, "Jabu Tentacle EnBx"),
            (0x0000, 0x0FFF, "Link spawn"),
            (0x0033, 0x0000, "Dark Link"),
            (0x000A, 0x0000, "Chest"),
            (0x0028, 0x0000, "Queen Gohma"),
            (0x0009, 0x0000, "Door"),
            (0x002E, 0x0000, "Door_Shutter"),
            (0x0136, 0x0000, "En_Blkobj room"),
            (0x0095, 0x2000, "Gold Skulltula"),
            (0x0095, 0x0000, "Skullwalltula ref"),
            (0x0014, 0x0000, "Epona"),
            (0x000F, 0x0000, "Cobweb Floor"),
            (0x000F, 0x1000, "Cobweb Wall"),
            (0x012A, 0x0000, "Switch Floor"),
            (0x012A, 0x0001, "Switch Floor Rusty"),
            (0x012A, 0x0003, "Switch Crystal Core"),
            (0x012A, 0x0013, "Switch Crystal Diamond"),
            (0x012A, 0x0012, "Switch Eye Silver"),
            (0x014E, 0x0000, "Rock Small"),
            (0x014E, 0x0001, "Rock Large Silver"),
            (0x00CF, 0x0000, "Hidan Cracked Floor"),
            (0x00CF, 0x0001, "Hidan Bombable Wall"),
            (0x00CF, 0x0002, "Hidan Large Wall"),
            (0x00C8, 0x0000, "Jabu Spike Platform"),
            (0x00C8, 0x0001, "Jabu Elevator"),
            (0x00C8, 0x0002, "Jabu Water"),
            (0x012D, 0x0000, "Hookshot Post"),
            (0x012D, 0x0002, "Hookshot Target"),
            (0x01BA, 0x0000, "Water Temple Bwall 0"),
            (0x01BA, 0x0002, "Water Temple Bwall 2"),
            (0x00B8, 0x0000, "Gerudo Bridge"),
            (0x00B8, 0x0003, "Carpenters Tent"),
            (0x00AE, 0x0000, "Haka Botw FakeWalls"),
            (0x00AE, 0x0003, "Haka Shadow Fake0"),
            (0x00E6, 0x0000, "BdanSwitch Blue"),
            (0x00E6, 0x0002, "BdanSwitch Yellow"),
            (0x00E6, 0x0004, "BdanSwitch Tall Yellow"),
            (0x014D, 0x0000, "Kaepora Gaebora"),
            (0x001B, 0x0000, "Tektite"),
            (0x0035, 0x0000, "En_Tp"),
            (0x00C4, 0x0000, "Morpha"),
            // Missing-texture audit (user-reported 2026-07): mostly NPCs.
            (0x01A4, 0x0000, "En_Guest"),
            (0x01B9, 0x0000, "En_Gs GossipStone"),
            (0x01AE, 0x0000, "En_Go2 Goron"),
            (0x01AF, 0x0000, "En_Wf Wolfos"),
            (0x0146, 0x0000, "En_Sa Saria"),
            (0x01D4, 0x0000, "En_Mm2 RunningMan"),
            (0x0164, 0x0000, "En_Kz KingZora"),
            (0x0179, 0x0000, "En_Zl3 Zelda"),
            (0x0084, 0x0000, "En_Ta Talon"),
            (0x0085, 0x0000, "En_Tk Dampe"),
            (0x0098, 0x0000, "En_Du Darunia"),
            (0x0093, 0x0000, "Bg_Po_Event block"),
            (0x0093, 0x0200, "Bg_Po_Event paint2"),
            (0x003D, 0x0000, "En_Ossan Kokiri"),
            (0x004F, 0x0000, "En_OE2 Navi Spot"),
            (0x00C3, 0x0000, "En_Nb Nabooru"),
        };

        var vp = new GLViewport(ViewportType.Perspective3D);
        form.Controls.Add(vp); Application.DoEvents();
        try
        {
            var resolver = new ActorModelResolver(rom, sceneKeep);
            for (int i = 0; i < picks.Length; i++)
            {
                var doc = new MapDocument { ShowSpawnMarker = false };   // hide the dummy-Link spawn marker so it can't overlap the actor at origin
                var room = doc.Scene.Rooms[0];
                // A small floor slab so the actor has ground context.
                AddFloor(room, "");
                room.Actors.Add(new ZActor { Number = picks[i].id, Variable = picks[i].var, XPos = 0, YPos = 0, ZPos = 0 });

                vp.Document = doc; vp.Textures = lib; vp.FallbackResolver = resolver; vp.FallbackRom = rom;

                // Frame on the actor's model bounds (fall back to a default box).
                MegatonHammer.Rom.ObjectModelReader.ResetTexDiag();
                var (mn, mx) = resolver.ModelWorldBounds(room.Actors[0], true)
                    ?? (new Vector3(-40, 0, -40), new Vector3(40, 80, 40));
                var center = (mn + mx) * 0.5f;
                float radius = MathF.Max(40f, (mx - mn).Length * 0.5f);
                var img = vp.RenderToImage(Cam(center, radius, -50f, -18f, 38f), 700, 700, showActors: true, Color.FromArgb(28, 28, 34));
                string safe = new string(picks[i].label.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                if (img != null) { img.Save(Path.Combine(outDir, $"actor_{i}_{safe}.png")); img.Dispose(); }
                bool resolved = resolver.Resolve(room.Actors[0], true) != null;
                var d = MegatonHammer.Rom.ObjectModelReader.DiagTimgSegs;
                int texT = MegatonHammer.Rom.ObjectModelReader.DiagTexTris, noTexT = MegatonHammer.Rom.ObjectModelReader.DiagNoTexTris;
                string segs = d.Count == 0 ? "-" : string.Join(",", d.OrderBy(kv => kv.Key).Select(kv => $"seg{kv.Key}:{kv.Value}"));
                string why = noTexT == 0 ? "" : $" null[noimg={MegatonHammer.Rom.ObjectModelReader.DiagNullNoTex} fmt={MegatonHammer.Rom.ObjectModelReader.DiagNullFmt} dims={MegatonHammer.Rom.ObjectModelReader.DiagNullDims}]";
                Console.WriteLine($"[renderactors] {picks[i].label,-16} id=0x{picks[i].id:X4} model={(resolved ? "YES" : "no (billboard)")} tris(tex/notex)={texT}/{noTexT}{why} timgSegs={{{segs}}}");
            }
        }
        finally { form.Controls.Remove(vp); vp.Dispose(); }
        form.Hide();
        Console.WriteLine($"[renderactors] PNGs under {outDir}");
    }

    /// <summary>--rendervariants [mm] : renders EVERY variant of every variable actor (VariantAudit tables)
    /// to out/variants/{game}/{label}_v{var}.png — the visual companion to --variantaudit, so each of an
    /// actor's per-params models (Kokiri boy vs girl, each townsperson, each shopkeeper, each dungeon object)
    /// can be eyeballed.</summary>
    public static void RenderVariants(string[] args)
    {
        bool mm = args.Any(a => a.Equals("mm", StringComparison.OrdinalIgnoreCase));
        string outDir = mm ? @"D:\Copilot_OOT\WorkFolders\MegatonHammer\out\variants\mm"
                           : @"D:\Copilot_OOT\WorkFolders\MegatonHammer\out\variants\oot";
        Directory.CreateDirectory(outDir);
        string romPath = mm ? MmRom : OotRom;
        if (!File.Exists(romPath)) { Console.WriteLine($"[rendervariants] ROM not found"); return; }

        using var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(64, 64),
        };
        form.Show(); Application.DoEvents();
        var rom = new RomImage(romPath);
        var lib = new TextureLibrary(); LoadTextures(rom, lib);
        var sets = mm ? VariantAudit.MmSets : VariantAudit.OotSets;

        var vp = new GLViewport(ViewportType.Perspective3D);
        form.Controls.Add(vp); Application.DoEvents();
        int n = 0;
        try
        {
            var resolver = new ActorModelResolver(rom, 0);
            foreach (var (id, label, vars) in sets)
                foreach (ushort v in vars)
                {
                    var doc = new MapDocument { ShowSpawnMarker = false };
                    var room = doc.Scene.Rooms[0];
                    AddFloor(room, "");
                    room.Actors.Add(new ZActor { Number = id, Variable = v, XPos = 0, YPos = 0, ZPos = 0 });
                    vp.Document = doc; vp.Textures = lib; vp.FallbackResolver = resolver; vp.FallbackRom = rom;
                    var (mn, mx) = resolver.ModelWorldBounds(room.Actors[0], true)
                        ?? (new Vector3(-40, 0, -40), new Vector3(40, 80, 40));
                    var center = (mn + mx) * 0.5f;
                    float radius = MathF.Max(40f, (mx - mn).Length * 0.5f);
                    var img = vp.RenderToImage(Cam(center, radius, -50f, -18f, 38f), 512, 512, showActors: true, Color.FromArgb(28, 28, 34));
                    string safe = new string(label.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                    if (img != null) { img.Save(Path.Combine(outDir, $"{id:X4}_{safe}_v{v:X4}.png")); img.Dispose(); n++; }
                }
        }
        finally { form.Controls.Remove(vp); vp.Dispose(); }
        form.Hide();
        Console.WriteLine($"[rendervariants] {n} variant PNGs under {outDir}");
    }

    /// <summary>Exports the brush Test Temple as MM with a scroll authored on its floor texture and verifies
    /// the binary: the scene gets a 0x1A AnimatedMaterial command + a valid tex-scroll entry, and the room
    /// DL gets a gsSPDisplayList(0x08000000) after the floor's texture load. Run: --testanimexport</summary>
    public static void TestAnimExport(string[] args)
    {
        if (!File.Exists(OotRom)) { Console.WriteLine("[testanimexport] ROM not found"); return; }
        var rom = new RomImage(OotRom);
        var lib = new TextureLibrary(); LoadTextures(rom, lib);
        string tempDir = Path.Combine(Path.GetTempPath(), "mh_animexport"); Directory.CreateDirectory(tempDir);
        TestTempleBuilder.Build(tempDir);
        var doc = new MapDocument();
        ProjectSerializer.Load(doc, Path.Combine(tempDir, "Test_Temple.mhproj"));
        doc.Scene.Settings.TextureScrolls.Add(new TextureScroll("rom_1131_007548", 0f, 0.5f));
        Func<string, System.Drawing.Bitmap?> texResolver = n => { try { return lib.Find(n)?.Image; } catch { return null; } };
        var (sceneBytes, rooms) = Export.SceneExporter.BuildBinaries(doc.Scene, texResolver, _ => null, n64Hw: false, mm: true);

        uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
        // Find the 0x1A command in the scene header (commands are 8 bytes until 0x14).
        int animOff = -1;
        for (int p = 0; p + 8 <= sceneBytes.Length; p += 8)
        {
            if (sceneBytes[p] == 0x14) break;
            if (sceneBytes[p] == 0x1A) { animOff = (int)(U32(sceneBytes, p + 4) & 0xFFFFFF); break; }
        }
        Console.WriteLine($"[testanimexport] scene 0x1A cmd: {(animOff >= 0 ? $"present → list @0x{animOff:X}" : "MISSING")}");
        if (animOff >= 0 && animOff + 12 <= sceneBytes.Length)
        {
            sbyte seg = (sbyte)sceneBytes[animOff];
            int type = (sceneBytes[animOff + 2] << 8) | sceneBytes[animOff + 3];
            int pOff = (int)(U32(sceneBytes, animOff + 4) & 0xFFFFFF);
            Console.WriteLine($"  entry0: segment={seg} (CPU seg 0x{Math.Abs(seg) + 7:X}) type={type} params@0x{pOff:X}");
            if (pOff + 4 <= sceneBytes.Length)
                Console.WriteLine($"  params: xStep={(sbyte)sceneBytes[pOff]} yStep={(sbyte)sceneBytes[pOff + 1]} w={sceneBytes[pOff + 2]} h={sceneBytes[pOff + 3]}");
        }
        // Scan room DLs for gsSPDisplayList to segment 8 (0xDE000000 0x08000000).
        int found = 0;
        foreach (var rm in rooms)
            for (int p = 0; p + 8 <= rm.Length; p += 8)
                if (rm[p] == 0xDE && rm[p + 4] == 0x08 && U32(rm, p + 4) == 0x08000000) found++;
        Console.WriteLine($"[testanimexport] room DL gsSPDisplayList(0x08000000) count = {found}");
    }

    /// <summary>Renders the brush-built Test Temple with a scroll authored on its floor texture, at two
    /// animation times, to verify brush-authored texture animation in the 3D view. Run: --renderbrushanim</summary>
    public static void RenderBrushAnim(string[] args)
    {
        string outDir = @"D:\Copilot_OOT\WorkFolders\MegatonHammer\out\animcheck";
        Directory.CreateDirectory(outDir);
        if (!File.Exists(OotRom)) { Console.WriteLine("[renderbrushanim] OoT ROM not found"); return; }
        using var form = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(64, 64) };
        form.Show(); Application.DoEvents();
        var rom = new RomImage(OotRom);
        var lib = new TextureLibrary(); LoadTextures(rom, lib);
        string tempDir = Path.Combine(outDir, "_brush_src"); Directory.CreateDirectory(tempDir);
        TestTempleBuilder.Build(tempDir);
        var doc = new MapDocument();
        ProjectSerializer.Load(doc, Path.Combine(tempDir, "Test_Temple.mhproj"));
        // Author a fast scroll on the floor texture so the motion is obvious between the two frames.
        doc.Scene.Settings.TextureScrolls.Add(new TextureScroll("rom_1131_007548", 0f, 0.5f));
        var vp = new GLViewport(ViewportType.Perspective3D);
        form.Controls.Add(vp); Application.DoEvents();
        try
        {
            vp.Document = doc; vp.Textures = lib; vp.FallbackRom = rom;
            Vector3 mn = new(1e9f), mx = new(-1e9f);
            foreach (var s in doc.Solids) { var (a, b) = s.GetAABB(); mn = Vector3.ComponentMin(mn, a); mx = Vector3.ComponentMax(mx, b); }
            var center = (mn + mx) * 0.5f; float radius = MathF.Max(100f, (mx - mn).Length * 0.5f);
            foreach (var t in new[] { 0f, 0.6f })
            {
                vp.ForcedAnimTime = t;
                var img = vp.RenderToImage(Cam(center, radius, -90f, -88f, 40f), 900, 900, showActors: false, Color.FromArgb(20, 20, 28));
                if (img != null) { img.Save(Path.Combine(outDir, $"brushanim_t{(int)(t * 10)}.png")); img.Dispose(); }
            }
        }
        finally { form.Controls.Remove(vp); vp.Dispose(); }
        form.Hide();
        Console.WriteLine($"[renderbrushanim] done -> {outDir}");
    }

    /// <summary>Renders an animated scene at two animation times so the texture scroll can be eyeballed
    /// (the water/lava UVs should shift between frames). Run: MegatonHammer --renderanim [oot|mm] [sceneIdHex]</summary>
    public static void RenderAnim(string[] args)
    {
        bool mm = args.Length >= 2 && args[1].Equals("mm", StringComparison.OrdinalIgnoreCase);
        int sceneId = args.Length >= 3 ? Convert.ToInt32(args[2], 16) : (mm ? 0x37 : 0x44);   // default MM Great Bay / OoT Chamber of Sages
        string romPath = mm ? MmRom : OotRom;
        string outDir = @"D:\Copilot_OOT\WorkFolders\MegatonHammer\out\animcheck";
        Directory.CreateDirectory(outDir);
        if (!File.Exists(romPath)) { Console.WriteLine("[renderanim] ROM not found"); return; }
        using var form = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(64, 64) };
        form.Show(); Application.DoEvents();
        var rom = new RomImage(romPath);
        var lib = new TextureLibrary(); LoadTextures(rom, lib);
        var level = ImportedLevel.Load(rom, sceneId);
        if (level == null) { Console.WriteLine("[renderanim] import null"); return; }
        Console.WriteLine($"[renderanim] scene {sceneId:X2}: segScroll={level.SegScroll.Count} animTris={level.RoomMeshes.Sum(m => m.Count(t => t.AnimSeg != 0))}");
        var doc = new MapDocument { Imported = level };
        var vp = new GLViewport(ViewportType.Perspective3D);
        form.Controls.Add(vp); Application.DoEvents();
        try
        {
            vp.Document = doc; vp.Textures = lib; vp.FallbackRom = rom;
            Vector3 mn = new(1e9f), mx = new(-1e9f);
            foreach (var m in level.RoomMeshes) foreach (var t in m) { foreach (var p in new[] { t.P0, t.P1, t.P2 }) { mn = Vector3.ComponentMin(mn, p); mx = Vector3.ComponentMax(mx, p); } }
            var center = (mn + mx) * 0.5f; float radius = MathF.Max(100f, (mx - mn).Length * 0.5f);
            foreach (var t in new[] { 0f, 0.7f })
            {
                vp.ForcedAnimTime = t;
                var img = vp.RenderToImage(Cam(center, radius, -50f, -40f, 40f), 1000, 1000, showActors: false, Color.FromArgb(20, 20, 28));
                if (img != null) { img.Save(Path.Combine(outDir, $"anim_{sceneId:X2}_t{(int)(t * 10)}.png")); img.Dispose(); }
            }
        }
        finally { form.Controls.Remove(vp); vp.Dispose(); }
        form.Hide();
        Console.WriteLine($"[renderanim] PNGs under {outDir}");
    }

    /// <summary>Renders the OoT Door_Shutter at every door style (and an unlocked + key-locked variant of
    /// each, plus the boss door) and En_Door, front-on, so the bar lattice and lock/chain attachments can
    /// be eyeballed for scale/position/direction. Run: MegatonHammer --renderdoors [outDir]</summary>
    public static void RenderDoors(string[] args)
    {
        string outDir = args.Length >= 2 ? args[1] : @"D:\Copilot_OOT\WorkFolders\MegatonHammer\out\doorcheck";
        Directory.CreateDirectory(outDir);
        if (!File.Exists(OotRom)) { Console.WriteLine("[renderdoors] OoT ROM not found"); return; }

        using var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(64, 64),
        };
        form.Show(); Application.DoEvents();

        var rom = new RomImage(OotRom);
        var lib = new TextureLibrary(); LoadTextures(rom, lib);
        var styles = new (int style, string name)[]
        {
            (0, "Generic-Forest"), (1, "DekuTree"), (2, "Dodongo"), (3, "Jabu"),
            (5, "Fire"), (6, "Water"), (7, "Spirit"), (8, "Shadow"), (10, "GerudoTraining"),
        };
        const int KEY_LOCKED = 0xB << 6, BOSS = 0x5 << 6, BARRED = 0x1 << 6;   // SHUTTER_FRONT_CLEAR

        var vp = new GLViewport(ViewportType.Perspective3D);
        form.Controls.Add(vp); Application.DoEvents();
        try
        {
            var resolver = new ActorModelResolver(rom);
            void Shot(string label, ushort id, ushort var, int style, bool lockBack = false)
            {
                resolver.DoorStyle = style;
                var doc = new MapDocument { ShowSpawnMarker = false };
                var room = doc.Scene.Rooms[0];
                AddFloor(room, "");
                room.Actors.Add(new ZActor { Number = id, Variable = var, XPos = 0, YPos = 0, ZPos = 0, LockBack = lockBack });
                vp.Document = doc; vp.Textures = lib; vp.FallbackResolver = resolver; vp.FallbackRom = rom;
                var (mn, mx) = resolver.ModelWorldBounds(room.Actors[0], true) ?? (new Vector3(-40, 0, -40), new Vector3(40, 110, 40));
                var center = (mn + mx) * 0.5f;
                float radius = MathF.Max(60f, (mx - mn).Length * 0.5f);
                // 3/4 front view (door face is the XY plane; the lock/chains sit in +Z in front) so the
                // attachment scale/position/direction read clearly.
                var img = vp.RenderToImage(Cam(center, radius, -55f, -14f, 40f), 700, 800, showActors: true, Color.FromArgb(40, 40, 48));
                string safe = new string(label.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                if (img != null) { img.Save(Path.Combine(outDir, $"door_{safe}.png")); img.Dispose(); }
                // zoomed close-up centred on the lock (+Z front, mid-height) so the lock/chain/bar attachment reads.
                if (label.Contains("lock", StringComparison.OrdinalIgnoreCase) || label.Contains("Boss"))
                {
                    var lc = new Vector3((mn.X + mx.X) * 0.5f, (mn.Y + mx.Y) * 0.5f, mx.Z);
                    var z = vp.RenderToImage(Cam(lc, MathF.Max(mx.X - mn.X, 50f) * 0.18f, -35f, -6f, 50f), 700, 700, showActors: true, Color.FromArgb(150, 150, 160));
                    if (z != null) { z.Save(Path.Combine(outDir, $"door_{safe}_ZOOM.png")); z.Dispose(); }
                }
                bool ok = resolver.Resolve(room.Actors[0], true) != null;
                Console.WriteLine($"[renderdoors] {label,-26} style={style} var=0x{var:X3} model={(ok ? "YES" : "no")} bounds=({mn.X:F0},{mn.Y:F0},{mn.Z:F0})..({mx.X:F0},{mx.Y:F0},{mx.Z:F0})");
            }
            Shot("EnDoor-knob", 0x0009, 0, 0);
            Shot("EnDoor-knob-lock", 0x0009, 1 << 7, 0);   // DOOR_LOCKED (type 1) — should show the small-key lock+chains
            Shot("EnDoor-lock-back", 0x0009, 1 << 7, 0, lockBack: true);   // lock on the BACK (−Z) face
            Shot("EyeSwitch", 0x012A, 0x0502, 0);          // Obj_Switch type 2 = eye switch (subtype 0, flag 5)
            foreach (var (style, name) in styles)
            {
                Shot($"{name}", 0x002E, 0, style);
                Shot($"{name}-locked", 0x002E, KEY_LOCKED, style);
                Shot($"{name}-barred", 0x002E, BARRED, style);
            }
            foreach (var (theme, tn) in new[] { (0, "Default"), (1, "Fire"), (2, "Water"), (3, "Shadow"), (5, "Forest"), (6, "Spirit") })
            {
                resolver.BossDoorTheme = theme;
                Shot($"BossDoor-{tn}", 0x002E, BOSS, 0);
            }
            resolver.BossDoorTheme = 0;
        }
        finally { form.Controls.Remove(vp); vp.Dispose(); }
        form.Hide();
        Console.WriteLine($"[renderdoors] PNGs under {outDir}");
    }

    /// <summary>Renders EVERY actor in a game to out/actorcheck/{oot|mm}/, one PNG each, with the
    /// Link spawn marker hidden so it doesn't overlap. Run: MegatonHammer --renderallactors [oot|mm|both]</summary>
    public static void RenderAllActors(string[] args)
    {
        string which = args.Length >= 2 ? args[1].ToLowerInvariant() : "both";
        string baseDir = @"D:\Copilot_OOT\WorkFolders\MegatonHammer\out\actorcheck";
        using var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(64, 64),
        };
        form.Show(); Application.DoEvents();

        if (which is "oot" or "both") RenderGameActors(form, false, Path.Combine(baseDir, "oot"));
        if (which is "mm" or "both")  RenderGameActors(form, true,  Path.Combine(baseDir, "mm"));
        form.Hide();
        Console.WriteLine("[renderallactors] done");
    }

    private static void RenderGameActors(Form form, bool mm, string outDir)
    {
        string romPath = mm ? MmRom : OotRom;
        if (!File.Exists(romPath)) { Console.WriteLine($"[renderallactors] {(mm ? "MM" : "OoT")} ROM not found"); return; }
        Directory.CreateDirectory(outDir);
        var rom = new RomImage(romPath);
        var lib = new TextureLibrary(); LoadTextures(rom, lib);
        var resolver = new ActorModelResolver(rom);
        var db = ActorDatabase.Load(isOoT: !mm);

        var actorObjs = ActorObjectTable.Build(mm: mm);
        var objTable = ObjectTable.Build(rom);
        var actors = db.All.ToList();
        Console.WriteLine($"[renderallactors] {(mm ? "MM" : "OoT")}: {actors.Count} actors -> {outDir}");
        var vp = new GLViewport(ViewportType.Perspective3D);
        form.Controls.Add(vp); Application.DoEvents();
        int model = 0, billboard = 0;
        var shouldHaveModel = new List<string>();   // billboard actors that DO have a real ROM object
        var restPose = new List<string>();           // skeletal actors that drew in bind pose (no idle anim)
        try
        {
            foreach (var info in actors)
            {
                var doc = new MapDocument { ShowSpawnMarker = false };   // hide Link so it can't overlap
                var room = doc.Scene.Rooms[0];
                AddFloor(room, "");
                room.Actors.Add(new ZActor { Number = info.Id, Variable = 0 });
                vp.Document = doc; vp.Textures = lib; vp.FallbackResolver = resolver; vp.FallbackRom = rom;

                int read0 = ObjectModelReader.SkeletonsRead, posed0 = ObjectModelReader.SkeletonsPosedWithAnim;
                bool hasModel = resolver.Resolve(room.Actors[0], true) != null;
                // A skeleton that was read but NOT posed with an animation = bind-pose tangle.
                if (ObjectModelReader.SkeletonsRead > read0 && ObjectModelReader.SkeletonsPosedWithAnim == posed0)
                    restPose.Add($"0x{info.Id:X4} {info.Name} -> {actorObjs.ObjectFor(info.Id)}");
                if (hasModel) model++;
                else
                {
                    billboard++;
                    // A billboard actor that has a real, resolvable ROM object should have shown a model.
                    string? obj = actorObjs.ObjectFor(info.Id);
                    if (obj != null && objTable.Resolve(obj) != null)
                        shouldHaveModel.Add($"0x{info.Id:X4} {info.Name} -> {obj}");
                }
                var (mn, mx) = resolver.ModelWorldBounds(room.Actors[0], true)
                    ?? (new Vector3(-40, 0, -40), new Vector3(40, 80, 40));
                var center = (mn + mx) * 0.5f;
                float radius = MathF.Max(40f, (mx - mn).Length * 0.5f);
                var img = vp.RenderToImage(Cam(center, radius, -50f, -18f, 38f), 512, 512, showActors: true, Color.FromArgb(28, 28, 34));
                string safe = new string((info.Name ?? "actor").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                if (safe.Length > 40) safe = safe[..40];
                string tag = hasModel ? "" : "_BILLBOARD";
                if (img != null) { img.Save(Path.Combine(outDir, $"{info.Id:X4}_{safe}{tag}.png")); img.Dispose(); }
            }
        }
        finally { form.Controls.Remove(vp); vp.Dispose(); }
        Console.WriteLine($"[renderallactors] {(mm ? "MM" : "OoT")}: {model} with model, {billboard} billboard");
        Console.WriteLine($"[renderallactors] {(mm ? "MM" : "OoT")}: {shouldHaveModel.Count} billboard actors HAVE a real object but rendered no model:");
        foreach (var s in shouldHaveModel) Console.WriteLine($"    {s}");
        Console.WriteLine($"[renderallactors] {(mm ? "MM" : "OoT")}: {restPose.Count} skeletal actors drew in BIND POSE (no idle anim found):");
        foreach (var s in restPose) Console.WriteLine($"    REST {s}");
    }

    private static void AddFloor(ZRoom room, string tex)
    {
        var s = Editor.Solid.CreateBox(new Vector3(-120, -12, -120), new Vector3(120, 0, 120));
        foreach (var f in s.Faces) f.TextureName = tex;
        room.Geometry.Add(s);
    }

    /// <summary>Measures the per-frame 3D render cost (what the anim timer / every repaint pays) for a
    /// loaded level vs an empty scene, to locate editor lag. Run: MegatonHammer --frametime</summary>
    public static void FrameTime(string[] args)
    {
        if (!File.Exists(OotRom)) { Console.WriteLine("[frametime] OoT ROM not found"); return; }
        using var form = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(64, 64) };
        form.Show(); Application.DoEvents();
        var rom = new RomImage(OotRom);
        var lib = new TextureLibrary(); LoadTextures(rom, lib);
        var vp = new GLViewport(ViewportType.Perspective3D);
        form.Controls.Add(vp); Application.DoEvents();

        void Time(string label, MapDocument doc, Camera3D cam)
        {
            vp.Document = doc; vp.Textures = lib;
            vp.FallbackResolver = doc.Imported?.Resolver; vp.FallbackRom = doc.Imported?.Rom ?? rom;
            var bg = Color.FromArgb(20, 20, 28);
            for (int i = 0; i < 4; i++) vp.RenderToImage(cam, 1280, 720, showActors: true, bg)?.Dispose();   // warm caches
            var swt = System.Diagnostics.Stopwatch.StartNew();
            const int N = 60;
            for (int i = 0; i < N; i++) vp.RenderToImage(cam, 1280, 720, showActors: true, bg)?.Dispose();
            swt.Stop();
            Console.WriteLine($"[frametime] {label,-22} {swt.Elapsed.TotalMilliseconds / N,6:F2} ms/frame  ({N} frames)");
        }

        try
        {
            Time("empty (baseline)", new MapDocument(), Cam(Vector3.Zero, 200, -45, -30, 40));
            foreach (int id in new[] { 0x05, 0x02 })   // Water Temple, Jabu-Jabu
            {
                var level = ImportedLevel.Load(rom, id);
                if (level == null) continue;
                var doc = new MapDocument { Imported = level };
                var scene = doc.Scene;
                for (int i = scene.Rooms.Count; i < Math.Max(1, level.Scene.Rooms.Count); i++) scene.AddRoom();
                for (int i = 0; i < level.Scene.Rooms.Count; i++)
                    foreach (var a in level.Scene.Rooms[i].Actors)
                        scene.Rooms[i].Actors.Add(new ZActor { Number = a.Id, Variable = a.Params, XPos = a.X, YPos = a.Y, ZPos = a.Z, YRot = a.RY });
                Vector3 mn = new(1e9f), mx = new(-1e9f);
                foreach (var mesh in level.RoomMeshes) foreach (var t in mesh) foreach (var p in new[] { t.P0, t.P1, t.P2 })
                { mn = Vector3.ComponentMin(mn, p); mx = Vector3.ComponentMax(mx, p); }
                var center = (mn + mx) * 0.5f; float radius = MathF.Max(80f, (mx - mn).Length * 0.5f);
                int animTris = level.RoomMeshes.Sum(m => m.Count(t => t.AnimSeg != 0));
                Console.WriteLine($"[frametime] scene 0x{id:X2}: geoTris={level.RoomMeshes.Sum(m => m.Count)} actors={doc.ActorCount} animTris={animTris} scroll={level.SegScroll.Count}");
                // RecordUndo cost: the whole-document serialize run on every edit action.
                var swS = System.Diagnostics.Stopwatch.StartNew();
                string? dump = null;
                for (int i = 0; i < 20; i++) dump = Editor.ProjectSerializer.Serialize(doc);
                swS.Stop();
                Console.WriteLine($"[frametime] scene 0x{id:X2}: RecordUndo serialize = {swS.Elapsed.TotalMilliseconds / 20,6:F2} ms  (json {dump?.Length ?? 0} chars)");
                Time($"scene 0x{id:X2}", doc, Cam(center, radius, -45, -30, 40));
            }
        }
        catch (Exception ex) { Console.WriteLine($"[frametime] EXCEPTION: {ex}"); }
        finally { form.Controls.Remove(vp); vp.Dispose(); form.Hide(); }
    }

    private static Camera3D Cam(Vector3 center, float radius, float yaw, float pitch, float fov)
    {
        float yawR = MathHelper.DegreesToRadians(yaw), pitchR = MathHelper.DegreesToRadians(pitch);
        var front = Vector3.Normalize(new Vector3(MathF.Cos(pitchR) * MathF.Cos(yawR), MathF.Sin(pitchR), MathF.Cos(pitchR) * MathF.Sin(yawR)));
        float dist = radius / MathF.Tan(MathHelper.DegreesToRadians(fov * 0.5f)) * 1.15f + radius * 0.2f;
        // Bracket the clip planes tightly around the level so the depth buffer has the precision to
        // stop coplanar floor/water/decal surfaces z-fighting into colourful speckle.
        return new Camera3D
        {
            Fov = fov, Yaw = yaw, Pitch = pitch, Position = center - front * dist,
            Near = MathF.Max(4f, dist - radius * 1.3f), Far = dist + radius * 1.6f,
        };
    }

    // A camera at <paramref name="eye"/> aimed at <paramref name="target"/> with the given FOV.
    private static Camera3D LookAtCam(Vector3 eye, Vector3 target, int fovDeg, float far)
    {
        var dir = target - eye;
        if (dir.LengthSquared < 1e-3f) dir = new Vector3(0, 0, 1);
        dir.Normalize();
        float yaw = MathHelper.RadiansToDegrees(MathF.Atan2(dir.Z, dir.X));
        float pitch = MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)));
        return new Camera3D
        {
            Fov = Math.Clamp(fovDeg, 30, 90),
            Yaw = yaw, Pitch = pitch, Position = eye, Near = 4f, Far = far,
        };
    }

    // Synchronous texture-library population (mirrors MainForm.StartRomTextureLoad's core).
    private static void LoadTextures(RomImage rom, TextureLibrary lib)
    {
        try
        {
            var src = new RomTextureSource(rom);
            var map = RomAssetIndex.BuildMap(rom);
            var infos = src.Scan();
            var (allTex, allFolders) = SceneTextureMapper.Build(rom, infos, map);
            lib.AddRomTextures(allTex, src, map.FileScene, allFolders);
        }
        catch { /* leave built-ins */ }
    }
}
