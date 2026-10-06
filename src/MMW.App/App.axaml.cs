using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MMW.App.Services;
using MMW.App.ViewModels;
using MMW.App.Views;

namespace MMW.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new SettingsService();
            RequestedThemeVariant = settings.Settings.Theme switch
            {
                ThemeChoice.Light => ThemeVariant.Light,
                ThemeChoice.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };

            var vm = new MainWindowViewModel(new DocumentService(), new AvaloniaDialogService(), settings);
            desktop.MainWindow = new MainWindow { DataContext = vm };

            var files = desktop.Args?.Where(File.Exists).ToList() ?? [];
            if (files.Count > 0)
                desktop.MainWindow.Opened += async (_, _) => await vm.OpenPathsAsync(files);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
