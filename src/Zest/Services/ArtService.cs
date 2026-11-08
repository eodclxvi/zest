using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Zest.Models;

namespace Zest.Services;

/// <summary>One cover the details page's "Find artwork" offers.</summary>
public sealed record ArtCandidate(string Source, string Label, string Url, string Thumb, int Width, int Height)
{
    public string SizeText => Width > 0 ? $"{Width}×{Height}" : "";
}

/// <summary>
/// Finds cover art: Switch icons come out of the game files themselves, 3DS/PS2 boxart from
/// thumbnails.libretro.com, Switch 2 covers from SteamGridDB (with the user's API key) or Wikipedia.
/// "Find artwork" lists covers from all of them. Everything is cached under ~/.cache/zest.
/// </summary>
public sealed partial class ArtService
{
    private const string LibretroRoot = "https://thumbnails.libretro.com";
    private static readonly TimeSpan IndexMaxAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissRetry = TimeSpan.FromDays(7);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _downloads = new(4);
    private readonly Lock _switchLock = new();
    private readonly Dictionary<string, Task<Dictionary<string, List<string>>>> _indexes = [];
    private readonly SwitchMetadata? _switch;
    private readonly string _steamGridDbKey;

    public ArtService(AppConfig config)
    {
        _switch = SwitchMetadata.TryCreate(config);
        _steamGridDbKey = config.SteamGridDbKey.Trim();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Zest/0.1 (+game launcher)");
        Directory.CreateDirectory(AppPaths.ArtCache);
        Directory.CreateDirectory(AppPaths.IndexCache);
    }

    public sealed record Result(string? ArtPath, string? Title);

    public async Task<Result> GetAsync(Game game)
    {
        var key = CacheKey(game.Path);
        var titleFile = Path.Combine(AppPaths.ArtCache, key + ".title");
        var cachedTitle = File.Exists(titleFile) ? await File.ReadAllTextAsync(titleFile) : null;

        foreach (var ext in ArtExtensions)
        {
            var cached = Path.Combine(AppPaths.ArtCache, key + ext);
            if (File.Exists(cached))
                return new Result(cached, cachedTitle);
        }

        var miss = Path.Combine(AppPaths.ArtCache, key + ".none");
        if (File.Exists(miss) && DateTime.Now - File.GetLastWriteTime(miss) < MissRetry)
            return new Result(null, cachedTitle);

        var result = game.Platform switch
        {
            Platform.Switch => await Task.Run(() => FromSwitchFile(game, key)),
            Platform.N3DS => await From3dsAsync(game, key),
            Platform.Switch2 => await FromWebAsync(game, key),
            _ => await FromLibretroAsync(game, game.Title, key),
        };

        if (result.Title is not null)
            await File.WriteAllTextAsync(titleFile, result.Title);
        if (result.ArtPath is null)
            await File.WriteAllTextAsync(miss, "");
        return result with { Title = result.Title ?? cachedTitle };
    }

    // ---- Web covers (Switch 2, and "Find artwork")

