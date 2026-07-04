namespace MegatonHammer.Rom;

/// <summary>
/// Majora's Mask scene id → internal resource name (e.g. 0x08 → "SPOT00"), from MM's
/// scene_table.h. 2Ship loads scenes from <c>scenes/nonmq/{NAME}/{NAME}</c> (all MM scenes
/// live under "nonmq"), so a mod O2R can override one with a custom level — the MM analogue
/// of <see cref="OotSceneFiles"/>. Used by the 2Ship playtest path and the MM-compat audit.
/// </summary>
public static class MmSceneFiles
{
    // Real (non-UNSET) MM scenes; gaps are unused slots.
    private static readonly Dictionary<int, string> Names = new()
    {
        [0x00]="Z2_20SICHITAI2", [0x07]="KAKUSIANA", [0x08]="SPOT00", [0x0A]="Z2_WITCH_SHOP",
        [0x0B]="Z2_LAST_BS", [0x0C]="Z2_HAKASHITA", [0x0D]="Z2_AYASHIISHOP", [0x10]="Z2_OMOYA",
        [0x11]="Z2_BOWLING", [0x12]="Z2_SONCHONOIE", [0x13]="Z2_IKANA", [0x14]="Z2_KAIZOKU",
        [0x15]="Z2_MILK_BAR", [0x16]="Z2_INISIE_N", [0x17]="Z2_TAKARAYA", [0x18]="Z2_INISIE_R",
        [0x19]="Z2_OKUJOU", [0x1A]="Z2_OPENINGDAN", [0x1B]="Z2_MITURIN", [0x1C]="Z2_13HUBUKINOMITI",
        [0x1D]="Z2_CASTLE", [0x1E]="Z2_DEKUTES", [0x1F]="Z2_MITURIN_BS", [0x20]="Z2_SYATEKI_MIZU",
        [0x21]="Z2_HAKUGIN", [0x22]="Z2_ROMANYMAE", [0x23]="Z2_PIRATE", [0x24]="Z2_SYATEKI_MORI",
        [0x25]="Z2_SINKAI", [0x26]="Z2_YOUSEI_IZUMI", [0x27]="Z2_KINSTA1", [0x28]="Z2_KINDAN2",
        [0x29]="Z2_TENMON_DAI", [0x2A]="Z2_LAST_DEKU", [0x2B]="Z2_22DEKUCITY", [0x2C]="Z2_KAJIYA",
        [0x2D]="Z2_00KEIKOKU", [0x2E]="Z2_POSTHOUSE", [0x2F]="Z2_LABO", [0x30]="Z2_DANPEI2TEST",
        [0x32]="Z2_16GORON_HOUSE", [0x33]="Z2_33ZORACITY", [0x34]="Z2_8ITEMSHOP", [0x35]="Z2_F01",
        [0x36]="Z2_INISIE_BS", [0x37]="Z2_30GYOSON", [0x38]="Z2_31MISAKI", [0x39]="Z2_TAKARAKUJI",
        [0x3B]="Z2_TORIDE", [0x3C]="Z2_FISHERMAN", [0x3D]="Z2_GORONSHOP", [0x3E]="Z2_DEKU_KING",
        [0x3F]="Z2_LAST_GORON", [0x40]="Z2_24KEMONOMITI", [0x41]="Z2_F01_B", [0x42]="Z2_F01C",
        [0x43]="Z2_BOTI", [0x44]="Z2_HAKUGIN_BS", [0x45]="Z2_20SICHITAI", [0x46]="Z2_21MITURINMAE",
        [0x47]="Z2_LAST_ZORA", [0x48]="Z2_11GORONNOSATO2", [0x49]="Z2_SEA", [0x4A]="Z2_35TAKI",
        [0x4B]="Z2_REDEAD", [0x4C]="Z2_BANDROOM", [0x4D]="Z2_11GORONNOSATO", [0x4E]="Z2_GORON_HAKA",
        [0x4F]="Z2_SECOM", [0x50]="Z2_10YUKIYAMANOMURA", [0x51]="Z2_TOUGITES", [0x52]="Z2_DANPEI",
        [0x53]="Z2_IKANAMAE", [0x54]="Z2_DOUJOU", [0x55]="Z2_MUSICHOUSE", [0x56]="Z2_IKNINSIDE",
        [0x57]="Z2_MAP_SHOP", [0x58]="Z2_F40", [0x59]="Z2_F41", [0x5A]="Z2_10YUKIYAMANOMURA2",
        [0x5B]="Z2_14YUKIDAMANOMITI", [0x5C]="Z2_12HAKUGINMAE", [0x5D]="Z2_17SETUGEN", [0x5E]="Z2_17SETUGEN2",
        [0x5F]="Z2_SEA_BS", [0x60]="Z2_RANDOM", [0x61]="Z2_YADOYA", [0x62]="Z2_KONPEKI_ENT",
        [0x63]="Z2_INSIDETOWER", [0x64]="Z2_26SARUNOMORI", [0x65]="Z2_LOST_WOODS", [0x66]="Z2_LAST_LINK",
        [0x67]="Z2_SOUGEN", [0x68]="Z2_BOMYA", [0x69]="Z2_KYOJINNOMA", [0x6A]="Z2_KOEPONARACE",
        [0x6B]="Z2_GORONRACE", [0x6C]="Z2_TOWN", [0x6D]="Z2_ICHIBA", [0x6E]="Z2_BACKTOWN",
        [0x6F]="Z2_CLOCKTOWER", [0x70]="Z2_ALLEY",
    };

