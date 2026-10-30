using Avalonia.Controls;
using Avalonia.Interactivity;
using Zest.Services.Forge;
using Zest.ViewModels;

namespace Zest.Views;

public partial class ForgeView : UserControl
{
    public ForgeView() => InitializeComponent();

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PokemonSave save } && DataContext is ForgeViewModel vm)
        {
            vm.Tab = ForgeTab.Save;
            _ = vm.OpenAsync(save);
        }
    }
}
