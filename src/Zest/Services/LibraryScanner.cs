using Zest.Models;

namespace Zest.Services;

public static class LibraryScanner
{
    public static List<Game> Scan(AppConfig config)
    {
        var games = new List<Game>();
        var seen = new HashSet<string>();

        foreach (var platform in PlatformInfo.All)
        {
            var emu = config.For(platform);
            var extensions = PlatformInfo.Extensions(platform);
            var excluded = emu.Excluded.Select(AppPaths.ExpandHome).ToHashSet();

            foreach (var folder in emu.Folders.Select(AppPaths.ExpandHome).Where(Directory.Exists))
            {
                var files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 6,
                });

                foreach (var file in files)
                {
                    if (!extensions.Contains(Path.GetExtension(file)) || excluded.Contains(file) || !seen.Add(file))
                        continue;
                    // .gz is only a PS2 image when it says so.
                    if (platform == Platform.PS2 && file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                        && !file.EndsWith(".iso.gz", StringComparison.OrdinalIgnoreCase))
                        continue;

                    games.Add(new Game { Path = file, Platform = platform, Title = Game.TitleFromFileName(file) });
                }
            }
        }

        if (config.For(Platform.N3DS).IncludeInstalled)
            games.AddRange(AzaharInstalled().Where(g => seen.Add(g.Path)));

        SwitchAddOns.Classify(games, config);
        return SwitchAddOns.Nest(games);
    }

    /// <summary>Applications installed into Azahar's virtual SD card (title/00040000/&lt;id&gt;/content/*.app).</summary>
    private static IEnumerable<Game> AzaharInstalled()
    {
        var sdmcRoots = new[]
        {
            Path.Combine(AppPaths.Home, ".var/app/org.azahar_emu.Azahar/data/azahar-emu/sdmc/Nintendo 3DS"),
            Path.Combine(AppPaths.Home, ".local/share/azahar-emu/sdmc/Nintendo 3DS"),
        };

        foreach (var root in sdmcRoots.Where(Directory.Exists))
        {
            // Nintendo 3DS/<id0>/<id1>/title/00040000/<title>/content
            var contentDirs = Directory.EnumerateDirectories(root)
                .SelectMany(Directory.EnumerateDirectories)
                .Select(d => Path.Combine(d, "title", "00040000"))
                .Where(Directory.Exists)
                .SelectMany(Directory.EnumerateDirectories)
                .Select(d => Path.Combine(d, "content"))
                .Where(Directory.Exists);

            foreach (var dir in contentDirs)
            {
                // The game's main content is the .app that has an ExeFS (manuals and DLC data don't).
                foreach (var app in Directory.EnumerateFiles(dir, "*.app").Order())
                {
                    N3dsMetadata.Info? info;
                    try
                    {
                        info = N3dsMetadata.Read(app);
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    if (info is not { HasExeFs: true })
                        continue;

                    yield return new Game
                    {
                        Path = app,
                        Platform = Platform.N3DS,
                        Title = info.Title ?? $"3DS title {info.TitleId:X16}",
                        IsInstalled = true,
                    };
                    break;
                }
            }
        }
    }
}
