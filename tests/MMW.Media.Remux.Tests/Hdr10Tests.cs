using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// HDR10 static metadata through importing and remuxing: container signalling (MP4 colr/mdcv/clli, Matroska Colour)
/// converted correctly, and filled in from the bitstream (VUI/SEI, AV1 sequence header and metadata OBUs) when the
/// source container has none. Checked against ffprobe's reading of the output container.
/// </summary>
public sealed class Hdr10Tests
{
    private const string Expected = "bt2020 smpte2084 bt2020nc tv | R 0.68,0.32 G 0.265,0.69 B 0.15,0.06 W 0.3127,0.329 L 0.005..1000 | CLL 1000/400";

    /// <summary>Raw HEVC whose HDR10 metadata lives only in the VUI and SEI messages.</summary>
    private static string RawHevcHdr10()
    {
        MediaProbe.RequireFfmpeg();
        return Fixtures.Get("hdr10-sei.hevc", "ffmpeg",
            "-v error -y -f lavfi -i testsrc2=duration=1:size=320x180:rate=25 -pix_fmt yuv420p10le -c:v libx265 -x265-params " +
            "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:" +
            "master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50):max-cll=1000,400:repeat-headers=1 " +
            "-f hevc {out}");
    }

    /// <summary>ffprobe's stream-level (container) colour and HDR10 metadata, normalised.</summary>
    private static string ContainerHdr(string path)
    {
        var json = Fixtures.Run("ffprobe",
            $"-v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer,color_space,color_range:stream_side_data -of json {Fixtures.Quote(path)}");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var s = doc.RootElement.GetProperty("streams")[0];
        string Str(string name) => s.TryGetProperty(name, out var v) ? v.GetString() ?? "-" : "-";
        double Q(System.Text.Json.JsonElement d, string name)
        {
            var parts = d.GetProperty(name).GetString()!.Split('/');
            return Math.Round(double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) /
                              (parts.Length > 1 ? double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 1), 4);
        }

