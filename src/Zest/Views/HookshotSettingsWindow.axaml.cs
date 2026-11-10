using Avalonia.Controls;
using Zest.ViewModels;

namespace Zest.Views;

/// <summary>Hookshot's settings (hookshot_options.ini) and the NVIDIA DLSS opt-in: a window of its own, one at a time.</summary>
public partial class HookshotSettingsWindow : Window
{
    private static HookshotSettingsWindow? _open;

    public HookshotSettingsWindow()
    {
        InitializeComponent();
    }

    public static void Show(HookshotSettingsViewModel vm)
    {
        if (_open is null)
        {
            _open = new HookshotSettingsWindow { DataContext = vm };
            _open.Closed += (_, _) => _open = null;
            _open.Show();
            _ = vm.LoadAsync();
        }
        _open.Activate();
    }
}
