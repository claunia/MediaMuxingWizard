using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Resources;
using MMW.App.Services;
using MMW.Core.Diagnostics;
using MMW.Core.Metadata;

namespace MMW.App.ViewModels;

/// <summary>The application window: open documents, menus and global commands.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private static readonly string[] s_imageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif"];
    private static readonly string[] s_chapterExtensions = [".txt", ".csv"];

    /// <summary>Files that only contain tracks (no document of their own): dropping them imports into the open document.</summary>
    private static readonly string[] s_trackExtensions =
        [".srt", ".ass", ".ssa", ".vtt", ".aac", ".ac3", ".eac3", ".ec3", ".264", ".h264", ".265", ".h265", ".hevc", ".266", ".h266", ".vvc", ".evc", ".avs", ".cavs", ".avs2", ".avs3", ".dts", ".dtshd", ".flac", ".fla", ".ivf", ".obu", ".av1", .. MMW.Formats.MpegTs.TsFormat.Extensions, .. MMW.Formats.Ogg.OggFormat.Extensions,
         .. MMW.Media.Conversion.FFmpegDemuxerFactory.Extensions];

    private readonly DocumentService _documents;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;

    public MainWindowViewModel(DocumentService documents, IDialogService dialogs, ISettingsService settings, QueueViewModel? queue = null, MetadataService? metadata = null)
    {
        _documents = documents;
        _dialogs = dialogs;
        _settings = settings;
        Queue = queue;
        Metadata = metadata;
        if (queue is not null)
            queue.EditRequested += async (_, path) => await OpenPathsAsync([path]);
        RebuildRecentMenu();
    }

    /// <summary>Online metadata search (null in tests that do not need it).</summary>
    public MetadataService? Metadata { get; }

    [RelayCommand]
    private async Task ImportTracks()
    {
        if (SelectedDocument is null)
            return;
        var filter = new FileFilter(Strings.FileFilter_Importable, MMW.Media.Remux.MediaRemux.ImportExtensions.Select(e => e.TrimStart('.')).ToList());
        var files = await _dialogs.OpenFilesAsync(Strings.Dialog_ImportTracks_Title, [filter, FileFilters.All], allowMultiple: true);
        if (files.Count > 0)
            await ImportIntoSelectedAsync(files);
    }

    /// <summary>Shows the import dialog for <paramref name="files"/> and the current document.</summary>
    public async Task ImportIntoSelectedAsync(IReadOnlyList<string> files)
    {
        if (SelectedDocument is not { } doc)
            return;
        var dialog = new ImportDialogViewModel(doc, files, _dialogs, Settings);
        _ = dialog.LoadAsync();
        await _dialogs.ShowDialogAsync(dialog);
    }

    [RelayCommand]
    private async Task SearchMetadata()
    {
        if (Metadata is null || SelectedDocument is not { } doc)
            return;
        using var search = new MetadataSearchViewModel(doc, Metadata);
        await _dialogs.ShowDialogAsync(search);
    }

    /// <summary>The batch queue (null in tests that do not need it).</summary>
    public QueueViewModel? Queue { get; }

    /// <summary>Raised to ask the view to show the queue window.</summary>
    public event EventHandler? ShowQueueRequested;

    [RelayCommand]
    private void ShowQueue() => ShowQueueRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task SendToQueue()
    {
        if (Queue is null || SelectedDocument is not { Document.Path: { } path } doc)
            return;
        if (doc.IsDirty)
        {
            switch (await _dialogs.AskSaveChangesAsync(doc.Document.DisplayName))
            {
                case SaveChangesChoice.Save when !await doc.Save():
                case SaveChangesChoice.Cancel:
                    return;
            }
        }

        Queue.AddFiles([path]);
        ShowQueue();
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

    public static string AppTitle => Strings.App_Name;

    // ------------------------------------------------------------------ opening

    [RelayCommand]
    private async Task Open()
    {
        var files = await _dialogs.OpenFilesAsync(Strings.Dialog_Open_Title, [FileFilters.Media, FileFilters.Mp4, FileFilters.Matroska, FileFilters.All], allowMultiple: true);
        await OpenPathsAsync(files);
    }

    [RelayCommand]
    private Task OpenRecent(string path) => OpenPathsAsync([path]);

    /// <summary>Handles files opened from the command line, the open dialog or dropped on the window.</summary>
    public async Task OpenPathsAsync(IEnumerable<string> paths)
    {
        // A VobSub pair (.idx + .sub) is one track: import it once, through its .idx.
        var list = paths.ToList();
        list.RemoveAll(p => Path.GetExtension(p).Equals(".sub", StringComparison.OrdinalIgnoreCase) &&
                            MMW.Media.Conversion.FFmpegDemuxerFactory.VobSubIndex(p) is { } idx &&
                            list.Any(o => string.Equals(o, idx, StringComparison.OrdinalIgnoreCase)));
        foreach (var path in list)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (SelectedDocument is { } doc && s_imageExtensions.Contains(ext))
            {
                doc.MetadataInspector.AddArtworkData([await File.ReadAllBytesAsync(path)]);
                doc.SelectedRow = doc.Rows[0];
                continue;
            }

            if (SelectedDocument is { } nfoDoc && ext == ".nfo")
            {
                try
                {
                    nfoDoc.ImportNfo(path);
                }
                catch (System.Xml.XmlException ex)
                {
                    await _dialogs.ShowMessageAsync(Strings.Dialog_CouldNotImportNfo_Title, ex.Message);
                }

                continue;
            }

            if (SelectedDocument is not null && s_trackExtensions.Contains(ext))
            {
                await ImportIntoSelectedAsync([path]);
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
            await _dialogs.ShowMessageAsync(Strings.Dialog_CouldNotOpen_Title, $"{Path.GetFileName(path)}: {ex.Message}");
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
            RecentMenu.Add(new MenuNode(Strings.Menu_File_ClearRecent, ClearRecentCommand));
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
        if (await _dialogs.ShowDialogAsync(new PreferencesViewModel(Settings, Metadata)))
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
            Metadata?.SettingsChanged();
        }
    }

    [RelayCommand]
    private async Task About() =>
        await _dialogs.ShowMessageAsync(string.Format(CultureInfo.CurrentCulture, Strings.Dialog_About_TitleFormat, AppTitle),
            $"{AppTitle} {typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3)}\n\n" +
            Strings.About_Description + "\n\n© 2026 Natalia Portillo\n" +
            (MMW.Media.Conversion.MediaConversion.IsAvailable
                ? string.Format(CultureInfo.CurrentCulture, Strings.About_FFmpegFormat, MMW.Media.Conversion.MediaConversion.Version)
                : Strings.About_FFmpegMissing) + "\n" +
            (MMW.Ocr.SubtitleOcr.Factory.IsAvailable
                ? string.Format(CultureInfo.CurrentCulture, Strings.About_OcrFormat, MMW.Ocr.SubtitleOcr.Factory.Name)
                : Strings.About_OcrMissing) + "\n" +
            Strings.About_Icons);

    public static IReadOnlyList<string> RatingCountries => Ratings.Countries;

    public void PersistSettings() => _settings.Save();
}
