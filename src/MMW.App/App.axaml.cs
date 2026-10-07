using System.Globalization;
using Avalonia;
using Avalonia.Controls;
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
            var app = new AppController(new DocumentService(), dialogs, settings, queue, metadata);
            ConnectWindows(desktop, app, queue);

            // The app ends with its last window (AppController decides, counting the queue window); macOS apps keep
            // running without windows.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var first = app.NewWindow();
            desktop.MainWindow = desktop.Windows.Count > 0 ? desktop.Windows[0] : null;
            var files = desktop.Args?.Where(File.Exists).ToList() ?? [];
            if (files.Count > 0 && desktop.MainWindow is { } main)
                main.Opened += async (_, _) => await first.OpenPathsAsync(files);

            // Files opened while running: from later launches (pipe) or from Finder (activation).
            async void OpenAndActivate(IReadOnlyList<string> paths)
            {
                if (paths.Count == 0)
                {
                    // macOS Dock click with no window open: show the home screen.
                    if (app.Windows.Count == 0)
                        app.NewWindow();
                    return;
                }

                await app.OpenPathsAsync(paths);
            }

            if (OperatingSystem.IsMacOS())
            {
                if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                {
                    activatable.Activated += (_, e) =>
                    {
                        if (e is FileActivatedEventArgs fileArgs)
                            OpenAndActivate(fileArgs.Files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
                        else if (e.Kind == ActivationKind.Reopen)
                            OpenAndActivate([]);
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

    /// <summary>Creates the views of the windows the application asks for: documents, the queue and the log.</summary>
    private static void ConnectWindows(IClassicDesktopStyleApplicationLifetime desktop, AppController app, QueueViewModel queue)
    {
        app.ShowWindow = vm =>
        {
            var window = new MainWindow { DataContext = vm };
            window.Show();
        };

        // The queue and log windows belong to no document window, so closing a document leaves them open.
        QueueWindow? queueWindow = null;
        app.ShowQueueRequested += (_, _) =>
        {
            if (queueWindow is not null)
            {
                if (queueWindow.WindowState == WindowState.Minimized)
                    queueWindow.WindowState = WindowState.Normal;
                queueWindow.Activate();
                return;
            }

            queueWindow = new QueueWindow { DataContext = queue };
            queueWindow.Closing += (_, e) =>
            {
                // A queue still processing with no other window open would end with the application: keep it in sight.
                if (app.Windows.Count == 0 && app.IsQueueBusy)
                {
                    e.Cancel = true;
                    queueWindow.WindowState = WindowState.Minimized;
                }
            };
            queueWindow.Closed += (_, _) =>
            {
                queueWindow = null;
                app.IsQueueWindowOpen = false;
                app.CheckShutdown();
            };
            app.IsQueueWindowOpen = true;
            queueWindow.Show();
        };

        LogWindow? logWindow = null;
        app.ShowLogRequested += (_, _) =>
        {
            if (logWindow is not null)
            {
                logWindow.Activate();
                return;
            }

            logWindow = new LogWindow { DataContext = new LogViewModel() };
            logWindow.Closed += (_, _) => logWindow = null;
            logWindow.Show();
        };

        app.ShutdownRequested += (_, _) =>
        {
            if (!OperatingSystem.IsMacOS())
                desktop.Shutdown();
        };
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
