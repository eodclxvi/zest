using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Zest.Services.Native;

public sealed record BuildProgress(string Step, double Percent);

/// <summary>
/// "Install native": turns a dump into a playable native build of a <see cref="SourcePort"/>.
/// 1. the project folder (cloned from its sibling when missing; Python venv, console fonts linked from it),
/// 2. game files: exefs/ and romfs.bin from the dump (+ update), written once and linked into the project,
/// 3. `make gen`: the game code translated to C (only changed files are rewritten),
/// 4. `make`: the runtime and the game library compiled (only what changed),
/// then the caller lists it as a native game. Every step is skipped when its result is already there, so a second
/// click after a fix only redoes what that fix touched. The output goes to native/&lt;id&gt;/build.log.
/// </summary>
public static partial class NativeBuilder
{
    public static string Root(SourcePort port) => Path.Combine(AppPaths.Data, "native", port.Id);
    public static string LogPath(SourcePort port) => Path.Combine(Root(port), "build.log");
    public static string Project(SourcePort port) => AppPaths.ExpandHome(port.ProjectDir);
    public static string Executable(SourcePort port) => Path.Combine(Project(port), "build", "lasrecomp");

    /// <summary>A finished build: the executable and the game library both exist.</summary>
    public static bool IsBuilt(SourcePort port) =>
        File.Exists(Executable(port)) && File.Exists(Path.Combine(Project(port), "build", "lasgame.so"));

    // Weights of the steps in the progress bar (percent)
    private const double FilesEnd = 30, GenEnd = 38, CompileEnd = 99;

