using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Zest.Models;

namespace Zest.Services;

/// <summary>One shader cache found for a game: a pack Zest can install, or a page to look at.</summary>
public sealed record ShaderCacheHit(string Source, string Title, string Detail, string Url, bool Installable);

/// <summary>
/// Shared shader caches. Lichii games get packs from Lichii Shader Sync
/// (github.com/oceanpacificpeace/lichii-shader-sync), installed with its own command-line tool, which Zest downloads
/// and checks against the release's SHA256SUMS.txt. Every other game gets a search of GitHub, shown as links only:
/// those are other people's files, so the user decides what to download.
/// </summary>
public static partial class ShaderCaches
{
    private const string SyncRepo = "oceanpacificpeace/lichii-shader-sync";
    private const string SyncAsset = "lichii-sync-linux-amd64";

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim SyncGate = new(1, 1);

    private static string Root => Path.Combine(AppPaths.Data, "shadercache");
    private static string Tool => Path.Combine(Root, "lichii-sync");
    private static string ToolVersion => Path.Combine(Root, "lichii-sync.version");
    private static string StateFile(string titleId) => Path.Combine(Root, "synced", titleId + ".json");

    /// <summary>
    /// The folder Zest's lichii launcher points XDG_DATA/CONFIG/CACHE_HOME at, so Lichii's "eden" folders stay apart
    /// from Eden's own; null when Lichii uses the normal XDG folders.
    /// </summary>
    private static string? LichiiRoot
    {
        get
        {
            var root = AppPaths.ExpandHome("~/.local/share/lichii-root");
            return Directory.Exists(root) ? root : null;
        }
    }

    private static string LichiiShaderFolder(string titleId)
    {
        var cache = LichiiRoot is { } r ? Path.Combine(r, "cache")
            : Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? x
            : Path.Combine(AppPaths.Home, ".cache");
        return Path.Combine(cache, "eden", "shader", titleId.ToLowerInvariant());
    }

    /// <summary>The game's title ID: read from the dump, else the [0400486024DEC000] in its file name.</summary>
    public static string? TitleId(Game game)
    {
        if (game.TitleId is { } id)
            return id.ToString("X16");
        var m = TitleIdInName().Match(game.FileName);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>Games whose emulator can take a Lichii Shader Sync pack (only Lichii's cache format is supported).</summary>
    public static bool SupportsPacks(Game game) => game.Local is null && game.Platform == Platform.Switch2;

    // ---- Lichii Shader Sync packs ----

    public sealed record PackManifest(
        [property: JsonPropertyName("title_id")] string TitleId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("source_version")] int SourceVersion,
        [property: JsonPropertyName("total_entries")] int Total,
        [property: JsonPropertyName("chunks")] List<PackChunk> Chunks);

    public sealed record PackChunk(
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("sha256")] string Sha256);

