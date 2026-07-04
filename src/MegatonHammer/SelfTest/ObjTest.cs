using System.Drawing;
using MegatonHammer.Editor;
using MegatonHammer.Export;
using MegatonHammer.Otr;
using OpenTK.Mathematics;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Verifies OBJ mesh import → textured engine export end to end: a Blender/SharpOcarina-style OBJ
/// (with UVs, an .mtl map_Kd texture, and a #nocollision group) parses into ObjMesh, then exports as
/// an OTR room display list + OVTX vertices + an OTEX texture, and as collision that honours the
/// #nocollision tag. Run: MegatonHammer --testobj
/// </summary>
public static class ObjTest
{
    public static void Run()
    {
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) { pass++; Console.WriteLine($"  PASS {what}"); } else { fail++; Console.WriteLine($"  FAIL {what}"); } }

        string dir = Path.Combine(Path.GetTempPath(), "mh_objtest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // A textured wall quad (drawn + collidable) and a #nocollision detail quad.
            using (var tex = new Bitmap(16, 16)) { using var g = Graphics.FromImage(tex); g.Clear(Color.SaddleBrown); tex.Save(Path.Combine(dir, "wall.png")); }
            File.WriteAllText(Path.Combine(dir, "m.mtl"), "newmtl wallmat\nmap_Kd wall.png\n");
            File.WriteAllText(Path.Combine(dir, "level.obj"), string.Join('\n', new[]
            {
                "mtllib m.mtl",
                "v 0 0 0", "v 100 0 0", "v 100 100 0", "v 0 100 0",  // wall
                "v 0 0 50", "v 100 0 50", "v 100 0 150",            // floating detail
                "vt 0 0", "vt 1 0", "vt 1 1", "vt 0 1",
                "g wall",
                "usemtl wallmat",
                "f 1/1 2/2 3/3", "f 1/1 3/3 4/4",
                "g detail #nocollision",
                "f 5/1 6/2 7/3",
            }));

            var mesh = ObjIO.ImportMesh(Path.Combine(dir, "level.obj"));
            Check(mesh.Tris.Count == 3, $"parsed 3 triangles (got {mesh.Tris.Count})");
            Check(mesh.Materials.TryGetValue("wallmat", out var b) && b != null, "material wallmat resolved to a bitmap (map_Kd)");
            Check(mesh.Tris.Count(t => t.NoCollision) == 1, "#nocollision group flagged 1 triangle");
            Check(mesh.Tris[0].UV1.X == 1f, $"per-vertex UV parsed (got {mesh.Tris[0].UV1.X})");

            var room = new ZRoom("test", 0) { ObjMesh = mesh };
            var scene = new ZScene();
            scene.Rooms.Clear(); scene.Rooms.Add(room);

            // OTR textured export.
            var geo = OtrRoomGeometry.Build(room, "vtx", "tex", _ => null);
            Check(!geo.Empty, "OTR geometry not empty");
            Check(geo.Dl.Length > 0 && geo.Vtx.Length > 0, "OTR display list + vertices emitted");
            Check(geo.Textures.Count >= 1, $"OTR texture resource emitted ({geo.Textures.Count})");

            // Collision honours #nocollision: 2 wall tris collide, the detail tri does not.
            var col = CollisionBuilder.Build(scene, 0x02, 0);
            int polyCount = (col[0x14] << 8) | col[0x15];
            Check(polyCount == 2, $"collision has 2 polys (#nocollision excluded) — got {polyCount}");

            // ROM-path textured display list: SETTIMG + RGBA16 texture data + s/t vertex coords.
            var dl = DisplayListBuilder.Build(room, 0x03, 0);
            Check(dl.TextureData.Length > 0, $"ROM DL emitted RGBA16 texture data ({dl.TextureData.Length} B)");
            bool hasSettimg = false;
            for (int i = 0; i + 8 <= dl.DlCommands.Length; i += 8) if (dl.DlCommands[i] == 0xFD) hasSettimg = true;
            Check(hasSettimg, "ROM DL contains a SETTIMG (textured load)");
            // A vertex with non-zero s/t proves UVs reached the ROM vertex block (s at byte 8 of a 16-byte vtx).
            bool hasUv = false;
            for (int i = 0; i + 16 <= dl.VertexData.Length; i += 16) if ((dl.VertexData[i + 8] | dl.VertexData[i + 9]) != 0) hasUv = true;
            Check(hasUv, "ROM vertices carry s/t texture coords");

            // ── #1 object-dependency list: a placed chest (En_Box, object_box) must appear in 0x0B ──
            room.Actors.Add(new ZActor { Number = 0x000A, Variable = 0x0000 });   // En_Box (treasure chest)
            var objRes = ActorObjectResolver.Build(mm: false);
            ushort? boxObj = objRes(0x000A);
            Check(boxObj is > 0, $"resolver maps En_Box → object id 0x{boxObj:X}");
            byte[] roomBin = RoomExporter.Build(room, _ => null, objRes);
            // Scan the room header for the 0x0B command + read its object array.
            int objCmd = -1, objCount = 0, objOff = 0;
            for (int p = 0; p + 8 <= roomBin.Length; p += 8)
            {
                if (roomBin[p] == 0x14) break;
                if (roomBin[p] == 0x0B) { objCmd = p; objCount = roomBin[p + 1]; objOff = (int)(((uint)((roomBin[p + 4] << 24) | (roomBin[p + 5] << 16) | (roomBin[p + 6] << 8) | roomBin[p + 7])) & 0xFFFFFF); break; }
            }
            Check(objCmd >= 0 && objCount >= 1, $"room emits a 0x0B object list (count={objCount})");
            bool listed = false;
            var seen = new List<string>();
            for (int i = 0; i < objCount && objOff + i * 2 + 2 <= roomBin.Length; i++)
            {
                int v = (roomBin[objOff + i * 2] << 8) | roomBin[objOff + i * 2 + 1];
                seen.Add($"0x{v:X}");
                if (v == boxObj) listed = true;
            }
            Check(listed, $"object_box (0x{boxObj:X}) is present in the room's object list (objOff=0x{objOff:X}, ids=[{string.Join(",", seen)}], len=0x{roomBin.Length:X})");

            // ── #2 surface-type authoring: distinct per-brush surface words → distinct table entries ──
            var ss = new ZScene();
            ss.Rooms.Clear();
            var sr = new ZRoom("surf", 0);
            sr.Geometry.Add(Solid.CreateBox(new Vector3(0, 0, 0), new Vector3(100, 10, 100)));                       // normal floor (0,0)
            var voidBox = Solid.CreateBox(new Vector3(200, 0, 0), new Vector3(300, 10, 100)); voidBox.SurfaceData0 = 0x30000000; // void-out
            sr.Geometry.Add(voidBox);
            var climbBox = Solid.CreateBox(new Vector3(400, 0, 0), new Vector3(500, 100, 10)); climbBox.SurfaceData0 = 0x00200000; // climbable wall
            sr.Geometry.Add(climbBox);
            ss.Rooms.Add(sr);
            byte[] col2 = CollisionBuilder.Build(ss, 0x02, 0);
            int stOff = (int)(((uint)((col2[0x1C] << 24) | (col2[0x1D] << 16) | (col2[0x1E] << 8) | col2[0x1F])) & 0xFFFFFF);
            uint Read(int o) => (uint)((col2[o] << 24) | (col2[o + 1] << 16) | (col2[o + 2] << 8) | col2[o + 3]);
            var tableWords = new List<uint>();
            for (int i = 0; i < 4 && stOff + i * 8 + 4 <= col2.Length; i++) tableWords.Add(Read(stOff + i * 8));
            Check(tableWords.Count >= 1 && tableWords[0] == 0, "surface-type entry 0 is plain floor (0,0)");
            Check(tableWords.Contains(0x30000000u), "void-out surface word (0x30000000) present in table");
            Check(tableWords.Contains(0x00200000u), "climbable-wall surface word (0x00200000) present in table");

            Console.WriteLine($"\n==== {(fail == 0 ? "ALL PASS" : $"{fail} FAILED")} ({pass} passed) ====");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
