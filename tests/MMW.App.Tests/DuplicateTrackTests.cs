using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MMW.App.Services;
using MMW.App.ViewModels;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Media.Remux;
using MMW.TestSupport;

namespace MMW.App.Tests;

/// <summary>"Duplicate Track" on subtitle tracks, and the conversion choices of pending tracks.</summary>
public class DuplicateTrackTests
{
    private const string Srt = "1\n00:00:00,500 --> 00:00:01,500\nHello\n\n2\n00:00:02,000 --> 00:00:02,800\nWorld\n";

    private static string SrtFile()
    {
        Directory.CreateDirectory(Fixtures.GeneratedDirectory);
        var path = Path.Combine(Fixtures.GeneratedDirectory, "duplicate.srt");
        if (!File.Exists(path) || File.ReadAllText(path) != Srt)
            File.WriteAllText(path, Srt);
        return path;
    }

    /// <summary>MKV with an AAC track and a Spanish SRT track named "Dialogue".</summary>
    private static string MkvWithSrt()
    {
        if (!Fixtures.HasTool("ffmpeg"))
            Assert.Skip("The ffmpeg tool is not available.");
        var srt = SrtFile();
        return Fixtures.CopyToTemp(Fixtures.Get("duplicate-aac-srt.mkv", "ffmpeg",
            $"-y -v error -f lavfi -i sine=f=440:d=3 -i {Fixtures.Quote(srt)} -map 0:a -map 1:s -c:a aac -c:s srt " +
            "-metadata:s:s:0 language=spa -metadata:s:s:0 title=Dialogue -f matroska {out}"));
    }

    private static async Task<DocumentViewModel> OpenAsync(string path)
    {
        MediaRemux.EnsureRegistered();
        var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
        var main = new MainWindowViewModel(new DocumentService(), new FakeDialogService(), new SettingsService(Path.Combine(dir, "settings.json")));
        await main.OpenPathsAsync([path]);
        return main.Documents.Single();
    }