    public static int Count => Names.Count;

    // Reverse map: internal scene name ("Z2_TOWN") → friendly label ("Clock Town"), reusing the
    // SceneNames.xml-backed Pretty(), for labelling O2R texture categories like the vanilla path.
    private static readonly Lazy<Dictionary<string, string>> FriendlyByName = new(() =>
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Names) d[kv.Value] = Pretty(kv.Key);
        return d;
    });
    /// <summary>Friendly label for an internal MM scene name, or null if unknown.</summary>
    public static string? FriendlyName(string folderName) => FriendlyByName.Value.GetValueOrDefault(folderName);

    public static bool IsValid(int sceneId) => Names.ContainsKey(sceneId);

    public static string? Name(int sceneId) => Names.GetValueOrDefault(sceneId);

    /// <summary>Friendly display name — prefers SharpOcarina's SceneNames.xml (e.g. "Stone Tower
    /// Temple (Inverted)"), falling back to the de-prefixed internal name.</summary>
    public static string Pretty(int sceneId)
    {
        if (Friendly.Value.TryGetValue(sceneId, out var f)) return f;
        var n = Name(sceneId);
        if (n == null) return $"Scene 0x{sceneId:X2}";
        return n.StartsWith("Z2_") ? n[3..] : n;
    }

    // SceneNames.xml is keyed by scene-table id; "NULL" entries are unused slots (skipped).
    private static readonly Lazy<Dictionary<int, string>> Friendly = new(() =>
    {
        var d = new Dictionary<int, string>();
        string path = System.IO.Path.Combine(MegatonHammer.Editor.AppPaths.Sources ?? MegatonHammer.Editor.AppPaths.BaseDir, @"SharpOcarina-main\XML\MM\SceneNames.xml");
        try
        {
            if (File.Exists(path))
                foreach (var el in System.Xml.Linq.XDocument.Load(path).Root!.Elements("Scene"))
                {
                    int id = Convert.ToInt32((string)el.Attribute("Key")!, 16);
                    string name = el.Value.Trim();
                    if (name.Length > 0 && !name.Equals("NULL", StringComparison.OrdinalIgnoreCase) && Names.ContainsKey(id))
                        d[id] = name;
                }
        }
        catch { /* fall back to internal names */ }
        return d;
    });

    /// <summary>The 2Ship archive path stem this scene loads from (also the resource path).</summary>
    public static string? ScenePath(int sceneId)
    {
        var name = Name(sceneId);
        return name == null ? null : $"scenes/nonmq/{name}/{name}";
    }

    public static IEnumerable<(int Id, string Name)> All =>
        Names.OrderBy(kv => kv.Key).Select(kv => (kv.Key, Pretty(kv.Key)));
}
