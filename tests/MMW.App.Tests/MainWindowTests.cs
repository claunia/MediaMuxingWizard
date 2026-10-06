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
