using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Resources;
using MMW.App.Services;
using MMW.Core.Actions;
using MMW.Core.Diagnostics;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
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
        _tracker.TrackCollection(document.Tracks, Strings.Undo_ItemTrack);
        _tracker.TrackCollection(document.Chapters, Strings.Undo_ItemChapter);
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

            if (e.PropertyName is nameof(MediaDocument.Path))
            {
                OnPropertyChanged(nameof(CanChangeOutputFormat));
                ChangeOutputFormatCommand.NotifyCanExecuteChanged();
            }
        };

        MetadataInspector = new MetadataInspectorViewModel(document.Metadata, Undo, dialogs, settings);
        Rows.Add(TrackRowViewModel.ForMetadata());
        foreach (var t in document.Tracks)
            Rows.Add(TrackRowViewModel.ForTrack(t));
        document.Tracks.CollectionChanged += OnTracksChanged;
        SelectedRow = Rows[0];
        _ = ScanVideoAsync();
    }

    public MediaDocument Document { get; }

    public UndoStack Undo { get; } = new();

    public ObservableCollection<TrackRowViewModel> Rows { get; } = [];

    public MetadataInspectorViewModel MetadataInspector { get; }

    public string Title => Document.DisplayName + (Document.IsDirty ? " •" : string.Empty);

    public bool IsDirty => Document.IsDirty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteTracksCommand), nameof(MoveTrackUpCommand), nameof(MoveTrackDownCommand), nameof(ExportTrackCommand), nameof(DuplicateTrackCommand))]
    private TrackRowViewModel? _selectedRow;

    /// <summary>All selected rows (set by the view; the grid supports extended selection).</summary>
    public IReadOnlyList<TrackRowViewModel> SelectedRows
    {
        get => _selectedRows;
        set
        {
            _selectedRows = value;
            DeleteTracksCommand.NotifyCanExecuteChanged();
            DuplicateTrackCommand.NotifyCanExecuteChanged();
            UpdateInspector();
        }
    }

    private IReadOnlyList<TrackRowViewModel> _selectedRows = [];

    [ObservableProperty]
    private object? _inspector;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(SaveAsCommand), nameof(ExportTrackCommand), nameof(DuplicateTrackCommand), nameof(ChangeOutputFormatCommand))]
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
                    _ => Strings.Status_UnknownContainer,
                },
                TrackRowViewModel.FormatDuration(Document.Duration),
                string.Create(CultureInfo.InvariantCulture, $"{Document.FileSize / 1048576.0:0.#} MiB"),
                string.Format(CultureInfo.CurrentCulture, Strings.Status_TracksFormat, Document.Tracks.Count(t => t is not ChapterTrack)),
            };
            if (Document.Chapters.Count > 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.Status_ChaptersFormat, Document.Chapters.Count));
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
            _trackInspectors[track] = vm = new TrackInspectorViewModel(track, Document, this);
        return vm;
    }

    // ------------------------------------------------------------------ Dolby Vision repair

    private (VideoTrack Track, DolbyVisionDetection Detection)? _dolbyVisionRepair;

    /// <summary>Notice shown when the bitstream has Dolby Vision but the container lacks its configuration.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDolbyVisionNotice))]
    [NotifyCanExecuteChangedFor(nameof(RepairDolbyVisionCommand))]
    private string? _dolbyVisionNotice;

    public bool HasDolbyVisionNotice => DolbyVisionNotice is not null;

    /// <summary>
    /// Scans the start of each video track: Dolby Vision RPUs the container does not signal (offered for repair) and
    /// HDR10+ dynamic metadata (shown in the track's HDR details).
    /// </summary>
    public async Task ScanVideoAsync()
    {
        foreach (var video in Document.Tracks.OfType<VideoTrack>().Where(VideoBitstreamScan.NeedsScan).ToList())
        {
            try
            {
                var result = await VideoBitstreamScan.ScanAsync(video);
                var changed = false;
                if (result.Hdr10Plus && !video.Hdr10Plus)
                {
                    video.Hdr10Plus = true; // detected, not an edit: not tracked for undo
                    changed = true;
                }

                if (result.HdrVivid && !video.HdrVivid)
                {
                    video.HdrVivid = true;
                    changed = true;
                }

                if ((result.OtherDynamicHdr & ~video.OtherDynamicHdr) != MMW.Core.Media.Codecs.DynamicHdrFormats.None)
                {
                    video.OtherDynamicHdr |= result.OtherDynamicHdr;
                    changed = true;
                }

                if (result.StreamInfo is { } stream && video.StreamInfo is null)
                {
                    video.StreamInfo = stream;
                    // Codecs that signal their profile only in the bitstream (AVS).
                    if (video.ProfileLevel.Length == 0 && stream.ProfileLevel.Length > 0)
                        video.ProfileLevel = stream.ProfileLevel;

                    changed = true;
                }

                // What the bitstream showed (HDR10+, HDR Vivid, the profile) belongs in the details too.
                if (changed)
                    TrackDetails.Refresh(video);

                if (changed)
                {
                    if (_trackInspectors.TryGetValue(video, out var inspector))
                        inspector.RefreshHdr();
                    foreach (var row in Rows.Where(r => r.Track == video))
                        row.Refresh();
                }

                if (result.MissingDolbyVision is { } detection && _dolbyVisionRepair is null)
                    OfferDolbyVisionRepair(video, detection);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                AppLog.Debug(string.Format(CultureInfo.CurrentCulture, Strings.Log_VideoScanSkippedFormat, Document.DisplayName, ex.Message));
            }
        }

        // Dolby Atmos in TrueHD and E-AC-3, and the DTS product (DTS-HD MA, DTS:X …), are only known from the bitstream; so are Opus's modes and bandwidth, and
        // FLAC's depth, block size and encoder are in its STREAMINFO and vendor string, MPEG audio's bit rate mode in its frames.
        foreach (var audio in Document.Tracks.OfType<AudioTrack>().Where(a => DtsDetector.NeedsCheck(a) || OpusDetector.NeedsCheck(a) || FlacDetector.NeedsCheck(a) ||
                                                                          MpegAudioDetector.NeedsCheck(a) || EntryCodecDetector.NeedsCheck(a) || AtmosDetector.NeedsCheck(a)).ToList())
        {
            try
            {
                var described = AtmosDetector.NeedsCheck(audio) ? await AtmosDetector.DescribeAsync(audio)
                    : OpusDetector.NeedsCheck(audio) ? await OpusDetector.DescribeAsync(audio)
                    : FlacDetector.NeedsCheck(audio) ? await FlacDetector.DescribeAsync(audio)
                    : MpegAudioDetector.NeedsCheck(audio) ? await MpegAudioDetector.DescribeAsync(audio)
                    : EntryCodecDetector.NeedsCheck(audio) ? await EntryCodecDetector.DescribeAsync(audio)
                    : await DtsDetector.DescribeAsync(audio);
                if (described)
                {
                    if (_trackInspectors.TryGetValue(audio, out var inspector))
                        inspector.RefreshHdr();
                    foreach (var row in Rows.Where(r => r.Track == audio))
                        row.Refresh();
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                AppLog.Debug(string.Format(CultureInfo.CurrentCulture, Strings.Log_AudioScanSkippedFormat, Document.DisplayName, ex.Message));
            }
        }
    }

    private void OfferDolbyVisionRepair(VideoTrack video, DolbyVisionDetection detection)
    {
        _dolbyVisionRepair = (video, detection);
        var fallback = detection.BlSignalCompatibilityId switch
        {
            1 or 6 => video.Hdr10Plus ? Strings.Fallback_Hdr10Plus : Strings.Fallback_Hdr10,
            4 => Strings.Fallback_Hlg,
            2 => Strings.Fallback_Sdr,
            _ => Strings.Fallback_Unwatchable,
        };
        DolbyVisionNotice = string.Format(CultureInfo.CurrentCulture, Strings.Notice_DolbyVisionMissingFormat, detection.ProfileName, detection.Level, fallback);
        AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.Log_DolbyVisionNotSignalledFormat, Document.DisplayName, detection.ProfileName));
    }

    private bool CanRepairDolbyVision() => _dolbyVisionRepair is not null && DolbyVisionNotice is not null;

    /// <summary>Adds the rebuilt configuration record to the video track (undoable; written on save).</summary>
    [RelayCommand(CanExecute = nameof(CanRepairDolbyVision))]
    private void RepairDolbyVision()
    {
        var (video, detection) = _dolbyVisionRepair!.Value;
        using (Undo.Transaction(Strings.Undo_RepairDolbyVision))
            video.DolbyVisionRecord = detection.ConfigurationRecord;
        TrackDetails.Refresh(video);
        foreach (var row in Rows.Where(r => r.Track == video))
            row.Refresh();
        DolbyVisionNotice = null;
        AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.Log_DolbyVisionRebuiltFormat, Document.DisplayName, detection.ProfileName, detection.Level));
    }

    [RelayCommand]
    private void DismissDolbyVisionNotice() => DolbyVisionNotice = null;

    private readonly Dictionary<string, Task<IReadOnlyList<MMW.Media.Remux.ImportableTrack>>> _sourceTracks = new(StringComparer.Ordinal);

    /// <summary>The document's own tracks as the importer sees them (codec details and conversion choices).</summary>
    public Task<IReadOnlyList<MMW.Media.Remux.ImportableTrack>> GetSourceTracksAsync() =>
        Document.Path is { } path ? GetSourceTracksAsync(path) : Task.FromResult<IReadOnlyList<MMW.Media.Remux.ImportableTrack>>([]);

    /// <summary>
    /// The tracks of a source file (the document's own, or the file a pending track is imported from) as the importer
    /// sees them for the document's container: codec details and conversion choices. Cached until the next save.
    /// </summary>
    public Task<IReadOnlyList<MMW.Media.Remux.ImportableTrack>> GetSourceTracksAsync(string path)
    {
        var full = Path.GetFullPath(path);
        if (!_sourceTracks.TryGetValue(full, out var task))
            _sourceTracks[full] = task = MMW.Media.Remux.TrackImporter.InspectAsync(full, Document.Container);
        return task;
    }

    /// <summary>Sets how a track is converted on the next save (may add an AAC companion track).</summary>
    public async Task SetConversionAsync(Track track, MMW.Core.Media.ImportChoice choice)
    {
        if (choice.Ocr && !await OcrAdvice.EnsureModelsAsync(_dialogs, _settings.Settings, [track.Language]))
            return;
        using (Undo.Transaction(string.Format(CultureInfo.CurrentCulture, Strings.Undo_ConvertFormat, track.Format)))
        {
            MMW.Core.Media.TrackConversions.SetAction(Document, track, choice.Action, choice.SettingsFrom(MMW.Core.Media.ConversionDefaults.Settings),
                choice.OcrFrom(OcrAdvice.Options(_settings.Settings)));
        }

        Document.IsDirty = true;
        OnPropertyChanged(nameof(StatusText));
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
        using (Undo.Transaction(tracks.Count == 1 ? Strings.Undo_DeleteTrack : Strings.Undo_DeleteTracks))
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
    private async Task OffsetTracks()
    {
        var tracks = SelectedTracks().Where(t => t is not ChapterTrack).ToList();
        if (tracks.Count == 0)
        {
            await _dialogs.ShowMessageAsync(Strings.Dialog_Offset_Title, Strings.Dialog_Offset_SelectFirst);
            return;
        }

        var text = await _dialogs.PromptAsync(Strings.Dialog_Offset_Title, Strings.Dialog_Offset_Prompt, "0");
        if (text is null)
            return;
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms))
        {
            await _dialogs.ShowMessageAsync(Strings.Dialog_Offset_Title, string.Format(CultureInfo.CurrentCulture, Strings.Dialog_Offset_NotANumberFormat, text));
            return;
        }

        using (Undo.Transaction(Strings.Undo_OffsetTracks))
        {
            foreach (var t in tracks)
                t.StartOffset += TimeSpan.FromMilliseconds(ms);
        }
    }

    [RelayCommand]
    private void ClearTrackNames()
    {
        using (Undo.Transaction(Strings.Undo_ClearTrackNames))
            TrackActions.ClearTrackNames(Document);
    }

    [RelayCommand]
    private async Task PrettifyAudioNames()
    {
        // Atmos and the DTS product come from the bitstream: read them first so the names say so.
        IsBusy = true;
        try
        {
            await TrackActions.DescribeAudioAsync(Document);
        }
        finally
        {
            IsBusy = false;
        }

        // Atmos or a DTS product just found changes the details shown, not only the names.
        foreach (var row in Rows.Skip(1))
            row.Refresh();

        using (Undo.Transaction(Strings.Undo_PrettifyAudioNames))
            TrackActions.PrettifyAudioNames(Document);
    }

    [RelayCommand]
    private void OrganizeGroups()
    {
        using (Undo.Transaction(Strings.Undo_OrganizeGroups))
            GroupActions.OrganizeAlternateGroups(Document, inferMediaCharacteristics: true);
    }

    [RelayCommand]
    private void FixFallbacks()
    {
        using (Undo.Transaction(Strings.Undo_FixFallbacks))
            GroupActions.FixAudioFallbacks(Document);
    }

    [RelayCommand]
    private async Task CompleteLanguages()
    {
        var language = await _dialogs.ShowDialogAsync(new LanguagePickerDialogViewModel(Strings.Dialog_CompleteLanguages_Title, Strings.Dialog_CompleteLanguages_Message));
        if (language is null)
            return;
        using (Undo.Transaction(Strings.Undo_CompleteLanguages))
            GroupActions.CompleteLanguages(Document, language);
    }

    public IReadOnlyList<MenuNode> ColorSpaceMenu => ColorPreset.All.Select(p => new MenuNode(p.Name, ApplyColorSpaceCommand, p)).ToList();

    [RelayCommand]
    private void ApplyColorSpace(ColorPreset preset)
    {
        using (Undo.Transaction(Strings.Undo_ApplyColorSpace))
            GroupActions.ApplyColorSpace(Document, preset.Color);
    }

    [RelayCommand]
    private void InsertChaptersEvery(int minutes)
    {
        using (Undo.Transaction(Strings.Undo_InsertChapters))
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
        ApplyMetadata(Strings.Undo_ImportNfo, doc => doc.Metadata.Merge(set, overwrite: true, replaceArtworks: false));
    }

    [RelayCommand]
    private async Task ExportNfo()
    {
        var suggested = Document.Path is { } p ? Path.GetFileName(MMW.Metadata.Nfo.NfoMetadata.NfoPathFor(p)) : "metadata.nfo";
        var path = await _dialogs.SaveFileAsync(Strings.Dialog_ExportNfo_Title, suggested, [new FileFilter(Strings.FileFilter_KodiNfo, ["nfo"])]);
        if (path is not null)
            await File.WriteAllTextAsync(path, MMW.Metadata.Nfo.NfoMetadata.Export(Document.Metadata));
    }

    private bool CanDuplicateTrack() => !IsBusy && SelectedTracks().Take(2).ToList() is [var track] && MMW.Media.Remux.TrackImporter.CanDuplicate(track);

    /// <summary>
    /// Adds a pending copy of the selected subtitle track after it (undoable), so the same subtitles can be muxed
    /// again with another conversion (chosen in the copy's inspector).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDuplicateTrack))]
    private async Task DuplicateTrack()
    {
        if (SelectedTracks().Take(2).ToList() is not [SubtitleTrack { Source: { } source } original])
            return;
        MMW.Media.Remux.ImportableTrack? inspected = null;
        if (!original.IsPending)
        {
            try
            {
                inspected = (await GetSourceTracksAsync(source.Path)).FirstOrDefault(t => t.TrackId == source.TrackId);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                AppLog.Debug(string.Format(CultureInfo.CurrentCulture, Strings.Log_TrackNotInspectedFormat, source.TrackId, source.Path, ex.Message));
            }
        }

        if (!Document.Tracks.Contains(original))
            return; // deleted while the file was inspected
        SubtitleTrack copy;
        using (Undo.Transaction(Strings.Undo_DuplicateTrack))
            copy = MMW.Media.Remux.TrackImporter.Duplicate(Document, original, inspected);
        SelectedRow = Rows.FirstOrDefault(r => r.Track == copy) ?? SelectedRow;
    }

    private bool CanExportTrack() => !IsBusy && SelectedRow?.Track is { Source: not null } and not ChapterTrack;

    /// <summary>Writes the selected track as a raw stream (.h264, .aac, .flac, .srt …), as its codec stores it outside a container.</summary>
    [RelayCommand(CanExecute = nameof(CanExportTrack))]
    private async Task ExportTrack()
    {
        if (SelectedRow?.Track is not { Source: { } source } track)
            return;
        CodecConfig? config;
        try
        {
            config = await Task.Run(() =>
            {
                using var demuxer = MediaFormatRegistry.OpenDemuxer(source.Path, new DemuxOptions { FrameRate = source.Import?.FrameRate });
                return demuxer.Tracks.FirstOrDefault(t => t.TrackId == source.TrackId)?.Config;
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync(Strings.Dialog_CouldNotExport_Title, ex.Message);
            return;
        }

        if (config is null || TrackExport.Extension(config) is not { } extension)
        {
            await _dialogs.ShowMessageAsync(Strings.Dialog_CouldNotExport_Title,
                string.Format(CultureInfo.CurrentCulture, Strings.Dialog_CannotExportTrack, config?.FormatName ?? track.Format));
            return;
        }

        var baseName = Path.GetFileNameWithoutExtension(Document.Path ?? source.Path);
        var number = track.IsPending ? source.TrackId : track.Id;
        var suggested = string.Create(CultureInfo.InvariantCulture, $"{baseName} - {number}{extension}");
        var filter = new FileFilter(string.Format(CultureInfo.CurrentCulture, Strings.FileFilter_TrackFormat, config.FormatName), [extension.TrimStart('.')]);
        if (await _dialogs.SaveFileAsync(Strings.Dialog_ExportTrack_Title, suggested, [filter]) is not { } path)
            return;

        IsBusy = true;
        Progress = 0;
        try
        {
            var progress = new Progress<double>(p => Progress = p);
            await Task.Run(async () =>
            {
                using var demuxer = MediaFormatRegistry.OpenDemuxer(source.Path, new DemuxOptions { FrameRate = source.Import?.FrameRate });
                var sampleSource = demuxer.Tracks.First(t => t.TrackId == source.TrackId);
                await TrackExport.ExportAsync(sampleSource, path, progress);
            });
            AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.Log_ExportedTrackFormat, config.FormatName, track.Id, path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            AppLog.Error(string.Format(CultureInfo.CurrentCulture, Strings.Log_ExportTrackFailedFormat, track.Id, Document.DisplayName), ex);
            await _dialogs.ShowMessageAsync(Strings.Dialog_CouldNotExport_Title, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Imports a chapter file dropped on the document.</summary>
    public async Task ImportChaptersAsync(string path)
    {
        _chaptersInspector ??= new ChaptersInspectorViewModel(Document, _dialogs);
        using (Undo.Transaction(Strings.Undo_ImportChapters))
            await _chaptersInspector.ImportFileAsync(path);
        SelectedRow = Rows.FirstOrDefault(r => r.Track is ChapterTrack) ?? SelectedRow;
    }

    // ------------------------------------------------------------------ saving

    private bool CanSave() => !IsBusy;

    /// <summary>
    /// Saves in place; a document that has no file yet, or whose output format was switched away from its file's,
    /// goes through Save As.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task<bool> Save()
    {
        if (Document.Path is null || RemuxPolicy.ChangesContainer(Document, Document.Container))
            return await SaveAsCoreAsync();
        var s = _settings.Settings;
        return await SaveCoreAsync(new SaveOptions
        {
            Optimize = s.OptimizeOnSave,
            Use64BitOffsets = s.Use64BitOffsets,
            Use64BitTimes = s.Use64BitTimes,
        });
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task<bool> SaveAs() => SaveAsCoreAsync();

    private async Task<bool> SaveAsCoreAsync()
    {
        var dialog = new SaveAsDialogViewModel(Document, _dialogs, _settings.Settings);
        var options = await _dialogs.ShowDialogAsync(dialog);
        if (options is null)
            return false;

        // The other container family: text subtitles are converted with the subtitle converters (SubRip, SSA and ASS
        // become tx3g in MP4; tx3g becomes SubRip or ASS in Matroska, as the user picks); any other track that does
        // not fit stops Save As, to be converted or removed in the document window first.
        var target = RemuxPolicy.TargetKind(Document, options);
        if (target != Document.Container)
        {
            // tx3g has no Matroska form: the tracks kept as they are are converted to the text format the user picks.
            var subRip = target == ContainerKind.Matroska
                ? Document.Tracks.OfType<SubtitleTrack>().Where(t => t.Format == "Tx3g" && t.Source is not null && !SubtitleConversions.IsOcr(t) &&
                                                                     (t.Source.Import?.Action ?? ImportAction.Passthrough) == ImportAction.Passthrough).ToList()
                : [];
            var (fits, textConversions) = await TracksFitAsync(target, subRip);
            if (!fits)
                return false;
            if (subRip.Count > 0)
            {
                string srt = Strings.Button_SubRip, ass = Strings.Button_Ass;
                var names = string.Join("\n", subRip.Select(t => "• " + (string.IsNullOrEmpty(t.Name) ? CodecNames.Friendly(t.Format) : $"{CodecNames.Friendly(t.Format)} – {t.Name}")));
                var answer = await _dialogs.ShowDialogAsync(new MessageDialogViewModel(Strings.Dialog_ConvertTx3g_Title,
                    string.Format(CultureInfo.CurrentCulture, Strings.Dialog_ConvertTx3g_MessageFormat, names), [srt, ass, Strings.Button_Cancel], ass));
                var action = answer == srt ? ImportAction.ConvertToSrt : answer == ass ? ImportAction.ConvertToAss : (ImportAction?)null;
                if (action is not { } chosen)
                    return false;
                using (Undo.Transaction(string.Format(CultureInfo.CurrentCulture, Strings.Undo_ConvertFormat, "Tx3g")))
                {
                    foreach (var track in subRip)
                        TrackConversions.SetAction(Document, track, chosen);
                }
            }

            if (textConversions.Count > 0)
            {
                using (Undo.Transaction(string.Format(CultureInfo.CurrentCulture, Strings.Undo_ConvertFormat, string.Join(", ", textConversions.Select(c => c.Track.Format).Distinct()))))
                {
                    foreach (var change in textConversions)
                        TrackConversions.SetAction(Document, change.Track, change.To.Action);
                }
            }
        }

        if (!await SaveCoreAsync(options))
            return false;
        _settings.Settings.AddRecent(options.OutputPath!);
        _settings.Save();
        return true;
    }

    // ------------------------------------------------------------------ output format

    /// <summary>
    /// Checks every track against <paramref name="target"/>. Text subtitles that do not fit are converted with the
    /// subtitle converters (returned, to be applied: SubRip, SSA and ASS become tx3g in MP4), and the
    /// <paramref name="converted"/> tracks Save As asks about are left to it; any other track that does not fit is
    /// reported (to be converted or removed in the document window) and the result is false.
    /// </summary>
    private async Task<(bool Fits, IReadOnlyList<MMW.Media.Remux.TrackRetarget> TextConversions)> TracksFitAsync(ContainerKind target, IReadOnlyCollection<Track> converted)
    {
        IsBusy = true;
        IReadOnlyList<MMW.Media.Remux.TrackRetarget> plan;
        try
        {
            plan = await MMW.Media.Remux.ContainerSwitch.PlanAsync(Document, target);
        }
        finally
        {
            IsBusy = false;
        }

        var text = plan.Where(c => c.Track is SubtitleTrack && !c.To.Ocr && !SubtitleConversions.IsOcr(c.Track) && !converted.Contains(c.Track) &&
                                   MMW.Core.Media.Subtitles.TextSubtitleConverter.Target(c.To.Action) is not null).ToList();
        var misfits = plan.Where(c => !converted.Contains(c.Track) && !text.Contains(c)).ToList();
        if (misfits.Count == 0)
            return (true, text);
        var lines = misfits.Select(c =>
        {
            var track = string.IsNullOrEmpty(c.Track.Name) ? CodecNames.Friendly(c.Track.Format) : $"{CodecNames.Friendly(c.Track.Format)} – {c.Track.Name}";
            return c.Reason is null
                ? string.Format(CultureInfo.CurrentCulture, Strings.Incompatible_LineFormat, track)
                : string.Format(CultureInfo.CurrentCulture, Strings.Incompatible_LineReasonFormat, track, c.Reason);
        });
        await _dialogs.ShowMessageAsync(Strings.Dialog_IncompatibleTracks_Title,
            string.Format(CultureInfo.CurrentCulture, Strings.Dialog_IncompatibleTracks_MessageFormat, ContainerName(target), string.Join("\n", lines)));
        return (false, []);
    }

    public bool IsMp4Output => Document.Container == ContainerKind.Mp4;

    public bool IsMatroskaOutput => Document.Container == ContainerKind.Matroska;

    /// <summary>
    /// Only a new document (never saved) can change its mind about its container; a file on disk is what it is (Save
    /// As still writes a copy in the other container).
    /// </summary>
    public bool CanChangeOutputFormat => Document.Path is null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanChangeOutputFormat))]
    private Task<bool> ChangeOutputFormat(string format) =>
        ChangeContainerAsync(format == "mkv" ? ContainerKind.Matroska : ContainerKind.Mp4);

    /// <summary>
    /// Makes <paramref name="target"/> the container the document is saved as. Tracks it cannot keep as they are get
    /// the recommended conversion (or are left out when nothing can store them), after the user agrees; the file
    /// itself changes on the next save. One undo step; false when the user declines.
    /// </summary>
    public async Task<bool> ChangeContainerAsync(ContainerKind target)
    {
        if (target == Document.Container || target == ContainerKind.Unknown)
            return true;
        IsBusy = true;
        IReadOnlyList<MMW.Media.Remux.TrackRetarget> changes;
        try
        {
            changes = await MMW.Media.Remux.ContainerSwitch.PlanAsync(Document, target);
        }
        finally
        {
            IsBusy = false;
        }

        var name = ContainerName(target);
        if (changes.Count > 0)
        {
            var lines = changes.Select(c =>
            {
                var track = string.IsNullOrEmpty(c.Track.Name) ? CodecNames.Friendly(c.Track.Format) : $"{CodecNames.Friendly(c.Track.Format)} – {c.Track.Name}";
                return c.To.Action == ImportAction.Skip
                    ? string.Format(CultureInfo.CurrentCulture, c.Reason is null ? Strings.Retarget_LeftOutFormat : Strings.Retarget_LeftOutReasonFormat, track, c.Reason)
                    : string.Format(CultureInfo.CurrentCulture, Strings.Retarget_LineFormat, track, ConversionDefaults.DisplayName(c.From), c.To.DisplayName);
            });
            if (!await _dialogs.ConfirmAsync(Strings.Dialog_ChangeContainer_Title,
                    string.Format(CultureInfo.CurrentCulture, Strings.Dialog_ChangeContainer_MessageFormat, name, string.Join("\n", lines)),
                    Strings.Button_ChangeFormat))
                return false;
            if (changes.Any(c => c.To.Ocr) &&
                !await OcrAdvice.EnsureModelsAsync(_dialogs, _settings.Settings, changes.Where(c => c.To.Ocr).Select(c => c.Track.Language).Distinct().ToList()))
                return false;
        }

        var previous = Document.Container;
        using (Undo.Transaction(string.Format(CultureInfo.CurrentCulture, Strings.Undo_ChangeContainerFormat, name)))
        {
            MMW.Media.Remux.ContainerSwitch.Apply(Document, target, changes);
            Undo.Record(new DelegateEdit(string.Empty, () => SetContainer(target), () => SetContainer(previous)));
        }

        SetContainer(target);
        Document.IsDirty = true;
        return true;
    }

    private static string ContainerName(ContainerKind kind) => kind == ContainerKind.Matroska ? "Matroska" : "MP4";

    /// <summary>Sets the output container and refreshes everything that depends on it (conversion choices, status).</summary>
    private void SetContainer(ContainerKind kind)
    {
        Document.Container = kind;
        _sourceTracks.Clear();
        _trackInspectors.Clear();
        UpdateInspector();
        foreach (var row in Rows)
            row.Refresh();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsMp4Output));
        OnPropertyChanged(nameof(IsMatroskaOutput));
    }

    /// <summary>Captures missing chapter previews before an MP4 save when the preference is on.</summary>
    private async Task CreateChapterPreviewsAsync(SaveOptions options)
    {
        var s = _settings.Settings;
        var targetIsMp4 = ContainerKinds.FromPath(options.OutputPath ?? Document.Path ?? string.Empty) == ContainerKind.Mp4;
        if (!s.CreateChapterPreviews || !targetIsMp4 || Document.Path is null || Document.Chapters.Count == 0 ||
            Document.Chapters.All(c => c.Thumbnail is not null) || !MMW.Media.Conversion.MediaConversion.IsAvailable ||
            Document.Tracks.OfType<VideoTrack>().FirstOrDefault() is not { IsPending: false } video)
            return;

        var chapters = Document.Chapters.OrderBy(c => c.Start).ToList();
        var times = new List<TimeSpan>(chapters.Count);
        for (var i = 0; i < chapters.Count; i++)
        {
            var end = i + 1 < chapters.Count ? chapters[i + 1].Start : Document.Duration;
            var length = end > chapters[i].Start ? end - chapters[i].Start : TimeSpan.Zero;
            // Stay a little inside the chapter so "end" does not land on the next one.
            times.Add(chapters[i].Start + length * Math.Clamp(s.ChapterPreviewPosition, 0, 0.95));
        }

        var images = await MMW.Media.Conversion.ThumbnailGenerator.CaptureManyAsync(Document.Path, video.Source?.TrackId ?? video.Id, times, 320);
        for (var i = 0; i < chapters.Count && i < images.Count; i++)
            chapters[i].Thumbnail ??= images[i];
    }

    /// <summary>
    /// Asks before saving to MP4 tracks whose HDR10+ metadata lives next to the frames (Matroska block additions):
    /// MP4 cannot keep it.
    /// </summary>
    private async Task<bool> ConfirmMetadataLossAsync(SaveOptions options)
    {
        if (RemuxPolicy.TargetKind(Document, options) != ContainerKind.Mp4 ||
            !Document.Tracks.OfType<VideoTrack>().Any(v => v.Hdr10Plus && v.Source is { Container: ContainerKind.Matroska }))
            return true;
        IReadOnlyList<(Track Track, TrackSupport Support)> checks;
        try
        {
            checks = await Remuxer.CheckAsync(Document, ContainerKind.Mp4);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return true; // the save reports it
        }

        var losses = checks.Where(c => c.Support.Level == TrackSupportLevel.Passthrough && c.Support.Reason is not null)
            .Select(c => $"• {c.Track.Name} ({CodecNames.Friendly(c.Track.Format)}): {c.Support.Reason}")
            .ToList();
        return losses.Count == 0 ||
               await _dialogs.ConfirmAsync(Strings.Dialog_MetadataLoss_Title,
                   string.Format(CultureInfo.CurrentCulture, Strings.Dialog_MetadataLoss_MessageFormat, string.Join("\n", losses)),
                   Strings.Button_SaveAnyway);
    }

    private async Task<bool> SaveCoreAsync(SaveOptions options)
    {
        if (!await ConfirmMetadataLossAsync(options))
            return false;
        IsBusy = true;
        Progress = 0;
        try
        {
            await CreateChapterPreviewsAsync(options);
            var handler = _documents.HandlerFor(Document);
            var progress = new Progress<double>(p => Progress = p);
            await handler.SaveAsync(Document, options, progress);
            _sourceTracks.Clear();
            _trackInspectors.Clear();
            OnPropertyChanged(nameof(StatusText));
            foreach (var row in Rows)
                row.Refresh();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Error(string.Format(CultureInfo.CurrentCulture, Strings.Log_SaveFailedFormat, Document.DisplayName), ex);
            await _dialogs.ShowMessageAsync(Strings.Dialog_CouldNotSave_Title, ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
