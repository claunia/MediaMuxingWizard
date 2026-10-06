using System.Text;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// Dolby Vision repair on corpus files: the container's configuration is hidden (dvcC/dvvC boxes, BlockAdditionMapping),
/// then rebuilt from the bitstream's RPUs and written back. The result must describe the same profile as the original.
/// </summary>
public sealed class DolbyVisionRepairTests
{
    private static readonly string[] Extensions = [".mp4", ".mkv"];

    /// <summary>The smallest MP4 and Matroska file of each Dolby Vision profile in the corpus.</summary>
    public static IEnumerable<TheoryDataRow<string>> Samples()
    {
        var root = Corpus.Directory is { } dir ? Path.Combine(dir, "High Dynamic Range", "Dolby Vision") : null;
        if (root is null || !Directory.Exists(root))
            return [new TheoryDataRow<string>(string.Empty)];
        return Directory.EnumerateDirectories(root, "Profile *")
            .SelectMany(d => Extensions.Select(ext => Directory.EnumerateFiles(d, "*" + ext)
                .Where(f => !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal))
                .OrderBy(f => new FileInfo(f).Length)
                .FirstOrDefault()))
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .Select(f => new TheoryDataRow<string>(f))
            .ToList();
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Missing_configuration_is_rebuilt_from_the_bitstream(string file)
    {
        Corpus.Require(file);
        var copy = Fixtures.CopyToTemp(file);
        try
        {
            var original = Single(file.EndsWith(".mkv", StringComparison.Ordinal) ? await Mkv.ReadAsync(file, Ct) : await Mp4.ReadAsync(file, Ct)).DolbyVision;
            Assert.NotNull(original);

            Strip(copy);
            var handler = copy.EndsWith(".mkv", StringComparison.Ordinal) ? (IContainerHandler)Mkv : Mp4;
            var doc = await handler.ReadAsync(copy, Ct);
            var video = Single(doc);
            Assert.Null(video.DolbyVisionRecord);
            Assert.True(DolbyVisionDetector.NeedsCheck(video));

            var detection = await DolbyVisionDetector.DetectAsync(video, Ct);
            Assert.NotNull(detection);
            Assert.Equal(original.Profile, detection.Profile);
            if (original.Profile is not (4 or 10)) // the corpus' profile 4 says compatibility 0 (Dolby's table: 2); AV1 files disagree with their RPU
                Assert.Equal(original.BlSignalCompatibilityId, detection.BlSignalCompatibilityId);
            Assert.Equal(original.ElPresent, detection.ElPresent);

            video.DolbyVisionRecord = detection.ConfigurationRecord;
            await handler.SaveAsync(doc, new SaveOptions(), cancellationToken: Ct);

            var repaired = Single(await handler.ReadAsync(copy, Ct)).DolbyVision;
            Assert.NotNull(repaired);
            Assert.Equal((detection.Profile, detection.Level, detection.BlSignalCompatibilityId), (repaired.Profile, repaired.Level, repaired.BlSignalCompatibilityId));

            if (MediaProbe.HasFfmpeg)
            {
                var probe = Fixtures.Run("ffprobe", $"-v error -select_streams V:0 -show_entries stream_side_data=dv_profile,dv_bl_signal_compatibility_id -of csv=p=0 {Fixtures.Quote(copy)}");
                Assert.StartsWith($"{detection.Profile},", probe.Trim(), StringComparison.Ordinal);
            }
        }
        finally
        {
            MediaProbe.Delete(copy);
        }
    }

    private static VideoTrack Single(MediaDocument doc) => doc.Tracks.OfType<VideoTrack>().First();

    /// <summary>Hides the Dolby Vision configuration the way broken muxers leave it: renamed to boxes/elements readers skip.</summary>
    private static void Strip(string path)
    {
        var data = File.ReadAllBytes(path);
        if (path.EndsWith(".mkv", StringComparison.Ordinal))
        {
            // BlockAdditionMapping (0x41E4) → an unknown ID, in the track header only.
            var limit = Math.Min(data.Length - 1, 1 << 20);
            for (var i = 0; i < limit; i++)
                if (data[i] == 0x41 && data[i + 1] == 0xE4)
                    data[i + 1] = 0xFF;
        }
        else
        {
            Replace(data, "dvcC", "free");
            Replace(data, "dvvC", "free");
            Replace(data, "dvwC", "free");
            Replace(data, "dvh1", "hvc1");
            Replace(data, "dvhe", "hev1");
            Replace(data, "dav1", "av01");
        }

        File.WriteAllBytes(path, data);
    }

    private static void Replace(byte[] data, string from, string to)
    {
        var a = Encoding.ASCII.GetBytes(from);
        var b = Encoding.ASCII.GetBytes(to);
        var span = data.AsSpan();
        for (var i = span.IndexOf(a); i >= 0;)
        {
            b.CopyTo(span[i..]);
            var next = span[(i + 4)..].IndexOf(a);
            i = next < 0 ? -1 : i + 4 + next;
        }
    }
}

