using System.Text.RegularExpressions;
using Zest.Services.GameData;

namespace Zest.Services.Walkthroughs;

/// <summary>
/// Zest's guide for Ocarina of Time on Switch 2 (2026). No open-licence walkthrough of the remake exists, so it's
/// put together when read:
///   * the chapters and walkthrough text of StrategyWiki's guide to the original (CC BY-SA 4.0), with places and items
///     renamed to the remake's names (Zelda Wiki's list),
///   * per chapter: the remake's screenshots and maps from Zelda Wiki, the game's own Adventure Log objectives for that
///     part (from the user's romfs, ticked off from the newest save), Zest's "do it as young Link" notes, and the
///     remake's changes from Zelda Wiki's article (GNU FDL 1.3),
///   * two chapters of its own: what's new on Switch 2, and what to do before becoming adult.
/// Each section says where it comes from; nothing is bundled with Zest.
/// </summary>
public static partial class OotRemakeGuide
{
    /// <summary>The guide's name in the reader and in "Change guide".</summary>
    public const string Root = "Ocarina of Time · Switch 2 (Zest guide)";
    public const string Original = "The Legend of Zelda: Ocarina of Time";
    private const string RemakePage = "The Legend of Zelda: Ocarina of Time (Nintendo Switch 2)";
    private const string YoungOrAdult = Root + "/Young or adult?";

    public static bool Supports(string title, Models.Platform platform) =>
        platform == Models.Platform.Switch2
        && LibraryScanner.TitleKey(StrategyWiki.CleanTitle(title)) == LibraryScanner.TitleKey(Original);

    public static bool IsPage(string page) => page == Root || page.StartsWith(Root + "/", StringComparison.Ordinal);

    /// <summary>What each StrategyWiki chapter covers on Switch 2: its name there, its quests, its pictures and words.</summary>
    private sealed record ChapterInfo(string? Label, string[] Quests, string[] Pictures, string[] Changes);

