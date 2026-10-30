using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PKForge.Domain;
using PKForge.Engine;
using Zest.Services.Forge;

namespace Zest.ViewModels;

/// <summary>A bag pouch chip ("Medicine", "Key Items").</summary>
public partial class PouchChip : ObservableObject
{
    public required string Id { get; init; }
    public required string Label { get; init; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>One item in the open pouch; <see cref="Count"/> is edited in place and saved together.</summary>
public partial class BagRow : ObservableObject
{
    public required int ItemId { get; init; }
    public required string Name { get; init; }
    public required int Original { get; init; }

    [ObservableProperty]
    public partial decimal Count { get; set; }
}

public sealed record ItemChoice(int Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A Mystery Gift card from PKForge's offline database.</summary>
public sealed record GiftRow(EventGift Gift, string Subject)
{
    public string Title => Gift.Title;
    public string Facts => string.Join(" · ", new[]
    {
        Gift.Kind == EventGiftKind.Item ? "Items" : Subject,
        Gift.Kind == EventGiftKind.Pokemon && Gift.Level > 0 ? $"Lv. {Gift.Level}" : null,
        Gift.Shiny ? "Shiny" : null,
        Gift.Year?.ToString(),
        Gift.CardId > 0 ? $"Card #{Gift.CardId}" : null,
    }.Where(x => !string.IsNullOrEmpty(x)));
}

/// <summary>Forge's Trainer, Bag and Gifts tabs: save-wide data beside the boxes.</summary>
public partial class ForgeViewModel
{
    private IReadOnlyList<GiftRow> _allGifts = [];
    private bool _confirmCompleteDex;

    // =====================================================================
    // Trainer
    // =====================================================================

    [ObservableProperty]
    public partial string TrainerName { get; set; } = "";

    [ObservableProperty]
    public partial decimal TrainerTid { get; set; }

    [ObservableProperty]
    public partial decimal TrainerSid { get; set; }

    [ObservableProperty]
    public partial decimal TrainerMoney { get; set; }

    [ObservableProperty]
    public partial int TrainerGender { get; set; }

    public IReadOnlyList<string> Genders { get; } = ["Male", "Female"];

    [ObservableProperty]
    public partial TrainerStats? TrainerStatsInfo { get; set; }

    [ObservableProperty]
    public partial decimal PlayHours { get; set; }

    [ObservableProperty]
    public partial decimal PlayMinutes { get; set; }

    [ObservableProperty]
    public partial decimal BattlePoints { get; set; }

    [ObservableProperty]
    public partial decimal Coins { get; set; }

    [ObservableProperty]
    public partial string DexText { get; set; } = "";

    [ObservableProperty]
    public partial string CompleteDexText { get; set; } = "Complete Pokédex";

    // =====================================================================
    // Bag
    // =====================================================================

    public ObservableCollection<PouchChip> Pouches { get; } = [];
    public ObservableCollection<BagRow> BagItems { get; } = [];
    public ObservableCollection<ItemChoice> AddableItems { get; } = [];

    [ObservableProperty]
    public partial ItemChoice? ItemToAdd { get; set; }

    [ObservableProperty]
    public partial decimal AddCount { get; set; } = 1;

    private string? _pouch;

    // =====================================================================
    // Gifts
    // =====================================================================

    public ObservableCollection<GiftRow> Gifts { get; } = [];

    [ObservableProperty]
    public partial string GiftSearch { get; set; } = "";

    [ObservableProperty]
    public partial GiftRow? SelectedGift { get; set; }

    [ObservableProperty]
    public partial string GiftSummary { get; set; } = "";

    partial void OnGiftSearchChanged(string value) => FilterGifts();

    /// <summary>Reloads the Trainer/Bag/Gifts data of the open save (gifts only when asked: the list is large).</summary>
    private void RefreshSaveTabs(bool reloadGifts = false)
    {
        if (_forge.Sessions.CurrentSession is not { } session)
            return;
        try
        {
            LoadTrainer(session);
            LoadExtras(session);
            LoadBag(session);
            if (reloadGifts || (Tab == ForgeTab.Gifts && _allGifts.Count == 0))
                LoadGifts(session);
        }
        catch (Exception error)
        {
            Status = $"Could not read this save's trainer data: {error.Message}";
        }
    }

    private void LoadTrainer(ISaveEngineSession session)
    {
        var t = session.GetTrainer();
        TrainerName = t.Name;
        TrainerTid = t.TID;
        TrainerSid = t.SID;
        TrainerMoney = t.Money;
        TrainerGender = Math.Clamp(t.Gender, 0, 1);

        var stats = session.GetTrainerStats();
        TrainerStatsInfo = stats;
        PlayHours = stats.PlayTimeHours;
        PlayMinutes = stats.PlayTimeMinutes;
        BattlePoints = stats.BP;
        Coins = stats.Coins;

        var dex = session.GetDexProgress();
        DexText = $"Pokédex: {dex.Caught} caught, {dex.Seen} seen of {dex.Total}";
        _confirmCompleteDex = false;
        CompleteDexText = "Complete Pokédex";
    }

    [RelayCommand]
    private async Task SaveTrainerAsync()
    {
        var stats = TrainerStatsInfo;
        var trainer = new TrainerInfo(TrainerName.Trim(), (int)TrainerTid, (int)TrainerSid, (uint)TrainerMoney, TrainerGender);
        var edit = new TrainerStatsEdit(
            PlayTimeHours: stats is { SupportsPlayTime: true } ? (int)PlayHours : null,
            PlayTimeMinutes: stats is { SupportsPlayTime: true } ? (int)PlayMinutes : null,
            BP: stats is { SupportsBP: true } ? (int)BattlePoints : null,
            Coins: stats is { SupportsCoins: true } ? (int)Coins : null);
        if (trainer.Name.Length == 0)
        {
            Status = "The trainer needs a name.";
            return;
        }
        await CommitAsync(s =>
        {
            s.SetTrainer(trainer);
            s.SetTrainerStats(edit);
            return new GenerationOutcome(true, "Trainer saved.");
        }, [], "Edit trainer");
    }

    [RelayCommand]
    private async Task CompleteDexAsync()
    {
        // Two taps: this marks every species in the game seen and caught, which can't be told apart afterwards.
        if (!_confirmCompleteDex)
        {
            _confirmCompleteDex = true;
            CompleteDexText = "Tap again to mark every species caught";
            return;
        }
        await CommitAsync(s =>
        {
            s.CompleteDex();
            return new GenerationOutcome(true, "Pokédex completed.");
        }, [], "Complete the Pokédex");
    }

    // ---- Bag ----

    private void LoadBag(ISaveEngineSession session)
    {
        var bag = session.GetBag();
        var current = _pouch;
        Pouches.Clear();
        foreach (var pouch in bag)
            Pouches.Add(new PouchChip { Id = pouch.Name, Label = SpacedName(pouch.Name) });
        _pouch = Pouches.Any(p => p.Id == current) ? current : Pouches.FirstOrDefault()?.Id;
        ShowPouch(session, bag);
    }

    [RelayCommand]
    private void SelectPouch(PouchChip chip)
    {
        if (_forge.Sessions.CurrentSession is not { } session)
            return;
        _pouch = chip.Id;
        ShowPouch(session, session.GetBag());
    }

    private void ShowPouch(ISaveEngineSession session, IReadOnlyList<BagPouch> bag)
    {
        foreach (var chip in Pouches)
            chip.IsSelected = chip.Id == _pouch;
        BagItems.Clear();
        AddableItems.Clear();
        if (bag.FirstOrDefault(p => p.Name == _pouch) is not { } pouch)
            return;
        // The TM pouch reads best in number order (TM01, TM02 … HM01, TR00…); other pouches keep the game's order.
        var machines = pouch.Name.Contains("TM", StringComparison.OrdinalIgnoreCase);
        foreach (var item in machines ? pouch.Items.OrderBy(i => BagName(i.Id), StringComparer.OrdinalIgnoreCase) : pouch.Items.AsEnumerable())
            BagItems.Add(new BagRow { ItemId = item.Id, Name = BagName(item.Id), Original = item.Count, Count = item.Count });
        var held = pouch.Items.Select(i => i.Id).ToHashSet();
        foreach (var id in session.GetPouchLegalItems(pouch.Name).Where(id => id > 0 && !held.Contains(id))
                     .OrderBy(BagName, StringComparer.OrdinalIgnoreCase))
            AddableItems.Add(new ItemChoice(id, BagName(id)));
        ItemToAdd = null;
        AddCount = 1;
    }

    [RelayCommand]
    private void AddItem()
    {
        if (ItemToAdd is not { } item)
            return;
        BagItems.Add(new BagRow { ItemId = item.Id, Name = item.Name, Original = 0, Count = Math.Max(1, AddCount) });
        AddableItems.Remove(item);
        ItemToAdd = null;
    }

    [RelayCommand]
    private void RemoveItem(BagRow? row)
    {
        if (row is not null)
            row.Count = 0;
    }

    [RelayCommand]
    private async Task SaveBagAsync()
    {
        if (_pouch is not { } pouch)
            return;
        var changes = BagItems.Where(r => (int)r.Count != r.Original).Select(r => (r.ItemId, Count: (int)r.Count)).ToList();
        if (changes.Count == 0)
        {
            Status = "Nothing changed in this pouch.";
            return;
        }
        await CommitAsync(s =>
        {
            foreach (var (id, count) in changes)
                s.SetItemCount(pouch, id, count);
            return new GenerationOutcome(true, $"{SpacedName(pouch)} saved ({changes.Count} item{(changes.Count == 1 ? "" : "s")} changed; counts are capped at what the game allows).");
        }, [], $"Edit bag: {SpacedName(pouch)}");
    }

    /// <summary>Bag label: machines name the move they teach in this game ("TM40 - Aerial Ace").</summary>
    private string BagName(int id) =>
        CurrentSave is { } save ? MachineMoves.Label(save.GameName, ItemName(id), MoveNames) : ItemName(id);

    private string ItemName(int id) =>
        id < ItemNames.Count && ItemNames[id].Length > 0 ? ItemNames[id] : $"Item {id}";

    /// <summary>"KeyItems" → "Key Items", "TMHMs" stays readable.</summary>
    private static string SpacedName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");

    // ---- Gifts ----

    private void LoadGifts(ISaveEngineSession session)
    {
        var archive = EventArchive.EnsureLoaded(session.Generation);
        var db = new EventDatabaseService();
        _allGifts = [.. db.GetGifts(session)
            .Select(g => new GiftRow(g, g.Species > 0 ? SpeciesName(g.Species) : ""))
            .OrderByDescending(g => g.Gift.Year ?? 0).ThenBy(g => g.Title)];
        GiftSummary = _allGifts.Count == 0
            ? "No Mystery Gifts for this game."
            : $"{_allGifts.Count} Mystery Gifts for this game{(archive ? "" : " (built-in list only; the full archive could not be read)")}.";
        FilterGifts();
    }

    private void FilterGifts()
    {
        var words = GiftSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Gifts.Clear();
        foreach (var g in _allGifts.Where(g => words.All(w =>
                     g.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                     g.Subject.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                     g.Gift.Header.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                     g.Gift.Year?.ToString() == w)))
            Gifts.Add(g);
    }

    [RelayCommand]
    private async Task ReceiveGiftAsync()
    {
        if (SelectedGift is not { } row || _forge.Sessions.CurrentSession is not { } session)
            return;
        var db = new EventDatabaseService();
        if (row.Gift.Kind == EventGiftKind.Item)
        {
            await CommitAsync(s => db.ReceiveItems(s, row.Gift.Id), [], $"Receive gift: {row.Title}");
            return;
        }
        if (FirstEmptySlot(session) is not { } room)
        {
            Status = "No empty box slot for the gift.";
            return;
        }
        if (await CommitAsync(s => db.Receive(s, row.Gift.Id, room.Box, room.Slot), [room], $"Receive gift: {row.Title}"))
            Status += $" It's in box {room.Box + 1}, slot {room.Slot + 1}.";
    }
}
