using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zest.Services;
using Zest.Services.GameData;
using Zest.Services.Walkthroughs;

namespace Zest.ViewModels;

public sealed record WalkthroughLink(string Label, string Url);

public partial class ChapterViewModel(WalkthroughViewModel owner, GuideChapter chapter, bool firstInGroup, bool done) : ObservableObject
{
    public GuideChapter Chapter { get; } = chapter;
    public string Label => Chapter.Label;
    public string Group => Chapter.Group;
    /// <summary>Shows the group's name above its first chapter.</summary>
    public bool ShowGroup { get; } = firstInGroup && chapter.Group.Length > 0;

    [ObservableProperty]
    public partial bool Done { get; set; } = done;

    partial void OnDoneChanged(bool value) => owner.SetDone(Chapter.Page, value);
}

public partial class CheckItemViewModel(WalkthroughViewModel owner, CheckItem item) : ObservableObject
{
    public CheckItem Item { get; } = item;
    public string Text => Item.Text;

    [ObservableProperty]
    public partial bool Done { get; set; } = item.Done;

    partial void OnDoneChanged(bool value)
    {
        Item.Done = value;
        owner.SaveNotes();
    }

    [RelayCommand]
    private void Remove() => owner.RemoveItem(this);
}

/// <summary>
/// A game's walkthrough: the details page card (finds the game's StrategyWiki guide, links to other guides) and the
/// reader window's state (chapters, the page being read, chapters done, checklist and notes, kept per game).
/// </summary>
public partial class WalkthroughViewModel : ViewModelBase
{
    private readonly string _title;
    private readonly GameNotes _notes;
    private Guide? _guide;
    private CancellationTokenSource? _load;
    private bool _found;
    private readonly bool _ready;

    private readonly bool _remake;

    public WalkthroughViewModel(string title, Models.Platform platform)
    {
        _title = title;
        _remake = OotRemakeGuide.Supports(title, platform);
        _notes = GameNotes.Load(title);
        Notes = _notes.Notes;
        foreach (var item in _notes.Checklist)
            Checklist.Add(new CheckItemViewModel(this, item));
        Links = WalkthroughLinks.For(title, platform);
        if (AdventureLogViewModel.ReaderFor(title, platform) is { } reader)
            AdventureLog = new AdventureLogViewModel(reader, CanOpenQuestGuide, OpenQuestGuide);
        UpdateSummary();
        _ready = true;
    }

    /// <summary>Progress read from the game's saves (Ocarina of Time on Switch 2, the Pokémon games); it replaces the checklist.</summary>
    public AdventureLogViewModel? AdventureLog { get; }

    public bool HasAdventureLog => AdventureLog is not null;

    /// <summary>The manual checklist, for games without an Adventure Log (kept for ones that already have items).</summary>
    public bool ShowChecklist => AdventureLog is null;

    public string NotesButton => ShowChecklist ? "My notes & checklist" : "My notes";

    /// <summary>The window should show the guide tab (a quest's "Guide" button).</summary>
    public event Action? GuideRequested;

    /// <summary>StrategyWiki chapters for the Adventure Log's quests (OoT's guide is the N64 one; the places match).</summary>
    private static readonly Dictionary<string, string[]> QuestChapters = new()
    {
        ["Meet_DekuTree"] = ["Inside the Deku Tree", "The Beginning"],
        ["Get_StoneOfForest"] = ["Inside the Deku Tree"],
        ["Goto_HyruleCastle"] = ["Hyrule Castle"],
        ["Get_StoneOfFire"] = ["Dodongo's Cavern", "Goron City"],
        ["Get_StoneOfWater"] = ["Jabu-Jabu's Belly"],
        ["DoorOfTime"] = ["Temple of Time"],
        ["SageOfForest"] = ["Forest Temple"],
        ["SageOfFire"] = ["Fire Temple"],
        ["SageOfWater"] = ["Water Temple"],
        ["SageOfShadow"] = ["Shadow Temple", "Bottom of the Well"],
        ["SageOfSpirit"] = ["Spirit Temple"],
        ["AllSage"] = ["Ganon's Castle"],
        ["Defeat_Ganon"] = ["Ganon's Castle"],
        ["Meet_GreatFairy"] = ["Fairy Fountains"],
        ["SubEvent_MaskSalesman"] = ["The Happy Mask Shop"],
        ["GatheringTokenGold"] = ["Gold Skulltulas"],
        ["SubEvent_GetSwordBigGoron"] = ["Item Trading"],
        ["SubEvent_GetGlassTruth"] = ["Bottom of the Well"],
        ["SubEvent_GetScarecrow"] = ["Ocarina Songs"],
        ["SubEvent_GetStormSong"] = ["Ocarina Songs"],
        ["SubEvent_GetMelodySun"] = ["Ocarina Songs"],
        ["SubEvent_GetFireArrow"] = ["Upgrades"],
        ["SubEvent_GetIceArrow"] = ["Gerudo Training Grounds"],
    };

