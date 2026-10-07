using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// The DVB / UWA HDR test streams (EBU, CC BY 4.0): a broadcast VVC stream whose first access unit repeats delimiters
/// and parameter sets before its picture.
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
