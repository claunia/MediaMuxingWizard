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
        _ = ScanVideoAsync();
    }

    public MediaDocument Document { get; }

    public UndoStack Undo { get; } = new();

    public ObservableCollection<TrackRowViewModel> Rows { get; } = [];

    public MetadataInspectorViewModel MetadataInspector { get; }

    public string Title => Document.DisplayName + (Document.IsDirty ? " •" : string.Empty);

    public bool IsDirty => Document.IsDirty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteTracksCommand), nameof(MoveTrackUpCommand), nameof(MoveTrackDownCommand), nameof(ExportTrackCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(SaveAsCommand), nameof(ExportTrackCommand))]
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
                    {
                        video.ProfileLevel = stream.ProfileLevel;
                        video.FormatDetails = video.FormatDetails.Length > 0 ? video.FormatDetails + ", " + stream.ProfileLevel : stream.ProfileLevel;
                    }

                    changed = true;
                }

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
                AppLog.Debug($"Video scan skipped for {Document.DisplayName}: {ex.Message}");
            }
        }

        // DTS: the product (DTS-HD MA, DTS:X …) is only known from the bitstream; so are Opus's modes and bandwidth, and
        // FLAC's depth, block size and encoder are in its STREAMINFO and vendor string, MPEG audio's bit rate mode in its frames.
        foreach (var audio in Document.Tracks.OfType<AudioTrack>().Where(a => DtsDetector.NeedsCheck(a) || OpusDetector.NeedsCheck(a) || FlacDetector.NeedsCheck(a) ||
                                                                          MpegAudioDetector.NeedsCheck(a) || EntryCodecDetector.NeedsCheck(a)).ToList())
        {
            try
            {
                var described = OpusDetector.NeedsCheck(audio) ? await OpusDetector.DescribeAsync(audio)
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
                AppLog.Debug($"Audio scan skipped for {Document.DisplayName}: {ex.Message}");
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
        AppLog.Info($"{Document.DisplayName}: Dolby Vision {detection.ProfileName} found in the bitstream but not signalled by the container.");
    }

    private bool CanRepairDolbyVision() => _dolbyVisionRepair is not null && DolbyVisionNotice is not null;

    /// <summary>Adds the rebuilt configuration record to the video track (undoable; written on save).</summary>
    [RelayCommand(CanExecute = nameof(CanRepairDolbyVision))]
    private void RepairDolbyVision()
    {
        var (video, detection) = _dolbyVisionRepair!.Value;
        using (Undo.Transaction(Strings.Undo_RepairDolbyVision))
            video.DolbyVisionRecord = detection.ConfigurationRecord;
        DolbyVisionNotice = null;
        AppLog.Info($"{Document.DisplayName}: Dolby Vision configuration rebuilt ({detection.ProfileName}, level {detection.Level}).");
    }

    [RelayCommand]
    private void DismissDolbyVisionNotice() => DolbyVisionNotice = null;

    private Task<IReadOnlyList<MMW.Media.Remux.ImportableTrack>>? _sourceTracks;

    /// <summary>The document's own tracks as the importer sees them (codec details and conversion choices).</summary>
    public Task<IReadOnlyList<MMW.Media.Remux.ImportableTrack>> GetSourceTracksAsync() =>
        _sourceTracks ??= Document.Path is { } path
            ? MMW.Media.Remux.TrackImporter.InspectAsync(path, Document.Container)
            : Task.FromResult<IReadOnlyList<MMW.Media.Remux.ImportableTrack>>([]);

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
    private void PrettifyAudioNames()
    {
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
            AppLog.Info($"Exported {config.FormatName} track {track.Id} to '{path}'.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            AppLog.Error($"Exporting track {track.Id} of '{Document.DisplayName}' failed", ex);
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
            .Select(c => $"• {c.Track.Name} ({c.Track.Format}): {c.Support.Reason}")
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
            _sourceTracks = null;
            _trackInspectors.Clear();
            OnPropertyChanged(nameof(StatusText));
            foreach (var row in Rows)
                row.Refresh();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Error($"Saving '{Document.DisplayName}' failed", ex);
            await _dialogs.ShowMessageAsync(Strings.Dialog_CouldNotSave_Title, ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
