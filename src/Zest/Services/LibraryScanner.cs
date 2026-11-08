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
        var library = SwitchAddOns.Nest(games);

        // (after Classify: it reads Switch files' content meta, which a native executable doesn't have)
        foreach (var port in config.LocalPorts)
        {
            var exe = AppPaths.ExpandHome(port.Executable);
            if (!File.Exists(exe) || !seen.Add(exe))
                continue;
            var game = new Game { Path = exe, Platform = port.Platform, Title = port.Title, Local = port };
            RepairArtFrom(game, library);
            library.Add(game);
        }
        return library;
    }

    /// <summary>
    /// A native build's icon comes from a dump of the game (LocalPort.ArtFrom). When that file was renamed or
    /// redumped, the library's base game of the same title ("Pokémon Sword" = "Pokemon Sword [0100ABF008968000][v0]")
    /// takes its place, and the cached "no art" mark is dropped so the icon is read again.
    /// </summary>
    private static void RepairArtFrom(Game game, List<Game> library)
    {
        var port = game.Local!;
        if (port.ArtFrom.Length > 0 && File.Exists(AppPaths.ExpandHome(port.ArtFrom)))
            return;
        var want = TitleKey(port.Title);
        var dump = library.FirstOrDefault(g => g.Local is null && g.Platform == port.Platform && g.Kind == ContentKind.Game
            && TitleKey(Game.TitleFromFileName(g.Path)) == want);
        if (dump is null)
            return;
        port.ArtFrom = dump.Path;
        ArtService.Forget(game);
    }

    /// <summary>"Pokémon Sword" and "Pokemon Sword" alike: accents, case and punctuation dropped.</summary>
    public static string TitleKey(string title) =>
        new(title.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => char.IsLetterOrDigit(c) && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(char.ToLowerInvariant).ToArray());

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
