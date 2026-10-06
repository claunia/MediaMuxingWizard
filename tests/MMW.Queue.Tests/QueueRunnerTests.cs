using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.Formats.Matroska;
using MMW.Formats.Mp4;
using MMW.TestSupport;

namespace MMW.Queue.Tests;

public class QueueRunnerTests
{
    private static ContainerRegistry Registry() => new([new Mp4Handler(), new MatroskaHandler()]);

    private static string Fixture()
    {
        var path = Path.Combine(Fixtures.GeneratedDirectory, "mp4-moov-end.mp4");
        if (!File.Exists(path))
            Assert.Skip("Run the MP4 format tests first to generate fixtures.");
        return Fixtures.CopyToTemp(path);
    }

    private static MetadataPreset TvPreset()
    {
        var set = new MetadataSet();
        set.Set(TagId.TvShow, "Queue Show");
        set.Set(TagId.TvSeason, 1);
        set.Set(TagId.TvEpisodeNumber, 3);
        set.Set(TagId.MediaKind, TagCatalog.MediaKindTvShow);
        return MetadataPreset.FromSet("TV", set, false, false);
    }

    [Fact]
    public async Task Runs_actions_and_writes_to_the_output_folder()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Fixture();
        var folder = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
        var runner = new QueueRunner(Registry())
        {
            Options = new QueueOptions
            {
                Location = OutputLocation.Folder,
                Folder = folder,
                FileType = ".m4v",
                DefaultActions = [new ApplyPresetAction { Preset = TvPreset() }, new SetOutputFileNameAction(), new OrganizeGroupsAction()],
            },
        };
        var item = Assert.Single(runner.Add([source]));
        var finished = false;
        runner.RunFinished += (_, r) => finished = r.Completed == 1 && r.Failed == 0;

        await runner.RunAsync(ct);

        Assert.True(finished, item.Error);
        Assert.Equal(QueueItemStatus.Completed, item.Status);
        var expected = Path.Combine(folder, "Queue Show s01e03.m4v");
        Assert.Equal(expected, item.DestinationPath);
        var doc = await new Mp4Handler().ReadAsync(expected, ct);
        Assert.Equal("Queue Show", doc.Metadata.GetString(TagId.TvShow));
        Assert.Equal(1, doc.Tracks.OfType<AudioTrack>().Count(a => a.Enabled));
    }

    [Fact]
    public async Task Edits_in_place_when_the_destination_is_the_source()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Fixture();
        var runner = new QueueRunner(Registry()) { Options = new QueueOptions { DefaultActions = [new ClearMetadataAction(), new ClearTrackNamesAction()] } };
        var item = runner.Add([source]).Single();

        await runner.RunAsync(ct);

        Assert.Equal(QueueItemStatus.Completed, item.Status);
        Assert.Equal(source, item.DestinationPath);
        Assert.Equal(0, (await new Mp4Handler().ReadAsync(source, ct)).Metadata.Count);
    }

    [Fact]
    public async Task Failures_are_reported_per_item_and_the_queue_continues()
    {
        var ct = TestContext.Current.CancellationToken;
        var bogus = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N") + ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(bogus)!);
        await File.WriteAllBytesAsync(bogus, new byte[64], ct);
        var runner = new QueueRunner(Registry());
        runner.Add([bogus, Fixture()]);

        await runner.RunAsync(ct);

        Assert.Equal(QueueItemStatus.Failed, runner.Items[0].Status);
        Assert.NotNull(runner.Items[0].Error);
        Assert.Equal(QueueItemStatus.Completed, runner.Items[1].Status);
    }

    [Fact]
    public void Queue_and_actions_round_trip_through_json()
    {
        var runner = new QueueRunner(Registry())
        {
            Options = new QueueOptions
            {
                FileType = ".mkv",
                DefaultActions = [new ApplyPresetAction { Preset = TvPreset() }, new EnableTrackWithLanguageAction { Kind = TrackKind.Subtitle, Language = "fr" }],
            },
        };
        runner.Add(["/tmp/a.mp4", "/tmp/b.mkv"]);
        runner.Items[1].Status = QueueItemStatus.Working;
        var path = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"), "queue.json");

        QueueStore.Save(runner, path);
        var loaded = new QueueRunner(Registry());
        QueueStore.Load(loaded, path);

        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal(QueueItemStatus.Ready, loaded.Items[1].Status); // interrupted work runs again
        Assert.Equal(".mkv", loaded.Options.FileType);
        var enable = Assert.IsType<EnableTrackWithLanguageAction>(loaded.Items[0].Actions[1]);
        Assert.Equal(TrackKind.Subtitle, enable.Kind);
        Assert.Equal("Queue Show", Assert.IsType<ApplyPresetAction>(loaded.Items[0].Actions[0]).Preset.ToSet().GetString(TagId.TvShow));
    }
}
