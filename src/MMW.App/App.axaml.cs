using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MMW.App.Resources;
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
            MMW.Media.Remux.MediaRemux.EnsureRegistered();
            var settings = new SettingsService();
            ApplyUiCulture(settings.Settings.UiCulture);
            settings.Settings.ApplyConversionDefaults();
            RequestedThemeVariant = settings.Settings.Theme switch
            {
                ThemeChoice.Light => ThemeVariant.Light,
                ThemeChoice.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };

            var dialogs = new AvaloniaDialogService();
            QueueViewModel.RegisterActions();
            var runner = new MMW.Queue.QueueRunner(new MMW.Core.Model.ContainerRegistry(DocumentService.DefaultHandlers()), new PowerService());
            var queuePath = Path.Combine(SettingsService.AppDataDirectory, "queue.json");
            MMW.Queue.QueueStore.Load(runner, queuePath);
            var queue = new QueueViewModel(runner, dialogs, settings, new NotificationService(), queuePath);
            var metadata = new MetadataService(settings);
            runner.Services = new ServiceMap { metadata };
            var vm = new MainWindowViewModel(new DocumentService(), dialogs, settings, queue, metadata);
            desktop.MainWindow = new MainWindow { DataContext = vm };

            var window = desktop.MainWindow;
            var files = desktop.Args?.Where(File.Exists).ToList() ?? [];
            if (files.Count > 0)
                window.Opened += async (_, _) => await vm.OpenPathsAsync(files);

            // Files opened while running: from later launches (pipe) or from Finder (activation).
            async void OpenAndActivate(IReadOnlyList<string> paths)
            {
                await vm.OpenPathsAsync(paths);
                window.Activate();
            }

            if (OperatingSystem.IsMacOS())
            {
                if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                {
                    activatable.Activated += (_, e) =>
                    {
                        if (e is FileActivatedEventArgs fileArgs)
                            OpenAndActivate(fileArgs.Files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
                    };
                }
            }
            else
            {
                var cts = new CancellationTokenSource();
                desktop.Exit += (_, _) => cts.Cancel();
                SingleInstance.StartServer(paths => Dispatcher.UIThread.Post(() => OpenAndActivate(paths)), cts.Token);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Sets the user interface language (resource lookups) for the UI thread and threads started later; null or an
    /// unknown name keeps the system language. Formatting of numbers and dates (CurrentCulture) is not changed.
    /// </summary>
    public static void ApplyUiCulture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        try
        {
            var culture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException ex)
        {
            MMW.Core.Diagnostics.AppLog.Warn(string.Format(CultureInfo.CurrentCulture, Strings.Log_UnknownInterfaceLanguageFormat, name, ex.Message));
        }
    }
}
