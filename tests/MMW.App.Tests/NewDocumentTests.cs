using Avalonia.Headless.XUnit;
using MMW.App.Resources;
using MMW.App.Services;
using MMW.App.ViewModels;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;

namespace MMW.App.Tests;

/// <summary>New documents: File › New, track files dropped where there is no document, and the output format of a new document.</summary>
public class NewDocumentTests
{
    private static (MainWindowViewModel Window, FakeDialogService Dialogs, string Dir) CreateWindow()
    {
        MMW.Media.Remux.MediaRemux.EnsureRegistered();
        var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dialogs = new FakeDialogService();
        return (new MainWindowViewModel(new DocumentService(), dialogs, new SettingsService(Path.Combine(dir, "settings.json"))), dialogs, dir);
    }

    /// <summary>Ticks every track the import dialog can import and imports them, as a user accepting the defaults would.</summary>
    private static async Task<bool> ImportEverything(object dialog)
    {
        if (dialog is not ImportDialogViewModel import)
            return false;
        // The window already started loading the files; wait for it as the user would.
        for (var i = 0; i < 600 && import.IsLoading; i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        foreach (var track in import.AllTracks.Where(t => t.CanImport))
            track.Selected = true;
        await import.ImportCommand.ExecuteAsync(null);
        return true;
    }

    private static async Task<(string Srt, string H264)> TrackFilesAsync(string dir)
    {
        var srt = Path.Combine(dir, "subs.en.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,200 --> 00:00:01,000\nHello\n\n2\n00:00:01,200 --> 00:00:01,800\nBye\n", TestContext.Current.CancellationToken);
        var h264 = Fixtures.CopyToTemp(Fixtures.Get("new-doc.h264", "ffmpeg",
            "-y -v error -f lavfi -i testsrc=duration=2:size=160x120:rate=25 -c:v libx264 -preset ultrafast -f h264 {out}"));
        return (srt, h264);
    }

    [AvaloniaFact]
    public void New_documents_fill_the_empty_window_then_get_their_own()
    {
        var (home, _, _) = CreateWindow();
        home.NewDocumentCommand.Execute("mkv");
        Assert.Equal(ContainerKind.Matroska, home.Document!.Document.Container);
        Assert.Null(home.Document.Document.Path);
        Assert.True(home.Document.CanChangeOutputFormat);

        home.NewDocumentCommand.Execute("mp4");
        Assert.Equal(2, home.App.Windows.Count);
        Assert.Equal(ContainerKind.Mp4, home.App.Windows[1].Document!.Document.Container);
    }

    [AvaloniaFact]
    public async Task Dropped_track_files_start_a_new_document_that_can_switch_container_until_saved()
    {
        MediaProbe.RequireFfmpeg();
        var (home, dialogs, dir) = CreateWindow();
        var (srt, h264) = await TrackFilesAsync(dir);

        // No document in the window: the drop asks for a container and imports into a new document.
        dialogs.DialogResults.Enqueue(Strings.Button_Matroska);
        dialogs.OnShowDialog = ImportEverything;
        await home.OpenPathsAsync([srt, h264]);
        var doc = home.Document!;
        Assert.Equal(ContainerKind.Matroska, doc.Document.Container);
        Assert.Null(doc.Document.Path);
        var tracks = string.Join(" / ", doc.Document.Tracks.Select(t => $"{t.GetType().Name} {t.Format} {Path.GetFileName(t.Source?.Path)} {t.Source?.TrackId} {t.Source?.Import?.Action}"));
        Assert.True(doc.Document.Tracks.OfType<SubtitleTrack>().Count() == 1, tracks);
        var subtitle = doc.Document.Tracks.OfType<SubtitleTrack>().Single();
        Assert.Single(doc.Document.Tracks.OfType<VideoTrack>());
        Assert.Equal(ImportAction.Passthrough, subtitle.Source!.Import?.Action ?? ImportAction.Passthrough);

        // Changing one's mind: SubRip cannot go into MP4 as it is, so it gets the recommended tx3g; one undo step.
        Assert.True(await doc.ChangeContainerAsync(ContainerKind.Mp4));
        Assert.Equal(ContainerKind.Mp4, doc.Document.Container);
        Assert.Equal(ImportAction.ConvertToTx3g, subtitle.Source!.Import!.Action);
        doc.UndoCommand.Execute(null);
        Assert.Equal(ContainerKind.Matroska, doc.Document.Container);
        Assert.Equal(ImportAction.Passthrough, subtitle.Source!.Import?.Action ?? ImportAction.Passthrough);
        doc.RedoCommand.Execute(null);
        Assert.Equal(ContainerKind.Mp4, doc.Document.Container);
        Assert.True(doc.IsMp4Output);

        // The first save asks where to write it.
        var m4v = Path.Combine(dir, "new.m4v");
        dialogs.OnShowDialog = null;
        dialogs.DialogResults.Enqueue(new SaveOptions { OutputPath = m4v });
        Assert.True(await doc.Save(), string.Join("\n", dialogs.Messages));
        Assert.Equal(m4v, doc.Document.Path);
        Assert.False(doc.CanChangeOutputFormat);
        var streams = MediaProbe.Streams(m4v);
        Assert.Contains(streams, s => s.Codec == "h264");
        Assert.Contains(streams, s => s.Codec == "mov_text");
    }

    [AvaloniaFact]
    public async Task A_cancelled_import_leaves_the_home_screen()
    {
        MediaProbe.RequireFfmpeg();
        var (home, dialogs, dir) = CreateWindow();
        var (srt, _) = await TrackFilesAsync(dir);
        dialogs.DialogResults.Enqueue(Strings.Button_Mp4);
        await home.OpenPathsAsync([srt]);
        Assert.Null(home.Document);
        Assert.Single(home.App.Windows);

        // Cancelling the container question does nothing either.
        dialogs.DialogResults.Enqueue(Strings.Button_Cancel);
        await home.OpenPathsAsync([srt]);
        Assert.Null(home.Document);
    }

    [AvaloniaFact]
    public async Task An_opened_file_keeps_its_container()
    {
        var path = Path.Combine(Fixtures.GeneratedDirectory, "mp4-moov-end.mp4");
        if (!File.Exists(path))
            Assert.Skip("Run the MP4 format tests first to generate fixtures.");
        var (home, _, _) = CreateWindow();
        await home.OpenPathsAsync([Fixtures.CopyToTemp(path)]);
        Assert.False(home.Document!.CanChangeOutputFormat);
        Assert.False(home.Document.ChangeOutputFormatCommand.CanExecute("mkv"));
    }
}

/// <summary>Save As into the other container family converts nothing: tracks that do not fit stop it until the user converts or deletes them.</summary>
public class SaveAsOtherFormatTests
{
    private static (MainWindowViewModel Window, FakeDialogService Dialogs, string Dir) CreateWindow()
    {
        MMW.Media.Remux.MediaRemux.EnsureRegistered();
        var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dialogs = new FakeDialogService();
        return (new MainWindowViewModel(new DocumentService(), dialogs, new SettingsService(Path.Combine(dir, "settings.json"))), dialogs, dir);
    }

