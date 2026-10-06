using MMW.Core.Media;
using MMW.TestSupport;
using static MMW.Media.Conversion.Tests.ConversionFixtures;
using static MMW.Media.Conversion.Tests.SyntheticSubtitles;

namespace MMW.Media.Conversion.Tests;

/// <summary>PGS and VobSub decoding into RGBA bitmaps (synthetic packets, and the corpus when enabled).</summary>
public sealed class BitmapSubtitleTests
{
    private static void RequireLibraries()
    {
        if (!Interop.FFmpegLoader.IsAvailable)
            Assert.Skip($"FFmpeg libraries not available: {Interop.FFmpegLoader.Error}");
    }

    private static async Task<List<SubtitleBitmap>> DecodeAll(ISampleSource source)
    {
        var list = new List<SubtitleBitmap>();
        await foreach (var b in BitmapSubtitleDecoder.DecodeAsync(source, Ct))
            list.Add(b);
        return list;
    }

    private static void AssertWhiteTopHalf(SubtitleBitmap b)
    {
        Assert.Equal(Width, b.Width);
        Assert.Equal(Height, b.Height);
        Assert.Equal(Width * Height * 4, b.Rgba.Length);
        Assert.Equal(255, b.AlphaAt(5, 2));
        Assert.True(b.Rgba[((2 * Width) + 5) * 4] > 200, "Expected a white pixel.");
    }

    [Fact]
    public async Task Pgs_display_sets_decode_with_times_from_clear_events()
    {
        RequireLibraries();
        var source = new MemorySampleSource(PgsConfig(),
        [
            Sample(1000, PgsShow(100, 900, forced: false, 1)),
            Sample(3000, PgsClear(2)),
            Sample(4000, PgsShow(200, 950, forced: true, 3)),
            Sample(6500, PgsClear(4)),
        ]);
        Assert.True(BitmapSubtitleDecoder.CanDecode(source.Config));
        var bitmaps = await DecodeAll(source);

        Assert.Equal(2, bitmaps.Count);
        var (a, b) = (bitmaps[0], bitmaps[1]);
        Assert.Equal(TimeSpan.FromSeconds(1), a.Start);
        Assert.Equal(TimeSpan.FromSeconds(3), a.End);
        Assert.Equal((100, 900), (a.X, a.Y));
        Assert.False(a.Forced);
        AssertWhiteTopHalf(a);
        Assert.Equal(0, a.AlphaAt(5, Height - 2)); // bottom half transparent
        Assert.Equal((1920, 1080), (a.CanvasWidth, a.CanvasHeight));

        Assert.Equal(TimeSpan.FromSeconds(4), b.Start);
        Assert.Equal(TimeSpan.FromSeconds(6.5), b.End);
        Assert.True(b.Forced);
    }

    [Fact]
    public async Task Pgs_without_a_clear_event_ends_with_the_track()
    {
        RequireLibraries();
        var source = new MemorySampleSource(PgsConfig(), [Sample(1000, PgsShow(10, 10, false, 1))]) { Duration = TimeSpan.FromSeconds(3) };
        var bitmap = Assert.Single(await DecodeAll(source));
        Assert.Equal(TimeSpan.FromSeconds(3), bitmap.End);
    }

    [Fact]
    public async Task VobSub_packets_decode_with_idx_palette_stop_time_and_forced_flag()
    {
        RequireLibraries();
        var source = new MemorySampleSource(VobSubConfig(),
        [
            Sample(500, Spu(100, 300, 2000, forced: false)),
            Sample(5000, Spu(60, 400, 1000, forced: true)),
        ]);
        var bitmaps = await DecodeAll(source);
        Assert.Equal(2, bitmaps.Count);

        var a = bitmaps[0];
        Assert.Equal(TimeSpan.FromMilliseconds(500), a.Start);
        AssertClose(2500, a.End.TotalMilliseconds, 15, "End time (ms)");
        Assert.False(a.Forced);
        Assert.Equal(255, a.AlphaAt(Width / 2, Height / 2));
        Assert.True(a.Rgba[((Height / 2 * a.Width) + (Width / 2)) * 4] > 200, "Expected the white idx palette entry.");
        Assert.Equal((720, 480), (a.CanvasWidth, a.CanvasHeight));

        var b = bitmaps[1];
        Assert.Equal(TimeSpan.FromSeconds(5), b.Start);
        AssertClose(6000, b.End.TotalMilliseconds, 15, "End time (ms)");
        Assert.True(b.Forced);
    }

    [Fact]
    public async Task Text_subtitles_are_rejected()
    {
        var source = new MemorySampleSource(new CodecConfig { Codec = CodecType.TextUtf8, Kind = Core.Model.TrackKind.Subtitle, Timescale = 1000 }, []);
        Assert.False(BitmapSubtitleDecoder.CanDecode(source.Config));
        await Assert.ThrowsAsync<NotSupportedException>(() => DecodeAll(source));
    }

    public static IEnumerable<TheoryDataRow<string>> CorpusSubtitleFiles() =>
        Corpus.Files(".mkv", ".mks")
            .Where(f => f.Length == 0 || Path.GetFileName(f).Contains("DVD", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(f).Contains("PGS", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(f).Contains("VobSub", StringComparison.OrdinalIgnoreCase))
            .DefaultIfEmpty(string.Empty)
            .Select(f => new TheoryDataRow<string>(f));

    [Theory]
    [MemberData(nameof(CorpusSubtitleFiles))]
    public async Task Corpus_bitmap_subtitles_decode(string file)
    {
        Corpus.Require(file);
        RequireFfmpeg();
        using var demuxer = Core.Media.MediaFormatRegistry.OpenDemuxer(file);
        var tracks = demuxer.Tracks.Where(t => t.Config.Codec is CodecType.Pgs or CodecType.VobSub).ToList();
        if (tracks.Count == 0)
            Assert.Skip("No bitmap subtitle track.");
        foreach (var track in tracks)
        {
            var count = 0;
            var previous = TimeSpan.Zero;
            await foreach (var b in BitmapSubtitleDecoder.DecodeAsync(track, Ct))
            {
                Assert.True(b.Width > 0 && b.Height > 0);
                Assert.Equal(b.Width * b.Height * 4, b.Rgba.Length);
                Assert.True(b.End > b.Start, $"Bitmap at {b.Start} has no duration.");
                Assert.True(b.Start >= previous - TimeSpan.FromSeconds(10), "Bitmaps are out of order.");
                Assert.Contains(b.Rgba.Where((_, i) => i % 4 == 3), a => a > 0);
                previous = b.Start;
                if (++count >= 200)
                    break;
            }

            Assert.True(count > 0, $"No bitmap decoded from track {track.TrackId} of {Path.GetFileName(file)}.");
            TestContext.Current.SendDiagnosticMessage($"{Path.GetFileName(file)} track {track.TrackId}: {count} bitmap(s) decoded.");
        }
    }
}
