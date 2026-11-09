using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zest.Services.GameData;

namespace Zest.ViewModels;

public sealed class SlotChoice(SlotCaption caption, string source)
{
    public SlotCaption Caption { get; } = caption;
    public string Label { get; } =
        $"{(caption.AutoSave ? "Autosave" : "Save")} {caption.Index + 1} · {caption.Location} · {caption.Saved:ddd d MMM, HH:mm}";
    public string Source { get; } = source;
}

public sealed class QuestViewModel(QuestProgress quest, Func<string, bool> canOpenGuide)
{
    public QuestProgress Quest { get; } = quest;
    public string Name => Quest.Name;
    public string Summary => Quest.Summary;
    public string NextAction => Quest.NextAction;
    public bool HasNextAction => NextAction.Length > 0;
    public bool HasSummary => Summary.Length > 0;
    public string Kind => Quest.Main ? "Main quest" : "Side quest";
    public string StepText => Quest.State switch
    {
        QuestState.Complete => "Complete",
        _ when Quest.StepCount > 0 => $"Step {Math.Max(Quest.StepIndex, 1)} of {Quest.StepCount}" + (Quest.BossBattle ? " · boss fight" : ""),
        _ => "",
    };
    public double Fraction => Quest.StepCount == 0 ? 0 : Quest.State == QuestState.Complete ? 1 : (double)Quest.StepIndex / Quest.StepCount;
    public bool HasGuide => canOpenGuide(Quest.Id);
}

public sealed record CollectibleViewModel(string Name, bool Have)
{
    public double Opacity => Have ? 1 : 0.38;
    public string Mark => Have ? "✓" : "·";
}

public sealed record DungeonViewModel(DungeonProgress D)
{
    public string Name => D.Name;
    public string Status => D.Cleared switch
    {
        true => "Cleared",
        false when D.Visited => "In progress",
        _ => D.Visited ? "Visited" : "Not yet",
    };
    public string Map => D.Map ? "✓" : "–";
    public string Compass => D.Compass ? "✓" : "–";
    public string BossKey => D.BossKey ? "✓" : "–";
    public string Keys => D.SmallKeys > 0 ? D.SmallKeys.ToString() : "–";
    public double Opacity => D.Visited || D.Cleared == true ? 1 : 0.45;
}

/// <summary>
/// Ocarina of Time (Switch 2)'s progress read from its saves: the game's own Adventure Log (what you've done, what to
/// do next), the save's picture and place, and a tracker for songs, stones, items, dungeons and places. It follows
/// the newest save in Lichii or Hookshot and refreshes when the game saves.
/// </summary>
public partial class AdventureLogViewModel : ViewModelBase, IDisposable
{
    private readonly Func<string, bool> _canOpenGuide;
    private readonly Action<string> _openGuide;
    private OotProgress? _data;
    private FileSystemWatcher[] _watchers = [];
    private DispatcherTimer? _debounce;
    private bool _loading;
    private bool _choosing;
    private bool _started;

    public AdventureLogViewModel(Func<string, bool> canOpenGuide, Action<string> openGuide)
    {
        _canOpenGuide = canOpenGuide;
        _openGuide = openGuide;
    }

    /// <summary>The game this page reads: Ocarina of Time on Switch 2 only.</summary>
    public static bool Supports(string title, Models.Platform platform) =>
        platform == Models.Platform.Switch2
        && Services.LibraryScanner.TitleKey(Services.Walkthroughs.StrategyWiki.CleanTitle(title))
           == Services.LibraryScanner.TitleKey("The Legend of Zelda: Ocarina of Time");

