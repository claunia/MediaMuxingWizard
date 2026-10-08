using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Resources;
using MMW.App.Services;
using MMW.Core.Diagnostics;
using MMW.Core.Media;
using MMW.Core.Model;

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
    /// Handles files opened from this window (open dialog, or dropped on the home screen): documents fill this window
    /// when it is empty and get windows of their own otherwise; artwork, NFO, chapter and track files go into its
    /// document. Track files with no document to go into (a TS, raw streams, subtitles…) start a new MP4 or Matroska
    /// document, as the user chooses. The files are checked first: those of which nothing can be used are left out and
    /// named, and nothing is done when nothing at all can be used.
    /// </summary>
    public Task OpenPathsAsync(IEnumerable<string> paths) => OpenPathsAsync(paths, importDocuments: false);

    /// <summary>
    /// Files dropped on the window. On a document everything is imported into it: an MP4 or Matroska file offers its
    /// tracks like any other file and is never opened as a document of its own. On the home screen they are opened.
    /// </summary>
    public Task DropAsync(IEnumerable<string> paths) => OpenPathsAsync(paths, importDocuments: Document is not null);

    private async Task OpenPathsAsync(IEnumerable<string> paths, bool importDocuments)
    {
        // A VobSub pair (.idx + .sub) is one track: import it once, through its .idx.
        var list = paths.ToList();
        list.RemoveAll(p => Path.GetExtension(p).Equals(".sub", StringComparison.OrdinalIgnoreCase) &&
                            MMW.Media.Conversion.FFmpegDemuxerFactory.VobSubIndex(p) is { } idx &&
                            list.Any(o => string.Equals(o, idx, StringComparison.OrdinalIgnoreCase)));

        var documents = importDocuments ? [] : list.Where(DocumentService.IsSupported).ToList();
        var others = importDocuments ? list : list.Where(p => !DocumentService.IsSupported(p)).ToList();
        var problems = new List<string>();
        var tracks = new List<string>();
        var attachments = new List<string>();
        // Documents of the same drop come first, so the other files go into the document they came with.
        var willHaveDocument = Document is not null || (documents.Count > 0 && !IsOpening);
        foreach (var path in others)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (s_imageExtensions.Contains(ext) || ext == ".nfo" || s_chapterExtensions.Contains(ext))
            {
                if (willHaveDocument)
                    attachments.Add(path);
                else
                    problems.Add(string.Format(CultureInfo.CurrentCulture, Strings.Drop_NeedsDocumentFormat, Path.GetFileName(path)));
            }
            else if (IsTrackFile(ext))
            {
                tracks.Add(path);
            }
            else
            {
                problems.Add(string.Format(CultureInfo.CurrentCulture, Strings.Drop_UnsupportedFileFormat, Path.GetFileName(path)));
            }
        }

        // Track files go into this window's document, the first document of the drop, or a new one of the chosen kind.
        ContainerKind? newDocument = null;
        var target = Document?.Document.Container;
        if (tracks.Count > 0 && target is null)
        {
            if (documents.Count > 0 && !IsOpening)
            {
                target = ContainerKinds.FromPath(documents[0]);
            }
            else
            {
                newDocument = await AskNewDocumentKindAsync(tracks);
                if (newDocument is null)
                    return;
                target = newDocument;
            }
        }

        if (tracks.Count > 0 && target is { } kind)
        {
            var (usable, unusable) = await SortTrackFilesAsync(tracks, kind);
            tracks = usable;
            problems.AddRange(unusable);
        }

        // Only a drop of which nothing can be used is rejected; otherwise the unusable files are left out and named.
        if (problems.Count > 0)
        {
            var nothingUsable = documents.Count == 0 && attachments.Count == 0 && tracks.Count == 0;
            await Dialogs.ShowMessageAsync(nothingUsable ? Strings.Drop_Rejected_Title : Strings.Drop_LeftOut_Title,
                string.Format(CultureInfo.CurrentCulture, nothingUsable ? Strings.Drop_Rejected_MessageFormat : Strings.Drop_LeftOut_MessageFormat,
                    string.Join("\n", problems)));
            if (nothingUsable)
                return;
        }

        foreach (var path in documents)
        {
            if (App.FindWindow(path) is { } open)
                open.RequestActivate();
            else if (Document is null && !IsOpening)
                await LoadDocumentAsync(path);
            else
                await App.OpenInNewWindowAsync(path);
        }

        foreach (var path in attachments)
            await AttachAsync(path);

        if (newDocument is { } kindForNew && tracks.Count > 0)
            await NewDocumentForTracksAsync(tracks, kindForNew);
        else if (tracks.Count > 0)
            await ImportIntoSelectedAsync(tracks);
    }

    /// <summary>Artwork, an NFO or a chapter file for this window's document.</summary>
    private async Task AttachAsync(string path)
    {
        if (Document is not { } doc)
            return;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (s_imageExtensions.Contains(ext))
        {
            doc.MetadataInspector.AddArtworkData([await File.ReadAllBytesAsync(path)]);
            doc.SelectedRow = doc.Rows[0];
        }
        else if (ext == ".nfo")
        {
            try
            {
                doc.ImportNfo(path);
            }
            catch (System.Xml.XmlException ex)
            {
                await Dialogs.ShowMessageAsync(Strings.Dialog_CouldNotImportNfo_Title, ex.Message);
            }
        }
        else
        {
            await doc.ImportChaptersAsync(path);
        }
    }

    /// <summary>
    /// Splits track files into those with at least one track <paramref name="target"/> can store (as it is or converted;
    /// the import dialog shows the others as not available) and those of which nothing can be used, described one
    /// line each: unreadable, without tracks, or with only tracks the container cannot store even converted.
    /// </summary>
    private static async Task<(List<string> Usable, List<string> Unusable)> SortTrackFilesAsync(IReadOnlyList<string> files, ContainerKind target)
    {
        var usable = new List<string>();
        var unusable = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            try
            {
                var inspected = await MMW.Media.Remux.TrackImporter.InspectAsync(file, target);
                if (inspected.Count == 0)
                {
                    unusable.Add(string.Format(CultureInfo.CurrentCulture, Strings.Drop_NoTracksFormat, name));
                }
                else if (inspected.Any(t => t.Choices.Any(c => c.Action != ImportAction.Skip)))
                {
                    usable.Add(file);
                }
                else
                {
                    foreach (var track in inspected)
                    {
                        unusable.Add(track.Support.Reason is { } reason
                            ? string.Format(CultureInfo.CurrentCulture, Strings.Drop_TrackReasonFormat, name, track.TrackId, track.Format, reason)
                            : string.Format(CultureInfo.CurrentCulture, Strings.Drop_TrackFormat, name, track.TrackId, track.Format));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or EndOfStreamException)
            {
                unusable.Add(string.Format(CultureInfo.CurrentCulture, Strings.Drop_UnreadableFormat, name, ex.Message));
            }
        }

        return (usable, unusable);
    }

    private static bool IsTrackFile(string extension) =>
        s_trackExtensions.Contains(extension) || MMW.Media.Remux.MediaRemux.ImportExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>Asks whether track files with no document to go into become an MP4 or a Matroska file; null when cancelled.</summary>
    private async Task<ContainerKind?> AskNewDocumentKindAsync(IReadOnlyList<string> files)
    {
        string mp4 = Strings.Button_Mp4, matroska = Strings.Button_Matroska;
        var names = string.Join("\n", files.Select(f => "• " + Path.GetFileName(f)));
        var answer = await Dialogs.ShowDialogAsync(new MessageDialogViewModel(Strings.Dialog_NewDocument_Title,
            string.Format(CultureInfo.CurrentCulture, Strings.Dialog_NewDocument_MessageFormat, names),
            [mp4, matroska, Strings.Button_Cancel], Settings.NewDocumentFormat == "mkv" ? matroska : mp4));
        if (answer != mp4 && answer != matroska)
            return null;
        Settings.NewDocumentFormat = answer == matroska ? "mkv" : "mp4";
        App.SettingsService.Save();
        return answer == matroska ? ContainerKind.Matroska : ContainerKind.Mp4;
    }

    /// <summary>
    /// Shows the import dialog for a new, untitled document of <paramref name="kind"/> holding <paramref name="files"/>;
    /// nothing is left behind when the user cancels.
    /// </summary>
    private async Task NewDocumentForTracksAsync(IReadOnlyList<string> files, ContainerKind kind)
    {
        var window = NewDocument(kind);
        await window.ImportIntoSelectedAsync(files);
        if (window.Document is { } doc && doc.Document.Tracks.Count == 0 && !doc.IsDirty)
        {
            // The import was cancelled: nothing to keep.
            if (ReferenceEquals(window, this))
                Document = null;
            else
                window.RequestClose(confirmed: true);
        }
    }

    [RelayCommand]
    private void NewDocument(string format) => NewDocument(format == "mkv" ? ContainerKind.Matroska : ContainerKind.Mp4);

    /// <summary>A new, untitled document of <paramref name="kind"/>: in this window when it is empty, otherwise in a new one.</summary>
    public MainWindowViewModel NewDocument(ContainerKind kind)
    {
        var window = Document is null && !IsOpening ? this : App.NewWindow();
        window.Show(new MediaDocument(null, kind));
        window.RequestActivate();
        return window;
    }

    /// <summary>Opens <paramref name="path"/> as this window's document; false (after telling the user) when it cannot be read.</summary>
    internal async Task<bool> LoadDocumentAsync(string path)
    {
        IsOpening = true;
        try
        {
            Show(await App.DocumentService.OpenAsync(path));
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

    private void Show(MediaDocument document)
    {
        Document = new DocumentViewModel(document, App.DocumentService, Dialogs, App.SettingsService);
        Document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentViewModel.Title))
                OnPropertyChanged(nameof(Title));
        };
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
            await ImportFilesAsync(files);
    }

    /// <summary>
    /// Imports track files into this window's document, after checking them: when one cannot be read or holds a
    /// track the document's container cannot store even converted, the user is told and nothing is imported.
    /// </summary>
    public async Task<bool> ImportFilesAsync(IReadOnlyList<string> files)
    {
        if (Document is not { } doc)
            return false;
        var (usable, unusable) = await SortTrackFilesAsync(files, doc.Document.Container);
        if (unusable.Count > 0)
        {
            await Dialogs.ShowMessageAsync(usable.Count == 0 ? Strings.Drop_Rejected_Title : Strings.Drop_LeftOut_Title,
                string.Format(CultureInfo.CurrentCulture, usable.Count == 0 ? Strings.Drop_Rejected_MessageFormat : Strings.Drop_LeftOut_MessageFormat,
                    string.Join("\n", unusable)));
            if (usable.Count == 0)
                return false;
        }

        await ImportIntoSelectedAsync(usable);
        return true;
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

        // The queue has the file now: like Subler, the document is done with.
        CloseSettled();
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
        CloseSettled();
    }

    /// <summary>Closes a document whose changes are settled: its window goes, or the last one returns to the home screen.</summary>
    private void CloseSettled()
    {
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
