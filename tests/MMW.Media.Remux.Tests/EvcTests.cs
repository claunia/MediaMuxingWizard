using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// MPEG-5 EVC: raw import to MP4 with profile, colour and HDR10 / HLG / HDR10+ read from the bitstream, the order of
/// presentation (POC from slice LSBs in Main, from the sub-GOP structure in Baseline), and Matroska refusing it.
/// Streams are encoded with xeve and checked with the xevd reference decoder (FFmpeg has neither an EVC encoder nor,
/// usually, a decoder). xeve writes no VUI or HDR SEI from its command line, so the tests add them: a VUI with BT.2020
/// colour and 25 fps timing in the SPS, and mastering display / content light level (and HDR10+) SEI after the PPS.
/// </summary>
public sealed partial class EvcTests
{
    /// <summary>An HDR10+ ITU-T T.35 message (ST 2094-40) taken from a real HEVC HDR10+ stream.</summary>
    private static readonly byte[] s_hdr10PlusT35 = Convert.FromHexString(
        "B5003C000104014000000000000000000000236408000028079C500190C801959001FA58029AD003F2F805771807900010");

    private static void RequireXeve()
    {
        MediaProbe.RequireFfmpeg();
        // The tools exit with an error for --help or no arguments: look them up on PATH.
        var path = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var tool in new[] { "xeve_app", "xevd_app" })
        {
            if (!path.Any(dir => File.Exists(Path.Combine(dir, tool)) || File.Exists(Path.Combine(dir, tool + ".exe"))))
                Assert.Skip($"{tool} (xeve/xevd) is not installed.");
        }
    }

    private static string Yuv() => Fixtures.Get("evc-in.yuv", "ffmpeg",
        "-v error -y -f lavfi -i testsrc2=duration=1:size=320x180:rate=25 -pix_fmt yuv420p10le -f rawvideo {out}");

    /// <summary>One second of 10-bit 4:2:0 EVC from xeve (16-picture hierarchical B sub-GOPs).</summary>
    private static string Encoded(string profile)
    {
        RequireXeve();
        return Fixtures.Get($"evc-{profile}.evc", "xeve_app",
            $"-v 1 -i {Fixtures.Quote(Yuv())} -w 320 -h 180 -z 25 -d 10 --codec-bit-depth 10 --profile {profile} --preset fast --level-idc 41 -o {{out}}");
    }

    /// <summary>The xeve stream with a VUI (BT.2020, <paramref name="transfer"/>, 25 fps) and HDR SEI added.</summary>
    private static string Hdr(string profile, int transfer, bool hdr10Plus)
    {
        var source = Encoded(profile);
        var path = Path.Combine(Fixtures.GeneratedDirectory, $"evc-{profile}-{transfer}{(hdr10Plus ? "-plus" : string.Empty)}.evc");
        if (File.Exists(path))
            return path;

        var data = File.ReadAllBytes(source);
        using var output = new MemoryStream();
        foreach (var range in NalUnits.SplitLengthPrefixed(data, 4))
        {
            var nal = data[range];
            var type = Evc.NalType(nal);
            Write(output, type == Evc.NalSps ? AddVui(nal, transfer) : nal);
            if (type == Evc.NalPps)
                Write(output, HdrSei(hdr10Plus));
        }

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, output.ToArray());
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private static void Write(Stream output, byte[] nal)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, nal.Length);
        output.Write(length);
        output.Write(nal);
    }

    /// <summary>
    /// xeve's SPS ends with vui_parameters_present_flag = 0 and the stop bit: the flag is set and a VUI appended.
    /// </summary>
    private static byte[] AddVui(byte[] sps, int transfer)
    {
        var bits = new StringBuilder();
        foreach (var b in sps.AsSpan(2))
            bits.Append(Convert.ToString(b, 2).PadLeft(8, '0'));
        var text = bits.ToString();
        var stop = text.LastIndexOf('1');
        Assert.Equal('0', text[stop - 1]);

        static string Bits(long value, int count) => Convert.ToString(value, 2).PadLeft(count, '0');
        var vui = "0" + "0" + // aspect ratio, overscan
                  "1" + "101" + "0" + "1" + Bits(9, 8) + Bits(transfer, 8) + Bits(9, 8) + // video signal type, colour description
                  "0" + "0" + "0" + // chroma location, neutral chroma, field_seq
                  "1" + Bits(1, 32) + Bits(25, 32) + "1" + // timing: 1/25 s, fixed rate
                  "0" + "0" + "0" + "0"; // NAL/VCL HRD, pic_struct_present_flag, bitstream_restriction_flag
        var patched = text[..(stop - 1)] + "1" + vui + "1";
        patched = patched.PadRight((patched.Length + 7) / 8 * 8, '0');
        var o = new List<byte> { sps[0], sps[1] };
        for (var i = 0; i < patched.Length; i += 8)
            o.Add(Convert.ToByte(patched.Substring(i, 8), 2));
        return [.. o];
    }

    /// <summary>A SEI NAL unit with mastering display (P3-D65 primaries, 1000/0.005 cd/m²) and light level (1000/400).</summary>
    private static byte[] HdrSei(bool hdr10Plus)
    {
        var body = new List<byte>();
        void Message(int type, byte[] payload)
        {
            body.Add((byte)type);
            body.Add((byte)payload.Length);
            body.AddRange(payload);
        }

        var mdcv = new byte[24];
        ushort[] chroma = [13250, 34500, 7500, 3000, 34000, 16000, 15635, 16450]; // G, B, R, white point
        for (var i = 0; i < chroma.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(mdcv.AsSpan(2 * i), chroma[i]);
        BinaryPrimitives.WriteUInt32BigEndian(mdcv.AsSpan(16), 10_000_000);
        BinaryPrimitives.WriteUInt32BigEndian(mdcv.AsSpan(20), 50);
        Message(Sei.MasteringDisplayColourVolume, mdcv);
        Message(Sei.ContentLightLevelInfo, [0x03, 0xE8, 0x01, 0x90]);
        if (hdr10Plus)
            Message(Sei.UserDataRegisteredItuTT35, s_hdr10PlusT35);
        body.Add(0x80);
        return [(Evc.NalSei + 1) << 1, 0, .. body];
    }

    [GeneratedRegex(@"poc=(-?\d+)")]
    private static partial Regex PocPattern();

    /// <summary>The pictures' order counts in decoding order, as xevd reports them, and the MD5 of the decoded video.</summary>
    private static (List<int> Pocs, string Md5) Decode(string evc)
    {
        var yuv = MediaProbe.TempPath(".yuv");
        try
        {
            var log = Fixtures.Run("xevd_app", $"-v 3 -i {Fixtures.Quote(evc)} -o {Fixtures.Quote(yuv)} --output-bit-depth 10");
            var pocs = PocPattern().Matches(log).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            using var md5 = System.Security.Cryptography.MD5.Create();
            using var file = File.OpenRead(yuv);
            return (pocs, Convert.ToHexString(md5.ComputeHash(file)));
        }
        finally
        {
            MediaProbe.Delete(yuv);
        }
    }

    /// <summary>
    /// The video track of <paramref name="path"/> as a raw EVC stream (configuration NAL units, then the samples in
    /// decoding order), and the samples' presentation times.
    /// </summary>
    private static (string Evc, List<long> Pts) Extract(string path)
    {
        using var demuxer = MediaFormatRegistry.OpenDemuxer(path, new DemuxOptions());
        var video = demuxer.Tracks.Single(t => t.Config.Kind == TrackKind.Video);
        var evc = MediaProbe.TempPath(".evc");
        var pts = new List<long>();
        using (var output = File.Create(evc))
        {
            foreach (var (_, _, nal) in Evc.ParseEvcC(video.Config.Extradata!).Nals)
                Write(output, nal);
            while (video.ReadNext() is { } sample)
            {
                pts.Add(sample.Pts);
                output.Write(sample.GetData().Span);
            }
        }

        return (evc, pts);
    }

    private static List<int> Ranks<T>(List<T> values) where T : IComparable<T> =>
        values.Select(v => values.Count(o => o.CompareTo(v) < 0)).ToList();

    [Theory]
    [InlineData("main", 16, "HDR10", "smpte2084")]
    [InlineData("main", 18, "HLG", "arib-std-b67")]
    [InlineData("baseline", 16, "HDR10", "smpte2084")]
    [InlineData("baseline", 18, "HLG", "arib-std-b67")]
    public async Task Raw_stream_imports_to_mp4_with_its_profile_colour_hdr_and_order(string profile, int transfer, string hdr, string ffmpegTransfer)
    {
        var raw = Hdr(profile, transfer, hdr10Plus: false);
        var tracks = await TrackImporter.InspectAsync(raw, ContainerKind.Mp4, Ct);
        var item = Assert.Single(tracks);
        Assert.Equal(CodecType.Evc, item.Config.Codec);
        var name = profile == "main" ? "Main" : "Baseline";
        Assert.Equal($"320×180, 25 fps, {name}@L4.1, {hdr}", item.Details);
        Assert.Equal(new ColorInfo(9, transfer, 9, false), item.Config.EffectiveColor);
        Assert.Equal(1000, item.Config.EffectiveHdr!.MaxCll);

        var doc = new MediaDocument(null, ContainerKind.Mp4);
        TrackImporter.AddToDocument(doc, tracks);
        var output = MediaProbe.TempPath(".mp4");
        string? extracted = null;
        try
        {
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, null, Ct);
            var colour = Fixtures.Run("ffprobe", $"-v error -select_streams v:0 -show_entries stream=codec_tag_string,color_space,color_transfer,color_primaries -of csv=p=0 {Fixtures.Quote(output)}");
            Assert.StartsWith($"evc1,bt2020nc,{ffmpegTransfer},bt2020", colour.Trim(), StringComparison.Ordinal);
            var sideData = Fixtures.Run("ffprobe", $"-v error -select_streams v:0 -show_entries stream_side_data=side_data_type -of csv=p=0 {Fixtures.Quote(output)}");
            Assert.Contains("Mastering display metadata", sideData, StringComparison.Ordinal);
            Assert.Contains("Content light level metadata", sideData, StringComparison.Ordinal);

            var reread = Assert.Single((await Mp4.ReadAsync(output, Ct)).Tracks.OfType<VideoTrack>());
            Assert.Equal($"{name}@L4.1", reread.ProfileLevel);

            // The stored stream decodes like the source, and the presentation order is the decoder's.
            List<long> pts;
            (extracted, pts) = Extract(output);
            var source = Decode(raw);
            var stored = Decode(extracted);
            Assert.Equal(25, source.Pocs.Count);
            Assert.Equal(source.Md5, stored.Md5);
            Assert.Equal(Ranks(source.Pocs), Ranks(pts));
        }
        finally
        {
            MediaProbe.Delete(output, extracted ?? string.Empty);
        }
    }

    [Fact]
    public async Task Hdr10_plus_sei_is_detected()
    {
        var tracks = await TrackImporter.InspectAsync(Hdr("baseline", 16, hdr10Plus: true), ContainerKind.Mp4, Ct);
        Assert.Contains("HDR10+", Assert.Single(tracks).Details, StringComparison.Ordinal);
        Assert.True(tracks[0].Config.Hdr10Plus);
    }

    [Fact]
    public async Task Matroska_refuses_evc_and_says_why()
    {
        var tracks = await TrackImporter.InspectAsync(Hdr("baseline", 16, hdr10Plus: false), ContainerKind.Matroska, Ct);
        var support = Assert.Single(tracks).Support;
        Assert.False(support.CanMux);
        Assert.Contains("no Matroska codec ID", support.Reason, StringComparison.Ordinal);
        Assert.Contains("save as MP4", support.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Corpus_mp4_shows_its_profile_and_remuxes_unchanged()
    {
        MediaProbe.RequireFfmpeg();
        var source = Corpus.Directory is { } dir ? Path.Combine(dir, "Video codecs", "MPEG-5 EVC.mp4") : string.Empty;
        Corpus.Require(File.Exists(source) ? source : string.Empty);
        var doc = await Mp4.ReadAsync(source, Ct);
        var video = Assert.Single(doc.Tracks.OfType<VideoTrack>());
        Assert.Equal("EVC", video.Format);
        Assert.Equal("Baseline@L7.1", video.ProfileLevel);

        var check = await Remuxer.CheckAsync(doc, ContainerKind.Matroska);
        Assert.Contains(check, c => c.Track is VideoTrack && !c.Support.CanMux && c.Support.Reason!.Contains("no Matroska codec ID", StringComparison.Ordinal));

        var output = MediaProbe.TempPath(".mp4");
        try
        {
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, null, Ct);
            Assert.Equal(MediaProbe.PacketHashes(source), MediaProbe.PacketHashes(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }
}
