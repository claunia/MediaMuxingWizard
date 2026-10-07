using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// MPEG transport streams (TS and Blu-ray M2TS) as import sources: tracks are found from the PAT/PMT, PES packets are
/// turned into samples (Annex B → length-prefixed video, audio split into frames), timing keeps the tracks in sync.
/// Checked by decoding source and output and comparing every frame.
/// </summary>
public sealed class MpegTsTests
{
    private static string Ts(string name, string args)
    {
        MediaProbe.RequireFfmpeg();
        return Fixtures.Get(name, "ffmpeg", "-v error -y " + args + " {out}");
    }

    /// <summary>H.264 with B-frames, AAC and AC-3, 3 seconds.</summary>
    private static string H264AacAc3() => Ts("ts-h264-aac-ac3.ts",
        "-f lavfi -i testsrc2=duration=3:size=320x240:rate=25 -f lavfi -i sine=f=440:d=3 -f lavfi -i sine=f=660:d=3 " +
        "-map 0 -map 1 -map 2 -c:v libx264 -bf 3 -g 25 -pix_fmt yuv420p -c:a:0 aac -c:a:1 ac3 -metadata:s:a:0 language=eng -metadata:s:a:1 language=spa -f mpegts");

    /// <summary>Blu-ray style M2TS (192-byte packets): HEVC, E-AC-3 5.1 and MP2.</summary>
    private static string HevcEac3M2ts() => Ts("ts-hevc-eac3.m2ts",
        "-f lavfi -i testsrc2=duration=3:size=320x240:rate=24 -f lavfi -i sine=f=440:d=3 -f lavfi -i sine=f=550:d=3 " +
        "-map 0 -map 1 -map 2 -c:v libx265 -x265-params log-level=error -pix_fmt yuv420p -c:a:0 eac3 -ac:a:0 6 -c:a:1 mp2 -f mpegts -mpegts_m2ts_mode 1");

    /// <summary>Timestamps crossing the 33-bit wraparound (2^33 / 90 kHz ≈ 95443.7 s) after about a second.</summary>
    private static string Wrapping() => Ts("ts-wrap.ts",
        "-f lavfi -i testsrc2=duration=3:size=160x120:rate=25 -f lavfi -i sine=f=440:d=3 -c:v libx264 -g 10 -pix_fmt yuv420p -c:a aac " +
        "-output_ts_offset 95442.5 -f mpegts");