        string mastering = "-", light = "-";
        if (s.TryGetProperty("side_data_list", out var list))
        {
            foreach (var d in list.EnumerateArray())
            {
                var type = d.GetProperty("side_data_type").GetString() ?? string.Empty;
                if (type.Contains("Mastering", StringComparison.Ordinal))
                {
                    mastering = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"R {Q(d, "red_x")},{Q(d, "red_y")} G {Q(d, "green_x")},{Q(d, "green_y")} B {Q(d, "blue_x")},{Q(d, "blue_y")} " +
                        $"W {Q(d, "white_point_x")},{Q(d, "white_point_y")} L {Q(d, "min_luminance")}..{Q(d, "max_luminance")}");
                }
                else if (type.Contains("Content light", StringComparison.Ordinal))
                {
                    light = $"CLL {d.GetProperty("max_content").GetInt32()}/{d.GetProperty("max_average").GetInt32()}";
                }
            }
        }

        return $"{Str("color_primaries")} {Str("color_transfer")} {Str("color_space")} {Str("color_range")} | {mastering} | {light}";
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    public async Task Raw_hevc_hdr10_is_signalled_by_the_output_container(string extension)
    {
        var raw = RawHevcHdr10();
        var output = MediaProbe.TempPath(extension);
        try
        {
            var tracks = await TrackImporter.InspectAsync(raw, extension == ".mp4" ? ContainerKind.Mp4 : ContainerKind.Matroska, Ct);
            var video = Assert.Single(tracks);
            Assert.Contains("HDR10", video.Details, StringComparison.Ordinal);
            tracks[0].FrameRate = 25;
            var doc = new MediaDocument(null, extension == ".mp4" ? ContainerKind.Mp4 : ContainerKind.Matroska);
            var added = Assert.IsType<VideoTrack>(Assert.Single(TrackImporter.AddToDocument(doc, tracks)));
            Assert.Equal(1000, added.StreamInfo!.Hdr!.MaxLuminance);

            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, doc.Container, null, Ct);
            Assert.Equal(Expected, ContainerHdr(output));

            // Our own reader agrees, and a second remux to the other container keeps it.
            var reread = await (extension == ".mp4" ? Mp4.ReadAsync(output, Ct) : Mkv.ReadAsync(output, Ct));
            var hdr = reread.Tracks.OfType<VideoTrack>().Single().Hdr!;
            Assert.Equal((0.68, 0.32), (Math.Round(hdr.DisplayPrimaries![0].X, 4), Math.Round(hdr.DisplayPrimaries[0].Y, 4)));
            var other = MediaProbe.TempPath(extension == ".mp4" ? ".mkv" : ".mp4");
            try
            {
                await Remuxer.SaveAsync(reread, new SaveOptions { OutputPath = other }, extension == ".mp4" ? ContainerKind.Matroska : ContainerKind.Mp4, null, Ct);
                Assert.Equal(Expected, ContainerHdr(other));
            }
            finally
            {
                MediaProbe.Delete(other);
            }
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public void Av1_sequence_header_and_metadata_obus_are_read()
    {
        MediaProbe.RequireFfmpeg();
        var av1 = Fixtures.Get("hdr10-av1.mkv", "ffmpeg",
            "-v error -y -f lavfi -i testsrc2=duration=1:size=320x180:rate=25 -pix_fmt yuv420p10le -c:v libsvtav1 -preset 12 -svtav1-params " +
            "color-primaries=9:transfer-characteristics=16:matrix-coefficients=9:" +
            "mastering-display=G(0.265,0.69)B(0.15,0.06)R(0.68,0.32)WP(0.3127,0.329)L(1000,0.005):content-light=1000,400 {out}");
        using var demuxer = MediaFormatRegistry.OpenDemuxer(av1);
        var info = VideoStreamInfoScanner.Scan(demuxer.Tracks.Single(t => t.Config.Kind == TrackKind.Video), Ct);
        Assert.Equal(new ColorInfo(9, 16, 9, false), info.Color);
        Assert.Equal((0.68, 0.32), (Math.Round(info.Hdr!.DisplayPrimaries![0].X, 3), Math.Round(info.Hdr.DisplayPrimaries[0].Y, 3)));
        Assert.Equal(1000, info.Hdr.MaxLuminance!.Value, 2);
        Assert.Equal((1000, 400), (info.Hdr.MaxCll, info.Hdr.MaxFall));
    }

    // ------------------------------------------------------------------ corpus (MMW_CORPUS)

    private static string Sample(params string[] parts)
    {
        var path = Corpus.Directory is { } dir ? Path.Combine([dir, "High Dynamic Range", .. parts]) : string.Empty;
        Corpus.Require(File.Exists(path) ? path : string.Empty);
        MediaProbe.RequireFfmpeg();
        return path;
    }

    [Fact]
    public async Task Corpus_quicktime_mdcv_is_read_in_the_right_order()
    {
        var mov = await Mp4.ReadAsync(Sample("HDR10", "{HDR10, ProRes - QuickTime} Strobe Scientist.mov"), Ct);
        var hdr = mov.Tracks.OfType<VideoTrack>().Single().Hdr!;
        Assert.Equal([(0.68, 0.32), (0.265, 0.69), (0.15, 0.06)], hdr.DisplayPrimaries!.Select(p => (Math.Round(p.X, 3), Math.Round(p.Y, 3))));
    }

    [Fact]
    public async Task Corpus_hevc_hdr10_only_in_sei_gets_container_signalling()
    {
        var source = Sample("HDR10", "{HDR10, HEVC - MP4} Exodus Sample.mp4");
        var output = MediaProbe.TempPath(".mkv");
        try
        {
            var doc = await Mp4.ReadAsync(source, Ct);
            var video = doc.Tracks.OfType<VideoTrack>().Single();
            Assert.False(video.Color.IsSpecified);
            Assert.Null(video.Hdr);
            var scan = await VideoBitstreamScan.ScanAsync(video, Ct);
            Assert.Equal(1200, scan.StreamInfo!.Hdr!.MaxLuminance!.Value, 3);

            foreach (var t in doc.Tracks.Where(t => t is not VideoTrack).ToList())
                doc.Tracks.Remove(t);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Matroska, null, Ct);
            Assert.Equal("bt2020 smpte2084 bt2020nc tv | R 0.68,0.32 G 0.265,0.69 B 0.15,0.06 W 0.3127,0.329 L 0.02..1200 | -", ContainerHdr(output));
            Assert.Equal(MediaProbe.PacketHashes(source, "-map 0:v"), MediaProbe.PacketHashes(output, "-map 0:v"));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }
}
