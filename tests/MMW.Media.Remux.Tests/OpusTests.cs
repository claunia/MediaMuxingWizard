using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>Opus: end trimming (encoder padding), channel mapping and pre-skip through Matroska and MP4.</summary>
public sealed class OpusTests
{
    /// <summary>Three seconds of Opus in Ogg from libopus (pre-skip 312, the last packet padded).</summary>
    internal static string Ogg(string layout)
    {
        MediaProbe.RequireFfmpeg();
        return Fixtures.Get($"opus-{layout}.opus", "ffmpeg",
            $"-v error -y -f lavfi -i sine=f=440:d=3:sample_rate=48000 -af aformat=channel_layouts={layout} -c:a libopus -b:a 128k {{out}}");
    }

    /// <summary>The same stream copied to Matroska by FFmpeg: DiscardPadding on the last block.</summary>
    private static string Matroska(string layout) =>
        Fixtures.Get($"opus-{layout}.mkv", "ffmpeg", $"-v error -y -i {Fixtures.Quote(Ogg(layout))} -c copy {{out}}");

    /// <summary>MD5 and length of the first audio track decoded by FFmpeg (32-bit float).</summary>
    internal static (string Md5, long Bytes) Pcm(string path)
    {
        var pcm = MediaProbe.TempPath(".pcm");
        try
        {
            Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(path)} -map 0:a:0 -f f32le {Fixtures.Quote(pcm)}");
            using var stream = File.OpenRead(pcm);
            return (Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream)), new FileInfo(pcm).Length);
        }
        finally
        {
            MediaProbe.Delete(pcm);
        }
    }

    /// <summary>Imports the audio of <paramref name="source"/> unchanged (Opus passthrough) into <paramref name="target"/>.</summary>
    internal static async Task<string> PassthroughAsync(string source, ContainerKind target)
    {
        var tracks = await TrackImporter.InspectAsync(source, target, Ct);
        var audio = Assert.Single(tracks, t => t.Config.Kind == TrackKind.Audio);
        audio.Choice = audio.Choices.First(c => c.Action == ImportAction.Passthrough);
        var doc = new MediaDocument(null, target);
        TrackImporter.AddToDocument(doc, [audio]);
        var output = MediaProbe.TempPath(target == ContainerKind.Mp4 ? ".mp4" : ".mkv");
        await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
        return output;
    }

    [Theory]
    [InlineData("stereo", ContainerKind.Matroska)]
    [InlineData("stereo", ContainerKind.Mp4)]
    [InlineData("5.1", ContainerKind.Mp4)]
    [InlineData("7.1", ContainerKind.Matroska)]
    public async Task Matroska_opus_keeps_its_exact_length(string layout, ContainerKind target)
    {
        var source = Matroska(layout);
        var output = await PassthroughAsync(source, target);
        string? back = null;
        try
        {
            Assert.Equal(Pcm(source), Pcm(output)); // the encoder padding stays trimmed, the pre-skip applied once
            Assert.Equal(layout, Fixtures.Run("ffprobe", $"-v error -select_streams a -show_entries stream=channel_layout -of csv=p=0 {Fixtures.Quote(output)}").Trim().TrimEnd(','));

            // And back to Matroska: the trim read from the other container is written again.
            back = await PassthroughAsync(output, ContainerKind.Matroska);
            Assert.Equal(Pcm(source), Pcm(back));
        }
        finally
        {
            MediaProbe.Delete(output, back ?? string.Empty);
        }
    }

    /// <summary>
    /// The Ogg stream copied to MPEG-TS by FFmpeg: the pre-skip and padding become the control headers' start and end
    /// trims, the channel configuration the Opus extension descriptor.
    /// </summary>
    [Theory]
    [InlineData("stereo", ContainerKind.Matroska)]
    [InlineData("5.1", ContainerKind.Mp4)]
    [InlineData("7.1", ContainerKind.Matroska)]
    public async Task Transport_stream_opus_keeps_its_trims(string layout, ContainerKind target)
    {
        var source = Ogg(layout);
        var ts = Fixtures.Get($"opus-{layout}.ts", "ffmpeg", $"-v error -y -i {Fixtures.Quote(source)} -c copy {{out}}");
        var output = await PassthroughAsync(ts, target);
        try
        {
            Assert.Equal(Pcm(source), Pcm(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Corpus_opus_keeps_its_exact_length()
    {
        MediaProbe.RequireFfmpeg();
        var source = Corpus.Directory is { } dir ? Path.Combine(dir, "Audio codecs", "Opus.mkv") : string.Empty;
        Corpus.Require(File.Exists(source) ? source : string.Empty);
        foreach (var target in new[] { ContainerKind.Matroska, ContainerKind.Mp4 })
        {
            var output = await PassthroughAsync(source, target);
            try
            {
                Assert.Equal(Pcm(source), Pcm(output));
            }
            finally
            {
                MediaProbe.Delete(output);
            }
        }
    }
}
