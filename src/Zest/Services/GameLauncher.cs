using System.Diagnostics;
using System.Text;
using Zest.Models;

namespace Zest.Services;

public static class GameLauncher
{
    /// <summary>Starts the emulator for the game and returns when it exits, with how long it ran.</summary>
    public static async Task<TimeSpan> RunAsync(Game game, AppConfig config)
    {
        if (game.Local is { } local)
            return await RunLocalAsync(game.Path, local);

        var args = SplitCommand(config.For(game.Platform).Command);
        if (args.Count == 0)
            throw new InvalidOperationException($"No {PlatformInfo.Emulator(game.Platform)} command is set. Add one in Settings.");

        var hasPlaceholder = args.Any(a => a.Contains("{rom}"));
        var psi = new ProcessStartInfo(AppPaths.ExpandHome(args[0])) { UseShellExecute = false };
        foreach (var a in args.Skip(1))
            psi.ArgumentList.Add(a.Replace("{rom}", game.Path));
        if (!hasPlaceholder)
            psi.ArgumentList.Add(game.Path);

        var started = Stopwatch.StartNew();
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {psi.FileName}.");
        await process.WaitForExitAsync();
        return started.Elapsed;
    }

    /// <summary>Starts a native build in its own folder, its output going to its log file when one is set.</summary>
    private static async Task<TimeSpan> RunLocalAsync(string exe, LocalPort port)
    {
        var dir = AppPaths.ExpandHome(port.WorkingDirectory);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = dir.Length > 0 ? dir : Path.GetDirectoryName(exe)!,
        };
        foreach (var a in port.Arguments)
            psi.ArgumentList.Add(a);

        var log = AppPaths.ExpandHome(port.Log);
        StreamWriter? writer = null;
        if (log.Length > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            writer = new StreamWriter(log, append: false) { AutoFlush = true };
            psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        }

        var started = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
            if (writer is not null)
            {
                var gate = new object();
                void Write(object _, DataReceivedEventArgs e)
                {
                    if (e.Data is not null)
                        lock (gate)
                            writer.WriteLine(e.Data);
                }
                process.OutputDataReceived += Write;
                process.ErrorDataReceived += Write;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            await process.WaitForExitAsync();
            return started.Elapsed;
        }
        finally
        {
            writer?.Dispose();
        }
    }

    /// <summary>Splits a command line on spaces, honouring "double" and 'single' quotes. No shell is involved.</summary>
    public static List<string> SplitCommand(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var inToken = false;

        foreach (var ch in command)
        {
            if (quote is not null)
            {
                if (ch == quote)
                    quote = null;
                else
                    current.Append(ch);
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
                inToken = true;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (inToken)
                    parts.Add(current.ToString());
                current.Clear();
                inToken = false;
            }
            else
            {
                current.Append(ch);
                inToken = true;
            }
        }
        if (inToken)
            parts.Add(current.ToString());
        return parts;
    }
}
