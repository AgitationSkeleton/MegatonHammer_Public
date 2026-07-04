using System.Text.Json;
using MegatonHammer.Rom;
using MegatonHammer.Textures;

namespace MegatonHammer.Editor;

/// <summary>
/// Per-scene texture tint: the dominant hue of a level's baked vertex colouring, used to preview that
/// level's textures in the browser the way they actually look in-game (e.g. the Lost Woods' blue-green
/// cast) instead of flat grayscale. The hue for a scene is computed once — by importing it and averaging
/// its room geometry's vertex colours — then cached in memory AND on disk (per game), so it's never
/// recomputed. Wired into TextureEntry.CategoryTint; gated by EditorSettings.PerLevelTextureTint.
/// </summary>
public static class LevelTints
{
    private static RomImage? _rom;
    private static bool _mm;
    private static Dictionary<string, int>? _cache;      // category → packed RGB (0 = computed, no tint)
    private static Dictionary<string, int>? _nameToId;   // category → scene id

    /// <summary>Point the provider at the ROM whose textures the browser is showing (call when it loads).</summary>
    public static void SetRom(RomImage rom)
    {
        if (_rom == rom) return;
        _rom = rom; _mm = rom.Game == RomGame.MM;
        _cache = Load(); _nameToId = null;
        TextureEntry.CategoryTint = TintFor;
    }

    /// <summary>The RGB multiplier (0..1) for a category, or null for no tint / feature off.</summary>
    public static (float r, float g, float b)? TintFor(string category)
    {
        if (_rom == null || !EditorSettings.PerLevelTextureTint) return null;
        var cache = _cache ??= Load();
        if (!cache.TryGetValue(category, out int rgb))
        {
            rgb = Compute(category);
            cache[category] = rgb;
            Save(cache);
        }
        if (rgb == 0) return null;
        return (((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
    }

    // Import the scene behind this category, average its room vertex colours, and normalise to a pure hue
    // (max channel = 1) so neutral scenes don't tint and coloured scenes show their cast without darkening.
    private static int Compute(string category)
    {
        if (_rom == null) return 0;
        _nameToId ??= RomAssetIndex.SceneNameToId(_rom);
        if (!_nameToId.TryGetValue(category, out int sceneId)) return 0;
        try
        {
            var lvl = ImportedLevel.Load(_rom, sceneId);
            if (lvl == null) return 0;
            double r = 0, g = 0, b = 0; long n = 0;
            foreach (var mesh in lvl.RoomMeshes)
                foreach (var t in mesh)
                {
                    r += t.C0.X + t.C1.X + t.C2.X; g += t.C0.Y + t.C1.Y + t.C2.Y; b += t.C0.Z + t.C1.Z + t.C2.Z; n += 3;
                }
            if (n == 0) return 0;
            float ar = (float)(r / n), ag = (float)(g / n), ab = (float)(b / n);
            float mx = MathF.Max(ar, MathF.Max(ag, ab));
            if (mx < 0.02f) return 0;                          // black/no colour → no tint
            ar /= mx; ag /= mx; ab /= mx;                      // normalise to pure hue
            if (ar > 0.96f && ag > 0.96f && ab > 0.96f) return 0;   // ~neutral → no tint
            int Pack(float v) => Math.Clamp((int)(v * 255f + 0.5f), 1, 255);
            return (Pack(ar) << 16) | (Pack(ag) << 8) | Pack(ab);
        }
        catch { return 0; }
    }

    private static string CacheFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "MegatonHammer", $"leveltints_{(_mm ? "mm" : "oot")}.json");

    private static Dictionary<string, int> Load()
    {
        try
        {
            var f = CacheFile();
            if (File.Exists(f))
                return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(f)) ?? new();
        }
        catch { /* fall through to empty */ }
        return new();
    }

    private static void Save(Dictionary<string, int> cache)
    {
        try
        {
            var f = CacheFile();
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, JsonSerializer.Serialize(cache));
        }
        catch { /* best-effort cache */ }
    }
}
