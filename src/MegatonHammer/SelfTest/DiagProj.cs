using MegatonHammer.Editor;
using MegatonHammer.Otr;
using OpenTK.Mathematics;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Diagnose why a real project voids out / renders black in SoH/2Ship: loads an .mhproj and dumps the
/// spawn, per-room geometry bounds, collision poly count, and the vertex-colour range that drives the
/// in-game shade. Run: MegatonHammer --diagproj &lt;path.mhproj&gt;
/// </summary>
public static class DiagProj
{
    public static void Run(string[] a)
    {
        if (a.Length < 2) { Console.WriteLine("usage: --diagproj <path.mhproj>"); return; }
        string path = a[1];
        var doc = new MapDocument();
        try { ProjectSerializer.Load(doc, path); }
        catch (Exception ex) { Console.WriteLine($"load failed: {ex.Message}"); return; }

        var scene = doc.Scene;
        var st = scene.Settings;
        Console.WriteLine($"== {Path.GetFileName(path)} ==");
        Console.WriteLine($"scene '{scene.Name}'  rooms={scene.Rooms.Count}  spawnRoom={st.SpawnRoom}");
        Console.WriteLine($"SpawnPos = ({st.SpawnPos.X:F0}, {st.SpawnPos.Y:F0}, {st.SpawnPos.Z:F0})  yaw={st.SpawnYaw}");
        Console.WriteLine($"IndoorLighting={st.IndoorLighting}  SkyboxId={st.SkyboxId}  DrawConfig={st.DrawConfig}");

        for (int ri = 0; ri < scene.Rooms.Count; ri++)
        {
            var room = scene.Rooms[ri];
            int brushes = room.Geometry.Count;
            var mn = new Vector3(1e9f); var mx = new Vector3(-1e9f);
            var cmin = new Vector3(1e9f); var cmax = new Vector3(-1e9f);
            int faces = 0;
            foreach (var s in room.Geometry)
            {
                var (a0, b0) = s.GetAABB();
                mn = Vector3.ComponentMin(mn, a0); mx = Vector3.ComponentMax(mx, b0);
                foreach (var f in s.Faces)
                {
                    faces++;
                    var c = f.Color;
                    cmin = Vector3.ComponentMin(cmin, c); cmax = Vector3.ComponentMax(cmax, c);
                }
            }
            Console.WriteLine($"room {ri}: {brushes} brushes, {faces} faces");
            if (brushes > 0)
            {
                Console.WriteLine($"   geometry AABB: ({mn.X:F0},{mn.Y:F0},{mn.Z:F0}) .. ({mx.X:F0},{mx.Y:F0},{mx.Z:F0})");
                Console.WriteLine($"   face colour range: ({cmin.X:F2},{cmin.Y:F2},{cmin.Z:F2}) .. ({cmax.X:F2},{cmax.Y:F2},{cmax.Z:F2})" +
                                  (cmax.LengthSquared < 0.01f ? "  <-- ALL BLACK (would render black in-game)" : ""));
                bool spawnInside = st.SpawnPos.X >= mn.X && st.SpawnPos.X <= mx.X &&
                                   st.SpawnPos.Z >= mn.Z && st.SpawnPos.Z <= mx.Z;
                Console.WriteLine($"   spawn XZ inside geometry footprint? {spawnInside}" +
                                  (spawnInside ? "" : "  <-- SPAWN OUTSIDE GEOMETRY (Link falls into the void)"));
                Console.WriteLine($"   spawn Y vs geometry top {mx.Y:F0}: {(st.SpawnPos.Y > mx.Y ? "above" : st.SpawnPos.Y < mn.Y ? "BELOW floor" : "within")}");
            }
        }

        // Actors + their required objects (mm): does each actor's object resolve so it spawns?
        var aoMm = Rom.ActorObjectTable.Build(mm: true);
        var objMm = Rom.ObjectTable.BuildNamesOnly(mm: true);
        Console.WriteLine("\nActors + object resolution (mm):");
        foreach (var room in scene.Rooms)
        foreach (var act in room.Actors)
        {
            if (act.IsTransitionActor || act.IsEditorOnly) continue;
            string? obj = aoMm.ObjectFor(act.Number);
            int? oid = obj != null ? objMm.IdOf(obj) : null;
            Console.WriteLine($"   0x{act.Number:X4} var=0x{act.Variable:X4} -> object={obj ?? "(none)"} id={(oid?.ToString("X") ?? "?")}" +
                              (obj == null ? "  <-- NO OBJECT (won't spawn)" : oid is null or <= 0 ? "  <-- object id unresolved" : ""));
        }

        // Editor model resolution: does each placed actor resolve to a real 3D model (tris + bounds), or
        // fall back to a billboard? Sanity-checks new model paths (e.g. the Skin system for horses).
        try { Rom.RomFingerprint.AutoDetect(); } catch { }   // CLI: fill the ROM paths from known locations
        string? mmRom = Editor.EditorSettings.MmRomPath;
        if (!string.IsNullOrWhiteSpace(mmRom) && File.Exists(mmRom))
        {
            Console.WriteLine("\nActor model resolution (editor 3D):");
            try
            {
                var resolver = new Editor.ActorModelResolver(new Rom.RomImage(mmRom));
                foreach (var room in scene.Rooms)
                foreach (var act in room.Actors)
                {
                    if (act.IsTransitionActor || act.IsEditorOnly) continue;
                    var model = resolver.Resolve(act, adult: false);
                    if (model == null) { Console.WriteLine($"   0x{act.Number:X4} -> (no model: billboard)"); continue; }
                    var bmn = new Vector3(1e9f); var bmx = new Vector3(-1e9f);
                    foreach (var t in model.Tris) foreach (var pt in new[] { t.P0, t.P1, t.P2 })
                    { bmn = Vector3.ComponentMin(bmn, pt); bmx = Vector3.ComponentMax(bmx, pt); }
                    var sz = (bmx - bmn) * model.Scale;
                    Console.WriteLine($"   0x{act.Number:X4} -> {model.Tris.Count} tris, model size ~({sz.X:F0},{sz.Y:F0},{sz.Z:F0})");
                }
            }
            catch (Exception ex) { Console.WriteLine($"   model resolution failed: {ex.Message}"); }
        }

        // N64 room geometry (DisplayListBuilder) — check the DL/vertices for the invisible-geometry bug.
        try
        {
            var room = scene.Rooms[0];
            var dl = Export.DisplayListBuilder.Build(room, 3, 0x1000, null);   // untextured: pure geometry
            int vtxN = dl.VertexData.Length / 16;
            Console.WriteLine($"\nN64 DisplayListBuilder: {vtxN} verts, DL {dl.DlCommands.Length} bytes, tex {dl.TextureData.Length} bytes");
            // sample vertex XYZ + S/T for NaN/garbage
            int bad = 0;
            for (int i = 0; i < vtxN; i++)
            {
                int o = i * 16;
                short x = (short)((dl.VertexData[o] << 8) | dl.VertexData[o + 1]);
                short y = (short)((dl.VertexData[o + 2] << 8) | dl.VertexData[o + 3]);
                short z = (short)((dl.VertexData[o + 4] << 8) | dl.VertexData[o + 5]);
                short s = (short)((dl.VertexData[o + 8] << 8) | dl.VertexData[o + 9]);
                short t = (short)((dl.VertexData[o + 10] << 8) | dl.VertexData[o + 11]);
                if (i < 4) Console.WriteLine($"   v{i}: pos=({x},{y},{z}) st=({s},{t})");
                if (Math.Abs((int)x) > 10000 || Math.Abs((int)y) > 10000 || Math.Abs((int)z) > 10000) bad++;
            }
            if (vtxN == 0) Console.WriteLine("   *** ZERO vertices — geometry would be INVISIBLE ***");
            if (bad > 0) Console.WriteLine($"   *** {bad} verts with out-of-range positions ***");
            // first DL words (geometry mode, render mode)
            Console.Write("   DL head:");
            for (int i = 0; i < Math.Min(32, dl.DlCommands.Length); i += 4)
                Console.Write($" {dl.DlCommands[i]:X2}{dl.DlCommands[i+1]:X2}{dl.DlCommands[i+2]:X2}{dl.DlCommands[i+3]:X2}");
            Console.WriteLine();
        }
        catch (Exception ex) { Console.WriteLine($"N64 DL build FAILED: {ex.Message}"); }

        // Collision: how many polys does the OTR export actually emit?
        try
        {
            var col = OtrCollisionHeader.Build(scene);
            Console.WriteLine($"collision resource: {col.Length} bytes");
        }
        catch (Exception ex) { Console.WriteLine($"collision build failed: {ex.Message}"); }

        // The crux of a void-out: is there a FLOOR face (normal.Y up) directly under the spawn XZ that
        // Link can land on? Mirror the collision triangulation: a floor triangle whose XZ projection
        // contains the spawn point, at or below the spawn Y. The highest such floor is where Link lands.
        var sp = st.SpawnPos;
        float bestFloorY = float.NegativeInfinity; int floorTris = 0, wallTris = 0;
        foreach (var room in scene.Rooms)
        foreach (var s in room.Geometry)
        foreach (var f in s.Faces)
        {
            var n = f.Plane.Normal;
            bool isFloor = n.Y > 0.5f;
            var verts = f.Vertices;
            for (int i = 1; i < verts.Count - 1; i++)
            {
                if (n.Y > 0.5f) { } // count below
                if (PointInTriXZ(sp, verts[0], verts[i], verts[i + 1]))
                {
                    float y = InterpY(sp, verts[0], verts[i], verts[i + 1]);
                    if (isFloor) { floorTris++; if (y <= sp.Y + 5f && y > bestFloorY) bestFloorY = y; }
                    else wallTris++;
                }
            }
        }
        Console.WriteLine($"under spawn XZ ({sp.X:F0},{sp.Z:F0}): floor-tris={floorTris} wall/other-tris={wallTris}");
        if (float.IsNegativeInfinity(bestFloorY))
            Console.WriteLine($"   *** NO floor under the spawn at/below Y={sp.Y:F0} -> LINK FALLS INTO THE VOID ***");
        else
            Console.WriteLine($"   highest floor under spawn = Y {bestFloorY:F0} (Link should land here; spawn Y={sp.Y:F0})");
    }

    // Is point p inside triangle (a,b,c) projected to the XZ plane?
    private static bool PointInTriXZ(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        float d1 = Sign(p, a, b), d2 = Sign(p, b, c), d3 = Sign(p, c, a);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }
    private static float Sign(Vector3 p, Vector3 a, Vector3 b)
        => (p.X - b.X) * (a.Z - b.Z) - (a.X - b.X) * (p.Z - b.Z);

    // Y of the triangle's plane at p's XZ.
    private static float InterpY(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Cross(b - a, c - a);
        if (MathF.Abs(n.Y) < 1e-6f) return a.Y;
        return a.Y - (n.X * (p.X - a.X) + n.Z * (p.Z - a.Z)) / n.Y;
    }
}
