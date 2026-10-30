using PKForge.Domain;
using PKForge.Infrastructure;
using Zest.Models;

namespace Zest.Services.Forge;

/// <summary>A Pokémon save PKForge can open, found in Eden's NAND or Azahar's SD card.</summary>
/// <param name="Key">"switch:&lt;title id&gt;" or "3ds:&lt;low title id&gt;", used to match library games.</param>
public sealed record PokemonSave(string Path, string Key, Platform Platform, SaveDescription Description, DateTime Modified)
{
    public string GameName => Description.GameName;
    public string Label => $"Pokémon {Description.GameName}";
}

/// <summary>
/// Finds Pokémon saves where Zest's emulators keep them, so a library game opens its own save
/// without a file picker:
///   Eden:   nand/user/save/0000000000000000/&lt;profile&gt;/&lt;title id&gt;/main
///   Azahar: sdmc/Nintendo 3DS/&lt;id0&gt;/&lt;id1&gt;/title/00040000/&lt;low id&gt;/data/00000001/main
/// </summary>
public static class SaveLocator
{
    // Pokémon titles on Switch (the same list PKForge labels Eden saves with).
    private static readonly HashSet<string> SwitchTitleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "010003F003A34000", "0100187003A36000", "0100ABF008968000", "01008DB008C2C000", "0100000011D90000",
        "010018E011D92000", "01001F5010B28000", "0100A3D008C5C000", "01008F6008C5E000", "0100F43008C44000",
    };

    public static IReadOnlyList<PokemonSave> Scan(ISaveEngine engine)
    {
        var found = new List<PokemonSave>();
        foreach (var titleDir in EdenTitleDirs())
        {
            var titleId = Path.GetFileName(titleDir);
            if (!SwitchTitleIds.Contains(titleId))
                continue;
            var file = Directory.EnumerateFiles(titleDir)
                .Where(f => EmulatorSaveHeuristics.IsEdenSaveFileName(Path.GetFileName(f)))
                .OrderBy(f => Path.GetFileName(f) == "main" ? 0 : 1)
                .FirstOrDefault();
            if (file is not null && Describe(engine, file, "switch:" + titleId.ToUpperInvariant(), Platform.Switch) is { } save)
                found.Add(save);
        }

        foreach (var titleDir in AzaharTitleDirs())
        {
            var file = Path.Combine(titleDir, "data", "00000001", "main");
            if (File.Exists(file) && Describe(engine, file, "3ds:" + Path.GetFileName(titleDir).ToLowerInvariant(), Platform.N3DS) is { } save)
                found.Add(save);
        }

        // Several Eden profiles can hold the same game: keep the one played most recently.
        return [.. found.GroupBy(s => s.Key).Select(g => g.MaxBy(s => s.Modified)!).OrderBy(s => s.Description.Generation).ThenBy(s => s.GameName)];
    }

    /// <summary>The save belonging to a library game: 3DS by title id, Switch (and unreadable 3DS files) by name.</summary>
    public static PokemonSave? ForGame(Game game, IReadOnlyList<PokemonSave> saves)
    {
        if (game.Platform == Platform.N3DS && N3dsKey(game) is { } key && saves.FirstOrDefault(s => s.Key == key) is { } exact)
            return exact;
        // "Pokémon Sword" yes, "The Legend of Zelda Skyward Sword HD" no.
        var title = game.Title.Replace('é', 'e');
        if (!title.Contains("Pokemon", StringComparison.OrdinalIgnoreCase))
            return null;
        return saves.Where(s => s.Platform == game.Platform)
            .FirstOrDefault(s => System.Text.RegularExpressions.Regex.IsMatch(title,
                $@"\b{System.Text.RegularExpressions.Regex.Escape(s.GameName)}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    private static string? N3dsKey(Game game)
    {
        // Installed titles live at .../title/00040000/<low id>/content/<n>.app.
        if (game.IsInstalled)
            return "3ds:" + Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(game.Path)))?.ToLowerInvariant();
        try
        {
            return N3dsMetadata.Read(game.Path) is { } info ? $"3ds:{(uint)info.TitleId:x8}" : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static PokemonSave? Describe(ISaveEngine engine, string file, string key, Platform platform)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            return engine.TryDescribe(bytes, Path.GetFileName(file)) is { } description
                ? new PokemonSave(file, key, platform, description, File.GetLastWriteTime(file))
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EdenTitleDirs()
    {
        var root = Path.Combine(AppPaths.Home, ".local/share/eden/nand/user/save/0000000000000000");
        return Directory.Exists(root) ? Directory.EnumerateDirectories(root).SelectMany(Directory.EnumerateDirectories) : [];
    }

    private static IEnumerable<string> AzaharTitleDirs()
    {
        string[] sdmcs =
        [
            Path.Combine(AppPaths.Home, ".var/app/org.azahar_emu.Azahar/data/azahar-emu/sdmc/Nintendo 3DS"),
            Path.Combine(AppPaths.Home, ".local/share/azahar-emu/sdmc/Nintendo 3DS"),
        ];
        return sdmcs.Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)          // id0
            .SelectMany(Directory.EnumerateDirectories)          // id1
            .Select(d => Path.Combine(d, "title", "00040000"))
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories);
    }
}