    /// <summary>A Switch 2 game's cover: SteamGridDB's top-voted grid when a key is set, else Wikipedia's infobox image.</summary>
    private async Task<Result> FromWebAsync(Game game, string key)
    {
        try
        {
            var pick = (await SteamGridDbAsync(game.Title, 1, 1, CancellationToken.None)).FirstOrDefault()
                ?? (await WikipediaAsync(game.Title, 3, CancellationToken.None)).FirstOrDefault(c => !c.Label.StartsWith("List of", StringComparison.Ordinal));
            return pick is null ? new Result(null, null) : new Result(await SaveAsync(key, pick.Url), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new Result(null, null);
        }
    }

    /// <summary>
    /// Covers for the game from every source: SteamGridDB (with a key), libretro's boxart (matched by title), and
    /// Wikipedia's article images (its search ranks the game's own page first). Sources that fail are left out.
    /// </summary>
    public async Task<List<ArtCandidate>> SearchAsync(Game game, string title, CancellationToken cancel)
    {
        var lists = await Task.WhenAll(
            Try(() => SteamGridDbAsync(title, 3, 6, cancel)),
            Try(() => LibretroCandidateAsync(game, title)),
            Try(() => WikipediaAsync(title, 8, cancel)));
        return lists.SelectMany(l => l).DistinctBy(c => c.Url).ToList();

        static async Task<List<ArtCandidate>> Try(Func<Task<List<ArtCandidate>>> source)
        {
            try
            {
                return await source();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return [];
            }
        }
    }

    public bool HasSteamGridDbKey => _steamGridDbKey.Length > 0;

    /// <summary>Makes the chosen cover the game's art (it stays until "Fetch art again").</summary>
    public async Task<string> UseAsync(Game game, ArtCandidate candidate)
    {
        Forget(game, keepTitle: true);
        return await SaveAsync(CacheKey(game.Path), candidate.Url);
    }

    public async Task<byte[]> ThumbnailAsync(ArtCandidate candidate, CancellationToken cancel)
    {
        await _downloads.WaitAsync(cancel);
        try
        {
            return await _http.GetByteArrayAsync(candidate.Thumb, cancel);
        }
        finally
        {
            _downloads.Release();
        }
    }

    private async Task<string> SaveAsync(string key, string url)
    {
        await _downloads.WaitAsync();
        try
        {
            var bytes = await _http.GetByteArrayAsync(url);
            // (the file's own bytes say what it is: Wikipedia and SteamGridDB serve JPEG and PNG)
            var ext = bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50 ? ".png" : ".jpg";
            var art = Path.Combine(AppPaths.ArtCache, key + ext);
            await File.WriteAllBytesAsync(art, bytes);
            return art;
        }
        finally
        {
            _downloads.Release();
        }
    }

    /// <summary>Wikipedia article images for "&lt;title&gt; video game": the infobox cover of each page, non-free ones too.</summary>
    private async Task<List<ArtCandidate>> WikipediaAsync(string title, int limit, CancellationToken cancel)
    {
        var url = "https://en.wikipedia.org/w/api.php?action=query&format=json&generator=search&prop=pageimages"
            + "&piprop=original|thumbnail&pithumbsize=240&pilicense=any"
            + $"&gsrlimit={limit}&gsrsearch=" + Uri.EscapeDataString(title + " video game");
        using var doc = System.Text.Json.JsonDocument.Parse(await _http.GetStringAsync(url, cancel));
        if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("pages", out var pages))
            return [];
        var found = new List<(int index, ArtCandidate c)>();
        foreach (var page in pages.EnumerateObject().Select(p => p.Value))
        {
            if (!page.TryGetProperty("original", out var o) || o.GetProperty("source").GetString() is not { } src
                || src.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                continue;
            var thumb = page.TryGetProperty("thumbnail", out var t) ? t.GetProperty("source").GetString() ?? src : src;
            found.Add((page.GetProperty("index").GetInt32(), new ArtCandidate("Wikipedia", page.GetProperty("title").GetString() ?? "",
                src, thumb, o.GetProperty("width").GetInt32(), o.GetProperty("height").GetInt32())));
        }
        return found.OrderBy(f => f.index).Select(f => f.c).ToList();
    }

    /// <summary>SteamGridDB portrait grids (600x900 and alike), top-voted first, for the best-matching games.</summary>
    private async Task<List<ArtCandidate>> SteamGridDbAsync(string title, int games, int perGame, CancellationToken cancel)
    {
        if (_steamGridDbKey.Length == 0)
            return [];
        const string api = "https://www.steamgriddb.com/api/v2";
        using var search = await SteamGridDbGetAsync($"{api}/search/autocomplete/{Uri.EscapeDataString(title)}", cancel);
        var result = new List<ArtCandidate>();
        foreach (var g in search.RootElement.GetProperty("data").EnumerateArray().Take(games))
        {
            var name = g.GetProperty("name").GetString() ?? title;
            using var grids = await SteamGridDbGetAsync($"{api}/grids/game/{g.GetProperty("id").GetInt64()}?dimensions=600x900,342x482,660x930&types=static", cancel);
            foreach (var grid in grids.RootElement.GetProperty("data").EnumerateArray().Take(perGame))
                result.Add(new ArtCandidate("SteamGridDB", name, grid.GetProperty("url").GetString()!,
                    grid.GetProperty("thumb").GetString()!, grid.GetProperty("width").GetInt32(), grid.GetProperty("height").GetInt32()));
        }
        return result;
    }

    private async Task<System.Text.Json.JsonDocument> SteamGridDbGetAsync(string url, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _steamGridDbKey);
        using var response = await _http.SendAsync(request, cancel);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new HttpRequestException("SteamGridDB refused the API key");
        response.EnsureSuccessStatusCode();
        return System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
    }

