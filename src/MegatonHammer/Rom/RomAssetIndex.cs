namespace MegatonHammer.Rom;

/// <summary>
/// Maps ROM file indices to a friendly category (the scene a file belongs to) by reading
/// gSceneTable and each scene's room list. Used to group extracted textures into folders.
/// </summary>
public static class RomAssetIndex
{
    public const string Common = "Objects & Common";

    /// <summary>Scene/room file → scene name, plus room file → its scene file index.</summary>
    public sealed class AssetMap
    {
        public Dictionary<int, string> FileScene = [];
        public Dictionary<int, int>    RoomToSceneFile = [];   // room file index → scene file index
    }

    /// <summary>Builds fileIndex → scene-name for scene and room files (OoT or MM).</summary>
    public static Dictionary<int, string> Build(RomImage rom) => BuildMap(rom).FileScene;

    /// <summary>Reverse of the scene categorisation: friendly category name → scene id (the same names
    /// used for the texture browser's folders), so a category can be resolved back to a scene to import.</summary>
    public static Dictionary<string, int> SceneNameToId(RomImage rom)
    {
        var d = new Dictionary<string, int>();
        var entries = rom.Game == RomGame.MM
            ? MmSceneFiles.All.Select(t => (t.Id, PrettyMm(t.Name)))
            : Enumerable.Range(0, OotSceneNames.Count).Select(id => (id, OotSceneNames.Pretty(id)));
        foreach (var (id, name) in entries) d[name] = id;
        return d;
    }

    /// <summary>Builds the full asset map (scene/room categories + room→scene-file links).</summary>
    public static AssetMap BuildMap(RomImage rom)
    {
        var map = new AssetMap();
        if (rom.Game == RomGame.Unknown) return map;

        // OoT and MM differ in the scene-table entry size and the locator fingerprint.
        bool mm = rom.Game == RomGame.MM;
        int entrySize = mm ? 0x10 : SceneTableLocator.EntrySize;

        // Locate gSceneTable: it lives in the code file, found by the fingerprint scan.
        byte[]? code = null;
        int tableOff = -1;
        foreach (var f in rom.Files)
        {
            if (!f.Exists || f.Size < entrySize * 14) continue;
            var bytes = rom.GetFile(f.Index);
            var loc = mm ? SceneTableLocator.FindMM(bytes, rom.Files, rom)
                         : SceneTableLocator.Find(bytes, rom.Files);
            if (loc.Offset >= 0) { code = bytes; tableOff = loc.Offset; break; }
        }
        if (code == null) return map;

        var fileByVrom = new Dictionary<uint, int>();
        foreach (var f in rom.Files) if (f.Exists) fileByVrom[f.VromStart] = f.Index;

        // Scene id → friendly name. MM's table has gaps, so iterate its known ids. MM's raw names are
        // "Z2_LOST_WOODS"-style; #9 makes them human/searchable ("Lost Woods") so the texture filter
        // matches by area term (e.g. searching "woods" surfaces the Lost Woods textures).
        IEnumerable<(int id, string name)> entries = mm
            ? MmSceneFiles.All.Select(t => (t.Id, PrettyMm(t.Name)))
            : Enumerable.Range(0, OotSceneNames.Count).Select(id => (id, OotSceneNames.Pretty(id)));

        foreach (var (id, name) in entries)
        {
            int o = tableOff + id * entrySize;
            if (o + 8 > code.Length) continue;

            uint sceneVrom = U32(code, o);
            if (sceneVrom == 0) continue;
            if (!fileByVrom.TryGetValue(sceneVrom, out int sceneIdx)) continue;

            map.FileScene[sceneIdx] = name;

            // Map the scene's room files (room-list command 0x04) to the same name + scene file.
            var sceneData = rom.GetFile(sceneIdx);
            foreach (uint roomVrom in ReadRoomList(sceneData))
                if (fileByVrom.TryGetValue(roomVrom, out int roomIdx))
                {
                    map.FileScene[roomIdx] = name;
                    map.RoomToSceneFile[roomIdx] = sceneIdx;
                }
        }
        return map;
    }

    // "Z2_LOST_WOODS" → "Lost Woods"; "Z2_20SICHITAI2" → "Sichitai 2". Strips the Z2_ prefix and any
    // leading digit cluster, then title-cases the words (keeps area terms like "woods" intact so the
    // texture search matches them). Falls back to the raw name if it doesn't fit the pattern.
    private static string PrettyMm(string raw)
    {
        string s = raw.StartsWith("Z2_", StringComparison.OrdinalIgnoreCase) ? raw[3..] : raw;
        var words = s.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i];
            // Split a leading digit run into its own token ("20SICHITAI2" → "20" + "SICHITAI2").
            int d = 0; while (d < w.Length && char.IsDigit(w[d])) d++;
            string digits = w[..d], rest = w[d..];
            string pretty = rest.Length == 0 ? digits
                : (digits.Length > 0 ? digits + " " : "") + char.ToUpperInvariant(rest[0]) + rest[1..].ToLowerInvariant();
            words[i] = pretty;
        }
        string result = string.Join(' ', words).Trim();
        return result.Length == 0 ? raw : result;
    }

    // Reads the {vromStart,...} list the scene's 0x04 header command points at.
    private static IEnumerable<uint> ReadRoomList(byte[] scene)
    {
        int limit = Math.Min(scene.Length, 0x200);
        for (int o = 0; o + 8 <= limit; o += 8)
        {
            byte op = scene[o];
            if (op == 0x14) break;             // end of header
            if (op != 0x04) continue;

            int count = scene[o + 1];
            int listOff = (int)(U32(scene, o + 4) & 0x00FFFFFF);
            for (int i = 0; i < count; i++)
            {
                int e = listOff + i * 8;
                if (e + 8 > scene.Length) yield break;
                yield return U32(scene, e);
            }
            yield break;
        }
    }

    private static uint U32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
}
