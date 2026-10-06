using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MMW.App.Services;
using MMW.App.ViewModels;
using MMW.App.Views;
using MMW.Core.Model;
using MMW.TestSupport;

namespace MMW.App.Tests;

public class ConversionUiTests
{
    private static void RequireFFmpeg()
    {
        MMW.Media.Remux.MediaRemux.EnsureRegistered();
        if (!MMW.Media.Conversion.MediaConversion.IsAvailable || !Fixtures.HasTool("ffmpeg"))
            Assert.Skip("FFmpeg libraries or the ffmpeg tool are not available.");
    }

    private static async Task Pump(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Existing_flac_track_is_converted_to_aac_on_save()
    {
        RequireFFmpeg();
        var mkv = Fixtures.CopyToTemp(Fixtures.Get("flac-audio.mkv", "ffmpeg",
            "-y -v error -f lavfi -i testsrc=duration=2:size=160x120:rate=25 -f lavfi -i sine=f=440:d=2 -c:v libx264 -preset ultrafast -c:a flac -f matroska {out}"));
        var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
        var dialogs = new FakeDialogService();
        var main = new MainWindowViewModel(new DocumentService(), dialogs, new SettingsService(Path.Combine(dir, "settings.json")));
        await main.OpenPathsAsync([mkv]);
        var doc = main.Documents.Single();

        doc.SelectedRow = doc.Rows.First(r => r.Track is AudioTrack);
        var inspector = Assert.IsType<TrackInspectorViewModel>(doc.Inspector);
        await Pump(() => inspector.HasConversionChoices);
        Assert.True(inspector.HasConversionChoices);

        var m4v = Path.ChangeExtension(mkv, ".m4v");
        dialogs.DialogResults.Enqueue(new SaveOptions { OutputPath = m4v });
        var stereo = inspector.ConversionChoices.First(c => c.DisplayName.Contains("Stereo", StringComparison.Ordinal));
        inspector.SelectedConversion = stereo;
        Assert.True(doc.IsDirty);
        await doc.SaveAsCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.Messages);
        var saved = await new MMW.Formats.Mp4.Mp4Handler().ReadAsync(m4v, TestContext.Current.CancellationToken);
        var audio = Assert.Single(saved.Tracks.OfType<AudioTrack>());
        Assert.StartsWith("AAC", audio.Format, StringComparison.Ordinal);
        Assert.Equal(2, audio.Channels);
    }

    [AvaloniaFact]
    public async Task Chapter_thumbnails_are_captured()
    {
        RequireFFmpeg();
        var fixture = Path.Combine(Fixtures.GeneratedDirectory, "mp4-moov-end.mp4");
        if (!File.Exists(fixture))
            Assert.Skip("Run the MP4 format tests first to generate fixtures.");
        var main = new MainWindowViewModel(new DocumentService(), new FakeDialogService(),
            new SettingsService(Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"), "settings.json")));
        await main.OpenPathsAsync([Fixtures.CopyToTemp(fixture)]);
        var doc = main.Documents.Single();
        doc.SelectedRow = doc.Rows.First(r => r.Track is ChapterTrack);
        var chapters = Assert.IsType<ChaptersInspectorViewModel>(doc.Inspector);
        Assert.True(chapters.CanMakeThumbnails);

        await chapters.MakeThumbnailsCommand.ExecuteAsync(null);
        Assert.All(doc.Document.Chapters, c => Assert.NotNull(c.Thumbnail));

        var window = new Window { Content = new ChaptersInspectorView { DataContext = chapters }, Width = 700, Height = 360 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var shots = Environment.GetEnvironmentVariable("MMW_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "mmw-screenshots");
        Directory.CreateDirectory(shots);
        window.CaptureRenderedFrame()?.Save(Path.Combine(shots, "11-chapter-thumbnails.png"));
    }
}
