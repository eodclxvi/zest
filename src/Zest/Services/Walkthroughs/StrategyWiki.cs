using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Zest.Services.Walkthroughs;

/// <summary>A guide's chapter: its wiki page and the group it's listed under in the guide's Table of Contents.</summary>
public sealed record GuideChapter(string Page, string Label, string Group);

/// <summary>A guide on StrategyWiki: its root page (the game's name there) and its chapters in reading order.</summary>
public sealed record Guide(string Root, IReadOnlyList<GuideChapter> Chapters);

/// <summary>
/// StrategyWiki's MediaWiki API (strategywiki.org/w/api.php). Its text is CC BY-SA 4.0, so Zest may show it with
/// credit and a link back; nothing is bundled with Zest, pages are fetched when read and cached in
/// ~/.cache/zest/walkthroughs for a week.
/// </summary>
public static partial class StrategyWiki
{
    public const string Licence = "CC BY-SA 4.0";
    public const string LicenceUrl = "https://creativecommons.org/licenses/by-sa/4.0/";
    private const string Api = "https://strategywiki.org/w/api.php";
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(7);
    private static readonly HttpClient Http = CreateClient();

    public static string Cache => Path.Combine(AppPaths.Cache, "walkthroughs");

    public static string PageUrl(string page) => "https://strategywiki.org/wiki/" + Uri.EscapeDataString(page.Replace(' ', '_')).Replace("%2F", "/").Replace("%3A", ":");

    /// <summary>Guides whose name matches this game, best first (the roots of the pages a search finds).</summary>
    public static async Task<IReadOnlyList<string>> FindGuidesAsync(string title, CancellationToken cancel)
    {
        var query = CleanTitle(title);
        var json = await GetJsonAsync($"action=query&list=search&srnamespace=0&srlimit=30&srsearch={Uri.EscapeDataString(query)}", "search-" + query, cancel);
        var hits = json["query"]?["search"]?.AsArray().Select(h => (string?)h?["title"]).OfType<string>().ToList() ?? [];
        var words = Words(query);
        // A guide is a root page with subpages; rank roots by how well their name matches, then by how many hits they had
        return hits.Select(h => h.Split('/')[0]).GroupBy(r => r)
            .Select(g => (root: g.Key, hits: g.Count(), score: Score(words, Words(g.Key))))
            .Where(r => r.score > 0)
            .OrderByDescending(r => r.score).ThenByDescending(r => r.hits)
            .Select(r => r.root).Take(6).ToList();
    }

    /// <summary>The guide's chapters from its "Table of Contents" page, else its subpages in alphabetical order.</summary>
    public static async Task<Guide> GuideAsync(string root, CancellationToken cancel)
    {
        var chapters = new List<GuideChapter> { new(root, "Overview", "") };
        var toc = await WikitextAsync(root + "/Table of Contents", cancel);
        if (toc is not null)
        {
            var group = "";
            foreach (var raw in toc.Split('\n'))
            {
                var line = raw.Trim();
                if (HeadingTemplate().Match(line) is { Success: true } h)
                    group = LinkLabel(h.Groups[1].Value);
                else if (HeadingWiki().Match(line) is { Success: true } hw)
                    group = LinkLabel(hw.Groups[1].Value);
                else if (line.StartsWith('*') || line.StartsWith('#'))
                    foreach (Match m in WikiLink().Matches(line))
                        Add(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null, group);
                // A heading that is itself a link ({{h2|[[X/Walkthrough|Walkthrough]]}}) is a chapter too
                if (line.StartsWith("{{h") || line.StartsWith("=="))
                    foreach (Match m in WikiLink().Matches(line))
                        Add(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null, group);
            }
        }
        if (chapters.Count == 1)
        {
            var json = await GetJsonAsync($"action=query&list=allpages&apnamespace=0&aplimit=500&apfilterredir=nonredirects&apprefix={Uri.EscapeDataString(root + "/")}", "pages-" + root, cancel);
            foreach (var p in json["query"]?["allpages"]?.AsArray() ?? [])
                if ((string?)p?["title"] is { } t)
                    Add(t, null, "");
        }
        return new Guide(root, chapters);

        void Add(string target, string? label, string group)
        {
            target = target.Split('#')[0].Trim().Replace('_', ' ');
            if (target.StartsWith('/'))
                target = root + target;
            if (!target.StartsWith(root + "/", StringComparison.Ordinal) || chapters.Any(c => c.Page == target))
                return;
            chapters.Add(new GuideChapter(target, (label ?? target[(root.Length + 1)..]).Trim(), group));
        }
    }

    /// <summary>A page as reader blocks, plus the page's own title (after redirects).</summary>
    public static async Task<(string page, IReadOnlyList<Block> blocks)> PageAsync(string page, CancellationToken cancel)
    {
        var json = await GetJsonAsync($"action=parse&prop=text&redirects=1&disableeditsection=1&disabletoc=1&page={Uri.EscapeDataString(page)}", "page-" + page, cancel);
        if (json["error"]?["info"] is { } error)
            throw new IOException((string?)error ?? "StrategyWiki couldn't find that page.");
        var title = (string?)json["parse"]?["title"] ?? page;
        var html = (string?)json["parse"]?["text"] ?? "";
        return (title, await Task.Run(() => HtmlBlocks.Parse(html), cancel));
    }

