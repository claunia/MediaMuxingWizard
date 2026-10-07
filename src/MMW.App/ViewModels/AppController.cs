using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Resources;
using MMW.App.Services;

namespace MMW.App.ViewModels;

/// <summary>
/// The application around its document windows: one window per document (an empty window is the home screen),
/// the queue, recent files and the commands every window shares. The app ends with its last window, except that a
/// queue still processing keeps its window open, and on macOS, where an application runs without windows.
/// </summary>
public sealed partial class AppController : ObservableObject
{
    public AppController(DocumentService documents, IDialogService dialogs, ISettingsService settings, QueueViewModel? queue = null, MetadataService? metadata = null)
    {
        DocumentService = documents;
        Dialogs = dialogs;
        SettingsService = settings;
        Queue = queue;
        Metadata = metadata;
        if (queue is not null)
            queue.EditRequested += async (_, path) => await OpenPathsAsync([path]);
        RebuildRecentMenu();
    }

    internal DocumentService DocumentService { get; }

    internal IDialogService Dialogs { get; }

    internal ISettingsService SettingsService { get; }

    public AppSettings Settings => SettingsService.Settings;

    /// <summary>The batch queue (null in tests that do not need it).</summary>
    public QueueViewModel? Queue { get; }

    /// <summary>Online metadata search (null in tests that do not need it).</summary>
    public MetadataService? Metadata { get; }

    /// <summary>The open windows, in the order they were opened.</summary>
    public ObservableCollection<MainWindowViewModel> Windows { get; } = [];

    /// <summary>The window the user worked in last (files from the file manager that are not documents go there).</summary>
    public MainWindowViewModel? ActiveWindow { get; private set; }

    public ObservableCollection<MenuNode> RecentMenu { get; } = [];

    /// <summary>The document windows, for the Window menu.</summary>
    public ObservableCollection<MenuNode> WindowMenu { get; } = [];

    /// <summary>Creates the view of a new window; null in tests, which work with the view models alone.</summary>
    public Action<MainWindowViewModel>? ShowWindow { get; set; }

    /// <summary>Whether the queue window is open: like a document window, it keeps the application running.</summary>
    public bool IsQueueWindowOpen { get; set; }

    /// <summary>True while the queue processes items: the queue window then cannot be the last thing to go.</summary>
    public bool IsQueueBusy => Queue?.Runner.IsRunning == true;

    public event EventHandler? ShowQueueRequested;

    public event EventHandler? ShowLogRequested;

    /// <summary>Raised when the last window has closed and nothing keeps the application running.</summary>
    public event EventHandler? ShutdownRequested;

    // ------------------------------------------------------------------ windows

    /// <summary>Opens a new, empty window.</summary>
    public MainWindowViewModel NewWindow()
    {
        var window = new MainWindowViewModel(this);
        Register(window);
        ShowWindow?.Invoke(window);
        return window;
    }

    internal void Register(MainWindowViewModel window)
    {
        Windows.Add(window);
        ActiveWindow ??= window;
        window.PropertyChanged += OnWindowPropertyChanged;
        RebuildWindowMenu();
    }

