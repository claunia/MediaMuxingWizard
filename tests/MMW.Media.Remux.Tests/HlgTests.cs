using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// HLG through importing and remuxing: transfer 18 signalled directly, or as BT.2020 SDR (14) with the alternative
/// transfer characteristics SEI preferring 18 (FFmpeg treats both as HLG and writes 18 to the container); and the
/// ambient viewing environment (SEI 148 → MP4 'amve').
/// </summary>
public sealed class HlgTests
{
    private static string RawHevc(string name, string transferParams)
    {
        MediaProbe.RequireFfmpeg();
        return Fixtures.Get(name, "ffmpeg",
            "-v error -y -f lavfi -i testsrc2=duration=1:size=320x180:rate=25 -pix_fmt yuv420p10le -c:v libx265 -x265-params " +
            $"log-level=error:colorprim=bt2020:colormatrix=bt2020nc:{transferParams}:repeat-headers=1 -f hevc {{out}}");
    }

    private static string ContainerColour(string path) =>
        Fixtures.Run("ffprobe", $"-v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer,color_space -of csv=p=0 {Fixtures.Quote(path)}").Trim().TrimEnd(',');

    [Theory]
    [InlineData("hlg-18.hevc", "transfer=arib-std-b67", ".mp4")]
    [InlineData("hlg-18.hevc", "transfer=arib-std-b67", ".mkv")]
    [InlineData("hlg-14-atc.hevc", "transfer=bt2020-10:atc-sei=18", ".mp4")]
    [InlineData("hlg-14-atc.hevc", "transfer=bt2020-10:atc-sei=18", ".mkv")]
    public async Task Raw_hlg_is_signalled_as_hlg(string name, string transferParams, string extension)
    {
        var raw = RawHevc(name, transferParams);
        var output = MediaProbe.TempPath(extension);
        try
        {
            var target = extension == ".mp4" ? ContainerKind.Mp4 : ContainerKind.Matroska;
            var tracks = await TrackImporter.InspectAsync(raw, target, Ct);
            Assert.Contains("HLG", Assert.Single(tracks).Details, StringComparison.Ordinal);
            tracks[0].FrameRate = 25;
            var doc = new MediaDocument(null, target);
            var video = Assert.IsType<VideoTrack>(Assert.Single(TrackImporter.AddToDocument(doc, tracks)));
            Assert.Equal(18, video.Color.Transfer);

            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
            Assert.Equal("bt2020nc,arib-std-b67,bt2020", ContainerColour(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Corpus_iphone_ambient_viewing_environment_is_written_to_mp4()
    {
        var source = Corpus.Directory is { } dir
            ? Path.Combine(dir, "High Dynamic Range", "Dolby Vision", "Profile 8", "{dvhe.08.12 - Matroska} - iPhone 11.mkv")
            : string.Empty;
        Corpus.Require(File.Exists(source) ? source : string.Empty);
        MediaProbe.RequireFfmpeg();
        var output = MediaProbe.TempPath(".mp4");
        try
        {
            var doc = await Mkv.ReadAsync(source, Ct);
            foreach (var t in doc.Tracks.Where(t => t is not VideoTrack).ToList())
                doc.Tracks.Remove(t);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, null, Ct);

            var hdr = (await Mp4.ReadAsync(output, Ct)).Tracks.OfType<VideoTrack>().Single().Hdr!;
            Assert.Equal(314, hdr.AmbientIlluminance!.Value, 3);
            Assert.Equal((0.3127, 0.329), (Math.Round(hdr.AmbientLight!.Value.X, 4), Math.Round(hdr.AmbientLight.Value.Y, 4)));
            Assert.Equal("bt2020nc,arib-std-b67,bt2020", ContainerColour(output));
            var ffprobe = Fixtures.Run("ffprobe", $"-v error -select_streams v:0 -show_entries stream_side_data=ambient_illuminance -of csv=p=0 {Fixtures.Quote(output)}");
            Assert.Contains("3140000/10000", ffprobe, StringComparison.Ordinal);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }
}
