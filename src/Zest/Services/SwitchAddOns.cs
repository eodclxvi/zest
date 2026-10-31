using System.Text.RegularExpressions;
using Zest.Models;
using Zest.Services.Native;

namespace Zest.Services;

/// <summary>
/// Sorts Switch files into games, updates and DLC, and nests updates and DLC under the game they belong to.
/// The signed content meta decides; compressed NSZ/XCZ files, which LibHac can't open, fall back to the
/// title ID in their file name.
/// </summary>
public static partial class SwitchAddOns
{
    public static void Classify(IEnumerable<Game> games, AppConfig config)
    {
        var content = SwitchContent.TryCreate(config);
        foreach (var game in games.Where(g => g.Platform == Platform.Switch && !g.IsInstalled))
        {
            SwitchFileInfo? info = null;
            try
            {
                info = content?.Describe(game.Path);
            }
            catch (Exception)
            {
                // Missing keys or a damaged file: fall back to the name.
            }
            info ??= FromFileName(game.Path);
            if (info is null)
                continue;

            game.Kind = info.Kind;
            game.TitleId = info.TitleId;
            game.BaseTitleId = info.BaseTitleId;
            game.Version = info.Version;
            if (info.Kind == ContentKind.Dlc)
                game.Title = DlcName(game.Path) ?? $"DLC {info.TitleId & 0xFFF}";
            else if (info.Kind == ContentKind.Update)
                game.Title = $"Update {info.Version}";
        }
    }

    /// <summary>
    /// Moves every update and DLC whose game is in the list under that game. Ones whose game isn't in the library
    /// stay as their own entries, so nothing disappears.
    /// </summary>
    public static List<Game> Nest(List<Game> games)
    {
        var bases = new Dictionary<ulong, Game>();
        foreach (var game in games.Where(g => g.Kind == ContentKind.Game && g.TitleId is not null))
            bases.TryAdd(game.TitleId!.Value, game);

        var top = new List<Game>();
        foreach (var game in games)
        {
            if (game.Kind != ContentKind.Game && game.BaseTitleId is { } baseId && bases.TryGetValue(baseId, out var parent))
                parent.AddOns.Add(game);
            else
                top.Add(game);
        }

        foreach (var parent in bases.Values.Where(p => p.AddOns.Count > 1))
        {
            var sorted = parent.AddOns
                .OrderBy(a => a.Kind == ContentKind.Update ? 0 : 1)
                .ThenByDescending(a => a.Kind == ContentKind.Update ? ParseVersion(a.Version) : default)
                .ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            parent.AddOns.Clear();
            parent.AddOns.AddRange(sorted);
        }
        return top;
    }

    /// <summary>"[0100ABF008968800]" in a file name: base games end in 000, updates in 800, DLC count up from base + 0x1000.</summary>
    private static SwitchFileInfo? FromFileName(string path)
    {
        var m = TitleIdInName().Match(Path.GetFileName(path));
        if (!m.Success)
            return null;
        var id = Convert.ToUInt64(m.Groups[1].Value, 16);
        var low = id & 0xFFF;
        if (low == 0)
            return new SwitchFileInfo(ContentKind.Game, id, id, null);
        if (low == 0x800)
            return new SwitchFileInfo(ContentKind.Update, id, id & ~0xFFFUL, VersionInName().Match(path) is { Success: true } v ? v.Groups[1].Value : null);
        return new SwitchFileInfo(ContentKind.Dlc, id, (id - 0x1000) & ~0xFFFUL, null);
    }

    /// <summary>The DLC's name from the file name: "Pokemon Shield [The Crown Tundra] [01008DB008C2D002] [v0]" → "The Crown Tundra".</summary>
    private static string? DlcName(string path)
    {
        foreach (Match m in Bracketed().Matches(Path.GetFileNameWithoutExtension(path)))
        {
            var text = m.Groups[1].Value.Trim();
            if (TitleIdInName().IsMatch($"[{text}]") || RawVersion().IsMatch(text) || text.Equals("DLC", StringComparison.OrdinalIgnoreCase)
                || text.Length <= 2)
                continue;
            return DlcPrefix().Replace(text, "").Trim();
        }
        return null;
    }

    private static Version ParseVersion(string? text) =>
        text is not null && Version.TryParse(text.TrimStart('v'), out var v) ? v : new Version(0, 0);

    [GeneratedRegex(@"\[(01[0-9A-Fa-f]{14})\]")]
    private static partial Regex TitleIdInName();

    [GeneratedRegex(@"v(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex VersionInName();

    [GeneratedRegex(@"[\[(]([^\])]+)[\])]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"^v\d+$|^(USA|US|EUR|EU|JPN|JP|NA)$", RegexOptions.IgnoreCase)]
    private static partial Regex RawVersion();

    [GeneratedRegex(@"^DLC\s*[-:]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex DlcPrefix();
}
