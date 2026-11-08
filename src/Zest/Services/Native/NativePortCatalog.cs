using System.Text.RegularExpressions;
using Zest.Models;

namespace Zest.Services.Native;

/// <summary>
/// A community static recompilation of one game: a native PC build of the game, made on this machine from the
/// player's own game files. Zest installs the project's official release and hands it a folder it prepares itself.
/// </summary>
public sealed record NativePort
{
    /// <summary>Folder name under Zest's data folder; never changes once released.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
    public required string Repo { get; init; }
    public required Platform Platform { get; init; }
    public required ulong TitleId { get; init; }

    /// <summary>Base-game versions (the control.nacp display version) the port accepts. Zest only extracts the base game.</summary>
    public required IReadOnlySet<string> Versions { get; init; }

    /// <summary>ExeFS files the port's setup screen checks for.</summary>
    public required IReadOnlyList<string> ExeFsFiles { get; init; }

    /// <summary>Picks the Linux x86-64 AppImage from a release's assets.</summary>
    public required Regex Asset { get; init; }

    public required string Status { get; init; }
    public required string Licence { get; init; }
    public required string Summary { get; init; }

    /// <summary>Shown under the card, as the project asks to be credited and described.</summary>
    public required string Credit { get; init; }

    /// <summary>Ports whose release builds the game itself through a command-line setup (TotK Recomp): Zest runs it
    /// with the player's dump (and update) and shows its progress; null for ports with their own setup screen.</summary>
    public CliSetup? Setup { get; init; }

    /// <summary>Ports whose release sets up a folder Zest prepares (exefs/ and romfs/ with the update applied) when
    /// started with an environment variable naming it (Savage Wind); null otherwise.</summary>
    public FolderSetup? Extract { get; init; }

    public string ProjectUrl => $"https://github.com/{Repo}";
    public string TitleIdText => TitleId.ToString("X16");
}

/// <summary>A release's command-line setup: unpacked from the AppImage, run as
/// <c>python3 -m installer --base --update --keys --lang --dest</c>, one JSON event per line on stdout.</summary>
public sealed record CliSetup
{
    /// <summary>The game executable in the install folder.</summary>
    public required string GameExecutable { get; init; }
    /// <summary>The update versions the setup accepts (it needs one).</summary>
    public required IReadOnlySet<string> UpdateVersions { get; init; }
    /// <summary>Share of the progress bar per setup step (the setup's own step names).</summary>
    public required IReadOnlyDictionary<string, double> StepWeights { get; init; }
    /// <summary>The setup's description of a step, for the progress line.</summary>
    public required IReadOnlyDictionary<string, string> StepNames { get; init; }
}

/// <summary>A release that sets up the game from a folder given in an environment variable, headless, then exits
/// (Savage Wind: <c>SAVAGEWIND_LAUNCHER_EXTRACT=&lt;folder&gt;</c> copies exefs/ and romfs/ - hard links on the same file
/// system - and builds the game code on this PC).</summary>
public sealed record FolderSetup
{
    public required string EnvVar { get; init; }
    /// <summary>The update versions the port is made for (it needs one, applied to the folder by Zest).</summary>
    public required IReadOnlySet<string> UpdateVersions { get; init; }
    /// <summary>Where the port keeps the set-up game (may start with ~).</summary>
    public required string GameDir { get; init; }
    /// <summary>Files in GameDir that mean the game is set up and built.</summary>
    public required IReadOnlyList<string> ReadyFiles { get; init; }
}

