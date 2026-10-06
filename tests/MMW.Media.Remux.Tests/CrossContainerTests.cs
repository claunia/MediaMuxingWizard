using System.Globalization;
using MMW.Core.Media;
using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>Save-As between MP4 and Matroska (full remux).</summary>
public sealed class CrossContainerTests
{
    [Fact]
    public async Task Matroska_to_mp4_keeps_packets_and_timing()
    {
        var source = MkvH264AacSrt();
        var output = MediaProbe.TempPath(".m4v");
        try
        {
            var doc = await Mkv.ReadAsync(source, Ct);
            var progress = new RecordingProgress();
            await Mkv.SaveAsync(doc, new SaveOptions { OutputPath = output }, progress, Ct);

            var src = MediaProbe.Streams(source);
            var dst = MediaProbe.Streams(output);
            Assert.Equal(["h264", "aac", "mov_text"], dst.Select(s => s.Codec));
            AssertDuration(src[0].Duration, dst[0].Duration, 0.041);
            AssertDuration(src[1].Duration, dst[1].Duration, 0.041);
            Assert.Equal("fra", dst[1].Language); // MP4 stores ISO 639-2/T codes
            Assert.Equal("spa", dst[2].Language);
            Assert.Equal(MediaProbe.PacketHashes(source), MediaProbe.PacketHashes(output));
            Assert.Equal(DecodedFrameHashes(source), DecodedFrameHashes(output));
            Assert.Empty(MediaProbe.DemuxErrors(output));
            Assert.StartsWith("ftyp,moov", Mp4BoxOrder(output), StringComparison.Ordinal);

            // Overlapping cues become non-overlapping tx3g samples.
            var cues = Fixtures.Run("ffprobe", $"-v error -select_streams s -show_packets -show_entries packet=pts_time,duration_time,size -of csv=p=0 {Fixtures.Quote(output)}");
            var texts = cues.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(',')).Where(p => int.Parse(p[2], CultureInfo.InvariantCulture) > 2).ToList();
            Assert.Equal(["0.500000", "2.000000", "2.500000", "2.800000"], texts.Select(t => t[0]));

            // The document now refers to the MP4 file and can be edited in place.
            Assert.Equal(ContainerKind.Mp4, doc.Container);
            Assert.Equal(output, doc.Path);
            Assert.False(doc.IsDirty);
            Assert.All(doc.Tracks, t => Assert.NotEqual(0u, t.Id));
            Assert.Equal("MkvSource", doc.Metadata.GetString(TagId.Name));
            doc.Metadata.Set(TagId.Name, "Edited");
            await Mp4.SaveAsync(doc, new SaveOptions(), cancellationToken: Ct);
            Assert.Equal("Edited", (await Mp4.ReadAsync(output, Ct)).Metadata.GetString(TagId.Name));
            Assert.NotEmpty(progress.Values);
            Assert.Equal(1.0, progress.Values[^1]);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Mp4_to_matroska_keeps_packets_chapters_and_tags()
    {
        MkvToolsRequired();
        var source = Mp4Full();
        var output = MediaProbe.TempPath(".mkv");
        try
        {
            var doc = await Mp4.ReadAsync(source, Ct);
            doc.Tracks.OfType<AudioTrack>().Last().Name = "Second audio";
            await Mp4.SaveAsync(doc, new SaveOptions { OutputPath = output }, cancellationToken: Ct);

            var id = MediaProbe.MkvIdentify(output);
            Assert.Empty(id.GetProperty("errors").EnumerateArray());
            Assert.Empty(id.GetProperty("warnings").EnumerateArray());
            var tracks = id.GetProperty("tracks").EnumerateArray().ToList();
            Assert.Equal(["V_MPEG4/ISO/AVC", "A_AAC", "A_AAC", "S_TEXT/UTF8"], tracks.Select(t => t.GetProperty("properties").GetProperty("codec_id").GetString()!));
            Assert.Equal("Second audio", tracks[2].GetProperty("properties").GetProperty("track_name").GetString());
            Assert.Equal("fre", tracks[2].GetProperty("properties").GetProperty("language").GetString()); // Matroska: ISO 639-2/B

            var src = MediaProbe.Streams(source);
            var dst = MediaProbe.Streams(output);
            Assert.Equal(["h264", "aac", "aac", "subrip"], dst.Select(s => s.Codec));
            AssertDuration(src[0].Duration, dst[0].Duration, 0.041);
            Assert.Equal(MediaProbe.PacketHashes(source), MediaProbe.PacketHashes(output));
            Assert.Equal(DecodedFrameHashes(source), DecodedFrameHashes(output));
            Assert.Empty(MediaProbe.DemuxErrors(output));

            var reread = await Mkv.ReadAsync(output, Ct);
            Assert.Equal(["One", "Two"], reread.Chapters.Select(c => c.Title));
            Assert.Equal("Mp4Source", reread.Metadata.GetString(TagId.Name));
            Assert.Equal(ContainerKind.Matroska, doc.Container);

            // The remuxed file has room for in-place edits.
            doc.Metadata.Set(TagId.Name, "A much longer title that still fits in the padding left after the header");
            await Mkv.SaveAsync(doc, new SaveOptions(), cancellationToken: Ct);
            Assert.Equal(new FileInfo(output).Length, doc.FileSize);
            Assert.Equal(MediaProbe.PacketHashes(source), MediaProbe.PacketHashes(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Hevc_ac3_eac3_configurations_are_built()
    {
        var source = MkvHevcAc3Eac3();
        var output = MediaProbe.TempPath(".mp4");
        try
        {
            var doc = await Mkv.ReadAsync(source, Ct);
            await Mkv.SaveAsync(doc, new SaveOptions { OutputPath = output }, cancellationToken: Ct);

            var json = Fixtures.Run("ffprobe", $"-v error -show_streams -of json {Fixtures.Quote(output)}");
            using var probe = System.Text.Json.JsonDocument.Parse(json);
            var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToList();
            Assert.Equal("hevc", streams[0].GetProperty("codec_name").GetString());
            Assert.Equal("hvc1", streams[0].GetProperty("codec_tag_string").GetString());
            Assert.Equal("24000/1001", streams[0].GetProperty("r_frame_rate").GetString());
            Assert.Equal("ac3", streams[1].GetProperty("codec_name").GetString());
            Assert.Equal("ac-3", streams[1].GetProperty("codec_tag_string").GetString());
            Assert.Equal(6, streams[1].GetProperty("channels").GetInt32());
            Assert.Equal("5.1(side)", streams[1].GetProperty("channel_layout").GetString());
            Assert.Equal("eac3", streams[2].GetProperty("codec_name").GetString());
            Assert.Equal("ec-3", streams[2].GetProperty("codec_tag_string").GetString());
            Assert.Equal(6, streams[2].GetProperty("channels").GetInt32());

            Assert.Equal(MediaProbe.PacketHashes(source), MediaProbe.PacketHashes(output));
            Assert.Equal(DecodedFrameHashes(source), DecodedFrameHashes(output));
            var src = MediaProbe.Streams(source);
            var dst = MediaProbe.Streams(output);
            for (var i = 0; i < 3; i++)
                AssertDuration(src[i].Duration, dst[i].Duration, 0.042);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Optimize_reinterleaves_and_puts_the_header_first()
    {
        var source = Mp4Full();
        var nonInterleaved = MediaProbe.TempPath(".mp4");
        try
        {
            // Write the tracks one after the other (no interleaving) through the public muxer API.
            var doc = await Mp4.ReadAsync(source, Ct);
            using (var demuxer = MediaFormatRegistry.OpenDemuxer(source))
            using (var stream = new FileStream(nonInterleaved, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                var muxer = MediaFormatRegistry.GetMuxer(ContainerKind.Mp4)!.Create(stream, new MuxerSettings { Document = doc, OutputPath = nonInterleaved });
                var indexes = demuxer.Tracks.Select(t => muxer.AddTrack(t.Config, new MuxTrackSettings())).ToList();
                for (var i = 0; i < demuxer.Tracks.Count; i++)
                {
                    var t = demuxer.Tracks[i];
                    while (t.ReadNext() is { } s)
                    {
                        s.Dts -= t.MediaStart;
                        muxer.WriteSample(indexes[i], s);
                    }
                }

                muxer.Finish(Ct);
            }

            Assert.False(IsInterleaved(nonInterleaved));
            var before = MediaProbe.PacketHashes(nonInterleaved);

            var copy = await Mp4.ReadAsync(nonInterleaved, Ct);
            await Mp4.SaveAsync(copy, new SaveOptions { Optimize = true }, cancellationToken: Ct);
            Assert.True(IsInterleaved(nonInterleaved));
            Assert.StartsWith("ftyp,moov", Mp4BoxOrder(nonInterleaved), StringComparison.Ordinal);
            Assert.Equal(before, MediaProbe.PacketHashes(nonInterleaved));
            Assert.Equal(MediaProbe.PacketHashes(source), before);
        }
        finally
        {
            MediaProbe.Delete(nonInterleaved);
        }
    }

    [Fact]
    public async Task Cancelling_a_remux_leaves_no_files_behind()
    {
        var source = MkvH264AacSrt();
        var directory = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "cancelled.mp4");
            var doc = await Mkv.ReadAsync(source, Ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var progress = new CancellingProgress(cts);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Mkv.SaveAsync(doc, new SaveOptions { OutputPath = output }, progress, cts.Token));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            Assert.Equal(source, doc.Path);
            Assert.Equal(ContainerKind.Matroska, doc.Container);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CancellingProgress(CancellationTokenSource cts) : IProgress<double>
    {
        public void Report(double value) => cts.Cancel();
    }

    /// <summary>Audio/video packets ordered by file position never go back in time by more than a second.</summary>
    private static bool IsInterleaved(string path)
    {
        var av = MediaProbe.Streams(path).Where(s => s.Type is "video" or "audio").Select(s => s.Index.ToString(CultureInfo.InvariantCulture)).ToHashSet();
        var lines = Fixtures.Run("ffprobe", $"-v error -show_packets -show_entries packet=stream_index,dts_time,pos -of csv=p=0 {Fixtures.Quote(path)}");
        var packets = lines.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(','))
            .Where(p => p.Length >= 3 && av.Contains(p[0]) && p[1] != "N/A" && p[2] != "N/A")
            .Select(p => (Time: double.Parse(p[1], CultureInfo.InvariantCulture), Pos: long.Parse(p[2], CultureInfo.InvariantCulture)))
            .OrderBy(p => p.Pos)
            .ToList();
        var max = double.MinValue;
        foreach (var (time, _) in packets)
        {
            if (time < max - 1.0)
                return false;
            max = Math.Max(max, time);
        }

        return true;
    }

    /// <summary>Records progress synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class RecordingProgress : IProgress<double>
    {
        private readonly Lock _lock = new();
        private readonly List<double> _values = [];

        public List<double> Values
        {
            get
            {
                lock (_lock)
                    return [.. _values];
            }
        }

        public void Report(double value)
        {
            lock (_lock)
                _values.Add(value);
        }
    }

    internal static string Mp4BoxOrder(string path)
    {
        using var fs = File.OpenRead(path);
        var types = new List<string>();
        Span<byte> header = stackalloc byte[16];
        long pos = 0;
        while (pos + 8 <= fs.Length)
        {
            fs.Position = pos;
            fs.ReadExactly(header[..8]);
            long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
            types.Add(System.Text.Encoding.Latin1.GetString(header.Slice(4, 4)));
            if (size == 1)
            {
                fs.ReadExactly(header.Slice(8, 8));
                size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            }

            if (size < 8)
                break;
            pos += size;
        }

        return string.Join(',', types);
    }

    internal static void MkvToolsRequired()
    {
        if (!Fixtures.HasTool("mkvmerge"))
            Assert.Skip("mkvmerge is not installed.");
    }
}
