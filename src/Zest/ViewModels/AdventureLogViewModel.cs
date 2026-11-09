using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zest.Services.GameData;

namespace Zest.ViewModels;

public sealed class SlotChoice(ProgressSlot slot)
{
    public ProgressSlot Slot { get; } = slot;
    public string Label => Slot.Label.Contains(Slot.Source) ? Slot.Label : $"{Slot.Label} · {Slot.Source}";
}

public sealed class QuestViewModel(QuestProgress quest, Func<QuestProgress, bool> canOpenGuide)
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
        _ when Quest.StepCount > 1 => $"{Quest.StepIndex} of {Quest.StepCount} done" + (Quest.BossBattle ? " · boss fight" : ""),
        _ => "",
    };
    public double Fraction => Quest.StepCount == 0 ? 0 : Quest.State == QuestState.Complete ? 1 : (double)Quest.StepIndex / Quest.StepCount;
    public bool HasProgressBar => Quest.StepCount > 1;
    public bool HasGuide => canOpenGuide(Quest);
}

public sealed record CollectibleViewModel(string Name, bool Have)
{
    public double Opacity => Have ? 1 : 0.38;
    public string Mark => Have ? "✓" : "·";
}

public sealed record GroupViewModel(string Title, IReadOnlyList<CollectibleViewModel> Items)
{
    public string Header => $"{Title.ToUpperInvariant()}  ·  {Items.Count(i => i.Have)}/{Items.Count}";
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
/// A game's progress read from its saves (<see cref="IProgressReader"/>): where you are, what to do next, the quests
/// and a tracker. Ocarina of Time (Switch 2) shows the game's own Adventure Log; the Pokémon games show Zest's
/// story steps. It follows the newest save and refreshes when the game saves.
/// </summary>
public partial class AdventureLogViewModel : ViewModelBase, IDisposable
{
    private readonly IProgressReader _reader;
    private readonly Func<QuestProgress, bool> _canOpenGuide;
    private readonly Action<QuestProgress> _openGuide;
    private FileSystemWatcher[] _watchers = [];
    private DispatcherTimer? _debounce;
    private bool _loading;
    private bool _choosing;
    private bool _started;
    private bool _again;

    public AdventureLogViewModel(IProgressReader reader, Func<QuestProgress, bool> canOpenGuide, Action<QuestProgress> openGuide)
    {
        _reader = reader;
        _canOpenGuide = canOpenGuide;
        _openGuide = openGuide;
    }

    /// <summary>The reader for a game whose saves Zest can read progress from, or null.</summary>
    public static IProgressReader? ReaderFor(string title, Models.Platform platform)
    {
        if (platform == Models.Platform.Switch2
            && Services.LibraryScanner.TitleKey(Services.Walkthroughs.StrategyWiki.CleanTitle(title))
               == Services.LibraryScanner.TitleKey("The Legend of Zelda: Ocarina of Time"))
            return new OotReader();
        return PokemonReader.For(title, platform);
    }

    public ObservableCollection<SlotChoice> Slots { get; } = [];
    public ObservableCollection<QuestViewModel> Current { get; } = [];
    public ObservableCollection<QuestViewModel> Completed { get; } = [];
    public ObservableCollection<GroupViewModel> Groups { get; } = [];
    public ObservableCollection<DungeonViewModel> Dungeons { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    public partial ProgressReport? Progress { get; set; }

    public bool HasProgress => Progress is not null;

    public bool HasDungeons => Dungeons.Count > 0;

    public bool HasCompleted => Completed.Count > 0;

    [ObservableProperty]
    public partial string Message { get; set; } = "Reading your save…";

    [ObservableProperty]
    public partial SlotChoice? SelectedSlot { get; set; }

    /// <summary>On: show the newest save each time the game saves. Picking an older one turns it off.</summary>
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

    [ObservableProperty]
    public partial string Footer { get; set; } = "";

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

    /// <summary>Reads the saves, then watches their folders. Once.</summary>
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
        {
            _again = true;   // a save came in while reading: read again after
            return;
        }
        _loading = true;
        try
        {
            do
            {
                _again = false;
                await LoadAsync();
            }
            while (_again);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            if (await Task.Run(_reader.Prepare) is { } problem)
            {
                Message = problem;
                return;
            }
            var slots = await Task.Run(_reader.Slots);
            if (slots.Count == 0)
            {
                Message = _reader.NoSaves;
                return;
            }
            var keep = SelectedSlot?.Slot.Id;
            _choosing = true;
            Slots.Clear();
            foreach (var s in slots)
                Slots.Add(new SlotChoice(s));
            SelectedSlot = FollowLatest ? Slots[0] : Slots.FirstOrDefault(s => s.Slot.Id == keep) ?? Slots[0];
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
        }
    }

    private async Task ShowSlotAsync(SlotChoice slot)
    {
        ProgressReport p;
        try
        {
            p = await Task.Run(() => _reader.Read(slot.Slot));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                       or IndexOutOfRangeException or ArgumentException)
        {
            // The game may be writing the save right now: the watcher will come back
            Message = $"Couldn't read that save: {e.Message}";
            return;
        }
        Progress = p;
        Message = "";

        Picture?.Dispose();
        Picture = null;
        if (p.Picture is { } jpeg)
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
        Replace(Groups, p.Groups.Where(g => g.Items.Count > 0)
            .Select(g => new GroupViewModel(g.Title, [.. g.Items.Select(c => new CollectibleViewModel(c.Name, c.Have))])));
        Replace(Dungeons, p.Dungeons.Select(d => new DungeonViewModel(d)));
        OnPropertyChanged(nameof(HasDungeons));
        OnPropertyChanged(nameof(HasCompleted));

        NextUp = Current.FirstOrDefault(q => q.Quest.Main && q.HasNextAction) ?? Current.FirstOrDefault(q => q.HasNextAction);
        Stats = p.Stats;
        Where = p.Where;
        Counts = p.Counts;
        Footer = p.Footer;
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
    private void OpenGuide(QuestViewModel quest) => _openGuide(quest.Quest);

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
        _watchers = [.. _reader.WatchFolders().Where(Directory.Exists).Select(folder =>
        {
            var w = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
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
