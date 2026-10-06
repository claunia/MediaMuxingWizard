using System.Globalization;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Media.Remux;
using MMW.TestSupport;
using static MMW.Media.Conversion.Tests.ConversionFixtures;

namespace MMW.Media.Conversion.Tests;

/// <summary>
/// Converts every audio track of the optional corpus (<c>MMW_CORPUS</c>) that MP4 cannot carry (or Apple players
/// cannot play) to AAC, and checks that the result decodes cleanly with the source's duration. Corpus files are only
/// read; outputs go to temporary files.
/// </summary>
public sealed class CorpusConversionTests
{
    /// <summary>Largest file converted (MiB); override with <c>MMW_CORPUS_MAX_MB</c>.</summary>
    private static long MaxSize =>
        (long.TryParse(Environment.GetEnvironmentVariable("MMW_CORPUS_MAX_MB"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? mb : 64) * 1024 * 1024;

    public static IEnumerable<TheoryDataRow<string>> Files() =>
        Corpus.Files(".mkv", ".mka", ".webm", ".mp4", ".m4a", ".mov").Select(f => new TheoryDataRow<string>(f));

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Audio_that_mp4_cannot_play_converts_to_aac(string file)
    {
        Corpus.Require(file);
        RequireFfmpeg();
        if (new FileInfo(file).Length > MaxSize)
            Assert.Skip("Larger than the corpus size limit (MMW_CORPUS_MAX_MB).");

        IReadOnlyList<ImportableTrack> tracks;
        try
        {
            tracks = await TrackImporter.InspectAsync(file, ContainerKind.Mp4, Ct);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
        {
            Assert.Skip($"Not importable: {ex.Message}");
            return;
        }

        var candidates = tracks.Where(t => t.Kind == TrackKind.Audio && t.CanConvert &&
                                           (t.ConversionRequired || ConversionDefaults.IsConversion(t.Action) ||
                                            t.Config.Codec is CodecType.Dts or CodecType.Flac or CodecType.Opus or CodecType.Vorbis or CodecType.Mp1 or CodecType.Mp2))
            .ToList();
        if (candidates.Count == 0)
            Assert.Skip("No audio track needing a conversion.");

        foreach (var track in candidates)
        {
            track.Choice = track.Choices.First(c => c.Action == ImportAction.ConvertToAac);
            var expectedChannels = track.Conversion!.AacChannels(track.Config.Channels);
            var doc = new MediaDocument(null, ContainerKind.Mp4);
            TrackImporter.AddToDocument(doc, [track]);
            var output = TempPath(".mp4");
            try
            {
                await Mp4.SaveAsync(doc, new SaveOptions { OutputPath = output }, cancellationToken: Ct);
                var s = Assert.Single(MediaProbe.Streams(output));
                Assert.Equal("aac", s.Codec);
                if (track.Config.Channels > 0)
                    Assert.Equal(expectedChannels, s.Channels);
                if (track.Duration > TimeSpan.Zero)
                    AssertClose(track.Duration.TotalSeconds, s.Duration, 0.1);
                Assert.Empty(DecodeErrors(output));
                TestContext.Current.SendDiagnosticMessage(
                    $"{Path.GetFileName(file)} #{track.TrackId} {track.Format} {track.Details} → AAC {s.Channels} ch, {s.Duration:0.###} s");
            }
            finally
            {
                MediaProbe.Delete(output);
            }
        }
    }
}