    /// <summary>The guide chapter for a quest: OoT's fixed ones, else the first chapter naming one of its hints ("Turffield").</summary>
    private ChapterViewModel? QuestChapter(QuestProgress quest)
    {
        if (QuestChapters.GetValueOrDefault(quest.Id) is { } names)
            return names.Select(n => Chapters.FirstOrDefault(c => c.Chapter.Page.EndsWith("/" + n, StringComparison.Ordinal)))
                .FirstOrDefault(c => c is not null);
        foreach (var hint in quest.GuideHints ?? [])
        {
            if (Chapters.FirstOrDefault(c => c.Label.Contains(hint, StringComparison.OrdinalIgnoreCase)
                                             || c.Chapter.Page.Split('/').Last().Contains(hint, StringComparison.OrdinalIgnoreCase)) is { } hit)
                return hit;
        }
        return null;
    }

    private bool CanOpenQuestGuide(QuestProgress quest) => QuestChapter(quest) is not null;

    private void OpenQuestGuide(QuestProgress quest)
    {
        if (QuestChapter(quest) is { } chapter)
        {
            GuideRequested?.Invoke();
            _ = OpenPageAsync(chapter.Chapter.Page, null);
        }
    }

    public string GameTitle => _title;
    public IReadOnlyList<WalkthroughLink> Links { get; }

    public bool HasLinks => Links.Count > 0;
    public ObservableCollection<string> Candidates { get; } = [];
    public ObservableCollection<ChapterViewModel> Chapters { get; } = [];
    public ObservableCollection<CheckItemViewModel> Checklist { get; } = [];

    public string Credit => _guide?.Root == OotRemakeGuide.Root
        ? $"Walkthrough text by StrategyWiki contributors ({StrategyWiki.Licence}), renamed for Switch 2. Remake changes from Zelda Wiki ({ZeldaWiki.Licence}); " +
          "pictures © Nintendo via Zelda Wiki; objectives from your game's Adventure Log; young Link notes by Zest."
        : $"Guide text by StrategyWiki contributors, {StrategyWiki.Licence}. Zest shows it as published; edits belong on StrategyWiki.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGuide))]
    public partial string? GuideName { get; set; }

