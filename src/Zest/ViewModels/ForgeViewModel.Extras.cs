using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PKForge.Domain;

namespace Zest.ViewModels;

/// <summary>A Poké Bean count (Ultra Sun/Moon's Poké Pelago).</summary>
public partial class BeanRow : ObservableObject
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required int Original { get; init; }
    public required int Max { get; init; }

    [ObservableProperty]
    public partial decimal Count { get; set; }
}

/// <summary>A Pokémon left at the Day Care / Nursery.</summary>
public sealed record DaycareRow(int Facility, int Slot, string Text);

/// <summary>Game-specific extras on the Trainer tab, shown only when the open save has them.</summary>
public partial class ForgeViewModel
{
    public ObservableCollection<BeanRow> Beans { get; } = [];
    public ObservableCollection<DaycareRow> Daycare { get; } = [];

    [ObservableProperty]
    public partial bool HasBeans { get; set; }

    [ObservableProperty]
    public partial bool HasDaycare { get; set; }

    [ObservableProperty]
    public partial bool CanUnlockFashion { get; set; }

    private void LoadExtras(ISaveEngineSession session)
    {
        Beans.Clear();
        foreach (var bean in Safe(session.GetPokeBeans, []))
            Beans.Add(new BeanRow { Index = bean.Id, Name = bean.Name, Original = bean.Count, Max = bean.MaxCount, Count = bean.Count });
        HasBeans = Beans.Count > 0;

        Daycare.Clear();
        var daycare = Safe(session.GetDaycare, new DaycareInfo(false, []));
        for (var f = 0; f < daycare.Facilities.Count; f++)
        {
            var facility = daycare.Facilities[f];
            foreach (var slot in facility.Slots.Where(s => s.Occupied))
                Daycare.Add(new DaycareRow(f, slot.Index,
                    $"{(slot.Nickname.Length > 0 ? slot.Nickname : slot.SpeciesName)} ({slot.SpeciesName}, Lv. {slot.Level}) at the {facility.Name}"));
        }
        HasDaycare = daycare.Supported && Daycare.Count > 0;

        CanUnlockFashion = session.SupportsLegalFashionUnlock;
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException) { return fallback; }
    }

    [RelayCommand]
    private async Task SaveBeansAsync()
    {
        var changes = Beans.Where(b => (int)b.Count != b.Original).Select(b => (b.Index, Count: (int)b.Count)).ToList();
        if (changes.Count == 0)
        {
            Status = "No bean counts changed.";
            return;
        }
        await CommitAsync(s =>
        {
            foreach (var (index, count) in changes)
                s.SetPokeBeanCount(index, count);
            return new GenerationOutcome(true, "Poké Beans saved.");
        }, [], "Edit Poké Beans");
    }

    [RelayCommand]
    private async Task UnlockFashionAsync() =>
        await CommitAsync(s =>
        {
            s.UnlockAllLegalFashion();
            return new GenerationOutcome(true, "Every fashion item the game can legally give you is unlocked.");
        }, [], "Unlock fashion");

    [RelayCommand]
    private async Task WithdrawDaycareAsync(DaycareRow? row)
    {
        if (row is null || _forge.Sessions.CurrentSession is not { } session || FirstEmptySlot(session) is not { } room)
        {
            Status = "No empty box slot to take it to.";
            return;
        }
        // The engine picks the first empty box slot itself; scope the write to the slot it will use.
        await CommitAsync(s =>
        {
            var landed = s.WithdrawDaycareToFirstEmptyBox(row.Facility, row.Slot);
            return new GenerationOutcome(true, $"{landed.SpeciesName} is back, in box {landed.Box + 1}, slot {landed.Slot + 1}.");
        }, [room], "Take a Pokémon out of the Day Care");
    }
}
