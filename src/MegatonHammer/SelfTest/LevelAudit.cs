using MegatonHammer.Editor;
using MegatonHammer.Export;
using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Headless whole-game fidelity audit. For every scene in OoT and MM it walks the real ROM
/// header (scene + every room + every alternate setup), classifies each header command as
/// PARSED (becomes editable), IGNORED (dropped on import) or LOST (parsed but not re-emitted on
/// export), validates the warp data (spawns / exits / transition actors), and round-trip-compiles
/// the scene through SceneExporter to confirm it builds. Run: MegatonHammer --audit [oot|mm|both] .
/// </summary>
public static class LevelAudit
{
    private static readonly string OotRom = Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
    private static readonly string MmRom  = Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).z64");

    private static readonly Dictionary<byte, string> CmdName = new()
    {
        [0x00] = "spawnList",   [0x01] = "actorList",    [0x02] = "actorCutscene", [0x03] = "collision",
        [0x04] = "roomList",    [0x05] = "windSettings", [0x06] = "entranceList",  [0x07] = "specialFiles",
        [0x08] = "roomBehavior",[0x09] = "undef09",      [0x0A] = "mesh",          [0x0B] = "objectList",
        [0x0C] = "unused0C",    [0x0D] = "pathList",     [0x0E] = "transitions",   [0x0F] = "lightSettings",
        [0x10] = "timeSettings",[0x11] = "skybox",       [0x12] = "skyboxMod",     [0x13] = "exitList",
        [0x14] = "endHeader",   [0x15] = "soundSettings",[0x16] = "echoSettings",  [0x17] = "cutscene",
        [0x18] = "altHeaders",  [0x19] = "censorFlag",   [0x1A] = "texAnimMM",     [0x1B] = "actorCsListMM",
        [0x1C] = "minimapMM",   [0x1D] = "unk1D",        [0x1E] = "minimapChestMM",[0x1F] = "regionVisitMM",
    };

    // What the importer actually reads into the editable document (from SceneImporter switch tables).
    private static readonly HashSet<byte> ParsedScene = [0x00, 0x03, 0x04, 0x07, 0x0D, 0x0E, 0x0F, 0x11, 0x13, 0x15, 0x17, 0x18];
    private static readonly HashSet<byte> ParsedRoom  = [0x01, 0x0A, 0x0B];
    // What the exporter writes back (from SceneExporter / RoomExporter).
    private static readonly HashSet<byte> ExportScene = [0x00, 0x03, 0x04, 0x06, 0x07, 0x0D, 0x0E, 0x0F, 0x11, 0x13, 0x14, 0x15, 0x17];
    private static readonly HashSet<byte> ExportRoom  = [0x01, 0x08, 0x0A, 0x10, 0x12, 0x14, 0x16];

    public static void Run(string[] a)
    {
        string mode = a.Length >= 2 ? a[1].ToLowerInvariant() : "both";
        if (mode != "mm") AuditGame("OoT (SoH)", OotRom);
        if (mode != "oot") AuditGame("MM (2Ship)", MmRom);
    }

    private static void AuditGame(string label, string romPath)
    {
        Console.WriteLine($"\n================  AUDIT: {label}  ================");
        RomImage rom;
        try { rom = new RomImage(romPath); }
        catch (Exception ex) { Console.WriteLine($"  cannot open ROM: {ex.Message}"); return; }

        IEnumerable<int> ids = rom.Game == RomGame.MM
            ? MmSceneFiles.All.Select(t => t.Id)
            : Enumerable.Range(0, 128).Where(OotSceneFiles.IsValid);

        // Aggregates: command -> (# scenes/rooms present). Plus incongruity tallies.
        var sceneCmdCount = new SortedDictionary<byte, int>();
        var roomCmdCount  = new SortedDictionary<byte, int>();
        int scenes = 0, roomsTotal = 0, importFail = 0, buildFail = 0, rtSettingsMismatch = 0, transRtMismatch = 0, envRtMismatch = 0, pathRtMismatch = 0, waterRtMismatch = 0;
        int withSetups = 0, withMultiEnv = 0, withCutscene = 0, withPaths = 0, unmappedExits = 0, totalExits = 0;

        foreach (int id in ids)
        {
            ImportedScene? s;
            try { s = SceneImporter.Import(rom, id); }
            catch (Exception ex) { Console.WriteLine($"  [{id:X2}] IMPORT EXCEPTION: {ex.Message}"); importFail++; continue; }
            if (s == null) continue;
            scenes++;

            // Walk the real scene header + every alternate setup header.
            byte[] sd; try { sd = rom.GetFile(s.SceneFileIndex); } catch { continue; }
            var sceneCmds = WalkHeader(sd, 0);
            foreach (var setup in s.Setups) sceneCmds.UnionWith(WalkHeader(sd, setup.HeaderOffset));

            // Walk every room header.
            var roomCmds = new HashSet<byte>();
            foreach (var r in s.Rooms)
            {
                roomsTotal++;
                try { roomCmds.UnionWith(WalkHeader(rom.GetFile(r.FileIndex), 0)); } catch { }
            }

            foreach (var c in sceneCmds) sceneCmdCount[c] = sceneCmdCount.GetValueOrDefault(c) + 1;
            foreach (var c in roomCmds)  roomCmdCount[c]  = roomCmdCount.GetValueOrDefault(c) + 1;

            // Incongruity tallies.
            if (s.Setups.Count > 0) withSetups++;
            if (s.Lights.Count > 1) withMultiEnv++;
            if (sceneCmds.Contains(0x17)) withCutscene++;
            if (sceneCmds.Contains(0x0D)) withPaths++;
            int unmapped = s.Exits.Count(e => e.EntranceIndex == 0);
            unmappedExits += unmapped; totalExits += s.Exits.Count;

            // Per-scene LOST (parsed but not exported) / IGNORED (present but not parsed) flags.
            var lost = sceneCmds.Where(c => c != 0x14 && ParsedScene.Contains(c) && !ExportScene.Contains(c)).ToList();
            var ignored = sceneCmds.Where(c => c != 0x14 && !ParsedScene.Contains(c) && c != 0x01).ToList();
            var roomLost = roomCmds.Where(c => c != 0x14 && ParsedRoom.Contains(c) && !ExportRoom.Contains(c)).ToList();
            var roomIgnored = roomCmds.Where(c => c != 0x14 && !ParsedRoom.Contains(c)).ToList();

            // Round-trip compile + verify room settings survive (re-parse the exported room headers).
            string build;
            try
            {
                var (scn, rms) = SceneExporter.BuildBinaries(BuildDoc(s));
                int mism = 0;
                for (int i = 0; i < s.Rooms.Count && i < rms.Count; i++)
                    if (!RoomSettingsMatch(s.Rooms[i], rms[i])) mism++;
                rtSettingsMismatch += mism;
                int outTrans = CountSceneTransitions(scn);
                if (outTrans != s.Transitions.Count) transRtMismatch++;
                int outEnv = CountSceneEnvs(scn);
                if (outEnv != Math.Max(1, s.Lights.Count)) envRtMismatch++;
                int outPaths = CountSceneCmd(scn, 0x0D);
                if (outPaths != s.Paths.Count) pathRtMismatch++;
                if (CountSceneWaterBoxes(scn) != s.WaterBoxes.Count) waterRtMismatch++;
                build = scn.Length > 0 && rms.Count == Math.Max(1, s.Rooms.Count)
                    ? $"OK {scn.Length}b/{rms.Count}rm{(mism > 0 ? $" rtMism={mism}" : "")}{(outTrans != s.Transitions.Count ? $" tr{s.Transitions.Count}->{outTrans}" : "")}" : "THIN";
            }
            catch (Exception ex) { build = "FAIL:" + ex.Message; buildFail++; }

            int actors = s.Rooms.Sum(r => r.Actors.Count);
            string setupStr = s.Setups.Count > 0 ? $" setups={s.Setups.Count}[{string.Join('/', s.Setups.Select(x => x.Name))}]" : "";
            Console.WriteLine($"  [{id:X2}] {s.Name,-26} rm={s.Rooms.Count} act={actors} trans={s.Transitions.Count} " +
                $"spawn={s.Spawns.Count} env={s.Lights.Count} exit={s.Exits.Count}(unmapped={unmapped}){setupStr} | build={build}");
            if (lost.Count + roomLost.Count > 0)
                Console.WriteLine($"        LOST on recompile: {Names(lost.Concat(roomLost))}");
            if (ignored.Count + roomIgnored.Count > 0)
                Console.WriteLine($"        IGNORED on import: {Names(ignored.Concat(roomIgnored))}");
        }

        // ── Aggregate report ──────────────────────────────────────────────
        Console.WriteLine($"\n  ── {label} totals: {scenes} scenes, {roomsTotal} rooms, importFail={importFail}, buildFail={buildFail}, roomSettingsRtMismatch={rtSettingsMismatch}, transitionRtMismatch={transRtMismatch}, envRtMismatch={envRtMismatch}, pathRtMismatch={pathRtMismatch}, waterboxRtMismatch={waterRtMismatch} ──");
        Console.WriteLine($"  warps: {totalExits} collision exits ({unmappedExits} unmapped), scenes with alt setups={withSetups}, " +
            $"multi-env={withMultiEnv}, cutscene cmd={withCutscene}, path list={withPaths}");
        Console.WriteLine("  SCENE header commands across the game (present / parsed / exported):");
        foreach (var (c, n) in sceneCmdCount)
            Console.WriteLine($"    0x{c:X2} {CmdName.GetValueOrDefault(c, "?"),-16} in {n,3} scenes  " +
                $"{Status(ParsedScene.Contains(c) || c == 0x01, ExportScene.Contains(c) || c == 0x14)}");
        Console.WriteLine("  ROOM header commands across the game (present / parsed / exported):");
        foreach (var (c, n) in roomCmdCount)
            Console.WriteLine($"    0x{c:X2} {CmdName.GetValueOrDefault(c, "?"),-16} in {n,3} rooms   " +
                $"{Status(ParsedRoom.Contains(c), ExportRoom.Contains(c) || c == 0x14)}");
    }

    private static string Status(bool parsed, bool exported) =>
        parsed && exported ? "PRESERVED" : parsed ? "LOST (parsed, not re-emitted)" : exported ? "synthesized" : "IGNORED (dropped on import)";

    private static string Names(IEnumerable<byte> cmds) =>
        string.Join(", ", cmds.Distinct().OrderBy(c => c).Select(c => $"0x{c:X2}:{CmdName.GetValueOrDefault(c, "?")}"));

    // Walk an 8-byte-command header from `start` until the 0x14 end command; collect command ids.
    private static HashSet<byte> WalkHeader(byte[] d, int start)
    {
        var set = new HashSet<byte>();
        for (int p = start, n = 0; p + 8 <= d.Length && n < 64; p += 8, n++)
        {
            byte c = d[p];
            set.Add(c);
            if (c == 0x14) break;
        }
        return set;
    }

    // Re-parse an exported scene binary's 0x0E command for its transition count (round-trip check).
    private static int CountSceneTransitions(byte[] scn)
    {
        for (int p = 0; p + 8 <= scn.Length; p += 8)
        {
            byte c = scn[p];
            if (c == 0x14) break;
            if (c == 0x0E) return scn[p + 1];
        }
        return 0;
    }

    // Re-parse an exported scene binary's 0x0F command for its environment count (round-trip check).
    private static int CountSceneEnvs(byte[] scn) => CountSceneCmd(scn, 0x0F);

    // Follow the 0x03 collision command to the header and read numWaterBoxes (@ +0x24).
    private static int CountSceneWaterBoxes(byte[] scn)
    {
        for (int p = 0; p + 8 <= scn.Length; p += 8)
        {
            if (scn[p] == 0x14) break;
            if (scn[p] != 0x03) continue;
            int colOff = ((scn[p + 5] << 16) | (scn[p + 6] << 8) | scn[p + 7]);   // seg-2 offset
            return colOff + 0x26 <= scn.Length ? ((scn[colOff + 0x24] << 8) | scn[colOff + 0x25]) : 0;
        }
        return 0;
    }

    // Returns the count byte of a scene header command (p+1), or 0 if the command is absent.
    private static int CountSceneCmd(byte[] scn, byte cmd)
    {
        for (int p = 0; p + 8 <= scn.Length; p += 8)
        {
            byte c = scn[p];
            if (c == 0x14) break;
            if (c == cmd) return scn[p + 1];
        }
        return 0;
    }

    // Re-parse an exported room binary's settings and compare to the imported source (round-trip check).
    private static bool RoomSettingsMatch(ImportedRoom src, byte[] bin)
    {
        byte beh = 0, ts = 0, echo = 0; ushort time = 0xFFFF; bool inv = false, dsky = false, dsun = false;
        for (int p = 0; p + 8 <= bin.Length; p += 8)
        {
            byte c = bin[p];
            if (c == 0x14) break;
            switch (c)
            {
                case 0x08: beh = bin[p + 1]; inv = bin[p + 6] != 0; break;
                case 0x10: time = (ushort)((bin[p + 4] << 8) | bin[p + 5]); ts = bin[p + 6]; break;
                case 0x12: dsky = bin[p + 4] != 0; dsun = bin[p + 5] != 0; break;
                case 0x16: echo = bin[p + 7]; break;
            }
        }
        return beh == src.BehaviorType && inv == src.ShowInvisibleActors && time == src.TimeOverride
            && ts == src.TimeSpeed && dsky == src.DisableSkybox && dsun == src.DisableSunMoon && echo == src.Echo;
    }

    // Build a minimal editor document from an imported scene (actors + settings) for the compile test.
    private static ZScene BuildDoc(ImportedScene s)
    {
        var zs = new ZScene(s.Name);
        while (zs.Rooms.Count < Math.Max(1, s.Rooms.Count)) zs.AddRoom();
        for (int i = 0; i < s.Rooms.Count; i++)
        {
            var ir = s.Rooms[i];
            var zr = zs.Rooms[i];
            zr.Settings.BehaviorType = ir.BehaviorType;
            zr.Settings.ShowInvisibleActors = ir.ShowInvisibleActors;
            zr.Settings.TimeOverride = ir.TimeOverride;
            zr.Settings.TimeSpeed = ir.TimeSpeed;
            zr.Settings.DisableSkybox = ir.DisableSkybox;
            zr.Settings.DisableSunMoon = ir.DisableSunMoon;
            zr.Settings.Echo = ir.Echo;
            foreach (var act in ir.Actors)
                zr.Actors.Add(new ZActor
                {
                    Number = act.Id, Variable = act.Params,
                    XPos = act.X, YPos = act.Y, ZPos = act.Z,
                    XRot = act.RX, YRot = act.RY, ZRot = act.RZ,
                });
        }
        // Transition actors (scene 0x0E) → IsTransition placements in the spawn room.
        foreach (var t in s.Transitions)
            zs.Rooms[0].Actors.Add(new ZActor
            {
                Number = t.Id, Variable = t.Params, XPos = t.X, YPos = t.Y, ZPos = t.Z, YRot = t.RY,
                IsTransition = true,
                FrontRoom = t.FrontRoom, FrontEffect = t.FrontEffect, BackRoom = t.BackRoom, BackEffect = t.BackEffect,
            });
        // Exit-trigger brushes (collision walk-into warps) → a 0x13 list on export.
        foreach (var ex in s.Exits)
        {
            var trig = Solid.CreateBox(ex.Min, ex.Max);
            trig.IsTrigger = true; trig.ExitEntrance = ex.EntranceIndex;
            zs.Rooms[0].Geometry.Add(trig);
        }
        zs.Environments = [.. s.Lights];   // all lighting environments (0x0F)
        zs.Paths = s.Paths.Select(p => new ZPath(p.Points)
        { AdditionalPathIndex = p.AdditionalPathIndex, CustomValue = p.CustomValue }).ToList();   // 0x0D paths
        zs.CutsceneData = s.CutsceneData; zs.CutsceneOrigOff = s.CutsceneOrigOff;   // 0x17 retention
        foreach (var wb in s.WaterBoxes)   // collision waterboxes → water brushes
        {
            var water = Solid.CreateBox(new OpenTK.Mathematics.Vector3(wb.X, wb.Y - 100, wb.Z),
                                        new OpenTK.Mathematics.Vector3(wb.X + wb.XLen, wb.Y, wb.Z + wb.ZLen));
            water.IsWater = true; water.WaterRoom = wb.Room;
            zs.Rooms[0].Geometry.Add(water);
        }
        var st = zs.Settings;
        st.SkyboxId = s.SkyboxId; st.MusicSeq = s.MusicSeq; st.NightSfx = s.NightSfx;
        if (s.Spawns.Count > 0)
        {
            var sp = s.Spawns[0];
            st.SpawnPos = new(sp.X, sp.Y, sp.Z); st.SpawnYaw = sp.RY;
        }
        return zs;
    }
}
