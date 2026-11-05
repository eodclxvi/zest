using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zest.Services.Native;

/// <summary>What Zest installed for a port: the official release AppImage, checked against GitHub's SHA-256.</summary>
public sealed record InstalledRelease(string Tag, string Asset, string Sha256, string AppImage, DateTime Installed);

/// <summary>The game files Zest extracted for a port, and where from.</summary>
public sealed record PreparedFiles(int Format, string TitleId, string Version, string Source, long Bytes, DateTime Prepared);

/// <summary>
/// Installs ports and prepares their game files under ~/.local/share/zest/native/&lt;port id&gt;/:
/// release/ holds the AppImage, gamefiles/ the extracted exefs/ and romfs/. Each step writes its JSON marker
/// last, so a step that was interrupted is simply redone.
/// </summary>
public static class NativePorts
{
    /// <summary>Bump when Extract changes what it writes, so older prepared folders are redone.</summary>
    private const int GameFilesFormat = 1;

    private static readonly HttpClient Http = CreateClient();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Root(NativePort port) => Path.Combine(AppPaths.Data, "native", port.Id);
    public static string GameFilesFolder(NativePort port) => Path.Combine(Root(port), "gamefiles");
    private static string ReleaseMarker(NativePort port) => Path.Combine(Root(port), "release.json");
    private static string GameFilesMarker(NativePort port) => Path.Combine(GameFilesFolder(port), "zest-gamefiles.json");

    public static InstalledRelease? Installed(NativePort port)
    {
        var release = Read<InstalledRelease>(ReleaseMarker(port));
        return release is not null && File.Exists(release.AppImage) ? release : null;
    }

    public static PreparedFiles? Prepared(NativePort port)
    {
        var files = Read<PreparedFiles>(GameFilesMarker(port));
        if (files is null || files.Format != GameFilesFormat)
            return null;
        var exefs = Path.Combine(GameFilesFolder(port), "exefs");
        return port.ExeFsFiles.All(f => File.Exists(Path.Combine(exefs, f))) ? files : null;
    }

    /// <summary>Downloads the latest official release AppImage, refusing it unless it matches GitHub's SHA-256 digest.</summary>
    public static async Task<InstalledRelease> InstallAsync(NativePort port, IProgress<(long done, long total)>? progress, CancellationToken cancel)
    {
        using var meta = await Http.GetAsync($"https://api.github.com/repos/{port.Repo}/releases/latest", cancel);
        meta.EnsureSuccessStatusCode();
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(await meta.Content.ReadAsStreamAsync(cancel), cancellationToken: cancel)
            ?? throw new InvalidDataException("GitHub returned no release.");

        var asset = release.Assets.FirstOrDefault(a => port.Asset.IsMatch(a.Name))
            ?? throw new InvalidDataException($"{port.Name} {release.Tag} has no Linux x86-64 AppImage.");
        if (asset.Digest is not { } digest || !digest.StartsWith("sha256:"))
            throw new InvalidDataException($"GitHub lists no SHA-256 for {asset.Name}, so Zest won't install it.");
        var expected = digest["sha256:".Length..].ToLowerInvariant();

        var folder = Path.Combine(Root(port), "release", release.Tag);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, asset.Name);
        var partial = target + ".part";

