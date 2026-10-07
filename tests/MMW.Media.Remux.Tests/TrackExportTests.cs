using System.Buffers.Binary;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// Exporting one track as a raw / elementary file (as mkvextract does): FFmpeg decodes the exported file to the same
/// frames / audio as the source track, and the formats this application imports read back with the same samples.
/// </summary>
public sealed class TrackExportTests
{
    public TrackExportTests() => MediaRemux.EnsureRegistered();

    private const string Video = "-f lavfi -i testsrc2=size=320x240:rate=25:duration=2";
    private const string Audio = "-f lavfi -i sine=f=440:duration=2:sample_rate=48000";

    private static string Make(string name, string args)
    {
        MediaProbe.RequireFfmpeg();
        return Fixtures.Get("export-" + name, "ffmpeg", $"-v error -y {args} {{out}}");
    }

    private static string Q(string path) => Fixtures.Quote(path);

    /// <summary>Input options that make FFmpeg decode every packet of an MP4 (its edit list would hide the AAC priming a raw file keeps).</summary>
    private static string InputOptions(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".m4a" or ".mov" or ".3gp" ? "-ignore_editlist 1" : string.Empty;

    private static List<string> VideoFrames(string path) =>
        Fixtures.Run("ffmpeg", $"-v error {InputOptions(path)} -i {Q(path)} -map 0:v:0 -fps_mode passthrough -f framemd5 -")
            .Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(',')[^1].Trim()).ToList();

    private static string AudioMd5(string path, string inputOptions = "") =>
        Fixtures.Run("ffmpeg", $"-v error {inputOptions} -i {Q(path)} -map 0:a:0 -f md5 -").Trim();