    /// <summary>SubRip, ASS and SSA have no MP4 form: the subtitle converters turn them into tx3g, without asking.</summary>
    [AvaloniaFact]
    public async Task Text_subtitles_become_tx3g_when_saved_as_mp4()
    {
        MediaProbe.RequireFfmpeg();
        var (window, dialogs, dir) = CreateWindow();
        var srt = Path.Combine(dir, "subs.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,200 --> 00:00:01,000\nHello\n", TestContext.Current.CancellationToken);
        var mkv = Path.Combine(dir, "movie.mkv");
        Fixtures.Run("ffmpeg", $"-y -v error -f lavfi -i testsrc=duration=2:size=160x120:rate=25 -i {Fixtures.Quote(srt)} " +
                               $"-map 0:v -map 1 -map 1 -map 1 -c:v libx264 -preset ultrafast -c:s:0 srt -c:s:1 ass -c:s:2 ssa -f matroska {Fixtures.Quote(mkv)}");
        await window.OpenPathsAsync([mkv]);
        var doc = window.Document!;
        Assert.Equal(3, doc.Document.Tracks.OfType<SubtitleTrack>().Count());

        var m4v = Path.Combine(dir, "movie.m4v");
        dialogs.DialogResults.Enqueue(new SaveOptions { OutputPath = m4v });
        await doc.SaveAsCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.Messages);
        var streams = MediaProbe.Streams(m4v);
        Assert.Contains(streams, s => s.Codec == "h264");
        Assert.Equal(["mov_text", "mov_text", "mov_text"], streams.Where(s => s.Type == "subtitle").Select(s => s.Codec));
    }