    private static string FrameHashes(string path, string map) =>
        string.Join('\n', Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map {map} -f framemd5 -")
            .Split('\n').Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split(',')[^1].Trim()));

    private static async Task<string> ImportAll(string source, ContainerKind target)
    {
        var output = MediaProbe.TempPath(target == ContainerKind.Mp4 ? ".mp4" : ".mkv");
        var tracks = await TrackImporter.InspectAsync(source, target, Ct);
        foreach (var t in tracks)
            t.Choice = t.Choices.First(c => c.Action == ImportAction.Passthrough);
        var doc = new MediaDocument(null, target);
        TrackImporter.AddToDocument(doc, tracks);
        await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
        return output;
    }

    [Fact]
    public async Task Finds_the_programs_streams()
    {
        var tracks = await TrackImporter.InspectAsync(H264AacAc3(), ContainerKind.Matroska, Ct);
        Assert.Equal([CodecType.H264, CodecType.Aac, CodecType.Ac3], tracks.Select(t => t.Config.Codec));
        Assert.Equal(["und", "en", "es"], tracks.Select(t => t.Language));
        Assert.Equal((320, 240), (tracks[0].Config.Width, tracks[0].Config.Height));
        Assert.Equal(25, tracks[0].Config.FrameRate, 1);
        Assert.Equal(44100, tracks[1].Config.SampleRate);
        Assert.All(tracks, t => Assert.InRange(t.Duration.TotalSeconds, 2.8, 3.1));
        Assert.All(tracks, t => Assert.Equal(TrackSupportLevel.Passthrough, t.Support.Level));
    }

    [Theory]
    [InlineData(ContainerKind.Matroska)]
    [InlineData(ContainerKind.Mp4)]
    public async Task Ts_tracks_remux_frame_exact(ContainerKind target)
    {
        var source = H264AacAc3();
        var output = await ImportAll(source, target);
        try
        {
            Assert.Empty(MediaProbe.DemuxErrors(output));
            foreach (var map in new[] { "0:v:0", "0:a:0", "0:a:1" })
            {
                var expected = FrameHashes(source, map).Split('\n');
                var actual = FrameHashes(output, map).Split('\n');
                Assert.Equal(expected.Length, actual.Length);
                // FFmpeg's MP4 reader trims the end of a track that starts with an empty edit by the edit's length (its
                // own MP4s of the same streams behave the same), so the last decoded audio frame is left out there.
                var compared = target == ContainerKind.Mp4 && map != "0:v:0" ? expected.Length - 1 : expected.Length;
                Assert.Equal(expected[..compared], actual[..compared]);
            }
            AssertInSync(source, output);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task M2ts_hevc_eac3_mp2_remux_frame_exact()
    {
        var source = HevcEac3M2ts();
        var tracks = await TrackImporter.InspectAsync(source, ContainerKind.Matroska, Ct);
        Assert.Equal([CodecType.Hevc, CodecType.Eac3, CodecType.Mp2], tracks.Select(t => t.Config.Codec));
        Assert.Equal(6, tracks[1].Config.Channels);
        var output = await ImportAll(source, ContainerKind.Matroska);
        try
        {
            foreach (var map in new[] { "0:v:0", "0:a:0", "0:a:1" })
                Assert.Equal(FrameHashes(source, map), FrameHashes(output, map));
            AssertInSync(source, output);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Timestamps_crossing_the_33_bit_wraparound_stay_continuous()
    {
        var source = Wrapping();
        var tracks = await TrackImporter.InspectAsync(source, ContainerKind.Matroska, Ct);
        Assert.All(tracks, t => Assert.InRange(t.Duration.TotalSeconds, 2.8, 3.2));
        var output = await ImportAll(source, ContainerKind.Matroska);
        try
        {
            var duration = double.Parse(Fixtures.Run("ffprobe", $"-v error -show_entries format=duration -of csv=p=0 {Fixtures.Quote(output)}").Trim(),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(duration, 2.8, 3.3);
            Assert.Equal(FrameHashes(source, "0:v:0"), FrameHashes(output, "0:v:0"));
            Assert.Equal(75, FrameHashes(output, "0:v:0").Split('\n').Length);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task A_cut_stream_starts_at_the_first_key_frame()
    {
        // Drop the first ~half second of packets (mid-GOP): leading pictures without their key frame are skipped.
        var whole = File.ReadAllBytes(H264AacAc3());
        var cut = MediaProbe.TempPath(".ts");
        var skip = whole.Length / 6 / 188 * 188;
        File.WriteAllBytes(cut, whole[skip..]);
        var output = MediaProbe.TempPath(".mkv");
        try
        {
            var tracks = await TrackImporter.InspectAsync(cut, ContainerKind.Matroska, Ct);
            Assert.Contains(tracks, t => t.Config.Codec == CodecType.H264);
            File.Delete(output);
            output = await ImportAll(cut, ContainerKind.Matroska);
            Assert.Equal(string.Empty, Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(output)} -map 0:v -f null -").Trim());
            var keyFrame = Fixtures.Run("ffprobe", $"-v error -select_streams v:0 -read_intervals %+#1 -show_packets -show_entries packet=flags -of csv=p=0 {Fixtures.Quote(output)}");
            Assert.StartsWith("K", keyFrame.Trim(), StringComparison.Ordinal);
        }
        finally
        {
            MediaProbe.Delete(cut, output);
        }
    }

    /// <summary>A PGS display set: presentation composition, window definition, then END (segments as on Blu-ray).</summary>
    private static byte[] PgsDisplaySet(ushort composition)
    {
        var segments = new List<byte>();
        void Segment(byte type, byte[] payload)
        {
            segments.Add(type);
            segments.Add((byte)(payload.Length >> 8));
            segments.Add((byte)payload.Length);
            segments.AddRange(payload);
        }

        Segment(0x16, [0x07, 0x80, 0x04, 0x38, 0x10, (byte)(composition >> 8), (byte)composition, 0x80, 0x00, 0x00, 0x00]); // PCS, epoch start, no objects
        Segment(0x17, [0x00]); // WDS without windows
        Segment(0x80, []); // END
        return [.. segments];
    }

    [Fact]
    public async Task Pgs_segments_are_gathered_into_display_sets()
    {
        MediaProbe.RequireFfmpeg();
        MediaRemux.EnsureRegistered();
        var mkv = MediaProbe.TempPath(".mkv");
        var m2ts = MediaProbe.TempPath(".m2ts");
        try
        {
            using (var stream = new FileStream(mkv, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                using var muxer = MediaFormatRegistry.GetMuxer(ContainerKind.Matroska)!.Create(stream,
                    new MuxerSettings { Document = new MediaDocument(null, ContainerKind.Matroska), OutputPath = mkv });
                var index = muxer.AddTrack(new CodecConfig { Kind = TrackKind.Subtitle, Codec = CodecType.Pgs, Timescale = 1000, Language = "en" }, new MuxTrackSettings());
                for (ushort i = 0; i < 3; i++)
                    muxer.WriteSample(index, new MediaSample { Dts = 1000 + i * 1000, Duration = 500, IsSync = true, Data = PgsDisplaySet(i) });
                muxer.Finish(Ct);
            }

            Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(mkv)} -map 0 -c copy -f mpegts -mpegts_m2ts_mode 1 {Fixtures.Quote(m2ts)}");
            using var demuxer = MediaFormatRegistry.OpenDemuxer(m2ts);
            var track = Assert.Single(demuxer.Tracks);
            Assert.Equal(CodecType.Pgs, track.Config.Codec);
            var samples = new List<MediaSample>();
            while (track.ReadNext() is { } sample)
                samples.Add(sample);
            Assert.Equal(3, samples.Count);
            for (ushort i = 0; i < 3; i++)
                Assert.Equal(PgsDisplaySet(i), samples[i].GetData().ToArray());
            Assert.InRange(samples[1].Pts - samples[0].Pts, 89910, 90090);
        }
        finally
        {
            MediaProbe.Delete(mkv, m2ts);
        }
    }

    private static string DecodedAudio(string path, int index) =>
        Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0:a:{index} -c:a pcm_s32le -f md5 -").Trim();

    [Theory]
    [InlineData("5.1", "s16")]
    [InlineData("7.1", "s32")]
    public async Task Bluray_lpcm_becomes_little_endian_pcm_in_wave_order(string layout, string format)
    {
        var source = Ts($"ts-lpcm-{layout}-{format}.m2ts",
            $"-f lavfi -i sine=f=300:d=2:sample_rate=48000 -filter_complex \"[0]aformat=channel_layouts={layout}:sample_fmts={format},aeval=val(ch)*(ch+1)/9:c=same[a]\" " +
            "-map [a] -c:a pcm_bluray -f mpegts -mpegts_m2ts_mode 1");
        var tracks = await TrackImporter.InspectAsync(source, ContainerKind.Matroska, Ct);
        var pcm = Assert.Single(tracks);
        Assert.Equal(CodecType.Pcm, pcm.Config.Codec);
        Assert.Equal(layout == "5.1" ? 6 : 8, pcm.Config.Channels);
        var output = await ImportAll(source, ContainerKind.Matroska);
        try
        {
            Assert.Equal(DecodedAudio(source, 0), DecodedAudio(output, 0)); // same samples, same channel order
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    // ------------------------------------------------------------------ corpus (MMW_CORPUS)

    public static TheoryData<string, bool> CorpusFiles() => new()
    {
        // PES not aligned with access units. FFmpeg drops two audio PES it reports as corrupt; they are kept (and decode).
        { Path.Combine("High Dynamic Range", "HLG", "[LG] Cymatic Jazz {4K HLG & AAC 2.0}.ts"), false },
        { Path.Combine("Multichannel audio", "{Dolby Digital Plus 7.1 - M2TS} Dolby Digital Plus.m2ts"), true }, // AC-3 core + E-AC-3 dependent
        { Path.Combine("Multichannel audio", "{DTS-HD MA 7.1 - M2TS} Dataset Digital Sound.m2ts"), true },
        { Path.Combine("Multichannel audio", "{Dolby TrueHD + Atmos - M2TS} Unfold (15 objects).m2ts"), true }, // TrueHD with interleaved AC-3
        { Path.Combine("Multichannel audio", "{LPCM 5.1 24b - M2TS} Barco Stinger Bees.m2ts"), true },
    };

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public async Task Corpus_transport_streams_remux_frame_exact(string relative, bool compareAudio)
    {
        var source = Corpus.Directory is { } dir ? Path.Combine(dir, relative) : string.Empty;
        Corpus.Require(File.Exists(source) ? source : string.Empty);
        MediaProbe.RequireFfmpeg();
        var output = await ImportAll(source, ContainerKind.Matroska);
        try
        {
            var expected = Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(source)} -map 0:v:0 -frames:v 120 -f framemd5 -").Split('\n').Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split(',')[^1].Trim()).ToList();
            var actual = Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(output)} -map 0:v:0 -frames:v 120 -f framemd5 -").Split('\n').Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split(',')[^1].Trim()).ToList();
            Assert.Equal(expected.Count, actual.Count);
            Assert.True(expected.Count(actual.Contains) >= expected.Count - 2, "the decoded pictures differ"); // leading undecodable pictures aside
            if (compareAudio)
                Assert.Equal(DecodedAudio(source, 0), DecodedAudio(output, 0));
            else
                Assert.Equal(string.Empty, Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(output)} -map 0:a -f null -").Trim());
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    /// <summary>MPEG-4 Part 2 video (stream type 0x10) with B-frames: the VOL becomes the configuration, frames keep their times.</summary>
    [Theory]
    [InlineData(ContainerKind.Matroska)]
    [InlineData(ContainerKind.Mp4)]
    public async Task Mpeg4_part2_streams_remux_frame_exact(ContainerKind target)
    {
        var source = Ts("ts-mpeg4.ts", "-f lavfi -i testsrc2=duration=2:size=320x240:rate=25 -c:v mpeg4 -bf 2 -g 12 -f mpegts");
        var track = Assert.Single(await TrackImporter.InspectAsync(source, target, Ct));
        Assert.Equal(CodecType.Mpeg4Visual, track.Config.Codec);
        Assert.Equal((320, 240), (track.Config.Width, track.Config.Height));
        Assert.Contains("Simple", track.Details, StringComparison.Ordinal);
        var output = await ImportAll(source, target);
        try
        {
            Assert.Equal(FrameHashes(source, "0:v:0"), FrameHashes(output, "0:v:0"));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    /// <summary>
    /// MPEG-H 3D Audio (stream type 0x2D, MHAS): the access units (configuration changes included) and the 'mhm1' entry
    /// written to MP4 are those of the encoder's own MP4 of the same content.
    /// </summary>
    [Theory]
    [InlineData("MPEG-H 2.0")]
    [InlineData("MPEG-H 5.1")]
    [InlineData("MPEG-H 2.0, 5.1.2, 5.1 config change")]
    public async Task Corpus_mpegh_transport_streams_match_the_reference_mp4(string name)
    {
        var dir = Corpus.Directory;
        var source = dir is null ? string.Empty : Path.Combine(dir, "Multichannel audio", $"{{{name} - MPEG-TS}} Fraunhofer.ts");
        Corpus.Require(File.Exists(source) ? source : string.Empty);
        MediaProbe.RequireFfmpeg();
        var reference = Path.Combine(dir!, "Multichannel audio", $"{{{name} - MP4}} Fraunhofer.mp4");
        string Packets(string path) => Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0:a -c copy -f framemd5 -")
            .Split('\n').Where(l => l.Length > 0 && l[0] != '#').Select(l => string.Join(',', l.Split(',').Skip(1).Select(f => f.Trim()))).Aggregate(string.Empty, (a, b) => a + b + "\n");
        var output = await ImportAll(source, ContainerKind.Mp4);
        try
        {
            Assert.Equal(Packets(reference), Packets(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    /// <summary>Each stream starts at the same time relative to the others as in the source.</summary>
    private static void AssertInSync(string source, string output)
    {
        double[] Starts(string path) => Fixtures.Run("ffprobe", $"-v error -show_entries stream=index,start_time -of csv=p=0 {Fixtures.Quote(path)}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim(',').Split(',')).DistinctBy(p => p[0]) // programs repeat their streams
            .Select(p => double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var a = Starts(source);
        var b = Starts(output);
        Assert.Equal(a.Length, b.Length);
        for (var i = 1; i < a.Length; i++)
            Assert.Equal(a[i] - a[0], b[i] - b[0], 0.03);
    }
}
