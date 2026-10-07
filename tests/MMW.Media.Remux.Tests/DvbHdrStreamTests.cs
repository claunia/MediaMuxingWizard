using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// The DVB / UWA HDR test streams (EBU, CC BY 4.0): AAC in LATM (stream type 0x11), and a broadcast VVC stream whose
/// first access unit repeats delimiters and parameter sets before its picture.
/// </summary>
public sealed class DvbHdrStreamTests
{
    private static string Source(string name)
    {
        MediaProbe.RequireFfmpeg();
        var path = Corpus.Directory is { } dir ? Path.Combine(dir, "High Dynamic Range", "HDR Vivid", name) : string.Empty;
        Corpus.Require(File.Exists(path) ? path : string.Empty);
        return path;
    }

    /// <summary>MD5 of the first audio track decoded by FFmpeg.</summary>
    private static string Pcm(string path)
    {
        var pcm = MediaProbe.TempPath(".pcm");
        try
        {
            Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(path)} -map 0:a:0 -f s16le {Fixtures.Quote(pcm)}");
            using var stream = File.OpenRead(pcm);
            return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream));
        }
        finally
        {
            MediaProbe.Delete(pcm);
        }
    }

    [Theory]
    [InlineData("DVB_2160p50_HDR_with_HDR_Vivid_DM_H266_20250930_1.ts", ".mkv")]
    [InlineData("DVB_2160p50_HDR_with_Switched_four_DMI_2094-10_2094-40_SL-HDR2_HDR_Vivid_20250926_1.ts", ".mp4")]
    public async Task Latm_audio_is_imported_unchanged(string name, string extension)
    {
        var source = Source(name);
        var target = extension == ".mp4" ? ContainerKind.Mp4 : ContainerKind.Matroska;
        var tracks = await TrackImporter.InspectAsync(source, target, Ct);
        var audio = Assert.Single(tracks, t => t.Config.Kind == TrackKind.Audio);
        Assert.Equal("AAC", audio.Format);
        Assert.Equal("Stereo, 48 kHz", audio.Details);

        var doc = new MediaDocument(null, target);
        TrackImporter.AddToDocument(doc, [audio]);
        var output = MediaProbe.TempPath(extension);
        try
        {
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
            Assert.Equal(Pcm(source), Pcm(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public void The_first_access_unit_keeps_its_timestamps()
    {
        var source = Source("DVB_2160p50_HDR_with_HDR_Vivid_DM_H266_20250930_1.ts");
        var first = Fixtures.Run("ffprobe", $"-v error -select_streams v:0 -read_intervals %+#1 -show_entries packet=pts,dts -of csv=p=0 {Fixtures.Quote(source)}")
            .Trim().TrimEnd(',').Split(',').Select(v => long.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        MediaRemux.EnsureRegistered();
        using var demuxer = MediaFormatRegistry.OpenDemuxer(source, new DemuxOptions());
        var video = demuxer.Tracks.Single(t => t.Config.Kind == TrackKind.Video);
        var sample = video.ReadNext()!;
        Assert.True(sample.IsSync);
        // Relative to the decoding time, the presentation time is the PES's (the leading pictures come before it).
        Assert.Equal(first[0] - first[1], sample.Pts - sample.Dts);
    }
}