    public bool HasGuide => GuideName is not null;

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCandidates))]
    public partial bool ShowCandidates { get; set; }

    public bool HasCandidates => ShowCandidates && Candidates.Count > 0;

    /// <summary>The search found more than one guide (another version, a remake): offer the choice.</summary>
    public bool CanChangeGuide => Candidates.Count > 1;

    // ---- the page being read

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle), nameof(PageUrl))]
    public partial string? CurrentPage { get; set; }

    public string PageTitle => CurrentPage is null ? "" : CurrentPage == _guide?.Root ? Chapters.FirstOrDefault()?.Label ?? "Overview"
        : Chapters.FirstOrDefault(c => c.Chapter.Page == CurrentPage)?.Label ?? CurrentPage.Split('/').Last();

    public string? PageUrl => CurrentPage is null ? null
        : OotRemakeGuide.IsPage(CurrentPage)
            ? OotRemakeGuide.OriginalPage(CurrentPage) is { } original ? StrategyWiki.PageUrl(original) : ZeldaWiki.PageUrl("The Legend of Zelda: Ocarina of Time (Nintendo Switch 2)")
            : StrategyWiki.PageUrl(CurrentPage);

    [ObservableProperty]
    public partial IReadOnlyList<Block> Blocks { get; set; } = [];

    [ObservableProperty]
    public partial ChapterViewModel? SelectedChapter { get; set; }

    /// <summary>Where the reader should scroll to once the page is drawn: an anchor, or a saved offset.</summary>
    public string? PendingAnchor { get; private set; }
    public double PendingScroll { get; private set; }

    [ObservableProperty]
    public partial bool IsLoadingPage { get; set; }

    [ObservableProperty]
    public partial string PageError { get; set; } = "";

    // ---- notes

    [ObservableProperty]
    public partial string Notes { get; set; }

    partial void OnNotesChanged(string value)
    {
        if (!_ready)
            return;
        _notes.Notes = value;
        SaveNotes();
    }

    [ObservableProperty]
    public partial string NewItem { get; set; } = "";

    /// <summary>Finds the guide (once): the one chosen before, else the search's best match.</summary>
    public async Task FindAsync()
    {
        if (AdventureLog is { } log)
            _ = log.StartAsync();
        if (_found || IsBusy)
            return;
        IsBusy = true;
        Message = "Looking for a guide on StrategyWiki…";
        try
        {
            var candidates = (await StrategyWiki.FindGuidesAsync(_title, CancellationToken.None)).ToList();
            if (_remake)
                candidates.Insert(0, OotRemakeGuide.Root);
            Candidates.Clear();
            foreach (var c in candidates)
                Candidates.Add(c);
            // The Switch 2 guide replaces the original's once (a choice made before it existed doesn't count)
            if (_remake && !_notes.RemakeGuideOffered)
            {
                _notes.RemakeGuideOffered = true;
                _notes.GuideChosen = false;
            }
            var guide = _notes.GuideChosen && _notes.Guide is not null ? _notes.Guide : candidates.FirstOrDefault() ?? _notes.Guide;
            if (guide is null)
            {
                Message = "StrategyWiki has no guide for this game. Try the links below.";
            }
            else
            {
                await UseGuideAsync(guide, chosen: _notes.GuideChosen);
            }
            _found = true;
            // The quests' "Guide" buttons need the chapters
            if (AdventureLog is { HasProgress: true } started)
                _ = started.RefreshAsync();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Message = $"Couldn't reach StrategyWiki: {e.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasCandidates));
            OnPropertyChanged(nameof(CanChangeGuide));
        }
    }

    private async Task UseGuideAsync(string root, bool chosen)
    {
        var guide = root == OotRemakeGuide.Root
            ? await OotRemakeGuide.GuideAsync(CancellationToken.None)
            : await StrategyWiki.GuideAsync(root, CancellationToken.None);
        _guide = guide;
        OnPropertyChanged(nameof(Credit));
        if (_notes.Guide != root)
        {
            _notes.Guide = root;
            _notes.Page = null;
            _notes.Scroll = 0;
        }
        _notes.GuideChosen = chosen;
        SaveNotes();
        Chapters.Clear();
        string? group = null;
        foreach (var c in guide.Chapters)
        {
            Chapters.Add(new ChapterViewModel(this, c, c.Group != group, _notes.DonePages.Contains(c.Page)));
            group = c.Group;
        }
        GuideName = root;
        Message = root == OotRemakeGuide.Root
            ? $"Zest's Switch 2 guide, {guide.Chapters.Count} chapters: StrategyWiki's walkthrough with the remake's names, pictures, changes and your Adventure Log."
            : guide.Chapters.Count > 1 ? $"StrategyWiki guide, {guide.Chapters.Count} chapters." : "StrategyWiki has a page for this game (no chapters).";
        UpdateSummary();
    }

    [RelayCommand]
    private void ToggleCandidates() => ShowCandidates = !ShowCandidates;

    [RelayCommand]
    private async Task ChooseGuideAsync(string root)
    {
        ShowCandidates = false;
        IsBusy = true;
        try
        {
            await UseGuideAsync(root, chosen: true);
            if (CurrentPage is not null)
                await OpenPageAsync(_guide!.Root, null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Message = $"Couldn't load that guide: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The reader opened: show where reading stopped, or the overview.</summary>
    public async Task OpenReaderAsync()
    {
        await FindAsync();
        if (_guide is null || CurrentPage is not null)
            return;
        var page = _notes.Page is { } p && (p == _guide.Root || p.StartsWith(_guide.Root + "/")) ? p : _guide.Root;
        await OpenPageAsync(page, null, _notes.Page == page ? _notes.Scroll : 0);
    }

    partial void OnSelectedChapterChanged(ChapterViewModel? value)
    {
        if (value is not null && value.Chapter.Page != CurrentPage)
            _ = OpenPageAsync(value.Chapter.Page, null);
    }

    /// <summary>Loads a page of the guide; <paramref name="scroll"/> or <paramref name="anchor"/> say where to show.</summary>
    public async Task OpenPageAsync(string page, string? anchor, double scroll = 0)
    {
        _load?.Cancel();
        var load = _load = new CancellationTokenSource();
        IsLoadingPage = true;
        PageError = "";
        try
        {
            var (title, blocks) = OotRemakeGuide.IsPage(page)
                ? await OotRemakeGuide.PageAsync(page, load.Token)
                : await StrategyWiki.PageAsync(page, load.Token);
            if (load.IsCancellationRequested)
                return;
            PendingAnchor = anchor;
            PendingScroll = scroll;
            CurrentPage = title;
            Blocks = blocks;
            SelectedChapter = Chapters.FirstOrDefault(c => c.Chapter.Page == title);
            _notes.Page = title;
            _notes.Scroll = scroll;
            SaveNotes();
            NextCommand.NotifyCanExecuteChanged();
            PreviousCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
        {
            PageError = $"Couldn't load {page}: {e.Message}";
        }
        finally
        {
            if (_load == load)
                IsLoadingPage = false;
        }
    }

    /// <summary>A link in the page: another page of this guide opens here, anything else in the browser.</summary>
    public void FollowLink(string href, Action<string> scrollToAnchor)
    {
        if (StrategyWiki.WikiTarget(href) is { } target)
        {
            if (target.page.Length == 0 || target.page == CurrentPage)
            {
                if (target.anchor is { } a)
                    scrollToAnchor(a);
                return;
            }
            if (_guide is { } g && (target.page == g.Root || target.page.StartsWith(g.Root + "/", StringComparison.Ordinal)))
            {
                _ = OpenPageAsync(target.page, target.anchor);
                return;
            }
            // In the Switch 2 guide, the original's chapters open as this guide's chapters
            if (_guide?.Root == OotRemakeGuide.Root && OotRemakeGuide.MapLink(target.page) is { } mapped
                && Chapters.Any(c => c.Chapter.Page == mapped))
            {
                _ = OpenPageAsync(mapped, target.anchor);
                return;
            }
            OpenUrl(StrategyWiki.PageUrl(target.page) + (target.anchor is null ? "" : "#" + Uri.EscapeDataString(target.anchor)));
            return;
        }
        OpenUrl(href.StartsWith("//") ? "https:" + href : href.StartsWith('/') ? "https://strategywiki.org" + href : href);
    }

    private int CurrentIndex => Chapters.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Chapter.Page == CurrentPage, (null!, -1)).Item2;

    private bool CanNext() => CurrentIndex >= 0 && CurrentIndex < Chapters.Count - 1;
    private bool CanPrevious() => CurrentIndex > 0;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextAsync() => OpenPageAsync(Chapters[CurrentIndex + 1].Chapter.Page, null);

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private Task PreviousAsync() => OpenPageAsync(Chapters[CurrentIndex - 1].Chapter.Page, null);

    [RelayCommand]
    private static void OpenLink(WalkthroughLink link) => OpenUrl(link.Url);

    [RelayCommand]
    private void OpenOnWiki()
    {
        if (PageUrl is { } url)
            OpenUrl(url);
    }

    [RelayCommand]
    private void MarkDoneAndNext()
    {
        if (Chapters.FirstOrDefault(c => c.Chapter.Page == CurrentPage) is { } chapter)
            chapter.Done = true;
        if (CanNext())
            _ = NextAsync();
    }

    /// <summary>The reader's scroll offset, kept so the next open returns there.</summary>
    public void RememberScroll(double offset)
    {
        if (CurrentPage is null || Math.Abs(_notes.Scroll - offset) < 1)
            return;
        _notes.Scroll = offset;
        SaveNotes();
    }

    public void SetDone(string page, bool done)
    {
        if (done ? _notes.DonePages.Add(page) : _notes.DonePages.Remove(page))
        {
            SaveNotes();
            UpdateSummary();
        }
    }

    [RelayCommand]
    private void AddItem()
    {
        var text = NewItem.Trim();
        if (text.Length == 0)
            return;
        var item = new CheckItem { Text = text };
        _notes.Checklist.Add(item);
        Checklist.Add(new CheckItemViewModel(this, item));
        NewItem = "";
        SaveNotes();
    }

    /// <summary>The page being read as a checklist item ("Forest Temple"), to tick off later.</summary>
    [RelayCommand]
    private void AddPageToChecklist()
    {
        if (CurrentPage is null)
            return;
        NewItem = PageTitle;
        AddItem();
    }

    public void RemoveItem(CheckItemViewModel item)
    {
        _notes.Checklist.Remove(item.Item);
        Checklist.Remove(item);
        SaveNotes();
    }

    public void SaveNotes()
    {
        try
        {
            _notes.Save(_title);
        }
        catch (IOException e)
        {
            Message = $"Couldn't save notes: {e.Message}";
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var parts = new List<string>();
        if (Chapters.Count > 1)
            parts.Add($"{Chapters.Count(c => c.Done)} of {Chapters.Count} chapters done");
        if (Checklist.Count > 0 && ShowChecklist)
            parts.Add($"checklist {Checklist.Count(c => c.Done)}/{Checklist.Count}");
        if (_notes.Notes.Length > 0)
            parts.Add("notes saved");
        if (_notes.Page is { } page && _guide is not null)
            parts.Add($"last read: {(page == _guide.Root ? "Overview" : page.Split('/').Last())}");
        Summary = string.Join(" · ", parts);
    }

    public static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo("xdg-open", [u.AbsoluteUri]) { UseShellExecute = false });
    }
}

/// <summary>Other places with guides for a game, by series; they open in the browser.</summary>
public static class WalkthroughLinks
{
    public static IReadOnlyList<WalkthroughLink> For(string title, Models.Platform platform)
    {
        // Ocarina of Time on Switch 2 has Zest's own guide (sources credited in it): no browser links to clutter the card
        if (OotRemakeGuide.Supports(title, platform))
            return [];
        var name = StrategyWiki.CleanTitle(title);
        var q = Uri.EscapeDataString(name);
        var links = new List<WalkthroughLink>();
        if (name.Contains("Zelda", StringComparison.OrdinalIgnoreCase))
        {
            links.Add(new("Zelda Dungeon", $"https://www.zeldadungeon.net/?s={q}+walkthrough"));
            links.Add(new("Zelda Wiki", $"https://zeldawiki.wiki/w/index.php?search={q}"));
        }
        if (name.Contains("Pokémon", StringComparison.OrdinalIgnoreCase) || name.Contains("Pokemon", StringComparison.OrdinalIgnoreCase))
        {
            links.Add(new("Bulbapedia", $"https://bulbapedia.bulbagarden.net/w/index.php?search={q}+walkthrough"));
            links.Add(new("Serebii", "https://www.serebii.net/"));
        }
        if (name.Contains("Mario", StringComparison.OrdinalIgnoreCase) || name.Contains("Yoshi", StringComparison.OrdinalIgnoreCase))
            links.Add(new("Super Mario Wiki", $"https://www.mariowiki.com/index.php?search={q}"));
        if (name.Contains("Kingdom Hearts", StringComparison.OrdinalIgnoreCase))
            links.Add(new("KH Wiki", $"https://www.khwiki.com/index.php?search={q}"));
        links.Add(new("GameFAQs", $"https://gamefaqs.gamespot.com/search?game={q}"));
        links.Add(new("YouTube", $"https://www.youtube.com/results?search_query={q}+walkthrough"));
        return links;
    }
}
