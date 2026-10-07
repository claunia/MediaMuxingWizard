using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Resources;
using MMW.App.Services;
using MMW.Core.Diagnostics;

namespace MMW.App.ViewModels;

/// <summary>A document window: one document (none on the home screen), its menus and the files dropped on it.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private static readonly string[] s_imageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif"];
    private static readonly string[] s_chapterExtensions = [".txt", ".csv"];

    /// <summary>Files that only contain tracks (no document of their own): dropping them imports into the open document.</summary>
    private static readonly string[] s_trackExtensions =
        [".srt", ".ass", ".ssa", ".vtt", ".aac", ".ac3", ".eac3", ".ec3", ".264", ".h264", ".265", ".h265", ".hevc", ".266", ".h266", ".vvc", ".evc", ".avs", ".cavs", ".avs2", ".avs3", ".dts", ".dtshd", ".flac", ".fla", ".ivf", ".obu", ".av1", .. MMW.Formats.MpegTs.TsFormat.Extensions, .. MMW.Formats.Ogg.OggFormat.Extensions,
         .. MMW.Media.Conversion.FFmpegDemuxerFactory.Extensions];

    /// <summary>A window of its own application (tests and tools that work with one window).</summary>
    public MainWindowViewModel(DocumentService documents, IDialogService dialogs, ISettingsService settings, QueueViewModel? queue = null, MetadataService? metadata = null)
    {
        App = new AppController(documents, dialogs, settings, queue, metadata);
        App.Register(this);
    }

    internal MainWindowViewModel(AppController app) => App = app;

    /// <summary>The application this window belongs to.</summary>
    public AppController App { get; }

    private IDialogService Dialogs => App.Dialogs;

    public MetadataService? Metadata => App.Metadata;

    public QueueViewModel? Queue => App.Queue;

    public AppSettings Settings => App.Settings;

    /// <summary>The document this window shows; null on the home screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument), nameof(HasNoDocuments), nameof(Title))]
    private DocumentViewModel? _document;

    public bool HasDocument => Document is not null;

    public bool HasNoDocuments => Document is null;

    /// <summary>The window title: the document's name, or the application's on the home screen.</summary>
    public string Title => Document is { } doc ? string.Format(CultureInfo.CurrentCulture, Strings.Window_TitleFormat, doc.Title) : Strings.App_Name;

    [ObservableProperty]
    private bool _isOpening;

    public ObservableCollection<MenuNode> RecentMenu => App.RecentMenu;

    public ObservableCollection<MenuNode> WindowMenu => App.WindowMenu;

    public IReadOnlyList<string> RecentFiles => App.RecentFiles;

    public static string AppTitle => Strings.App_Name;

    /// <summary>Asks the view to bring this window to the front.</summary>
    public event EventHandler? ActivateRequested;

    /// <summary>Asks the view to close this window; the argument tells whether unsaved changes were already settled.</summary>
    public event EventHandler<bool>? CloseRequested;

    public void RequestActivate() => ActivateRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Closes the window (without a view, as in tests, it simply leaves the application).</summary>
    public void RequestClose(bool confirmed = false)
    {
        if (CloseRequested is null)
            App.WindowClosed(this);
        else
            CloseRequested.Invoke(this, confirmed);
    }

    // ------------------------------------------------------------------ opening

    [RelayCommand]
    private async Task Open()
    {
        var files = await Dialogs.OpenFilesAsync(Strings.Dialog_Open_Title, [FileFilters.Media, FileFilters.Mp4, FileFilters.Matroska, FileFilters.All], allowMultiple: true);
        await OpenPathsAsync(files);
    }

    [RelayCommand]
    private Task OpenRecent(string path) => OpenPathsAsync([path]);

    /// <summary>
    /// Handles files opened from this window (open dialog, drop): artwork, NFO, chapter and track files go into its
    /// document; documents fill this window when it is empty and get windows of their own otherwise.
    /// </summary>
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
            if (Document is { } doc && s_imageExtensions.Contains(ext))
            {
                doc.MetadataInspector.AddArtworkData([await File.ReadAllBytesAsync(path)]);
                doc.SelectedRow = doc.Rows[0];
                continue;
            }

            if (Document is { } nfoDoc && ext == ".nfo")
            {
                try
                {
                    nfoDoc.ImportNfo(path);
                }
                catch (System.Xml.XmlException ex)
                {
                    await Dialogs.ShowMessageAsync(Strings.Dialog_CouldNotImportNfo_Title, ex.Message);
                }

                continue;
            }

            if (Document is not null && s_trackExtensions.Contains(ext))
            {
                await ImportIntoSelectedAsync([path]);
                continue;
            }

            if (Document is { } chapterDoc && s_chapterExtensions.Contains(ext))
            {
                await chapterDoc.ImportChaptersAsync(path);
                continue;
            }

            if (App.FindWindow(path) is { } open)
                open.RequestActivate();
            else if (Document is null && !IsOpening)
                await LoadDocumentAsync(path);
            else
                await App.OpenInNewWindowAsync(path);
        }
    }

    /// <summary>Opens <paramref name="path"/> as this window's document; false (after telling the user) when it cannot be read.</summary>
    internal async Task<bool> LoadDocumentAsync(string path)
    {
        IsOpening = true;
        try
        {
            var document = await App.DocumentService.OpenAsync(path);
            Document = new DocumentViewModel(document, App.DocumentService, Dialogs, App.SettingsService);
            Document.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DocumentViewModel.Title))
                    OnPropertyChanged(nameof(Title));
            };
            App.AddRecent(path);
            AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.Log_OpenedFormat, path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or EndOfStreamException)
        {
            AppLog.Error(string.Format(CultureInfo.CurrentCulture, Strings.Log_CouldNotOpenFormat, path), ex);
            await Dialogs.ShowMessageAsync(Strings.Dialog_CouldNotOpen_Title, $"{Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
        finally
        {
            IsOpening = false;
        }
    }

    // ------------------------------------------------------------------ document commands

    [RelayCommand]
    private async Task ImportTracks()
    {
        if (Document is null)
            return;
        var filter = new FileFilter(Strings.FileFilter_Importable, MMW.Media.Remux.MediaRemux.ImportExtensions.Select(e => e.TrimStart('.')).ToList());
        var files = await Dialogs.OpenFilesAsync(Strings.Dialog_ImportTracks_Title, [filter, FileFilters.All], allowMultiple: true);
        if (files.Count > 0)
            await ImportIntoSelectedAsync(files);
    }

    /// <summary>Shows the import dialog for <paramref name="files"/> and this window's document.</summary>
    public async Task ImportIntoSelectedAsync(IReadOnlyList<string> files)
    {
        if (Document is not { } doc)
            return;
        var dialog = new ImportDialogViewModel(doc, files, Dialogs, Settings);
        _ = dialog.LoadAsync();
        await Dialogs.ShowDialogAsync(dialog);
    }

    [RelayCommand]
    private async Task SearchMetadata()
    {
        if (Metadata is null || Document is not { } doc)
            return;
        using var search = new MetadataSearchViewModel(doc, Metadata);
        await Dialogs.ShowDialogAsync(search);
    }

    [RelayCommand]
    private void ShowQueue() => App.ShowQueueCommand.Execute(null);

    [RelayCommand]
    private async Task SendToQueue()
    {
        if (Queue is null || Document is not { Document.Path: { } path } doc)
            return;
        if (doc.IsDirty)
        {
            switch (await Dialogs.AskSaveChangesAsync(doc.Document.DisplayName))
            {
                case SaveChangesChoice.Save when !await doc.Save():
                case SaveChangesChoice.Cancel:
                    return;
            }
        }

        Queue.AddFiles([path]);
        ShowQueue();
    }

    [RelayCommand]
    private void ApplyPresetShortcut(string index)
    {
        if (Document is { } doc && int.TryParse(index, out var i))
            doc.MetadataInspector.ApplyPresetAt(i - 1);
    }

    // ------------------------------------------------------------------ closing

    /// <summary>
    /// File › Close: closes the document. Another window being open, this window goes; the last window stays as the
    /// home screen instead, so closing a file does not end the application.
    /// </summary>
    [RelayCommand]
    private async Task CloseDocument()
    {
        if (Document is null || !await ConfirmCloseAsync())
            return;
        if (App.Windows.Count > 1)
            RequestClose(confirmed: true);
        else
            Document = null;
    }

    /// <summary>Asks to save a modified document; false when the user cancels.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (Document is not { IsDirty: true } document)
            return true;
        RequestActivate();
        return await Dialogs.AskSaveChangesAsync(document.Document.DisplayName) switch
        {
            SaveChangesChoice.Save => await document.Save(),
            SaveChangesChoice.Discard => true,
            _ => false,
        };
    }

    public void PersistSettings() => App.SettingsService.Save();
}
