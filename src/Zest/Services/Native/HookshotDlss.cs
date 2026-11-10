using System.Diagnostics;

namespace Zest.Services.Native;

/// <summary>
/// Opt-in NVIDIA DLSS for Hookshot. Hookshot offers DLSS/DLAA only in a game built with NVIDIA's DLSS SDK, which is
/// NVIDIA's, under NVIDIA's licence: never shipped by Hookshot or Zest. When the user has accepted that licence in Zest,
/// and the installed Hookshot release supports it (its source has scripts/ngx_sdk.py), Zest runs that script to fetch the
/// SDK into ~/.local/share/zest/native/hookshot/ngx-sdk (it checks each file's SHA-256) and builds the game with
/// HOOKSHOT_NGX_SDK pointing there. The built game folder must not be shared (Hookshot's readme).
/// </summary>
public static class HookshotDlss
{
    public const string LicenceUrl = "https://github.com/NVIDIA/DLSS/blob/9981c6350dc58c0ce21f024fd65e31e851c31689/LICENSE.txt";
    private const string Repo = "NotRexed/Hookshot";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly Dictionary<string, bool> Supported = [];

    public static string SdkFolder => Path.Combine(AppPaths.Data, "native", "hookshot", "ngx-sdk");

    /// <summary>The user wants DLSS and has accepted NVIDIA's licence in Zest.</summary>
    public static bool Wanted(AppConfig config) => config.HookshotDlss && config.NvidiaDlssLicenceAccepted is not null;

    /// <summary>The game folder was built with the SDK: its ngx folder holds DLSS's runtime.</summary>
    public static bool GameHasDlss(string gameFolder) =>
        Directory.Exists(Path.Combine(gameFolder, "ngx"))
        && Directory.EnumerateFiles(Path.Combine(gameFolder, "ngx"), "libnvidia-ngx-dlss.so*").Any();

    /// <summary>Whether this Hookshot release can build with DLSS: its source at that tag has the SDK script.</summary>
    public static async Task<bool> ReleaseSupportsAsync(string tag, CancellationToken cancel)
    {
        lock (Supported)
            if (Supported.TryGetValue(tag, out var known))
                return known;
        bool ok;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, ScriptUrl(tag));
            using var response = await Http.SendAsync(request, cancel);
            ok = response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;   // offline: don't remember a guess
        }
        lock (Supported)
            Supported[tag] = ok;
        return ok;
    }

    private static string ScriptUrl(string tag) => $"https://raw.githubusercontent.com/{Repo}/{Uri.EscapeDataString(tag)}/scripts/ngx_sdk.py";

    /// <summary>The SDK in place (fetched with the release's own script, which checks each file), else fetches it.
    /// Only called when <see cref="Wanted"/>: the user accepted NVIDIA's licence in Zest.</summary>
    public static async Task EnsureSdkAsync(string tag, StreamWriter log, CancellationToken cancel)
    {
        if (Directory.Exists(SdkFolder) && Directory.EnumerateFiles(SdkFolder, "*", SearchOption.AllDirectories).Any(f => f.Contains("ngx", StringComparison.OrdinalIgnoreCase)))
            return;
        var scripts = Path.Combine(AppPaths.Cache, "hookshot-ngx", tag);
        Directory.CreateDirectory(scripts);
        var script = Path.Combine(scripts, "ngx_sdk.py");
        await File.WriteAllBytesAsync(script, await Http.GetByteArrayAsync(ScriptUrl(tag), cancel), cancel);
        Directory.CreateDirectory(SdkFolder);
        var psi = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = scripts };
        foreach (var a in new[] { "-I", script, "--platform", "linux", "--accept-nvidia-license", SdkFolder })
            psi.ArgumentList.Add(a);
        log.WriteLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} NVIDIA DLSS SDK (licence accepted in Zest): python3 {string.Join(' ', psi.ArgumentList)}");
        using var p = Process.Start(psi) ?? throw new IOException("Couldn't start python3 to fetch NVIDIA's DLSS SDK.");
        using var reg = cancel.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var output = p.StandardOutput.ReadToEndAsync(cancel);
        var errors = p.StandardError.ReadToEndAsync(cancel);
        await p.WaitForExitAsync(cancel);
        log.WriteLine(await output);
        log.WriteLine(await errors);
        if (p.ExitCode != 0)
            throw new IOException($"Fetching NVIDIA's DLSS SDK failed (exit {p.ExitCode}); see the build log.");
    }

    /// <summary>The builder's environment: build with the SDK when the user wants DLSS and it's fetched.</summary>
    public static void Apply(ProcessStartInfo psi, AppConfig config)
    {
        if (Wanted(config) && Directory.Exists(SdkFolder))
            psi.Environment["HOOKSHOT_NGX_SDK"] = SdkFolder;
    }
}
