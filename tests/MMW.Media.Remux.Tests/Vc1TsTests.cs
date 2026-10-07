using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// VC-1 in MPEG-2 TS as SMPTE RP 227 maps it (stream type 0xEA, 'VC-1' registration, start-code delimited EBDUs with the
/// sequence header and entry point at random access points): it becomes a 'vc-1' track with a 'dvc1' built from those
/// headers, in MP4 and (as V_QUICKTIME) in Matroska. FFmpeg cannot write such a stream from WMV (it drops the start
/// codes and headers), so the test writes it from the corpus VC-1 file.
/// </summary>
public sealed class Vc1TsTests
{
    [Fact]
    public async Task Corpus_vc1_transport_stream_imports_as_vc1()
    {
        var wmv = Corpus.Directory is { } dir ? Path.Combine(dir, "Video codecs", "VC1.wmv") : string.Empty;
        Corpus.Require(File.Exists(wmv) ? wmv : string.Empty);
        MediaProbe.RequireFfmpeg();
        MediaRemux.EnsureRegistered();
        var ts = MediaProbe.TempPath(".ts");
        var outputs = new List<string>();
        try
        {
            File.WriteAllBytes(ts, Rp227(wmv, seconds: 4));
            var video = (await TrackImporter.InspectAsync(ts, ContainerKind.Mp4, Ct)).Single(t => t.Config.Kind == TrackKind.Video);
            Assert.Equal(CodecType.Vc1, video.Config.Codec);
            Assert.Equal((1280, 720), (video.Config.Width, video.Config.Height));
            Assert.Contains("Advanced@L3", video.Details, StringComparison.Ordinal);

            string Frames(string path) => Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0:v:0 -fps_mode passthrough -f framemd5 -")
                .Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(',').Last()).Aggregate(string.Empty, (a, b) => a + b + "\n");
            var expected = Frames(ts);
            Assert.NotEmpty(expected);
            foreach (var target in new[] { ContainerKind.Mp4, ContainerKind.Matroska })
            {
                var output = MediaProbe.TempPath(target == ContainerKind.Mp4 ? ".mp4" : ".mkv");
                outputs.Add(output);
                var tracks = await TrackImporter.InspectAsync(ts, target, Ct);
                var doc = new MediaDocument(null, target);
                TrackImporter.AddToDocument(doc, tracks);
                await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
                Assert.Equal(expected, Frames(output));
            }

            Assert.Contains("vc-1", Fixtures.Run("ffprobe", $"-v error -show_entries stream=codec_tag_string -of csv=p=0 {Fixtures.Quote(outputs[0])}"), StringComparison.Ordinal);
        }
        finally
        {
            MediaProbe.Delete(ts);
            foreach (var output in outputs)
                MediaProbe.Delete(output);
        }
    }

    /// <summary>The first seconds of the WMV's VC-1 frames as an RP 227 transport stream.</summary>
    private static byte[] Rp227(string wmv, int seconds)
    {
        using var demuxer = MediaFormatRegistry.OpenDemuxer(wmv);
        var track = demuxer.Tracks.Single(t => t.Config.Kind == TrackKind.Video);
        var extra = Vfw.ParseBitmapInfoHeader(track.Config.Extradata)!.Value.Extra;
        var headerStart = extra.AsSpan().IndexOf([(byte)0, (byte)0, (byte)1, (byte)0x0F]);
        var headers = extra[headerStart..]; // sequence header and entry point, start codes included
        var o = new List<byte>();
        var counters = new Dictionary<int, int>();
        byte[] descriptors = [0x05, 0x04, .. "VC-1"u8];
        var frames = 0;
        while (track.ReadNext() is { } sample)
        {
            var time = 90000 + sample.Dts * 90000 / track.Config.Timescale;
            if (time > 90000 * (seconds + 1))
                break;
            if (frames++ % 25 == 0)
                TsWriter.Psi(o, counters, 0xEA, descriptors);
            var frame = sample.GetData().ToArray();
            byte[] payload = sample.IsSync ? [.. headers, 0, 0, 1, 0x0D, .. frame] : [0, 0, 1, 0x0D, .. frame];
            TsWriter.Packets(o, counters, 0x100, TsWriter.Pes(payload, time, 0xFD), sample.IsSync);
        }

        return [.. o];
    }
}
