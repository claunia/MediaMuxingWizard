using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Resources;
using MMW.Core.Diagnostics;
using MMW.Core.Languages;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Media.Remux;

namespace MMW.App.ViewModels;

/// <summary>One importable track in the import dialog.</summary>
public sealed partial class ImportTrackViewModel : ViewModelBase
{
    public ImportTrackViewModel(ImportableTrack track)
    {
        Track = track;
        _selected = track.Selected;
        Actions = track.Choices;
        _selectedAction = track.Choice ?? Actions[0];
        _language = LanguageTable.Find(track.Language) ?? LanguageTable.All[0];
        _name = track.Name;
        if (track.RequiresFrameRate)
            _frameRate = FrameRates.FirstOrDefault(f => track.FrameRate is { } r && Math.Abs(f.Value - r) < 0.01) ?? FrameRates[1];
    }

    public ImportableTrack Track { get; }

    public string IdText => Track.TrackId.ToString(CultureInfo.InvariantCulture);

    public string Info => $"{Track.Format}, {Track.Details}";

    public string Duration => TrackRowViewModel.FormatDuration(Track.Duration);

    public TrackKind Kind => Track.Kind;

    public string? Problem => Track.Support.Level switch
    {
        TrackSupportLevel.NeedsConversion when Track.CanConvert => null,
        TrackSupportLevel.NeedsConversion or TrackSupportLevel.Unsupported => Track.Support.Reason,
        TrackSupportLevel.Passthrough => Track.Support.Reason, // a warning: e.g. HDR10+ that MP4 cannot keep
        _ => null,
    };

    public bool CanImport => Actions.Any(a => a.Action != ImportAction.Skip);

    /// <summary>True when the chosen action reads bitmap subtitles with OCR.</summary>
    public bool IsOcr => OcrAdvice.IsOcr(SelectedAction);

    public static string OcrWarning => OcrAdvice.Warning;

    public IReadOnlyList<ImportChoice> Actions { get; }

    public static IReadOnlyList<Choice<double>> FrameRates { get; } =
    [
        new(24000.0 / 1001, "23.976"), new(24, "24"), new(25, "25"), new(30000.0 / 1001, "29.97"),
        new(30, "30"), new(50, "50"), new(60000.0 / 1001, "59.94"), new(60, "60"),
    ];

    public bool RequiresFrameRate => Track.RequiresFrameRate;

    public static IReadOnlyList<Language> Languages => LanguageTable.All;

    [ObservableProperty]
    private bool _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOcr))]
    private ImportChoice _selectedAction;

    [ObservableProperty]
    private Language _language;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private Choice<double>? _frameRate;

    /// <summary>Writes the user's choices back to the importable track.</summary>
    public void Commit()
    {
        Track.Choice = SelectedAction;
        Track.Selected = Selected && SelectedAction.Action != ImportAction.Skip;
        Track.Language = Language.Tag;
        Track.Name = Name;
        if (FrameRate is { } f)
            Track.FrameRate = f.Value;
    }
}

/// <summary>The tracks of one source file.</summary>
public sealed class ImportFileViewModel(string path, IReadOnlyList<ImportTrackViewModel> tracks)
{
    public string Path { get; } = path;

    public string FileName { get; } = System.IO.Path.GetFileName(path);

    public IReadOnlyList<ImportTrackViewModel> Tracks { get; } = tracks;
}

/// <summary>Chooses which tracks of other files to add to a document (Subler's import sheet).</summary>
public sealed partial class ImportDialogViewModel : DialogViewModel<bool>
{
    private readonly DocumentViewModel _document;
    private readonly IReadOnlyList<string> _paths;

    private readonly Services.IDialogService? _dialogs;
    private readonly Services.AppSettings? _settings;

    public ImportDialogViewModel(DocumentViewModel document, IReadOnlyList<string> paths, Services.IDialogService? dialogs = null, Services.AppSettings? settings = null)
    {
        _document = document;
        _paths = paths;
        _dialogs = dialogs;
        _settings = settings;
    }

