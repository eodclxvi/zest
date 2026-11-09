using PKHeX.Core;
using Zest.Models;

namespace Zest.Services.GameData;

/// <summary>
/// The Adventure Log for the Pokémon games Zest can read with PKHeX: Sword/Shield, Scarlet/Violet, Omega Ruby/Alpha
/// Sapphire and Ultra Sun/Ultra Moon. These games keep no quest log, so the story steps are Zest's own short notes,
/// ticked off from what the save records (badges, Titans, Team Star, island stamps, story flags). Saves are found in
/// Eden, in a native build's save folder and in Azahar; the newest one is shown. Saves are only read.
/// </summary>
public sealed class PokemonReader : IProgressReader
{
    private enum Family { SwordShield, ScarletViolet, Oras, Usum }

    private sealed record GameInfo(string Name, Family Family, string? SwitchTitleId, string? N3dsLowId);

    private static readonly GameInfo[] Games =
    [
        new("Sword", Family.SwordShield, "0100ABF008968000", null),
        new("Shield", Family.SwordShield, "01008DB008C2C000", null),
        new("Scarlet", Family.ScarletViolet, "0100A3D008C5C000", null),
        new("Violet", Family.ScarletViolet, "01008F6008C5E000", null),
        new("Omega Ruby", Family.Oras, null, "0011c400"),
        new("Alpha Sapphire", Family.Oras, null, "0011c500"),
        new("Ultra Sun", Family.Usum, null, "001b5000"),
        new("Ultra Moon", Family.Usum, null, "001b5100"),
    ];

    private readonly GameInfo _game;
    private readonly string _title;

    private PokemonReader(GameInfo game, string title)
    {
        _game = game;
        _title = title;
    }

