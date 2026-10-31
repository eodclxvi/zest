using System.Text.RegularExpressions;

namespace Zest.Models;

public sealed partial class Game
{
    public required string Path { get; init; }
    public required Platform Platform { get; init; }
    public required string Title { get; set; }

    /// <summary>A title installed inside the emulator (Azahar's SD card), rather than a game file in a folder.</summary>
    public bool IsInstalled { get; init; }

    /// <summary>Whether the file is a game, an update or DLC (Switch files say so in their signed content meta).</summary>
    public ContentKind Kind { get; set; } = ContentKind.Game;

    /// <summary>The title ID of this content, when it could be read.</summary>
    public ulong? TitleId { get; set; }

    /// <summary>Updates and DLC: the title ID of the game they belong to.</summary>
    public ulong? BaseTitleId { get; set; }

    /// <summary>Updates: the version they bring the game to (control.nacp display version, e.g. "2.0.2").</summary>
    public string? Version { get; set; }

    /// <summary>The updates and DLC found for this game, nested under it in the library.</summary>
    public List<Game> AddOns { get; } = [];

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Turns "Legend of Zelda, The - Ocarina of Time 3D (USA) [v0].cci" into "The Legend of Zelda - Ocarina of Time 3D".</summary>
    public static string TitleFromFileName(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        name = Brackets().Replace(name, " ");
        name = name.Replace('_', ' ');
        name = Spaces().Replace(name, " ").Trim();

        var m = TrailingArticle().Match(name);
        if (m.Success)
            name = $"{m.Groups[2].Value} {m.Groups[1].Value}{m.Groups[3].Value}";

        return name;
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // "Legend of Zelda, The - Subtitle" -> groups: "Legend of Zelda", "The", " - Subtitle"
    [GeneratedRegex(@"^(.+?), (The|A|An)\b(.*)$")]
    private static partial Regex TrailingArticle();
}

public enum ContentKind
{
    Game,
    Update,
    Dlc,
}

