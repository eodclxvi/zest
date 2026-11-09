using Zest.Services.Native;

namespace Zest.Services.GameData;

public enum QuestState { NotStarted, InProgress, Complete }

/// <summary>A quest from the game's own Adventure Log, as one save has it.</summary>
/// <param name="GuideHints">Words to find the quest's chapter in the walkthrough ("Turffield"), when it has no fixed one.</param>
public sealed record QuestProgress(
    string Id, string Name, bool Main, QuestState State, string Step, int StepIndex, int StepCount,
    string Summary, string NextAction, bool BossBattle, IReadOnlyList<string>? GuideHints = null);

public sealed record Collectible(string Key, string Name, bool Have);

public sealed record DungeonProgress(string Id, string Name, bool Visited, bool Map, bool Compass, bool BossKey, int SmallKeys, bool? Cleared);

/// <summary>One save slot's caption: what the game's own load screen shows.</summary>
public sealed record SlotCaption(int Index, string Folder, bool AutoSave, DateTime? Saved, string Location, byte[]? Thumbnail,
    int MaxLifeQuarters, int GoldTokens, int Rupees);

public sealed record SlotProgress(
    SlotCaption Caption,
    IReadOnlyList<QuestProgress> Quests,
    IReadOnlyList<Collectible> Songs,
    IReadOnlyList<Collectible> Stones,
    IReadOnlyList<Collectible> Items,
    IReadOnlyList<Collectible> Equipment,
    IReadOnlyList<DungeonProgress> Dungeons,
    IReadOnlyList<Collectible> Places);

/// <summary>Where a set of saves lives: Lichii's NAND (the emulator) or Hookshot's save folder (the native port).</summary>
public sealed record SaveSource(string Name, string Folder)
{
    public IEnumerable<string> SlotFolders => Directory.Exists(Folder)
        ? Directory.EnumerateDirectories(Folder, "slot_*").Where(d => File.Exists(Path.Combine(d, "progress.sav"))).Order()
        : [];
}

/// <summary>
/// Ocarina of Time (Switch 2, 2026): reads progress from its saves the way the game's Adventure Log and save screen
/// show it. The save only stores hashes, so the names and the text come from the user's own game files (Hookshot's
/// extracted romfs): the quest table (RSDB/Challenge), the Adventure Log text (Mals ChallengeMsg) and the item and
/// place names. Nothing of the game is copied into Zest.
/// </summary>
public sealed class OotProgress
{
    public const string ProgramId = "0400486024DEC000";

    private readonly List<Challenge> _challenges;
    private readonly Dictionary<string, Dictionary<string, string>> _log;
    private readonly Dictionary<string, string> _items;
    private readonly Dictionary<string, string> _places;

    private sealed record Challenge(string Id, bool Main, string[] Steps, HashSet<string> BossSteps, string? DependsOn);

    private OotProgress(string romfs)
    {
        var zs = new ZsReader(romfs);
        _challenges = [];
        if (Byml.Parse(zs.Read(Path.Combine(romfs, "RSDB", "Challenge.Product.111.rstbl.byml.zs"))) is List<object?> rows)
        {
            foreach (var row in rows.OfType<Dictionary<string, object?>>())
            {
                if (row.GetValueOrDefault("__RowId") is not string rowId || row.GetValueOrDefault("Steps") is not List<object?> steps)
                    continue;
                var category = row.GetValueOrDefault("Category") as uint?;
                if (category is null)
                    continue;   // mini-games: not in the Adventure Log
                var id = Path.GetFileName(rowId).Split('.')[0];
                var stepRows = steps.OfType<Dictionary<string, object?>>().ToList();
                _challenges.Add(new Challenge(
                    id,
                    category == Murmur3.Hash("Main"),
                    // The table lists the steps from "Ready"; the save also uses "Complete" once it's done
                    [.. stepRows.Select(s => s.GetValueOrDefault("Name") as string ?? "").Append("Complete").Distinct()],
                    [.. stepRows.Where(s => s.GetValueOrDefault("IsBossBattle") is true).Select(s => (string)s["Name"]!)],
                    row.GetValueOrDefault("DependFlagName") as string));
            }
        }

        var mals = MessageArchive(romfs);
        var messages = Sarc.Read(zs.Read(mals));
        _log = [];
        foreach (var (name, data) in messages)
        {
            if (name.StartsWith("ChallengeMsg/Info_", StringComparison.Ordinal))
                _log[Path.GetFileNameWithoutExtension(name)["Info_".Length..]] = Msbt.Read(data);
        }
        _items = messages.TryGetValue("ActorMsg/PouchContent.msbt", out var pouch) ? Msbt.Read(pouch) : [];
        _places = messages.TryGetValue("LocationMsg/Location.msbt", out var loc) ? Msbt.Read(loc) : [];
    }

