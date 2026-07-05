using System.IO.Compression;
using System.Text;
using MegatonHammer.Editor;
using MegatonHammer.Otr;
using MegatonHammer.Rom;

namespace MegatonHammer.Export;

/// <summary>Playtest options written into the mod O2R for the engine to read.</summary>
public sealed class PlaytestConfig
{
    /// <summary>Scene slot the level is written into in-game (warp target).</summary>
    public int TargetSceneId { get; set; } = 0x52;
    /// <summary>Append mode: the target is a spare/unused dev-test slot (no real scene clobbered) and
    /// the boot hook patches a spare entrance to reach it. False = replace an existing scene.</summary>
    public bool Append { get; set; }
    public bool Adult { get; set; }                       // false = child
    /// <summary>"empty" | "debug" | "custom" — debug = OoT title-screen save inventory.</summary>
    public string Inventory { get; set; } = "debug";
    /// <summary>Item keys granted in "custom" inventory mode (legacy quick list; kept for compat).</summary>
    public List<string> CustomItems { get; set; } = [];
    /// <summary>Full custom inventory payload (hearts/tiers/toggles) the boot hook applies when
    /// Inventory == "custom". JSON object, or null. See <see cref="Editor.PlaytestInventory.ToPayloadJson"/>.</summary>
    public string? InventoryPayload { get; set; }
    /// <summary>#20: user-facing level name shown at the tail of the SoH/2Ship debug warp list so the
    /// injected scene can be re-warped to manually. Defaults to "Megaton Project".</summary>
    public string DisplayName { get; set; } = "Megaton Project";
}

/// <summary>
/// Packs the editor's raw N64 scene + room binaries into a Megaton Hammer mod O2R
/// (a ZIP) that the (modified) SoH/2Ship engine loads, converts, and warps to. The raw
/// blobs live under "mh/"; "mh/info" carries the room count + playtest settings.
/// Any existing target file is backed up to "&lt;path&gt;.mhbak".
/// </summary>
public static class O2RPacker
{
    public const string SceneEntry = "mh/scene";
    public const string InfoEntry  = "mh/info";
    public static string RoomEntry(int i) => $"mh/room_{i}";

    /// <summary>Builds the scene binaries from the document and packs them into <paramref name="o2rPath"/>.</summary>
    public static void Pack(ZScene scene, string o2rPath, PlaytestConfig cfg)
    {
        // n64Hw: false — SoH/2Ship render the room DLs through libultraship Fast3D, not real RDP silicon,
        // so use the Fast3D-validated geometry/render-mode/alpha bytes (decoupled from the N64 path).
        var (sceneBytes, rooms) = SceneExporter.BuildBinaries(scene, n64Hw: false);
        Pack(o2rPath, sceneBytes, rooms, cfg);
    }

    public static void Pack(string o2rPath, byte[] sceneBytes, IReadOnlyList<byte[]> rooms, PlaytestConfig cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(o2rPath)!);

        // Back up an existing mod O2R (keep one backup).
        if (File.Exists(o2rPath))
        {
            string bak = o2rPath + ".mhbak";
            File.Copy(o2rPath, bak, overwrite: true);
            File.Delete(o2rPath);
        }

        using var zip = ZipFile.Open(o2rPath, ZipArchiveMode.Create);
        AddEntry(zip, SceneEntry, sceneBytes);
        for (int i = 0; i < rooms.Count; i++)
            AddEntry(zip, RoomEntry(i), rooms[i]);

