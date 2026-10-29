namespace Zest.Services;

/// <summary>XDG locations for Zest's config, cache and data.</summary>
public static class AppPaths
{
    public static string Home { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Config { get; } = Xdg("XDG_CONFIG_HOME", ".config");
    public static string Cache { get; } = Xdg("XDG_CACHE_HOME", ".cache");
    public static string Data { get; } = Xdg("XDG_DATA_HOME", ".local/share");

    public static string ConfigFile => Path.Combine(Config, "config.json");
    public static string StatsFile => Path.Combine(Data, "playtime.json");
    public static string ArtCache => Path.Combine(Cache, "art");
    public static string IndexCache => Path.Combine(Cache, "libretro");

    public static string ExpandHome(string path) =>
        path.StartsWith('~') ? Path.Combine(Home, path.TrimStart('~').TrimStart('/')) : path;

    private static string Xdg(string variable, string fallback)
    {
        var root = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(root))
            root = Path.Combine(Home, fallback);
        return Path.Combine(root, "zest");
    }
}