    /// <summary>The English message archive; US first (Hookshot builds with en-US).</summary>
    private static string MessageArchive(string romfs)
    {
        var folder = Path.Combine(romfs, "Mals");
        foreach (var lang in new[] { "USen", "EUen" })
        {
            var hit = Directory.EnumerateFiles(folder, lang + ".Product.*.sarc.zs").Order().LastOrDefault();
            if (hit is not null)
                return hit;
        }
        throw new FileNotFoundException("The game's English text (Mals/USen) is missing.");
    }

    private static readonly Lock CacheLock = new();
    private static (string romfs, OotProgress data)? _cache;

    /// <summary>The game's romfs: Hookshot's extracted copy (Zest builds it there from the user's .nup).</summary>
    public static string? FindRomfs()
    {
        var port = NativePortCatalog.All.FirstOrDefault(p => p.Id == "hookshot");
        if (port is null)
            return null;
        var romfs = Path.Combine(NativePorts.GameFolder(port), "romfs");
        return File.Exists(Path.Combine(romfs, "RSDB", "Challenge.Product.111.rstbl.byml.zs")) ? romfs : null;
    }

    /// <summary>Loads (once) the names and text from the romfs. Throws IOException/InvalidDataException when unreadable.</summary>
    public static OotProgress Load(string romfs)
    {
        lock (CacheLock)
        {
            if (_cache is { } c && c.romfs == romfs)
                return c.data;
            var data = new OotProgress(romfs);
            _cache = (romfs, data);
            return data;
        }
    }

    /// <summary>The save folders that exist: Lichii's (any user) and Hookshot's.</summary>
    public static IReadOnlyList<SaveSource> FindSources()
    {
        var sources = new List<SaveSource>();
        var lichii = AppPaths.ExpandHome("~/.local/share/lichii-root/data/eden/nand/user/save/0000000000000000");
        if (Directory.Exists(lichii))
        {
            foreach (var user in Directory.EnumerateDirectories(lichii))
            {
                var folder = Path.Combine(user, ProgramId);
                if (Directory.Exists(folder))
                    sources.Add(new SaveSource("Lichii", folder));
            }
        }
        var hookshot = AppPaths.ExpandHome("~/.local/share/hookshot/save");
        if (Directory.Exists(hookshot))
        {
            foreach (var folder in Directory.EnumerateDirectories(hookshot))
            {
                if (Directory.EnumerateDirectories(folder, "slot_*").Any())
                    sources.Add(new SaveSource("Hookshot", folder));
            }
        }
        // Several users in Lichii: tell them apart
        return sources.Count(s => s.Name == "Lichii") > 1
            ? [.. sources.Select(s => s.Name == "Lichii" ? s with { Name = $"Lichii ({Path.GetFileName(Path.GetDirectoryName(s.Folder))![..8]})" } : s)]
            : sources;
    }