    /// <summary>A track that cannot go into MP4 (AVS1 video has no sample entry) stops Save As until it is removed.</summary>
    [AvaloniaFact]
    public async Task Incompatible_tracks_stop_save_as_until_they_are_removed()
    {
        var avs = Path.Combine(Corpus.Directory ?? string.Empty, "Video codecs", "AVS.mkv");
        Corpus.Require(Corpus.Directory is not null && File.Exists(avs) ? avs : string.Empty);
        MediaProbe.RequireFfmpeg();
        var (window, dialogs, dir) = CreateWindow();
        await window.OpenPathsAsync([Fixtures.CopyToTemp(avs)]);
        var doc = window.Document!;
        var m4v = Path.Combine(dir, "movie.m4v");

        dialogs.DialogResults.Enqueue(new SaveOptions { OutputPath = m4v });
        await doc.SaveAsCommand.ExecuteAsync(null);
        var error = Assert.Single(dialogs.Messages);
        Assert.StartsWith(Strings.Dialog_IncompatibleTracks_Title, error, StringComparison.Ordinal);
        Assert.Contains("AVS", error, StringComparison.Ordinal);
        Assert.False(File.Exists(m4v));

        doc.SelectedRow = doc.Rows.First(r => r.Track is VideoTrack);
        doc.DeleteTracksCommand.Execute(null);
        dialogs.DialogResults.Enqueue(new SaveOptions { OutputPath = m4v });
        await doc.SaveAsCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Messages);
        Assert.Equal("aac", Assert.Single(MediaProbe.Streams(m4v)).Codec);
    }

    /// <summary>tx3g has no Matroska form: Save As asks which text format to convert it to (or to stop).</summary>
    [AvaloniaTheory]
    [InlineData("SubRip", "S_TEXT/UTF8")]
    [InlineData("ASS", "S_TEXT/ASS")]
    [InlineData("Cancel", null)]
    public async Task Tx3g_saved_as_matroska_is_converted_to_the_chosen_format(string answer, string? codec)
    {
        MediaProbe.RequireFfmpeg();
        MMW.Media.Remux.MediaRemux.EnsureRegistered();
        var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var srt = Path.Combine(dir, "subs.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,200 --> 00:00:01,000\nHello\n", TestContext.Current.CancellationToken);
        var mp4 = Path.Combine(dir, "movie.mp4");
        Fixtures.Run("ffmpeg", $"-y -v error -f lavfi -i testsrc=duration=2:size=160x120:rate=25 -i {Fixtures.Quote(srt)} -c:v libx264 -preset ultrafast -c:s mov_text {Fixtures.Quote(mp4)}");
        var dialogs = new FakeDialogService();
        var window = new MainWindowViewModel(new DocumentService(), dialogs, new SettingsService(Path.Combine(dir, "settings.json")));
        await window.OpenPathsAsync([mp4]);
        var doc = window.Document!;
        Assert.Equal("Tx3g", doc.Document.Tracks.OfType<SubtitleTrack>().Single().Format);

        var mkv = Path.Combine(dir, "movie.mkv");
        dialogs.DialogResults.Enqueue(new SaveOptions { OutputPath = mkv });
        dialogs.DialogResults.Enqueue(answer switch
        {
            "SubRip" => Strings.Button_SubRip,
            "ASS" => Strings.Button_Ass,
            _ => Strings.Button_Cancel,
        });
        await doc.SaveAsCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.Messages);
        if (codec is null)
        {
            Assert.False(File.Exists(mkv));
            return;
        }

        // FFmpeg calls SSA "ass": the Matroska codec ID tells them apart.
        if (!Fixtures.HasTool("mkvmerge"))
            Assert.Skip("mkvmerge is needed to read the Matroska codec IDs.");
        var ids = MediaProbe.MkvIdentify(mkv).GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("properties").GetProperty("codec_id").GetString()).ToList();
        Assert.Contains("V_MPEG4/ISO/AVC", ids);
        Assert.Contains(codec, ids);
    }

}

