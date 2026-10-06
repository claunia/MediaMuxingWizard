using System.Text.Json;
using MMW.TestSupport;

namespace MMW.Cli.Tests;

public class CommandLineTests
{
    private static string Fixture()
    {
        var path = Path.Combine(Fixtures.GeneratedDirectory, "mp4-moov-end.mp4");
        if (!File.Exists(path))
            Assert.Skip("Run the MP4 format tests first to generate fixtures.");
        return Fixtures.CopyToTemp(path);
    }

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var queue = Path.Combine(Path.GetTempPath(), "mmw-tests", "cli-queue-" + Guid.NewGuid().ToString("N") + ".json");
        var code = await CommandLine.RunAsync(args, output, error, queue);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Set_tags_then_read_them_as_json()
    {
        var file = Fixture();
        var (code, _, err) = await Run("set", file, "Name=CLI Movie", "Media Kind=Movie", "Cast=A, B", "TV Season=3");
        Assert.True(code == 0, err);

        var (_, json, _) = await Run("info", file, "--json");
        var root = JsonDocument.Parse(json).RootElement;
        var tags = root.GetProperty("tags");
        Assert.Equal("CLI Movie", tags.GetProperty("Name").GetString());
        Assert.Equal("Movie", tags.GetProperty("Media Kind").GetString());
        Assert.Equal("A, B", tags.GetProperty("Cast").GetString());
        Assert.Equal(4, root.GetProperty("tracks").GetArrayLength());
    }

    [Fact]
    public async Task Chapters_and_track_actions()
    {
        var file = Fixture();
        Assert.Equal(0, (await Run("chapters", file, "--every", "0.02")).Code);
        Assert.Equal(0, (await Run("tracks", file, "--organize-groups", "--track", "2", "--name", "Main", "--language", "deu")).Code);

        var root = JsonDocument.Parse((await Run("info", file, "--json")).Out).RootElement;
        Assert.True(root.GetProperty("chapters").GetArrayLength() >= 2);
        var track = root.GetProperty("tracks").EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == 2);
        Assert.Equal("Main", track.GetProperty("name").GetString());
        Assert.Equal("de", track.GetProperty("language").GetString());
        Assert.Equal(1, track.GetProperty("alternateGroup").GetInt32());

        var export = Path.ChangeExtension(file, ".chapters.txt");
        Assert.Equal(0, (await Run("chapters", file, "--export", export)).Code);
        Assert.Contains("CHAPTER01=", await File.ReadAllTextAsync(export, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Errors_are_reported_with_exit_codes()
    {
        Assert.Equal(2, (await Run("frobnicate")).Code);
        var (code, _, err) = await Run("set", Fixture(), "Nonsense=1");
        Assert.Equal(2, code);
        Assert.Contains("Unknown tag", err, StringComparison.Ordinal);
        Assert.Equal(1, (await Run("info", "/nonexistent/file.mp4")).Code);
    }

    [Fact]
    public async Task Queue_add_and_start()
    {
        var file = Fixture();
        var output = new StringWriter();
        var queue = Path.Combine(Path.GetTempPath(), "mmw-tests", "cli-queue-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Equal(0, await CommandLine.RunAsync(["queue", "add", file], output, TextWriter.Null, queue));
        Assert.Equal(0, await CommandLine.RunAsync(["queue", "start"], output, TextWriter.Null, queue));
        Assert.Contains("Completed", output.ToString(), StringComparison.Ordinal);
    }
}

public class MediaCommandTests
{
    private static string Fixture()
    {
        var path = Path.Combine(Fixtures.GeneratedDirectory, "mp4-moov-end.mp4");
        if (!File.Exists(path))
            Assert.Skip("Run the MP4 format tests first to generate fixtures.");
        return Fixtures.CopyToTemp(path);
    }

    private static async Task<(int Code, string Out)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CommandLine.RunAsync(args, output, error, Path.Combine(Path.GetTempPath(), "mmw-tests", "q-" + Guid.NewGuid().ToString("N") + ".json"));
        return (code, output + error.ToString());
    }

    [Fact]
    public async Task Nfo_export_then_import_round_trips_tags()
    {
        var file = Fixture();
        Assert.Equal(0, (await Run("set", file, "Name=Exported", "Genre=Drama", "Director=Dee")).Code);
        var nfo = Path.ChangeExtension(file, ".nfo");
        Assert.Equal(0, (await Run("nfo", file, "--export", nfo)).Code);
        Assert.Equal(0, (await Run("clear-tags", file)).Code);

        var (code, output) = await Run("nfo", file, "--import");
        Assert.True(code == 0, output);
        var info = (await Run("tags", file)).Out;
        Assert.Contains("Name: Exported", info, StringComparison.Ordinal);
        Assert.Contains("Genre: Drama", info, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_srt_and_remux_to_matroska()
    {
        var file = Fixture();
        var srt = Path.ChangeExtension(file, ".srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,100 --> 00:00:01,000\nHi\n", TestContext.Current.CancellationToken);
        var (code, output) = await Run("import", file, srt, "--language", "de");
        Assert.True(code == 0, output);

        var mkv = Path.ChangeExtension(file, ".mkv");
        (code, output) = await Run("remux", file, mkv);
        Assert.True(code == 0, output);
        var info = (await Run("info", mkv)).Out;
        Assert.Contains("Matroska", info, StringComparison.Ordinal);
        Assert.Contains("German", info, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_requires_a_configured_provider()
    {
        var file = Fixture();
        var (code, output) = await Run("search", file, "--provider", "Nonexistent");
        Assert.Equal(2, code);
        Assert.Contains("Unknown provider", output, StringComparison.Ordinal);
    }
}
