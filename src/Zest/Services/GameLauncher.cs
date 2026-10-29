using System.Diagnostics;
using System.Text;
using Zest.Models;

namespace Zest.Services;

public static class GameLauncher
{
    /// <summary>Starts the emulator for the game and returns when it exits, with how long it ran.</summary>
    public static async Task<TimeSpan> RunAsync(Game game, AppConfig config)
    {
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