    private static readonly Dictionary<string, ChapterInfo> Info = new(StringComparer.OrdinalIgnoreCase)
    {
        ["The Beginning"] = new("Kokiri Forest", ["Meet_DekuTree"], ["Kokiri_Forest", "Link's_House", "Link_Wakes_Up", "Kokiri_Shop"], ["Kokiri", "Mido"]),
        ["Inside the Deku Tree"] = new("Great Deku Tree", ["Get_StoneOfForest"], ["Inside_the_Deku_Tree", "Great_Deku_Tree", "Gohma"], ["Deku Tree", "Gohma"]),
        ["The Lost Woods"] = new("Lost Woods & Sacred Grove", [], ["Lost_Woods", "Sacred_Grove", "Forbidden_Forest"], ["Lost Woods", "Sacred"]),
        ["Hyrule Castle"] = new(null, ["Goto_HyruleCastle", "Get_Epona"], ["Hyrule_Castle", "Castle_Gate", "Castle_Courtyard", "Lon_Lon_Ranch", "Hyrule_Field"], ["Hyrule Castle", "Castle Town", "Lon Lon", "Malon", "Hyrule Field"]),
        ["Kakariko Village"] = new(null, ["SubEvent_GetMelodySun"], ["Kakariko_Village", "Graveyard", "Impa's_House", "Kakariko_Windmill"], ["Kakariko", "Graveyard", "Dampé"]),
        ["Goron City"] = new("Death Mountain & Goron City", ["Get_StoneOfFire", "Meet_GreatFairy"], ["Death_Mountain_Trail", "Goron_City", "Darunia", "Death_Mountain_Summit"], ["Goron", "Death Mountain", "Darunia"]),
        ["Dodongo's Cavern"] = new("Dodongo Cavern", ["Get_StoneOfFire"], ["Dodongo's_Cavern", "King_Dodongo"], ["Dodongo"]),
        ["Jabu-Jabu's Belly"] = new("Lord Jabu-Jabu's Belly", ["Get_StoneOfWater"], ["Zora's_River", "Zora's_Domain", "Lake_Hylia", "Jabu-Jabu", "Barinade"], ["Zora", "Jabu", "Ruto", "Barinade"]),
        ["Temple of Time"] = new(null, ["DoorOfTime"], ["Temple_of_Time", "Master_Sword", "Ocarina_of_Time", "Hyrule_Castle_Town"], ["Temple of Time", "Master Sword", "Ocarina of Time"]),
        ["Forest Temple"] = new(null, ["SageOfForest"], ["Forest_Temple", "Phantom_Ganon"], ["Forest Temple", "Phantom Ganon"]),
        ["Fire Temple"] = new(null, ["SageOfFire"], ["Fire_Temple", "Death_Mountain_Crater", "Volvagia"], ["Fire Temple", "Volvagia", "Crater"]),
        ["Ice Cavern"] = new(null, [], ["Ice_Cavern", "Zora's_Fountain"], ["Ice Cavern", "Zora Fountain"]),
        ["Water Temple"] = new(null, ["SageOfWater"], ["Water_Temple", "Morpha", "Lake_Hylia"], ["Water Temple", "Morpha", "Iron Boots"]),
        ["Bottom of the Well"] = new(null, ["SubEvent_GetGlassTruth"], ["Bottom_of_the_Well"], ["Bottom of the Well", "Lens"]),
        ["Shadow Temple"] = new(null, ["SageOfShadow"], ["Shadow_Temple", "Bongo_Bongo"], ["Shadow Temple", "Bongo"]),
        ["Gerudo Fortress"] = new(null, [], ["Gerudo", "Gerudo_Valley"], ["Gerudo"]),
        ["Spirit Temple"] = new(null, ["SageOfSpirit"], ["Spirit_Temple", "Desert_Colossus", "Mirage_Desert", "Twinrova"], ["Spirit Temple", "Twinrova", "Desert", "Nabooru"]),
        ["Gerudo Training Grounds"] = new("Gerudo Proving Grounds", [], ["Gerudo_Proving_Grounds"], ["Proving Grounds", "Training Ground"]),
        ["Ganon's Castle"] = new("Ganondorf's Castle", ["AllSage", "Defeat_Ganon"], ["Ganondorf's_Castle", "Ganondorf", "Ganon"], ["Ganondorf", "Ganon's Castle", "Ganondorf's Castle"]),
        ["The Happy Mask Shop"] = new(null, ["SubEvent_MaskSalesman"], ["Happy_Mask_Shop"], ["Mask"]),
        ["Item Trading"] = new(null, ["SubEvent_GetSwordBigGoron"], ["Biggoron", "Lakeside_Laboratory"], ["Biggoron", "trading"]),
        ["Gold Skulltulas"] = new(null, ["GatheringTokenGold"], ["Gold_Skulltula", "House_of_Skulltula"], ["Skulltula"]),
        ["Ocarina Songs"] = new(null, ["SubEvent_GetScarecrow", "SubEvent_GetStormSong", "SubEvent_GetMelodySun"], ["Song", "Bolero", "Ocarina"], ["Ocarina", "song", "Scarecrow"]),
        ["Fairy Fountains"] = new(null, ["Meet_GreatFairy"], ["Great_Fairy"], ["Great Fairy", "Fairy Fountain"]),
        ["Magic Beans"] = new(null, [], ["Magic_Bean"], ["Magic Bean"]),
        ["Minigames"] = new(null, [], ["Bombchu_Bowling", "Shooting_Gallery", "Fishing", "Treasure"], ["mini-game", "minigame", "Bombchu Bowling", "Fishing"]),
    };

    private static readonly HashSet<string> SkipChapters = new(StringComparer.OrdinalIgnoreCase) { "Gameplay", "Controls", "Versions", "Glitches" };

    private static string Sub(string page) => page.Contains('/') ? page[(page.IndexOf('/') + 1)..] : "";