    public SlotCaption ReadCaption(string slotFolder)
    {
        var index = int.TryParse(Path.GetFileName(slotFolder).Replace("slot_", ""), out var i) ? i : 0;
        var file = Path.Combine(slotFolder, "caption.sav");
        if (!File.Exists(file))
            return new SlotCaption(index, slotFolder, false, File.GetLastWriteTime(Path.Combine(slotFolder, "progress.sav")), "", null, 0, 0, 0);
        var c = GameDataSave.Load(file);
        DateTime? saved = null;
        try
        {
            saved = new DateTime(c.Int("CaptionData.Date.Year"), c.Int("CaptionData.Date.Month"), c.Int("CaptionData.Date.Day"),
                c.Int("CaptionData.Date.Hour"), c.Int("CaptionData.Date.Minute"), c.Int("CaptionData.Date.Second"));
        }
        catch (ArgumentOutOfRangeException)
        {
        }
        var location = c.String("CaptionData.LocationName") ?? "";
        return new SlotCaption(
            index, slotFolder, c.Bool("CaptionData.IsAutoSave"), saved ?? File.GetLastWriteTime(file),
            PlaceName(location),
            // The slot's screenshot (JPEG); its key's name isn't known, only its hash
            c.Binary(0x63696A32u),
            c.Int("CaptionData.MaxLife"), c.Int("CaptionData.GoldToken"), c.Int("CaptionData.CurrentRupee"));
    }

    public string PlaceName(string id) => _places.GetValueOrDefault(id) is { Length: > 0 } n ? n : Spaced(id);

    private string ItemName(string key) => _items.GetValueOrDefault(key + "_Name") is { Length: > 0 } n ? n.Trim() : Spaced(key.Split('_').Last());

    private static string Spaced(string id) => System.Text.RegularExpressions.Regex.Replace(id, "(?<=[a-z])(?=[A-Z])", " ");

    private static readonly string[] SongKeys =
    [
        "Item_Melody_ZeldaLullaby", "Item_Melody_EponaSong", "Item_Melody_SariaSong", "Item_Melody_SunSong",
        "Item_Melody_TimeSong", "Item_Melody_StormSong", "Item_Melody_ForestMinuet", "Item_Melody_FireBolero",
        "Item_Melody_WaterSerenade", "Item_Melody_SpiritRequiem", "Item_Melody_ShadowNocturne", "Item_Melody_LightPrelude",
    ];

    private static readonly string[] StoneKeys =
    [
        "Item_Stone_Forest", "Item_Stone_Fire", "Item_Stone_Water",
        "Item_Medal_Forest", "Item_Medal_Fire", "Item_Medal_Water", "Item_Medal_Shadow", "Item_Medal_Spirit", "Item_Medal_Light",
    ];

    private static readonly string[] ItemKeys =
    [
        "Item_DekuStick", "Item_DekuNut", "Item_Bomb", "Weapon_Range_SlingShot", "Item_Ocarina_Fairy", "Item_Ocarina_Time",
        "Item_Bombchu", "Item_Boomerang", "Item_Hookshot", "Item_Longshot", "Weapon_Range_Bow", "Item_Arrow_Fire",
        "Item_Arrow_Ice", "Item_Arrow_Light", "Item_DinFire", "Item_FaroreWind", "Item_NayruLove", "Item_MagicBean",
        "Armor_GlassTruth", "Item_Hammer", "Item_Bottle", "Item_AgonyStone", "Item_Token_Gerudo",
    ];

    private static readonly string[] EquipmentKeys =
    [
        "Weapon_Sword_Kokiri", "Weapon_Sword_Master", "Weapon_Sword_Giant", "Weapon_Sword_Biggoron",
        "Weapon_Shield_Deku", "Weapon_Shield_Hylian", "Weapon_Shield_Mirror",
        "Armor_Adult_Goron", "Armor_Adult_Zora", "Armor_Adult_HeavyBoot", "Armor_Adult_HoverBoot",
        "Armor_Child_Gauntlet_Goron", "Armor_Adult_Gauntlet_Silver", "Armor_Adult_Gauntlet_Gold",
        "Item_Scale_Silver", "Item_Scale_Gold", "Item_Wallet_Medium", "Item_Wallet_Large",
    ];

    /// <summary>Dungeons in story order, with the quest whose completion means the dungeon is done (null: none tracked).</summary>
    private static readonly (string id, string? quest)[] DungeonKeys =
    [
        ("InsideTheDekuTree", "Get_StoneOfForest"), ("DodongoCavern", "Get_StoneOfFire"), ("InsideJabuJabuBelly", "Get_StoneOfWater"),
        ("ForestTemple", "SageOfForest"), ("FireTemple", "SageOfFire"), ("WaterTemple", "SageOfWater"),
        ("BottomOfTheWell", null), ("ShadowTemple", "SageOfShadow"), ("SpiritTemple", "SageOfSpirit"),
        ("IceCavern", null), ("GerudoTrainingGround", null), ("InsideGanonCastle", "Defeat_Ganon"),
    ];

