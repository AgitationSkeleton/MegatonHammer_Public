namespace MegatonHammer.Rom;

/// <summary>
/// OoT scene id → internal resource file name (e.g. 0x51 → "spot00_scene"), matching SoH's
/// scene_table.h DEFINE_SCENE order. Used to build the archive path SoH loads a scene from
/// (<c>scenes/{version}/{name}/{name}</c>) so a mod O2R can override that scene with a
/// custom level. The "version" folder is "shared" for everything except the MQ-capable
/// dungeons, which use "nonmq"/"mq" — exactly as OTRPlay_SpawnScene decides it.
/// </summary>
public static class OotSceneFiles
{
    private static readonly string[] Files =
    [
        "ydan_scene","ddan_scene","bdan_scene","Bmori1_scene","HIDAN_scene","MIZUsin_scene",
        "jyasinzou_scene","HAKAdan_scene","HAKAdanCH_scene","ice_doukutu_scene","ganon_scene","men_scene",
        "gerudoway_scene","ganontika_scene","ganon_sonogo_scene","ganontikasonogo_scene","takaraya_scene","ydan_boss_scene",
        "ddan_boss_scene","bdan_boss_scene","moribossroom_scene","FIRE_bs_scene","MIZUsin_bs_scene","jyasinboss_scene",
        "HAKAdan_bs_scene","ganon_boss_scene","ganon_final_scene","entra_scene","entra_n_scene","enrui_scene",
        "market_alley_scene","market_alley_n_scene","market_day_scene","market_night_scene","market_ruins_scene","shrine_scene",
        "shrine_n_scene","shrine_r_scene","kokiri_home_scene","kokiri_home3_scene","kokiri_home4_scene","kokiri_home5_scene",
        "kakariko_scene","kakariko3_scene","shop1_scene","kokiri_shop_scene","golon_scene","zoora_scene",
        "drag_scene","alley_shop_scene","night_shop_scene","face_shop_scene","link_home_scene","impa_scene",
        "malon_stable_scene","labo_scene","hylia_labo_scene","tent_scene","hut_scene","daiyousei_izumi_scene",
        "yousei_izumi_tate_scene","yousei_izumi_yoko_scene","kakusiana_scene","hakaana_scene","hakaana2_scene","hakaana_ouke_scene",
        "syatekijyou_scene","tokinoma_scene","kenjyanoma_scene","hairal_niwa_scene","hairal_niwa_n_scene","hiral_demo_scene",
        "hakasitarelay_scene","turibori_scene","nakaniwa_scene","bowling_scene","souko_scene","miharigoya_scene",
        "mahouya_scene","ganon_demo_scene","kinsuta_scene","spot00_scene","spot01_scene","spot02_scene",
        "spot03_scene","spot04_scene","spot05_scene","spot06_scene","spot07_scene","spot08_scene",
        "spot09_scene","spot10_scene","spot11_scene","spot12_scene","spot13_scene","spot15_scene",
        "spot16_scene","spot17_scene","spot18_scene","spot20_scene","ganon_tou_scene","test01_scene",
        "besitu_scene","depth_test_scene","syotes_scene","syotes2_scene","sutaru_scene","hairal_niwa2_scene",
        "sasatest_scene","testroom_scene",
    ];

    public static int Count => Files.Length;

    // Reverse map: internal scene-folder name ("ganon_scene") → friendly name ("Ganon Boss"),
    // for labelling O2R/OTR texture categories the way the vanilla-ROM path does.
    private static readonly Dictionary<string, string> Friendly = BuildFriendly();
    private static Dictionary<string, string> BuildFriendly()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Files.Length; i++) d[Files[i]] = OotSceneNames.Pretty(i);
        return d;
    }
    /// <summary>Friendly scene name for an internal scene-folder name, or null if not a known scene.</summary>
    public static string? FriendlyName(string folderName) => Friendly.GetValueOrDefault(folderName);

    public static bool IsValid(int sceneId) => sceneId >= 0 && sceneId < Files.Length;

    /// <summary>Internal resource name for a scene id, or null if out of range.</summary>
    public static string? Name(int sceneId) => IsValid(sceneId) ? Files[sceneId] : null;

    // MQ-capable dungeons (scenes 0x00..0x09, Gerudo Training Ground 0x0B, Inside Ganon's Castle 0x0D).
    private static bool IsDungeon(int sceneId) =>
        (sceneId >= 0x00 && sceneId <= 0x09) || sceneId == 0x0B || sceneId == 0x0D;

    public static string Version(int sceneId, bool masterQuest = false) =>
        IsDungeon(sceneId) ? (masterQuest ? "mq" : "nonmq") : "shared";

    /// <summary>The archive path stem SoH loads this scene from (also the scene resource path).</summary>
    public static string? ScenePath(int sceneId, bool masterQuest = false)
    {
        var name = Name(sceneId);
        return name == null ? null : $"scenes/{Version(sceneId, masterQuest)}/{name}/{name}";
    }
}