    private static List<string> AudioFrames(string path) =>
        Fixtures.Run("ffmpeg", $"-v error -i {Q(path)} -map 0:a:0 -f framemd5 -")
            .Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(',')[^1].Trim()).ToList();

    /// <summary>
    /// The same decoded audio: frame by frame for codecs with frames (the first and last may be trimmed by the source
    /// container's priming / padding information, which raw formats do not carry), as a whole for PCM-like streams
    /// (re-packetized by the reader).
    /// </summary>
    private static void AssertSameAudio(string source, string exported, CodecType codec)
    {
        if (codec is CodecType.Pcm or CodecType.Alac)
        {
            Assert.Equal(AudioMd5(source), AudioMd5(exported));
            return;
        }

        var expected = AudioFrames(source);
        var actual = AudioFrames(exported);
        // A priming frame the source's edit list hides entirely is decoded from the raw file.
        var hidden = actual.Count - expected.Count;
        Assert.InRange(hidden, 0, 1);
        Assert.Equal(expected[1..^1], actual[(1 + hidden)..^1]);
    }

    private static ISampleSource Track(IDemuxer demuxer, TrackKind kind) => demuxer.Tracks.First(t => t.Config.Kind == kind);

    private static long Count(ISampleSource source)
    {
        source.Reset();
        long n = 0;
        while (source.ReadNext() is not null)
            n++;
        source.Reset();
        return n;
    }

    private static async Task<string> ExportAsync(ISampleSource track, string extension)
    {
        Assert.Equal(extension, TrackExport.Extension(track.Config));
        var output = MediaProbe.TempPath(extension);
        var reports = new List<double>();
        await TrackExport.ExportAsync(track, output, new SyncProgress(reports), Ct);
        Assert.True(File.Exists(output));
        Assert.Equal(1.0, reports[^1]);
        return output;
    }

    private sealed class SyncProgress(List<double> reports) : IProgress<double>
    {
        public void Report(double value) => reports.Add(value);
    }

    /// <summary>Re-imports an exported file this application reads and checks codec and sample count.</summary>
    private static void AssertReimports(string exported, ISampleSource source, long? expectedCount = null)
    {
        using var demuxer = MediaFormatRegistry.OpenDemuxer(exported, new DemuxOptions { FrameRate = 25 });
        var track = demuxer.Tracks.Single();
        Assert.Equal(source.Config.Codec, track.Config.Codec);
        Assert.Equal(expectedCount ?? Count(source), Count(track));
    }

    public static TheoryData<string, string, string, bool> VideoCases() => new()
    {
        { "h264.mkv", $"{Video} -c:v libx264 -preset fast -bf 3 -g 12", ".h264", true },
        { "h264.mp4", $"{Video} -c:v libx264 -preset fast -bf 2 -g 20", ".h264", true },
        { "hevc.mp4", $"{Video} -c:v libx265 -preset ultrafast -x265-params log-level=error:bframes=3:keyint=15", ".h265", true },
        { "hevc.mkv", $"{Video} -c:v libx265 -preset ultrafast -x265-params log-level=error:keyint=20", ".h265", true },
        { "av1.mkv", $"{Video} -c:v libsvtav1 -preset 12 -g 20", ".ivf", true },
        { "av1.mp4", $"{Video} -c:v libsvtav1 -preset 12 -g 20", ".ivf", true },
        { "vp8.webm", $"{Video} -c:v libvpx -b:v 300k -g 20", ".ivf", false },
        { "vp9.mkv", $"{Video} -c:v libvpx-vp9 -b:v 300k -g 20 -deadline realtime", ".ivf", false },
        { "mpeg2.mkv", $"{Video} -c:v mpeg2video -bf 2 -g 12", ".m2v", false },
        { "mpeg2.mp4", $"{Video} -c:v mpeg2video -bf 2 -g 12", ".m2v", false },
        { "mpeg1.mkv", $"{Video} -c:v mpeg1video -bf 2", ".m1v", false },
        { "mpeg4.mp4", $"{Video} -c:v mpeg4 -bf 2", ".m4v", false },
        { "mpeg4.mkv", $"{Video} -c:v mpeg4", ".m4v", false },
        { "dirac.mp4", $"{Video} -c:v vc2 -strict -1", ".drc", false },
        { "dnxhr.mov", $"{Video} -c:v dnxhd -profile:v dnxhr_lb -pix_fmt yuv422p", ".dnxhd", false },
        { "h263.3gp", "-f lavfi -i testsrc2=size=352x288:rate=25:duration=2 -c:v h263 -f 3gp", ".h263", false },
        { "mjpeg.mkv", $"{Video} -c:v mjpeg -pix_fmt yuvj420p", ".mjpg", false },
    };

    [Theory]
    [MemberData(nameof(VideoCases))]
    public async Task Video_tracks_export_to_decodable_raw_streams(string name, string args, string extension, bool reimport)
    {
        var source = Make(name, args);
        using var demuxer = MediaFormatRegistry.OpenDemuxer(source);
        var track = Track(demuxer, TrackKind.Video);
        var exported = await ExportAsync(track, extension);
        try
        {
            Assert.Equal(VideoFrames(source), VideoFrames(exported));
            if (extension == ".h264")
            {
                // FFmpeg keeps the parameter sets in avcC only: they are repeated before every key frame.
                var bytes = await File.ReadAllBytesAsync(exported, Ct);
                var sps = 0;
                for (var i = 0; i + 5 <= bytes.Length; i++)
                {
                    if (bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 0 && bytes[i + 3] == 1 && (bytes[i + 4] & 0x1F) == 7)
                        sps++;
                }

                track.Reset();
                var keyFrames = 0;
                while (track.ReadNext() is { } sample)
                    keyFrames += sample.IsSync ? 1 : 0;
                Assert.True(keyFrames > 1);
                Assert.Equal(keyFrames, sps);
            }

            if (reimport)
                AssertReimports(exported, track);
        }
        finally
        {
            MediaProbe.Delete(exported);
        }
    }

    public static TheoryData<string, string, string, bool> AudioCases() => new()
    {
        { "aac.mp4", $"{Audio} -c:a aac", ".aac", true },
        { "aac51.mkv", $"{Audio} -af pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0 -c:a aac", ".aac", true },
        { "ac3.mkv", $"{Audio} -c:a ac3", ".ac3", true },
        { "eac3.mp4", $"{Audio} -c:a eac3", ".eac3", true },
        { "dts.mkv", $"{Audio} -ac 2 -c:a dca -strict -2", ".dts", true },
        { "mlp.mkv", $"{Audio} -ac 2 -c:a mlp -strict -2", ".mlp", false },
        { "truehd.mkv", $"{Audio} -ac 2 -c:a truehd -strict -2", ".thd", false },
        { "mp2.mkv", $"{Audio} -c:a mp2", ".mp2", false },
        { "mp3.mp4", $"{Audio} -c:a libmp3lame", ".mp3", false },
        { "flac.mkv", $"{Audio} -c:a flac", ".flac", true },
        { "flac.mp4", $"{Audio} -c:a flac", ".flac", true },
        { "opus.mkv", $"{Audio} -c:a libopus", ".opus", true },
        { "opus.mp4", $"{Audio} -c:a libopus", ".opus", true },
        { "vorbis.mkv", $"{Audio} -ac 2 -c:a vorbis -strict -2", ".ogg", true },
        { "pcm16.mkv", $"{Audio} -c:a pcm_s16le", ".wav", false },
        { "pcm24be.mov", $"{Audio} -ac 2 -c:a pcm_s24be", ".wav", false },
        { "pcmf32.mkv", $"{Audio} -c:a pcm_f32le", ".wav", false },
        { "pcm51.mkv", $"{Audio} -af pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0 -c:a pcm_s16le", ".wav", false },
        { "pcm8.mov", $"{Audio} -c:a pcm_u8", ".wav", false },
        { "alac.m4a", $"{Audio} -c:a alac", ".caf", false },
        { "adpcm.mkv", $"{Audio} -c:a adpcm_ms", ".wav", false },
    };

    [Theory]
    [MemberData(nameof(AudioCases))]
    public async Task Audio_tracks_export_to_decodable_raw_streams(string name, string args, string extension, bool reimport)
    {
        var source = Make(name, args);
        using var demuxer = MediaFormatRegistry.OpenDemuxer(source);
        var track = Track(demuxer, TrackKind.Audio);
        var exported = await ExportAsync(track, extension);
        try
        {
            AssertSameAudio(source, exported, track.Config.Codec);
            if (reimport)
                AssertReimports(exported, track);
        }
        finally
        {
            MediaProbe.Delete(exported);
        }
    }

    private const string Ass =
        "[Script Info]\nScriptType: v4.00+\nPlayResX: 384\nPlayResY: 288\n\n" +
        "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
        "Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,2,2,10,10,10,1\n\n" +
        "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
        "Dialogue: 0,0:00:00.50,0:00:01.50,Default,,0,0,0,,{\\i1}Hello{\\i0}\n" +
        "Dialogue: 1,0:00:02.00,0:00:02.80,Default,Bob,0,0,0,,World\\Nsecond line\n" +
        "Dialogue: 0,0:00:02.50,0:00:03.50,Default,,0,0,0,,Overlap\n";

    private const string Vtt = "WEBVTT\n\n00:00:00.500 --> 00:00:01.500\n<i>Hello</i>\n\n00:00:02.000 --> 00:00:02.800\nWorld\n\n00:00:02.500 --> 00:00:03.500\nOverlap\n";

    public static TheoryData<string, string, string, string> SubtitleCases() => new()
    {
        { "srt.mkv", "remux.srt", "-c:s srt", ".srt" },
        { "tx3g.mp4", "remux.srt", "-c:s mov_text", ".srt" },
        { "ass.mkv", "export.ass", "-c:s ass", ".ass" },
        { "vtt.mkv", "export.vtt", "-c:s webvtt", ".vtt" },
    };

    [Theory]
    [MemberData(nameof(SubtitleCases))]
    public async Task Text_subtitles_export_with_their_cues(string name, string input, string codec, string extension)
    {
        var text = Text(input, Path.GetExtension(input) switch
        {
            ".ass" => Ass,
            ".vtt" => Vtt,
            _ => Srt,
        });
        var source = Make(name, $"-f lavfi -i color=size=64x64:duration=4 -i {Q(text)} -map 0:v -map 1:s -c:v libx264 {codec}");
        using var demuxer = MediaFormatRegistry.OpenDemuxer(source);
        var track = Track(demuxer, TrackKind.Subtitle);
        var exported = await ExportAsync(track, extension);
        try
        {
            // FFmpeg reads the same cues from the exported file as from the source track.
            var format = extension switch
            {
                ".ass" => "ass",
                ".vtt" => "webvtt",
                _ => "srt",
            };
            string Cues(string path) => Fixtures.Run("ffmpeg", $"-v error -i {Q(path)} -map 0:s:0 -c:s {(format == "srt" ? "srt" : format)} -f {format} -");
            Assert.Equal(Cues(source), Cues(exported));

            var written = await File.ReadAllTextAsync(exported, Ct);
            if (extension == ".srt")
                Assert.StartsWith("1\n00:00:00,500 --> 00:00:01,500\n<i>Hello</i>\n\n2\n00:00:02,000 --> 00:00:02,", written, StringComparison.Ordinal);
            if (extension == ".ass")
            {
                Assert.Contains("Dialogue: 1,0:00:02.00,0:00:02.80,Default,Bob,0,0,0,,World\\Nsecond line\n", written, StringComparison.Ordinal);
                Assert.Contains("Style: Default,Arial,20", written, StringComparison.Ordinal);
            }

            using var reread = MediaFormatRegistry.OpenDemuxer(exported);
            Assert.Equal(track.Config.Codec == CodecType.Tx3g ? CodecType.TextUtf8 : track.Config.Codec, reread.Tracks.Single().Config.Codec);
            Assert.Equal(3, Count(reread.Tracks.Single()));
        }
        finally
        {
            MediaProbe.Delete(exported);
        }
    }

    [Fact]
    public async Task Pgs_display_sets_are_written_as_sup_segments()
    {
        // Two display sets: a presentation composition segment (0x16) and an end segment (0x80) each.
        byte[] set = [0x16, 0x00, 0x02, 0xAA, 0xBB, 0x80, 0x00, 0x00];
        var source = new MemorySource(
            new CodecConfig { Codec = CodecType.Pgs, Kind = TrackKind.Subtitle, Timescale = 1000 },
            [new MediaSample { Dts = 1000, Duration = 500, IsSync = true, Data = set }, new MediaSample { Dts = 2500, IsSync = true, Data = set }]);
        var exported = await ExportAsync(source, ".sup");
        try
        {
            var bytes = await File.ReadAllBytesAsync(exported, Ct);
            Assert.Equal(2 * (10 + 5 + 10 + 3), bytes.Length);
            Assert.Equal("PG"u8.ToArray(), bytes[..2]);
            Assert.Equal(90_000u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)));
            Assert.Equal(0x16, bytes[10]);
            Assert.Equal(90_000u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(17))); // end segment of the first set
            Assert.Equal(0x80, bytes[25]);
            Assert.Equal(225_000u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(30)));
        }
        finally
        {
            MediaProbe.Delete(exported);
        }
    }

    [Fact]
    public async Task Unsupported_codecs_are_refused()
    {
        var config = new CodecConfig { Codec = CodecType.RealVideo, Kind = TrackKind.Video, Timescale = 1000 };
        Assert.Null(TrackExport.Extension(config));
        Assert.False(TrackExport.CanExport(config));
        Assert.False(TrackExport.CanExport(config with { Codec = CodecType.VfwVideo }));
        Assert.False(TrackExport.CanExport(config with { Codec = CodecType.Unknown }));
        var output = MediaProbe.TempPath(".bin");
        await Assert.ThrowsAsync<NotSupportedException>(() => TrackExport.ExportAsync(new MemorySource(config, []), output, null, Ct));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Export_overwrites_the_destination_and_leaves_no_temporary_file()
    {
        var source = Make("ac3.mkv", $"{Audio} -c:a ac3");
        using var demuxer = MediaFormatRegistry.OpenDemuxer(source);
        var output = MediaProbe.TempPath(".ac3");
        await File.WriteAllTextAsync(output, "old", Ct);
        try
        {
            await TrackExport.ExportAsync(demuxer.Tracks.Single(), output, null, Ct);
            Assert.True(new FileInfo(output).Length > 1000);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(output)!, "*" + Path.GetFileName(output) + "*"));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    public static TheoryData<string, string, string, bool> CorpusCases() => new()
    {
        { Path.Combine("Video codecs", "H266 VVC.mp4"), "v", ".h266", true },
        { Path.Combine("Video codecs", "MPEG-5 EVC.mp4"), "v", ".evc", false },
        { Path.Combine("Video codecs", "AV2.ivf"), "v", ".ivf", false },
        { Path.Combine("Video codecs", "AVS.mkv"), "v", ".avs", true },
        { Path.Combine("Video codecs", "AVS2.mkv"), "v", ".avs2", false },
        { Path.Combine("Video codecs", "AVS3.mkv"), "v", ".avs3", false },
        { Path.Combine("Video codecs", "VC1.mp4"), "v", ".vc1", true },
        { Path.Combine("Audio codecs", "AC4.mp4"), "a", ".ac4", false },
        { Path.Combine("Audio codecs", "MPEG-H.mp4"), "a", ".mhas", false },
        { Path.Combine("Containers", "3GPP.3gp"), "a", ".amr", true },
        { Path.Combine("Subtitles", "Embedded", "DVD.mkv"), "s", ".idx", true },
    };

    /// <summary>
    /// Codecs FFmpeg cannot encode: the exported file holds every sample (re-imported where this application reads the
    /// format, otherwise checked by FFmpeg), and FFmpeg decodes it like the source when <c>decode</c> is set.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusCases))]
    public async Task Corpus_tracks_export(string relative, string stream, string extension, bool decode)
    {
        var source = Corpus.Directory is { } dir ? Path.Combine(dir, relative) : string.Empty;
        Corpus.Require(File.Exists(source) ? source : string.Empty);
        MediaProbe.RequireFfmpeg();
        using var demuxer = MediaFormatRegistry.OpenDemuxer(source);
        var kind = stream switch
        {
            "v" => TrackKind.Video,
            "a" => TrackKind.Audio,
            _ => TrackKind.Subtitle,
        };
        var track = Track(demuxer, kind);
        var exported = await ExportAsync(track, extension);
        var sub = Path.ChangeExtension(exported, ".sub");
        try
        {
            var reimportable = extension is ".h266" or ".evc" or ".ivf" or ".avs" or ".avs2" or ".avs3" or ".ac4";
            if (reimportable)
            {
                using var reread = MediaFormatRegistry.OpenDemuxer(exported, new DemuxOptions { FrameRate = 25 });
                var copy = reread.Tracks.Single();
                Assert.Equal(track.Config.Codec, copy.Config.Codec);
                Assert.Equal(Count(track), Count(copy));
                if (extension is ".ac4" or ".ivf")
                {
                    // Same payloads (the AV2 ones without their configuration OBUs, as MP4 stores them).
                    track.Reset();
                    copy.Reset();
                    while (track.ReadNext() is { } a)
                        Assert.Equal(a.GetData().ToArray(), copy.ReadNext()!.GetData().ToArray());
                }
            }

            if (extension == ".mhas")
            {
                var bytes = await File.ReadAllBytesAsync(exported, Ct);
                var frames = 0;
                Assert.Equal(bytes.Length, Core.Media.Codecs.MpegH.ForEachPacket(bytes, (type, _, _) => frames += type == Core.Media.Codecs.MpegH.PacketFrame ? 1 : 0));
                Assert.Equal(Count(track), frames);
            }

            if (decode && extension == ".idx")
            {
                Assert.True(File.Exists(sub));
                string Packets(string path) => Fixtures.Run("ffmpeg", $"-v error -i {Q(path)} -map 0:s:0 -c copy -f framemd5 -")
                    .Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(',')[^1].Trim()).Aggregate(string.Empty, (a, b) => a + b + "\n");
                Assert.Equal(Packets(source), Packets(exported));
            }
            else if (decode && kind == TrackKind.Video)
            {
                var frames = VideoFrames(exported);
                Assert.Equal(Count(track), frames.Count);
                if (extension != ".vc1") // FFmpeg reads one packet of the VC-1 MP4
                    Assert.Equal(VideoFrames(source), frames);
            }
            else if (decode)
            {
                AssertSameAudio(source, exported, track.Config.Codec);
            }
        }
        finally
        {
            MediaProbe.Delete(exported, sub);
        }
    }

    /// <summary>A track held in memory.</summary>
    private sealed class MemorySource(CodecConfig config, IReadOnlyList<MediaSample> samples) : ISampleSource
    {
        private int _next;

        public uint TrackId => 1;

        public CodecConfig Config => config;

        public TimeSpan StartOffset => TimeSpan.Zero;

        public long MediaStart => 0;

        public TimeSpan Duration => TimeSpan.FromSeconds(samples.Count > 0 ? (samples[^1].Dts + samples[^1].Duration) / (double)config.Timescale : 0);

        public long SampleCountHint => samples.Count;

        public MediaSample? ReadNext() => _next < samples.Count ? samples[_next++] : null;

        public void Reset() => _next = 0;
    }

}