    private static readonly string[] PlaceKeys =
    [
        "KokiriForest", "LostWood", "SacredForestMeadow", "HyruleField", "Market", "HyruleCastle", "TimeTemple",
        "LonLonRanch", "KakarikoVillage", "Graveyard", "DeathMountainTrail", "GoronCity", "DeathMountainCrater",
        "ZoraRiver", "ZoraDomain", "ZoraFountain", "LakeHylia", "GerudoValley", "GerudoFortress", "HountedWasteland",
        "DesertColossus", "IngoRanch", "GanonCastle",
    ];

    public SlotProgress ReadSlot(string slotFolder)
    {
        var caption = ReadCaption(slotFolder);
        var s = GameDataSave.Load(Path.Combine(slotFolder, "progress.sav"));

        var quests = new List<QuestProgress>();
        foreach (var c in _challenges)
        {
            if (!_log.TryGetValue(c.Id, out var text))
                continue;   // no Adventure Log page (Sage of Light, the final escape)
            var raw = s.Enum("Step_" + c.Id);
            var stepIndex = raw is { } v ? Array.FindIndex(c.Steps, n => Murmur3.Hash(n) == v) : -1;
            var step = stepIndex >= 0 ? c.Steps[stepIndex] : "NotReady";
            var state = step == "Complete" ? QuestState.Complete : stepIndex > 0 ? QuestState.InProgress : QuestState.NotStarted;
            // Not every step has its own page: the log shows the latest one that does
            string summary = "", next = "";
            for (var i = Math.Max(stepIndex, 0); i >= 0 && summary.Length == 0; i--)
            {
                summary = Clean(text.GetValueOrDefault(c.Steps[i]));
                next = Clean(text.GetValueOrDefault(c.Steps[i] + "_NextAction"));
            }
            var counted = c.Steps.Count(n => n is not ("Ready" or "Complete"));
            var done = Math.Clamp(stepIndex - 1, 0, counted);
            quests.Add(new QuestProgress(
                c.Id, Clean(text.GetValueOrDefault("Name")) is { Length: > 0 } n ? n : Spaced(c.Id), c.Main, state, step,
                state == QuestState.Complete ? counted : done, counted, summary, state == QuestState.Complete ? "" : next,
                c.BossSteps.Contains(step)));
        }

        Collectible Item(string key) => new(key, ItemName(key), s.Bool("IsGet." + key));
        var dungeons = DungeonKeys.Select(d =>
        {
            var quest = d.quest is null ? null : quests.FirstOrDefault(q => q.Id == d.quest);
            return new DungeonProgress(
                d.id, PlaceName(d.id), s.Bool("IsVisitLocation." + d.id),
                s.Bool($"DungeonInfo.{d.id}.IsGetMap"), s.Bool($"DungeonInfo.{d.id}.IsGetCompass"),
                s.Bool($"DungeonInfo.{d.id}.IsGetBossKey"), s.Int($"DungeonInfo.{d.id}.SmallKeyNum"),
                quest is null ? null : quest.State == QuestState.Complete);
        }).ToList();

        // The caption is written with the save; progress.sav has the same numbers when the caption is missing
        if (caption.MaxLifeQuarters == 0)
            caption = caption with
            {
                MaxLifeQuarters = s.Int("PlayerStatus.MaxLife"),
                GoldTokens = s.Int("PouchData.Item_Token_Gold.StockNum"),
                Rupees = s.Int("PlayerStatus.CurrentRupee"),
            };

        return new SlotProgress(
            caption,
            quests,
            [.. SongKeys.Select(Item)],
            [.. StoneKeys.Select(Item)],
            [.. ItemKeys.Select(Item)],
            [.. EquipmentKeys.Select(Item)],
            dungeons,
            [.. PlaceKeys.Select(p => new Collectible(p, PlaceName(p), s.Bool("IsVisitLocation." + p)))]);
    }