    private void OnWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.Title))
            RebuildWindowMenu();
    }

    /// <summary>Called by a window when the user brings it to the front.</summary>
    public void WindowActivated(MainWindowViewModel window) => ActiveWindow = window;

    /// <summary>Called once a window has closed.</summary>
    public void WindowClosed(MainWindowViewModel window)
    {
        window.PropertyChanged -= OnWindowPropertyChanged;
        Windows.Remove(window);
        if (ReferenceEquals(ActiveWindow, window))
            ActiveWindow = Windows.LastOrDefault();
        RebuildWindowMenu();
        CheckShutdown();
    }

    /// <summary>
    /// Ends the application when its last window is gone: a queue still processing shows its window instead, so the
    /// work finishes in sight; an open queue window keeps the application running as a document window does.
    /// </summary>
    public void CheckShutdown()
    {
        if (Windows.Count > 0)
            return;
        if (IsQueueBusy && !IsQueueWindowOpen)
        {
            ShowQueueRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!IsQueueWindowOpen)
            ShutdownRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The window showing <paramref name="path"/>, if any.</summary>
    public MainWindowViewModel? FindWindow(string path) =>
        Windows.FirstOrDefault(w => w.Document is { } d && string.Equals(d.Document.Path, path, StringComparison.Ordinal));

    /// <summary>
    /// Opens files from outside a window (command line, file manager, later launches): documents fill an empty window
    /// or get their own; other files (artwork, tracks, chapters) go to the window the user worked in last.
    /// </summary>
    public async Task OpenPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;
        var target = Windows.FirstOrDefault(w => w.Document is null && !w.IsOpening) ?? ActiveWindow ?? NewWindow();
        await target.OpenPathsAsync(paths);
        target.RequestActivate();
    }

    /// <summary>Opens <paramref name="path"/> in a new window; the window closes again if the file cannot be opened.</summary>
    internal async Task OpenInNewWindowAsync(string path)
    {
        var window = NewWindow();
        if (!await window.LoadDocumentAsync(path))
            window.RequestClose();
    }

    private void RebuildWindowMenu()
    {
        WindowMenu.Clear();
        foreach (var window in Windows)
            WindowMenu.Add(new MenuNode(window.Title, ActivateWindowCommand, window));
    }

    [RelayCommand]
    private static void ActivateWindow(MainWindowViewModel? window) => window?.RequestActivate();

    /// <summary>Closes every window (each asks to save its changes); stops at the first the user keeps open.</summary>
    [RelayCommand]
    private async Task Quit()
    {
        foreach (var window in Windows.ToList())
        {
            if (!await window.ConfirmCloseAsync())
                return;
        }

        foreach (var window in Windows.ToList())
            window.RequestClose(confirmed: true);
        if (Windows.Count == 0)
            CheckShutdown();
    }

    [RelayCommand]
    private void ShowQueue() => ShowQueueRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ShowLog() => ShowLogRequested?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ recent files

    internal void AddRecent(string path)
    {
        Settings.AddRecent(path);
        SettingsService.Save();
        RebuildRecentMenu();
    }

    [RelayCommand]
    private Task OpenRecent(string path) => (ActiveWindow ?? NewWindow()).OpenPathsAsync([path]);

    private void RebuildRecentMenu()
    {
        RecentMenu.Clear();
        foreach (var path in Settings.RecentFiles)
            RecentMenu.Add(new MenuNode(path, OpenRecentCommand, path));
        if (RecentMenu.Count > 0)
        {
            RecentMenu.Add(MenuNode.Separator);
            RecentMenu.Add(new MenuNode(Strings.Menu_File_ClearRecent, ClearRecentCommand));
        }

        OnPropertyChanged(nameof(RecentFiles));
    }

    public IReadOnlyList<string> RecentFiles => Settings.RecentFiles;

    [RelayCommand]
    private void ClearRecent()
    {
        Settings.RecentFiles.Clear();
        SettingsService.Save();
        RebuildRecentMenu();
    }

    // ------------------------------------------------------------------ application commands

    [RelayCommand]
    private async Task Preferences()
    {
        if (await Dialogs.ShowDialogAsync(new PreferencesViewModel(Settings, Metadata)))
        {
            SettingsService.Save();
            if (Avalonia.Application.Current is { } app)
            {
                app.RequestedThemeVariant = Settings.Theme switch
                {
                    ThemeChoice.Light => Avalonia.Styling.ThemeVariant.Light,
                    ThemeChoice.Dark => Avalonia.Styling.ThemeVariant.Dark,
                    _ => Avalonia.Styling.ThemeVariant.Default,
                };
            }

            foreach (var window in Windows)
                window.Document?.MetadataInspector.SettingsChanged();
            Metadata?.SettingsChanged();
        }
    }

    [RelayCommand]
    private async Task About() =>
        await Dialogs.ShowMessageAsync(string.Format(CultureInfo.CurrentCulture, Strings.Dialog_About_TitleFormat, Strings.App_Name),
            $"{Strings.App_Name} {typeof(AppController).Assembly.GetName().Version?.ToString(3)}\n\n" +
            Strings.About_Description + "\n\n© 2026 Natalia Portillo\n" +
            (MMW.Media.Conversion.MediaConversion.IsAvailable
                ? string.Format(CultureInfo.CurrentCulture, Strings.About_FFmpegFormat, MMW.Media.Conversion.MediaConversion.Version)
                : Strings.About_FFmpegMissing) + "\n" +
            (MMW.Ocr.SubtitleOcr.Factory.IsAvailable
                ? string.Format(CultureInfo.CurrentCulture, Strings.About_OcrFormat, MMW.Ocr.SubtitleOcr.Factory.Name)
                : Strings.About_OcrMissing) + "\n" +
            Strings.About_Icons);
}