    public override string Title => Strings.Dialog_ImportTracks_Title;

    public ObservableCollection<ImportFileViewModel> Files { get; } = [];

    public IEnumerable<ImportTrackViewModel> AllTracks => Files.SelectMany(f => f.Tracks);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _status = Strings.Import_Reading;

    /// <summary>Also merge the tags of the source files (MP4/Matroska sources only).</summary>
    [ObservableProperty]
    private bool _importMetadata;

    public string Target => _document.Document.Container == ContainerKind.Matroska ? "Matroska" : "MPEG-4";

    /// <summary>Reads every source file; call once after construction.</summary>
    public async Task LoadAsync()
    {
        var errors = new List<string>();
        foreach (var path in _paths)
        {
            try
            {
                var tracks = await TrackImporter.InspectAsync(path, _document.Document.Container);
                Files.Add(new ImportFileViewModel(path, tracks.Select(t => new ImportTrackViewModel(t)).ToList()));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                AppLog.Error($"Cannot import from '{path}'", ex);
            }
        }

        IsLoading = false;
        var count = AllTracks.Count();
        Status = errors.Count > 0
            ? string.Join("\n", errors)
            : string.Format(CultureInfo.CurrentCulture, Strings.Import_TracksFoundFormat, count) + (MMW.Media.Conversion.MediaConversion.IsAvailable ? string.Empty : " " + Strings.Import_FFmpegMissing);
    }

    [RelayCommand]
    private void CheckAll()
    {
        foreach (var t in AllTracks.Where(t => t.CanImport))
            t.Selected = true;
    }

    [RelayCommand]
    private void UncheckAll()
    {
        foreach (var t in AllTracks)
            t.Selected = false;
    }

    /// <summary>Keeps only tracks in the given track's language checked.</summary>
    [RelayCommand]
    private void CheckSameLanguage(ImportTrackViewModel? reference)
    {
        if (reference is null)
            return;
        foreach (var t in AllTracks)
            t.Selected = t.CanImport && (t.Kind == TrackKind.Video || t.Language.Tag == reference.Language.Tag);
    }

    private bool CanImport() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task Import()
    {
        foreach (var t in AllTracks)
            t.Commit();
        var selected = AllTracks.Where(t => t.Track.Selected).Select(t => t.Track).ToList();

        // OCR needs the language models of the subtitle tracks.
        var ocr = selected.Where(t => t.Choice?.Ocr == true).ToList();
        if (ocr.Count > 0 && _dialogs is not null && _settings is not null)
        {
            if (!await OcrAdvice.EnsureModelsAsync(_dialogs, _settings, ocr.Select(t => t.Language)))
                return;
            foreach (var t in ocr)
                t.Ocr = OcrAdvice.Options(_settings);
        }

        using (_document.Undo.Transaction(Strings.Undo_ImportTracks))
        {
            if (selected.Count > 0)
                TrackImporter.AddToDocument(_document.Document, selected);
        }

        if (ImportMetadata)
        {
            foreach (var file in Files)
            {
                try
                {
                    // MP4 and Matroska documents, or the Vorbis comments and pictures of FLAC and Ogg files.
                    var metadata = ContainerKinds.FromPath(file.Path) != ContainerKind.Unknown
                        ? (await new ContainerRegistry(Services.DocumentService.DefaultHandlers()).OpenAsync(file.Path)).Metadata
                        : await Task.Run(() => TrackImporter.ReadMetadata(file.Path));
                    if (metadata is null)
                        continue;
                    _document.ApplyMetadata(Strings.Undo_ImportMetadata, doc => doc.Metadata.Merge(metadata, overwrite: false, replaceArtworks: false));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
                {
                    AppLog.Warn($"No metadata imported from {file.FileName}: {ex.Message}");
                }
            }
        }

        Close(true);
    }
}
