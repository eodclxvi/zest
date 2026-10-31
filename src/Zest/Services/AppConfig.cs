using System.Text.Json;
using System.Text.Json.Serialization;
using Zest.Models;

namespace Zest.Services;

public sealed class EmulatorConfig
{
    /// <summary>Command line with a {rom} placeholder, e.g. "flatpak run org.azahar_emu.Azahar {rom}".</summary>
    public string Command { get; set; } = "";
    public List<string> Folders { get; set; } = [];
    public List<string> Excluded { get; set; } = [];

    /// <summary>3DS only: also list titles installed into Azahar itself.</summary>
    public bool IncludeInstalled { get; set; } = true;
}

/// <summary>
/// A native build of a game made on this machine (e.g. a static recompilation built from source), listed in the library
/// as its own entry and started directly instead of through an emulator.
/// </summary>
public sealed class LocalPort
{
    public string Title { get; set; } = "";
    public Platform Platform { get; set; } = Platform.Switch;
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];

    /// <summary>The folder it runs in (defaults to the executable's folder).</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>A dump of the same game to take the icon from.</summary>
    public string ArtFrom { get; set; } = "";

    /// <summary>When set, the build's output goes to this file (overwritten each run), for reading after a crash.</summary>
    public string Log { get; set; } = "";
}

public sealed class AppConfig
{
    public Dictionary<Platform, EmulatorConfig> Emulators { get; set; } = [];
    public string ProdKeys { get; set; } = "";
    public string TitleKeys { get; set; } = "";
    public bool ShowStats { get; set; } = true;
    public SortMode Sort { get; set; } = SortMode.Title;
    public ViewMode View { get; set; } = ViewMode.Grid;
    public string Theme { get; set; } = "Lemon";

    /// <summary>Game paths the user chose to hide from the library (e.g. a duplicate of an installed title).</summary>
    public HashSet<string> Hidden { get; set; } = [];

    public List<LocalPort> LocalPorts { get; set; } = [];

    public EmulatorConfig For(Platform p)
    {
        if (!Emulators.TryGetValue(p, out var e))
            Emulators[p] = e = new EmulatorConfig();
        return e;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile), Json) ?? Detect();
        }
        catch (JsonException)
        {
            // A hand-edited config that no longer parses: start over from detection rather than crash.
        }

        var detected = Detect();
        detected.Save();
        return detected;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigFile)!);
        File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Builds a config from the emulators' own settings files.</summary>
    public static AppConfig Detect()
    {
        var c = new AppConfig();
        var home = AppPaths.Home;

        var eden = Path.Combine(home, "eden/build/bin/eden");
        c.Emulators[Platform.Switch] = new EmulatorConfig
        {
            Command = $"{(File.Exists(eden) ? Quote(eden) : "eden")} -g {{rom}}",
            Folders = QtGameDirs(Path.Combine(home, ".config/eden/qt-config.ini")),
        };

        var azaharFlatpak = Directory.Exists(Path.Combine(home, ".var/app/org.azahar_emu.Azahar"));
        c.Emulators[Platform.N3DS] = new EmulatorConfig
        {
            Command = azaharFlatpak ? "flatpak run org.azahar_emu.Azahar {rom}" : "azahar {rom}",
            Folders = QtGameDirs(azaharFlatpak
                ? Path.Combine(home, ".var/app/org.azahar_emu.Azahar/config/azahar-emu/qt-config.ini")
                : Path.Combine(home, ".config/azahar-emu/qt-config.ini")),
        };

        var (ps2Folders, ps2Excluded) = Pcsx2GameDirs(Path.Combine(home, ".config/PCSX2/inis/PCSX2.ini"));
        c.Emulators[Platform.PS2] = new EmulatorConfig
        {
            // -batch closes PCSX2 when the game is shut down, so play time is tracked correctly.
            Command = "pcsx2-qt -batch -- {rom}",
            Folders = ps2Folders,
            Excluded = ps2Excluded,
        };

        foreach (var dir in new[] { ".local/share/eden/keys", ".local/share/suyu/keys", ".switch" })
        {
            var prod = Path.Combine(home, dir, "prod.keys");
            if (!File.Exists(prod))
                continue;
            c.ProdKeys = prod;
            var title = Path.Combine(home, dir, "title.keys");
            c.TitleKeys = File.Exists(title) ? title : "";
            break;
        }

        return c;
    }

    // Qt frontends (Eden, Azahar) store "Paths\gamedirs\N\path=..."; keep only real folders, not SDMC/INSTALLED.
    private static List<string> QtGameDirs(string ini)
    {
        if (!File.Exists(ini))
            return [];
        return File.ReadLines(ini)
            .Where(l => l.StartsWith(@"Paths\gamedirs\", StringComparison.Ordinal) && l.Contains(@"\path=", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf('=') + 1)..].Trim())
            .Where(p => p.StartsWith('/') && Directory.Exists(p))
            .Distinct()
            .ToList();
    }

    private static (List<string> folders, List<string> excluded) Pcsx2GameDirs(string ini)
    {
        if (!File.Exists(ini))
            return ([], []);
        List<string> folders = [], excluded = [];
        var inGameList = false;
        foreach (var raw in File.ReadLines(ini))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inGameList = line == "[GameList]";
                continue;
            }
            if (!inGameList || !line.Contains('='))
                continue;
            var key = line[..line.IndexOf('=')].Trim();
            var value = line[(line.IndexOf('=') + 1)..].Trim();
            if (key is "RecursivePaths" or "Paths" && Directory.Exists(value))
                folders.Add(value);
            else if (key == "ExcludedPaths")
                excluded.Add(value);
        }
        return (folders, excluded);
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}

public enum SortMode
{
    Title,
    RecentlyPlayed,
    MostPlayed,
    Platform,
}

public enum ViewMode
{
    Grid,
    Carousel,
}