/// <summary>Dolby Vision through remuxing and importing, on corpus files (MMW_CORPUS).</summary>
public sealed class DolbyVisionMuxTests
{
    private static string Sample(params string[] parts)
    {
        var path = Corpus.Directory is { } dir ? Path.Combine([dir, "High Dynamic Range", "Dolby Vision", .. parts]) : string.Empty;
        Corpus.Require(File.Exists(path) ? path : string.Empty);
        return path;
    }

    private static string Probe(string path) =>
        Fixtures.Run("ffprobe", $"-v error -select_streams V -show_entries stream=codec_tag_string:stream_side_data=dv_profile,dv_bl_signal_compatibility_id -of compact=p=0:nk=1 {Fixtures.Quote(path)}").Trim();

    private static MMW.Formats.Mp4.Boxes.Box VideoEntry(MMW.Formats.Mp4.Boxes.Box trak) => trak.FindPath("mdia/minf/stbl/stsd")!.Children![0];

    [Fact]
    public async Task Profile_5_mp4_stored_as_hev1_is_remuxed_as_dvhe()
    {
        var source = Sample("Profile 5", "{dvhe.05.06 - MP4} Dolby Palette.mp4");
        MediaProbe.RequireFfmpeg();
        var output = MediaProbe.TempPath(".mp4");
        try
        {
            var doc = await Mp4.ReadAsync(source, Ct);
            foreach (var t in doc.Tracks.Where(t => t is not VideoTrack).ToList())
                doc.Tracks.Remove(t);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, null, Ct);

            Assert.Equal("dvhe|5|0", Probe(output));
            var layout = MMW.Formats.Mp4.Boxes.Mp4Layout.Read(output);
            Assert.Contains("dby1", Encoding.ASCII.GetString(layout.Ftyp!.Payload), StringComparison.Ordinal);
            Assert.Equal(MediaProbe.PacketHashes(source, "-map 0:V")[0], MediaProbe.PacketHashes(output, "-map 0:V")[0]);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Dual_track_enhancement_layer_references_the_base_layer_in_mp4()
    {
        var source = Sample("Full Enhancement Layer", "Control.mkv");
        MediaProbe.RequireFfmpeg();
        var output = MediaProbe.TempPath(".mp4");
        try
        {
            var doc = await Mkv.ReadAsync(source, Ct);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, null, Ct);

            var traks = MMW.Formats.Mp4.Boxes.Mp4Layout.Read(output).Moov.Loaded!.FindAll("trak").ToList();
            Assert.Equal(2, traks.Count);
            var baseId = MMW.Formats.Mp4.Boxes.HeaderBoxes.TkhdTrackId(traks[0].Find("tkhd")!);
            Assert.Null(VideoEntry(traks[0]).Find("dvcC"));
            Assert.NotNull(VideoEntry(traks[1]).Find("dvcC"));
            var vdep = traks[1].FindPath("tref/vdep")!.Payload;
            Assert.Equal(baseId, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(vdep));
            Assert.Null(traks[0].FindPath("tref/vdep"));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Raw_hevc_with_rpus_is_imported_as_dolby_vision()
    {
        var source = Sample("Profile 8", "{dvhe.08.04 - Matroska} ASUS Vivobook.mkv");
        MediaProbe.RequireFfmpeg();
        var raw = MediaProbe.TempPath(".hevc");
        var output = MediaProbe.TempPath(".mp4");
        try
        {
            Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(source)} -map 0:V:0 -t 4 -c copy -bsf:v hevc_mp4toannexb -f hevc {Fixtures.Quote(raw)}");
            var original = (await Mkv.ReadAsync(source, Ct)).Tracks.OfType<VideoTrack>().First().DolbyVision!;

            var tracks = await TrackImporter.InspectAsync(raw, ContainerKind.Mp4, Ct);
            var video = Assert.Single(tracks);
            Assert.NotNull(video.Config.DolbyVisionConfig);
            Assert.Equal(3, DolbyVision.ParseConfigurationRecord(video.Config.DolbyVisionConfig).Level); // 1080p24 from the VUI timing
            video.FrameRate = 60; // the level follows the frame rate chosen on import: 1080p60 is level 5

            var doc = new MediaDocument(null, ContainerKind.Mp4);
            var added = Assert.IsType<VideoTrack>(Assert.Single(TrackImporter.AddToDocument(doc, tracks)));
            Assert.Equal((original.Profile, original.BlSignalCompatibilityId, 5), (added.DolbyVision!.Profile, added.DolbyVision.BlSignalCompatibilityId, added.DolbyVision.Level));

            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, null, Ct);
            Assert.EndsWith($"|{original.Profile}|{original.BlSignalCompatibilityId}", Probe(output), StringComparison.Ordinal);
        }
        finally
        {
            MediaProbe.Delete(raw, output);
        }
    }
}
