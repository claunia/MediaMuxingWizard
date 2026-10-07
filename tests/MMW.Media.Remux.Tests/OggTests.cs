using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Formats.Ogg;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.OpusTests;

namespace MMW.Media.Remux.Tests;

/// <summary>Ogg import: Opus, Vorbis and FLAC streams copied to Matroska and MP4 decode to the same samples.</summary>
public sealed class OggTests
{
    /// <summary>Three seconds of a tone as WAV, the input of the external encoders.</summary>
    private static string Wave(string layout, int rate) =>
        Fixtures.Get($"ogg-{layout}-{rate}.wav", "ffmpeg",
            $"-v error -y -f lavfi -i sine=f=440:d=3:sample_rate={rate} -af aformat=channel_layouts={layout} {{out}}");

    /// <summary>libvorbis (oggenc): several modes with both block sizes.</summary>
    private static string Vorbis(string layout)
    {
        MediaProbe.RequireFfmpeg();
        if (!Fixtures.HasTool("oggenc"))
            Assert.Skip("oggenc not installed.");
        return Fixtures.Get($"vorbis-{layout}.ogg", "oggenc", $"-Q -q 3 -o {{out}} {Fixtures.Quote(Wave(layout, 44100))}");
    }

    /// <summary>Ogg FLAC as FFmpeg writes it (one header packet count) or as the flac tool does.</summary>
    private static string Flac(string layout, bool reference)
    {
        MediaProbe.RequireFfmpeg();
        if (!reference)
            return Fixtures.Get($"flac-{layout}.oga", "ffmpeg", $"-v error -y -i {Fixtures.Quote(Wave(layout, 48000))} -c:a flac {{out}}");
        if (!Fixtures.HasTool("flac"))
            Assert.Skip("flac not installed.");
        return Fixtures.Get($"flac-{layout}-ref.oga", "flac", $"-s -f --ogg -o {{out}} {Fixtures.Quote(Wave(layout, 48000))}");
    }

    private static async Task RoundTripAsync(string source, ContainerKind target)
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

    [Theory]
    [InlineData("mono", ContainerKind.Matroska)]
    [InlineData("stereo", ContainerKind.Mp4)]
    [InlineData("5.1", ContainerKind.Matroska)]
    [InlineData("7.1", ContainerKind.Mp4)]
    public Task Opus_keeps_its_pre_skip_and_end_trim(string layout, ContainerKind target) => RoundTripAsync(Ogg(layout), target);

    [Theory]
    [InlineData("stereo")]
    [InlineData("5.1")]
    public Task Vorbis_keeps_its_packet_durations(string layout) => RoundTripAsync(Vorbis(layout), ContainerKind.Matroska);

    [Theory]
    [InlineData("stereo", false, ContainerKind.Matroska)]
    [InlineData("5.1", false, ContainerKind.Mp4)]
    [InlineData("stereo", true, ContainerKind.Mp4)]
    [InlineData("stereo", true, ContainerKind.Matroska)]
    public Task Flac_keeps_its_frames(string layout, bool reference, ContainerKind target) => RoundTripAsync(Flac(layout, reference), target);

    [Fact]
    public void Reads_the_opus_timeline()
    {
        OggFormat.Register();
        using var demuxer = OggDemuxer.Open(Ogg("stereo"));
        var track = Assert.Single(demuxer.Tracks);
        Assert.Equal((CodecType.Opus, 2, 48000u), (track.Config.Codec, track.Config.Channels, track.Config.Timescale));
        Assert.Equal(312, track.MediaStart); // libopus pre-skip
        Assert.Equal(3.0, track.Duration.TotalSeconds, 3);
        MediaSample? last = null;
        while (track.ReadNext() is { } sample)
            last = sample;
        Assert.True(last!.TrimEnd > 0); // the last packet is padded past the end
    }

    [Fact]
    public void Reads_the_vorbis_headers()
    {
        using var demuxer = OggDemuxer.Open(Vorbis("5.1"));
        var track = Assert.Single(demuxer.Tracks);
        Assert.Equal((CodecType.Vorbis, 6, 44100u), (track.Config.Codec, track.Config.Channels, track.Config.Timescale));
        Assert.Equal(2, track.Config.Extradata![0]); // three Xiph-laced headers
        Assert.Equal(3.0, track.Duration.TotalSeconds, 3);
    }
}