    /// <summary>A wiki href ("/wiki/X/Y#z") as its page title and anchor; null for links off the wiki.</summary>
    public static (string page, string? anchor)? WikiTarget(string href)
    {
        const string prefix = "/wiki/";
        var path = href.StartsWith("https://strategywiki.org/wiki/") ? href["https://strategywiki.org".Length..]
            : href.StartsWith("//strategywiki.org/wiki/") ? href["//strategywiki.org".Length..] : href;
        if (path.StartsWith('#'))
            return ("", Uri.UnescapeDataString(path[1..]));
        if (!path.StartsWith(prefix))
            return null;
        var rest = path[prefix.Length..];
        var hash = rest.IndexOf('#');
        var anchor = hash < 0 ? null : Uri.UnescapeDataString(rest[(hash + 1)..]);
        var name = Uri.UnescapeDataString(hash < 0 ? rest : rest[..hash]).Replace('_', ' ');
        return name.Contains(':') && name.Split(':')[0] is "File" or "Category" or "Talk" or "Special" or "User" or "StrategyWiki" or "Template"
            ? null : (name, anchor);
    }

    public static async Task<byte[]> ImageAsync(string url, CancellationToken cancel)
    {
        var file = Path.Combine(Cache, "img", Hash(url) + Path.GetExtension(new Uri(url).AbsolutePath));
        if (File.Exists(file))
            return await File.ReadAllBytesAsync(file, cancel);
        var bytes = await Http.GetByteArrayAsync(url, cancel);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, bytes, cancel);
        return bytes;
    }

    private static async Task<string?> WikitextAsync(string page, CancellationToken cancel)
    {
        var json = await GetJsonAsync($"action=parse&prop=wikitext&redirects=1&page={Uri.EscapeDataString(page)}", "wikitext-" + page, cancel);
        return (string?)json["parse"]?["wikitext"];
    }

    /// <summary>One API call (format=json, formatversion=2); cached on disk under <paramref name="cacheKey"/> when given.</summary>
    private static async Task<JsonNode> GetJsonAsync(string query, string? cacheKey, CancellationToken cancel)
    {
        var file = cacheKey is null ? null : Path.Combine(Cache, Hash(cacheKey) + ".json");
        if (file is not null && File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < Fresh)
            try
            {
                return JsonNode.Parse(await File.ReadAllTextAsync(file, cancel))!;
            }
            catch (JsonException)
            {
            }
        string text;
        try
        {
            text = await Http.GetStringAsync($"{Api}?{query}&format=json&formatversion=2", cancel);
        }
        catch (HttpRequestException) when (file is not null && File.Exists(file))
        {
            text = await File.ReadAllTextAsync(file, cancel);   // offline: an old copy beats nothing
        }
        var node = JsonNode.Parse(text) ?? new JsonObject();
        if (file is not null && node["error"] is null)
        {
            Directory.CreateDirectory(Cache);
            await File.WriteAllTextAsync(file + ".tmp", text, cancel);
            File.Move(file + ".tmp", file, overwrite: true);
        }
        return node;
    }

    /// <summary>Drops what a dump's title adds that a guide's name doesn't: edition tags, "Nintendo Switch 2 Edition", trademarks.</summary>
    public static string CleanTitle(string title)
    {
        var t = Bracketed().Replace(title, " ");
        t = t.Replace("™", "").Replace("®", "").Replace("Nintendo Switch 2 Edition", "", StringComparison.OrdinalIgnoreCase);
        return Spaces().Replace(t, " ").Trim(' ', '-', '–');
    }

    private static HashSet<string> Words(string s) =>
        WordChars().Matches(s.ToLowerInvariant().Replace("é", "e")).Select(m => m.Value)
            .Where(w => w is not ("the" or "of" or "and" or "a")).ToHashSet();

    /// <summary>The share of the game's words in the guide's name, less a little for extra words (a sequel's name).</summary>
    private static double Score(HashSet<string> game, HashSet<string> guide)
    {
        if (game.Count == 0 || guide.Count == 0)
            return 0;
        var common = game.Count(guide.Contains);
        return common == 0 ? 0 : (double)common / game.Count - 0.05 * guide.Count(w => !game.Contains(w));
    }

    private static string LinkLabel(string s)
    {
        var m = WikiLink().Match(s);
        var label = m.Success ? m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value.Split('/').Last() : s;
        return label.Replace("'''", "").Replace("''", "").Trim();
    }

    private static string Hash(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..24].ToLowerInvariant();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zest", "1.0"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(game launcher; walkthrough reader)"));
        return client;
    }

    [GeneratedRegex(@"^\{\{h\d\|(.+)\}\}\s*$")]
    private static partial Regex HeadingTemplate();
    [GeneratedRegex(@"^=+\s*(.+?)\s*=+\s*$")]
    private static partial Regex HeadingWiki();
    [GeneratedRegex(@"\[\[([^\]|]+)(?:\|([^\]]+))?\]\]")]
    private static partial Regex WikiLink();
    [GeneratedRegex(@"[\[(][^\])]*[\])]")]
    private static partial Regex Bracketed();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordChars();
}