    private async Task<List<ArtCandidate>> LibretroCandidateAsync(Game game, string title)
    {
        if (PlatformInfo.LibretroSystem(game.Platform) is not { } system)
            return [];
        var name = BestMatch(await GetIndexAsync(system), title);
        if (name is null)
            return [];
        var url = $"{LibretroRoot}/{Uri.EscapeDataString(system)}/Named_Boxarts/{Uri.EscapeDataString(name)}.png";
        return [new ArtCandidate("libretro", name, url, url, 0, 0)];
    }

    /// <summary>Forgets cached art for one game so the next lookup fetches it again.</summary>
    public static void Forget(Game game, bool keepTitle = false)
    {
        var key = CacheKey(game.Path);
        foreach (var ext in ArtExtensions.Append(".none").Concat(keepTitle ? [] : [".title"]))
            File.Delete(Path.Combine(AppPaths.ArtCache, key + ext));
    }

    private static readonly string[] ArtExtensions = [".jpg", ".png", ".bmp"];

    /// <summary>Boxart looked up by the game's own SMDH title; the 48px SMDH icon if libretro has nothing.</summary>
    private async Task<Result> From3dsAsync(Game game, string key)
    {
        N3dsMetadata.Info? info = null;
        try
        {
            info = await Task.Run(() => N3dsMetadata.Read(game.Path));
        }
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {
            // Unreadable or truncated dump: boxart by filename still works.
        }

        // The built-in title can be shorter than libretro's name ("Ocarina of Time 3D" vs
        // "Legend of Zelda, The - Ocarina of Time 3D"), so the filename gets a turn too.
        var title = info?.Title;
        var boxart = await FromLibretroAsync(game, title ?? game.Title, key);
        if (boxart.ArtPath is null && title is not null && title != game.Title)
            boxart = await FromLibretroAsync(game, game.Title, key);
        if (boxart.ArtPath is null && info?.IconBmp is { } bmp)
        {
            var art = Path.Combine(AppPaths.ArtCache, key + ".bmp");
            await File.WriteAllBytesAsync(art, bmp);
            return new Result(art, title);
        }
        return boxart with { Title = title };
    }

    private Result FromSwitchFile(Game game, string key)
    {
        if (_switch is null)
            return new Result(null, null);
        try
        {
            (string? title, byte[]? icon)? meta;
            // LibHac's key set is shared and not thread-safe while tickets are imported.
            lock (_switchLock)
                meta = _switch.Read(game.Local is { ArtFrom.Length: > 0 } local ? AppPaths.ExpandHome(local.ArtFrom) : game.Path);
            if (meta is not { } m)
                return new Result(null, null);

            string? art = null;
            if (m.icon is { Length: > 0 })
            {
                art = Path.Combine(AppPaths.ArtCache, key + ".jpg");
                File.WriteAllBytes(art, m.icon);
            }
            return new Result(art, m.title);
        }
        catch (Exception)
        {
            // Missing keys, a newer firmware key generation, or a damaged dump: fall back to the filename.
            return new Result(null, null);
        }
    }

    private async Task<Result> FromLibretroAsync(Game game, string title, string key)
    {
        var system = PlatformInfo.LibretroSystem(game.Platform);
        if (system is null)
            return new Result(null, null);

        try
        {
            var index = await GetIndexAsync(system);
            var name = BestMatch(index, title);
            if (name is null)
                return new Result(null, null);

            var url = $"{LibretroRoot}/{Uri.EscapeDataString(system)}/Named_Boxarts/{Uri.EscapeDataString(name)}.png";
            await _downloads.WaitAsync();
            try
            {
                var bytes = await _http.GetByteArrayAsync(url);
                var art = Path.Combine(AppPaths.ArtCache, key + ".png");
                await File.WriteAllBytesAsync(art, bytes);
                return new Result(art, null);
            }
            finally
            {
                _downloads.Release();
            }
        }
        catch (HttpRequestException)
        {
            return new Result(null, null);
        }
        catch (TaskCanceledException)
        {
            return new Result(null, null);
        }
    }