/// <summary>Dropped or imported files are checked first: anything that cannot be used, even converted, rejects the whole drop.</summary>
public class DropRejectionTests
{
    private static (MainWindowViewModel Window, FakeDialogService Dialogs, string Dir) CreateWindow()
    {
        MMW.Media.Remux.MediaRemux.EnsureRegistered();
        var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dialogs = new FakeDialogService();
        return (new MainWindowViewModel(new DocumentService(), dialogs, new SettingsService(Path.Combine(dir, "settings.json"))), dialogs, dir);
    }

    [AvaloniaFact]
    public async Task Unusable_files_are_left_out_and_a_drop_of_nothing_usable_is_rejected()
    {
        var fixture = Path.Combine(Fixtures.GeneratedDirectory, "mp4-moov-end.mp4");
        if (!File.Exists(fixture))
            Assert.Skip("Run the MP4 format tests first to generate fixtures.");
        var (window, dialogs, dir) = CreateWindow();
        await window.OpenPathsAsync([Fixtures.CopyToTemp(fixture)]);
        var srt = Path.Combine(dir, "subs.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,200 --> 00:00:01,000\nHello\n", TestContext.Current.CancellationToken);
        var pdf = Path.Combine(dir, "notes.pdf");
        await File.WriteAllTextAsync(pdf, "%PDF-1.4", TestContext.Current.CancellationToken);
        var imported = new List<string>();
        dialogs.OnShowDialog = d =>
        {
            if (d is ImportDialogViewModel)
                imported.Add("import");
            return Task.FromResult(d is ImportDialogViewModel);
        };

        // The subtitles are usable: they go to the import dialog, and the PDF is named as left out.
        await window.OpenPathsAsync([srt, pdf]);
        var message = Assert.Single(dialogs.Messages);
        Assert.StartsWith(Strings.Drop_LeftOut_Title, message, StringComparison.Ordinal);
        Assert.Contains("notes.pdf", message, StringComparison.Ordinal);
        Assert.Single(imported);

        // Nothing usable: the drop is rejected.
        await window.OpenPathsAsync([pdf]);
        Assert.StartsWith(Strings.Drop_Rejected_Title, dialogs.Messages[1], StringComparison.Ordinal);
        Assert.Single(imported);
    }

    [AvaloniaFact]
    public async Task Artwork_alone_needs_a_document()
    {
        var (window, dialogs, dir) = CreateWindow();
        var jpg = Path.Combine(dir, "cover.jpg");
        await File.WriteAllBytesAsync(jpg, [0xFF, 0xD8, 0xFF, 0xD9], TestContext.Current.CancellationToken);
        await window.OpenPathsAsync([jpg]);
        Assert.Contains("cover.jpg", Assert.Single(dialogs.Messages), StringComparison.Ordinal);
        Assert.Null(window.Document);
    }

