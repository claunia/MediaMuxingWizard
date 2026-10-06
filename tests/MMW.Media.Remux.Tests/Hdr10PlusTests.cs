using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// HDR10+ through muxing: in-band (HEVC SEI, AV1 metadata OBUs) it travels with the frames; for VP9 it is kept in
/// Matroska BlockAdditions (WebM, BlockAddID 4, ITU-T T.35), which MP4 cannot store.
/// </summary>
public sealed class Hdr10PlusTests
{
    /// <summary>A valid SMPTE ST 2094-40 message (one window, no percentiles, no tone mapping) with a per-frame value.</summary>
    private static byte[] Hdr10PlusMessage(int frame)
    {
        var bits = new List<bool>();
        void Put(long value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                bits.Add(((value >> i) & 1) != 0);
        }

        Put(1, 2); // num_windows
        Put(400, 27); // targeted_system_display_maximum_luminance
        Put(0, 1); // targeted_system_display_actual_peak_luminance_flag
        for (var i = 0; i < 3; i++)
            Put(10000 + frame, 17); // maxscl
        Put(5000 + frame, 17); // average_maxrgb
        Put(0, 4); // num_distribution_maxrgb_percentiles
        Put(0, 10); // fraction_bright_pixels
        Put(0, 1); // mastering_display_actual_peak_luminance_flag
        Put(0, 1); // tone_mapping_flag
        Put(0, 1); // color_saturation_mapping_flag
        while (bits.Count % 8 != 0)
            bits.Add(false);
        var body = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i])
                body[i / 8] |= (byte)(0x80 >> (i % 8));
        return [0xB5, 0x00, 0x3C, 0x00, 0x01, 0x04, 0x01, .. body];
    }

    private static string Vp9()
    {
        MediaProbe.RequireFfmpeg();
        return Fixtures.Get("remux-vp9.webm", "ffmpeg",
            "-v error -y -f lavfi -i testsrc=duration=1:size=160x120:rate=10 -c:v libvpx-vp9 -deadline realtime -b:v 100k -g 4 " +
            "-pix_fmt yuv420p {out}");
    }

    private static int PacketsWithHdr10Plus(string path) =>
        Fixtures.Run("ffprobe", $"-v error -select_streams v:0 -show_packets -show_entries packet_side_data=side_data_type -of csv=p=0 {Fixtures.Quote(path)}")
            .Split('\n').Count(l => l.Contains("SMPTE 2094", StringComparison.Ordinal));

    /// <summary>VP9 frames with HDR10+ block additions, written by our muxer to a new (non-copied) track entry.</summary>
    private static string MuxWithBlockAdditions(out int frames)
    {
        var output = MediaProbe.TempPath(".webm");
        using var demuxer = MediaFormatRegistry.OpenDemuxer(Vp9());
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite);
        var doc = new MediaDocument(null, ContainerKind.Matroska);
        using var muxer = MediaFormatRegistry.GetMuxer(ContainerKind.Matroska)!.Create(stream, new MuxerSettings { Document = doc, OutputPath = output });
        var source = demuxer.Tracks.Single(t => t.Config.Kind == TrackKind.Video);
        var index = muxer.AddTrack(source.Config with { Native = null, Hdr10PlusInBlockAdditions = true, Hdr10Plus = true }, new MuxTrackSettings());
        frames = 0;
        while (source.ReadNext() is { } sample)
        {
            sample.Additions = [new BlockAddition(BlockAddition.ItuT35, Hdr10PlusMessage(frames++))];
            muxer.WriteSample(index, sample);
        }

        muxer.Finish(Ct);
        return output;
    }

    [Fact]
    public async Task Vp9_block_additions_survive_matroska_remuxing()
    {
        var written = MuxWithBlockAdditions(out var frames);
        var remuxed = MediaProbe.TempPath(".mkv");
        try
        {
            // Our writer: ffmpeg sees HDR10+ on every packet, and our reader gets the additions back.
            Assert.Equal(frames, PacketsWithHdr10Plus(written));
            Assert.Empty(MediaProbe.DemuxErrors(written));
            using (var demuxer = MediaFormatRegistry.OpenDemuxer(written))
            {
                var track = Assert.Single(demuxer.Tracks);
                Assert.True(track.Config.Hdr10PlusInBlockAdditions);
                for (var i = 0; track.ReadNext() is { } sample; i++)
                {
                    var addition = Assert.Single(sample.Additions!);
                    Assert.Equal(BlockAddition.ItuT35, addition.Id);
                    Assert.Equal(Hdr10PlusMessage(i), addition.Data.ToArray());
                }
            }

            // Remux (copied track entry): additions, key frames and frames unchanged.
            var doc = await Mkv.ReadAsync(written, Ct);
            Assert.True(doc.Tracks.OfType<VideoTrack>().Single().Hdr10Plus);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = remuxed }, ContainerKind.Matroska, null, Ct);
            Assert.Equal(frames, PacketsWithHdr10Plus(remuxed));
            Assert.Equal(MediaProbe.PacketHashes(written, "-map 0:v"), MediaProbe.PacketHashes(remuxed, "-map 0:v"));
            Assert.Equal(KeyFrames(written), KeyFrames(remuxed));
        }
        finally
        {
            MediaProbe.Delete(written, remuxed);
        }
    }

    [Fact]
    public async Task Mp4_cannot_keep_vp9_block_additions_and_says_so()
    {
        var written = MuxWithBlockAdditions(out _);
        var output = MediaProbe.TempPath(".mp4");
        try
        {
            var doc = await Mkv.ReadAsync(written, Ct);
            var (_, support) = Assert.Single(await Remuxer.CheckAsync(doc, ContainerKind.Mp4, Ct));
            Assert.Equal(TrackSupportLevel.Passthrough, support.Level);
            Assert.Contains("HDR10+", support.Reason!, StringComparison.Ordinal);

            var tracks = await TrackImporter.InspectAsync(written, ContainerKind.Mp4, Ct);
            Assert.Contains("HDR10+", Assert.Single(tracks).Details, StringComparison.Ordinal);
            Assert.NotNull(tracks[0].Support.Reason);

            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, null, Ct);
            Assert.Equal(0, PacketsWithHdr10Plus(output));
            Assert.Equal(MediaProbe.PacketHashes(written, "-map 0:v"), MediaProbe.PacketHashes(output, "-map 0:v"));
        }
        finally
        {
            MediaProbe.Delete(written, output);
        }
    }

    private static string KeyFrames(string path) =>
        Fixtures.Run("ffprobe", $"-v error -select_streams v:0 -show_packets -show_entries packet=flags -of csv=p=0 {Fixtures.Quote(path)}");

    // ------------------------------------------------------------------ corpus (MMW_CORPUS)

    private static string Sample(string name)
    {
        var path = Corpus.Directory is { } dir ? Path.Combine(dir, "High Dynamic Range", "HDR10+", name) : string.Empty;
        Corpus.Require(File.Exists(path) ? path : string.Empty);
        MediaProbe.RequireFfmpeg();
        return path;
    }

    [Fact]
    public async Task Corpus_webm_vp9_hdr10plus_is_kept_in_matroska()
    {
        var source = Sample("{HDR10+, VP9 - WebM} Dublin Galaxy S10+ Sample.webm");
        var output = MediaProbe.TempPath(".webm");
        try
        {
            var doc = await Mkv.ReadAsync(source, Ct);
            doc.Tracks.Remove(doc.Tracks.Single(t => t is AudioTrack));
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Matroska, null, Ct);
            Assert.Equal(PacketsWithHdr10Plus(source), PacketsWithHdr10Plus(output));
            Assert.Equal(MediaProbe.PacketHashes(source, "-map 0:v"), MediaProbe.PacketHashes(output, "-map 0:v"));
            Assert.Equal(KeyFrames(source), KeyFrames(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Corpus_hevc_hdr10plus_is_detected_and_travels_in_the_sei()
    {
        var source = Sample("Version 1 Profile A.mkv");
        var mp4Source = Sample("Version 0.mp4");
        var output = MediaProbe.TempPath(".mp4");
        try
        {
            // Detected from the SEI messages, in Matroska and in MP4 sources.
            var doc = await Mkv.ReadAsync(source, Ct);
            Assert.True((await VideoBitstreamScan.ScanAsync(doc.Tracks.OfType<VideoTrack>().Single(), Ct)).Hdr10Plus);
            var mp4 = await Mp4.ReadAsync(mp4Source, Ct);
            Assert.True((await VideoBitstreamScan.ScanAsync(mp4.Tracks.OfType<VideoTrack>().Single(), Ct)).Hdr10Plus);

            // In-band: no warning for MP4, and the frames keep it.
            foreach (var t in doc.Tracks.Where(t => t is not VideoTrack).ToList())
                doc.Tracks.Remove(t);
            var (_, support) = Assert.Single(await Remuxer.CheckAsync(doc, ContainerKind.Mp4, Ct));
            Assert.Null(support.Reason);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, null, Ct);
            Assert.Equal(MediaProbe.PacketHashes(source, "-map 0:v"), MediaProbe.PacketHashes(output, "-map 0:v"));
            Assert.Equal(Hdr10PlusFrames(source, 48), Hdr10PlusFrames(output, 48));
            Assert.True(Hdr10PlusFrames(output, 48) > 40);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    private static int Hdr10PlusFrames(string path, int frames) =>
        Fixtures.Run("ffprobe", $"-v error -select_streams v:0 -read_intervals %+#{frames} -show_frames -show_entries frame_side_data=side_data_type -of csv=p=0 {Fixtures.Quote(path)}")
            .Split('\n').Count(l => l.Contains("2094", StringComparison.Ordinal));

    [Fact]
    public async Task Corpus_av1_hdr10plus_is_detected_and_signalled_in_mp4()
    {
        var source = Sample("{HDR10+, AV1 - Matroska} Movie Sample.mkv");
        var output = MediaProbe.TempPath(".mp4");
        try
        {
            var doc = await Mkv.ReadAsync(source, Ct);
            var video = doc.Tracks.OfType<VideoTrack>().Single();
            Assert.True((await VideoBitstreamScan.ScanAsync(video, Ct)).Hdr10Plus);

            doc.Tracks.Remove(doc.Tracks.Single(t => t is AudioTrack));
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, null, Ct);
            var ftyp = MMW.Formats.Mp4.Boxes.Mp4Layout.Read(output).Ftyp!.Payload;
            Assert.Contains("cdm4", System.Text.Encoding.ASCII.GetString(ftyp), StringComparison.Ordinal);
            Assert.Equal(MediaProbe.PacketHashes(source, "-map 0:v"), MediaProbe.PacketHashes(output, "-map 0:v"));
            var frames = Fixtures.Run("ffprobe", $"-v error -select_streams v:0 -read_intervals %+#24 -show_frames -show_entries frame_side_data=side_data_type -of csv=p=0 {Fixtures.Quote(output)}");
            Assert.Equal(24, frames.Split('\n').Count(l => l.Contains("2094", StringComparison.Ordinal)));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }
}
