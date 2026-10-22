using System.IO;

namespace WallZ.Core;

internal static class WallpaperLibrary
{
    public static string BuiltInWallpaperDirectory => Path.Combine(AppContext.BaseDirectory, "wallpapers");

    // All paths cached once — avoids repeated GetFolderPath + string alloc on every call.
    private static readonly string AppDataDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WALL Z");

    internal static readonly string CustomWallpaperFile  = Path.Combine(AppDataDir, "custom-wallpapers.txt");
    internal static readonly string LastWallpaperFile    = Path.Combine(AppDataDir, "last-wallpaper.txt");
    internal static readonly string FavoritesFile        = Path.Combine(AppDataDir, "favorites.txt");
    internal static readonly string CategoriesFile       = Path.Combine(AppDataDir, "custom-categories.json");

    private static readonly Dictionary<string, string> CustomCategories = new(StringComparer.OrdinalIgnoreCase);

    static WallpaperLibrary()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            MigrateLegacyData();
            LoadCustomCategories();
        }
        catch { }
    }

    private static void MigrateLegacyData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] legacyFolders = { Path.Combine(localAppData, "WallZ"), Path.Combine(localAppData, "LivePaperLite") };
        string[] targetFiles = { "custom-wallpapers.txt", "last-wallpaper.txt", "favorites.txt" };

        foreach (var legacyDir in legacyFolders)
        {
            if (!Directory.Exists(legacyDir)) continue;
            foreach (var file in targetFiles)
            {
                var srcFile = Path.Combine(legacyDir, file);
                var destFile = Path.Combine(AppDataDir, file);
                if (File.Exists(srcFile) && !File.Exists(destFile))
                {
                    try { File.Copy(srcFile, destFile, overwrite: false); }
                    catch { }
                }
            }
        }
    }

    // ── Built-in wallpapers ────────────────────────────────────────────────

    public static IReadOnlyList<string> GetBuiltInWallpapers()
    {
        var dir = BuiltInWallpaperDirectory;
        if (!Directory.Exists(dir))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(dir, "*.mp4", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(dir, "*.webm", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => Path.GetFileNameWithoutExtension(p).Equals("soft-panels", StringComparison.OrdinalIgnoreCase)
                                 || Path.GetFileNameWithoutExtension(p).Contains("soft-panel", StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> GetCategorizedBuiltInWallpapers()
    {
        var dir = BuiltInWallpaperDirectory;
        if (!Directory.Exists(dir))
            return new Dictionary<string, IReadOnlyList<string>>();

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in GetBuiltInWallpapers())
        {
            var relativeTo = Path.GetRelativePath(dir, file);
            var parts = relativeTo.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var category = parts.Length > 1 ? ToPrettyName(parts[0]) : "All";

            if (!result.TryGetValue(category, out var list))
                result[category] = list = new List<string>();

            list.Add(file);
        }

        return result.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.AsReadOnly(),
            StringComparer.OrdinalIgnoreCase);
    }

    // ── Custom wallpapers ──────────────────────────────────────────────────

    public static IReadOnlyList<string> GetCustomWallpapers()
    {
        if (!File.Exists(CustomWallpaperFile))
            return Array.Empty<string>();

        return File.ReadLines(CustomWallpaperFile)
            .Where(IsSupportedVideo)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static void SaveCustomWallpaper(string path)
    {
        if (!IsSupportedVideo(path) || !File.Exists(path))
            return;

        var existing = GetCustomWallpapers();
        if (existing.Contains(path, StringComparer.OrdinalIgnoreCase))
            return;

        File.WriteAllLines(CustomWallpaperFile, existing.Append(path));
    }

    public static void RemoveCustomWallpapers(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0 || !File.Exists(CustomWallpaperFile))
            return;

        var removeSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = GetCustomWallpapers().Where(p => !removeSet.Contains(p)).ToArray();
        File.WriteAllLines(CustomWallpaperFile, remaining);

        if (File.Exists(LastWallpaperFile))
        {
            var last = File.ReadAllText(LastWallpaperFile).Trim();
            if (removeSet.Contains(last))
                File.Delete(LastWallpaperFile);
        }

        RemoveFavorites(paths);
    }

    // ── Favorites ──────────────────────────────────────────────────────────

    public static IReadOnlyList<string> GetFavorites()
    {
        if (!File.Exists(FavoritesFile))
            return Array.Empty<string>();

        return File.ReadLines(FavoritesFile)
            .Where(IsSupportedVideo)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsFavorite(string path) =>
        GetFavorites().Contains(path, StringComparer.OrdinalIgnoreCase);

    public static void ToggleFavorite(string path)
    {
        if (!IsSupportedVideo(path) || !File.Exists(path))
            return;

        var existing = GetFavorites().ToList();
        if (existing.Remove(path))
            File.WriteAllLines(FavoritesFile, existing);
        else
            File.WriteAllLines(FavoritesFile, existing.Append(path));
    }

    private static void RemoveFavorites(IReadOnlyCollection<string> paths)
    {
        if (!File.Exists(FavoritesFile))
            return;

        var removeSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = GetFavorites().Where(p => !removeSet.Contains(p)).ToArray();
        File.WriteAllLines(FavoritesFile, remaining);
    }

    // ── Startup / last wallpaper ───────────────────────────────────────────

    public static string? GetStartupWallpaper(
        IReadOnlyList<string> builtInWallpapers,
        IReadOnlyList<string> customWallpapers)
    {
        if (File.Exists(LastWallpaperFile))
        {
            var last = File.ReadAllText(LastWallpaperFile).Trim();
            // If the user previously had a valid wallpaper and it's not the old calm-orbit fallback, use it
            if (IsSupportedVideo(last) && File.Exists(last) && !Path.GetFileNameWithoutExtension(last).Equals("calm-orbit", StringComparison.OrdinalIgnoreCase))
                return last;
        }

        // Default wallpaper should be soft-panels
        var softPanel = builtInWallpapers.FirstOrDefault(p =>
            Path.GetFileNameWithoutExtension(p).Equals("soft-panels", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(p).Contains("soft-panel", StringComparison.OrdinalIgnoreCase));

        if (softPanel is not null)
            return softPanel;

        return builtInWallpapers.FirstOrDefault() ?? customWallpapers.FirstOrDefault();
    }

    public static void SaveLastWallpaper(string path)
    {
        if (!IsSupportedVideo(path) || !File.Exists(path))
            return;

        File.WriteAllText(LastWallpaperFile, path);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    public static bool IsSupportedVideo(string path) =>
        path.EndsWith(".mp4",  StringComparison.OrdinalIgnoreCase)
     || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);

    public static string ToDisplayName(string path)
    {
        var title = Path.GetFileNameWithoutExtension(path).Replace('-', ' ').Replace('_', ' ');
        return string.Join(' ',
            title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                 .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static string ToPrettyName(string folderName) => ToDisplayName(folderName);

    // ── Category System ────────────────────────────────────────────────────

    private static void LoadCustomCategories()
    {
        try
        {
            if (File.Exists(CategoriesFile))
            {
                var json = File.ReadAllText(CategoriesFile);
                var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (map != null)
                {
                    foreach (var kvp in map)
                        CustomCategories[kvp.Key] = kvp.Value;
                }
            }
        }
        catch { }
    }

    public static void SaveCategory(string path, string category)
    {
        try
        {
            CustomCategories[path] = category;
            var json = System.Text.Json.JsonSerializer.Serialize(CustomCategories, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CategoriesFile, json);
        }
        catch { }
    }

    public static string DetectCategory(string filePath)
    {
        if (CustomCategories.TryGetValue(filePath, out var customCat) && !string.IsNullOrWhiteSpace(customCat))
            return customCat;

        var fileName = Path.GetFileName(filePath).ToLowerInvariant();
        var folderName = Path.GetFileName(Path.GetDirectoryName(filePath) ?? "").ToLowerInvariant();
        var combined = $"{folderName}/{fileName}";

        // Anime keywords
        if (ContainsAny(combined, "goku", "dragon", "ball", "makima", "chainsaw", "yuta", "okkotsu", "gojo", "satoru", 
            "jujutsu", "kaisen", "sukuna", "frieren", "naruto", "luffy", "zoro", "tanjiro", "nezuko", "demon", "slayer", 
            "titan", "eren", "levi", "bleach", "ichigo", "evangelion", "asuka", "shinji", "ghibli", "totoro", "manga", 
            "otaku", "waifu", "genshin", "honkai", "star rail", "raiden", "hollow", "instinct", "kakarot", "super saiyan", 
            "anime", "curse", "spirit"))
        {
            return "Anime";
        }

        // Cyberpunk / Sci-Fi keywords
        if (ContainsAny(combined, "cyber", "punk", "neon", "futuristic", "synthwave", "retrowave", "tokyo", "night city", 
            "blade runner", "glitch", "matrix", "mech", "scifi", "sci-fi", "invincible", "thragg", "omni-man", "robot", 
            "android", "dystopia", "rainy cyberpunk"))
        {
            return "Cyberpunk";
        }

        // Atmosphere / Nature / Landscape keywords
        if (ContainsAny(combined, "atmosphere", "ocean", "water", "sea", "lake", "rain", "rainy", "sunset", "sunrise", 
            "twilight", "horizon", "sky", "clouds", "mountain", "forest", "trees", "mist", "fog", "meadow", "nature", 
            "windmills", "evening", "floating", "car", "drive", "landscape", "calm", "ripples", "spring", "stratospheric"))
        {
            return "Atmosphere";
        }

        // Minimal / Clean / Abstract keywords
        if (ContainsAny(combined, "minimal", "minimalist", "orbit", "panels", "soft", "clean", "geometry", "lines", 
            "abstract", "dark", "amoled", "gradient", "monochrome", "simple", "prana", "system"))
        {
            return "Minimal";
        }

        // Gaming keywords
        if (ContainsAny(combined, "game", "gaming", "minecraft", "hunt", "showdown", "spider-man", "spiderman", 
            "halo", "witcher", "elden", "ring", "souls", "csgo", "valorant", "fortnite", "apex", "silver surfer"))
        {
            return "Gaming";
        }

        return "Curated";
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        foreach (var kw in keywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