    public ObservableCollection<SlotChoice> Slots { get; } = [];
    public ObservableCollection<QuestViewModel> Current { get; } = [];
    public ObservableCollection<QuestViewModel> Completed { get; } = [];
    public ObservableCollection<CollectibleViewModel> Songs { get; } = [];
    public ObservableCollection<CollectibleViewModel> Stones { get; } = [];
    public ObservableCollection<CollectibleViewModel> Items { get; } = [];
    public ObservableCollection<CollectibleViewModel> Equipment { get; } = [];
    public ObservableCollection<DungeonViewModel> Dungeons { get; } = [];
    public ObservableCollection<CollectibleViewModel> Places { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    public partial SlotProgress? Progress { get; set; }

    public bool HasProgress => Progress is not null;

    [ObservableProperty]
    public partial string Message { get; set; } = "Reading your save…";

    [ObservableProperty]
    public partial SlotChoice? SelectedSlot { get; set; }

    /// <summary>On: show the newest save each time the game saves. Picking an older slot turns it off.</summary>
    [ObservableProperty]
    public partial bool FollowLatest { get; set; } = true;

    [ObservableProperty]
    public partial Bitmap? Picture { get; set; }

    [ObservableProperty]
    public partial QuestViewModel? NextUp { get; set; }

    [ObservableProperty]
    public partial string Stats { get; set; } = "";

    [ObservableProperty]
    public partial string Where { get; set; } = "";

    [ObservableProperty]
    public partial string Counts { get; set; } = "";

    /// <summary>One line for the details page card: "Next: Explore Death Mountain."</summary>
    [ObservableProperty]
    public partial string Headline { get; set; } = "";

    partial void OnSelectedSlotChanged(SlotChoice? value)
    {
        if (value is null || _choosing)
            return;
        FollowLatest = Slots.Count > 0 && value == Slots[0];
        _ = ShowSlotAsync(value);
    }

    partial void OnFollowLatestChanged(bool value)
    {
        if (value && !_choosing)
            _ = RefreshAsync();
    }

    /// <summary>Reads the game's text (once) and the saves, then watches the save folders.</summary>
    public async Task StartAsync()
    {
        if (_started)
            return;
        _started = true;
        await RefreshAsync();
        Watch();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_loading)
            return;
        _loading = true;
        try
        {
            var romfs = OotProgress.FindRomfs();
            if (romfs is null)
            {
                Message = "The Adventure Log reads its text from the game's files. Install the native version (Hookshot) " +
                          "from the details page first; your saves can stay in Lichii.";
                return;
            }
            _data ??= await Task.Run(() => OotProgress.Load(romfs));
            var data = _data;
            var slots = await Task.Run(() => OotProgress.FindSources()
                .SelectMany(src => src.SlotFolders.Select(f => new SlotChoice(data.ReadCaption(f), src.Name)))
                .OrderByDescending(s => s.Caption.Saved)
                .ToList());
            if (slots.Count == 0)
            {
                Message = "No Ocarina of Time saves yet in Lichii or Hookshot.";
                return;
            }
            var keep = SelectedSlot?.Caption.Folder;
            _choosing = true;
            Slots.Clear();
            foreach (var s in slots)
                Slots.Add(s);
            SelectedSlot = FollowLatest ? Slots[0] : Slots.FirstOrDefault(s => s.Caption.Folder == keep) ?? Slots[0];
            _choosing = false;
            await ShowSlotAsync(SelectedSlot);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ZstdSharp.ZstdException
                                       or IndexOutOfRangeException or ArgumentException)
        {
            Message = $"Couldn't read the save: {e.Message}";
        }
        finally
        {
            _choosing = false;
            _loading = false;
        }
    }

