using MegatonHammer.Forms;

namespace MegatonHammer;

static class Program
{
    private static readonly string LogPath = Editor.AppPaths.Log("crash.log");

    // USER-object count for this process (GR_USEROBJECTS = 1) — window handles etc. A control leak shows
    // up here, not in Process.HandleCount (which counts kernel objects, not HWNDs).
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
    private static uint UserObjects() => GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 1);

    [STAThread]
    static void Main(string[] args)
    {
        // Window-handle leak test for the Properties panel: MegatonHammer --handleleak
        // Rebuilds the panel hundreds of times (as editing/pasting does) and checks the process USER-object
        // count stays bounded. Pre-fix this leaked ~1 control table per rebuild and crashed ("Error creating
        // window handle") once the ~10k handle pool was exhausted.
        if (args.Length >= 1 && args[0] == "--handleleak")
        {
            using var form = new System.Windows.Forms.Form
            {
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None, ShowInTaskbar = false,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location = new System.Drawing.Point(-4000, -4000), Size = new System.Drawing.Size(320, 640),
            };
            var doc = new Editor.MapDocument();
            var b1 = Editor.Solid.CreateBox(new(0, 0, 0), new(64, 64, 64));
            var b2 = Editor.Solid.CreateBox(new(128, 0, 0), new(192, 64, 64));
            doc.Scene.Rooms[0].Geometry.Add(b1); doc.Scene.Rooms[0].Geometry.Add(b2);
            // A chest actor exercises BuildActor -> AddSchemaFields, incl. the 128-item Contents combo + its
            // tooltip — the heaviest per-rebuild control set (this is what crashed when handles leaked).
            var chest = new Editor.ZActor { Number = 0x000A, Variable = 0x0001 };
            doc.Scene.Rooms[0].Actors.Add(chest);
            var db = Editor.ActorDatabase.Load(isOoT: true);
            var panel = new PropertiesPanel(doc, db, true) { Dock = System.Windows.Forms.DockStyle.Fill };
            form.Controls.Add(panel);
            form.Show(); System.Windows.Forms.Application.DoEvents();
            uint baseline = 0;
            for (int i = 0; i < 600; i++)
            {
                // Cycle solid1, solid2, the chest actor, and the scene so every panel variant rebuilds.
                doc.ClearSelection();
                if ((i % 4) == 0) b1.IsSelected = true;
                else if ((i % 4) == 1) b2.IsSelected = true;
                else if ((i % 4) == 2) chest.IsSelected = true;
                panel.ForceRefresh();
                System.Windows.Forms.Application.DoEvents();
                if (i == 50) baseline = UserObjects();                       // measure after warm-up
                if (i % 150 == 0) Console.WriteLine($"  iter {i,4}: USER objects = {UserObjects()}");
            }
            uint final = UserObjects();
            int growth = (int)final - (int)baseline;
            Console.WriteLine($"[handleleak] 550 rebuilds: USER objects {baseline} -> {final} (growth {growth})");
            Console.WriteLine($"[handleleak] {(growth < 200 ? "PASS — bounded, no per-rebuild handle leak" : "FAIL — handles grow per rebuild")}");
            form.Hide();
            return;
        }

        // Headless smoke test for the actor model pipeline (#8): MegatonHammer --selftest [romPath]
        if (args.Length >= 1 && args[0] == "--selftest")
        {
            try { SelfTest.ModelSelfTest.Run(args.Length >= 2 ? args[1] : null); }
            catch (Exception ex) { Console.WriteLine($"[selftest] EXCEPTION: {ex}"); }
            return;
        }

        // Read-only forest-scene texture/palette diagnostic. MegatonHammer --foresttex
        if (args.Length >= 1 && args[0] == "--foresttex")
        {
            try { SelfTest.ForestTexProbe.Run(); }
            catch (Exception ex) { Console.WriteLine($"[foresttex] EXCEPTION: {ex}"); }
            return;
        }

        // Read-only N64 append-parity probe (scene tables, UNSET slots, free space). MegatonHammer --n64probe
        if (args.Length >= 1 && args[0] == "--n64probe")
        {
            try { SelfTest.N64Probe.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[n64probe] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: actor texture-coverage audit. MegatonHammer --audit [romPath]
        if (args.Length >= 1 && args[0] == "--audit")
        {
            try { SelfTest.ModelSelfTest.Audit(args.Length >= 2 ? args[1] : null); }
            catch (Exception ex) { Console.WriteLine($"[audit] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: list large ROM files / dump animation joint frames.
        if (args.Length >= 2 && args[0] == "--animframe")
        {
            try { SelfTest.ModelSelfTest.AnimFrame(args); }
            catch (Exception ex) { Console.WriteLine($"[animframe] EXCEPTION: {ex}"); }
            return;
        }

        // Headless proof that scene setups are a from-blank editor feature. MegatonHammer --testsetups
        if (args.Length >= 1 && args[0] == "--testsetups")
        {
            try { SelfTest.SetupTest.Run(); }
            catch (Exception ex) { Console.WriteLine($"[testsetups] EXCEPTION: {ex}"); }
            return;
        }

        // Verify the dialogue message encoder (markup → OoT/MM control bytes). MegatonHammer --testmessages
        if (args.Length >= 1 && args[0] == "--testmessages")
        {
            try { SelfTest.MessageTest.Run(); }
            catch (Exception ex) { Console.WriteLine($"[testmessages] EXCEPTION: {ex}"); }
            return;
        }

        // Locate OoT's message table in a ROM and report stats. MegatonHammer --testmsgtable [romPath]
        if (args.Length >= 1 && args[0] == "--testmsgtable")
        {
            try { SelfTest.MessageTest.Table(args.Length >= 2 ? args[1] : null); }
            catch (Exception ex) { Console.WriteLine($"[testmsgtable] EXCEPTION: {ex}"); }
            return;
        }

        // Verify in-place message overwrite against a ROM. MegatonHammer --testmsgappend [romPath]
        if (args.Length >= 1 && args[0] == "--testmsgappend")
        {
            try { SelfTest.MessageTest.Append(args.Length >= 2 ? args[1] : null); }
            catch (Exception ex) { Console.WriteLine($"[testmsgappend] EXCEPTION: {ex}"); }
            return;
        }

        // List MM ROM textures for picking real surfaces. MegatonHammer --listmmtex [substr]
        if (args.Length >= 1 && args[0] == "--listmmtex")
        {
            try { SelfTest.MmTexList.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[listmmtex] EXCEPTION: {ex}"); }
            return;
        }

        // Decompress an MM ROM to a flat (ROM==VROM) image + validate the scene table.
        // MegatonHammer --decompressmm [inRom] [outRom]
        if (args.Length >= 1 && args[0] == "--decompressmm")
        {
            try { SelfTest.MmDecompressTest.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[decompressmm] EXCEPTION: {ex}"); }
            return;
        }

        // Inject a custom 1-room scene into the decompressed MM retail ROM (Termina Field slot).
        // MegatonHammer --injectmmscene [inRom] [outRom]
        if (args.Length >= 1 && args[0] == "--injectmmscene")
        {
            try { SelfTest.MmInjectScene.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[injectmmscene] EXCEPTION: {ex}"); }
            return;
        }

        // Apply ONLY the OoT debug auto-boot detour to a ROM (no scene injection) to isolate-test it: boots
        // straight into vanilla SCENE_TEST01. MegatonHammer --ootautoboot [inRom] [outRom] [entranceHex]
        if (args.Length >= 1 && args[0] == "--ootautoboot")
        {
            string inRom = args.Length >= 2 ? args[1] : Editor.AppPaths.Rom(@"ZELOOTMA.Z64");
            string outRom = args.Length >= 3 ? args[2] : System.IO.Path.Combine(Editor.AppPaths.BaseDir, @"roms\mm_test\oot_autoboot.z64");
            int entr = args.Length >= 4 ? Convert.ToInt32(args[3], 16) : 0x0094;   // SCENE_TEST01
            try
            {
                var rom = System.IO.File.ReadAllBytes(inRom);
                bool ok = Rom.OotDebugAutoBoot.Patch(rom, entr);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outRom)!);
                System.IO.File.WriteAllBytes(outRom, rom);
                Console.WriteLine($"[ootautoboot] {(ok ? "PATCHED" : "SKIPPED (unrecognized ROM)")} entrance=0x{entr:X4} -> {outRom}");
            }
            catch (Exception ex) { Console.WriteLine($"[ootautoboot] EXCEPTION: {ex}"); }
            return;
        }

        // Author the demo "Test Temple" dungeon project + map. MegatonHammer --testtemple [outDir]
        if (args.Length >= 1 && args[0] == "--testtemple")
        {
            try { SelfTest.TestTempleBuilder.Build(args.Length >= 2 ? args[1] : System.IO.Path.Combine(Editor.AppPaths.BaseDir, @"out")); }
            catch (Exception ex) { Console.WriteLine($"[testtemple] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: whole-game level fidelity audit. MegatonHammer --auditlevels [oot|mm|both]
        if (args.Length >= 1 && args[0] == "--auditlevels")
        {
            try { SelfTest.LevelAudit.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[audit] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnose a real project's spawn/geometry/collision (void-out / black render). MegatonHammer --diagproj <path>
        if (args.Length >= 1 && args[0] == "--diagrom")
        {
            try { SelfTest.DiagRom.Run(args); } catch (Exception ex) { Console.WriteLine(ex.ToString()); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--diagtex")
        {
            try { SelfTest.DiagTex.Run(args); } catch (Exception ex) { Console.WriteLine(ex.ToString()); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--diagproj")
        {
            try { SelfTest.DiagProj.Run(args); } catch (Exception ex) { Console.WriteLine($"[diagproj] {ex}"); }
            return;
        }

        // Diagnostic (#9): clip/slice geometry pipeline. MegatonHammer --testclip
        if (args.Length >= 1 && args[0] == "--testclip")
        {
            try { SelfTest.ClipTest.Run(); } catch (Exception ex) { Console.WriteLine($"[testclip] {ex}"); }
            return;
        }

        // Diagnostic (#7): actor model-RESOLUTION coverage — who renders as a real model vs a
        // billboard/point. MegatonHammer --modelaudit [oot|mm|both] [romPath]
        if (args.Length >= 1 && args[0] == "--modelaudit")
        {
            try { SelfTest.ModelAudit.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[modelaudit] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: whole-game actor/gimmick editor-coverage audit. MegatonHammer --actoraudit [oot|mm|both]
        if (args.Length >= 1 && args[0] == "--actoraudit")
        {
            try { SelfTest.ActorAudit.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[actoraudit] EXCEPTION: {ex}"); }
            return;
        }

        // Headless: pack a custom-inventory playtest O2R into the engine mods dir. MegatonHammer --packplaytest [engineDir] [preset]
        if (args.Length >= 1 && args[0] == "--packplaytest")
        {
            try { SelfTest.PlaytestPack.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[packplaytest] EXCEPTION: {ex}"); }
            return;
        }

        // Headless: build the textured N64 ROM (PJ64 parity with SoH/2Ship). MegatonHammer --packplaytestn64 [outDir]
        // Round-trip fidelity audit: import every scene, re-export via the faithful retain path, verify
        // no crash + actors/commands preserved + edits persist. MegatonHammer --roundtrip [oot|mm|both]
        if (args.Length >= 1 && args[0] == "--roundtrip")
        {
            try { SelfTest.RoundTripAudit.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[roundtrip] EXCEPTION: {ex}"); }
            return;
        }

        // Audit decomp-derived per-actor draw scales (OoT/MM) + flag non-literal gaps.
        // MegatonHammer --scaleaudit [oot|mm|both] [all]
        if (args.Length >= 1 && args[0] == "--scaleaudit")
        {
            try { SelfTest.ActorScaleAudit.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[scaleaudit] EXCEPTION: {ex}"); }
            return;
        }

        // Verify the texture coplanar-run + adjacent-align geometry headlessly: MegatonHammer --texaligntest
        if (args.Length >= 1 && args[0] == "--texaligntest")
        {
            var V = (float x, float y, float z) => new OpenTK.Mathematics.Vector3(x, y, z);
            // Three boxes in a row along +X (a 3-brush wall). Front faces (normal -Z, z=0) form one surface.
            var a = Editor.Solid.CreateBox(V(0, 0, 0),   V(64, 64, 64));
            var b = Editor.Solid.CreateBox(V(64, 0, 0),  V(128, 64, 64));
            var c = Editor.Solid.CreateBox(V(128, 0, 0), V(192, 64, 64));
            var solids = new List<Editor.Solid> { a, b, c };
            Editor.SolidFace Front(Editor.Solid s) => s.Faces.OrderBy(f => f.Plane.Normal.Z).First();   // most -Z
            var fa = Front(a); var fb = Front(b); var fc = Front(c);
            Console.WriteLine($"[texaligntest] front-face normals: a={fa.Plane.Normal} b={fb.Plane.Normal} c={fc.Plane.Normal}");

            var run = Editor.TextureAlign.CoplanarRun(solids, fa);
            bool runOk = run.Count == 3 && run.Contains(fa) && run.Contains(fb) && run.Contains(fc);
            Console.WriteLine($"[texaligntest] CoplanarRun(front a) => {run.Count} faces (expect 3 across the wall): {(runOk ? "PASS" : "FAIL")}");

            var top = a.Faces.OrderByDescending(f => f.Plane.Normal.Y).First();
            var topRun = Editor.TextureAlign.CoplanarRun(solids, top);
            Console.WriteLine($"[texaligntest] CoplanarRun(top a) => {topRun.Count} faces (expect 3, the shared ceiling): {(topRun.Count == 3 ? "PASS" : "FAIL")}");

            // Adjacent-with-texture + seam align: paint b's front "wall", then "paint" a's front and align it.
            fb.TextureName = "wall"; fb.TexScaleS = 64; fb.TexScaleT = 64; fb.TexShiftS = 0; fb.TexShiftT = 0; fb.ResetAxes();
            var nb = Editor.TextureAlign.AdjacentWithTexture(solids, fa, "wall");
            Console.WriteLine($"[texaligntest] AdjacentWithTexture(a front,'wall') => {(ReferenceEquals(nb, fb) ? "b front (PASS)" : "WRONG (FAIL)")}");
            fa.TextureName = "wall";
            if (nb != null) Editor.TextureAlign.TryAlignAcrossSeam(nb, fa);
            // Continuity check: at the shared edge x=64,z=0, a's UV.x and b's UV.x should match (mod 1 tile).
            var edge = V(64, 32, 0);
            float ua = fa.UVAt(edge).X, ub = fb.UVAt(edge).X;
            float du = MathF.Abs(ua - ub); du -= MathF.Floor(du); if (du > 0.5f) du = 1 - du;
            Console.WriteLine($"[texaligntest] seam UV.x a={ua:F3} b={ub:F3} diff(mod tile)={du:F4} => {(du < 0.01f ? "CONTINUOUS (PASS)" : "FAIL")}");

            // Rotation is read back from the real axes (fix: a visibly-rotated face must report its angle).
            var rA = Editor.Solid.CreateBox(V(0, 0, 0), V(64, 64, 64));
            var rf = rA.Faces.OrderBy(f => f.Plane.Normal.Z).First();
            rf.SetRotation(90f);
            float rd = rf.CurrentRotationDegrees();
            Console.WriteLine($"[texaligntest] SetRotation(90) -> CurrentRotationDegrees={rd:F1}, TexRotation={rf.TexRotation:F1} => {(MathF.Abs(rd - 90) < 0.5f ? "PASS" : "FAIL")}");

            // Texture-lock-style direct axis set must sync the displayed rotation (was the stale-0 bug).
            rf.SetAxes(rf.VAxis, -rf.UAxis);   // a further 90deg turn applied straight to the axes
            Console.WriteLine($"[texaligntest] SetAxes(turn) -> rotation now {rf.CurrentRotationDegrees():F1} (field {rf.TexRotation:F1}) => {(MathF.Abs(rf.CurrentRotationDegrees() - rf.TexRotation) < 0.5f ? "IN SYNC (PASS)" : "FAIL")}");

            // Aligning a coplanar neighbour to a rotated source carries the rotation across.
            var sA = Editor.Solid.CreateBox(V(0, 0, 0),   V(64, 64, 64));
            var sB = Editor.Solid.CreateBox(V(64, 0, 0),  V(128, 64, 64));
            var sf = sA.Faces.OrderBy(f => f.Plane.Normal.Z).First();
            var df = sB.Faces.OrderBy(f => f.Plane.Normal.Z).First();
            sf.SetRotation(90f);
            Editor.TextureAlign.TryAlignAcrossSeam(sf, df);
            Console.WriteLine($"[texaligntest] align neighbour to src(rot90) -> neighbour rotation {df.CurrentRotationDegrees():F1} => {(MathF.Abs(df.CurrentRotationDegrees() - 90) < 1f ? "PASS" : "FAIL")}");

            // Same-texture-only shift-select stops at a texture boundary; all-adjacent crosses it.
            var ta = Editor.Solid.CreateBox(V(0, 0, 0), V(64, 64, 64));
            var tb = Editor.Solid.CreateBox(V(64, 0, 0), V(128, 64, 64));
            var tc = Editor.Solid.CreateBox(V(128, 0, 0), V(192, 64, 64));
            var tset = new List<Editor.Solid> { ta, tb, tc };
            ta.Faces.OrderBy(f => f.Plane.Normal.Z).First().TextureName = "wall";
            tb.Faces.OrderBy(f => f.Plane.Normal.Z).First().TextureName = "wall";
            tc.Faces.OrderBy(f => f.Plane.Normal.Z).First().TextureName = "other";
            var tfa = ta.Faces.OrderBy(f => f.Plane.Normal.Z).First();
            int same = Editor.TextureAlign.CoplanarRun(tset, tfa, true).Count;
            int all = Editor.TextureAlign.CoplanarRun(tset, tfa, false).Count;
            Console.WriteLine($"[texaligntest] sameTextureOnly run={same} (expect 2), allAdjacent run={all} (expect 3) => {(same == 2 && all == 3 ? "PASS" : "FAIL")}");

            // Right-click align uses the SELECTED reference face, not an arbitrary same-texture neighbour
            // (the bug: every face is the same texture, so it grabbed the top face instead of the one clicked).
            var doc = new Editor.MapDocument();
            var box = Editor.Solid.CreateBox(V(0, 0, 0), V(64, 64, 64));
            foreach (var f in box.Faces) f.TextureName = "wall";
            doc.Scene.Rooms[0].Geometry.Add(box);
            var front = box.Faces.OrderBy(f => f.Plane.Normal.Z).First();          // -Z (the reference)
            var side = box.Faces.OrderByDescending(f => f.Plane.Normal.X).First(); // +X (the right-clicked target)
            front.SetRotation(45f);
            front.FaceSelected = true;   // user left-clicked the front face
            var reference = doc.SelectedFaces.LastOrDefault(f => !ReferenceEquals(f, side));
            Console.WriteLine($"[texaligntest] right-click reference = {(ReferenceEquals(reference, front) ? "the selected front face (PASS)" : "WRONG face (FAIL)")}");

            // Applying a reference face's mapping to a NON-adjacent, differently-oriented face must keep the
            // texture axes IN the target's plane (no shear/stretch), not borrow the source's out-of-plane axes.
            var refBox = Editor.Solid.CreateBox(V(0, 0, 0), V(64, 64, 64));
            var tgtBox = Editor.Solid.CreateBox(V(500, 0, 0), V(900, 8, 200));   // far away, long + flat (a beam)
            var refFace = refBox.Faces.OrderBy(f => f.Plane.Normal.Z).First();        // -Z wall
            var tgtFace = tgtBox.Faces.OrderByDescending(f => f.Plane.Normal.Y).First(); // +Y top (perpendicular)
            refFace.TextureName = "wall"; refFace.SetRotation(0f);
            tgtFace.TextureName = "wall";
            Editor.TextureAlign.TryAlignAcrossSeam(refFace, tgtFace);
            var (tu, tv) = tgtFace.TextureAxes();
            float uDotN = MathF.Abs(OpenTK.Mathematics.Vector3.Dot(tu, tgtFace.Plane.Normal));
            float vDotN = MathF.Abs(OpenTK.Mathematics.Vector3.Dot(tv, tgtFace.Plane.Normal));
            Console.WriteLine($"[texaligntest] apply to non-adjacent diff-plane face: U.n={uDotN:F3} V.n={vDotN:F3} (expect ~0, in-plane) => {(uDotN < 0.01f && vDotN < 0.01f ? "NO STRETCH (PASS)" : "FAIL")}");

            // Texture lock through a rotate must keep the texture PINNED: a surface point's UV is preserved
            // (the shift is re-offset, not just the axes rotated) — same formula SelectTool.LockTextureAxes uses.
            {
                var Dot = (Func<OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3, float>)OpenTK.Mathematics.Vector3.Dot;
                var u0 = OpenTK.Mathematics.Vector3.UnitX; var v0 = OpenTK.Mathematics.Vector3.UnitY;
                float sS = 64, sT = 64, shS = 0.2f, shT = -0.3f;
                var P = V(150, 40, 100); var pivot = V(132, 32, 132);
                float uvU0 = Dot(P, u0) / sS + shS, uvV0 = Dot(P, v0) / sT + shT;
                float ang = 37f * MathF.PI / 180f, cc = MathF.Cos(ang), ss = MathF.Sin(ang);
                Func<OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3> Rot = w => V(cc * w.X - ss * w.Y, ss * w.X + cc * w.Y, w.Z);
                var u1 = Rot(u0); var v1 = Rot(v0);
                float shS1 = shS + (Dot(pivot, u0) - Dot(pivot, u1)) / sS;
                float shT1 = shT + (Dot(pivot, v0) - Dot(pivot, v1)) / sT;
                var P1 = Rot(P - pivot) + pivot;
                float uvU1 = Dot(P1, u1) / sS + shS1, uvV1 = Dot(P1, v1) / sT + shT1;
                bool pinned = MathF.Abs(uvU1 - uvU0) < 1e-3f && MathF.Abs(uvV1 - uvV0) < 1e-3f;
                Console.WriteLine($"[texaligntest] rotate texture-lock pins UV: ({uvU0:F3},{uvV0:F3}) -> ({uvU1:F3},{uvV1:F3}) => {(pinned ? "PINNED (PASS)" : "FAIL")}");
            }

            // Moving a brush with texture lock must NOT COMPOUND the offset: a drag re-applies
            // RestorePlanes + Translate(cumulativeDelta) each frame, and ComputeFaces CARRIES the shifted
            // offset, so without re-baselining the texture the shift drifts every frame (the reported bug,
            // obvious on centred/aligned faces). Verify the fixed pattern (restore baseline shift before each
            // Translate — what SelectTool.RestoreMoveTex does) equals ONE Translate of the total delta.
            {
                Editor.Solid.TextureLock = true;
                var total = V(160, 0, 96);
                Editor.SolidFace TopOf(Editor.Solid s) => s.Faces.OrderByDescending(f => f.Plane.Normal.Y).First();

                var mvRefBox = Editor.Solid.CreateBox(V(0, 0, 0), V(64, 64, 64));
                var rt = TopOf(mvRefBox); rt.TexScaleS = 64; rt.TexScaleT = 64; rt.TexShiftS = 0.3f; rt.TexShiftT = 0.7f;
                mvRefBox.Translate(total);                                 // reference: single translate
                float refS = TopOf(mvRefBox).TexShiftS;

                var mv = Editor.Solid.CreateBox(V(0, 0, 0), V(64, 64, 64));
                var mt = TopOf(mv); mt.TexScaleS = 64; mt.TexScaleT = 64; mt.TexShiftS = 0.3f; mt.TexShiftT = 0.7f;
                var snap = mv.SnapshotPlanes();
                var baseShift = mv.Faces.Where(f => f.PlaneIndex >= 0)
                                        .ToDictionary(f => f.PlaneIndex, f => (f.TexShiftS, f.TexShiftT));
                for (int i = 1; i <= 8; i++)                               // 8-frame drag, cumulative delta grows
                {
                    mv.RestorePlanes(snap);
                    foreach (var f in mv.Faces)
                        if (baseShift.TryGetValue(f.PlaneIndex, out var t)) { f.TexShiftS = t.Item1; f.TexShiftT = t.Item2; }
                    mv.Translate(total * (i / 8f));
                }
                float draggedS = TopOf(mv).TexShiftS;
                bool noDrift = MathF.Abs(draggedS - refS) < 1e-3f;
                Console.WriteLine($"[texaligntest] move texture-lock no-compound: dragged={draggedS:F3} single={refS:F3} => {(noDrift ? "NO DRIFT (PASS)" : "FAIL")}");
            }
            return;
        }

        // Probe one object's skeleton detection + pinned idle frame-0: MegatonHammer --skelprobe [oot|mm] <object>
        if (args.Length >= 3 && args[0] == "--skelprobe")
        {
            bool mm = args[1].Equals("mm", StringComparison.OrdinalIgnoreCase);
            string romPath = mm ? Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).z64")
                                : Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
            var rom = new Rom.RomImage(romPath);
            var objs = Rom.ObjectTable.Build(rom);
            var bytes = objs.GetObjectBytes(rom, args[2]);
            if (bytes == null) { Console.WriteLine($"no bytes for {args[2]}"); return; }
            int skel = Rom.ObjectModelReader.FindSkeleton(bytes);
            int limbCount = skel >= 0 && skel + 5 <= bytes.Length ? bytes[skel + 4] : -1;
            Console.WriteLine($"[skelprobe] {args[2]}: len=0x{bytes.Length:X} FindSkeleton=0x{(skel < 0 ? -1 : skel):X} limbCount={limbCount}");
            if (args.Length >= 4)
            {
                int hoff = Convert.ToInt32(args[3], 16);
                uint U32b(int o) => (uint)((bytes[o] << 24) | (bytes[o + 1] << 16) | (bytes[o + 2] << 8) | bytes[o + 3]);
                var sb = new System.Text.StringBuilder($"  raw 0x{hoff - 16:X}: ");
                for (int b = hoff - 16; b < hoff + 16 && b < bytes.Length; b++) sb.Append($"{bytes[b]:X2}{((b + 1) % 4 == 0 ? " " : "")}");
                Console.WriteLine(sb.ToString());
                Console.WriteLine($"  hdr@0x{hoff:X}: segPtr=0x{U32b(hoff):X8} limbCount={bytes[hoff + 4]} dListCount={bytes[hoff + 5]}");
                int arr = (int)(U32b(hoff) & 0xFFFFFF); int cnt = bytes[hoff + 4];
                Console.WriteLine($"  limbArray@0x{arr:X} (inRange={arr + cnt * 4 <= bytes.Length})");
                for (int i = 0; i < cnt; i++)
                {
                    int lp = (int)(U32b(arr + i * 4) & 0xFFFFFF);
                    if (lp + 12 > bytes.Length) { Console.WriteLine($"    limb{i} ptr=0x{lp:X} OUT OF RANGE"); continue; }
                    uint dl = U32b(lp + 8);
                    Console.WriteLine($"    limb{i}@0x{lp:X}: child={bytes[lp + 6]} sibling={bytes[lp + 7]} dl=0x{dl:X8} (seg={(dl >> 24)})");
                }
            }
            int idle = Rom.ActorIdleAnimTable.Build(mm).OffsetFor(args[2]) ?? -1;
            Console.WriteLine($"  pinned idle offset = 0x{(idle < 0 ? -1 : idle):X}");
            if (idle >= 0 && limbCount > 0)
            {
                var j = Rom.ObjectModelReader.ReadAnimFrame0(bytes, idle, limbCount);
                Console.WriteLine($"  ReadAnimFrame0 -> {(j == null ? "NULL" : $"{j.Length} shorts; root=({j[0]},{j[1]},{j[2]}) limb0rot=({j[3]},{j[4]},{j[5]}) limb1rot=({j[6]},{j[7]},{j[8]})")}");
            }
            return;
        }

        // Verify a WATERBOX-textured brush exports as a collision water box: MegatonHammer --watertest
        if (args.Length >= 1 && args[0] == "--watertest")
        {
            Func<float, float, float, OpenTK.Mathematics.Vector3> V = (x, y, z) => new(x, y, z);
            var scene = new Editor.ZScene("t");
            if (scene.Rooms.Count == 0) scene.AddRoom();
            var room = scene.Rooms[0];
            room.Geometry.Add(Editor.Solid.CreateBox(V(0, 0, 0), V(64, 64, 64)));   // a normal solid brush
            var wb = Editor.Solid.CreateBox(V(200, -32, 200), V(300, 0, 300));       // textured WATERBOX, NOT IsWater-flagged
            foreach (var f in wb.Faces) f.TextureName = Textures.SpecialTextures.Water;
            room.Geometry.Add(wb);
            var col = Export.CollisionBuilder.Build(scene, 0x03, 0);
            int numWb = (short)((col[0x24] << 8) | col[0x25]);   // numWaterBoxes @ header offset 0x24 (big-endian)
            Console.WriteLine($"[watertest] WATERBOX-textured brush (IsWater flag={wb.IsWater}) -> numWaterBoxes={numWb} (expect 1) => {(numWb == 1 ? "PASS" : "FAIL")}");
            return;
        }

        // Verify the climbable + warp tool textures (encoding vs decomp): MegatonHammer --tooltex
        if (args.Length >= 1 && args[0] == "--tooltex")
        {
            var ST = typeof(Textures.SpecialTextures);
            void Chk(bool ok, string m) => Console.WriteLine($"[tooltex] {m} => {(ok ? "PASS" : "FAIL")}");
            // Wall-type bits: data0>>21&0x1F. Ladder=2, Vines=4, Crawlspace=5 (verified z_bgcheck.c sWallFlags).
            (uint d0, uint d1) SB(string n) => Textures.SpecialTextures.SurfaceBits(n)!.Value;
            Chk((SB("LADDER").d0 >> 21 & 0x1F) == 2, "LADDER -> wall type 2 (ladder side-climb, WALL_FLAG_1)");
            Chk((SB("VINES").d0 >> 21 & 0x1F) == 4, "VINES -> wall type 4 (front-climb, WALL_FLAG_3)");
            Chk((SB("CRAWLSPACE").d0 >> 21 & 0x1F) == 5, "CRAWLSPACE -> wall type 5 (child crawl, WALL_FLAG_4)");
            Chk(Textures.SpecialTextures.IsNoRender("LADDER") && Textures.SpecialTextures.IsNoRender("VINES")
                && Textures.SpecialTextures.IsNoRender("WARP"), "LADDER/VINES/WARP are NoRender (invisible)");
            // A brush wearing WARP is a warp trigger; the CollisionBuilder assigns it the brush's exit index.
            Func<float, float, float, OpenTK.Mathematics.Vector3> V = (x, y, z) => new(x, y, z);
            var scene = new Editor.ZScene("t"); if (scene.Rooms.Count == 0) scene.AddRoom();
            var warp = Editor.Solid.CreateBox(V(0, 0, 0), V(64, 8, 64));
            foreach (var f in warp.Faces) f.TextureName = Textures.SpecialTextures.Warp;
            warp.ExitEntrance = 0x00CD; scene.Rooms[0].Geometry.Add(warp);
            Chk(Export.CollisionBuilder.IsWarpTrigger(warp), "WARP-textured brush is a trigger");
            Chk(Export.CollisionBuilder.ExitEntrances(scene).Contains(0x00CD), "WARP brush exit entrance 0xCD exported");
            // The wall-type-2 surface bytes appear somewhere in the built collision (2<<21 = 0x00400000).
            var lad = Editor.Solid.CreateBox(V(200, 0, 200), V(210, 128, 260));
            foreach (var f in lad.Faces) f.TextureName = Textures.SpecialTextures.Ladder;
            scene.Rooms[0].Geometry.Add(lad);
            var col = Export.CollisionBuilder.Build(scene, 0x03, 0);
            bool hasLadderType = false;
            for (int i = 0; i + 4 <= col.Length; i += 4)
                if ((uint)(col[i] << 24 | col[i + 1] << 16 | col[i + 2] << 8 | col[i + 3]) == (2u << 21)) hasLadderType = true;
            Chk(hasLadderType, "LADDER brush collision contains a wall-type-2 (0x00400000) surface word");
            return;
        }

        // Verify the flag-connection overlay doesn't spuriously wire same-type default actors: --linktest
        if (args.Length >= 1 && args[0] == "--linktest")
        {
            void Chk(bool ok, string m) => Console.WriteLine($"[linktest] {m} => {(ok ? "PASS" : "FAIL")}");
            // oot_devtestmap repro: 3 PLAIN hookshot posts (type 0, ignore switch flag) + 2 DEFAULT chests
            // (treasure flag 0, self-state). None of these logically wire to each other → expect 0 lines.
            var spurious = new List<Editor.ZActor>
            {
                new() { Number = 0x012D, Variable = 0 }, new() { Number = 0x012D, Variable = 0 },
                new() { Number = 0x012D, Variable = 0 },
                new() { Number = 0x000A, Variable = 0 }, new() { Number = 0x000A, Variable = 0 },
            };
            int n1 = Editor.FlagConnectionAnalyzer.Links(spurious, true).Count;
            Chk(n1 == 0, $"3 plain Hsblock + 2 default chests -> {n1} links (expect 0 — no same-type auto-link)");
            // A REAL wire still forms: two SINKING hookshot posts (type 1) that both use switch flag 5.
            var real = new List<Editor.ZActor>
            {
                new() { Number = 0x012D, Variable = (ushort)(1 | (5 << 8)) },
                new() { Number = 0x012D, Variable = (ushort)(1 | (5 << 8)) },
            };
            int n2 = Editor.FlagConnectionAnalyzer.Links(real, true).Count;
            Chk(n2 >= 1, $"2 sinking Hsblock on switch flag 5 -> {n2} links (expect >=1 — real wire preserved)");
            // Chest treasure-flag normalization: two chests both at flag 0 (like oot_devtestmap) must be split.
            var cdoc = new Editor.MapDocument();
            cdoc.Scene.Rooms[0].Actors.Add(new Editor.ZActor { Number = 0x000A, Variable = 0x0120 });  // longshot, flag 0
            cdoc.Scene.Rooms[0].Actors.Add(new Editor.ZActor { Number = 0x000A, Variable = 0x0640 });  // bomb bag, flag 0
            int fixedN = cdoc.NormalizeChestFlags();
            var flags = cdoc.AllActors.Where(a => a.Number == 0x000A).Select(a => a.Variable & 0x1F).ToList();
            Chk(fixedN == 1 && flags.Distinct().Count() == 2,
                $"2 chests both flag 0 -> normalized (fixed {fixedN}, flags now {string.Join(',', flags)})");
            var contents = cdoc.AllActors.Where(a => a.Number == 0x000A).Select(a => (a.Variable >> 5) & 0x7F).ToList();
            Chk(contents.Contains(9) && contents.Contains(0x32), "contents preserved (longshot 9 + bomb bag 0x32) after normalize");
            // Basic/Advanced field split: a chest's Contents + type are BASIC; its logic flags are ADVANCED.
            var chestDef = Editor.ActorParamSchema.For(true, 0x000A)!;
            bool contentsBasic = !chestDef.Fields.First(f => f.Name == "Contents").IsAdvanced;
            bool treasureAdv = chestDef.Fields.First(f => f.Name == "Treasure flag").IsAdvanced;
            int nBasic = chestDef.Fields.Count(f => !f.IsAdvanced), nAdv = chestDef.Fields.Count(f => f.IsAdvanced);
            Chk(contentsBasic && treasureAdv && nBasic >= 2 && nAdv >= 1,
                $"chest field split: {nBasic} basic (Contents shown), {nAdv} advanced (flags hidden)");
            // Beamos: Size + Sight range are both basic (no logic flags to hide).
            var vmDef = Editor.ActorParamSchema.For(true, 0x008A)!;
            Chk(vmDef.Fields.All(f => !f.IsAdvanced), "beamos fields all basic (Size + Sight range visible)");
            return;
        }

        // Bake the decomp-derived schemas to shipped Data/ files (portable to no-decomp builds): --dumpschemas
        if (args.Length >= 1 && args[0] == "--dumpschemas")
        {
            // Locate the project's Data/ folder (walk up from the running dll to the dir holding the csproj).
            string dataDir;
            var d = new System.IO.DirectoryInfo(Editor.AppPaths.BaseDir);
            for (int i = 0; i < 12 && d != null; i++, d = d.Parent)
                if (System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "MegatonHammer.csproj"))) break;
            dataDir = System.IO.Path.Combine(d?.FullName ?? Editor.AppPaths.BaseDir, "Data");
            System.IO.Directory.CreateDirectory(dataDir);
            void Dump(bool oot)
            {
                var defs = Editor.ActorParamSchemaExtractor.For(oot);
                if (defs.Count == 0) { Console.WriteLine($"[dumpschemas] {(oot ? "OoT" : "MM")}: extractor empty (decomp not found — set MH_SOURCES)"); return; }
                string file = System.IO.Path.Combine(dataDir, Editor.BakedSchemas.FileName(oot));
                System.IO.File.WriteAllText(file, Editor.BakedSchemas.Serialize(defs));
                int enums = defs.Values.Sum(dd => dd.Fields.Count(f => f.Options != null));
                Console.WriteLine($"[dumpschemas] {(oot ? "OoT" : "MM")}: {defs.Count} actors ({enums} enum dropdowns) -> {file}");
            }
            Dump(true); Dump(false);
            return;
        }

        // Report actor-schema coverage (curated vs auto-derived vs raw hex) per game: MegatonHammer --schemacoverage
        if (args.Length >= 1 && args[0] == "--schemacoverage")
        {
            void Report(bool oot)
            {
                string game = oot ? "oot-master" : "mm-main";
                string tag = oot ? "OoT" : "MM";
                string? tbl = Editor.AppPaths.SourceFile(game, "include", "tables", "actor_table.h");
                var ids = new List<(int id, string name)>();
                if (tbl != null && System.IO.File.Exists(tbl))
                    foreach (var line in System.IO.File.ReadLines(tbl))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(line, @"/\*\s*0x([0-9A-Fa-f]+)\s*\*/\s*DEFINE_ACTOR\w*\(\s*(\w+)\s*,");
                        if (m.Success) ids.Add((Convert.ToInt32(m.Groups[1].Value, 16), m.Groups[2].Value));
                    }
                var curated = Editor.ActorParamSchema.CuratedDefs(oot).Select(kv => (int)kv.Key).ToHashSet();
                var extracted = Editor.ActorParamSchemaExtractor.For(oot).Keys.Select(k => (int)k).ToHashSet();
                int nCur = 0, nExt = 0, nRaw = 0;
                var rawList = new List<string>();
                foreach (var (id, name) in ids)
                {
                    if (curated.Contains(id)) nCur++;
                    else if (extracted.Contains(id)) nExt++;
                    else { nRaw++; rawList.Add($"0x{id:X4} {name}"); }
                }
                Console.WriteLine($"[coverage] {tag}: {ids.Count} actors | curated(dropdowns) {nCur} | auto-derived(named) {nExt} | RAW HEX {nRaw}");
                int nBaked = Editor.BakedSchemas.For(oot).Count;
                var baked = Editor.BakedSchemas.For(oot).Keys.Select(k => (int)k).ToHashSet();
                int publicFriendly = ids.Count(x => curated.Contains(x.id) || baked.Contains(x.id));
                Console.WriteLine($"[coverage] {tag}: friendly (dev, decomp present) = {nCur + nExt} ({100.0 * (nCur + nExt) / System.Math.Max(1, ids.Count):0.0}%)");
                Console.WriteLine($"[coverage] {tag}: friendly (PUBLIC build, curated {nCur} + baked {nBaked}) = {publicFriendly} ({100.0 * publicFriendly / System.Math.Max(1, ids.Count):0.0}%)");
                var outp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"schema-raw-{tag}.txt");
                System.IO.File.WriteAllLines(outp, rawList);
                Console.WriteLine($"[coverage] {tag}: raw-hex actor list -> {outp}");
            }
            Report(true); Report(false);
            return;
        }

        // Validate every hand-curated actor param schema for internal consistency: MegatonHammer --schematest
        if (args.Length >= 1 && args[0] == "--schematest")
        {
            int fails = 0;
            void Chk(bool ok, string m) { if (!ok) { Console.WriteLine($"[schematest] {m} => FAIL"); fails++; } }

            void Validate(bool oot)
            {
                string tag = oot ? "OoT" : "MM";
                int n = 0;
                foreach (var (id, def) in Editor.ActorParamSchema.CuratedDefs(oot))
                {
                    n++;
                    foreach (var f in def.Fields)
                    {
                        Chk(f.Shift >= 0 && f.Length >= 1 && f.Shift + f.Length <= 16,
                            $"{tag} 0x{id:X4} '{f.Name}': bit range [{f.Shift}..{f.Shift + f.Length - 1}] out of 0..15");
                        if (f.Options != null)
                            Chk(f.Options.Count <= (1 << f.Length),
                                $"{tag} 0x{id:X4} '{f.Name}': {f.Options.Count} enum options exceed {f.Length}-bit field (max {1 << f.Length})");
                        if (f.Flag != Editor.ActorParamSchema.FlagKind.None)
                            Chk(f.Role != Editor.ActorParamSchema.FlagRole.None,
                                $"{tag} 0x{id:X4} '{f.Name}': flag field has no Setter/Reader role");
                        // Every enum option must round-trip through the (signed-aware) bit encode/decode.
                        if (f.Kind == Editor.ActorParamSchema.FieldKind.Enum && f.Options != null)
                            for (int oi = 0; oi < f.Options.Count; oi++)
                            {
                                ushort v = f.Set((ushort)0, f.EnumValueAt(oi));
                                Chk(f.EnumIndex(v) == oi, $"{tag} 0x{id:X4} '{f.Name}': enum option {oi} ({f.Options[oi]}) round-trips");
                            }
                    }
                    // No two params-stored fields may claim the same bit (FromRotZ fields live in Rot Z, separate).
                    var pf = def.Fields.Where(f => !f.FromRotZ).ToList();
                    for (int i = 0; i < pf.Count; i++)
                        for (int j = i + 1; j < pf.Count; j++)
                        {
                            bool overlap = pf[i].Shift < pf[j].Shift + pf[j].Length && pf[j].Shift < pf[i].Shift + pf[i].Length;
                            Chk(!overlap, $"{tag} 0x{id:X4}: fields '{pf[i].Name}' and '{pf[j].Name}' overlap in params bits");
                        }
                }
                Console.WriteLine($"[schematest] {tag}: validated {n} curated schemas");
            }

            Validate(true); Validate(false);
            Console.WriteLine($"[schematest] {(fails == 0 ? "ALL PASS" : fails + " FAIL(s)")}");
            return;
        }

        // Prove a spray dab only shades vertices within the brush radius, NOT the whole face (the "whole brush
        // tints" bug). Paints one dab near a corner of a big TEXTURED quad and checks the far corner stays at
        // the unpainted base (white). MegatonHammer --shadetest
        if (args.Length >= 1 && args[0] == "--shadetest")
        {
            int fails = 0;
            void Chk(bool ok, string m) { if (!ok) { Console.WriteLine($"[shadetest] {m} => FAIL"); fails++; } }
            var box = Editor.Solid.CreateBox(new OpenTK.Mathematics.Vector3(-256, -8, -256), new OpenTK.Mathematics.Vector3(256, 8, 256));
            // The top face (a 512×512 quad); mark it textured so the base is white (the reported case).
            var top = box.Faces.OrderByDescending(f => f.Plane.Normal.Y).First();
            top.TextureName = "test_tex";
            var tool = new Tools.ShadePaintTool(new Editor.MapDocument())
            { PaintColor = OpenTK.Mathematics.Vector3.Zero, Radius = 32f, Opacity = 1f, Erase = false };
            var corner = new OpenTK.Mathematics.Vector3(256, 8, 256);   // dab near one corner
            Chk(tool.PaintAt(top, corner), "dab reports a change");
            var g = top.ShadePaint;
            Chk(g != null, "textured quad got a shade grid");
            if (g != null)
            {
                var white = OpenTK.Mathematics.Vector3.One;
                int atBase = g.Colors.Count(c => (c - white).LengthSquared < 1e-4f);
                int darkened = g.Colors.Count(c => c.LengthSquared < 0.25f);   // pulled toward black
                Chk(atBase > g.Colors.Length / 2, $"most nodes stay at unpainted white ({atBase}/{g.Colors.Length}) — whole face NOT tinted");
                Chk(darkened >= 1, $"at least one node near the dab darkened ({darkened})");
                // The far corner node specifically must be untouched (radius 32 << 724-unit diagonal).
                Chk((g.Colors[0] - white).LengthSquared < 1e-4f, "far corner node is still white (dab is local)");
            }
            Console.WriteLine($"[shadetest] {(fails == 0 ? "ALL PASS — spray is local, no whole-brush tint" : fails + " FAIL(s)")}");
            return;
        }

        // Measure the world-space size of freestanding En_Item00 models (rupee vs heart) so the editor preview
        // scales can be tuned to match. MegatonHammer --itemscale
        if (args.Length >= 1 && args[0] == "--itemscale")
        {
            string romPath = Editor.AppPaths.Rom("Legend of Zelda, The - Ocarina of Time (USA).z64");
            if (!System.IO.File.Exists(romPath)) { Console.WriteLine($"[itemscale] no ROM at {romPath}"); return; }
            var rom = new Rom.RomImage(romPath);
            var resolver = new Editor.ActorModelResolver(rom);
            foreach (int t in new[] { 0x00, 0x02, 0x13, 0x14, 0x03, 0x06, 0x07 })
            {
                var a = new Editor.ZActor { Number = 0x0015, Variable = (ushort)t };
                var m = resolver.Resolve(a, adult: true);
                if (m == null || m.Tris.Count == 0) { Console.WriteLine($"[itemscale] type 0x{t:X2}: no model"); continue; }
                var mn = new OpenTK.Mathematics.Vector3(float.MaxValue);
                var mx = new OpenTK.Mathematics.Vector3(float.MinValue);
                foreach (var tri in m.Tris)
                    foreach (var p in new[] { tri.P0, tri.P1, tri.P2 })
                    { mn = OpenTK.Mathematics.Vector3.ComponentMin(mn, p); mx = OpenTK.Mathematics.Vector3.ComponentMax(mx, p); }
                float objSize = (mx - mn).Length;
                Console.WriteLine($"[itemscale] type 0x{t:X2}: objSize={objSize,8:F1}  scale={m.Scale:F4}  worldSize={objSize * m.Scale,7:F3}  ({m.Tris.Count} tris)");
            }
            return;
        }

        // Verify the vanilla-SoH .o2r export + level-pack merge: write a single-level o2r, add a 2nd scene, and
        // check entries/paths/conflict detection. MegatonHammer --exporto2rtest [file.mhproj]
        if (args.Length >= 1 && args[0] == "--exporto2rtest")
        {
            int fails = 0;
            void Chk(bool ok, string m) { if (!ok) { Console.WriteLine($"[exporto2rtest] {m} => FAIL"); fails++; } }
            var doc = new Editor.MapDocument();
            if (args.Length >= 2 && System.IO.File.Exists(args[1])) Editor.ProjectSerializer.Load(doc, args[1]);
            var scene = doc.Scene;
            string outDir = System.IO.Path.Combine(Editor.AppPaths.BaseDir, "out");
            System.IO.Directory.CreateDirectory(outDir);
            string o2r = System.IO.Path.Combine(outDir, "exporttest.o2r");
            if (System.IO.File.Exists(o2r)) System.IO.File.Delete(o2r);

            // New archive overriding Hyrule Field (0x51).
            var resA = Export.O2RPacker.BuildVanillaSceneResources(scene, 0x51, mm: false, masterQuest: false, texResolver: null);
            Chk(resA.Count > 0, "built scene resources for 0x51");
            Chk(resA.All(r => r.Path.StartsWith("scenes/", StringComparison.OrdinalIgnoreCase)), "resources live under scenes/ path");
            var ow0 = Export.O2RPacker.WriteLevelO2R(o2r, resA, merge: false);
            Chk(ow0.Count == 0 && System.IO.File.Exists(o2r), "fresh o2r written");

            // Merge a 2nd scene (Kokiri Forest 0x55) → pack now overrides both, no conflict.
            var resB = Export.O2RPacker.BuildVanillaSceneResources(scene, 0x55, mm: false, masterQuest: false, texResolver: null);
            var ow1 = Export.O2RPacker.WriteLevelO2R(o2r, resB, merge: true);
            Chk(ow1.Count == 0, "adding a DIFFERENT scene reports no overwrite");
            var entries = Export.O2RPacker.ListEntries(o2r);
            Chk(entries.Any(p => p.Contains("spot00")), "pack still contains scene 0x51 (spot00) after merge");
            Chk(resB.All(p => entries.Contains(p.Path)), "pack contains scene 0x55 after merge");

            // Re-merge scene 0x51 → conflict: every 0x51 path already present ⇒ reported as overwritten.
            var ow2 = Export.O2RPacker.WriteLevelO2R(o2r, resA, merge: true);
            Chk(ow2.Count == resA.Count, $"re-adding scene 0x51 reports {resA.Count} overwrite(s) (got {ow2.Count})");
            Console.WriteLine($"[exporto2rtest] {(fails == 0 ? "ALL PASS" : fails + " FAIL(s)")}  (o2r: {o2r})");
            return;
        }

        // Slice a PAINTED brush headlessly and verify the halves are valid + no face carries a shade grid /
        // VertexColors array whose size mismatches its (reshaped) geometry. MegatonHammer --slicetest
        if (args.Length >= 1 && args[0] == "--slicetest")
        {
            int fails = 0;
            void Chk(bool ok, string m) { if (!ok) { Console.WriteLine($"[slicetest] {m} => FAIL"); fails++; } }
            var box = Editor.Solid.CreateBox(new OpenTK.Mathematics.Vector3(-64, -64, -64), new OpenTK.Mathematics.Vector3(64, 64, 64));
            // Paint every quad face: a parametric ShadeGrid + a matching VertexColors fallback.
            foreach (var f in box.Faces)
            {
                if (f.Vertices.Count == 4)
                    f.ShadePaint = new Editor.SolidFace.ShadeGrid { Nu = 3, Nv = 3,
                        Colors = Enumerable.Repeat(OpenTK.Mathematics.Vector3.Zero, 16).ToArray() };
                f.VertexColors = Enumerable.Repeat(OpenTK.Mathematics.Vector3.Zero, f.Vertices.Count).ToArray();
            }
            // Cut on an angled plane so at least one quad is trimmed to a non-quad (the reshape case).
            var n = OpenTK.Mathematics.Vector3.Normalize(new OpenTK.Mathematics.Vector3(1, 0, 0.35f));
            var (front, back) = box.Split(new Editor.Plane3D(n, 10f));
            Chk(front != null && back != null, "angled cut yields both halves");
            foreach (var half in new[] { front, back })
            {
                if (half == null) continue;
                foreach (var f in half.Faces)
                {
                    Chk(f.VertexColors == null || f.VertexColors.Length == f.Vertices.Count,
                        $"VertexColors[{f.VertexColors?.Length}] matches {f.Vertices.Count}-vert face");
                    Chk(f.ShadePaint == null || (f.Vertices.Count == 4 && f.ShadePaint.Colors.Length == (f.ShadePaint.Nu + 1) * (f.ShadePaint.Nv + 1)),
                        $"ShadePaint only on quad faces with a well-sized grid ({f.Vertices.Count} verts)");
                }
            }
            Console.WriteLine($"[slicetest] {(fails == 0 ? "ALL PASS" : fails + " FAIL(s)")}");
            return;
        }

        // Reproduce editor crashes when opening an actor's Entity Configuration sheet, headlessly: build the
        // EntityConfigDialog for every curated actor across a spread of variable values and report any that
        // throw. A hard crash (StackOverflow) will terminate this process on the offending actor — the last
        // "[configtest] building …" line printed before death names it. MegatonHammer --configtest
        if (args.Length >= 1 && args[0] == "--configtest")
        {
            int fails = 0;
            var th = new System.Threading.Thread(() =>
            {
                ApplicationConfiguration.Initialize();
                foreach (bool oot in new[] { true, false })
                {
                    string tag = oot ? "OoT" : "MM";
                    var db = Editor.ActorDatabase.Load(oot);
                    foreach (var info in db.All)
                    {
                        ushort id = info.Id;
                        foreach (ushort v in new ushort[] { 0x0000, 0x00FF, 0x1234, 0xFFFF })
                        {
                            Console.WriteLine($"[configtest] building {tag} 0x{id:X4} var=0x{v:X4}");
                            Console.Out.Flush();
                            var actor = new Editor.ZActor { Number = id, Variable = v };
                            try { using var dlg = new Forms.EntityConfigDialog(actor, db, oot, null); }
                            catch (Exception ex)
                            {
                                fails++;
                                Console.WriteLine($"[configtest] {tag} 0x{id:X4} var=0x{v:X4} THREW {ex.GetType().Name}: {ex.Message}");
                            }
                        }
                    }
                }
                Console.WriteLine($"[configtest] {(fails == 0 ? "ALL PASS" : fails + " threw")}");
            });
            th.SetApartmentState(System.Threading.ApartmentState.STA);
            th.Start(); th.Join();
            return;
        }

        // Verify the Dungeon Mechanism presets emit correct, vanilla, wired actor data: MegatonHammer --presettest
        if (args.Length >= 1 && args[0] == "--presettest")
        {
            void Chk(bool ok, string m) => Console.WriteLine($"[presettest] {m} => {(ok ? "PASS" : "FAIL")}");

            void RunGame(bool isMM)
            {
                string tag = isMM ? "MM " : "OoT";
                var doc = new Editor.MapDocument();
                if (isMM) doc.InitGameDefaults(true);
                var presets = Editor.DungeonMechanismPresets.For(isMM).ToList();
                Chk(presets.Count >= 1, $"{tag}: {presets.Count} preset(s) available");

                // Read an actor's switch-flag value + role from the schema (setter or reader), whatever the actor is.
                (Editor.ActorParamSchema.FlagRole role, int val)? SwitchFlagOf(Editor.ZActor a)
                {
                    var f = Editor.ActorParamSchema.For(!isMM, a.Number)?.Fields
                            .FirstOrDefault(x => x.Flag == Editor.ActorParamSchema.FlagKind.Switch);
                    if (f == null) return null;
                    int v = f.FromRotZ ? ((a.ZRot >> f.Shift) & f.Mask) : f.Get(a.Variable);
                    return (f.Role, v);
                }

                var flagsUsed = new List<int>();
                foreach (var p in presets)
                {
                    var placed = Editor.DungeonMechanismPresets.Insert(doc, p, OpenTK.Mathematics.Vector3.Zero);
                    var refs  = placed.Select(SwitchFlagOf).Where(x => x != null).Select(x => x!.Value).ToList();
                    var flags = refs.Select(x => x.val).ToList();

                    if (flags.Count >= 2)   // a flag-bus mechanism (switch/torch/silver -> gate/ladder)
                    {
                        Chk(placed.Count >= 2, $"{tag} '{p.Name}': placed {placed.Count} actors");
                        // Every switch-flag reference in the preset must point at the SAME flag (one wired channel).
                        Chk(flags.Distinct().Count() == 1, $"{tag} '{p.Name}': all wired to ONE flag ({string.Join(',', flags.Distinct())})");
                        flagsUsed.Add(flags[0]);
                        bool canSet  = refs.Any(x => x.role is Editor.ActorParamSchema.FlagRole.Setter or Editor.ActorParamSchema.FlagRole.Both);
                        bool canRead = refs.Any(x => x.role is Editor.ActorParamSchema.FlagRole.Reader or Editor.ActorParamSchema.FlagRole.Both);
                        Chk(canSet && canRead, $"{tag} '{p.Name}': has a flag setter + a flag reader");
                        int g = placed[0].GroupId;
                        Chk(g != 0 && placed.All(a => a.GroupId == g), $"{tag} '{p.Name}': grouped as one unit (group {g})");
                    }
                    else   // a non-flag mechanism (e.g. boss reward + warp exit)
                    {
                        Chk(placed.Count >= 1, $"{tag} '{p.Name}': placed {placed.Count} actor(s) (non-flag mechanism)");
                    }
                }

                Chk(flagsUsed.Distinct().Count() == flagsUsed.Count,
                    $"{tag}: each flag preset got a UNIQUE switch flag ({string.Join(',', flagsUsed)}) — no shared-state collision");
                int links = Editor.FlagConnectionAnalyzer.Links(doc.AllActors.ToList(), !isMM).Count;
                Chk(links >= flagsUsed.Count, $"{tag}: flag-bus wires formed: {links} link(s) (>= {flagsUsed.Count})");

                // Boss-exit (OoT): a Heart Container + an invisible WARP trigger pad, grouped together.
                if (!isMM)
                {
                    var trig  = doc.Solids.FirstOrDefault(s => s.IsTrigger);
                    var heart = doc.AllActors.FirstOrDefault(a => a.Number == 0x005F);
                    Chk(trig != null, "OoT boss-exit: created a warp trigger pad");
                    Chk(heart != null, "OoT boss-exit: placed a Heart Container (0x005F)");
                    Chk(trig != null && trig.Faces.Any(f => f.TextureName == Textures.SpecialTextures.Warp),
                        "OoT boss-exit: warp pad carries the WARP tool texture");
                    if (trig != null && heart != null)
                        Chk(trig.GroupId != 0 && trig.GroupId == heart.GroupId, $"OoT boss-exit: heart + warp pad grouped (group {trig.GroupId})");

                    // Torch presets must emit Timed torches (type 1): Permanent (type 0) torches are NOT
                    // player-lightable (z_obj_syokudai.c ignite path is gated on torchType != 0).
                    var typeF = Editor.ActorParamSchema.For(true, 0x005E)!.Fields.First(f => f.Name == "Torch type");
                    foreach (var id in new[] { "torch_gate", "multi_torch_gate" })
                    {
                        var td = new Editor.MapDocument();
                        var placed = Editor.DungeonMechanismPresets.Insert(td, Editor.DungeonMechanismPresets.ById(id)!, OpenTK.Mathematics.Vector3.Zero);
                        var torches = placed.Where(a => a.Number == 0x005E).ToList();
                        Chk(torches.Count >= 1 && torches.All(t => typeF.Get(t.Variable) == 1),
                            $"OoT '{id}': {torches.Count} torch(es) are Timed/player-lightable (not Permanent)");
                    }
                }
            }

            RunGame(false);   // OoT
            RunGame(true);    // MM
            return;
        }

        // Verify the dialogue model + encoder (the Talon "buy a Cuccoo" mockup): MegatonHammer --dialoguetest
        if (args.Length >= 1 && args[0] == "--dialoguetest")
        {
            void Chk(bool ok, string m) => Console.WriteLine($"[dialoguetest] {m} => {(ok ? "PASS" : "FAIL")}");
            const int doneFlag = 12;

            // Greeting box: colour spans + text-speed timing ("Haw ~1 haw") + a laugh SFX + a gesture.
            var msgA = new Editor.MhMessage(0x0301,
                "My name is %bTalon%w! I own this ranch.&Say, how'd you like to marry %rMalon%w?&Haw ~1haw ~1haw!")
            { Sfx = 0x6836, Gesture = 1 };
            // Purchase prompt: give item + charge rupees on Yes; branch on No; fulfilled-state fallback.
            var msgB = new Editor.MhMessage(0x0302, "Buy a %bCuccoo%w for %r20 Rupees%w?")
            {
                Kind = Editor.MhMsgKind.Prompt, Choice1 = "%gYes", Choice2 = "%gNo",
                Outcome1 = new() { GiveItem = 0x0048, ChargeRupees = true, RupeeCost = 20, FireFlag = doneFlag },
                Outcome2 = new() { NextMsgId = 0x0303 },
                DoneFlag = doneFlag, AfterMsgId = 0x0304,
            };
            var msgAfter = new Editor.MhMessage(0x0304, "Thanks! Take good care of that Cuccoo.");

            var a = Export.MessageEncoder.Encode(msgA, mm: false);
            var b = Export.MessageEncoder.Encode(msgB, mm: false);

            Chk(a[^1] == 0x02, "OoT message terminated with END (0x02)");
            Chk(a.Contains((byte)0x05), "greeting has a COLOUR control code (0x05)");
            Chk(a.Contains((byte)0x14), "greeting has a TEXT_SPEED control code (0x14) from ~1 timing");
            Chk(a[0] == 0x12 && ((a[1] << 8) | a[2]) == 0x6836, "greeting opens with a SFX control code (0x12 + id) for the NPC noise");
            Chk(msgA.Gesture == 1, "greeting carries a gesture index for the speaking actor");
            Chk(b.Contains((byte)0x1B), "prompt emits the TWO_CHOICE control code (0x1B)");
            Chk(System.Text.Encoding.ASCII.GetString(b).Contains("Yes") &&
                System.Text.Encoding.ASCII.GetString(b).Contains("No"), "prompt encodes both choice labels");

            // MM control bytes differ (SFX 0x1E, two-choice 0xC2, END 0xBF) — z_message_nes.c.
            var mmA = Export.MessageEncoder.Encode(msgA, mm: true);
            var mmB = Export.MessageEncoder.Encode(msgB, mm: true);
            Chk(mmA[0] == 0x1E && ((mmA[1] << 8) | mmA[2]) == 0x6836, "MM greeting opens with SFX (0x1E + id)");
            Chk(mmB.Contains((byte)0xC2) && mmB[^1] == 0xBF, "MM prompt emits two-choice (0xC2) and ends 0xBF");

            var yes = msgB.Outcome1;
            Chk(yes.GiveItem == 0x0048 && yes.ChargeRupees && yes.RupeeCost == 20 && yes.FireFlag == doneFlag,
                $"'Yes' = give item + charge {yes.RupeeCost} rupees + set flag {yes.FireFlag} (purchase)");
            Chk(msgB.Outcome2.NextMsgId == 0x0303, "'No' branches to another message (Display MsgBox #)");
            Chk(msgB.DoneFlag == doneFlag && msgB.AfterMsgId == 0x0304, "fulfilled state -> fallback message wired");
            Chk(!yes.IsEmpty && msgAfter.Outcome1.IsEmpty, "outcome emptiness detection works");

            // The portable mh_dialogue_data.c export: behaviour rows only, matching the actor's table format.
            string cData = Export.MhDialogueDataWriter.Write(new[] { msgA, msgB, msgAfter });
            Chk(cData.Contains("gMhDialogueTable") && cData.Contains("gMhDialogueCount"), "data export has the table + count");
            Chk(cData.Contains("0x0302, 0x0000, -1, 1,"), "prompt row present (textId/sfx/gesture/isPrompt)");
            Chk(cData.Contains(", 12, 0x") || cData.Contains(", 12, 772"), "prompt row carries the done flag (12)");
            Chk(cData.Contains("{ -1, 12, 72, 20 }"), "'Yes' outcome row = give item 72 + charge 20 + flag 12");
            Chk(!Export.MhDialogueDataWriter.NeedsEntry(msgAfter), "plain fallback message needs no behaviour row");

            // The portable dialogue-point actor is placeable + opens the Dialogue Editor (has a Message field).
            var mhDef = Editor.ActorParamSchema.For(true, Editor.ActorDatabase.MhTalkId);
            Chk(mhDef != null && mhDef.Fields.Any(f => f.Kind == Editor.ActorParamSchema.FieldKind.Message),
                "En_MhTalk (0x0230) has a Message field -> the Dialogue Editor 'Edit...' button");
            Chk(Editor.ActorDatabase.Load(true).Get(Editor.ActorDatabase.MhTalkId) != null,
                "En_MhTalk is registered as a placeable editor actor (shows in PLACE ACTOR)");

            // Default vs Custom: a Default (vanilla-reference) message is NOT exported; only Custom overrides are.
            var vanillaRef = new Editor.MhMessage(0x0305, "vanilla text") { Kind = Editor.MhMsgKind.Prompt, IsOverride = false };
            string cData2 = Export.MhDialogueDataWriter.Write(new[] { msgB, vanillaRef });
            Chk(cData2.Contains("0x0302") && !cData2.Contains("0x0305"),
                "Default (vanilla) dialogue is left alone; only Custom overrides export");

            // Per-NPC vanilla dialogue catalog: contextual "Default Dialogue" lines for talkable NPCs.
            Chk(Editor.DialogueCatalog.For(false, 0x0084) is { Length: > 0 }, "dialogue catalog has Talon (En_Ta 0x0084) vanilla lines");
            Chk(Editor.DialogueCatalog.For(false, 0x01CE) is { Length: > 0 }, "dialogue catalog has Zora (En_Zo 0x01CE) vanilla lines");
            Chk(Editor.DialogueCatalog.For(false, 0x0146)![0].TextId == 0x1002, "catalog maps Saria (0x0146) to her greeting textId 0x1002");
            Chk(Editor.DialogueCatalog.For(true, 0x0202) is { Length: > 0 }, "MM catalog has Anju (En_An 0x0202) vanilla lines");
            Chk(Editor.DialogueCatalog.For(true, 0x01A4)![0].TextId == 0x334D, "MM catalog maps Romani (En_Ma4 0x01A4) to 0x334D");
            return;
        }

        // Verify the compile-time face culler headlessly: MegatonHammer --culltest
        if (args.Length >= 1 && args[0] == "--culltest")
        {
            Func<float, float, float, OpenTK.Mathematics.Vector3> V = (x, y, z) => new(x, y, z);
            Func<Editor.Solid, OpenTK.Mathematics.Vector3, Editor.SolidFace> faceBy =
                (s, n) => s.Faces.OrderByDescending(f => OpenTK.Mathematics.Vector3.Dot(f.Plane.Normal, n)).First();
            var allN = new[] { V(1, 0, 0), V(-1, 0, 0), V(0, 1, 0), V(0, -1, 0), V(0, 0, 1), V(0, 0, -1) };

            // 1. Two equal boxes flush at x=64: the two shared faces are buried; the other 10 are visible.
            var a = Editor.Solid.CreateBox(V(0, 0, 0), V(64, 64, 64));
            var b = Editor.Solid.CreateBox(V(64, 0, 0), V(128, 64, 64));
            var r1 = new List<Editor.Solid> { a, b };
            bool aShared = Export.FaceCuller.IsObscured(faceBy(a, V(1, 0, 0)), a, r1);
            bool bShared = Export.FaceCuller.IsObscured(faceBy(b, V(-1, 0, 0)), b, r1);
            int aVisCulled = allN.Where(n => n.X < 0.5f).Count(n => Export.FaceCuller.IsObscured(faceBy(a, n), a, r1));
            Console.WriteLine($"[culltest] flush boxes: shared culled a={aShared} b={bShared}; A's 5 visible faces wrongly culled={aVisCulled} => {(aShared && bShared && aVisCulled == 0 ? "PASS" : "FAIL")}");

            // 2. Standalone box: nothing culled.
            var c = Editor.Solid.CreateBox(V(500, 0, 0), V(564, 64, 64));
            var r2 = new List<Editor.Solid> { c };
            int cCulled = allN.Count(n => Export.FaceCuller.IsObscured(faceBy(c, n), c, r2));
            Console.WriteLine($"[culltest] standalone box: faces culled={cCulled} (expect 0) => {(cCulled == 0 ? "PASS" : "FAIL")}");

            // 3. Small box flush against a big box — the CRITICAL safety case: the small box's fully-covered
            //    face is culled, but the big box's only-PARTLY-covered face must NOT be (else a hole).
            var big = Editor.Solid.CreateBox(V(200, 0, 0), V(264, 64, 64));
            var small = Editor.Solid.CreateBox(V(264, 16, 16), V(300, 48, 48));
            var r3 = new List<Editor.Solid> { big, small };
            bool smallCovered = Export.FaceCuller.IsObscured(faceBy(small, V(-1, 0, 0)), small, r3);
            bool bigPartly = Export.FaceCuller.IsObscured(faceBy(big, V(1, 0, 0)), big, r3);
            Console.WriteLine($"[culltest] small-vs-big: small fully-covered culled={smallCovered}, big partial culled={bigPartly} (must be false) => {(smallCovered && !bigPartly ? "PASS" : "FAIL")}");

            // 4. Two boxes that only TOUCH at an edge (offset) — neither face is buried.
            var d = Editor.Solid.CreateBox(V(0, 0, 0), V(64, 64, 64));
            var e = Editor.Solid.CreateBox(V(64, 64, 0), V(128, 128, 64));   // shares only the x=64 edge line
            var r4 = new List<Editor.Solid> { d, e };
            bool dCull = Export.FaceCuller.IsObscured(faceBy(d, V(1, 0, 0)), d, r4);
            Console.WriteLine($"[culltest] edge-touch boxes: face culled={dCull} (expect false) => {(!dCull ? "PASS" : "FAIL")}");
            return;
        }

        // Verify the texture-browser duplicate audit headlessly: MegatonHammer --deduptest
        if (args.Length >= 1 && args[0] == "--deduptest")
        {
            string dir = Path.Combine(Path.GetTempPath(), "mh_deduptest");
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            // sceneA/tex.png and sceneB/tex.png are pixel-identical; sceneA/other.png is distinct.
            void Png(string sub, string name, System.Drawing.Color c)
            {
                string sd = Path.Combine(dir, sub);
                Directory.CreateDirectory(sd);
                using var bmp = new System.Drawing.Bitmap(8, 8);
                using (var g = System.Drawing.Graphics.FromImage(bmp)) g.Clear(c);
                bmp.Save(Path.Combine(sd, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            var red  = System.Drawing.Color.FromArgb(255, 200, 30, 30);
            var blue = System.Drawing.Color.FromArgb(255, 30, 30, 200);
            Png("sceneA", "tex",   red);    // identical red, folder sceneA
            Png("sceneB", "tex",   red);    // identical red, folder sceneB  (-> name "tex#2")
            Png("sceneA", "other", blue);   // distinct blue, folder sceneA

            var lib = new Textures.TextureLibrary();
            int builtins = lib.Entries.Count;
            int added = lib.LoadFolder(dir);
            int before = lib.Entries.Count;
            int removed = lib.DedupeIdentical();
            int after = lib.Entries.Count;

            // The two identical reds collapse to one; the blue and every built-in are untouched.
            bool removedOne = removed == 1 && after == before - 1;
            // A face still referencing the dropped name resolves to the survivor.
            var surv = lib.Find("tex") ?? lib.Find("tex#2");
            var aliasOk = lib.Find("tex") != null && lib.Find("tex#2") != null
                          && ReferenceEquals(lib.Find("tex"), lib.Find("tex#2"));
            // The survivor now lists under BOTH source scenes.
            bool foldersMerged = surv != null && surv.Folders.Contains("sceneA") && surv.Folders.Contains("sceneB");
            // Distinct texture and the tool/special swatches survive.
            bool distinctKept = lib.Find("other") != null && lib.Entries.Any(e => e.Name == "other");
            bool toolKept = lib.Entries.Any(e => e.Name == Textures.SpecialTextures.Water);
            // Idempotent: a second pass removes nothing.
            int second = lib.DedupeIdentical();

            Console.WriteLine($"[deduptest] builtins={builtins} added={added} before={before} removed={removed} after={after}");
            Console.WriteLine($"[deduptest] removed-one={removedOne} alias-resolves={aliasOk} folders-merged={foldersMerged} distinct-kept={distinctKept} tool-kept={toolKept} idempotent={(second == 0)}");
            bool pass = removedOne && aliasOk && foldersMerged && distinctKept && toolKept && second == 0;
            Console.WriteLine($"[deduptest] => {(pass ? "PASS" : "FAIL")}");
            try { Directory.Delete(dir, true); } catch { }
            return;
        }

        // Verify WATERBOX brushes render as translucent OoT water (surface-only, XLU): MegatonHammer --waterrender
        if (args.Length >= 1 && args[0] == "--waterrender")
        {
            Func<float, float, float, OpenTK.Mathematics.Vector3> V = (x, y, z) => new(x, y, z);
            static bool Has(byte[] dl, ulong word)
            {
                var t = new byte[8];
                for (int i = 0; i < 8; i++) t[i] = (byte)(word >> (56 - i * 8));
                for (int o = 0; o + 8 <= dl.Length; o += 8)
                    if (dl.AsSpan(o, 8).SequenceEqual(t)) return true;
                return false;
            }
            const ulong XLU = 0xE200001C005049D8UL, COMB = 0xFC11FE23FFFFF7FBUL, PRIM = 0xFA000000FFFFFF80UL;
            const ulong SCROLL08 = 0xDE00000008000000UL;   // gsSPDisplayList(seg 0x08) — the per-frame tile scroll

            // A water-only room: a WATERBOX box from y=-32 (bottom) to y=0 (top). waterScroll: true emits the
            // segment-0x08 scroll call (paired with the scene's SDC_CALM_WATER draw config).
            var scene = new Editor.ZScene("t");
            if (scene.Rooms.Count == 0) scene.AddRoom();
            var room = scene.Rooms[0];
            var wb = Editor.Solid.CreateBox(V(0, -32, 0), V(64, 0, 64));
            foreach (var f in wb.Faces) f.TextureName = Textures.SpecialTextures.Water;
            room.Geometry.Add(wb);
            var dl = Export.DisplayListBuilder.Build(room, 0x03, 0, null, true, null, null, waterScroll: true);

            // Water now lives in the SEPARATE XLU display list (room shape xluPtr), NOT the opaque list.
            bool xlu = Has(dl.XluDlCommands, XLU), comb = Has(dl.XluDlCommands, COMB), prim = Has(dl.XluDlCommands, PRIM);
            bool scroll = Has(dl.XluDlCommands, SCROLL08);
            bool opaqueClean = !Has(dl.DlCommands, XLU);     // opaque list must not carry the water mode
            int verts = dl.VertexData.Length / 16;   // top face only = 4 corner verts

            // Control: a plain (non-water) box has NO XLU list at all.
            var scene2 = new Editor.ZScene("t2");
            if (scene2.Rooms.Count == 0) scene2.AddRoom();
            scene2.Rooms[0].Geometry.Add(Editor.Solid.CreateBox(V(0, 0, 0), V(64, 64, 64)));
            var dl2 = Export.DisplayListBuilder.Build(scene2.Rooms[0], 0x03, 0, null, true, null, null, waterScroll: true);
            bool ctrlClean = dl2.XluDlCommands.Length == 0;

            // Without waterScroll the XLU water DL must NOT contain the segment call (no crash on a target
            // with no scroll draw config).
            var dlNoScroll = Export.DisplayListBuilder.Build(room, 0x03, 0, null, true, null, null, waterScroll: false);
            bool gateOk = dlNoScroll.XluDlCommands.Length > 0 && !Has(dlNoScroll.XluDlCommands, SCROLL08);

            Console.WriteLine($"[waterrender] N64: xlu-mode={xlu} modulatei-prim={comb} prim-alpha={prim} scroll-call={scroll} opaque-clean={opaqueClean} surface-verts={verts} (expect 4) control-no-xlu={ctrlClean} scroll-gated={gateOk}");

            // OTR path (SoH/2Ship) — the separate OtrRoomGeometry builder must also render water: a water
            // surface produces a "_water" OTEX resource and is non-empty; a plain box produces none.
            var otr  = Otr.OtrRoomGeometry.Build(room, "vtx", "rmtex", null, null, null);
            var otr2 = Otr.OtrRoomGeometry.Build(scene2.Rooms[0], "vtx", "rmtex", null, null, null);
            bool otrWater = !otr.Empty && otr.Textures.Any(t => t.Path.EndsWith("_water", StringComparison.Ordinal));
            bool otrCtrl  = !otr2.Textures.Any(t => t.Path.EndsWith("_water", StringComparison.Ordinal));
            // OTR DL carries the XLU water render mode + MODULATEI_PRIM combiner (words written via U32 pairs).
            static bool HasOtr(byte[] dl, uint w0, uint w1)
            {
                for (int o = 0; o + 8 <= dl.Length; o += 4)
                    if (BitConverter.ToUInt32(dl, o) == w0 && BitConverter.ToUInt32(dl, o + 4) == w1) return true;
                return false;
            }
            // Water now lives in the SEPARATE XLU display list (mesh xlu path), not the opaque Dl.
            bool otrXlu  = HasOtr(otr.XluDl, 0xE200001Cu, 0x005049D8u) && HasOtr(otr.XluDl, 0xFC11FE23u, 0xFFFFF7FBu);
            bool otrOpaqueClean = !HasOtr(otr.Dl, 0xE200001Cu, 0x005049D8u);
            Console.WriteLine($"[waterrender] OTR: water-tex={otrWater} xlu+combiner={otrXlu} opaque-clean={otrOpaqueClean} control-no-water={otrCtrl}");

            bool pass = xlu && comb && prim && scroll && opaqueClean && verts == 4 && ctrlClean && gateOk
                        && otrWater && otrXlu && otrOpaqueClean && otrCtrl;
            Console.WriteLine($"[waterrender] => {(pass ? "PASS" : "FAIL")}");
            return;
        }

        // Extract a music sequence binary from a ROM's audioseq (cross-game music foundation):
        // MegatonHammer --seqextract <rom.z64> [seqIdHex]
        if (args.Length >= 2 && args[0] == "--seqextract")
        {
            var rom = new Rom.RomImage(args[1]);
            int seqId = args.Length >= 3 ? Convert.ToInt32(args[2], 16) : 0x18;
            var seq = Rom.AudioSeqExtractor.Extract(rom, seqId);
            Console.WriteLine($"[seqextract] {rom.Game} seq 0x{seqId:X2}: {(seq == null ? "NULL" : seq.Length + " bytes")}");
            if (seq != null) Console.WriteLine($"[seqextract] first bytes: {BitConverter.ToString(seq, 0, Math.Min(16, seq.Length))}");
            Console.WriteLine($"[seqextract] => {(seq != null && seq.Length > 16 && seq.Length < 0x40000 ? "PASS" : "FAIL")}");
            return;
        }

        // Verify per-actor model draw offsets resolve + compute: MegatonHammer --offsetcheck <ootRom.z64>
        if (args.Length >= 2 && args[0] == "--offsetcheck")
        {
            var rom = new Rom.RomImage(args[1]);
            var res = new Editor.ActorModelResolver(rom);
            (ushort id, string name)[] ids = rom.Game == Rom.RomGame.MM
                ? new (ushort, string)[]
                {
                    (0x003C,"En_Bbfall"), (0x003E,"En_Bb"), (0x0043,"En_Death"), (0x014B,"En_Pr"),
                    (0x0155,"En_Baguo"), (0x0180,"En_Pr2"), (0x0182,"En_Jso2"), (0x0224,"En_Zog"),
                    (0x00BD,"En_Ani"), (0x012B,"Boss_03"), (0x012C,"Boss_04"), (0x01EA,"En_Hakurock"),
                    (0x0156,"Obj_Vspinyroll"), (0x028B,"Obj_Milk_Bin"), (0x0290,"En_Recepgirl"),
                }
                : new (ushort, string)[]
                {
                    (0x001B,"En_Tite"), (0x001C,"En_Reeba"), (0x0027,"Boss_Dodongo"), (0x0028,"Boss_Gohma"),
                    (0x019E,"Obj_Comb"), (0x01AC,"En_Tg"), (0x01AD,"En_Mu"), (0x01D2,"Obj_Hamishi"),
                };
            foreach (var (id, name) in ids)
            {
                var a = new Editor.ZActor { Number = id, XPos = 0, YPos = 0, ZPos = 0 };
                var m = res.Resolve(a, true);
                if (m == null) { Console.WriteLine($"[offset] 0x{id:X4} {name,-13}: NO MODEL (billboard) — offset inert"); continue; }
                var off = res.ModelDrawOffset(a, m);
                Console.WriteLine($"[offset] 0x{id:X4} {name,-13}: model YES scale={m.Scale:F4} worldOffset=({off.X:F1},{off.Y:F1},{off.Z:F1})");
            }
            return;
        }

        // Probe the En_Door skeleton (gDoorSkel) limb positions: MegatonHammer --doorskel <ootRom.z64> [skelHex]
        if (args.Length >= 2 && args[0] == "--doorskel")
        {
            var rom = new Rom.RomImage(args[1]);
            int skel = args.Length >= 3 ? Convert.ToInt32(args[2], 16) : 0x10418;   // gDoorSkel (N64 NTSC-1.0)
            var ot = Rom.ObjectTable.Build(rom);
            int keepId = ot.IdOf("object_gameplay_keep") ?? 1;
            byte[]? kb = ot.GetObjectBytes(rom, keepId);
            if (kb == null) { Console.WriteLine("[doorskel] gameplay_keep not found"); return; }
            int U16(int o) => (kb[o] << 8) | kb[o + 1];
            int S16(int o) => (short)U16(o);
            int Seg6(int o) { if (o<0||o+4>kb.Length) return -1; uint a = (uint)((kb[o]<<24)|(kb[o+1]<<16)|(kb[o+2]<<8)|kb[o+3]); uint seg=a>>24; int off=(int)(a&0xFFFFFF); return (seg is 0x04 or 0x05 or 0x06) && off < kb.Length ? off : -1; }
            int limbArr = Seg6(skel); int limbCount = kb[skel + 4];
            Console.WriteLine($"[doorskel] skel@0x{skel:X} limbArr@0x{limbArr:X} limbCount={limbCount}");
            // Walk parent chain to accumulate rest-pose position (no rotation) for each limb.
            var pos = new (int x,int y,int z)[limbCount];
            var parent = new int[limbCount]; for (int i=0;i<limbCount;i++) parent[i] = -1;
            for (int i = 0; i < limbCount; i++)
            {
                int limb = Seg6(limbArr + i*4); if (limb < 0) { Console.WriteLine($"  limb {i}: <bad>"); continue; }
                int jx=S16(limb), jy=S16(limb+2), jz=S16(limb+4); int child=kb[limb+6], sib=kb[limb+7]; int dl=Seg6(limb+8);
                Console.WriteLine($"  limb {i}: joint=({jx},{jy},{jz}) child={child} sibling={sib} dList={(dl<0?"none":"0x"+dl.ToString("X"))}");
            }
            // Accumulate limb 4 by summing joints along the child hierarchy from root 0.
            (int x,int y,int z) acc = (0,0,0);
            void Walk(int idx, int px, int py, int pz)
            {
                if (idx==0xFF || idx>=limbCount) return;
                int limb = Seg6(limbArr + idx*4); if (limb<0) return;
                int ax=px+S16(limb), ay=py+S16(limb+2), az=pz+S16(limb+4);
                pos[idx]=(ax,ay,az);
                Walk(kb[limb+6], ax,ay,az);   // child inherits accumulated
                Walk(kb[limb+7], px,py,pz);    // sibling shares parent
            }
            Walk(0,0,0,0);
            for (int i=0;i<limbCount;i++) Console.WriteLine($"[doorskel] limb {i} accumulated world pos = ({pos[i].x},{pos[i].y},{pos[i].z})");
            return;
        }

        // Verify cross-game N64 music injection: MegatonHammer --seqinject <targetMmRom> <sourceOoTRom> [srcSeqHex]
        if (args.Length >= 3 && args[0] == "--seqinject")
        {
            static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
            var targetRom = new Rom.RomImage(args[1]);
            var srcRom    = new Rom.RomImage(args[2]);
            int srcSeqId  = args.Length >= 4 ? Convert.ToInt32(args[3], 16) : 0x5C;
            var srcSeq = Rom.AudioSeqExtractor.Extract(srcRom, srcSeqId);
            if (srcSeq == null) { Console.WriteLine("[seqinject] source extract FAILED"); return; }

            // RomBuilder.Decompress lays every file out decompressed at its VROM — exactly what RomInjector
            // operates on — so this exercises the real injection buffer for either target game (OoT or MM).
            byte[] dec = Rom.RomBuilder.Decompress(targetRom).Data;
            var f4 = targetRom.Files[4];
            int host = Rom.CrossGameMusic.InjectInPlace(dec, targetRom.Game, (int)f4.VromStart, f4.Size, srcSeq, srcSeqId);
            int locatedTable = Rom.CrossGameMusic.FindSeqTable(dec, targetRom.Game, f4.Size);
            Console.WriteLine($"[seqinject] located seq table @ 0x{locatedTable:X} (retail-known: OoT 0xB89AE0 / MM 0xC77B80)");
            bool ok = host >= 0 && locatedTable >= 0;
            if (ok)
            {
                int tableVrom = locatedTable;
                int hAddr = (int)U32(dec, tableVrom + host * 0x10);
                int hSize = (int)U32(dec, tableVrom + host * 0x10 + 4);
                bool sizeOk = hSize == srcSeq.Length;
                bool dataOk = dec.AsSpan((int)f4.VromStart + hAddr, srcSeq.Length).SequenceEqual(srcSeq);
                Console.WriteLine($"[seqinject] src {srcRom.Game} 0x{srcSeqId:X2} ({srcSeq.Length}B) -> {targetRom.Game} host slot 0x{host:X2} @ audioseq+0x{hAddr:X}: size-patched={sizeOk} data-written={dataOk}");
                ok = sizeOk && dataOk;
            }
            else Console.WriteLine("[seqinject] no host slot large enough");
            Console.WriteLine($"[seqinject] => {(ok ? "PASS" : "FAIL")}");
            return;
        }

        // Probe the generated idle-anim table: MegatonHammer --idleaudit [oot|mm]
        if (args.Length >= 1 && args[0] == "--idleaudit")
        {
            bool mm = args.Length >= 2 && args[1].Equals("mm", StringComparison.OrdinalIgnoreCase);
            var tbl = Rom.ActorIdleAnimTable.Build(mm);
            Console.WriteLine($"[idleaudit] {(mm ? "MM" : "OoT")}: {tbl.Count} objects with a pinned idle anim");
            foreach (var probe in new[] { "object_fsn", "object_trt", "object_horse", "object_os", "object_om", "object_zo", "object_daiku" })
                Console.WriteLine($"    {probe} -> {(tbl.OffsetFor(probe) is { } o ? $"0x{o:X}" : "(none)")}");
            return;
        }

        if (args.Length >= 1 && args[0] == "--packplaytestn64")
        {
            try { SelfTest.PlaytestPack.RunN64(args); }
            catch (Exception ex) { Console.WriteLine($"[packplaytestn64] EXCEPTION: {ex}"); }
            return;
        }

        // End-to-end editor->PJ64 playtest + logging test: runs the real Launch (inject + params + launch +
        // log link to fork log AND PJ64 trace) with MH_MAXFRAMES self-exit, then asserts the produced
        // playtest log captured every section. MegatonHammer --testplaytestlog [oot|mm] [path.mhproj]
        if (args.Length >= 1 && args[0] == "--testplaytestlog")
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            try { SelfTest.PlaytestPack.TestPlaytestLog(args); }
            catch (Exception ex) { Console.WriteLine($"[testplaytestlog] EXCEPTION: {ex}"); }
            return;
        }

        // Headless: exercise the per-playtest logging (config dump + inventory dump + engine-log tail +
        // crash capture + stop-on-close). MegatonHammer --testlog
        if (args.Length >= 1 && args[0] == "--testlog")
        {
            try
            {
                var inv = Editor.PlaytestInventory.Default(true);
                var plog = Editor.PlaytestLog.Begin("SelfTest");
                plog.Section("PLAYTEST CONFIG"); plog.Kv("game", "OoT (test)"); plog.Kv("mode", "custom");
                plog.DumpInventory("custom", inv, oot: true);
                plog.DumpObject("SCENE SETTINGS (test)", new Editor.SceneSettings());
                string fake = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mh_fakeengine.log");
                System.IO.File.WriteAllText(fake, "stale line from a previous run\n");
                plog.LinkEngine(null, new[] { fake }, idleCloseSeconds: 3);
                System.Threading.Thread.Sleep(900);   // let the tail thread capture the baseline EOF
                // Simulate the engine recreating its log at launch (truncation) then writing the run.
                System.IO.File.WriteAllText(fake, "[mh_boot] hello\n");
                System.Threading.Thread.Sleep(700);
                System.IO.File.AppendAllText(fake, "[mh_boot] RENDER OK\nException: simulated crash\n");
                System.Threading.Thread.Sleep(6500);
                Console.WriteLine($"[testlog] LogDir = {Editor.PlaytestLog.LogDir}");
                Console.WriteLine($"[testlog] file   = {plog.Path}");
            }
            catch (Exception ex) { Console.WriteLine($"[testlog] EXCEPTION: {ex}"); }
            return;
        }

        // Headless: verify the N64 inventory poke computation against SoH's known [mh_inv] applied: values.
        // MegatonHammer --testpokes
        if (args.Length >= 1 && args[0] == "--testpokes")
        {
            try
            {
                var inv = Editor.PlaytestInventory.Default(true);   // OoT Default loadout (the one SoH logs)
                var pokes = Rom.N64SavePokes.ComputeOoT(inv, "custom", out var s);
                Console.WriteLine($"[testpokes] OoT Default: healthCap={s.HealthCap} (exp 48)");
                Console.WriteLine($"[testpokes]   invEquipment=0x{s.InvEquipment:X} (exp 1133)");
                Console.WriteLine($"[testpokes]   equipsEquipment=0x{s.EquipsEquipment:X} (exp 1122)");
                Console.WriteLine($"[testpokes]   upgrades=0x{s.Upgrades:X} (exp 40)");
                Console.WriteLine($"[testpokes]   questItems=0x{s.QuestItems:X} (exp 0)");
                Console.WriteLine($"[testpokes]   buttonItem0={s.ButtonItem0} (exp 60)");
                Console.WriteLine($"[testpokes]   magicCap=0x{s.MagicCap:X} (exp 30)");
                Console.WriteLine($"[testpokes]   items[7]={s.OcarinaSlotItem} (exp 7)");
                bool ok = s.HealthCap == 48 && s.InvEquipment == 0x1133 && s.EquipsEquipment == 0x1122
                          && s.Upgrades == 0x40 && s.QuestItems == 0 && s.ButtonItem0 == 60
                          && s.MagicCap == 0x30 && s.OcarinaSlotItem == 7;
                Console.WriteLine($"[testpokes] {(ok ? "PASS — matches SoH MhApplyCustomInventory" : "FAIL — mismatch")}  ({pokes.Count} pokes)");
            }
            catch (Exception ex) { Console.WriteLine($"[testpokes] EXCEPTION: {ex}"); }
            return;
        }

        // Headless: verify the MM N64 inventory poke computation. MegatonHammer --testpokesmm
        if (args.Length >= 1 && args[0] == "--testpokesmm")
        {
            try
            {
                var inv = Editor.PlaytestInventory.Default(false);   // MM Default loadout
                var pokes = Rom.N64SavePokes.ComputeMM(inv, "custom", out var s);
                Console.WriteLine($"[testpokesmm] MM Default: healthCap={s.HealthCap} (exp 48)");
                Console.WriteLine($"[testpokesmm]   equipsEquipment=0x{s.EquipsEquipment:X} (exp 11)");
                Console.WriteLine($"[testpokesmm]   upgrades=0x{s.Upgrades:X} (exp 0)");
                Console.WriteLine($"[testpokesmm]   questItems=0x{s.QuestItems:X} (exp 1000  = song_time bit12)");
                Console.WriteLine($"[testpokesmm]   magicCap=0x{s.MagicCap:X} (exp 30)");
                Console.WriteLine($"[testpokesmm]   hasTatl={s.HasTatl} (exp 1)");
                bool ok = s.HealthCap == 48 && s.EquipsEquipment == 0x11 && s.Upgrades == 0
                          && s.QuestItems == 0x1000 && s.MagicCap == 0x30 && s.HasTatl == 1;
                Console.WriteLine($"[testpokesmm] {(ok ? "PASS — matches 2Ship MhApplyCustomInventory" : "FAIL")}  ({pokes.Count} pokes)");
            }
            catch (Exception ex) { Console.WriteLine($"[testpokesmm] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--coverage") { try { SelfTest.CoverageAudit.Run(args); } catch (Exception ex) { Console.WriteLine(ex.ToString()); } return; }

        // Headless playtest-inventory model test. MegatonHammer --testinv
        if (args.Length >= 1 && args[0] == "--testinv")
        {
            try { SelfTest.InventoryTest.Run(); }
            catch (Exception ex) { Console.WriteLine($"[testinv] EXCEPTION: {ex}"); }
            return;
        }

        // Headless iso/top-down render + actor-display audit. MegatonHammer --renderlevel [outDir]
        if (args.Length >= 1 && args[0] == "--renderlevel")
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            try { SelfTest.RenderHarness.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[renderlevel] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--renderproj")
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            try { SelfTest.RenderHarness.RenderProj(args); }
            catch (Exception ex) { Console.WriteLine($"[renderproj] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--frametime")
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            try { SelfTest.RenderHarness.FrameTime(args); }
            catch (Exception ex) { Console.WriteLine($"[frametime] EXCEPTION: {ex}"); }
            return;
        }

        // Headless: render EVERY scene in both games to levelout/{oot,mm}. MegatonHammer --renderlevels [outDir]
        if (args.Length >= 1 && args[0] == "--renderlevels")
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            try { SelfTest.RenderHarness.RenderAllLevels(args); }
            catch (Exception ex) { Console.WriteLine($"[renderlevels] EXCEPTION: {ex}"); }
            return;
        }

        // Build + structurally verify a 2-room test dungeon + inject a playable ROM. MegatonHammer --testdungeon
        if (args.Length >= 1 && args[0] == "--testdungeon")
        {
            try { SelfTest.DungeonTest.Run(); }
            catch (Exception ex) { Console.WriteLine($"[testdungeon] EXCEPTION: {ex}"); }
            return;
        }

        // Verify the injection pipeline against the OoT gc-eu-mq-dbg DEBUG ROM. MegatonHammer --testdebugrom [rom]
        if (args.Length >= 1 && args[0] == "--testdebugrom")
        {
            try { SelfTest.DebugRomTest.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[testdebugrom] EXCEPTION: {ex}"); }
            return;
        }

        // Verify multi-scene packing + sequence injection. MegatonHammer --testpack
        if (args.Length >= 1 && args[0] == "--testpack")
        {
            try { SelfTest.PackTest.Run(); }
            catch (Exception ex) { Console.WriteLine($"[testpack] EXCEPTION: {ex}"); }
            return;
        }

        // Render an imported OBJ as textured room geometry. MegatonHammer --renderobj <file.obj> [out.png]
        if (args.Length >= 1 && args[0] == "--renderobj")
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            try { SelfTest.RenderHarness.RenderObj(args); }
            catch (Exception ex) { Console.WriteLine($"[renderobj] EXCEPTION: {ex}"); }
            return;
        }

        // Verify OBJ mesh import → textured engine export. MegatonHammer --testobj
        if (args.Length >= 1 && args[0] == "--testobj")
        {
            try { SelfTest.ObjTest.Run(); }
            catch (Exception ex) { Console.WriteLine($"[testobj] EXCEPTION: {ex}"); }
            return;
        }

        // Verify faithful vanilla round-trip (retain+patch room actors). MegatonHammer --testretain [oot|mm]
        if (args.Length >= 1 && args[0] == "--testretain")
        {
            try { SelfTest.RetainTest.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[testretain] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: dump one room's vertex loads. MegatonHammer --roomdl [oot|mm] [sceneHex] [room]
        if (args.Length >= 1 && args[0] == "--roomdl")
        {
            try { SelfTest.RenderHarness.RoomDl(args); }
            catch (Exception ex) { Console.WriteLine($"[roomdl] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: decode prerendered (type-1) room backgrounds. MegatonHammer --prerender [oot|mm] [outDir]
        if (args.Length >= 1 && args[0] == "--prerender")
        {
            try { SelfTest.RenderHarness.Prerender(args); }
            catch (Exception ex) { Console.WriteLine($"[prerender] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: per-room actor lists + resolve status. MegatonHammer --actordump [oot|mm] [nameSubstr] [hexId]
        if (args.Length >= 1 && args[0] == "--textureaudit")
        {
            try { SelfTest.TextureAudit.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[textureaudit] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--msgread")
        {
            try
            {
                string romp = args.Length >= 2 ? args[1] : Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
                var rom = new Rom.RomImage(romp);
                var r = Rom.RomMessageReader.Build(rom);
                if (r == null) { Console.WriteLine("[msgread] reader null (table/data not found)"); return; }
                foreach (int id in new[] { 0x0001, 0x0002, 0x0010, 0x0100, 0x1000, 0x3040 })
                {
                    var m = r.Read(id);
                    Console.WriteLine($"0x{id:X4}: {(m == null ? "(none)" : m.Text.Replace("&", " / ").Replace("^", " || "))}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"[msgread] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--actordump")
        {
            try { SelfTest.RenderHarness.ActorDump(args); }
            catch (Exception ex) { Console.WriteLine($"[actordump] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: why a scene's walls render untextured (gray). MegatonHammer --graydiag [oot|mm] [nameSubstr]
        if (args.Length >= 1 && args[0] == "--graydiag")
        {
            try { SelfTest.RenderHarness.GrayDiag(args); }
            catch (Exception ex) { Console.WriteLine($"[graydiag] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: full ROM texture pipeline + scene categories. MegatonHammer --textures [romPath]
        if (args.Length >= 1 && args[0] == "--textures")
        {
            try { SelfTest.ModelSelfTest.TextureScan(args.Length >= 2 ? args[1] : null); }
            catch (Exception ex) { Console.WriteLine($"[textures] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: render close-ups of specific actors. MegatonHammer --renderactors [outDir]
        if (args.Length >= 1 && args[0] == "--renderactors")
        {
            try { SelfTest.RenderHarness.RenderActors(args); }
            catch (Exception ex) { Console.WriteLine($"[renderactors] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--renderdoors")
        {
            try { SelfTest.RenderHarness.RenderDoors(args); }
            catch (Exception ex) { Console.WriteLine($"[renderdoors] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--renderanim")
        {
            try { SelfTest.RenderHarness.RenderAnim(args); }
            catch (Exception ex) { Console.WriteLine($"[renderanim] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--renderbrushanim")
        {
            try { SelfTest.RenderHarness.RenderBrushAnim(args); }
            catch (Exception ex) { Console.WriteLine($"[renderbrushanim] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--testanimexport")
        {
            try { SelfTest.RenderHarness.TestAnimExport(args); }
            catch (Exception ex) { Console.WriteLine($"[testanimexport] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--logicdemos")
        {
            string dir = args.Length >= 2 ? args[1] : System.IO.Path.Combine(Editor.AppPaths.BaseDir, @"megaton_mhprojs\LogicDemos");
            try { SelfTest.LogicDemoBuilder.BuildAll(dir); }
            catch (Exception ex) { Console.WriteLine($"[logicdemos] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--diagchestskel")
        {
            try { SelfTest.ChestSkelDiag.Run(); }
            catch (Exception ex) { Console.WriteLine($"[diagchestskel] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--diagquesticons")
        {
            try { SelfTest.QuestIconDiag.Run(); }
            catch (Exception ex) { Console.WriteLine($"[diagquesticons] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--diagoverlay")
        {
            try { SelfTest.OverlayDiag.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[diagoverlay] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--texaudit")
        {
            try { SelfTest.TexAudit.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[texaudit] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--poseaudit")
        {
            try { SelfTest.PoseAudit.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[poseaudit] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--variantaudit")
        {
            try { SelfTest.VariantAudit.Run(args); }
            catch (Exception ex) { Console.WriteLine($"[variantaudit] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--rendervariants")
        {
            try { SelfTest.RenderHarness.RenderVariants(args); }
            catch (Exception ex) { Console.WriteLine($"[rendervariants] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--schedvmtest")
        {
            try { SelfTest.ScheduleVmSelfTest.Run(); }
            catch (Exception ex) { Console.WriteLine($"[schedvmtest] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--makescrolltest")
        {
            string dir = args.Length >= 2 ? args[1] : System.IO.Path.Combine(Editor.AppPaths.BaseDir, @"megaton_mhprojs\MM_ScrollTest");
            try { string p = System.IO.Path.Combine(dir, "MM_Animated_Floor.mhproj"); SelfTest.MmSystemsDemoBuilder.BuildScrollTest(p); Console.WriteLine($"[makescrolltest] wrote {p} (clean MM room, scrolling floor, no actors)"); }
            catch (Exception ex) { Console.WriteLine($"[makescrolltest] EXCEPTION: {ex}"); }
            return;
        }

        if (args.Length >= 1 && args[0] == "--leveltints")
        {
            bool mm = args.Length >= 2 && args[1].Equals("mm", StringComparison.OrdinalIgnoreCase);
            string romPath = mm ? Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).z64")
                                : Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
            try
            {
                var rom = new Rom.RomImage(romPath);
                Editor.LevelTints.SetRom(rom);
                foreach (var kv in Rom.RomAssetIndex.SceneNameToId(rom).OrderBy(k => k.Value))
                {
                    var t = Editor.LevelTints.TintFor(kv.Key);
                    if (t is { } c) Console.WriteLine($"  {kv.Key,-32} tint=({c.r:F2},{c.g:F2},{c.b:F2})");
                }
            }
            catch (Exception ex) { Console.WriteLine($"[leveltints] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: render EVERY actor in a game. MegatonHammer --renderallactors [oot|mm|both]
        if (args.Length >= 1 && args[0] == "--renderallactors")
        {
            try { SelfTest.RenderHarness.RenderAllActors(args); }
            catch (Exception ex) { Console.WriteLine($"[renderallactors] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: tile a scene category's textures into a PNG. MegatonHammer --texmontage [rom] [catSub] [out.png]
        if (args.Length >= 1 && args[0] == "--texmontage")
        {
            try { SelfTest.ModelSelfTest.TexMontage(args.Length >= 2 ? args[1] : null,
                                                    args.Length >= 3 ? args[2] : "Lost Woods",
                                                    args.Length >= 4 ? args[3] : "texmontage.png",
                                                    args.Length >= 5 ? int.Parse(args[4]) : 0,
                                                    args.Length >= 6 ? int.Parse(args[5]) : 72,
                                                    args.Length >= 7 ? int.Parse(args[6]) : 12); }
            catch (Exception ex) { Console.WriteLine($"[texmontage] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: dump F3DEX2 commands at a file offset. MegatonHammer --dldump [rom] [file] [hexOff]
        if (args.Length >= 1 && args[0] == "--dldump")
        {
            try { SelfTest.ModelSelfTest.DlDump(args.Length >= 2 ? args[1] : null,
                                                int.Parse(args[2]), Convert.ToInt32(args[3], 16)); }
            catch (Exception ex) { Console.WriteLine($"[dldump] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: tile item icons into a PNG. MegatonHammer --iconmontage [rom] [out.png]
        if (args.Length >= 1 && args[0] == "--iconmontage")
        {
            try { SelfTest.ModelSelfTest.IconMontage(args.Length >= 2 ? args[1] : null, args.Length >= 3 ? args[2] : "icons.png"); }
            catch (Exception ex) { Console.WriteLine($"[iconmontage] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: find a file by decompressed size. MegatonHammer --findfile [rom] [hexSize]
        if (args.Length >= 1 && args[0] == "--findfile")
        {
            try { SelfTest.ModelSelfTest.FindFile(args.Length >= 2 ? args[1] : null,
                                                  args.Length >= 3 ? Convert.ToInt32(args[2], 16) : 0xBF000); }
            catch (Exception ex) { Console.WriteLine($"[findfile] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: scan all ROM files for skeletal objects. MegatonHammer --scanskel [romPath]
        if (args.Length >= 1 && args[0] == "--scanskel")
        {
            try { SelfTest.ModelSelfTest.ScanSkel(args.Length >= 2 ? args[1] : null); }
            catch (Exception ex) { Console.WriteLine($"[scanskel] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: brute-force locate the gObjectTable. MegatonHammer --findtable [romPath]
        if (args.Length >= 1 && args[0] == "--findtable")
        {
            try { SelfTest.ModelSelfTest.FindTable(args.Length >= 2 ? args[1] : null); }
            catch (Exception ex) { Console.WriteLine($"[findtable] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: dump an object's decoded skeleton/DLs. MegatonHammer --dump <objectName> [romPath]
        if (args.Length >= 2 && args[0] == "--dump")
        {
            try { SelfTest.ModelSelfTest.DumpObject(args.Length >= 3 ? args[2] : null, args[1]); }
            catch (Exception ex) { Console.WriteLine($"[dump] EXCEPTION: {ex}"); }
            return;
        }

        // Diagnostic: print a display list's opcodes. MegatonHammer --dumpdl <object> <offHex> [romPath]
        if (args.Length >= 3 && args[0] == "--dumpdl")
        {
            try { SelfTest.ModelSelfTest.DumpDl(args.Length >= 4 ? args[3] : null, args[1], Convert.ToInt32(args[2].Replace("0x", ""), 16)); }
            catch (Exception ex) { Console.WriteLine($"[dumpdl] EXCEPTION: {ex}"); }
            return;
        }

        // Manage the .mhproj file association without launching the UI (installers / scripting):
        // MegatonHammer --register | --unregister
        if (args.Length >= 1 && (args[0] == "--register" || args[0] == "--unregister"))
        {
            if (args[0] == "--register") Editor.FileAssociation.EnsureRegistered();
            else Editor.FileAssociation.Unregister();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            File.AppendAllText(LogPath, $"UNHANDLED: {e.ExceptionObject}\n\n");

        Application.ThreadException += (s, e) =>
            File.AppendAllText(LogPath, $"THREAD EX: {e.Exception}\n\n");

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
            ApplicationConfiguration.Initialize();
            Editor.EditorSettings.Load();

            // #12b: fill any unset base-ROM / fork paths from known locations (ROMs validated by MD5),
            // before the game-select screen so its remembered paths are pre-populated. Configurable.
            if (Editor.EditorSettings.AutoDetectAssetsOnStartup)
            {
                try
                {
                    var det = Rom.RomFingerprint.AutoDetect();
                    foreach (var f in det.Found) Editor.DiagnosticLog.Step($"auto-detected {f}");
                    foreach (var m in det.Mismatched) Editor.DiagnosticLog.Step($"auto-detect: {m}");
                }
                catch { /* detection is best-effort; never block startup */ }
            }

            // Identify the process to the shell before any window appears, so the taskbar jump list
            // (right-click ▸ Recent Projects) attaches to our button.
            WindowsJumpList.SetAppId();

            // Keep .mhproj associated with this build for the current user (so double-clicking a
            // project opens it here and files show the app icon), unless the user opted out.
            if (Editor.EditorSettings.AssociateProjectFiles)
                Editor.FileAssociation.EnsureRegistered();

            // A project path passed on the command line (taskbar jump-list "Recent Projects" item)
            // is opened once the editor is up — the game must still be chosen first (it isn't stored
            // in the project file).
            string? pendingProject = args.FirstOrDefault(a =>
                a.EndsWith(Editor.ProjectSerializer.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(a));

            // Valve Hammer: holding Ctrl during a drag temporarily suspends grid snapping.
            Editor.GridSnap.SnapSuspended = () => (Control.ModifierKeys & Keys.Control) != 0;

            using var selectDlg = new GameSelectDialog();
            if (selectDlg.ShowDialog() != DialogResult.OK)
                return;

            // Run the editor; if the user re-picks a game target via File ▸ Close Project, the form
            // requests a relaunch (PendingGameChange) and we start a fresh MainForm in that mode —
            // a clean, startup-identical re-init rather than an in-place mutation of game state.
            var config = selectDlg.SelectedConfig;
            string? openPath = pendingProject;
            while (true)
            {
                var form = new MainForm(config, openPath);
                Application.Run(form);
                if (form.PendingGameChange == null) break;
                config   = form.PendingGameChange;
                openPath = null;   // a re-pick starts an empty project in the new mode
            }
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"MAIN EX: {ex}\n\n");
            MessageBox.Show($"Fatal error:\n{ex.Message}\n\nSee crash.log", "Megaton Hammer",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
