using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zest.Services.Walkthroughs;

/// <summary>A picture on Zelda Wiki: its file name and a thumbnail Zest can show.</summary>
public sealed record WikiImage(string Name, string Url, int Width, int Height);

/// <summary>
/// Zelda Wiki's MediaWiki API (zeldawiki.wiki/w/api.php), for the Switch 2 Ocarina of Time guide: the remake's
/// changes (text under the GNU FDL 1.3, shown with credit and a link back), its list of renamed places and items, and
/// its screenshots and maps. The screenshots are Nintendo's, hosted there; Zest shows them like a browser would
/// (fetched when read, cached in ~/.cache/zest/walkthroughs) and never ships them.
/// </summary>
public static class ZeldaWiki
{
    public const string Licence = "GNU FDL 1.3";
    public const string Site = "https://zeldawiki.wiki";
    private const string Api = Site + "/w/api.php";
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(7);
    private static readonly HttpClient Http = CreateClient();

    public static string PageUrl(string page) => Site + "/wiki/" + Uri.EscapeDataString(page.Replace(' ', '_')).Replace("%3A", ":");

    /// <summary>A page's wikitext (null when the page doesn't exist).</summary>
    public static async Task<string?> WikitextAsync(string page, CancellationToken cancel)
    {
        var json = await GetJsonAsync($"action=parse&prop=wikitext&redirects=1&page={Uri.EscapeDataString(page)}", "zw-wikitext-" + page, cancel);
        return (string?)json["parse"]?["wikitext"];
    }

    /// <summary>One section of a page (by its heading) as reader blocks, subsections included; empty when it's missing.</summary>
    public static async Task<IReadOnlyList<Block>> SectionAsync(string page, string heading, CancellationToken cancel)
    {
        var sections = await GetJsonAsync($"action=parse&prop=sections&redirects=1&page={Uri.EscapeDataString(page)}", "zw-sections-" + page, cancel);
        var index = sections["parse"]?["sections"]?.AsArray()
            .FirstOrDefault(s => string.Equals((string?)s?["line"], heading, StringComparison.OrdinalIgnoreCase))?["index"];
        if (index is null)
            return [];
        var json = await GetJsonAsync(
            $"action=parse&prop=text&redirects=1&disableeditsection=1&disabletoc=1&section={(string?)index}&page={Uri.EscapeDataString(page)}",
            $"zw-section-{page}-{heading}", cancel);
        var html = (string?)json["parse"]?["text"] ?? "";
        return await Task.Run(() => HtmlBlocks.Parse(html, Site), cancel);
    }

    /// <summary>All file names starting with <paramref name="prefix"/> ("OoTNS2_"), cached for a week.</summary>
    public static async Task<IReadOnlyList<string>> FilesAsync(string prefix, CancellationToken cancel)
    {
        var names = new List<string>();
        string? from = null;
        for (var page = 0; page < 20; page++)
        {
            var query = $"action=query&list=allimages&aiprefix={Uri.EscapeDataString(prefix)}&ailimit=500"
                        + (from is null ? "" : $"&aicontinue={Uri.EscapeDataString(from)}");
            var json = await GetJsonAsync(query, $"zw-files-{prefix}-{from}", cancel);
            names.AddRange(json["query"]?["allimages"]?.AsArray().Select(i => (string?)i?["name"]).OfType<string>() ?? []);
            from = (string?)json["continue"]?["aicontinue"];
            if (from is null)
                break;
        }
        return names;
    }

    /// <summary>Thumbnails (<paramref name="width"/> px wide at most) for these files, in the same order.</summary>
    public static async Task<IReadOnlyList<WikiImage>> ImagesAsync(IReadOnlyList<string> files, int width, CancellationToken cancel)
    {
        var found = new Dictionary<string, WikiImage>(StringComparer.Ordinal);
        foreach (var batch in files.Chunk(40))
        {
            var titles = string.Join('|', batch.Select(f => "File:" + f));
            var json = await GetJsonAsync($"action=query&prop=imageinfo&iiprop=url|size&iiurlwidth={width}&titles={Uri.EscapeDataString(titles)}",
                $"zw-images-{width}-{titles}", cancel);
            foreach (var page in json["query"]?["pages"]?.AsArray() ?? [])
            {
                var info = page?["imageinfo"]?.AsArray().FirstOrDefault();
                if (info is null || (string?)page?["title"] is not { } title)
                    continue;
                var url = (string?)info["thumburl"] ?? (string?)info["url"];
                if (url is null)
                    continue;
                found[title["File:".Length..].Replace(' ', '_')] = new WikiImage(title["File:".Length..], url,
                    (int?)info["thumbwidth"] ?? (int?)info["width"] ?? width, (int?)info["thumbheight"] ?? (int?)info["height"] ?? 0);
            }
        }
        return [.. files.Select(f => found.GetValueOrDefault(f.Replace(' ', '_'))).OfType<WikiImage>()];
    }

    /// <summary>One API call (format=json, formatversion=2), cached on disk; an old copy is used when offline.</summary>
    private static async Task<JsonNode> GetJsonAsync(string query, string cacheKey, CancellationToken cancel)
    {
        var file = Path.Combine(StrategyWiki.Cache, "zw-" + Hash(cacheKey) + ".json");
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < Fresh)
        {
            try
            {
                return JsonNode.Parse(await File.ReadAllTextAsync(file, cancel))!;
            }
            catch (JsonException)
            {
            }
        }
        string text;
        try
        {
            text = await Http.GetStringAsync($"{Api}?{query}&format=json&formatversion=2", cancel);
        }
        catch (HttpRequestException) when (File.Exists(file))
        {
            text = await File.ReadAllTextAsync(file, cancel);
        }
        var node = JsonNode.Parse(text) ?? new JsonObject();
        if (node["error"] is null)
        {
            Directory.CreateDirectory(StrategyWiki.Cache);
            await File.WriteAllTextAsync(file + ".tmp", text, cancel);
            File.Move(file + ".tmp", file, overwrite: true);
        }
        return node;
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
}
