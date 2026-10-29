using Avalonia;
using Zest.Models;
using Zest.Services;

namespace Zest;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--probe", ..])
            return Probe(args[1..]);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();

    /// <summary>`zest --probe [files]`: prints what the scanner and metadata readers find, without opening a window.</summary>
    private static int Probe(string[] files)
    {
        var config = AppConfig.Load();
        var switchMeta = SwitchMetadata.TryCreate(config);
        var games = files.Length > 0
            ? files.Select(f => new Game { Path = f, Platform = Platform.Switch, Title = Game.TitleFromFileName(f) }).ToList()
            : LibraryScanner.Scan(config);

        foreach (var game in games)
        {
            var line = $"[{PlatformInfo.Short(game.Platform)}] {game.Title}";
            if (game.Platform == Platform.Switch && switchMeta is not null)
            {
                try
                {
                    var meta = switchMeta.Read(game.Path);
                    line += meta is { } m ? $"  ->  \"{m.title}\" icon={m.icon?.Length ?? 0}B" : "  ->  (unsupported format)";
                }
                catch (Exception e)
                {
                    line += $"  ->  ERROR {e.GetType().Name}: {e.Message}";
                }
            }
            Console.WriteLine(line);
        }
        return 0;
    }
}