    private static async Task Pump(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static TrackRowViewModel Row(DocumentViewModel doc, Track track) => doc.Rows.First(r => r.Track == track);

    [AvaloniaFact]
    public async Task Duplicate_is_offered_for_one_selected_subtitle_track()
    {
        var path = MkvWithSrt();
        var doc = await OpenAsync(path);
        var sub = doc.Document.Tracks.OfType<SubtitleTrack>().Single();
        var audio = doc.Document.Tracks.OfType<AudioTrack>().Single();

        Assert.False(doc.DuplicateTrackCommand.CanExecute(null)); // metadata row
        doc.SelectedRow = Row(doc, audio);
        Assert.False(doc.DuplicateTrackCommand.CanExecute(null));
        doc.SelectedRow = Row(doc, sub);
        Assert.True(doc.DuplicateTrackCommand.CanExecute(null));

        doc.SelectedRows = [Row(doc, sub), Row(doc, audio)];
        Assert.False(doc.DuplicateTrackCommand.CanExecute(null));
        doc.SelectedRows = [Row(doc, sub)];
        Assert.True(doc.DuplicateTrackCommand.CanExecute(null));

        doc.IsBusy = true;
        Assert.False(doc.DuplicateTrackCommand.CanExecute(null));
        doc.IsBusy = false;
        Assert.True(doc.DuplicateTrackCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Track_in_the_file_is_duplicated_as_a_pending_track_and_undone()
    {
        var path = MkvWithSrt();
        var doc = await OpenAsync(path);
        var sub = doc.Document.Tracks.OfType<SubtitleTrack>().Single();
        sub.IsForced = true;
        sub.MediaCharacteristics.Add("public.accessibility.describes-music-and-sound");
        var count = doc.Document.Tracks.Count;
        var index = doc.Document.Tracks.IndexOf(sub);
        doc.SelectedRow = Row(doc, sub);

        await doc.DuplicateTrackCommand.ExecuteAsync(null);

        Assert.Equal(count + 1, doc.Document.Tracks.Count);
        var copy = Assert.IsType<SubtitleTrack>(doc.Document.Tracks[index + 1]);
        Assert.NotSame(sub, copy);
        Assert.True(copy.IsPending);
        Assert.False(sub.IsPending);
        Assert.Equal("Dialogue", copy.Name);
        Assert.Equal(sub.Language, copy.Language);
        Assert.Equal(sub.Format, copy.Format);
        Assert.True(copy.IsForced);
        Assert.Equal(sub.MediaCharacteristics, copy.MediaCharacteristics);
        Assert.Equal(sub.AlternateGroup, copy.AlternateGroup);
        Assert.False(copy.Enabled);
        Assert.False(copy.IsDefault);
        Assert.True(sub.Enabled);

        // Read again from the document's file, with the importer's suggestion for Matroska (SRT is kept).
        var source = Assert.IsType<TrackSource>(copy.Source);
        Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(source.Path));
        Assert.Equal(sub.Source!.TrackId, source.TrackId);
        Assert.Equal(ImportAction.Passthrough, source.Import?.Action);
        Assert.Null(sub.Source.Import);

        // The copy is listed (after the original) and selected.
        Assert.Same(copy, doc.SelectedRow?.Track);
        Assert.Equal(doc.Rows.IndexOf(Row(doc, sub)) + 1, doc.Rows.IndexOf(Row(doc, copy)));
        Assert.True(doc.IsDirty);

        doc.UndoCommand.Execute(null);
        Assert.Equal(count, doc.Document.Tracks.Count);
        Assert.DoesNotContain(copy, doc.Document.Tracks);
        Assert.DoesNotContain(doc.Rows, r => r.Track == copy);

        doc.RedoCommand.Execute(null);
        Assert.Same(copy, doc.Document.Tracks[index + 1]);
    }

    [AvaloniaFact]
    public async Task Pending_track_is_duplicated_with_its_own_import_settings()
    {
        var path = MkvWithSrt();
        var doc = await OpenAsync(path);
        var imported = await TrackImporter.InspectAsync(SrtFile(), doc.Document.Container, TestContext.Current.CancellationToken);
        imported[0].Name = "Imported";
        var pending = Assert.IsType<SubtitleTrack>(Assert.Single(TrackImporter.AddToDocument(doc.Document, imported)));
        doc.SelectedRow = Row(doc, pending);

        await doc.DuplicateTrackCommand.ExecuteAsync(null);

        var copy = Assert.IsType<SubtitleTrack>(doc.Document.Tracks[doc.Document.Tracks.IndexOf(pending) + 1]);
        Assert.True(copy.IsPending);
        Assert.Equal("Imported", copy.Name);
        Assert.Equal(pending.Source, copy.Source); // same file, track and import options
        Assert.False(copy.Enabled);

        // A conversion chosen for the copy leaves the original's alone.
        await doc.SetConversionAsync(copy, new ImportChoice(ImportAction.ConvertToTx3g, "Tx3g"));
        Assert.Equal(ImportAction.ConvertToTx3g, copy.Source!.Import!.Action);
        Assert.Equal(ImportAction.Passthrough, pending.Source!.Import!.Action);
        Assert.Equal(pending.Source.Path, copy.Source.Path);

        doc.UndoCommand.Execute(null);
        Assert.Equal(ImportAction.Passthrough, copy.Source!.Import!.Action);
        doc.UndoCommand.Execute(null);
        Assert.DoesNotContain(copy, doc.Document.Tracks);
        Assert.Contains(pending, doc.Document.Tracks);
    }

    [AvaloniaFact]
    public async Task Inspector_of_a_duplicate_offers_the_choices_of_its_source()
    {
        var path = MkvWithSrt();
        var doc = await OpenAsync(path);
        var sub = doc.Document.Tracks.OfType<SubtitleTrack>().Single();
        doc.SelectedRow = Row(doc, sub);
        await doc.DuplicateTrackCommand.ExecuteAsync(null);
        var copy = Assert.IsType<SubtitleTrack>(doc.SelectedRow?.Track);
        Assert.True(copy.IsPending);

        var inspector = Assert.IsType<TrackInspectorViewModel>(doc.Inspector);
        await Pump(() => inspector.SelectedConversion is not null);

        var expected = (await TrackImporter.InspectAsync(path, ContainerKind.Matroska, TestContext.Current.CancellationToken))
            .Single(t => t.TrackId == copy.Source!.TrackId).Choices.Where(c => c.Action != ImportAction.Skip);
        Assert.Equal(expected, inspector.ConversionChoices);
        Assert.Equal(ImportAction.Passthrough, inspector.SelectedConversion?.Action);
        Assert.Contains(inspector.ConversionChoices, c => c.Action == ImportAction.Passthrough);

        // Choosing an entry changes the copy only.
        if (inspector.ConversionChoices.FirstOrDefault(c => c.Action != ImportAction.Passthrough) is { } other)
        {
            inspector.SelectedConversion = other;
            await Pump(() => copy.Source?.Import?.Action == other.Action);
            Assert.Equal(other.Action, copy.Source!.Import!.Action);
            Assert.Null(sub.Source!.Import);
        }
    }

    [AvaloniaFact]
    public async Task Inspector_of_an_imported_audio_track_shows_its_chosen_conversion()
    {
        MediaRemux.EnsureRegistered();
        if (!MMW.Media.Conversion.MediaConversion.IsAvailable)
            Assert.Skip("The FFmpeg libraries are not available.");
        var path = MkvWithSrt();
        var flac = Fixtures.Get("duplicate-import.flac", "ffmpeg", "-y -v error -f lavfi -i sine=f=550:d=2 -ac 2 -c:a flac -f flac {out}");
        var doc = await OpenAsync(path);
        var imported = await TrackImporter.InspectAsync(flac, doc.Document.Container, TestContext.Current.CancellationToken);
        var item = Assert.Single(imported);
        item.Choice = item.Choices.First(c => c.Action == ImportAction.ConvertToAac && c.Mixdown == AudioMixdown.Mono);
        var pending = Assert.Single(TrackImporter.AddToDocument(doc.Document, imported));

        doc.SelectedRow = Row(doc, pending);
        var inspector = Assert.IsType<TrackInspectorViewModel>(doc.Inspector);
        await Pump(() => inspector.HasConversionChoices);

        Assert.True(inspector.HasConversionChoices);
        Assert.Equal(ImportAction.ConvertToAac, inspector.SelectedConversion?.Action);
        Assert.Equal(AudioMixdown.Mono, inspector.SelectedConversion?.Mixdown);

        var passthrough = inspector.ConversionChoices.First(c => c.Action == ImportAction.Passthrough);
        inspector.SelectedConversion = passthrough;
        await Pump(() => pending.Source?.Import?.Action == ImportAction.Passthrough);
        Assert.Equal(ImportAction.Passthrough, pending.Source!.Import!.Action);
    }
}