    private async Task ShowSlotAsync(SlotChoice slot)
    {
        if (_data is not { } data)
            return;
        SlotProgress p;
        try
        {
            p = await Task.Run(() => data.ReadSlot(slot.Caption.Folder));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // The game may be writing the save right now: the watcher will come back
            Message = $"Couldn't read {Path.GetFileName(slot.Caption.Folder)}: {e.Message}";
            return;
        }
        Progress = p;
        Message = "";

        Picture?.Dispose();
        Picture = null;
        if (p.Caption.Thumbnail is { } jpeg)
        {
            try
            {
                using var ms = new MemoryStream(jpeg);
                Picture = new Bitmap(ms);
            }
            catch (Exception)
            {
                // A damaged picture shouldn't hide the log
            }
        }

        // Main quests first, and the specific ones ("Spiritual Stone of Fire") before the ones spanning them
        Replace(Current, p.Quests.Where(q => q.State == QuestState.InProgress)
            .OrderByDescending(q => q.Main).ThenBy(q => Umbrella.Contains(q.Id))
            .Select(q => new QuestViewModel(q, _canOpenGuide)));
        Replace(Completed, p.Quests.Where(q => q.State == QuestState.Complete).Select(q => new QuestViewModel(q, _canOpenGuide)));
        Replace(Songs, p.Songs.Select(c => new CollectibleViewModel(c.Name, c.Have)));
        Replace(Stones, p.Stones.Select(c => new CollectibleViewModel(c.Name, c.Have)));
        Replace(Items, p.Items.Select(c => new CollectibleViewModel(c.Name, c.Have)));
        Replace(Equipment, p.Equipment.Select(c => new CollectibleViewModel(c.Name, c.Have)));
        Replace(Dungeons, p.Dungeons.Select(d => new DungeonViewModel(d)));
        Replace(Places, p.Places.Select(c => new CollectibleViewModel(c.Name, c.Have)));

        NextUp = Current.FirstOrDefault(q => q.Quest.Main && q.HasNextAction) ?? Current.FirstOrDefault(q => q.HasNextAction);
        var c = p.Caption;
        var hearts = c.MaxLifeQuarters / 4;
        var pieces = c.MaxLifeQuarters % 4;
        Stats = $"♥ {hearts}{(pieces > 0 ? $" + {pieces}/4" : "")} hearts   ·   {c.GoldTokens} of 100 Gold Skulltulas   ·   {c.Rupees} rupees";
        Where = $"{c.Location} · {(c.AutoSave ? "autosave" : "saved")} {c.Saved:ddd d MMM, HH:mm} · {slot.Source}";
        Counts = $"{p.Quests.Count(q => q.Main && q.State == QuestState.Complete)} of {p.Quests.Count(q => q.Main)} main quests · " +
                 $"{p.Songs.Count(s => s.Have)}/12 songs · {p.Stones.Count(s => s.Have)}/9 stones and medallions · " +
                 $"{p.Places.Count(s => s.Have)}/{p.Places.Count} places";
        Headline = NextUp is { } n ? $"Next: {n.NextAction}" : "";
    }

    /// <summary>Quests that span others: "Three Spiritual Stones" is done by the three stone quests.</summary>
    private static readonly HashSet<string> Umbrella = ["DoorOfTime", "AllSage"];

    private static void Replace<T>(ObservableCollection<T> list, IEnumerable<T> items)
    {
        list.Clear();
        foreach (var i in items)
            list.Add(i);
    }

    [RelayCommand]
    private void OpenGuide(QuestViewModel quest) => _openGuide(quest.Quest.Id);

    /// <summary>The game saves (autosaves too) → refresh a moment later, once it has finished writing.</summary>
    private void Watch()
    {
        foreach (var w in _watchers)
            w.Dispose();
        _debounce ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            _ = RefreshAsync();
        });
        _watchers = [.. OotProgress.FindSources().Select(src =>
        {
            var w = new FileSystemWatcher(src.Folder, "*.sav") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            w.Changed += OnSaveChanged;
            w.Created += OnSaveChanged;
            w.Renamed += OnSaveChanged;
            w.EnableRaisingEvents = true;
            return w;
        })];
    }

    private void OnSaveChanged(object sender, FileSystemEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            _debounce?.Stop();
            _debounce?.Start();
        });

    public void Dispose()
    {
        foreach (var w in _watchers)
            w.Dispose();
        _watchers = [];
        _debounce?.Stop();
        GC.SuppressFinalize(this);
    }
}