    private Task<Dictionary<string, List<string>>> GetIndexAsync(string system)
    {
        lock (_indexes)
        {
            if (!_indexes.TryGetValue(system, out var task))
                _indexes[system] = task = LoadIndexAsync(system);
            return task;
        }
    }

    /// <summary>Boxart names for a system, grouped by normalized title. Cached on disk for a month.</summary>
    private async Task<Dictionary<string, List<string>>> LoadIndexAsync(string system)
    {
        var file = Path.Combine(AppPaths.IndexCache, system + ".txt");
        string[] names;
        if (File.Exists(file) && DateTime.Now - File.GetLastWriteTime(file) < IndexMaxAge)
        {
            names = await File.ReadAllLinesAsync(file);
        }
        else
        {
            try
            {
                var html = await _http.GetStringAsync($"{LibretroRoot}/{Uri.EscapeDataString(system)}/Named_Boxarts/");
                names = PngLink().Matches(html).Select(m => WebUtility.UrlDecode(m.Groups[1].Value)).Distinct().ToArray();
                await File.WriteAllLinesAsync(file, names);
            }
            catch (HttpRequestException) when (File.Exists(file))
            {
                names = await File.ReadAllLinesAsync(file); // offline: an old index beats none
            }
        }

        var index = new Dictionary<string, List<string>>();
        foreach (var name in names)
        {
            var norm = Normalize(name);
            if (!index.TryGetValue(norm, out var list))
                index[norm] = list = [];
            list.Add(name);
        }
        return index;
    }

    private static string? BestMatch(Dictionary<string, List<string>> index, string title)
    {
        var norm = Normalize(title);
        if (index.TryGetValue(norm, out var exact))
            return PickRegion(exact);

        // Close enough: same words, allowing one extra or missing word (e.g. a dropped subtitle),
        // but never a different sequel number ("Pro Skater" is not "Pro Skater 3").
        var words = norm.Split(' ').ToHashSet();
        var best = index
            .Select(kv => (kv.Value, other: kv.Key.Split(' ').ToHashSet()))
            .Where(x => !x.other.Union(words).Except(x.other.Intersect(words)).Any(IsSequelNumber))
            .Select(x => (x.Value, score: Jaccard(words, x.other)))
            .Where(x => x.score >= 0.75)
            .OrderByDescending(x => x.score)
            .FirstOrDefault();
        return best.Value is null ? null : PickRegion(best.Value);
    }

    private static string PickRegion(List<string> names) =>
        names.OrderBy(n => n.Contains("(USA") ? 0 : n.Contains("(World") ? 1 : n.Contains("(Europe") ? 2 : 3)
             .ThenBy(n => n.Length)
             .First();

    private static bool IsSequelNumber(string word) =>
        word.All(char.IsDigit) || word is "ii" or "iii" or "iv" or "v" or "vi" or "vii" or "viii" or "ix" or "x" or "xi" or "xii";

    private static double Jaccard(HashSet<string> a, HashSet<string> b) =>
        (double)a.Intersect(b).Count() / a.Union(b).Count();

    /// <summary>"Legend of Zelda, The - Ocarina of Time 3D (USA)" and "The Legend of Zelda: Ocarina of Time 3D" both become "legend of zelda ocarina of time 3d".</summary>
    public static string Normalize(string name)
    {
        name = Brackets().Replace(name, " ");
        var sb = new StringBuilder();
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            // libretro swaps &*/:`<>?\| for underscores, so treat all of those as separators.
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("the" or "and" or "s"));
        return string.Join(' ', words);
    }

    private static string CacheKey(string path)
    {
        var info = new FileInfo(path);
        var id = $"{path}|{(info.Exists ? info.Length : 0)}";
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(id)))[..16];
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Brackets();

    [GeneratedRegex("href=\"([^\"]+)\\.png\"")]
    private static partial Regex PngLink();
}
