using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Services;
using MMW.Core.Diagnostics;
using MMW.Core.Metadata;

namespace MMW.App.ViewModels;

/// <summary>The application window: open documents, menus and global commands.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private static readonly string[] s_imageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif"];
    private static readonly string[] s_chapterExtensions = [".txt", ".csv"];

    private readonly DocumentService _documents;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;

    public MainWindowViewModel(DocumentService documents, IDialogService dialogs, ISettingsService settings)
    {
        _documents = documents;
        _dialogs = dialogs;
        _settings = settings;
        RebuildRecentMenu();
    }

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    public ObservableCollection<MenuNode> RecentMenu { get; } = [];

    public AppSettings Settings => _settings.Settings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    private DocumentViewModel? _selectedDocument;

    public bool HasDocument => SelectedDocument is not null;

    public bool HasNoDocuments => Documents.Count == 0;

    [ObservableProperty]
    private bool _isOpening;

    public IReadOnlyList<string> RecentFiles => Settings.RecentFiles;

    public static string AppTitle => "Media Metadata Wizard";

    // ------------------------------------------------------------------ opening

    [RelayCommand]
    private async Task Open()
    {
        var files = await _dialogs.OpenFilesAsync("Open", [FileFilters.Media, FileFilters.Mp4, FileFilters.Matroska, FileFilters.All], allowMultiple: true);
        await OpenPathsAsync(files);
    }

    [RelayCommand]
    private Task OpenRecent(string path) => OpenPathsAsync([path]);

    /// <summary>Handles files opened from the command line, the open dialog or dropped on the window.</summary>
    public async Task OpenPathsAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (SelectedDocument is { } doc && s_imageExtensions.Contains(ext))
            {
                doc.MetadataInspector.AddArtworkData([await File.ReadAllBytesAsync(path)]);
                doc.SelectedRow = doc.Rows[0];
                continue;
            }

            if (SelectedDocument is { } chapterDoc && s_chapterExtensions.Contains(ext))
            {
                await chapterDoc.ImportChaptersAsync(path);
                continue;
            }

            await OpenDocumentAsync(path);
        }
    }

    private async Task OpenDocumentAsync(string path)
    {
        var existing = Documents.FirstOrDefault(d => string.Equals(d.Document.Path, path, StringComparison.Ordinal));
        if (existing is not null)
        {
            SelectedDocument = existing;
            return;
        }

        IsOpening = true;
        try
        {
            var document = await _documents.OpenAsync(path);
            var vm = new DocumentViewModel(document, _documents, _dialogs, _settings);
            Documents.Add(vm);
            SelectedDocument = vm;
            OnPropertyChanged(nameof(HasNoDocuments));
            Settings.AddRecent(path);
            _settings.Save();
            RebuildRecentMenu();
            AppLog.Info($"Opened '{path}'.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or EndOfStreamException)
        {
            AppLog.Error($"Could not open '{path}'", ex);
            await _dialogs.ShowMessageAsync("Could not open file", $"{Path.GetFileName(path)}: {ex.Message}");
        }
        finally
        {
            IsOpening = false;
        }
    }

    private void RebuildRecentMenu()
    {
        RecentMenu.Clear();
        foreach (var path in Settings.RecentFiles)
            RecentMenu.Add(new MenuNode(path, OpenRecentCommand, path));
        if (RecentMenu.Count > 0)
        {
            RecentMenu.Add(MenuNode.Separator);
            RecentMenu.Add(new MenuNode("Clear Menu", ClearRecentCommand));
        }

        OnPropertyChanged(nameof(RecentFiles));
    }

    [RelayCommand]
    private void ClearRecent()
    {
        Settings.RecentFiles.Clear();
        _settings.Save();
        RebuildRecentMenu();
    }

    // ------------------------------------------------------------------ closing

    [RelayCommand]
    private async Task CloseDocument(DocumentViewModel? document)
    {
        document ??= SelectedDocument;
        if (document is null || !await ConfirmCloseAsync(document))
            return;
        var index = Documents.IndexOf(document);
        Documents.Remove(document);
        SelectedDocument = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
        OnPropertyChanged(nameof(HasNoDocuments));
    }

    /// <summary>Asks to save a modified document; false when the user cancels.</summary>
    public async Task<bool> ConfirmCloseAsync(DocumentViewModel document)
    {
        if (!document.IsDirty)
            return true;
        SelectedDocument = document;
        return await _dialogs.AskSaveChangesAsync(document.Document.DisplayName) switch
        {
            SaveChangesChoice.Save => await document.Save(),
            SaveChangesChoice.Discard => true,
            _ => false,
        };
    }

    /// <summary>Called before the window closes; false keeps the window open.</summary>
    public async Task<bool> ConfirmExitAsync()
    {
        foreach (var doc in Documents.ToList())
        {
            if (!await ConfirmCloseAsync(doc))
                return false;
        }

        return true;
    }

    // ------------------------------------------------------------------ misc

    [RelayCommand]
    private void ApplyPresetShortcut(string index)
    {
        if (SelectedDocument is { } doc && int.TryParse(index, out var i))
            doc.MetadataInspector.ApplyPresetAt(i - 1);
    }

    [RelayCommand]
    private async Task Preferences()
    {
        if (await _dialogs.ShowDialogAsync(new PreferencesViewModel(Settings)))
        {
            _settings.Save();
            if (Avalonia.Application.Current is { } app)
            {
                app.RequestedThemeVariant = Settings.Theme switch
                {
                    ThemeChoice.Light => Avalonia.Styling.ThemeVariant.Light,
                    ThemeChoice.Dark => Avalonia.Styling.ThemeVariant.Dark,
                    _ => Avalonia.Styling.ThemeVariant.Default,
                };
            }

            foreach (var doc in Documents)
                doc.MetadataInspector.SettingsChanged();
        }
    }

    [RelayCommand]
    private async Task About() =>
        await _dialogs.ShowMessageAsync("About " + AppTitle,
            $"{AppTitle} {typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3)}\n\n" +
            "Edit metadata, chapters and tracks of MP4 and Matroska files.\n\n© 2026 Natalia Portillo\n" +
            "Icons: Material Design Icons (Apache 2.0).");

    public static IReadOnlyList<string> RatingCountries => Ratings.Countries;

    public void PersistSettings() => _settings.Save();
}