    /// <summary>The log's line breaks are the game's text box width: join them into paragraphs.</summary>
    private static string Clean(string? text) =>
        text is null ? "" : string.Join("\n\n", text.Split("\n\n").Select(p => string.Join(' ', p.Split('\n').Select(l => l.Trim())).Trim()));
}

/// <summary>The Adventure Log's reader for Ocarina of Time (Switch 2): saves in Lichii and Hookshot.</summary>
public sealed class OotReader : IProgressReader
{
    private OotProgress? _data;
    private readonly Dictionary<string, SlotCaption> _captions = [];

    public string NoSaves => "No Ocarina of Time saves yet in Lichii or Hookshot.";

    public string? Prepare()
    {
        if (_data is not null)
            return null;
        if (OotProgress.FindRomfs() is not { } romfs)
            return "The Adventure Log reads its text from the game's files. Install the native version (Hookshot) " +
                   "from the details page first; your saves can stay in Lichii.";
        _data = OotProgress.Load(romfs);
        return null;
    }

    public IReadOnlyList<string> WatchFolders() => [.. OotProgress.FindSources().Select(s => s.Folder)];

    public IReadOnlyList<ProgressSlot> Slots()
    {
        var data = _data ?? throw new InvalidOperationException("Prepare first.");
        _captions.Clear();
        var slots = new List<ProgressSlot>();
        foreach (var src in OotProgress.FindSources())
        {
            foreach (var folder in src.SlotFolders)
            {
                var c = data.ReadCaption(folder);
                _captions[folder] = c;
                slots.Add(new ProgressSlot(folder, src.Name,
                    $"{(c.AutoSave ? "Autosave" : "Save")} {c.Index + 1} · {c.Location} · {c.Saved:ddd d MMM, HH:mm}", c.Saved ?? DateTime.MinValue));
            }
        }
        // The same save copied between Lichii and Hookshot: list it once
        return [.. slots
            .GroupBy(s => (s.Label, Hash: Fingerprint(s.Id)))
            .Select(g => g.Count() == 1 ? g.First() : g.First() with { Source = string.Join(" + ", g.Select(x => x.Source)) })
            .OrderByDescending(s => s.Saved)];
    }

    private static string Fingerprint(string slotFolder)
    {
        try
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(Path.Combine(slotFolder, "progress.sav"))));
        }
        catch (IOException)
        {
            return slotFolder;
        }
    }

    public ProgressReport Read(ProgressSlot slot)
    {
        var data = _data ?? throw new InvalidOperationException("Prepare first.");
        var p = data.ReadSlot(slot.Id);
        var c = p.Caption;
        var hearts = c.MaxLifeQuarters / 4;
        var pieces = c.MaxLifeQuarters % 4;
        return new ProgressReport(
            Where: $"{c.Location} · {(c.AutoSave ? "autosave" : "saved")} {c.Saved:ddd d MMM, HH:mm} · {slot.Source}",
            Stats: $"♥ {hearts}{(pieces > 0 ? $" + {pieces}/4" : "")} hearts   ·   {c.GoldTokens} of 100 Gold Skulltulas   ·   {c.Rupees} rupees",
            Counts: $"{p.Quests.Count(q => q.Main && q.State == QuestState.Complete)} of {p.Quests.Count(q => q.Main)} main quests · " +
                    $"{p.Songs.Count(s => s.Have)}/12 songs · {p.Stones.Count(s => s.Have)}/9 stones and medallions · " +
                    $"{p.Places.Count(s => s.Have)}/{p.Places.Count} places",
            Picture: c.Thumbnail,
            Quests: p.Quests,
            Groups:
            [
                new("Spiritual stones and medallions", p.Stones),
                new("Songs", p.Songs),
                new("Items", p.Items),
                new("Equipment and upgrades", p.Equipment),
                new("Places visited", p.Places),
            ],
            Dungeons: p.Dungeons,
            Footer: "The Adventure Log text, item and place names come from your own copy of the game; Zest reads your saves and never writes them.");
    }
}