    [AvaloniaFact]
    public async Task A_file_is_rejected_only_when_none_of_its_tracks_can_be_stored()
    {
        var corpusFile = Path.Combine(Corpus.Directory ?? string.Empty, "Video codecs", "MPEG-5 EVC.mp4");
        Corpus.Require(Corpus.Directory is not null && File.Exists(corpusFile) ? corpusFile : string.Empty);
        MediaProbe.RequireFfmpeg();
        var (window, dialogs, dir) = CreateWindow();
        window.NewDocumentCommand.Execute("mkv");
        ImportDialogViewModel? shown = null;
        dialogs.OnShowDialog = async d =>
        {
            if (d is not ImportDialogViewModel import)
                return false;
            for (var i = 0; i < 600 && import.IsLoading; i++)
                await Task.Delay(50, TestContext.Current.CancellationToken);
            shown = import;
            return true;
        };

        // EVC + AAC: Matroska has no codec ID for MPEG-5 EVC, but the AAC can be used, so the file is accepted.
        Assert.True(await window.ImportFilesAsync([corpusFile]));
        Assert.Empty(dialogs.Messages);
        Assert.NotNull(shown);
        Assert.Contains(shown.AllTracks, t => t.CanImport && t.Track.Kind == TrackKind.Audio);
        Assert.DoesNotContain(shown.AllTracks, t => t.CanImport && t.Track.Kind == TrackKind.Video);

        // The EVC stream alone: nothing in it can be used, so it is rejected.
        var evc = Path.Combine(dir, "video.evc");
        Fixtures.Run("ffmpeg", $"-y -v error -i {Fixtures.Quote(corpusFile)} -map 0:v -c copy -f evc {Fixtures.Quote(evc)}");
        shown = null;
        Assert.False(await window.ImportFilesAsync([evc]));
        Assert.StartsWith(Strings.Drop_Rejected_Title, Assert.Single(dialogs.Messages), StringComparison.Ordinal);
        Assert.Null(shown);
    }
}

/// <summary>Whatever is dropped on an open document is imported into it, MP4 and Matroska files included.</summary>
public class DropOnDocumentTests
{
    [AvaloniaFact]
    public async Task A_matroska_file_dropped_on_a_document_offers_its_tracks()
    {
        var fixture = Path.Combine(Fixtures.GeneratedDirectory, "mp4-moov-end.mp4");
        if (!File.Exists(fixture))
            Assert.Skip("Run the MP4 format tests first to generate fixtures.");
        MediaProbe.RequireFfmpeg();
        MMW.Media.Remux.MediaRemux.EnsureRegistered();
        var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dialogs = new FakeDialogService();
        var window = new MainWindowViewModel(new DocumentService(), dialogs, new SettingsService(Path.Combine(dir, "settings.json")));
        await window.OpenPathsAsync([Fixtures.CopyToTemp(fixture)]);
        var before = window.Document!.Document.Tracks.Count;
        var mkv = Fixtures.CopyToTemp(Fixtures.Get("drop-audio.mkv", "ffmpeg",
            "-y -v error -f lavfi -i sine=f=440:d=1 -c:a aac -f matroska {out}"));

        ImportDialogViewModel? shown = null;
        dialogs.OnShowDialog = async d =>
        {
            if (d is not ImportDialogViewModel import)
                return false;
            for (var i = 0; i < 600 && import.IsLoading; i++)
                await Task.Delay(50, TestContext.Current.CancellationToken);
            foreach (var track in import.AllTracks.Where(t => t.CanImport))
                track.Selected = true;
            await import.ImportCommand.ExecuteAsync(null);
            shown = import;
            return true;
        };

        await window.DropAsync([mkv]);
        Assert.NotNull(shown);
        Assert.Same(window, Assert.Single(window.App.Windows));
        Assert.Equal(before + 1, window.Document.Document.Tracks.Count);
        Assert.Empty(dialogs.Messages);
    }
}
