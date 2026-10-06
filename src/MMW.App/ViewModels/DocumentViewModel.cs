using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Services;
using MMW.Core.Actions;
using MMW.Core.Diagnostics;
using MMW.Core.Model;
using MMW.Core.Undo;

namespace MMW.App.ViewModels;

/// <summary>One open file: track list, inspector, undo history and save commands.</summary>
public sealed partial class DocumentViewModel : ViewModelBase
{
    private readonly DocumentService _documents;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly ObservableUndoTracker _tracker;
    private readonly Dictionary<Track, TrackInspectorViewModel> _trackInspectors = [];
    private ChaptersInspectorViewModel? _chaptersInspector;

    public DocumentViewModel(MediaDocument document, DocumentService documents, IDialogService dialogs, ISettingsService settings)
    {
        Document = document;
        _documents = documents;
        _dialogs = dialogs;
        _settings = settings;

        _tracker = new ObservableUndoTracker(Undo);
        _tracker.TrackCollection(document.Tracks, "Track");
        _tracker.TrackCollection(document.Chapters, "Chapter");
        Undo.Changed += (_, _) =>
        {
            Document.IsDirty = true;
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };
        Document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MediaDocument.IsDirty) or nameof(MediaDocument.Path))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(IsDirty));
            }
        };

        MetadataInspector = new MetadataInspectorViewModel(document.Metadata, Undo, dialogs, settings);
        Rows.Add(TrackRowViewModel.ForMetadata());
        foreach (var t in document.Tracks)
            Rows.Add(TrackRowViewModel.ForTrack(t));
        document.Tracks.CollectionChanged += OnTracksChanged;
        SelectedRow = Rows[0];
    }

    public MediaDocument Document { get; }

    public UndoStack Undo { get; } = new();

    public ObservableCollection<TrackRowViewModel> Rows { get; } = [];

    public MetadataInspectorViewModel MetadataInspector { get; }

    public string Title => Document.DisplayName + (Document.IsDirty ? " •" : string.Empty);

    public bool IsDirty => Document.IsDirty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteTracksCommand), nameof(MoveTrackUpCommand), nameof(MoveTrackDownCommand))]
    private TrackRowViewModel? _selectedRow;

    /// <summary>All selected rows (set by the view; the grid supports extended selection).</summary>
    public IReadOnlyList<TrackRowViewModel> SelectedRows
    {
        get => _selectedRows;
        set
        {
            _selectedRows = value;
            DeleteTracksCommand.NotifyCanExecuteChanged();
            UpdateInspector();
        }
    }

    private IReadOnlyList<TrackRowViewModel> _selectedRows = [];

    [ObservableProperty]
    private object? _inspector;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(SaveAsCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    public string StatusText
    {
        get
        {
            var parts = new List<string>
            {
                Document.Container switch
                {
                    ContainerKind.Mp4 => "MPEG-4",
                    ContainerKind.Matroska => "Matroska",
                    _ => "Unknown",
                },
                TrackRowViewModel.FormatDuration(Document.Duration),
                string.Create(CultureInfo.InvariantCulture, $"{Document.FileSize / 1048576.0:0.#} MiB"),
                $"{Document.Tracks.Count(t => t is not ChapterTrack)} tracks",
            };
            if (Document.Chapters.Count > 0)
                parts.Add($"{Document.Chapters.Count} chapters");
            return string.Join("  ·  ", parts);
        }
    }

    partial void OnSelectedRowChanged(TrackRowViewModel? value)
    {
        if (_selectedRows.Count <= 1)
            _selectedRows = value is null ? [] : [value];
        UpdateInspector();
    }

    private void UpdateInspector()
    {
        var rows = _selectedRows.Count > 0 ? _selectedRows : SelectedRow is null ? [] : [SelectedRow];
        Inspector = rows.Count switch
        {
            0 => MetadataInspector,
            > 1 => new MultiSelectionViewModel(rows.Where(r => r.Track is not null and not ChapterTrack).Select(r => r.Track!).ToList(), Undo),
            _ => rows[0].Track switch
            {
                null => MetadataInspector,
                ChapterTrack => _chaptersInspector ??= new ChaptersInspectorViewModel(Document, _dialogs),
                { } t => InspectorFor(t),
            },
        };
    }

    private TrackInspectorViewModel InspectorFor(Track track)
    {
        if (!_trackInspectors.TryGetValue(track, out var vm))
            _trackInspectors[track] = vm = new TrackInspectorViewModel(track, Document);
        return vm;
    }

    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Rows mirror Document.Tracks after the metadata row.
        var selected = SelectedRow?.Track;
        foreach (var row in Rows.Skip(1))
            row.Detach();
        while (Rows.Count > 1)
            Rows.RemoveAt(Rows.Count - 1);
        foreach (var t in Document.Tracks)
            Rows.Add(TrackRowViewModel.ForTrack(t));
        SelectedRow = Rows.FirstOrDefault(r => r.Track == selected && selected is not null) ?? Rows[0];
        OnPropertyChanged(nameof(StatusText));
    }

    // ------------------------------------------------------------------ commands

    private bool CanUndo() => Undo.CanUndo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void UndoEdit() => Undo.Undo();

    private bool CanRedo() => Undo.CanRedo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void RedoEdit() => Undo.Redo();

    // Short names used by menus and key bindings.
    public IRelayCommand UndoCommand => UndoEditCommand;

    public IRelayCommand RedoCommand => RedoEditCommand;

    private IEnumerable<Track> SelectedTracks() =>
        (_selectedRows.Count > 0 ? _selectedRows : SelectedRow is null ? [] : [SelectedRow]).Where(r => r.Track is not null).Select(r => r.Track!);

    private bool CanDeleteTracks() => SelectedTracks().Any();

    [RelayCommand(CanExecute = nameof(CanDeleteTracks))]
    private void DeleteTracks()
    {
        var tracks = SelectedTracks().ToList();
        using (Undo.Transaction(tracks.Count == 1 ? "Delete Track" : "Delete Tracks"))
        {
            foreach (var t in tracks)
            {
                if (t is ChapterTrack)
                    TrackActions.RemoveAll(Document.Chapters);
                Document.Tracks.Remove(t);
            }
        }
    }

    private bool CanMoveTrack() => SelectedRow?.Track is not null and not ChapterTrack;

    [RelayCommand(CanExecute = nameof(CanMoveTrack))]
    private void MoveTrackUp() => MoveTrack(-1);

    [RelayCommand(CanExecute = nameof(CanMoveTrack))]
    private void MoveTrackDown() => MoveTrack(1);

    /// <summary>Moves a track to a new index (used by drag-and-drop reordering too).</summary>
    public void MoveTrack(Track track, int newIndex)
    {
        var index = Document.Tracks.IndexOf(track);
        newIndex = Math.Clamp(newIndex, 0, Document.Tracks.Count - 1);
        if (index >= 0 && index != newIndex)
            Document.Tracks.Move(index, newIndex);
        SelectedRow = Rows.FirstOrDefault(r => r.Track == track);
    }

    private void MoveTrack(int delta)
    {
        if (SelectedRow?.Track is { } t)
            MoveTrack(t, Document.Tracks.IndexOf(t) + delta);
    }

    [RelayCommand]
    private void ClearTrackNames()
    {
        using (Undo.Transaction("Clear Track Names"))
            TrackActions.ClearTrackNames(Document);
    }

    [RelayCommand]
    private void PrettifyAudioNames()
    {
        using (Undo.Transaction("Prettify Audio Track Names"))
            TrackActions.PrettifyAudioNames(Document);
    }

    [RelayCommand]
    private void OrganizeGroups()
    {
        using (Undo.Transaction("Organize Alternate Groups"))
            GroupActions.OrganizeAlternateGroups(Document, inferMediaCharacteristics: true);
    }

    [RelayCommand]
    private void FixFallbacks()
    {
        using (Undo.Transaction("Fix Audio Fallbacks"))
            GroupActions.FixAudioFallbacks(Document);
    }

    [RelayCommand]
    private async Task CompleteLanguages()
    {
        var language = await _dialogs.ShowDialogAsync(new LanguagePickerDialogViewModel("Complete Track Languages",
            "Set this language on every track whose language is undetermined."));
        if (language is null)
            return;
        using (Undo.Transaction("Complete Languages"))
            GroupActions.CompleteLanguages(Document, language);
    }

    public IReadOnlyList<MenuNode> ColorSpaceMenu => ColorPreset.All.Select(p => new MenuNode(p.Name, ApplyColorSpaceCommand, p)).ToList();

    [RelayCommand]
    private void ApplyColorSpace(ColorPreset preset)
    {
        using (Undo.Transaction("Apply Colour Space"))
            GroupActions.ApplyColorSpace(Document, preset.Color);
    }

    [RelayCommand]
    private void InsertChaptersEvery(int minutes)
    {
        using (Undo.Transaction("Insert Chapters"))
            TrackActions.InsertChaptersEvery(Document, minutes == 0 ? null : TimeSpan.FromMinutes(minutes));
        SelectedRow = Rows.FirstOrDefault(r => r.Track is ChapterTrack) ?? SelectedRow;
    }

    /// <summary>Applies a metadata change (search result, NFO) with undo, and shows the metadata inspector.</summary>
    public void ApplyMetadata(string description, Action<MediaDocument> change)
    {
        MetadataInspector.ApplyExternal(description, () => change(Document));
        SelectedRow = Rows[0];
    }

    /// <summary>Merges the tags of a Kodi .nfo file.</summary>
    public void ImportNfo(string path)
    {
        var set = MMW.Metadata.Nfo.NfoMetadata.Read(path);
        ApplyMetadata("Import NFO", doc => doc.Metadata.Merge(set, overwrite: true, replaceArtworks: false));
    }

    [RelayCommand]
    private async Task ExportNfo()
    {
        var suggested = Document.Path is { } p ? Path.GetFileName(MMW.Metadata.Nfo.NfoMetadata.NfoPathFor(p)) : "metadata.nfo";
        var path = await _dialogs.SaveFileAsync("Export NFO", suggested, [new FileFilter("Kodi NFO", ["nfo"])]);
        if (path is not null)
            await File.WriteAllTextAsync(path, MMW.Metadata.Nfo.NfoMetadata.Export(Document.Metadata));
    }

    /// <summary>Imports a chapter file dropped on the document.</summary>
    public async Task ImportChaptersAsync(string path)
    {
        _chaptersInspector ??= new ChaptersInspectorViewModel(Document, _dialogs);
        using (Undo.Transaction("Import Chapters"))
            await _chaptersInspector.ImportFileAsync(path);
        SelectedRow = Rows.FirstOrDefault(r => r.Track is ChapterTrack) ?? SelectedRow;
    }

    // ------------------------------------------------------------------ saving

    private bool CanSave() => !IsBusy && Document.Path is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task<bool> Save()
    {
        var s = _settings.Settings;
        return await SaveCoreAsync(new SaveOptions
        {
            Optimize = s.OptimizeOnSave,
            Use64BitOffsets = s.Use64BitOffsets,
            Use64BitTimes = s.Use64BitTimes,
        });
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAs()
    {
        var dialog = new SaveAsDialogViewModel(Document, _dialogs, _settings.Settings);
        var options = await _dialogs.ShowDialogAsync(dialog);
        if (options is null)
            return;
        if (await SaveCoreAsync(options))
        {
            _settings.Settings.AddRecent(options.OutputPath!);
            _settings.Save();
        }
    }

    private async Task<bool> SaveCoreAsync(SaveOptions options)
    {
        IsBusy = true;
        Progress = 0;
        try
        {
            var handler = _documents.HandlerFor(Document);
            var progress = new Progress<double>(p => Progress = p);
            await handler.SaveAsync(Document, options, progress);
            OnPropertyChanged(nameof(StatusText));
            foreach (var row in Rows)
                row.Refresh();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Error($"Saving '{Document.DisplayName}' failed", ex);
            await _dialogs.ShowMessageAsync("Could not save", ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