    /// <summary>The guide: Zest's two chapters, then StrategyWiki's chapters of the original, renamed for Switch 2.</summary>
    public static async Task<Guide> GuideAsync(CancellationToken cancel)
    {
        var original = await StrategyWiki.GuideAsync(Original, cancel);
        var renames = await RenamesAsync(cancel);
        var chapters = new List<GuideChapter>
        {
            new(Root, "What's new on Switch 2", "Switch 2"),
            new(YoungOrAdult, "Young or adult? Do these first", "Switch 2"),
        };
        foreach (var c in original.Chapters.Skip(1))
        {
            // The original's controls, versions and glitches don't apply to the remake (its controls are in "What's new")
            if (SkipChapters.Contains(Sub(c.Page)))
                continue;
            var sub = Sub(c.Page);
            var label = Info.GetValueOrDefault(sub)?.Label ?? Rename(c.Label, renames);
            chapters.Add(new GuideChapter(Root + "/" + sub, label, Rename(c.Group, renames)));
        }
        return new Guide(Root, chapters);
    }

    /// <summary>The StrategyWiki page a chapter is built on (for "Web" and links), or null for Zest's own chapters.</summary>
    public static string? OriginalPage(string page) =>
        page == Root || page == YoungOrAdult ? null : Original + "/" + Sub(page);

    /// <summary>A link in StrategyWiki's text to another chapter of the original: the same chapter of this guide.</summary>
    public static string? MapLink(string strategyWikiPage) =>
        strategyWikiPage.StartsWith(Original + "/", StringComparison.Ordinal) ? Root + "/" + strategyWikiPage[(Original.Length + 1)..] : null;

    public static async Task<(string page, IReadOnlyList<Block> blocks)> PageAsync(string page, CancellationToken cancel)
    {
        if (page == Root)
            return (page, await WhatsNewAsync(cancel));
        if (page == YoungOrAdult)
            return (page, await Task.Run(() => YoungOrAdultPage(Latest().save), cancel));
        return (page, await ChapterAsync(Sub(page), cancel));
    }

    // ---- the chapters

    private static async Task<IReadOnlyList<Block>> WhatsNewAsync(CancellationToken cancel)
    {
        var blocks = new List<Block>
        {
            Para(T("This is Zest's guide to the Switch 2 remake. Each chapter follows StrategyWiki's walkthrough of the original game, " +
                   "with the remake's names, its pictures and maps, the Adventure Log objectives from your own game (ticked off from your newest save), " +
                   "notes on what to do as young Link, and what the remake changed. Remade dungeons can differ from the original's text, so the " +
                   "Switch 2 notes at the top of each chapter come first.")),
        };
        blocks.AddRange(await GalleryAsync(["Link_Wakes_Up", "Hyrule_Field", "Hall_of_Fallen_Foes", "Threads_of_Time"], 4, cancel));
        blocks.Add(new HeadingBlock(2, "What changed on Switch 2", "changes"));
        blocks.Add(Para(T("From "), L("Zelda Wiki's article on the remake", ZeldaWiki.PageUrl(RemakePage)), T($" ({ZeldaWiki.Licence}).", italic: true)));
        var changes = await ZeldaWiki.SectionAsync(RemakePage, "Changes and Additions", cancel);
        blocks.AddRange(changes.Where(b => b is not GalleryBlock));
        return blocks;
    }

    private static IReadOnlyList<Block> YoungOrAdultPage(GameDataSave? save)
    {
        var blocks = new List<Block>
        {
            Para(T("Once Link pulls the Master Sword he's adult, and Hyrule Castle Town lies in ruins. Putting the sword back in the Temple of Time " +
                   "makes him young again, so nothing below is lost for good, but these are much quicker now, and some of them are what adult Link needs later. " +
                   (save is null ? "Zest couldn't read a save, so nothing is ticked." : "Ticked from your newest save: ✓ done, ◐ partly, ○ not yet, ? not in the save."))),
        };
        foreach (var tip in OotYoungAdult.Tips(save))
            blocks.AddRange(TipBlocks(tip));
        blocks.Add(new HeadingBlock(2, "Fine to leave for later", "later"));
        blocks.Add(Item("Gold Skulltulas: many need young Link (small holes, soft soil with bugs), but you'll be back as young Link for the Spirit Temple anyway."));
        blocks.Add(Item("Great Fairies, Gossip Stones and Fairy Thoughts: most can be visited in either era."));
        blocks.Add(Item("The Hall of Fallen Foes opens after you've been adult once: Kaepora Gaebora meets young Link in Kakariko Village (Zelda Wiki)."));
        return blocks;
    }

