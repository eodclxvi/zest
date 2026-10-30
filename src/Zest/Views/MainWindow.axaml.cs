using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Zest.Models;
using Zest.Services;
using Zest.ViewModels;

namespace Zest.Views;

public partial class MainWindow : Window
{
    private readonly GamepadService _gamepad = new();
    private readonly DispatcherTimer _carouselScroll;
    private double _carouselTarget;
    private DateTime _lastWheel;

    public MainWindow()
    {
        InitializeComponent();

        _gamepad.Pressed += OnPadPressed;
        _gamepad.ConnectionChanged += UpdateControllerHints;
        _carouselScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => StepCarouselScroll());

        // Tunnel so the details page sees keys before the game list underneath does.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Carousel.AddHandler(PointerWheelChangedEvent, OnCarouselWheel, RoutingStrategies.Tunnel);
        Carousel.SizeChanged += (_, _) => LayoutCarousel(animate: false);
        Closed += (_, _) => _gamepad.Dispose();
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.Games.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() => LayoutCarousel(animate: false), DispatcherPriority.Loaded);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedGame):
                Dispatcher.UIThread.Post(() => LayoutCarousel(animate: true), DispatcherPriority.Loaded);
                break;
            case nameof(MainViewModel.View):
                Dispatcher.UIThread.Post(() =>
                {
                    LayoutCarousel(animate: false);
                    FocusSelected();
                }, DispatcherPriority.Loaded);
                break;
            case nameof(MainViewModel.IsDetailsOpen):
                Dispatcher.UIThread.Post(() =>
                {
                    if (Vm.IsDetailsOpen)
                        DetailsPlay.Focus(NavigationMethod.Directional);
                    else
                        FocusSelected();
                }, DispatcherPriority.Loaded);
                break;
            case nameof(MainViewModel.IsGameRunning):
                // Let the emulator have the controller; take it back (and the window) when the game closes.
                if (Vm.IsGameRunning)
                {
                    _gamepad.Suspend();
                }
                else
                {
                    _gamepad.Resume();
                    Activate();
                }
                break;
        }
    }

    // ---- Keyboard ----

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm.IsForgeOpen)
        {
            // Forge's own controls take every other key (typing nicknames, picking moves).
            if (e.Key == Key.Escape && FocusManager?.GetFocusedElement() is not ComboBox { IsDropDownOpen: true })
            {
                Vm.CloseForgeCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }
        if (!Vm.IsDetailsOpen)
            return;
        switch (e.Key)
        {
            case Key.Escape or Key.Back:
                Vm.CloseDetailsCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when FocusManager?.GetFocusedElement() is not Button:
                _ = Vm.LaunchAsync(Vm.DetailsGame);
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown:
                // Keep arrow keys from moving the selection in the list hidden underneath.
                if (FocusManager?.GetFocusedElement() is ListBoxItem)
                    e.Handled = true;
                break;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Vm.IsDetailsOpen || Vm.IsForgeOpen)
            return;

        var typing = SearchBox.IsFocused;
        switch (e.Key)
        {
            case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.F5:
                _ = Vm.RefreshAsync();
                e.Handled = true;
                break;
            case Key.V when !typing:
                Vm.ToggleViewCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Space or Key.I when !typing:
                Vm.OpenDetailsCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when typing:
                Vm.SearchText = "";
                FocusSelected();
                e.Handled = true;
                break;
            case Key.Down when typing && Vm.Games.Count > 0:
                Vm.SelectedGame ??= Vm.Games[0];
                FocusSelected();
                e.Handled = true;
                break;
            case Key.Enter when !typing || Vm.Games.Count == 1:
                _ = Vm.LaunchAsync(Vm.SelectedGame ?? Vm.Games.FirstOrDefault());
                e.Handled = true;
                break;
        }
    }

    // ---- Controller ----

    private void UpdateControllerHints()
    {
        var hints = Vm.Controller;
        hints.IsConnected = _gamepad.IsConnected;
        hints.Name = _gamepad.ControllerName ?? "";
        if (!_gamepad.IsConnected)
            return;
        hints.Confirm = _gamepad.LabelOf(PadButton.Confirm);
        hints.Back = _gamepad.LabelOf(PadButton.Back);
        hints.West = _gamepad.LabelOf(PadButton.West);
        hints.North = _gamepad.LabelOf(PadButton.North);
        Vm.Status = $"Controller connected: {hints.Name}";
    }

    private PadNavigator? _forgePad;

    private void OnPadPressed(PadButton button)
    {
        // Only drive Zest while it's the window in front (not behind a game, Settings, or another app).
        if (!IsActive || Vm.IsGameRunning)
            return;

        if (Vm.IsForgeOpen)
        {
            _forgePad ??= new PadNavigator(ForgePage);
            if (_forgePad.Handle(button, FocusManager?.GetFocusedElement()))
                return;
            switch (button)
            {
                case PadButton.West:
                    Vm.Forge?.StepTab(-1);
                    break;
                case PadButton.North:
                    Vm.Forge?.StepTab(1);
                    break;
                case PadButton.Back:
                    Vm.CloseForgeCommand.Execute(null);
                    break;
                case PadButton.PrevTab:
                    Vm.Forge?.ChangeBox(-1);
                    break;
                case PadButton.NextTab:
                    Vm.Forge?.ChangeBox(1);
                    break;
            }
            return;
        }

        if (Vm.IsDetailsOpen)
        {
            switch (button)
            {
                case PadButton.Confirm or PadButton.Start:
                    _ = Vm.LaunchAsync(Vm.DetailsGame);
                    break;
                case PadButton.Back or PadButton.North:
                    Vm.CloseDetailsCommand.Execute(null);
                    break;
                case PadButton.West:
                    Vm.ToggleHiddenCommand.Execute(Vm.DetailsGame);
                    break;
            }
            return;
        }

        switch (button)
        {
            case PadButton.Up: MoveSelection(0, -1); break;
            case PadButton.Down: MoveSelection(0, 1); break;
            case PadButton.Left: MoveSelection(-1, 0); break;
            case PadButton.Right: MoveSelection(1, 0); break;
            case PadButton.Confirm or PadButton.Start:
                _ = Vm.LaunchAsync(Vm.SelectedGame ?? Vm.Games.FirstOrDefault());
                break;
            case PadButton.North:
                Vm.OpenDetailsCommand.Execute(Vm.SelectedGame ?? Vm.Games.FirstOrDefault());
                break;
            case PadButton.West:
                Vm.ToggleViewCommand.Execute(null);
                break;
            case PadButton.Back when Vm.SearchText.Length > 0:
                Vm.SearchText = "";
                break;
            case PadButton.PrevTab: Vm.StepPlatform(-1); break;
            case PadButton.NextTab: Vm.StepPlatform(1); break;
            case PadButton.Select: Vm.CycleSortCommand.Execute(null); break;
        }
    }

    /// <summary>Moves the selection like a d-pad: rows and columns in the grid, left/right in the carousel.</summary>
    private void MoveSelection(int dx, int dy)
    {
        var games = Vm.Games;
        if (games.Count == 0)
            return;

        var index = Vm.SelectedGame is { } current ? games.IndexOf(current) : -1;
        if (index < 0)
        {
            index = 0;
        }
        else if (Vm.IsCarouselView)
        {
            if (dx == 0)
                return;
            index = Math.Clamp(index + dx, 0, games.Count - 1);
        }
        else
        {
            var next = index + dx + dy * GridColumns();
            // Down from a partial last row lands on the last game rather than doing nothing.
            index = dy > 0 && next >= games.Count && index / GridColumns() < (games.Count - 1) / GridColumns()
                ? games.Count - 1
                : next >= 0 && next < games.Count ? next : index;
        }

        Vm.SelectedGame = games[index];
        FocusSelected();
    }

    private int GridColumns()
    {
        if (GameGrid.ItemsPanelRoot is not { Children.Count: > 0 } panel)
            return 1;
        var firstRow = panel.Children[0].Bounds.Y;
        return Math.Max(1, panel.Children.TakeWhile(c => Math.Abs(c.Bounds.Y - firstRow) < 1).Count());
    }

    private void FocusSelected()
    {
        if (Vm.SelectedGame is not { } game)
            return;
        var list = Vm.IsCarouselView ? Carousel : GameGrid;
        var index = Vm.Games.IndexOf(game);
        if (index < 0)
            return;
        if (Vm.IsGridView)
            list.ScrollIntoView(index);
        list.ContainerFromIndex(index)?.Focus(NavigationMethod.Directional);
    }

    // ---- Carousel ----

    /// <summary>Pads the strip so the first and last covers can sit in the middle, then centers the selection.</summary>
    private void LayoutCarousel(bool animate)
    {
        if (!Vm.IsCarouselView || Carousel.ItemsPanelRoot is not { } panel
            || Carousel.FindDescendantOfType<ScrollViewer>() is not { } scroller)
            return;

        var cardWidth = panel.Children.Count > 0 ? panel.Children[0].Bounds.Width : 300;
        var side = Math.Max(0, (scroller.Viewport.Width - cardWidth) / 2);
        if (Math.Abs(panel.Margin.Left - side) > 0.5)
            panel.Margin = new Thickness(side, 0, side, 0);

        if (Vm.SelectedGame is not { } game || Carousel.ContainerFromItem(game) is not { } container)
            return;

        // With the side padding equal to (viewport - card) / 2, centering a card means scrolling to its X.
        _carouselTarget = container.Bounds.X;
        if (animate)
        {
            _carouselScroll.Start();
        }
        else
        {
            _carouselScroll.Stop();
            scroller.Offset = new Vector(_carouselTarget, 0);
        }
    }

    private void StepCarouselScroll()
    {
        if (Carousel.FindDescendantOfType<ScrollViewer>() is not { } scroller)
        {
            _carouselScroll.Stop();
            return;
        }
        var x = scroller.Offset.X;
        var next = x + (_carouselTarget - x) * 0.25;
        if (Math.Abs(_carouselTarget - next) < 0.5)
        {
            next = _carouselTarget;
            _carouselScroll.Stop();
        }
        scroller.Offset = new Vector(next, 0);
    }

    private void OnCarouselWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        // Touchpads send a stream of small deltas; one step per notch-ish.
        if (DateTime.UtcNow - _lastWheel < TimeSpan.FromMilliseconds(110))
            return;
        _lastWheel = DateTime.UtcNow;
        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? -e.Delta.X : -e.Delta.Y;
        if (delta != 0)
            MoveSelection(Math.Sign(delta), 0);
    }

    // ---- Mouse & menus ----

    private void OnGameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is GameViewModel game)
            _ = Vm.LaunchAsync(game);
    }

    private void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        Vm.SearchText = "";
        SearchBox.Focus();
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var vm = new SettingsViewModel(Vm.Config);
        var settings = new SettingsWindow { DataContext = vm };
        if (await settings.ShowDialog<bool>(this))
        {
            vm.ToConfig(Vm.Config).Save();
            await Vm.ReloadConfigAsync();
        }
        else
        {
            ThemeService.Apply(vm.OriginalTheme); // undo the live theme preview
        }
    }

    private static GameViewModel? MenuGame(object? sender) => (sender as MenuItem)?.DataContext as GameViewModel;

    private void OnMenuPlay(object? sender, RoutedEventArgs e) => _ = Vm.LaunchAsync(MenuGame(sender));

    private void OnMenuDetails(object? sender, RoutedEventArgs e) => Vm.OpenDetailsCommand.Execute(MenuGame(sender));

    private void OnMenuRefetchArt(object? sender, RoutedEventArgs e) => Vm.RefetchArtCommand.Execute(MenuGame(sender));

    private void OnMenuToggleHidden(object? sender, RoutedEventArgs e) => Vm.ToggleHiddenCommand.Execute(MenuGame(sender));

    private void OnMenuShowFile(object? sender, RoutedEventArgs e) => ShowFile(MenuGame(sender));

    private void OnDetailsShowFile(object? sender, RoutedEventArgs e) => ShowFile(Vm.DetailsGame);

    private static void ShowFile(GameViewModel? game)
    {
        var folder = game is null ? null : Path.GetDirectoryName(game.Game.Path);
        if (folder is not null)
            Process.Start(new ProcessStartInfo("xdg-open", [folder]) { UseShellExecute = false });
    }
}
