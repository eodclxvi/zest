using System.Text.RegularExpressions;

namespace Zest.Models;

public sealed partial class Game
{
    public required string Path { get; init; }
    public required Platform Platform { get; init; }
    public required string Title { get; set; }

    /// <summary>A title installed inside the emulator (Azahar's SD card), rather than a game file in a folder.</summary>
    public bool IsInstalled { get; init; }

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
