using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Zest.Services;
using Zest.ViewModels;
using Zest.Views;

namespace Zest;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var config = AppConfig.Load();
            ThemeService.Apply(config.Theme);
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(config),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