        string items = string.Join(",", cfg.CustomItems.Select(s => $"\"{s}\""));
        string info =
            "{" +
            $"\"rooms\":{rooms.Count}," +
            $"\"sceneId\":{cfg.TargetSceneId}," +
            $"\"adult\":{(cfg.Adult ? "true" : "false")}," +
            $"\"append\":{(cfg.Append ? "true" : "false")}," +
            $"\"inventory\":\"{cfg.Inventory}\"," +
            $"\"items\":[{items}]," +
            $"\"displayName\":\"{JsonEscape(cfg.DisplayName)}\"," +
            $"\"inv\":{cfg.InventoryPayload ?? "null"}" +
            "}";
        AddEntry(zip, InfoEntry, Encoding.UTF8.GetBytes(info));
    }

    /// <summary>Packs MULTIPLE custom scenes into one playtest O2R, each at its own reserved append
    /// slot (mh_append_0, mh_append_1, …). The mh/info "scenes" manifest lists them so the fork's boot
    /// hook can register a scene-table + entrance entry per slot and let you warp to any. Reserved slots
    /// are bounded by the fork (MH_APPEND_COUNT); excess scenes are dropped with a log note.</summary>
    public static void PackOtrMulti(IReadOnlyList<ZScene> scenes, string o2rPath, PlaytestConfig cfg,
                                    bool mm = false, Func<string, System.Drawing.Bitmap?>? texResolver = null)
    {
        const int MaxAppendSlots = 16;   // must match the fork's reserved block
        int n = Math.Min(scenes.Count, MaxAppendSlots);
        if (scenes.Count > MaxAppendSlots)
            Editor.DiagnosticLog.Step($"WARNING: {scenes.Count} scenes > {MaxAppendSlots} reserved slots; packing first {MaxAppendSlots}.");

        Directory.CreateDirectory(Path.GetDirectoryName(o2rPath)!);
        if (File.Exists(o2rPath)) { File.Copy(o2rPath, o2rPath + ".mhbak", overwrite: true); File.Delete(o2rPath); }

        var manifest = new System.Text.StringBuilder();
        using (var zip = ZipFile.Open(o2rPath, ZipArchiveMode.Create))
        {
            for (int i = 0; i < n; i++)
            {
                string slot = mm ? $"scenes/nonmq/mh_append_{i}/mh_append_{i}" : $"scenes/shared/mh_append_{i}/mh_append_{i}";
                var resources = OtrSceneWriter.BuildLevel(scenes[i], slot, mm, texResolver);
                foreach (var r in resources) AddEntry(zip, r.Path, r.Data);
                Editor.DiagnosticLog.Step($"scene {i}: {resources.Count} resource(s) at {slot}");
                if (i > 0) manifest.Append(',');
                string sname = scenes[i].Name.Replace("\"", "");
                manifest.Append($"{{\"slot\":{i},\"name\":\"{sname}\",\"rooms\":{scenes[i].Rooms.Count}}}");
            }
            string items = string.Join(",", cfg.CustomItems.Select(s => $"\"{s}\""));
            string info =
                "{" +
                $"\"format\":\"otr\",\"game\":\"{(mm ? "mm" : "oot")}\",\"append\":true,\"multi\":true," +
                $"\"appendCount\":{n}," +
                $"\"adult\":{(cfg.Adult ? "true" : "false")}," +
                $"\"inventory\":\"{cfg.Inventory}\",\"items\":[{items}]," +
                $"\"displayName\":\"{JsonEscape(cfg.DisplayName)}\"," +
                $"\"inv\":{cfg.InventoryPayload ?? "null"}," +
                $"\"scenes\":[{manifest}]" +
                "}";
            AddEntry(zip, InfoEntry, Encoding.UTF8.GetBytes(info));
        }
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var s = entry.Open();
        s.Write(data, 0, data.Length);
    }

    // ── Native OTR path (preferred) ─────────────────────────────────────────

    /// <summary>
    /// Packs the level as native libultraship resources that OVERRIDE the chosen scene's
    /// archive paths, so SoH/2Ship loads it with no change to the resource pipeline — the
    /// engine only needs to warp there. The scene/room/collision/geometry resources are
    /// written at <c>scenes/{ver}/{name}/{name}…</c> for the target slot; "mh/info" carries
    /// the warp + playtest settings for the small boot hook to read.
    /// </summary>
    public static void PackOtr(ZScene scene, string o2rPath, PlaytestConfig cfg, bool masterQuest = false, bool mm = false,
                               Func<string, System.Drawing.Bitmap?>? texResolver = null)
    {
        // Append writes the level at the reserved scene's resource path. The Megaton Hammer fork adds a
        // genuinely new scene to each game's (grown) scene table — OoT: SCENE_MH_APPEND at
        // scenes/shared/mh_append_scene/mh_append_scene; MM: SCENE_MH_APPEND at
        // scenes/nonmq/mh_append/mh_append — so no existing scene is overridden. Replace mode targets a
        // real scene slot's path.
        string? basePath = cfg.Append
            ? (mm ? "scenes/nonmq/mh_append/mh_append" : "scenes/shared/mh_append_scene/mh_append_scene")
            : (mm ? MmSceneFiles.ScenePath(cfg.TargetSceneId)
                  : OotSceneFiles.ScenePath(cfg.TargetSceneId, masterQuest));
        Editor.DiagnosticLog.Step($"scene resource base path: {basePath ?? "(none)"}");
        if (basePath == null)
            throw new ArgumentOutOfRangeException(nameof(cfg), $"Scene id 0x{cfg.TargetSceneId:X2} has no resource path.");

        var resources = OtrSceneWriter.BuildLevel(scene, basePath, mm, texResolver);
        Editor.DiagnosticLog.Step($"built {resources.Count} native resource(s) overriding {basePath}");

        Directory.CreateDirectory(Path.GetDirectoryName(o2rPath)!);
        if (File.Exists(o2rPath))
        {
            File.Copy(o2rPath, o2rPath + ".mhbak", overwrite: true);
            File.Delete(o2rPath);
        }

        using (var zip = ZipFile.Open(o2rPath, ZipArchiveMode.Create))
        {
            foreach (var r in resources)
                AddEntry(zip, r.Path, r.Data);

            string version = mm ? "mm" : (cfg.Append ? "shared" : OotSceneFiles.Version(cfg.TargetSceneId, masterQuest));
            string name    = cfg.Append ? (mm ? "mh_append" : "mh_append_scene")
                : ((mm ? MmSceneFiles.Name(cfg.TargetSceneId) : OotSceneFiles.Name(cfg.TargetSceneId)) ?? "");
            string items = string.Join(",", cfg.CustomItems.Select(s => $"\"{s}\""));
            string weekEvents = string.Join(",", scene.Settings.StartWeekEvents);
            string persistentWeekEvents = string.Join(",", scene.Settings.PersistentWeekEvents);
            string info =
                "{" +
                $"\"format\":\"otr\"," +
                $"\"game\":\"{(mm ? "mm" : "oot")}\"," +
                $"\"weekEvents\":[{weekEvents}]," +
                $"\"persistentWeekEvents\":[{persistentWeekEvents}]," +
                $"\"timeOfDay\":{scene.Settings.PlaytestTimeOfDay}," +
                $"\"sceneId\":{cfg.TargetSceneId}," +
                $"\"masterQuest\":{(masterQuest ? "true" : "false")}," +
                $"\"version\":\"{version}\"," +
                $"\"name\":\"{name}\"," +
                $"\"displayName\":\"{JsonEscape(cfg.DisplayName)}\"," +
                $"\"rooms\":{scene.Rooms.Count}," +
                $"\"adult\":{(cfg.Adult ? "true" : "false")}," +
                $"\"append\":{(cfg.Append ? "true" : "false")}," +
                // OoT only: the fork sets the scene draw config to SDC_CALM_WATER so the water surface scrolls.
                $"\"waterScroll\":{(!mm && Export.DisplayListBuilder.SceneHasWater(scene) ? "true" : "false")}," +
                $"\"inventory\":\"{cfg.Inventory}\"," +
                $"\"items\":[{items}]," +
                $"\"inv\":{cfg.InventoryPayload ?? "null"}" +
                "}";
            AddEntry(zip, InfoEntry, Encoding.UTF8.GetBytes(info));

            // MM NPC schedules (custom-engine convention): the 2Ship fork reads mh/schedules and
            // overrides each matching actor's position/facing by the in-game clock. OoT has no schedules.
            if (mm)
            {
                string sched = BuildSchedulesJson(scene);
                if (sched != "[]") AddEntry(zip, "mh/schedules", Encoding.UTF8.GetBytes(sched));

                // MM schedule bytecode VM (decomp z_schedule.c): actors carrying an authored ScheduleProgram.
                // Each entry ships the compiled bytecode (hex) + a pose table; the 2Ship fork runs it through
                // the engine's own Schedule_RunScript every frame and places the actor at poses[result].
                string schedVm = BuildScheduleVmJson(scene);
                if (schedVm != "[]") AddEntry(zip, "mh/schedule_vm", Encoding.UTF8.GetBytes(schedVm));
            }

            // Authored dialogue (Message Bank): the SoH/2Ship fork reads mh/messages at boot, registers
            // each id with the engine's custom-message system, and supplies the text via an OnOpenText
            // hook when an actor opens that textId. Applies to both games.
            if (scene.Messages.Any(m => m.IsOverride))   // only CUSTOM overrides; Default (vanilla) messages are left alone
                AddEntry(zip, "mh/messages", Encoding.UTF8.GetBytes(BuildMessagesJson(scene)));
        }
    }

    // ── Vanilla-SoH level export (a plain .o2r level mod, no playtest boot metadata) ────────────────────
    // A regular SoH loads scene resources by path from any mounted .o2r, so writing our native OTR scene at a
    // vanilla scene's resource path makes SoH render our level in that scene's place — no fork needed. The
    // user just walks into that scene in-game. Multiple levels (each at its own scene path) coexist in one
    // .o2r as a level pack.

    /// <summary>The vanilla resource base path a scene loads from ("scenes/.../name/name"), or null.</summary>
    public static string? VanillaScenePath(int sceneId, bool mm, bool masterQuest = false) =>
        mm ? MmSceneFiles.ScenePath(sceneId) : OotSceneFiles.ScenePath(sceneId, masterQuest);

    /// <summary>Builds the native OTR resources (scene / rooms / collision / textures) that override a vanilla
    /// scene slot, for a plain vanilla-SoH level mod.</summary>
    public static List<OtrSceneWriter.OtrResource> BuildVanillaSceneResources(
        ZScene scene, int sceneId, bool mm, bool masterQuest, Func<string, System.Drawing.Bitmap?>? texResolver)
    {
        string basePath = VanillaScenePath(sceneId, mm, masterQuest)
            ?? throw new ArgumentOutOfRangeException(nameof(sceneId), $"Scene id 0x{sceneId:X2} has no resource path.");
        return OtrSceneWriter.BuildLevel(scene, basePath, mm, texResolver);
    }

    /// <summary>All entry paths in an .o2r (empty if the file is absent), for conflict inspection.</summary>
    public static HashSet<string> ListEntries(string o2rPath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(o2rPath)) return set;
        using var zip = ZipFile.OpenRead(o2rPath);
        foreach (var e in zip.Entries) set.Add(e.FullName);
        return set;
    }

    /// <summary>Writes (merge=false) or updates (merge=true) a vanilla-SoH .o2r with one level override. In
    /// merge mode the level is ADDED to the existing archive (a multi-level pack); existing entries whose path
    /// collides with the new resources are replaced and returned (non-empty ⇒ the same scene was already
    /// overridden). A .mhbak backup of the prior archive is kept.</summary>
    public static List<string> WriteLevelO2R(string o2rPath, IReadOnlyList<OtrSceneWriter.OtrResource> res, bool merge)
    {
        var overwritten = new List<string>();
        Directory.CreateDirectory(Path.GetDirectoryName(o2rPath)!);
        bool exists = File.Exists(o2rPath);
        if (exists) File.Copy(o2rPath, o2rPath + ".mhbak", overwrite: true);

        if (!merge || !exists)
        {
            if (exists) File.Delete(o2rPath);
            using var zip = ZipFile.Open(o2rPath, ZipArchiveMode.Create);
            foreach (var r in res) AddEntry(zip, r.Path, r.Data);
            return overwritten;
        }

        using (var zip = ZipFile.Open(o2rPath, ZipArchiveMode.Update))
        {
            var newPaths = new HashSet<string>(res.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries.ToList())
                if (newPaths.Contains(e.FullName)) { overwritten.Add(e.FullName); e.Delete(); }
            foreach (var r in res) AddEntry(zip, r.Path, r.Data);
        }
        return overwritten;
    }

    /// <summary>JSON array of authored messages for the fork: id (textId), box type / position / icon,
    /// and the friendly-markup text (&amp; newline, ^ new box, %r/%g/%b/%y/%w/%p colour) which the fork's
    /// custom-message converter lowers to engine control bytes.</summary>
    private static string BuildMessagesJson(ZScene scene)
    {
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var m in scene.Messages.Where(m => m.IsOverride))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('{')
              .Append($"\"id\":{m.Id},\"type\":{m.BoxType},\"pos\":{m.YPos},\"icon\":{m.Icon},")
              .Append($"\"text\":\"{JsonEscape(m.Text)}\"")
              .Append('}');
        }
        return sb.Append(']').ToString();
    }

    private static string JsonEscape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}"); else sb.Append(c);
                    break;
            }
        return sb.ToString();
    }

    /// <summary>JSON array of scheduled actors for the fork: each entry identifies the actor by room +
    /// id + placed position, and carries its time/day position rules.</summary>
    private static string BuildSchedulesJson(ZScene scene)
    {
        var sb = new StringBuilder("[");
        bool first = true;
        for (int room = 0; room < scene.Rooms.Count; room++)
            foreach (var a in scene.Rooms[room].Actors)
            {
                if (a.Schedule is not { Count: > 0 } rules || a.IsEditorOnly) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{')
                  .Append($"\"room\":{room},\"id\":{a.Number},")
                  .Append($"\"x\":{(int)MathF.Round(a.XPos)},\"y\":{(int)MathF.Round(a.YPos)},\"z\":{(int)MathF.Round(a.ZPos)},")
                  .Append("\"rules\":[");
                for (int i = 0; i < rules.Count; i++)
                {
                    var r = rules[i];
                    if (i > 0) sb.Append(',');
                    sb.Append('{')
                      .Append($"\"day\":{r.Day},\"sh\":{r.StartHour},\"sm\":{r.StartMin},\"eh\":{r.EndHour},\"em\":{r.EndMin},")
                      .Append($"\"x\":{(int)MathF.Round(r.X)},\"y\":{(int)MathF.Round(r.Y)},\"z\":{(int)MathF.Round(r.Z)},\"yaw\":{r.Yaw}")
                      .Append('}');
                }
                sb.Append("]}");
            }
        return sb.Append(']').ToString();
    }

    /// <summary>mh/schedule_vm: each actor with an authored <see cref="ScheduleProgram"/> → its compiled
    /// bytecode (hex) + a pose table the engine result indexes into. The fork identifies the live actor by
    /// id nearest the placed (x,y,z), runs the bytecode via the engine's Schedule_RunScript, and applies
    /// poses[output.result] (or hides the actor when the script returns nothing).</summary>
    private static string BuildScheduleVmJson(ZScene scene)
    {
        var sb = new StringBuilder("[");
        bool first = true;
        for (int room = 0; room < scene.Rooms.Count; room++)
            foreach (var a in scene.Rooms[room].Actors)
            {
                if (a.ScheduleVm is not { IsEmpty: false } prog || a.IsEditorOnly) continue;
                byte[] bytecode = prog.Assemble();
                string hex = Convert.ToHexString(bytecode);
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{')
                  .Append($"\"room\":{room},\"id\":{a.Number},")
                  .Append($"\"x\":{(int)MathF.Round(a.XPos)},\"y\":{(int)MathF.Round(a.YPos)},\"z\":{(int)MathF.Round(a.ZPos)},")
                  .Append($"\"code\":\"{hex}\",\"poses\":[");
                var poses = a.SchedulePoses ?? new();
                for (int i = 0; i < poses.Count; i++)
                {
                    var p = poses[i];
                    if (i > 0) sb.Append(',');
                    sb.Append('{')
                      .Append($"\"x\":{(int)MathF.Round(p.X)},\"y\":{(int)MathF.Round(p.Y)},\"z\":{(int)MathF.Round(p.Z)},\"yaw\":{p.Yaw}")
                      .Append('}');
                }
                sb.Append("]}");
            }
        return sb.Append(']').ToString();
    }
}