    private static IEnumerable<Block> TipBlocks(EraTip tip)
    {
        yield return new ListItemBlock(
            [new TextSpan(tip.Title, Bold: true), new TextSpan(" — " + tip.Where + ". "), new TextSpan(tip.Why),
             new TextSpan(tip.Detail.Length > 0 ? $" ({tip.Detail})" : "", Italic: true)],
            0, OotYoungAdult.Mark(tip.Status));
    }

    private static async Task<IReadOnlyList<Block>> ChapterAsync(string sub, CancellationToken cancel)
    {
        var info = Info.GetValueOrDefault(sub);
        var blocks = new List<Block>();
        if (info is not null)
            blocks.AddRange(await GalleryAsync(info.Pictures, 6, cancel));

        // ---- Switch 2: the Adventure Log, young Link notes, the remake's changes
        var (data, save, current) = Latest();
        var mine = new List<Block>();
        foreach (var quest in info?.Quests ?? [])
        {
            if (data?.QuestLog(quest) is not { } log || log.Steps.Count == 0)
                continue;
            var steps = data.QuestSteps(quest);
            var at = current.GetValueOrDefault(quest);
            var atIndex = at is null ? -1 : steps.ToList().IndexOf(at);
            mine.Add(new HeadingBlock(3, $"Adventure Log: {log.Name}", null));
            var lines = new List<(string mark, string line)>();
            foreach (var (step, next, _) in log.Steps.Where(s => s.Step != "Complete"))
            {
                var index = steps.ToList().IndexOf(step);
                var mark = atIndex < 0 ? "•" : index < atIndex ? "✓" : index == atIndex ? "→" : "○";
                // The log repeats a line over several steps ("Find Ruto."): show it once, as its latest state
                if (lines.Count > 0 && lines[^1].line == next)
                {
                    var before = lines[^1].mark;
                    // done then not done (or either current): that line is what's being done now
                    lines[^1] = (before == mark ? mark : before == "•" ? "•" : "→", next);
                }
                else
                {
                    lines.Add((mark, next));
                }
            }
            // The current step may have no line of its own: the first one not done is what to do now
            if (atIndex >= 0 && at != "Complete" && !lines.Any(l => l.mark == "→") && lines.FindIndex(l => l.mark == "○") is var first and >= 0)
                lines[first] = ("→", lines[first].line);
            foreach (var (mark, line) in lines)
                mine.Add(new ListItemBlock([new TextSpan(line, Bold: mark == "→")], 0, mark));
            if (at == "Complete")
                mine.Add(Item("Done.", "✓"));
        }
        var tips = OotYoungAdult.Tips(save).Where(t => t.Chapters.Contains(sub, StringComparer.OrdinalIgnoreCase)).ToList();
        if (tips.Count > 0)
        {
            mine.Add(new HeadingBlock(3, "As young Link, while you're here", null));
            foreach (var tip in tips)
                mine.AddRange(TipBlocks(tip));
        }
        if (info is { Changes.Length: > 0 })
        {
            var changes = (await ZeldaWiki.SectionAsync(RemakePage, "Changes and Additions", cancel))
                .Where(b => b is ParagraphBlock or ListItemBlock && Mentions(b, info.Changes)).ToList();
            if (changes.Count > 0)
            {
                mine.Add(new HeadingBlock(3, "Changed in the remake", null));
                mine.Add(Para(T("From "), L("Zelda Wiki", ZeldaWiki.PageUrl(RemakePage)), T($" ({ZeldaWiki.Licence}).", italic: true)));
                mine.AddRange(changes);
            }
        }
        if (mine.Count > 0)
        {
            blocks.Add(new HeadingBlock(2, "On Switch 2", "switch-2"));
            blocks.AddRange(mine);
        }

        // ---- the walkthrough of the original, with the remake's names
        var original = Original + "/" + sub;
        blocks.Add(new HeadingBlock(2, "Walkthrough", "walkthrough"));
        blocks.Add(Para(T("From "), L("StrategyWiki's guide to the original", StrategyWiki.PageUrl(original)),
            T($" ({StrategyWiki.Licence}), with places and items renamed for Switch 2. The remake redesigns some rooms and puzzles, so trust the notes above where they differ.", italic: true)));
        var (_, text) = await StrategyWiki.PageAsync(original, cancel);
        var renames = await RenamesAsync(cancel);
        blocks.AddRange(text.Select(b => RenameBlock(b, renames)));
        return blocks;
    }

