using System.Buffers.Binary;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// AV1 in MPEG-TS: AOMedia's mapping ('AV01', start codes and emulation prevention, one PES per access unit, written
/// here as the specification describes it since no tool does yet), GStreamer's earlier 'AV1G' mapping and FFmpeg's
/// untagged private stream.
/// </summary>
public sealed class Av1TsTests
{
    private static string Ivf()
    {
        MediaProbe.RequireFfmpeg();
        return Fixtures.Get("av1-ts-source.ivf", "ffmpeg",
            "-v error -y -f lavfi -i testsrc2=size=320x180:rate=25:duration=2 -pix_fmt yuv420p -c:v libsvtav1 -preset 12 -g 25 {out}");
    }

    private static string Frames(string path) =>
        string.Join('\n', Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0:v:0 -f framemd5 -").Split('\n')
            .Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(',').Last()));

    private static async Task CheckAsync(string ts)
    {
        MediaRemux.EnsureRegistered();
        var track = Assert.Single(await TrackImporter.InspectAsync(ts, ContainerKind.Mp4, Ct));
        Assert.Equal((CodecType.Av1, 320, 180), (track.Config.Codec, track.Config.Width, track.Config.Height));
        Assert.Equal("Main@L2.0", TrackImporter.ProfileLevel(track.Config));
        Assert.Equal(25, track.Config.FrameRate, 3);
        var expected = Frames(Ivf());
        foreach (var target in new[] { ContainerKind.Mp4, ContainerKind.Matroska })
        {
            var doc = new MediaDocument(null, target);
            TrackImporter.AddToDocument(doc, await TrackImporter.InspectAsync(ts, target, Ct));
            var output = MediaProbe.TempPath(target == ContainerKind.Mp4 ? ".mp4" : ".mkv");
            try
            {
                await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
                Assert.Equal(expected, Frames(output));
            }
            finally
            {
                MediaProbe.Delete(output);
            }
        }
    }

    [Fact]
    public async Task Reads_the_aomedia_mapping()
    {
        var ts = MediaProbe.TempPath(".ts");
        try
        {
            File.WriteAllBytes(ts, SpecTransportStream(File.ReadAllBytes(Ivf())));
            await CheckAsync(ts);
        }
        finally
        {
            MediaProbe.Delete(ts);
        }
    }

    [Fact]
    public Task Reads_ffmpegs_untagged_stream() =>
        CheckAsync(Fixtures.Get("av1-ffmpeg.ts", "ffmpeg", $"-v error -y -i {Fixtures.Quote(Ivf())} -c copy {{out}}"));

    [Fact]
    public Task Reads_gstreamers_mapping()
    {
        if (!Fixtures.HasTool("gst-launch-1.0") && !File.Exists("/usr/bin/gst-launch-1.0"))
            Assert.Skip("GStreamer not installed.");
        var ivf = Ivf();
        return CheckAsync(Fixtures.Get("av1-gstreamer.ts", "gst-launch-1.0",
            $"-q filesrc location={Fixtures.Quote(ivf)} ! ivfparse ! av1parse ! video/x-av1,stream-format=obu-stream,alignment=frame ! " +
            "mpegtsmux enable-custom-mappings=true ! filesink location={out}"));
    }

    // ------------------------------------------------------------------ a writer of AOMedia's mapping

    /// <summary>
    /// An IVF AV1 stream as "Carriage of AV1 in MPEG-2 TS" describes it: PMT with the 'AV01' registration and AV1 video
    /// descriptor, one PES per access unit (hidden frames alone), OBUs behind start codes with emulation prevention,
    /// every other OBU without its size field.
    /// </summary>
    private static byte[] SpecTransportStream(byte[] ivf)
    {
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(ivf.AsSpan(16));
        var scale = BinaryPrimitives.ReadUInt32LittleEndian(ivf.AsSpan(20));
        var o = new List<byte>();
        var counters = new Dictionary<int, int>();
        byte[] pat = [0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, 0xF0, 0x00];
        byte[] es = [0x05, 0x04, .. "AV01"u8, 0x80, 0x04, 0x81, 0x00, 0x0C, 0x00];
        byte[] body = [0x00, 0x01, 0xC1, 0x00, 0x00, 0xE1, 0x00, 0xF0, 0x00, 0x06, 0xE1, 0x00, 0xF0, (byte)es.Length, .. es];
        byte[] pmt = [0x02, 0xB0, (byte)(body.Length + 4), .. body];
        void Psi()
        {
            TsWriter.Packets(o, counters, 0, [0, .. pat, .. TsWriter.Crc(pat)], false);
            TsWriter.Packets(o, counters, 0x1000, [0, .. pmt, .. TsWriter.Crc(pmt)], false);
        }

        Psi();
        var pos = 32;
        var frame = 0;
        while (pos + 12 <= ivf.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(ivf.AsSpan(pos));
            var pts = (long)BinaryPrimitives.ReadUInt64LittleEndian(ivf.AsSpan(pos + 4));
            var unit = ivf.AsSpan(pos + 12, size).ToArray();
            pos += 12 + size;
            var time = 90000 + 90000 * pts * scale / rate;

            // Access units end with each frame (header) OBU; trailing OBUs join the last one.
            var units = new List<List<byte[]>> { new() };
            var p = 0;
            while (p < unit.Length)
            {
                var h = unit[p];
                var q = p + 1 + ((h & 4) != 0 ? 1 : 0);
                long length = 0;
                for (var i = 0; ; i++)
                {
                    var b = unit[q++];
                    length |= (long)(b & 0x7F) << (7 * i);
                    if ((b & 0x80) == 0)
                        break;
                }

                units[^1].Add(unit[p..(q + (int)length)]);
                if (((h >> 3) & 0xF) is 3 or 6)
                    units.Add([]);
                p = q + (int)length;
            }

            if (units[^1].Count == 0)
                units.RemoveAt(units.Count - 1);
            for (var k = 0; k < units.Count; k++)
            {
                var payload = new List<byte>();
                for (var j = 0; j < units[k].Count; j++)
                    payload.AddRange(TsObu(units[k][j], keepSize: j % 2 == 0));
                var key = units[k].Any(x => ((x[0] >> 3) & 0xF) == 1);
                TsWriter.Packets(o, counters, 0x100, TsWriter.Pes([.. payload], time - (units.Count - 1 - k)), key);
            }

            if (++frame % 10 == 0)
                Psi();
        }

        return [.. o];
    }

    private static byte[] TsObu(byte[] obu, bool keepSize)
    {
        var raw = obu;
        if (!keepSize)
        {
            var headerLength = 1 + ((obu[0] & 4) != 0 ? 1 : 0);
            var q = headerLength;
            while ((obu[q++] & 0x80) != 0)
            {
            }

            raw = [(byte)(obu[0] & ~2), .. obu[1..headerLength], .. obu[q..]];
        }

        var o = new List<byte> { 0, 0, 1 };
        var zeros = 0;
        foreach (var b in raw)
        {
            if (zeros >= 2 && b <= 3)
            {
                o.Add(3);
                zeros = 0;
            }

            o.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return [.. o];
    }
}
