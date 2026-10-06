using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Services;
using MMW.Core.Languages;
using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.Queue;

namespace MMW.App.ViewModels;

/// <summary>The batch queue window: items, options and the default actions given to new items.</summary>
public sealed partial class QueueViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly string _storePath;
    private bool _loading;

    public QueueViewModel(QueueRunner runner, IDialogService dialogs, ISettingsService settings, INotificationService notifications, string storePath)
    {
        Runner = runner;
        _dialogs = dialogs;
        _settings = settings;
        _notifications = notifications;
        _storePath = storePath;

        _loading = true;
        LoadOptions(runner.Options);
        _loading = false;

        runner.Items.CollectionChanged += (_, _) =>
        {
            Persist();
            OnPropertyChanged(nameof(Summary));
            StartCommand.NotifyCanExecuteChanged();
        };
        runner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueueRunner.IsRunning))
            {
                StartCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(Summary));
            }
        };
        runner.RunFinished += (_, r) =>
        {
            Persist();
            OnPropertyChanged(nameof(Summary));
            if (NotifyWhenDone && r.Completed + r.Failed > 0)
                _notifications.Notify("Queue finished", $"{r.Completed} completed, {r.Failed} failed.");
        };
    }

    public QueueRunner Runner { get; }

    public ObservableCollection<QueueItem> Items => Runner.Items;

    /// <summary>Raised when the user asks to edit an item in the main window.</summary>
    public event EventHandler<string>? EditRequested;

    public string Summary
    {
        get
        {
            var ready = Items.Count(i => i.Status == QueueItemStatus.Ready);
            var done = Items.Count(i => i.Status == QueueItemStatus.Completed);
            var failed = Items.Count(i => i.Status == QueueItemStatus.Failed);
            return (Runner.IsRunning ? "Running · " : string.Empty) + $"{Items.Count} items · {ready} ready · {done} done · {failed} failed";
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(RetryCommand), nameof(RevealCommand), nameof(EditCommand))]
    private QueueItem? _selectedItem;

    // ------------------------------------------------------------------ options

    public static IReadOnlyList<Choice<string?>> FileTypes { get; } =
        [new(null, "Same as source"), new(".m4v", "MPEG-4 Video (.m4v)"), new(".mp4", "MPEG-4 (.mp4)"), new(".mkv", "Matroska (.mkv)")];

    public static IReadOnlyList<Language> Languages => LanguageTable.All;

    public static IReadOnlyList<ColorPreset> ColorPresets => ColorPreset.All;

    public IReadOnlyList<MetadataPreset> Presets => _settings.Settings.Presets;

    [ObservableProperty]
    private bool _useFolder;

    [ObservableProperty]
    private string? _folder;

    [ObservableProperty]
    private Choice<string?> _fileType = FileTypes[0];

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private bool _notifyWhenDone = true;

    [ObservableProperty]
    private bool _optimize;

    // Default actions
    [ObservableProperty]
    private bool _fetchMetadata;

    public static IReadOnlyList<Choice<MMW.Metadata.Search.ArtworkKind?>> ArtworkKinds { get; } =
    [
        new(MMW.Metadata.Search.ArtworkKind.Poster, "Poster"),
        new(MMW.Metadata.Search.ArtworkKind.Season, "Season"),
        new(MMW.Metadata.Search.ArtworkKind.Episode, "Episode"),
        new(MMW.Metadata.Search.ArtworkKind.Backdrop, "Backdrop"),
        new(MMW.Metadata.Search.ArtworkKind.Square, "Square"),
        new(null, "No artwork"),
    ];

    [ObservableProperty]
    private Choice<MMW.Metadata.Search.ArtworkKind?> _fetchArtwork = ArtworkKinds[0];

    [ObservableProperty]
    private MetadataPreset? _applyPreset;

    [ObservableProperty]
    private bool _applyPresetEnabled;

    [ObservableProperty]
    private bool _clearMetadata;

    [ObservableProperty]
    private bool _setOutputFileName;

    [ObservableProperty]
    private bool _loadChapters;

    [ObservableProperty]
    private bool _organizeGroups;

    [ObservableProperty]
    private bool _fixFallbacks;

    [ObservableProperty]
    private bool _completeLanguages;

    [ObservableProperty]
    private Language? _completeLanguage = LanguageTable.Find("en");

    [ObservableProperty]
    private bool _enableAudio;

    [ObservableProperty]
    private Language? _audioLanguage = LanguageTable.Find("en");

    [ObservableProperty]
    private bool _enableSubtitles;

    [ObservableProperty]
    private Language? _subtitleLanguage = LanguageTable.Find("en");

    [ObservableProperty]
    private bool _clearTrackNames;

    [ObservableProperty]
    private bool _prettifyAudioNames;

    [ObservableProperty]
    private bool _renameChapters;

    [ObservableProperty]
    private bool _applyColorSpace;

    [ObservableProperty]
    private ColorPreset? _colorPreset = ColorPreset.All[3];

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is not (nameof(SelectedItem) or nameof(Summary)))
            SaveOptions();
    }

    private void LoadOptions(QueueOptions o)
    {
        UseFolder = o.Location == OutputLocation.Folder;
        Folder = o.Folder;
        FileType = FileTypes.FirstOrDefault(f => f.Value == o.FileType) ?? FileTypes[0];
        AutoStart = o.AutoStart;
        NotifyWhenDone = o.NotifyWhenDone;
        Optimize = o.Optimize;
        foreach (var action in o.DefaultActions)
        {
            switch (action)
            {
                case ApplyPresetAction a:
                    ApplyPresetEnabled = true;
                    ApplyPreset = Presets.FirstOrDefault(p => p.Name == a.Preset.Name) ?? a.Preset;
                    break;
                case ClearMetadataAction:
                    ClearMetadata = true;
                    break;
                case FetchMetadataAction f:
                    FetchMetadata = true;
                    FetchArtwork = ArtworkKinds.FirstOrDefault(k => k.Value == f.Artwork) ?? ArtworkKinds[0];
                    break;
                case SetOutputFileNameAction:
                    SetOutputFileName = true;
                    break;
                case ImportChaptersFileAction:
                    LoadChapters = true;
                    break;
                case OrganizeGroupsAction:
                    OrganizeGroups = true;
                    break;
                case FixFallbacksAction:
                    FixFallbacks = true;
                    break;
                case CompleteLanguagesAction c:
                    CompleteLanguages = true;
                    CompleteLanguage = LanguageTable.Find(c.Language);
                    break;
                case EnableTrackWithLanguageAction { Kind: TrackKind.Audio } e:
                    EnableAudio = true;
                    AudioLanguage = LanguageTable.Find(e.Language);
                    break;
                case EnableTrackWithLanguageAction e:
                    EnableSubtitles = true;
                    SubtitleLanguage = LanguageTable.Find(e.Language);
                    break;
                case ClearTrackNamesAction:
                    ClearTrackNames = true;
                    break;
                case PrettifyAudioNamesAction:
                    PrettifyAudioNames = true;
                    break;
                case RenameChaptersAction:
                    RenameChapters = true;
                    break;
                case ApplyColorSpaceAction c:
                    ApplyColorSpace = true;
                    ColorPreset = ColorPreset.All.FirstOrDefault(p => p.Color == new ColorInfo(c.Primaries, c.Transfer, c.Matrix)) ?? ColorPreset;
                    break;
            }
        }
    }

    private void SaveOptions()
    {
        var o = Runner.Options;
        o.Location = UseFolder ? OutputLocation.Folder : OutputLocation.SameAsSource;
        o.Folder = Folder;
        o.FileType = FileType.Value;
        o.AutoStart = AutoStart;
        o.NotifyWhenDone = NotifyWhenDone;
        o.Optimize = Optimize;

        // Order matters: metadata first, then naming (which reads the tags), then track tidying.
        var actions = new List<QueueAction>();
        if (ClearMetadata)
            actions.Add(new ClearMetadataAction());
        if (FetchMetadata)
            actions.Add(new FetchMetadataAction { Artwork = FetchArtwork.Value });
        if (ApplyPresetEnabled && ApplyPreset is not null)
            actions.Add(new ApplyPresetAction { Preset = ApplyPreset });
        if (LoadChapters)
            actions.Add(new ImportChaptersFileAction());
        if (CompleteLanguages && CompleteLanguage is not null)
            actions.Add(new CompleteLanguagesAction { Language = CompleteLanguage.Tag });
        if (OrganizeGroups)
            actions.Add(new OrganizeGroupsAction());
        if (FixFallbacks)
            actions.Add(new FixFallbacksAction());
        if (EnableAudio && AudioLanguage is not null)
            actions.Add(new EnableTrackWithLanguageAction { Kind = TrackKind.Audio, Language = AudioLanguage.Tag });
        if (EnableSubtitles && SubtitleLanguage is not null)
            actions.Add(new EnableTrackWithLanguageAction { Kind = TrackKind.Subtitle, Language = SubtitleLanguage.Tag });
        if (ClearTrackNames)
            actions.Add(new ClearTrackNamesAction());
        if (PrettifyAudioNames)
            actions.Add(new PrettifyAudioNamesAction());
        if (RenameChapters)
            actions.Add(new RenameChaptersAction());
        if (ApplyColorSpace && ColorPreset is not null)
            actions.Add(new ApplyColorSpaceAction { Primaries = ColorPreset.Color.Primaries, Transfer = ColorPreset.Color.Transfer, Matrix = ColorPreset.Color.Matrix });
        if (SetOutputFileName)
            actions.Add(new SetOutputFileNameAction { MovieFormat = _settings.Settings.MovieFileNameFormat, TvFormat = _settings.Settings.TvFileNameFormat });
        o.DefaultActions = actions;
        Persist();
    }

    private void Persist()
    {
        try
        {
            QueueStore.Save(Runner, _storePath);
        }
        catch (IOException ex)
        {
            Core.Diagnostics.AppLog.Error("Could not save the queue", ex);
        }
    }

    // ------------------------------------------------------------------ commands

    /// <summary>Adds files and starts the queue when auto-start is on.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        Runner.Add(paths.Where(DocumentService.IsSupported));
        if (AutoStart && !Runner.IsRunning)
            _ = Start();
    }

    [RelayCommand]
    private async Task Add()
    {
        var files = await _dialogs.OpenFilesAsync("Add to Queue", [FileFilters.Media, FileFilters.All], allowMultiple: true);
        AddFiles(files);
    }

    [RelayCommand]
    private async Task ChooseFolder()
    {
        var folder = await _dialogs.PickFolderAsync("Output Folder");
        if (folder is not null)
        {
            Folder = folder;
            UseFolder = true;
        }
    }

    private bool CanStart() => !Runner.IsRunning && Items.Any(i => i.Status == QueueItemStatus.Ready);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        await Runner.RunAsync();
        StartCommand.NotifyCanExecuteChanged();
    }

    private bool CanStop() => Runner.IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => Runner.Stop();

    private bool HasSelection() => SelectedItem is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (SelectedItem is { Status: not QueueItemStatus.Working } item)
            Items.Remove(item);
    }

    [RelayCommand]
    private void RemoveCompleted() => Runner.RemoveCompleted();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Retry()
    {
        if (SelectedItem is { Status: QueueItemStatus.Failed or QueueItemStatus.Cancelled or QueueItemStatus.Completed } item)
        {
            item.Reset();
            StartCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(Summary));
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Reveal() => FileManager.Reveal(SelectedItem!.DestinationPath ?? SelectedItem.SourcePath);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Edit() => EditRequested?.Invoke(this, SelectedItem!.DestinationPath is { } d && File.Exists(d) ? d : SelectedItem.SourcePath);
}
