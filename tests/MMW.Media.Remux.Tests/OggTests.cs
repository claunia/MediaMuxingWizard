using MMW.Core.Media;
using MMW.Core.Metadata;
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

    /// <summary>A tagged three-second tone as WAV and a JPEG cover, the inputs of the tagging encoders.</summary>
    private static (string Wave, string Cover) TagInputs()
    {
        MediaProbe.RequireFfmpeg();
        return (Wave("stereo", 48000), Fixtures.Get("ogg-cover.jpg", "ffmpeg", "-v error -y -f lavfi -i color=red:s=32x32 -frames:v 1 {out}"));
    }

    /// <summary>A FLAC PICTURE block of a JPEG, base64-encoded as Ogg Vorbis/Opus comments carry it.</summary>
    private static string PictureComment(string jpeg)
    {
        var image = File.ReadAllBytes(jpeg);
        var block = new List<byte>();
        void U32(int v) => block.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        U32(3);
        U32(10);
        block.AddRange("image/jpeg"u8.ToArray());
        U32(0);
        U32(32);
        U32(32);
        U32(24);
        U32(0);
        U32(image.Length);
        block.AddRange(image);
        return Convert.ToBase64String(block.ToArray());
    }

    private static void AssertTags(string path, bool cover)
    {
        var metadata = Assert.IsType<MetadataSet>(TrackImporter.ReadMetadata(path));
        Assert.Equal("Song", metadata.GetString(TagId.Name));
        Assert.Equal("Someone", metadata.GetString(TagId.Artist));
        Assert.Equal("Record", metadata.GetString(TagId.Album));
        Assert.Equal(new IntPair(3, 12), metadata.GetPair(TagId.TrackNumber));
        Assert.Equal(cover ? 1 : 0, metadata.Artworks.Count);
        if (cover)
            Assert.Equal(ArtworkFormat.Jpeg, metadata.Artworks[0].Format);
    }

    [Fact]
    public void Reads_opus_comments()
    {
        var (wave, _) = TagInputs();
        AssertTags(Fixtures.Get("opus-tagged.opus", "ffmpeg",
            $"-v error -y -i {Fixtures.Quote(wave)} -c:a libopus -metadata title=Song -metadata artist=Someone -metadata album=Record -metadata track=3/12 {{out}}"), cover: false);
    }

    [Fact]
    public void Reads_vorbis_comments_and_picture()
    {
        var (wave, cover) = TagInputs();
        if (!Fixtures.HasTool("oggenc"))
            Assert.Skip("oggenc not installed.");
        AssertTags(Fixtures.Get("vorbis-tagged.ogg", "oggenc",
            $"-Q -t Song -a Someone -l Record -N 3 -c TRACKTOTAL=12 -c METADATA_BLOCK_PICTURE={PictureComment(cover)} -o {{out}} {Fixtures.Quote(wave)}"), cover: true);
    }

    [Fact]
    public void Reads_ogg_flac_comments_and_picture()
    {
        var (wave, cover) = TagInputs();
        if (!Fixtures.HasTool("flac"))
            Assert.Skip("flac not installed.");
        AssertTags(Fixtures.Get("flac-tagged.oga", "flac",
            $"-s -f --ogg -T TITLE=Song -T ARTIST=Someone -T ALBUM=Record -T TRACKNUMBER=3/12 --picture {Fixtures.Quote(cover)} -o {{out}} {Fixtures.Quote(wave)}"), cover: true);
    }

    [Fact]
    public void Files_without_comments_have_no_metadata() => Assert.Null(TrackImporter.ReadMetadata(TagInputs().Wave));
}