    /// <summary>The online pack for the title, or null when there is none. Throws when GitHub can't be reached.</summary>
    public static async Task<PackManifest?> FindPackAsync(string titleId, CancellationToken cancel = default)
    {
        var url = $"https://github.com/{SyncRepo}/releases/download/manifests/{titleId}.json";
        using var response = await Http.GetAsync(url, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<PackManifest>(await response.Content.ReadAsStringAsync(cancel));
    }

    /// <summary>A short fingerprint of the pack's chunk list: it changes whenever the pack gains pipelines.</summary>
    private static string Fingerprint(PackManifest m) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(",", m.Chunks.Select(c => c.Sha256)))))[..16];

    private sealed record SyncState(string Fingerprint, int Pipelines, DateTime When);

    private static SyncState? ReadState(string titleId)
    {
        try
        {
            return JsonSerializer.Deserialize<SyncState>(File.ReadAllText(StateFile(titleId)));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether the game has booted in Lichii once (the sync tool only merges into a cache Lichii made).</summary>
    public static bool HasLocalCache(string titleId) =>
        Directory.Exists(LichiiShaderFolder(titleId))
        && Directory.EnumerateFiles(LichiiShaderFolder(titleId), "vulkan*.fork-v*.bin").Any(f => !f.Contains("vulkan_used"));

    /// <summary>Whether this pack was already merged by Zest (same chunk list as the last sync).</summary>
    public static bool IsUpToDate(string titleId, PackManifest pack) => ReadState(titleId)?.Fingerprint == Fingerprint(pack);

    /// <summary>
    /// Merges the online pack into Lichii's cache for the title. Lichii must be closed (it writes the cache while open).
    /// Returns a one-line result for the status bar.
    /// </summary>
    public static async Task<string> SyncAsync(string titleId, PackManifest pack, IProgress<string>? progress, CancellationToken cancel = default)
    {
        if (!HasLocalCache(titleId))
            return "Play the game once in Lichii first; the shader pack is merged into the cache Lichii makes.";
        if (LichiiIsRunning())
            return "Close Lichii first: it writes to the shader cache while it's open.";

        await SyncGate.WaitAsync(cancel);
        try
        {
            progress?.Report("Getting the shader sync tool…");
            var tool = await EnsureToolAsync(cancel);

            var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "sync", "-title", titleId })
                psi.ArgumentList.Add(arg);
            if (LichiiRoot is { } root)
            {
                psi.Environment["XDG_DATA_HOME"] = Path.Combine(root, "data");
                psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(root, "config");
                psi.Environment["XDG_CACHE_HOME"] = Path.Combine(root, "cache");
            }

            using var p = Process.Start(psi) ?? throw new IOException("Could not start the shader sync tool.");
            using var reg = cancel.Register(() => { try { p.Kill(); } catch (InvalidOperationException) { } });
            var lines = new List<string>();
            var errors = p.StandardError.ReadToEndAsync(cancel);
            while (await p.StandardOutput.ReadLineAsync(cancel) is { } line)
            {
                line = line.Trim();
                if (line.Length == 0)
                    continue;
                lines.Add(line);
                progress?.Report(line.EndsWith('%') ? $"Downloading shaders… {line}" : line);
            }
            await p.WaitForExitAsync(cancel);
            var err = (await errors).Trim();
            if (p.ExitCode != 0)
                throw new IOException(err.Length > 0 ? err.Replace("ERROR: ", "") : $"the sync tool exited with code {p.ExitCode}");

            // "vulkan.fork-v54.bin: done, 91115 pipelines (+90642)" or "...: up to date (91115 pipelines)"
            var result = lines.LastOrDefault(l => l.Contains("pipelines") && l.Contains(".bin:")) ?? lines.LastOrDefault() ?? "";
            var count = PipelineCount().Match(result) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile(titleId))!);
            File.WriteAllText(StateFile(titleId), JsonSerializer.Serialize(new SyncState(Fingerprint(pack), count, DateTime.Now)));

            var added = AddedCount().Match(result) is { Success: true } a ? int.Parse(a.Groups[1].Value) : 0;
            return added > 0 ? $"Shader cache updated: {count:N0} pipelines (+{added:N0})."
                : count > 0 ? $"Shader cache up to date ({count:N0} pipelines)."
                : result;
        }
        finally
        {
            SyncGate.Release();
        }
    }

    /// <summary>
    /// Before a Lichii game starts: merge its pack when the online pack changed since the last sync. Never stops the
    /// launch; returns a message when something was done or went wrong, null when there was nothing to do.
    /// </summary>
    public static async Task<string?> BeforeLaunchAsync(Game game, IProgress<string> progress)
    {
        if (!SupportsPacks(game) || TitleId(game) is not { } id || !HasLocalCache(id))
            return null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var pack = await FindPackAsync(id, timeout.Token);
            if (pack is null || IsUpToDate(id, pack))
                return null;
            progress.Report("Updating the shader cache…");
            return await SyncAsync(id, pack, progress);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            return $"Shader cache not updated ({e.Message}).";
        }
    }

    /// <summary>A Lichii process (the AppImage's own binary, or our launcher script), found by its command line.</summary>
    private static bool LichiiIsRunning()
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out _))
                continue;
            try
            {
                var argv0 = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0')[0];
                var name = Path.GetFileName(argv0);
                if (name == "lichii" || name.StartsWith("lichii-linux", StringComparison.Ordinal))
                    return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }

    /// <summary>The sync tool from the newest app release (tags starting with "v"), checked against SHA256SUMS.txt.</summary>
    private static async Task<string> EnsureToolAsync(CancellationToken cancel)
    {
        var have = File.Exists(ToolVersion) ? File.ReadAllText(ToolVersion).Trim() : null;
        // look for a newer tool at most once a day
        if (have is not null && File.Exists(Tool) && DateTime.Now - File.GetLastWriteTime(ToolVersion) < TimeSpan.FromDays(1))
            return Tool;

        List<Release>? releases = null;
        try
        {
            releases = JsonSerializer.Deserialize<List<Release>>(
                await Http.GetStringAsync($"https://api.github.com/repos/{SyncRepo}/releases?per_page=20", cancel));
        }
        catch (HttpRequestException) when (File.Exists(Tool))
        {
            return Tool; // offline: keep the one we have
        }
        var newest = releases?.FirstOrDefault(r => r.Tag.StartsWith('v') && r.Assets.Any(a => a.Name == SyncAsset))
            ?? throw new IOException("no release of the shader sync tool was found");
        if (newest.Tag == have && File.Exists(Tool))
        {
            File.SetLastWriteTime(ToolVersion, DateTime.Now);
            return Tool;
        }

        var sums = newest.Assets.FirstOrDefault(a => a.Name == "SHA256SUMS.txt")
            ?? throw new IOException("the sync tool release has no SHA256SUMS.txt");
        var expected = (await Http.GetStringAsync(sums.Url, cancel)).Split('\n')
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == SyncAsset)?[0]
            ?? throw new IOException("SHA256SUMS.txt doesn't list the Linux tool");
        var bytes = await Http.GetByteArrayAsync(newest.Assets.First(a => a.Name == SyncAsset).Url, cancel);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("the downloaded sync tool doesn't match its checksum");

        Directory.CreateDirectory(Root);
        var temp = Tool + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, cancel);
        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(temp, Tool, overwrite: true);
        File.WriteAllText(ToolVersion, newest.Tag);
        return Tool;
    }

    // ---- Search ----

    /// <summary>
    /// Everything found for the game: its installable pack (Lichii games) and GitHub repositories that describe a
    /// shader cache for it. GitHub's search allows 10 queries a minute without an account, so this runs on request.
    /// </summary>
    public static async Task<List<ShaderCacheHit>> SearchAsync(Game game, string title, CancellationToken cancel = default)
    {
        var hits = new List<ShaderCacheHit>();
        var id = TitleId(game);

        if (SupportsPacks(game) && id is not null && await FindPackAsync(id, cancel) is { } pack)
            hits.Add(new ShaderCacheHit("Lichii Shader Sync", pack.Name ?? title,
                $"{pack.Total:N0} pipelines · cache v{pack.SourceVersion}", $"https://github.com/{SyncRepo}", true));

        var names = new List<string> { title };
        // "The Legend of Zelda: Ocarina of Time" also as "Ocarina of Time"
        var sub = title.Split([": ", " - "], 2, StringSplitOptions.TrimEntries);
        if (sub.Length == 2 && sub[1].Length >= 6)
            names.Add(sub[1]);

        var queries = names.Select(n => $"\"{n}\" shader in:name,description").ToList();
        if (id is not null)
            queries.Add($"{id} shader in:name,description,readme");

        var seen = new HashSet<string>(hits.Select(h => h.Url), StringComparer.OrdinalIgnoreCase);
        var found = new List<(Repo repo, int score)>();
        foreach (var q in queries)
        {
            var url = "https://api.github.com/search/repositories?per_page=15&q=" + Uri.EscapeDataString(q);
            using var response = await Http.GetAsync(url, cancel);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("GitHub's search limit was reached; try again in a minute");
            response.EnsureSuccessStatusCode();
            var result = JsonSerializer.Deserialize<RepoSearch>(await response.Content.ReadAsStringAsync(cancel));
            foreach (var repo in result?.Items ?? [])
            {
                var text = $"{repo.Name} {repo.Description}".ToLowerInvariant();
                // a cache, not a shader effect or a mod: "shader cache", "pipelines", or shaders for an emulator
                var forEmulator = Emulators.Any(text.Contains);
                var score = (text.Contains("cache") ? 2 : 0) + (text.Contains("pipeline") ? 2 : 0)
                    + (text.Contains("shaders") && forEmulator ? 1 : 0);
                if (score == 0 || repo.FullName.Equals(SyncRepo, StringComparison.OrdinalIgnoreCase) || !seen.Add(repo.Url))
                    continue;
                found.Add((repo, score));
            }
        }
        foreach (var (repo, _) in found.OrderByDescending(f => f.score).ThenByDescending(f => f.repo.Stars).Take(8))
            hits.Add(new ShaderCacheHit("GitHub", repo.FullName,
                $"★{repo.Stars} · updated {repo.Pushed:MMM d, yyyy}" + (string.IsNullOrWhiteSpace(repo.Description) ? "" : $" · {repo.Description.Trim()}"),
                repo.Url, false));
        return hits;
    }

    private static readonly string[] Emulators =
        ["emulator", "lichii", "eden", "yuzu", "suyu", "ryujinx", "sudachi", "citron", "azahar", "citra", "lime3ds", "pcsx2", "switch"];

    /// <summary>A web search for what GitHub doesn't have (forums, wikis, Discord mirrors).</summary>
    public static string WebSearchUrl(Game game, string title) =>
        "https://duckduckgo.com/?q=" + Uri.EscapeDataString($"\"{title}\" {PlatformInfo.Emulator(game.Platform)} shader cache");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zest", "1.0"));
        return client;
    }

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string Tag,
        [property: JsonPropertyName("assets")] List<Asset> Assets);

    private sealed record Asset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string Url);

    private sealed record RepoSearch([property: JsonPropertyName("items")] List<Repo>? Items);

    private sealed record Repo(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("full_name")] string FullName,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("html_url")] string Url,
        [property: JsonPropertyName("stargazers_count")] int Stars,
        [property: JsonPropertyName("pushed_at")] DateTime Pushed);

    [GeneratedRegex(@"\[([0-9A-Fa-f]{16})\]")]
    private static partial Regex TitleIdInName();

    [GeneratedRegex(@"(\d+) pipelines")]
    private static partial Regex PipelineCount();

    [GeneratedRegex(@"\(\+(\d+)\)")]
    private static partial Regex AddedCount();
}