/// <summary>
/// The ports that match games in this library. Each entry was checked against the project's own README:
/// accepted versions, the files its setup screen wants, and that it builds the game code on the player's machine
/// (no release ships Nintendo's code).
/// </summary>
public static class NativePortCatalog
{
    public static readonly IReadOnlyList<NativePort> All =
    [
        // github.com/peiikai/LASRecomp README: "v1.0.0, v1.0.1 or v1.1.0", a folder with exefs and romfs; MIT.
        new()
        {
            Id = "lasrecomp",
            Name = "LAS Recomp",
            Author = "peiikai",
            Repo = "peiikai/LASRecomp",
            Platform = Platform.Switch,
            TitleId = 0x01006BB00C6F0000,
            Versions = new HashSet<string> { "1.0.0", "1.0.1", "1.1.0" },
            ExeFsFiles = ["main", "main.npdm"],
            Asset = new Regex(@"^LAS\.Recomp-.+-linux-x86_64\.AppImage$"),
            Status = "Beta",
            Licence = "MIT",
            Summary = "Link's Awakening running natively on Vulkan: ultrawide, higher resolutions, remappable controls and a built-in randomizer.",
            Credit = "LAS Recomp by peiikai and contributors (MIT). The project says it was made almost exclusively with AI (Claude Code), "
                + "directed, tested and reviewed by a human. Not affiliated with Nintendo.",
        },

        // github.com/Apolinario-coder/SkyRecomp README: "v1.0.0, without updates"; exefs with main, sdk, subsdk0,
        // subsdk1 plus romfs/. Its runtime is built on LAS Recomp.
        new()
        {
            Id = "skyrecomp",
            Name = "Sky Recomp",
            Author = "Apolinario-coder",
            Repo = "Apolinario-coder/SkyRecomp",
            Platform = Platform.Switch,
            TitleId = 0x01002DA013484000,
            Versions = new HashSet<string> { "1.0.0" },
            ExeFsFiles = ["main", "main.npdm", "sdk", "subsdk0", "subsdk1"],
            Asset = new Regex(@"^Sky\.Recomp-.+-linux-x86_64\.AppImage$"),
            Status = "Experimental",
            Licence = "see the project page (its LAS Recomp parts are MIT)",
            Summary = "Skyward Sword HD running natively on Vulkan: 60 FPS by default, optional 120 FPS, mouse camera and a mod manager.",
            Credit = "Sky Recomp by Apolinario-coder, built on LAS Recomp. Experimental: crashes and menu freezes are still being investigated. "
                + "Not affiliated with Nintendo.",
        },

        // github.com/valentderah/totk-pc-recomp README: base game + update 1.1.0, 1.2.1 or 1.4.3 (NSP/XCI, NSZ/XCZ);
        // Linux AppImage whose setup builds the game on the player's PC (docs/installer.md: a command-line setup with
        // JSON progress events). MIT. Chosen over UltraTears (GPL, 1.4.3 only, wants unpacked folders) on 2026-11-04.
        new()
        {
            Id = "totkrecomp",
            Name = "TotK Recomp",
            Author = "valentderah",
            Repo = "valentderah/totk-pc-recomp",
            Platform = Platform.Switch,
            TitleId = 0x0100F2C0115B6000,
            Versions = new HashSet<string> { "1.0.0" },
            ExeFsFiles = ["main", "main.npdm"],
            Asset = new Regex(@"^TotK-Setup-x86_64-.+-linux\.AppImage$"),
            Status = "Early development",
            Licence = "MIT",
            Summary = "Tears of the Kingdom natively on Vulkan: 60 fps (up to 144), custom resolutions and supersampling, FSR 1, ultrawide, emulator mods and cheats.",
            Credit = "TotK Recomp by valentderah and contributors (MIT). Its README says most of the code is written by AI coding agents "
                + "(Claude Code) under the maintainer's direction. Not affiliated with Nintendo.",
            Setup = new CliSetup
            {
                GameExecutable = "totk",
                UpdateVersions = new HashSet<string> { "1.1.0", "1.2.1", "1.4.3" },
                StepWeights = new Dictionary<string, double>
                {
                    ["check"] = 1, ["fonts"] = 0.5, ["extract"] = 28, ["lift"] = 8, ["compile"] = 45, ["prepare"] = 16, ["dlss"] = 0.5, ["cleanup"] = 1,
                },
                StepNames = new Dictionary<string, string>
                {
                    ["check"] = "Checking your files", ["fonts"] = "Fonts", ["extract"] = "Unpacking the game and the update",
                    ["lift"] = "Translating the game code", ["compile"] = "Compiling the game", ["prepare"] = "Preparing shaders",
                    ["dlss"] = "DLSS", ["cleanup"] = "Cleaning up",
                },
            },
        },

        // github.com/Revivedx/SavageWindRecomp README: Breath of the Wild 1.9.0 (base + update; DLC 3.0 optional),
        // Linux x86-64 AppImage, the game code built on the player's PC at setup; MIT. Built on LAS Recomp. Chosen over
        // our own botw19 port (2026-11-07): it plays the Great Plateau at 60 fps, a tester finished Vah Ruta.
        new()
        {
            Id = "savagewind",
            Name = "Savage Wind",
            Author = "Revivedx",
            Repo = "Revivedx/SavageWindRecomp",
            Platform = Platform.Switch,
            TitleId = 0x01007EF00011E000,
            Versions = new HashSet<string> { "1.0.0" },
            ExeFsFiles = ["main", "main.npdm"],
            Asset = new Regex(@"^Savage_Wind-.+-x86_64\.AppImage$"),
            Status = "Alpha",
            Licence = "MIT",
            Summary = "Breath of the Wild 1.9.0 natively on Vulkan: 60 fps, PC graphics options, any controller, keyboard and mouse.",
            Credit = "Savage Wind Recomp by Revivedx and contributors (MIT), built on LAS Recomp. Its README says it is made almost "
                + "entirely with AI (Claude Code), directed, tested and reviewed by a human. Not affiliated with Nintendo.",
            Extract = new FolderSetup
            {
                EnvVar = "SAVAGEWIND_LAUNCHER_EXTRACT",
                UpdateVersions = new HashSet<string> { "1.9.0" },
                GameDir = "~/.local/share/savagewind/extracted",
                ReadyFiles = ["exefs/main", "swgame.so"],
            },
        },
    ];

    public static NativePort? For(Platform platform, ulong titleId) =>
        All.FirstOrDefault(p => p.Platform == platform && p.TitleId == titleId);
}