    public static async Task BuildAsync(SourcePort port, AppConfig config, string dump, string? update,
        IProgress<BuildProgress> progress, CancellationToken cancel, IReadOnlyList<string>? dlcs = null)
    {
        Directory.CreateDirectory(Root(port));
        await using var log = new StreamWriter(LogPath(port), append: false) { AutoFlush = true };
        await log.WriteLineAsync($"[zest] Install native: {port.Name} from {dump}{(update is null ? "" : $" + {update}")}, {DateTime.Now:u}");

        // 1. project folder
        progress.Report(new("Preparing the project", 0));
        var project = Project(port);
        if (!Directory.Exists(project))
        {
            if (port.CloneFrom is not { } from)
                throw new IOException($"The project folder {project} is missing.");
            var src = AppPaths.ExpandHome(from.Dir);
            await RunAsync(log, "git", ["clone", "-q", "-o", Path.GetFileName(src), "-b", from.Branch, src, project], null, null, cancel);
            await RunAsync(log, "git", ["-C", project, "switch", "-q", "-c", port.Branch], null, null, cancel);
        }
        LinkFromSibling(port, project, ".venv");
        LinkFonts(port, project);
        SeedFromSibling(port, project, log);

        // 2. game files
        var extracted = Path.Combine(project, "extracted");
        Directory.CreateDirectory(extracted);
        var keys = AppPaths.ExpandHome(config.ProdKeys);
        var titleKeys = AppPaths.ExpandHome(config.TitleKeys);
        using (var image = await Task.Run(() => new GameImage(keys, titleKeys, dump, update), cancel))
        {
            var build = image.MainBuildId();
            var romSize = image.RomFsSize();
            await log.WriteLineAsync($"[zest] main build {build}, romfs {romSize} bytes");
            if (port.KnownBuilds.Count > 0 && !port.KnownBuilds.ContainsKey(build))
                await log.WriteLineAsync($"[zest] note: build {build} isn't one this port was made for ({string.Join(", ", port.KnownBuilds.Values)}); trying anyway");

            var exefs = Path.Combine(extracted, "exefs");
            var romfs = Path.Combine(extracted, "romfs.bin");
            if (MainBuildIdAt(exefs) == build && FileSize(romfs) == romSize)
                await log.WriteLineAsync("[zest] game files already in place");
            else
            {
                var files = Path.Combine(Root(port), "gamefiles");
                Directory.CreateDirectory(files);
                progress.Report(new("Writing the game code files", 1));
                var newExefs = Path.Combine(files, "exefs");
                if (Directory.Exists(newExefs))
                    Directory.Delete(newExefs, true);
                await Task.Run(() => image.WriteExeFs(newExefs), cancel);
                var newRomfs = Path.Combine(files, "romfs.bin");
                if (FileSize(newRomfs) != romSize)
                {
                    var p = new Progress<(long done, long total)>(t =>
                        progress.Report(new($"Writing the game data ({t.done >> 30} of {t.total >> 30} GB)", 1 + (FilesEnd - 1) * t.done / Math.Max(1, t.total))));
                    await Task.Run(() => image.WriteRomFs(newRomfs, p, cancel), cancel);
                }
                Relink(exefs, newExefs);
                Relink(romfs, newRomfs);
                await log.WriteLineAsync($"[zest] game files written to {files}");
            }
        }

        // 2b. DLC: each package's data as extracted/aoc/<title id>.romfs (what the runtime's aoc:u lists and mounts)
        if (dlcs is { Count: > 0 })
        {
            var aoc = Path.Combine(extracted, "aoc");
            var have = Directory.Exists(aoc) ? Directory.GetFiles(aoc, "*.romfs").Length : 0;
            if (have >= dlcs.Count)
                await log.WriteLineAsync($"[zest] DLC already in place ({have})");
            else
            {
                progress.Report(new("Writing the DLC", FilesEnd));
                var dest = Path.Combine(Root(port), "aoc");
                foreach (var d in dlcs)
                {
                    var ids = await Task.Run(() => GameImage.WriteAddOn(keys, titleKeys, d, dest, cancel), cancel);
                    await log.WriteLineAsync($"[zest] DLC {Path.GetFileName(d)}: {string.Join(", ", ids.Select(i => i.ToString("X16")))}");
                }
                if (Directory.Exists(aoc) && new DirectoryInfo(aoc).LinkTarget is null)
                    await log.WriteLineAsync($"[zest] {aoc} is a real folder: DLC written to {dest} instead, not linked");
                else
                    Relink(aoc, dest);
            }
        }

        // 3. game code -> C
        var make = new List<string> { "-C", project };
        make.AddRange(port.MakeArgs.Select(a => a.Replace("=~/", "=" + AppPaths.Home + "/")));  // (DEPS=~/...)
        progress.Report(new("Translating the game code", FilesEnd));
        var genLine = GenProgress();
        await RunAsync(log, "make", [.. make, "gen"], line =>
        {
            // (one pass per module, main the longest: the bar follows the module being translated)
            if (genLine.Match(line) is { Success: true } m && double.TryParse(m.Groups[3].Value, out var total) && total > 0)
                progress.Report(new($"Translating the game code ({m.Groups[1].Value})",
                    FilesEnd + (GenEnd - FilesEnd) * Math.Min(1, double.Parse(m.Groups[2].Value) / total)));
        }, null, cancel);

        // 4. compile: the dry run counts what is out of date, then each compiler line is one step
        var jobs = Math.Max(1, Environment.ProcessorCount);
        var todo = 0;
        await RunAsync(log, "make", [.. make, "-n", "all"], line => { if (IsCompile(line)) todo++; }, null, cancel, quiet: true);
        await log.WriteLineAsync($"[zest] {todo} files to compile with {jobs} jobs");
        // (the generated code compiles silently, @ in the Makefile: progress = object files written since the start)
        progress.Report(new(todo == 0 ? "Already compiled" : $"Compiling (0 of {todo} files)", GenEnd));
        var started = DateTime.UtcNow.AddSeconds(-1);
        var objDir = Path.Combine(project, "build");
        using var tick = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            while (!tick.IsCancellationRequested && todo > 0)
            {
                try { await Task.Delay(1000, tick.Token); } catch (OperationCanceledException) { break; }
                var done = Directory.Exists(objDir)
                    ? Directory.EnumerateFiles(objDir, "*.o", SearchOption.AllDirectories).Count(f => File.GetLastWriteTimeUtc(f) >= started)
                    : 0;
                progress.Report(new($"Compiling ({Math.Min(done, todo)} of {todo} files)", GenEnd + (CompileEnd - GenEnd) * Math.Min(1, done / (double)todo)));
            }
        });
        try
        {
            await RunAsync(log, "make", [.. make, $"-j{jobs}", "all"], null, null, cancel);
        }
        finally
        {
            tick.Cancel();
            await watcher;
        }
        if (!IsBuilt(port))
            throw new IOException("The build finished without the game executable; see the build log.");
        progress.Report(new("Done", 100));
        await log.WriteLineAsync($"[zest] done {DateTime.Now:u}");
    }

    private static bool IsCompile(string line) =>
        (line.StartsWith("clang ", StringComparison.Ordinal) || line.StartsWith("clang++ ", StringComparison.Ordinal) ||
         line.StartsWith("cc ", StringComparison.Ordinal)) && line.Contains(" -c ", StringComparison.Ordinal);

    // gen.py prints "<module>: progress <done>/<total> files <n>"; groups: module, done, total
    [GeneratedRegex(@"^(\w+): progress (\d+)/(\d+)")]
    private static partial Regex GenProgress();

    private static string? MainBuildIdAt(string exefs)
    {
        var main = Path.Combine(exefs, "main");
        if (!File.Exists(main))
            return null;
        using var f = File.OpenRead(main);
        var id = new byte[8];
        f.Seek(0x40, SeekOrigin.Begin);
        return f.Read(id, 0, 8) == 8 ? Convert.ToHexString(id) : null;
    }

    // (through a symlink: FileInfo.Length of a link is the link's own size)
    private static long FileSize(string path)
    {
        if (!File.Exists(path))
            return -1;
        var info = new FileInfo(path);
        return (info.ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? info).Length;
    }

    /// <summary>Points path (a symlink, or missing) at target; a real file or folder there is left alone.</summary>
    private static void Relink(string path, string target)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (!File.Exists(path) && !Directory.Exists(path)))
        {
            if (info.LinkTarget is not null)
                File.Delete(path);
            File.CreateSymbolicLink(path, target);
            return;
        }
        throw new IOException($"{path} is a real file or folder, not a link: move it aside first.");
    }

    // The Python venv and the console's shared fonts (dumped from the user's own firmware) come from the sibling
    // project the port was cloned from.
    private static void LinkFromSibling(SourcePort port, string project, string name)
    {
        var path = Path.Combine(project, name);
        if (File.Exists(path) || Directory.Exists(path) || port.CloneFrom is not { } from)
            return;
        var src = Path.Combine(AppPaths.ExpandHome(from.Dir), name);
        if (Directory.Exists(src))
            File.CreateSymbolicLink(path, src);
    }

    private static void LinkFonts(SourcePort port, string project)
    {
        if (port.CloneFrom is not { } from)
            return;
        var src = Path.Combine(AppPaths.ExpandHome(from.Dir), "fonts");
        var dst = Path.Combine(project, "fonts");
        if (!Directory.Exists(src))
            return;
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src, "*.bfttf"))
        {
            var target = new FileInfo(f).LinkTarget is { } t ? Path.GetFullPath(t, src) : f;
            var link = Path.Combine(dst, Path.GetFileName(f));
            if (!File.Exists(link))
                File.CreateSymbolicLink(link, target);
        }
    }

    // A new project starts with the sibling's settings (resolution, anti-aliasing, frame rate: without them a port runs at
    // 720p with no AA) and its compiled shaders (cache/spirv, one file per shader hash: games on one engine share many,
    // so the first play compiles far fewer - Scarlet shared 176 of Violet's). Nothing already there is replaced.
    private static void SeedFromSibling(SourcePort port, string project, StreamWriter log)
    {
        if (port.CloneFrom is not { } from)
            return;
        var sibling = AppPaths.ExpandHome(from.Dir);
        var ini = Path.Combine(project, PortControls.FileName);
        var siblingIni = Path.Combine(sibling, PortControls.FileName);
        if (!File.Exists(ini) && File.Exists(siblingIni))
        {
            File.Copy(siblingIni, ini);
            log.WriteLine($"[zest] settings copied from {siblingIni}");
        }
        var src = Path.Combine(sibling, "cache", "spirv");
        if (!Directory.Exists(src))
            return;
        var dst = Path.Combine(project, "cache", "spirv");
        Directory.CreateDirectory(dst);
        var copied = 0;
        foreach (var f in Directory.EnumerateFiles(src, "*.spv"))
        {
            var t = Path.Combine(dst, Path.GetFileName(f));
            if (File.Exists(t))
                continue;
            File.Copy(f, t);
            copied++;
        }
        if (copied > 0)
            log.WriteLine($"[zest] {copied} compiled shaders seeded from {src}");
    }

    private static async Task RunAsync(StreamWriter log, string exe, IReadOnlyList<string> args, Action<string>? onLine,
        string? workDir, CancellationToken cancel, bool quiet = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir ?? AppPaths.Home,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        await log.WriteLineAsync($"[zest] $ {exe} {string.Join(' ', args)}");
        using var p = Process.Start(psi) ?? throw new IOException($"Could not start {exe}.");
        var gate = new object();
        var tail = new Queue<string>();
        void Line(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null)
                return;
            lock (gate)
            {
                if (!quiet)
                    log.WriteLine(e.Data);
                tail.Enqueue(e.Data);
                if (tail.Count > 8)
                    tail.Dequeue();
                onLine?.Invoke(e.Data);
            }
        }
        p.OutputDataReceived += Line;
        p.ErrorDataReceived += Line;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        if (p.ExitCode != 0)
        {
            string last;
            lock (gate)
                last = string.Join("\n", tail);
            throw new IOException($"{exe} {args.LastOrDefault()} failed (exit {p.ExitCode}):\n{last}");
        }
    }
}
