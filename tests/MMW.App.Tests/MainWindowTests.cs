using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MMW.App.Services;
using MMW.App.ViewModels;
using MMW.App.Views;
using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.TestSupport;

namespace MMW.App.Tests;

public class MainWindowTests
{
    private static readonly string s_screenshots = Environment.GetEnvironmentVariable("MMW_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "mmw-screenshots");

    private static (MainWindow Window, MainWindowViewModel Vm, FakeDialogService Dialogs) CreateWindow()
    {
        var dialogs = new FakeDialogService();
        var settings = new SettingsService(Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"), "settings.json"));
        var vm = new MainWindowViewModel(new DocumentService(), dialogs, settings);
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
        window.Show();
        return (window, vm, dialogs);
    }

    private static void Snapshot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(s_screenshots);
        window.CaptureRenderedFrame()?.Save(Path.Combine(s_screenshots, name + ".png"));
    }

    private static string Fixture()
    {
        var path = Path.Combine(Fixtures.GeneratedDirectory, "mp4-moov-end.mp4");
        if (!File.Exists(path))
            Assert.Skip("Run the MP4 format tests first to generate fixtures.");
        return Fixtures.CopyToTemp(path);
    }

    [AvaloniaFact]
    public void Welcome_screen_renders()
    {
        var (window, vm, _) = CreateWindow();
        Assert.True(vm.HasNoDocuments);
        Snapshot(window, "01-welcome");
    }

    [AvaloniaFact]
    public async Task Opening_a_file_shows_tracks_and_metadata_inspector()
    {
        var (window, vm, _) = CreateWindow();
        await vm.OpenPathsAsync([Fixture()]);
        Dispatcher.UIThread.RunJobs();

        var doc = Assert.Single(vm.Documents);
        Assert.Equal(6, doc.Rows.Count); // metadata + video + 2 audio + subtitle + chapters
        Assert.IsType<MetadataInspectorViewModel>(doc.Inspector);
        Snapshot(window, "02-metadata");

        doc.MetadataInspector.SelectedTab = 1;
        Snapshot(window, "03-artwork");

        doc.SelectedRow = doc.Rows[2];
        Assert.IsType<TrackInspectorViewModel>(doc.Inspector);
        Snapshot(window, "04-audio-track");

        doc.SelectedRow = doc.Rows[1];
        Snapshot(window, "05-video-track");

        doc.SelectedRow = doc.Rows[^1];
        Assert.IsType<ChaptersInspectorViewModel>(doc.Inspector);
        Snapshot(window, "06-chapters");
    }

    [AvaloniaFact]
    public async Task Tag_edits_are_undoable_and_saved()
    {
        var (_, vm, dialogs) = CreateWindow();
        var path = Fixture();
        await vm.OpenPathsAsync([path]);
        var doc = vm.Documents.Single();
        var inspector = doc.MetadataInspector;

        inspector.SetTag(TagId.Name, "Renamed");
        inspector.SetTag(TagId.Genre, "Drama");
        Assert.True(doc.IsDirty);
        doc.UndoCommand.Execute(null);
        Assert.False(doc.Document.Metadata.Contains(TagId.Genre));
        Assert.Equal("Renamed", doc.Document.Metadata.GetString(TagId.Name));

        var audio = doc.Document.Tracks.OfType<AudioTrack>().First();
        audio.Name = "Commentary";
        doc.UndoCommand.Execute(null);
        Assert.Equal(string.Empty, audio.Name);
        doc.RedoCommand.Execute(null);

        Assert.True(await doc.Save(), string.Join("\n", dialogs.Messages));
        Assert.False(doc.IsDirty);
        var reread = await new MMW.Formats.Mp4.Mp4Handler().ReadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("Renamed", reread.Metadata.GetString(TagId.Name));
        Assert.Equal("Commentary", reread.Tracks.OfType<AudioTrack>().First().Name);
    }

    [AvaloniaFact]
    public async Task Deleting_a_track_and_undoing_restores_it()
    {
        var (_, vm, _) = CreateWindow();
        await vm.OpenPathsAsync([Fixture()]);
        var doc = vm.Documents.Single();
        var before = doc.Document.Tracks.Count;

        doc.SelectedRow = doc.Rows[3];
        doc.DeleteTracksCommand.Execute(null);
        Assert.Equal(before - 1, doc.Document.Tracks.Count);
        Assert.Equal(before, doc.Rows.Count);

        doc.UndoCommand.Execute(null);
        Assert.Equal(before, doc.Document.Tracks.Count);
    }

    [AvaloniaFact]
    public async Task Closing_a_dirty_document_asks_to_save()
    {
        var (_, vm, dialogs) = CreateWindow();
        await vm.OpenPathsAsync([Fixture()]);
        var doc = vm.Documents.Single();
        doc.MetadataInspector.SetTag(TagId.Name, "x");

        dialogs.SaveChangesAnswer = SaveChangesChoice.Cancel;
        await vm.CloseDocumentCommand.ExecuteAsync(doc);
        Assert.Single(vm.Documents);

        dialogs.SaveChangesAnswer = SaveChangesChoice.Discard;
        await vm.CloseDocumentCommand.ExecuteAsync(doc);
        Assert.Empty(vm.Documents);
    }
}

public class DocumentActionTests
{
    [AvaloniaFact]
    public async Task Organize_groups_is_one_undo_step_and_multi_selection_edits_all()
    {
        var dialogs = new FakeDialogService();
        var settings = new SettingsService(Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"), "settings.json"));
        var vm = new MainWindowViewModel(new DocumentService(), dialogs, settings);
        var path = Path.Combine(Fixtures.GeneratedDirectory, "mp4-moov-end.mp4");
        if (!File.Exists(path))
            Assert.Skip("Run the MP4 format tests first to generate fixtures.");
        await vm.OpenPathsAsync([Fixtures.CopyToTemp(path)]);
        var doc = vm.Documents.Single();
        var audio = doc.Document.Tracks.OfType<AudioTrack>().ToList();

        audio[1].Enabled = true;
        var before = doc.Document.Tracks.Select(t => (t.Enabled, t.AlternateGroup, t.MediaCharacteristics.Count)).ToList();

        doc.OrganizeGroupsCommand.Execute(null);
        Assert.True(audio[0].Enabled);
        Assert.False(audio[1].Enabled);
        Assert.Equal("Organize Alternate Groups", doc.Undo.UndoDescription);
        doc.UndoCommand.Execute(null);
        Assert.Equal(before, doc.Document.Tracks.Select(t => (t.Enabled, t.AlternateGroup, t.MediaCharacteristics.Count)).ToList());

        doc.SelectedRows = doc.Rows.Where(r => r.Track is AudioTrack).ToList();
        var multi = Assert.IsType<MultiSelectionViewModel>(doc.Inspector);
        multi.SelectedLanguage = MMW.Core.Languages.LanguageTable.Find("de");
        Assert.All(audio, a => Assert.Equal("de", a.Language));
        doc.UndoCommand.Execute(null);
        Assert.Equal(["en", "fr"], audio.Select(a => a.Language));
    }
}

public class PreferencesTests
{
    [AvaloniaFact]
    public void Preferences_render_and_apply_changes()
    {
        var settings = new AppSettings();
        var vm = new PreferencesViewModel(settings);
        var window = new Window { Content = new PreferencesView { DataContext = vm }, Width = 700, Height = 540 };
        window.Show();

        vm.TvFormat = "{TV Show|dot}.S{TV Season:00}E{TV Episode #:00}";
        Assert.Equal("The.Show.S01E02.m4v", vm.TvPreview);
        vm.RatingsCountry = "UK";
        vm.AcceptCommand.Execute(null);

        Assert.Equal("UK", settings.RatingsCountry);
        Assert.Equal("{TV Show|dot}.S{TV Season:00}E{TV Episode #:00}", settings.TvFileNameFormat);

        Dispatcher.UIThread.RunJobs();
        var dir = Environment.GetEnvironmentVariable("MMW_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "mmw-screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, "07-preferences.png"));
    }
}

public class QueueWindowTests
{
    [AvaloniaFact]
    public async Task Queue_options_become_default_actions_and_items_run()
    {
        var path = Path.Combine(Fixtures.GeneratedDirectory, "mp4-moov-end.mp4");
        if (!File.Exists(path))
            Assert.Skip("Run the MP4 format tests first to generate fixtures.");
        var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        var runner = new MMW.Queue.QueueRunner(new ContainerRegistry(DocumentService.DefaultHandlers()));
        var vm = new QueueViewModel(runner, new FakeDialogService(), settings, new NullNotifications(), Path.Combine(dir, "queue.json"));

        vm.OrganizeGroups = true;
        vm.ClearTrackNames = true;
        vm.CompleteLanguages = true;
        Assert.Equal(4, runner.Options.DefaultActions.Count); // + the automatic "prepare tracks" step

        var window = new QueueWindow { DataContext = vm, Width = 1000, Height = 640 };
        window.Show();
        vm.AddFiles([Fixtures.CopyToTemp(path)]);
        Assert.Equal(4, runner.Items[0].Actions.Count);
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(MMW.Queue.QueueItemStatus.Completed, runner.Items[0].Status);
        Assert.True(File.Exists(Path.Combine(dir, "queue.json")));

        Dispatcher.UIThread.RunJobs();
        var shots = Environment.GetEnvironmentVariable("MMW_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "mmw-screenshots");
        Directory.CreateDirectory(shots);
        window.CaptureRenderedFrame()?.Save(Path.Combine(shots, "08-queue.png"));
    }

    private sealed class NullNotifications : INotificationService
    {
        public void Notify(string title, string message)
        {
        }
    }
}

public class ImportTests
{
    [AvaloniaFact]
    public async Task Importing_an_srt_adds_a_subtitle_track_that_is_saved()
    {
        var fixture = Path.Combine(Fixtures.GeneratedDirectory, "mp4-moov-end.mp4");
        if (!File.Exists(fixture))
            Assert.Skip("Run the MP4 format tests first to generate fixtures.");
        MMW.Media.Remux.MediaRemux.EnsureRegistered();
        var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var srt = Path.Combine(dir, "extra.fr.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,200 --> 00:00:01,000\nBonjour\n\n2\n00:00:01,500 --> 00:00:02,500\nAu revoir\n", TestContext.Current.CancellationToken);
        var main = new MainWindowViewModel(new DocumentService(), new FakeDialogService(), new SettingsService(Path.Combine(dir, "settings.json")));
        var media = Fixtures.CopyToTemp(fixture);
        await main.OpenPathsAsync([media]);
        var doc = main.Documents.Single();
        var before = doc.Document.Tracks.OfType<SubtitleTrack>().Count();

        var dialog = new ImportDialogViewModel(doc, [srt]);
        await dialog.LoadAsync();
        var track = Assert.Single(dialog.AllTracks);
        Assert.True(track.CanImport);
        track.Selected = true;
        track.Language = MMW.Core.Languages.LanguageTable.Find("fr")!;

        var window = new Window { Content = new ImportDialogView { DataContext = dialog }, Width = 940, Height = 580 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var shots = Environment.GetEnvironmentVariable("MMW_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "mmw-screenshots");
        Directory.CreateDirectory(shots);
        window.CaptureRenderedFrame()?.Save(Path.Combine(shots, "10-import.png"));

        await dialog.ImportCommand.ExecuteAsync(null);
        Assert.Equal(before + 1, doc.Document.Tracks.OfType<SubtitleTrack>().Count());
        Assert.Contains(doc.Rows, r => r.IdText == "na");

        Assert.True(await doc.Save());
        var reread = await new MMW.Formats.Mp4.Mp4Handler().ReadAsync(media, TestContext.Current.CancellationToken);
        Assert.Equal(before + 1, reread.Tracks.OfType<SubtitleTrack>().Count());
        Assert.Contains(reread.Tracks.OfType<SubtitleTrack>(), s => s.Language == "fr");
        Assert.DoesNotContain(doc.Rows, r => r.IdText == "na");
    }
}

public class OffsetTests
{
    [AvaloniaFact]
    public async Task Offsetting_audio_delays_it_in_the_saved_file()
    {
        var fixture = Path.Combine(Fixtures.GeneratedDirectory, "mp4-moov-end.mp4");
        if (!File.Exists(fixture) || !Fixtures.HasTool("ffprobe"))
            Assert.Skip("Needs the MP4 fixtures and ffprobe.");
        var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
        var dialogs = new FakeDialogService();
        var main = new MainWindowViewModel(new DocumentService(), dialogs, new SettingsService(Path.Combine(dir, "settings.json")));
        var media = Fixtures.CopyToTemp(fixture);
        await main.OpenPathsAsync([media]);
        var doc = main.Documents.Single();

        doc.SelectedRow = doc.Rows.First(r => r.Track is AudioTrack);
        var inspector = Assert.IsType<TrackInspectorViewModel>(doc.Inspector);
        inspector.StartOffsetMs = 500;
        Assert.True(await doc.Save(), string.Join("\n", dialogs.Messages));

        var json = Fixtures.Run("ffprobe", $"-v error -select_streams a:0 -show_entries stream=start_time -of csv=p=0 {Fixtures.Quote(media)}");
        var start = double.Parse(json.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(start, 0.45, 0.55);
        Assert.Equal(TimeSpan.Zero, doc.Document.Tracks.OfType<AudioTrack>().First().StartOffset);
    }
}

public class SingleInstanceTests
{
    [Fact]
    public async Task Paths_are_forwarded_to_the_running_instance()
    {
        SingleInstance.PipeName = "mmw-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        var received = new TaskCompletionSource<IReadOnlyList<string>>();
        SingleInstance.StartServer(p => received.TrySetResult(p), cts.Token);

        var forwarded = false;
        for (var i = 0; i < 20 && !forwarded; i++)
        {
            forwarded = SingleInstance.TryForward(["/tmp/a.mkv", "/tmp/b c.m4v"]);
            if (!forwarded)
                await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(forwarded);
        var paths = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["/tmp/a.mkv", "/tmp/b c.m4v"], paths);
        cts.Cancel();
    }
}

public class OcrAdviceTests
{
    [Fact]
    public void Ocr_choices_trigger_the_subtitle_edit_advice()
    {
        Assert.True(OcrAdvice.IsOcr(new MMW.Core.Media.ImportChoice(MMW.Core.Media.ImportAction.ConvertToTx3g, "Tx3g (OCR)")));
        Assert.False(OcrAdvice.IsOcr(new MMW.Core.Media.ImportChoice(MMW.Core.Media.ImportAction.ConvertToTx3g, "Tx3g")));
        Assert.Contains("Subtitle Edit", OcrAdvice.Warning, StringComparison.Ordinal);
    }
}
