using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// Codecs Matroska has no own ID for (H.263, Dirac, DNxHD, VC-1, AMR) are stored as mkvmerge stores them from MP4/MOV:
/// V_QUICKTIME / A_QUICKTIME with the sample entry as CodecPrivate; read back, they go to MP4 unchanged.
/// </summary>
public sealed class QuickTimeCodecTests
{
    private static string Make(string name, string options)
    {
        MediaProbe.RequireFfmpeg();
        return Fixtures.Get(name, "ffmpeg", $"-v error -y -f lavfi -i testsrc2=size=320x240:rate=25:duration=1 -f lavfi -i sine=f=440:duration=1:sample_rate=48000 {options} {{out}}");
    }

    private static string Decode(string path) =>
        Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0:v:0 -fps_mode passthrough -f framemd5 -")
            .Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(',').Last()).Aggregate(string.Empty, (a, b) => a + b + "\n");

    private static async Task<string> SaveAsync(string source, ContainerKind target)
    {
        MediaRemux.EnsureRegistered();
        var tracks = await TrackImporter.InspectAsync(source, target, Ct);
        var doc = new MediaDocument(null, target);
        TrackImporter.AddToDocument(doc, tracks);
        var output = MediaProbe.TempPath(target == ContainerKind.Mp4 ? ".mp4" : ".mkv");
        await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
        return output;
    }

    [Theory]
    [InlineData("qt-h263.3gp", "-s 352x288 -c:v h263 -c:a aac -f 3gp", "s263", "H.263")]
    [InlineData("qt-dirac.mp4", "-c:v vc2 -strict -1 -c:a aac", "drac", "Dirac")]
    [InlineData("qt-dnxhr.mov", "-c:v dnxhd -profile:v dnxhr_lb -pix_fmt yuv422p -c:a aac", "AVdh", "DNxHD")]
    public async Task Sample_entry_codecs_round_trip_through_matroska(string name, string options, string entryType, string format)
    {
        if (!Fixtures.HasTool("mkvmerge"))
            Assert.Skip("mkvmerge not installed.");
        var source = Make(name, options);
        MediaRemux.EnsureRegistered();
        var video = (await TrackImporter.InspectAsync(source, ContainerKind.Matroska, Ct)).Single(t => t.Config.Kind == TrackKind.Video);
        Assert.Equal(format, video.Format);
        Assert.Equal(TrackSupportLevel.Passthrough, video.Support.Level);

        var mkv = await SaveAsync(source, ContainerKind.Matroska);
        string? mp4 = null;
        try
        {
            var info = System.Text.Json.JsonDocument.Parse(Fixtures.Run("mkvmerge", $"-J {Fixtures.Quote(mkv)}"));
            var properties = info.RootElement.GetProperty("tracks").EnumerateArray().First(t => t.GetProperty("type").GetString() == "video").GetProperty("properties");
            Assert.Equal("V_QUICKTIME", properties.GetProperty("codec_id").GetString());
            var codecPrivate = Convert.FromHexString(properties.GetProperty("codec_private_data").GetString()!);
            Assert.Equal(entryType, System.Text.Encoding.ASCII.GetString(codecPrivate, 4, 4));
            Assert.Equal(Decode(source), Decode(mkv));

            mp4 = await SaveAsync(mkv, ContainerKind.Mp4);
            Assert.Equal(Decode(source), Decode(mp4));
            Assert.Contains(entryType, Fixtures.Run("ffprobe", $"-v error -select_streams v -show_entries stream=codec_tag_string -of csv=p=0 {Fixtures.Quote(mp4)}"), StringComparison.Ordinal);
        }
        finally
        {
            MediaProbe.Delete(mkv);
            if (mp4 is not null)
                MediaProbe.Delete(mp4);
        }
    }

    public static TheoryData<string, string, string, bool> CorpusFiles() => new()
    {
        { Path.Combine("Video codecs", "VC1.mp4"), "v", "V_QUICKTIME", false }, // FFmpeg reads one packet of it from MP4 (all from Matroska)
        { Path.Combine("Containers", "3GPP.3gp"), "a", "A_QUICKTIME", true }, // AMR-NB
        { Path.Combine("Multichannel audio", "{AC-4 5.1 - MP4} Dolby Audio ID.mp4"), "a", "A_QUICKTIME", true },
        { Path.Combine("Multichannel audio", "{AC-4 5.1.4 - MP4} Dolby Audio ID.mp4"), "a", "A_QUICKTIME", true },
        { Path.Combine("Multichannel audio", "{AC-4 Immersive Stereo - MP4} Dolby Audio ID.mp4"), "a", "A_QUICKTIME", true },
        { Path.Combine("Multichannel audio", "{MPEG-H 5.1 - MP4} Fraunhofer.mp4"), "a", "A_QUICKTIME", true },
        { Path.Combine("Multichannel audio", "{MPEG-H 2.0, 5.1.2, 5.1 config change - MP4} Fraunhofer.mp4"), "a", "A_QUICKTIME", true },
    };

    /// <summary>Codecs FFmpeg cannot decode (or decodes badly): the packets go through Matroska and back to MP4 unchanged.</summary>
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public async Task Corpus_sample_entry_codecs_keep_their_packets(string relative, string stream, string codecId, bool compareMatroska)
    {
        var source = Corpus.Directory is { } dir ? Path.Combine(dir, relative) : string.Empty;
        Corpus.Require(File.Exists(source) ? source : string.Empty);
        MediaProbe.RequireFfmpeg();
        string Packets(string path) => Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0:{stream}:0 -c copy -f framemd5 -")
            .Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(',').Last()).Aggregate(string.Empty, (a, b) => a + b + "\n");

        var mkv = await SaveAsync(source, ContainerKind.Matroska);
        string? mp4 = null;
        try
        {
            var info = System.Text.Json.JsonDocument.Parse(Fixtures.Run("mkvmerge", $"-J {Fixtures.Quote(mkv)}"));
            Assert.Contains(info.RootElement.GetProperty("tracks").EnumerateArray(), t => t.GetProperty("properties").GetProperty("codec_id").GetString() == codecId);
            var expected = Packets(source);
            if (compareMatroska)
                Assert.Equal(expected, Packets(mkv));
            mp4 = await SaveAsync(mkv, ContainerKind.Mp4);
            Assert.Equal(expected, Packets(mp4));
        }
        finally
        {
            MediaProbe.Delete(mkv);
            if (mp4 is not null)
                MediaProbe.Delete(mp4);
        }
    }
}
