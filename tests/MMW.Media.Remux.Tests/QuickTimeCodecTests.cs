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
}
