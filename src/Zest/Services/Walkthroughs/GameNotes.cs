using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zest.Services.Walkthroughs;

public sealed class CheckItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

/// <summary>
/// What Zest keeps per game for its walkthrough: the guide chosen for it, where reading stopped, the chapters marked
/// done, a checklist and free notes. One file per game in ~/.local/share/zest/notes, named after the game's title
/// (so a new dump or an update of the same game keeps them).
/// </summary>
public sealed class GameNotes
{
    /// <summary>The StrategyWiki guide's root page; null until one is found or chosen.</summary>
    public string? Guide { get; set; }
    /// <summary>The user picked <see cref="Guide"/> themselves: don't replace it with the search's best match.</summary>
    public bool GuideChosen { get; set; }
    /// <summary>Zest has switched this game to its Switch 2 guide once (Ocarina of Time); later choices stick.</summary>
    public bool RemakeGuideOffered { get; set; }
    public string? Page { get; set; }
    public double Scroll { get; set; }
    public HashSet<string> DonePages { get; set; } = [];
    public List<CheckItem> Checklist { get; set; } = [];
    public string Notes { get; set; } = "";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Folder => Path.Combine(AppPaths.Data, "notes");

    public static string FileFor(string title)
    {
        var name = new string(StrategyWiki.CleanTitle(title).Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
        while (name.Contains("--"))
            name = name.Replace("--", "-");
        return Path.Combine(Folder, name.Trim('-') + ".json");
    }

    public static GameNotes Load(string title)
    {
        var file = FileFor(title);
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<GameNotes>(File.ReadAllText(file), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for the user rather than overwriting it on the next save
            File.Copy(file, file + ".broken", overwrite: true);
            return new();
        }
    }

    public void Save(string title)
    {
        var file = FileFor(title);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(file + ".tmp", file, overwrite: true);
    }
}
