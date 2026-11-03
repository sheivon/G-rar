using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GRAR.Services;
using GRAR.ViewModels;
using GRAR.Views;

namespace GRAR;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = SettingsService.Instance;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel(settings);
            var window = new MainWindow { DataContext = vm };
            vm.AttachDialogHost(window);

            desktop.MainWindow = window;
            desktop.Exit += (_, _) => settings.Save();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