    // ---- pictures

    private static readonly string[] NotScreenshots =
        ["_Model", "_Icon", "_Render", "Box_Art", "_Logo", "Artwork", "_JP_", "Flag", "Bundle", "Wallpaper", "Mini-Map", "Merchandise", "Amiibo", "Pin_"];

    /// <summary>Up to <paramref name="max"/> remake pictures whose names have one of these words: a map first, then screenshots.</summary>
    private static async Task<IReadOnlyList<Block>> GalleryAsync(string[] words, int max, CancellationToken cancel)
    {
        try
        {
            var files = (await ZeldaWiki.FilesAsync("OoTNS2_", cancel))
                .Where(f => !NotScreenshots.Any(n => f.Contains(n, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var picked = new List<string>();
            foreach (var word in words)
            {
                var hits = files.Where(f => f.Contains(word, StringComparison.OrdinalIgnoreCase) && !picked.Contains(f)).ToList();
                // One map per place, then the rest
                picked.AddRange(hits.Where(h => h.Contains("_Map")).Take(1));
                picked.AddRange(hits.Where(h => !h.Contains("_Map")).Take(2));
                if (picked.Count >= max)
                    break;
            }
            if (picked.Count == 0)
                return [];
            // Thumbnails three to a row (fetched at twice the size, so they stay sharp)
            const int thumb = 280;
            var images = await ZeldaWiki.ImagesAsync([.. picked.Take(max)], thumb * 2, cancel);
            return images.Count == 0 ? [] :
            [
                new GalleryBlock([.. images.Select(i => new ImageBlock(i.Url, thumb, i.Width > 0 ? i.Height * thumb / i.Width : 0, Caption(i.Name)))]),
                Para(T("Pictures © Nintendo, from Zelda Wiki's gallery of the remake.", italic: true)),
            ];
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return [];   // pictures are a bonus: the page reads fine without them
        }
    }

    private static string Caption(string file) =>
        Regex.Replace(Path.GetFileNameWithoutExtension(file).Replace('_', ' ').Replace("OoTNS2 ", ""), @" (Promotional Screenshot|Introduction)( \d)?$", "");

    // ---- renames: the remake's names for places and items (Zelda Wiki's list), applied to StrategyWiki's text

    private static IReadOnlyList<(string from, string to)>? _renames;

    /// <summary>Names too general to replace everywhere ("Bottle" → "Empty Bottle"), or only true in one era.</summary>
    private static readonly HashSet<string> KeepNames = ["Bottle", "Wallet", "Bottled Bug", "Cucco", "Magic Bean", "Lon Lon Milk (Half)", "Kokiri Tunic", "Joelle"];

    private static async Task<IReadOnlyList<(string from, string to)>> RenamesAsync(CancellationToken cancel)
    {
        if (_renames is not null)
            return _renames;
        var list = new List<(string, string)>();
        try
        {
            var text = await ZeldaWiki.WikitextAsync(RemakePage, cancel) ?? "";
            foreach (Match m in RenameLine().Matches(text))
            {
                var from = m.Groups[1].Value.Trim();
                if (!KeepNames.Contains(from) && !m.Groups[2].Success)
                    list.Add((from, m.Groups[3].Value.Trim()));
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
        {
        }
        // StrategyWiki's own spellings of a few of them
        list.Add(("Jabu-Jabu's Belly", "Lord Jabu-Jabu's Belly"));
        list.Add(("Gerudo Training Ground", "Gerudo Proving Grounds"));
        list.Add(("Zora's River", "Zora River"));
        // Longest first, so "Inside Jabu-Jabu's Belly" wins over "Jabu-Jabu's Belly"
        _renames = [.. list.OrderByDescending(r => r.Item1.Length)];
        return _renames;
    }

    public static string Rename(string text, IReadOnlyList<(string from, string to)> renames)
    {
        foreach (var (from, to) in renames)
        {
            if (!text.Contains(from, StringComparison.Ordinal))
                continue;
            text = Regex.Replace(text, $@"(?<![\w'-]){Regex.Escape(from)}(?![\w-])", m =>
            {
                // "Desert Colossus" inside "Evil Desert Colossus" is already the new name
                var lead = to.EndsWith(from, StringComparison.Ordinal) ? to[..^from.Length] : "";
                return lead.Length > 0 && m.Index >= lead.Length && text.AsSpan(m.Index - lead.Length, lead.Length).SequenceEqual(lead) ? m.Value : to;
            });
        }
        return text;
    }

    private static Block RenameBlock(Block b, IReadOnlyList<(string from, string to)> renames)
    {
        IReadOnlyList<TextSpan> R(IReadOnlyList<TextSpan> spans) => [.. spans.Select(s => s with { Text = Rename(s.Text, renames) })];
        return b switch
        {
            HeadingBlock h => h with { Text = Rename(h.Text, renames) },
            ParagraphBlock p => p with { Spans = R(p.Spans) },
            ListItemBlock l => l with { Spans = R(l.Spans) },
            TableBlock t => t with { Rows = [.. t.Rows.Select(r => (IReadOnlyList<TableCell>)[.. r.Select(c => c with { Spans = R(c.Spans) })])] },
            _ => b,
        };
    }

    private static bool Mentions(Block b, string[] words)
    {
        var spans = b switch { ParagraphBlock p => p.Spans, ListItemBlock l => l.Spans, _ => [] };
        var text = string.Concat(spans.Select(s => s.Text));
        return words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    // ---- the save

    /// <summary>The game data and newest save (by its own date) with each quest's current step; nulls when unreadable.</summary>
    private static (OotProgress? data, GameDataSave? save, Dictionary<string, string> steps) Latest()
    {
        try
        {
            if (OotProgress.FindRomfs() is not { } romfs)
                return (null, null, []);
            var data = OotProgress.Load(romfs);
            var newest = OotProgress.FindSources().SelectMany(s => s.SlotFolders).Select(data.ReadCaption)
                .OrderByDescending(c => c.Saved).FirstOrDefault();
            if (newest is null)
                return (data, null, []);
            var save = GameDataSave.Load(Path.Combine(newest.Folder, "progress.sav"));
            var steps = data.ReadSlot(newest.Folder).Quests.ToDictionary(q => q.Id, q => q.Step);
            return (data, save, steps);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ZstdSharp.ZstdException)
        {
            return (null, null, []);
        }
    }

    // ---- block helpers

    private static TextSpan T(string text, bool italic = false) => new(text, Italic: italic);
    private static TextSpan L(string text, string url) => new(text, Href: url);
    private static ParagraphBlock Para(params TextSpan[] spans) => new(spans);
    private static ListItemBlock Item(string text, string marker = "•") => new([new TextSpan(text)], 0, marker);

    // "* [[Inside the Deku Tree]] → '''Great Deku Tree'''", "* [[Kokiri Tunic]] (Adult-era only) → '''Hero's Clothes'''"
    [GeneratedRegex(@"^\*\s*\[\[([^\]|]+)(?:\|[^\]]*)?\]\]\s*(\([^)]*\)\s*)?→\s*'{3,5}([^']+?)'{3,5}", RegexOptions.Multiline)]
    private static partial Regex RenameLine();
}
