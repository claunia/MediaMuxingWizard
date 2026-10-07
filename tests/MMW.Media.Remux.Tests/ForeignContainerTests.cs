using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// Containers read through FFmpeg (AVI, MPEG program streams, ASF, WAV, AIFF, MP3, VobSub): the copied packets decode
/// to the same frames and samples in MP4 and Matroska, with presentation times rebuilt where the container has none.
/// </summary>
public sealed class ForeignContainerTests
{
    private const string Source = "-v error -y -f lavfi -i testsrc2=size=320x240:rate=25:duration=2 -f lavfi -i sine=f=440:duration=2:sample_rate=48000";

    private static string Make(string name, string options)
    {
        MediaProbe.RequireFfmpeg();
        return Fixtures.Get(name, "ffmpeg", $"{Source} {options} {{out}}");
    }

    /// <summary>Decoded frames (display order) or audio samples of a file's first stream of a kind, as MD5 lines.</summary>
    private static string Decode(string path, string stream) =>
        Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0:{stream}:0 -fps_mode passthrough -f framemd5 -")
            .Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(',').Last()).Aggregate(string.Empty, (a, b) => a + b + "\n");

    private static string Pcm(string path)
    {
        var raw = MediaProbe.TempPath(".pcm");
        try
        {
            Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(path)} -map 0:a:0 -f s32le {Fixtures.Quote(raw)}");
            using var stream = File.OpenRead(raw);
            return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream));
        }
        finally
        {
            MediaProbe.Delete(raw);
        }
    }

    private static async Task<string> SaveAsync(string source, ContainerKind target, Func<ImportableTrack, ImportChoice?>? choose = null)
    {
        MediaRemux.EnsureRegistered();
        var tracks = await TrackImporter.InspectAsync(source, target, Ct);
        foreach (var t in tracks)
        {
            if (choose?.Invoke(t) is { } choice)
                t.Choice = choice;
        }

        var doc = new MediaDocument(null, target);
        TrackImporter.AddToDocument(doc, tracks);
        var output = MediaProbe.TempPath(target == ContainerKind.Mp4 ? ".mp4" : ".mkv");
        await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
        return output;
    }

    private static async Task RoundTripAsync(string source, bool video, bool audio)
    {
        foreach (var target in new[] { ContainerKind.Mp4, ContainerKind.Matroska })
        {
            var output = await SaveAsync(source, target);
            try
            {
                if (video)
                    Assert.Equal(Decode(source, "v"), Decode(output, "v"));
                if (audio)
                    Assert.Equal(Pcm(source), Pcm(output));
            }
            finally
            {
                MediaProbe.Delete(output);
            }
        }
    }

    /// <summary>AVI keeps decoding times only: the B pictures' order comes from the VOP coding types.</summary>
    [Fact]
    public Task Avi_mpeg4_with_b_frames() =>
        RoundTripAsync(Make("foreign-mpeg4-bf.avi", "-c:v mpeg4 -bf 2 -vtag DIVX -c:a libmp3lame -b:a 128k"), video: true, audio: true);

    /// <summary>H.264 in AVI is Annex B without presentation times: the picture order count gives them.</summary>
    [Fact]
    public Task Avi_h264_with_b_frames() =>
        RoundTripAsync(Make("foreign-h264-bf.avi", "-c:v libx264 -preset veryfast -bf 3 -pix_fmt yuv420p -c:a pcm_s16le"), video: true, audio: true);

    /// <summary>DVD VOB: MPEG-2 with B pictures and AC-3; pictures without a PTS get one from their types.</summary>
    [Fact]
    public Task Vob_mpeg2_with_b_frames() =>
        RoundTripAsync(Make("foreign-mpeg2-ac3.vob", "-c:v mpeg2video -bf 2 -g 12 -c:a ac3 -f vob"), video: true, audio: true);

    /// <summary>MP3 with a LAME header: the encoder delay and padding stay trimmed.</summary>
    [Fact]
    public Task Mp3_keeps_its_gapless_trims() => RoundTripAsync(Make("foreign-gapless.mp3", "-map 1:a -c:a libmp3lame -b:a 128k"), video: false, audio: true);

    [Theory]
    [InlineData("foreign.wav", "-map 1:a -c:a pcm_s24le")]
    [InlineData("foreign.aiff", "-map 1:a -c:a pcm_s16be")]
    public Task Pcm_files_pass_through(string name, string options) => RoundTripAsync(Make(name, options), video: false, audio: true);

    /// <summary>WMA cannot be stored in MP4 or Matroska as it is: it is converted (FFmpeg decodes it).</summary>
    [Fact]
    public async Task Wma_is_converted()
    {
        var source = Make("foreign-wma.asf", "-map 1:a -c:a wmav2 -b:a 128k");
        MediaRemux.EnsureRegistered();
        var track = Assert.Single(await TrackImporter.InspectAsync(source, ContainerKind.Mp4, Ct));
        Assert.Equal(ImportAction.ConvertToAac, track.Action);
        var output = await SaveAsync(source, ContainerKind.Mp4);
        try
        {
            Assert.Contains("aac", Fixtures.Run("ffprobe", $"-v error -show_entries stream=codec_name -of csv=p=0 {Fixtures.Quote(output)}"), StringComparison.Ordinal);
            Assert.Equal(2.0, double.Parse(Fixtures.Run("ffprobe", $"-v error -show_entries format=duration -of csv=p=0 {Fixtures.Quote(output)}"),
                System.Globalization.CultureInfo.InvariantCulture), 1);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    /// <summary>
    /// Codecs only Matroska's compatibility modes hold pass through as mkvmerge stores them (V_MS/VFW/FOURCC with a
    /// BITMAPINFOHEADER, A_MS/ACM with a WAVEFORMATEX, V_REAL/* with the RealMedia type-specific data) and decode
    /// unchanged; read back from Matroska, the video cannot go to MP4 and the audio is converted.
    /// </summary>
    [Theory]
    [InlineData("foreign-msmpeg4.avi", "-c:v msmpeg4 -c:a wmav2 -b:a 128k", "V_MS/VFW/FOURCC", "MS-MPEG4 v3", "A_MS/ACM", "WMA 2")]
    [InlineData("foreign-wmv2.asf", "-c:v wmv2 -c:a wmav1 -b:a 128k", "V_MS/VFW/FOURCC", "WMV 8", "A_MS/ACM", "WMA 1")]
    [InlineData("foreign-dv.dv", "-s 720x576 -r 25 -pix_fmt yuv420p -c:v dvvideo -c:a pcm_s16le -ar 48000 -ac 2 -f dv", "V_MS/VFW/FOURCC", "DV", "A_PCM/INT/LIT", "PCM")]
    [InlineData("foreign-rv10.rm", "-c:v rv10 -c:a ac3 -f rm", "V_REAL/RV10", "RealVideo 1", "A_AC3", "AC-3")]
    public async Task Compatibility_codecs_pass_through_to_matroska(string name, string options, string videoId, string videoName, string audioId, string audioName)
    {
        if (!Fixtures.HasTool("mkvmerge"))
            Assert.Skip("mkvmerge not installed.");
        var source = Make(name, options);
        MediaRemux.EnsureRegistered();
        var tracks = await TrackImporter.InspectAsync(source, ContainerKind.Matroska, Ct);
        Assert.Equal([videoName, audioName], tracks.Select(t => t.Format));

        var output = await SaveAsync(source, ContainerKind.Matroska);
        try
        {
            Assert.Equal(Decode(source, "v"), Decode(output, "v"));
            Assert.Equal(Pcm(source), Pcm(output));
            var info = System.Text.Json.JsonDocument.Parse(Fixtures.Run("mkvmerge", $"-J {Fixtures.Quote(output)}"));
            var ids = info.RootElement.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("properties").GetProperty("codec_id").GetString()).ToList();
            Assert.Equal([videoId, audioId], ids);

            var back = await TrackImporter.InspectAsync(output, ContainerKind.Mp4, Ct);
            Assert.Equal(TrackSupportLevel.Unsupported, back[0].Support.Level);
            if (audioId == "A_MS/ACM")
                Assert.Equal(ImportAction.ConvertToAac, back[1].Action);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    /// <summary>A VobSub pair: either file opens the track; it passes through with the subpictures' own durations, and OCR is offered.</summary>
    [Fact]
    public async Task Vobsub_pairs_import_and_offer_ocr()
    {
        MediaProbe.RequireFfmpeg();
        if (!Fixtures.HasTool("mkvextract"))
            Assert.Skip("mkvextract not installed.");
        var mkv = Path.Combine(Fixtures.GeneratedDirectory, "foreign-dvdsub.mks");
        if (!File.Exists(mkv))
            WriteVobSub(mkv, [(500, 1000), (2000, 1000)]);
        var idx = Path.Combine(Fixtures.GeneratedDirectory, "foreign-dvdsub.idx");
        if (!File.Exists(idx))
            Fixtures.Run("mkvextract", $"{Fixtures.Quote(mkv)} tracks 0:{Fixtures.Quote(idx)}");
        var sub = Path.ChangeExtension(idx, ".sub");
        Assert.True(File.Exists(sub));

        MediaRemux.EnsureRegistered();
        foreach (var path in new[] { idx, sub })
        {
            var track = Assert.Single(await TrackImporter.InspectAsync(path, ContainerKind.Matroska, Ct));
            Assert.Equal(CodecType.VobSub, track.Config.Codec);
            Assert.Contains(track.Choices, c => c.Ocr);
        }

        var output = await SaveAsync(idx, ContainerKind.Matroska);
        try
        {
            var packets = Fixtures.Run("ffprobe", $"-v error -show_entries packet=pts_time,duration_time -of csv=p=0 {Fixtures.Quote(output)}")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, packets.Length);
            Assert.InRange(double.Parse(packets[0].Split(',')[1], System.Globalization.CultureInfo.InvariantCulture), 0.98, 1.02); // the stop command (1024/90000 s units)
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    /// <summary>A Matroska VobSub track (written with the application's muxer) of 8×2 white blocks shown at each (start, duration).</summary>
    private static void WriteVobSub(string path, (int StartMs, int DurationMs)[] cues)
    {
        MediaRemux.EnsureRegistered();
        var config = new CodecConfig
        {
            Codec = CodecType.VobSub,
            Kind = TrackKind.Subtitle,
            Timescale = 1000,
            SourceCodecId = "S_VOBSUB",
            Extradata = System.Text.Encoding.ASCII.GetBytes(
                "# VobSub index file, v7 (do not modify this line!)\nsize: 720x480\npalette: 000000, ffffff, 000000, 808080, 000000, 000000, 000000, 000000, " +
                "000000, 000000, 000000, 000000, 000000, 000000, 000000, 000000\n"),
        };
        var document = new MediaDocument(null, ContainerKind.Matroska) { Duration = TimeSpan.FromMilliseconds(cues.Max(c => c.StartMs + c.DurationMs)) };
        var temp = path + ".part";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite))
        using (var muxer = MediaFormatRegistry.GetMuxer(ContainerKind.Matroska)!.Create(stream, new MuxerSettings { Document = document, OutputPath = temp }))
        {
            var index = muxer.AddTrack(config, new MuxTrackSettings { Model = new SubtitleTrack { Language = "en" } });
            foreach (var (start, duration) in cues)
                muxer.WriteSample(index, new MediaSample { Dts = start, Duration = duration, IsSync = true, Data = Spu(duration) });
            muxer.Finish(Ct);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>A subpicture of one 8×2 block of colour 1 at (40, 380), shown for <paramref name="durationMs"/>.</summary>
    private static byte[] Spu(int durationMs)
    {
        byte[] pixels = [0x00, 0x01, 0x00, 0x01]; // each field: one line run to the end in colour 1
        const int ctrl = 4 + 4;
        List<byte> seq1 = [0, 0, 0, 0, 0x01, 0x03, 0x32, 0x10, 0x04, 0xFF, 0xF0, 0x05, 0x02, 0x80, 0x2F, 0x17, 0xC1, 0x7D, 0x06, 0x00, 0x04, 0x00, 0x06, 0xFF];
        var seq2Offset = ctrl + seq1.Count;
        seq1[2] = (byte)(seq2Offset >> 8);
        seq1[3] = (byte)seq2Offset;
        var date = (int)Math.Round(durationMs * 90.0 / 1024);
        byte[] seq2 = [(byte)(date >> 8), (byte)date, (byte)(seq2Offset >> 8), (byte)seq2Offset, 0x02, 0xFF];
        var total = ctrl + seq1.Count + seq2.Length;
        return [(byte)(total >> 8), (byte)total, (byte)(ctrl >> 8), (byte)ctrl, .. pixels, .. seq1, .. seq2];
    }
}
