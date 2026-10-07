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
    private static async Task<(MainWindowViewModel Window, FakeDialogService Dialogs, string Dir)> OpenMkvWithSubRipAsync()
    {
        MediaProbe.RequireFfmpeg();
        MMW.Media.Remux.MediaRemux.EnsureRegistered();
        var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var srt = Path.Combine(dir, "subs.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,200 --> 00:00:01,000\nHello\n", TestContext.Current.CancellationToken);
        var mkv = Path.Combine(dir, "movie.mkv");
        Fixtures.Run("ffmpeg", $"-y -v error -f lavfi -i testsrc=duration=2:size=160x120:rate=25 -i {Fixtures.Quote(srt)} -c:v libx264 -preset ultrafast -c:s srt -f matroska {Fixtures.Quote(mkv)}");
        var dialogs = new FakeDialogService();
        var window = new MainWindowViewModel(new DocumentService(), dialogs, new SettingsService(Path.Combine(dir, "settings.json")));
        await window.OpenPathsAsync([mkv]);
        return (window, dialogs, dir);
    }

    [AvaloniaFact]
    public async Task Incompatible_tracks_stop_save_as_until_they_are_converted()
    {
        var (window, dialogs, dir) = await OpenMkvWithSubRipAsync();
        var doc = window.Document!;
        var m4v = Path.Combine(dir, "movie.m4v");

        dialogs.DialogResults.Enqueue(new SaveOptions { OutputPath = m4v });
        await doc.SaveAsCommand.ExecuteAsync(null);
        var error = Assert.Single(dialogs.Messages);
        Assert.StartsWith(Strings.Dialog_IncompatibleTracks_Title, error, StringComparison.Ordinal);
        Assert.Contains("SRT", error, StringComparison.Ordinal);
        Assert.False(File.Exists(m4v));
        Assert.Equal(ContainerKind.Matroska, doc.Document.Container);

        // Converting the subtitles in the document window to a format MP4 takes (WebVTT) lets Save As through.
        var subtitle = doc.Document.Tracks.OfType<SubtitleTrack>().Single();
        await doc.SetConversionAsync(subtitle, new ImportChoice(ImportAction.ConvertToWebVtt, "WebVTT"));
        dialogs.DialogResults.Enqueue(new SaveOptions { OutputPath = m4v });
        await doc.SaveAsCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Messages);
        Assert.True(File.Exists(m4v));
        Assert.Equal(ContainerKind.Mp4, doc.Document.Container);
    }

    [AvaloniaFact]
    public async Task Deleting_the_incompatible_tracks_lets_save_as_through()
    {
        var (window, dialogs, dir) = await OpenMkvWithSubRipAsync();
        var doc = window.Document!;
        var m4v = Path.Combine(dir, "movie.m4v");
        dialogs.DialogResults.Enqueue(new SaveOptions { OutputPath = m4v });
        await doc.SaveAsCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Messages);

        doc.SelectedRow = doc.Rows.First(r => r.Track is SubtitleTrack);
        doc.DeleteTracksCommand.Execute(null);
        dialogs.DialogResults.Enqueue(new SaveOptions { OutputPath = m4v });
        await doc.SaveAsCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Messages);
        var streams = MediaProbe.Streams(m4v);
        Assert.Equal("h264", Assert.Single(streams).Codec);
    }
}