        using (var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancel);
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancel)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                sha.AppendData(buffer, 0, read);
                done += read;
                progress?.Report((done, asset.Size));
            }

            var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (actual != expected)
            {
                output.Close();
                File.Delete(partial);
                throw new InvalidDataException($"{asset.Name} failed its checksum (expected {expected}, got {actual}). Nothing was installed.");
            }
        }

        File.Move(partial, target, overwrite: true);
        File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

        var installed = new InstalledRelease(release.Tag, asset.Name, expected, target, DateTime.Now);
        RemoveOtherReleases(port, release.Tag);
        Write(ReleaseMarker(port), installed);
        return installed;
    }

    /// <summary>The tag of the newest official release, or null when GitHub can't be reached.</summary>
    public static async Task<string?> LatestTagAsync(NativePort port, CancellationToken cancel)
    {
        try
        {
            using var meta = await Http.GetAsync($"https://api.github.com/repos/{port.Repo}/releases/latest", cancel);
            if (!meta.IsSuccessStatusCode)
                return null;
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(await meta.Content.ReadAsStreamAsync(cancel), cancellationToken: cancel);
            return release?.Tag;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Extracts the base game's exefs/ and romfs/ for the port, after checking it is a version the port accepts.</summary>
    public static PreparedFiles Prepare(NativePort port, SwitchContent content, string dumpPath,
        IProgress<(long done, long total)>? progress, CancellationToken cancel)
    {
        var app = content.Identify(dumpPath) ?? throw new InvalidDataException("No base game was found in this file.");
        if (app.TitleId != port.TitleId)
            throw new InvalidDataException($"This file is title {app.TitleId:X16}, not {port.TitleIdText}.");
        if (!port.Versions.Contains(app.DisplayVersion))
            throw new InvalidDataException($"{port.Name} accepts version {string.Join(", ", port.Versions)}; this file's base game is {app.DisplayVersion}.");

        var folder = GameFilesFolder(port);
        content.Extract(dumpPath, folder, progress, cancel);

        var missing = port.ExeFsFiles.Where(f => !File.Exists(Path.Combine(folder, "exefs", f))).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"The extracted ExeFS is missing {string.Join(", ", missing)}.");

        var bytes = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        var files = new PreparedFiles(GameFilesFormat, app.TitleId.ToString("X16"), app.DisplayVersion, dumpPath, bytes, DateTime.Now);
        Write(GameFilesMarker(port), files);
        return files;
    }

    /// <summary>Deletes Zest's extracted copy. The port keeps its own copy once its setup screen has imported them.</summary>
    public static void RemoveGameFiles(NativePort port)
    {
        foreach (var folder in new[] { GameFilesFolder(port), GameFilesFolder(port) + ".partial" })
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
    }

    // ---- ports with a command-line setup (NativePort.Setup: TotK Recomp)

    /// <summary>Where the setup builds the game (it stays portable: saves, settings and mods live there too).</summary>
    public static string GameFolder(NativePort port) => Path.Combine(Root(port), "game");

    /// <summary>The setup has finished a build in the game folder.</summary>
    public static bool IsGameBuilt(NativePort port) =>
        port.Setup is { } s && File.Exists(Path.Combine(GameFolder(port), s.GameExecutable)) &&
        File.Exists(Path.Combine(GameFolder(port), "totk-install.json"));

    /// <summary>The release's setup, unpacked from its AppImage once (release/&lt;tag&gt;/setup).</summary>
    private static async Task<string> UnpackSetupAsync(InstalledRelease release, CancellationToken cancel)
    {
        var folder = Path.Combine(Path.GetDirectoryName(release.AppImage)!, "setup");
        if (File.Exists(Path.Combine(folder, "python", "bin", "python3")))
            return folder;
        var work = Path.GetDirectoryName(release.AppImage)!;
        var unpacked = Path.Combine(work, "squashfs-root");
        if (Directory.Exists(unpacked))
            Directory.Delete(unpacked, true);
        var psi = new ProcessStartInfo(release.AppImage, "--appimage-extract")
        {
            UseShellExecute = false, WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using (var p = Process.Start(psi) ?? throw new IOException("Could not unpack the setup."))
        {
            _ = p.StandardOutput.ReadToEndAsync(cancel);
            _ = p.StandardError.ReadToEndAsync(cancel);
            await p.WaitForExitAsync(cancel);
            if (p.ExitCode != 0 || !Directory.Exists(unpacked))
                throw new IOException($"Unpacking {release.Asset} failed (exit {p.ExitCode}).");
        }
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
        Directory.Move(unpacked, folder);
        return folder;
    }

    /// <summary>
    /// Runs the release's setup on the player's dump and update. It reports one JSON event per line (start, plan, step,
    /// progress, space, warning, error, end); the bar is the steps' weights (CliSetup.StepWeights). Steps it finished
    /// before are skipped by the setup itself, so this also resumes and updates. Cancelling sends SIGTERM, which the
    /// setup handles (it stops between files and keeps what is finished).
    /// </summary>
    public static async Task RunSetupAsync(NativePort port, string keys, string dump, string update,
        IProgress<(double percent, string text)> progress, CancellationToken cancel)
    {
        var setup = port.Setup ?? throw new InvalidOperationException($"{port.Name} has no command-line setup.");
        var release = Installed(port) ?? throw new InvalidOperationException($"{port.Name} isn't installed.");
        progress.Report((0, "Unpacking the setup…"));
        var folder = await UnpackSetupAsync(release, cancel);

        var psi = new ProcessStartInfo(Path.Combine(folder, "python", "bin", "python3"))
        {
            UseShellExecute = false, WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-m", "installer", "--base", dump, "--update", update, "--keys", keys, "--lang", "en-US", "--dest", GameFolder(port) })
            psi.ArgumentList.Add(a);
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        Directory.CreateDirectory(Root(port));
        await using var log = new StreamWriter(Path.Combine(Root(port), "setup-events.jsonl"), append: false) { AutoFlush = true };

        double total = setup.StepWeights.Values.Sum(), finished = 0;
        var done = new HashSet<string>();
        string? error = null;
        using var p = Process.Start(psi) ?? throw new IOException("Could not start the setup.");
        _ = Task.Run(async () => { while (await p.StandardError.ReadLineAsync() is { } l) await log.WriteLineAsync("# " + l); });
        using var reg = cancel.Register(() => { try { Process.Start("kill", ["-TERM", p.Id.ToString()])?.WaitForExit(2000); } catch (Exception) { } });
        while (await p.StandardOutput.ReadLineAsync() is { } line)
        {
            await log.WriteLineAsync(line);
            JsonElement e;
            try { e = JsonDocument.Parse(line).RootElement; } catch (JsonException) { continue; }
            var type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
            var step = e.TryGetProperty("step", out var st) ? st.GetString() ?? "" : "";
            var weight = setup.StepWeights.GetValueOrDefault(step, 0);
            var name = setup.StepNames.GetValueOrDefault(step, step);
            switch (type)
            {
                case "plan" when e.TryGetProperty("skip", out var skip) && skip.ValueKind == JsonValueKind.Object:
                    foreach (var s in skip.EnumerateObject())
                        if (done.Add(s.Name)) finished += setup.StepWeights.GetValueOrDefault(s.Name, 0);
                    break;
                case "step" when e.TryGetProperty("state", out var state):
                    if (state.GetString() is "done" or "skipped" && done.Add(step))
                        finished += weight;
                    progress.Report((100 * finished / total, state.GetString() == "started" ? name + "…" : name));
                    break;
                case "progress":
                    double dn = e.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
                    double tn = e.TryGetProperty("total", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetDouble() : 0;
                    double frac = tn > 0 ? Math.Min(1, dn / tn) : 0;
                    var unit = e.TryGetProperty("unit", out var u) ? u.GetString() : null;
                    var detail = tn > 0 ? unit == "bytes" ? $" ({dn / (1L << 30):0.0} of {tn / (1L << 30):0.0} GB)" : $" ({dn:0} of {tn:0})" : "";
                    progress.Report((100 * (finished + weight * frac) / total, name + detail));
                    break;
                case "error":
                    error = e.TryGetProperty("message", out var m) ? m.ToString() : line;
                    break;
            }
        }
        await p.WaitForExitAsync(CancellationToken.None);
        cancel.ThrowIfCancellationRequested();
        if (p.ExitCode == 3)
            throw new OperationCanceledException();
        if (p.ExitCode != 0)
            throw new IOException($"{port.Name}'s setup stopped (exit {p.ExitCode}){(error is null ? "" : $": {error}")}. Events: {Path.Combine(Root(port), "setup-events.jsonl")}");
        if (!IsGameBuilt(port))
            throw new IOException($"{port.Name}'s setup finished without {setup.GameExecutable} in {GameFolder(port)}.");
        progress.Report((100, "Ready"));
    }

    /// <summary>Starts the port and returns when it exits, with how long it ran: the built game for a port with a
    /// command-line setup, otherwise the release's AppImage.</summary>
    public static async Task<TimeSpan> RunAsync(NativePort port)
    {
        var release = Installed(port) ?? throw new InvalidOperationException($"{port.Name} isn't installed.");
        var psi = port.Setup is { } cli
            ? new ProcessStartInfo(Path.Combine(GameFolder(port), cli.GameExecutable)) { UseShellExecute = false, WorkingDirectory = GameFolder(port) }
            : new ProcessStartInfo(release.AppImage)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(release.AppImage)!,
        };
        // Without FUSE an AppImage can't mount itself; this makes it unpack and run instead of failing.
        if (!File.Exists("/usr/bin/fusermount3") && !File.Exists("/usr/bin/fusermount"))
            psi.Environment["APPIMAGE_EXTRACT_AND_RUN"] = "1";

        var started = Stopwatch.StartNew();
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {port.Name}.");
        await process.WaitForExitAsync();
        return started.Elapsed;
    }

    private static void RemoveOtherReleases(NativePort port, string keep)
    {
        var releases = Path.Combine(Root(port), "release");
        foreach (var dir in Directory.EnumerateDirectories(releases))
            if (Path.GetFileName(dir) != keep)
                Directory.Delete(dir, recursive: true);
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zest", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string Tag,
        [property: JsonPropertyName("assets")] List<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("browser_download_url")] string Url);
}