    /// <summary>The reader for a library game, or null when it isn't one of the supported Pokémon games.</summary>
    public static PokemonReader? For(string title, Platform platform)
    {
        var t = title.Replace('é', 'e');
        if (!t.Contains("Pokemon", StringComparison.OrdinalIgnoreCase))
            return null;
        // Longest name first: "Ultra Moon" before "Moon"
        var game = Games.Where(g => (g.SwitchTitleId is not null) == (platform == Platform.Switch)
                                    && (g.N3dsLowId is not null) == (platform == Platform.N3DS))
            .OrderByDescending(g => g.Name.Length)
            .FirstOrDefault(g => System.Text.RegularExpressions.Regex.IsMatch(t, $@"\b{g.Name}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        return game is null ? null : new PokemonReader(game, title);
    }

    public string NoSaves => $"No Pokémon {_game.Name} save yet in Eden, Azahar or a native build. It shows up here after your first save.";

    public string? Prepare() => null;

    /// <summary>(source, save file) for every place this game's save can be.</summary>
    private IEnumerable<(string source, string file)> Files()
    {
        if (_game.SwitchTitleId is { } tid)
        {
            var eden = Path.Combine(AppPaths.Home, ".local/share/eden/nand/user/save/0000000000000000");
            if (Directory.Exists(eden))
            {
                foreach (var profile in Directory.EnumerateDirectories(eden))
                {
                    var main = Path.Combine(profile, tid, "main");
                    if (File.Exists(main))
                        yield return ("Eden", main);
                }
            }
            // Native builds (LAS Recomp family) keep the game's save in <working dir>/save/save_0
            foreach (var port in AppConfig.Load().LocalPorts.Where(p => SameGame(p.Title)))
            {
                var dir = AppPaths.ExpandHome(port.WorkingDirectory.Length > 0 ? port.WorkingDirectory : Path.GetDirectoryName(port.Executable) ?? "");
                var main = Path.Combine(dir, "save", "save_0", "main");
                if (File.Exists(main))
                    yield return ("Native build", main);
            }
        }
        if (_game.N3dsLowId is { } low)
        {
            string[] sdmcs =
            [
                Path.Combine(AppPaths.Home, ".var/app/org.azahar_emu.Azahar/data/azahar-emu/sdmc/Nintendo 3DS"),
                Path.Combine(AppPaths.Home, ".local/share/azahar-emu/sdmc/Nintendo 3DS"),
            ];
            foreach (var id1 in sdmcs.Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories).SelectMany(Directory.EnumerateDirectories))
            {
                var main = Path.Combine(id1, "title", "00040000", low, "data", "00000001", "main");
                if (File.Exists(main))
                    yield return ("Azahar", main);
            }
        }
    }

    private bool SameGame(string title) => For(title, _game.SwitchTitleId is null ? Platform.N3DS : Platform.Switch)?._game == _game;

    public IReadOnlyList<string> WatchFolders() => [.. Files().Select(f => Path.GetDirectoryName(f.file)!).Distinct()];

    public IReadOnlyList<ProgressSlot> Slots() =>
    [
        .. Files().Select(f =>
        {
            var saved = File.GetLastWriteTime(f.file);
            return new ProgressSlot(f.file, f.source, $"{f.source} · saved {saved:ddd d MMM, HH:mm}", saved);
        }).OrderByDescending(s => s.Saved),
    ];

    public ProgressReport Read(ProgressSlot slot)
    {
        var bytes = File.ReadAllBytes(slot.Id);   // a copy: the save on disk is never touched
        var sav = SaveUtil.GetSaveFile(bytes, slot.Id)
                  ?? throw new InvalidDataException($"PKHeX doesn't recognise {slot.Id} as a Pokémon {_game.Name} save.");

        var (quests, groups, progress) = sav switch
        {
            SAV8SWSH s => SwordShield(s),
            SAV9SV s => ScarletViolet(s),
            SAV6AO s => Oras(s),
            SAV7USUM s => Usum(s),
            _ => throw new InvalidDataException($"That save is {sav.Version}, not Pokémon {_game.Name}."),
        };

        var party = sav.PartyData.Where(p => p.Species != 0)
            .Select(p => new Collectible(p.Species.ToString(),
                p.IsEgg ? "Egg" : $"{SpeciesName(p)} · Lv. {p.CurrentLevel}", true))
            .ToList();

        return new ProgressReport(
            Where: $"Pokémon {_game.Name} · {sav.OT} · saved {slot.Saved:ddd d MMM, HH:mm} · {slot.Source}",
            Stats: $"{sav.PlayedHours} h {sav.PlayedMinutes:00} m played   ·   ₽{sav.Money:N0}   ·   Pokédex {sav.CaughtCount} caught, {sav.SeenCount} seen",
            Counts: progress,
            Picture: null,
            Quests: quests,
            Groups: [.. groups, new ProgressGroup("Party", party)],
            Dungeons: [],
            Footer: "Story steps are Zest's own notes, ticked off from what your save records (read with PKHeX). " +
                    "Zest reads your saves and never writes them.");
    }

    private static string SpeciesName(PKM p)
    {
        var species = PKHeX.Core.GameInfo.Strings.Species[p.Species];
        return p.IsNicknamed && p.Nickname != species ? $"{p.Nickname} ({species})" : species;
    }

    // ---- shared helpers

    /// <param name="Done">Whether the save says this step is done.</param>
    /// <param name="Name">The chip ("Milo · Grass").</param>
    /// <param name="Next">What to do for it, shown when it's the next step.</param>
    private sealed record Step(bool Done, string Name, string Next, params string[] Hints);

    /// <summary>A quest made of steps done in any order; the next one is the first not done.</summary>
    private static QuestProgress Quest(string id, string name, bool available, IReadOnlyList<Step> steps, string doneWord,
        string completeSummary, string? afterAll = null, bool main = true)
    {
        var done = steps.Count(s => s.Done);
        var next = steps.FirstOrDefault(s => !s.Done);
        var state = done == steps.Count && afterAll is null ? QuestState.Complete
            : available || done > 0 ? QuestState.InProgress : QuestState.NotStarted;
        var summary = state == QuestState.Complete ? completeSummary
            : done == 0 ? "Not started yet."
            : $"{doneWord}: {string.Join(", ", steps.Where(s => s.Done).Select(s => s.Name))} ({done} of {steps.Count}).";
        return new QuestProgress(id, name, main, state, next?.Name ?? "Complete", done, steps.Count, summary,
            next?.Next ?? afterAll ?? "", false, next?.Hints ?? []);
    }

    private static ProgressGroup Chips(string title, IEnumerable<Step> steps) =>
        new(title, [.. steps.Select(s => new Collectible(s.Name, s.Name, s.Done))]);

    private static bool Bit(int value, int bit) => (value >> bit & 1) != 0;

    // ---- Sword / Shield

    private (List<QuestProgress>, List<ProgressGroup>, string) SwordShield(SAV8SWSH s)
    {
        var sword = s.Version == GameVersion.SW;
        var b = s.Badges;
        Step[] gyms =
        [
            new(Bit(b, 0), "Milo · Grass", "Travel through the Wild Area to Motostoke for the opening ceremony, then beat Milo's Grass-type Gym in Turffield.", "Turffield"),
            new(Bit(b, 1), "Nessa · Water", "Head through Galar Mine to Hulbury and beat Nessa's Water-type Gym.", "Hulbury"),
            new(Bit(b, 2), "Kabu · Fire", "Return to Motostoke and beat Kabu's Fire-type Gym.", "Motostoke"),
            sword
                ? new(Bit(b, 3), "Bea · Fighting", "Go through Hammerlocke and Route 6 to Stow-on-Side and beat Bea's Fighting-type Gym.", "Stow-on-Side")
                : new(Bit(b, 3), "Allister · Ghost", "Go through Hammerlocke and Route 6 to Stow-on-Side and beat Allister's Ghost-type Gym.", "Stow-on-Side"),
            new(Bit(b, 4), "Opal · Fairy", "Cross Glimwood Tangle to Ballonlea and beat Opal's Fairy-type Gym.", "Ballonlea"),
            sword
                ? new(Bit(b, 5), "Gordie · Rock", "Head north from Hammerlocke along Routes 7 and 8 to Circhester and beat Gordie's Rock-type Gym.", "Circhester")
                : new(Bit(b, 5), "Melony · Ice", "Head north from Hammerlocke along Routes 7 and 8 to Circhester and beat Melony's Ice-type Gym.", "Circhester"),
            new(Bit(b, 6), "Piers · Dark", "Take Route 9 to Spikemuth, get past Team Yell and beat Piers (Dark type).", "Spikemuth"),
            new(Bit(b, 7), "Raihan · Dragon", "Go back to Hammerlocke and beat Raihan's Dragon-type Gym, the last badge.", "Hammerlocke"),
        ];
        var badges = gyms.Count(g => g.Done);
        var quests = new List<QuestProgress>
        {
            Quest("gyms", "The Gym Challenge", true, gyms, "Badges",
                "All eight Gym badges earned: you're cleared for the Champion Cup."),
        };
        if (badges == 8)
        {
            quests.Add(new QuestProgress("champion", "The Champion Cup", true, QuestState.InProgress, "Wyndon", 0, 1,
                "With all eight badges you can enter the Champion Cup in Wyndon. (The save doesn't show whether you've beaten Leon yet.)",
                "Go to Wyndon, win the semifinals and finals at the Stadium, then face Champion Leon.", false, ["Wyndon", "Champion"]));
        }
        return (quests, [Chips("Gym badges", gyms)], $"{badges} of 8 badges");
    }

    // ---- Scarlet / Violet: three stories in any order, plus the ending once all are done

    // Badge keys from PKHeX's SaveBlockAccessor9SV (GPL-3.0): the value is the order the badge came in, 0 = not yet
    private (List<QuestProgress>, List<ProgressGroup>, string) ScarletViolet(SAV9SV s)
    {
        var scarlet = s.Version == GameVersion.SL;
        bool Got(uint key) => s.Accessor.TryGetBlock(key, out var block) && block.GetValue() is int v && v > 0;

        // Recommended order by the opponents' levels; each stop knows its story
        (string story, Step step)[] stops =
        [
            ("gyms", new(Got(0x89306FE6), "Katy · Bug", "Beat Katy's Bug-type Gym in Cortondo (around Lv. 15).", "Cortondo")),
            ("titans", new(Got(0xA6CDE603), "Klawf · Stony Cliff", "Find the Stony Cliff Titan, Klawf, in South Province (Area Three) (around Lv. 16).", "Klawf", "Stony Cliff")),
            ("gyms", new(Got(0xB4C3AFE6), "Brassius · Grass", "Beat Brassius's Grass-type Gym in Artazon (around Lv. 17).", "Artazon")),
            ("titans", new(Got(0x9C16DA94), "Bombirdier · Open Sky", "Find the Open Sky Titan, Bombirdier, in West Province (Area One) (around Lv. 20).", "Bombirdier", "Open Sky")),
            ("star", new(Got(0x6C29ACC5), "Giacomo · Dark", "Take on Team Star's Segin Squad base (Dark) in West Province (Area One) (around Lv. 21).", "Segin", "Giacomo")),
            ("gyms", new(Got(0x8205ECAD), "Iono · Electric", "Beat Iono's Electric-type Gym in Levincia (around Lv. 24).", "Levincia")),
            ("star", new(Got(0x9C6FF7DD), "Mela · Fire", "Take on Team Star's Schedar Squad base (Fire) in East Province (Area One) (around Lv. 27).", "Schedar", "Mela")),
            ("titans", new(Got(0x0D0602DE), "Orthworm · Lurking Steel", "Find the Lurking Steel Titan, Orthworm, in East Province (Area Three) (around Lv. 29).", "Orthworm", "Lurking Steel")),
            ("gyms", new(Got(0xA803FAAD), "Kofu · Water", "Beat Kofu's Water-type Gym in Cascarrafa (around Lv. 30).", "Cascarrafa")),
            ("star", new(Got(0x71DB2CEB), "Atticus · Poison", "Take on Team Star's Navi Squad base (Poison) in Tagtree Thicket (around Lv. 33).", "Navi", "Atticus")),
            ("gyms", new(Got(0xF90EFD79), "Larry · Normal", "Beat Larry's Normal-type Gym in Medali (around Lv. 36).", "Medali")),
            ("gyms", new(Got(0xCDA61DED), "Ryme · Ghost", "Beat Ryme's Ghost-type Gym in Montenevera (around Lv. 42).", "Montenevera")),
            ("titans", new(Got(0xBDAC74B3), scarlet ? "Great Tusk · Quaking Earth" : "Iron Treads · Quaking Earth",
                $"Find the Quaking Earth Titan, {(scarlet ? "Great Tusk" : "Iron Treads")}, in Asado Desert (around Lv. 45).", "Quaking Earth", "Asado")),
            ("gyms", new(Got(0x3B819021), "Tulip · Psychic", "Beat Tulip's Psychic-type Gym in Alfornada (around Lv. 45).", "Alfornada")),
            ("gyms", new(Got(0x46B6CB30), "Grusha · Ice", "Beat Grusha's Ice-type Gym in Glaseado (around Lv. 48).", "Glaseado")),
            ("star", new(Got(0xE1271327), "Ortega · Fairy", "Take on Team Star's Ruchbah Squad base (Fairy) in North Province (around Lv. 51).", "Ruchbah", "Ortega")),
            ("star", new(Got(0x2A3AC89A), "Eri · Fighting", "Take on Team Star's Caph Squad base (Fighting) in North Province (around Lv. 56).", "Caph", "Eri")),
            ("titans", new(Got(0xEC7361B7), "Dondozo · False Dragon", "Find the False Dragon Titan, Dondozo and Tatsugiri, at Casseroya Lake (around Lv. 57).", "Dondozo", "False Dragon")),
        ];

        Step[] Of(string story) => [.. stops.Where(x => x.story == story).Select(x => x.step)];
        var gyms = Of("gyms");
        var titans = Of("titans");
        var star = Of("star");
        var quests = new List<(int rank, QuestProgress quest)>();
        int Rank(Step[] steps) => steps.FirstOrDefault(x => !x.Done) is { } n ? Array.FindIndex(stops, x => x.step == n) : int.MaxValue;

        quests.Add((Rank(gyms), Quest("gyms", "Victory Road", true, gyms, "Badges",
            "All eight Gym badges: the Pokémon League's Champion Assessment in Mesagoza is open.",
            gyms.All(g => g.Done) ? "Take the Champion Assessment at the Pokémon League (north of Mesagoza): the interview, the Elite Four, then Geeta." : null)));
        quests.Add((Rank(titans), Quest("titans", "Path of Legends", true, titans, "Titans",
            "All five Titans beaten: Arven's quest for the Herba Mystica is done.")));
        quests.Add((Rank(star), Quest("star", "Starfall Street", true, star, "Bases",
            "All five Team Star bases cleared and Cassiopeia's story wrapped up.")));
        var all = stops.All(x => x.step.Done);
        if (all)
        {
            quests.Add((-1, new QuestProgress("wayhome", "The Way Home", true, QuestState.InProgress, "Area Zero", 0, 1,
                "All three stories are done. (The save doesn't show whether you've finished the ending yet.)",
                "Meet Arven, Nemona and Penny at the Zero Gate in Area Zero and head down into the Great Crater of Paldea.",
                false, ["Area Zero", "Way Home"])));
        }

        var got = stops.Count(x => x.step.Done);
        return ([.. quests.OrderBy(q => q.rank).Select(q => q.quest)],
            [Chips("Gym badges", gyms), Chips("Titans", titans), Chips("Team Star", star)],
            $"{gyms.Count(x => x.Done)}/8 Gyms · {titans.Count(x => x.Done)}/5 Titans · {star.Count(x => x.Done)}/5 Team Star · {got}/18 in all");
    }

    // ---- Omega Ruby / Alpha Sapphire

    private (List<QuestProgress>, List<ProgressGroup>, string) Oras(SAV6AO s)
    {
        var sapphire = s.Version == GameVersion.AS;
        var team = sapphire ? "Team Aqua" : "Team Magma";
        var legend = sapphire ? "Kyogre" : "Groudon";
        var b = s.Badges;
        Step[] gyms =
        [
            new(Bit(b, 0), "Roxanne · Rock", "Go through Petalburg Woods to Rustboro City and beat Roxanne's Rock-type Gym.", "Rustboro"),
            new(Bit(b, 1), "Brawly · Fighting", "Sail with Mr. Briney to Dewford Town and beat Brawly's Fighting-type Gym.", "Dewford"),
            new(Bit(b, 2), "Wattson · Electric", "Deliver Mr. Stone's letter to Steven in Granite Cave, sail to Slateport, then head north to Mauville City and beat Wattson's Electric-type Gym.", "Mauville"),
            new(Bit(b, 3), "Flannery · Fire", $"Take the cable car up Mt. Chimney, stop {team} at the summit, then go down Jagged Pass to Lavaridge Town and beat Flannery's Fire-type Gym.", "Lavaridge"),
            new(Bit(b, 4), "Norman · Normal", "Go back to Petalburg City and beat your dad, Norman, at the Normal-type Gym.", "Petalburg"),
            new(Bit(b, 5), "Winona · Flying", "Head east past the Weather Institute (Route 119) to Fortree City and beat Winona's Flying-type Gym.", "Fortree"),
            new(Bit(b, 6), "Tate & Liza · Psychic", $"Pass Lilycove, Mt. Pyre and {team}'s hideout, then surf to Mossdeep City and beat Tate & Liza's Psychic-type Gym.", "Mossdeep"),
            new(Bit(b, 7), "Wallace · Water", $"After {legend} is calmed, beat Wallace's Water-type Gym in Sootopolis City.", "Sootopolis"),
        ];
        var primal = s.EventWork.GetEventFlag(301);   // PKHeX flags_oras: Defeated Primal Groudon/Kyogre (Story Event)
        var fame = s.EventWork.GetEventFlag(2720);    // Entered Hall of Fame
        var delta = s.EventWork.GetEventFlag(2729);   // Completed Delta Episode
        var badges = gyms.Count(g => g.Done);
        var quests = new List<QuestProgress>
        {
            Quest("gyms", "Hoenn's Gym Challenge", true, gyms, "Badges", "All eight Hoenn badges earned."),
            Quest("team", $"Stopping {team}", badges >= 6, [new(primal, $"Primal {legend} calmed",
                $"Stop {team} at the Mossdeep Space Center, follow them to the Seafloor Cavern, wake Rayquaza at Sky Pillar and calm Primal {legend} in Sootopolis.", "Seafloor Cavern", "Sky Pillar")],
                "Done", $"{team} was stopped and Primal {legend} calmed with Rayquaza's help."),
            Quest("league", "The Pokémon League", badges == 8, [new(fame, "Hall of Fame",
                "Surf east to the Pokémon League, cross Victory Road and beat the Elite Four and the Champion.", "Victory Road", "Elite Four")],
                "Done", "You entered the Hall of Fame as Hoenn's Champion."),
            Quest("delta", "The Delta Episode", fame, [new(delta, "Delta Episode",
                "After the Hall of Fame, a meteor threatens Hoenn: follow Zinnia and Steven from Mossdeep to save it.", "Delta Episode")],
                "Done", "You finished the Delta Episode and saved Hoenn from the meteor.", main: false),
        };
        return (quests, [Chips("Gym badges", gyms)], $"{badges} of 8 badges · {(fame ? "Champion" : "not yet Champion")}");
    }

    // ---- Ultra Sun / Ultra Moon

    private (List<QuestProgress>, List<ProgressGroup>, string) Usum(SAV7USUM s)
    {
        var stamps = (int)s.Misc.Stamps;   // Trainer Passport stamps (PKHeX Stamp7)
        Step[] islands =
        [
            new(Bit(stamps, 1), "Melemele", "Clear Captain Ilima's trial in Verdant Cavern, then win Kahuna Hala's grand trial in Iki Town.", "Melemele", "Verdant Cavern"),
            new(Bit(stamps, 2), "Akala", "Clear the trials of Lana (Brooklet Hill), Kiawe (Wela Volcano Park) and Mallow (Lush Jungle), then win Kahuna Olivia's grand trial.", "Akala"),
            new(Bit(stamps, 3), "Ula'ula", "Clear the trials on Ula'ula Island (Sophocles at Hokulani Observatory, Acerola at the Thrifty Megamart), then win Kahuna Nanu's grand trial.", "Ula'ula", "Ulaula"),
            new(Bit(stamps, 4), "Poni", "Clear the trial in Vast Poni Canyon, then win the Poni Island grand trial.", "Poni"),
        ];
        var challenge = Bit(stamps, 5) || islands.All(i => i.Done);
        var champion = s.EventWork.GetEventFlag(4060);   // PKHeX flags_usum: Is Alolan Champion
        var quests = new List<QuestProgress>
        {
            Quest("islands", "The Island Challenge", true, islands, "Islands cleared", "All four islands' trials and grand trials cleared."),
            Quest("league", "The Pokémon League", challenge, [new(champion, "Alola Champion",
                "Climb Mount Lanakila, beat the Elite Four and become Alola's first Champion.", "Lanakila", "Pokémon League")],
                "Done", "You became the first Champion of Alola."),
        };
        return (quests, [Chips("Island challenge", islands)],
            $"{islands.Count(i => i.Done)} of 4 islands · {(champion ? "Champion" : "not yet Champion")}");
    }
}
