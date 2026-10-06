using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;

namespace MMW.Formats.Mp4.Tests;

/// <summary>Full remux of MP4 files through the MP4 demuxer and muxer.</summary>
public sealed class Mp4RemuxTests
{
    private static readonly Mp4Handler s_handler = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Remux_keeps_packets_and_puts_moov_first()
    {
        var source = Mp4Fixtures.MoovAtEnd();
        var output = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N") + ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            var doc = await s_handler.ReadAsync(source, Ct);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, cancellationToken: Ct);

            Assert.Equal(Mp4Fixtures.PacketHashes(source), Mp4Fixtures.PacketHashes(output));
            Assert.Empty(Mp4Fixtures.DemuxErrors(output));
            Assert.StartsWith("ftyp,moov", Mp4Fixtures.BoxOrder(output), StringComparison.Ordinal);
            Assert.Equal(output, doc.Path);
            Assert.False(doc.IsDirty);

            var reread = await s_handler.ReadAsync(output, Ct);
            Assert.Equal(doc.Tracks.Count, reread.Tracks.Count);
            Assert.Equal(3, reread.Chapters.Count);
            Assert.Equal("Fixture", reread.Metadata.GetString(Core.Metadata.TagId.Name));
            var srcProbe = Mp4Fixtures.Probe(source).GetProperty("streams").EnumerateArray().ToList();
            var outProbe = Mp4Fixtures.Probe(output).GetProperty("streams").EnumerateArray().ToList();
            Assert.Equal(srcProbe.Select(s => s.GetProperty("codec_name").GetString()), outProbe.Select(s => s.GetProperty("codec_name").GetString()));
            for (var i = 0; i < srcProbe.Count; i++)
            {
                var a = double.Parse(srcProbe[i].GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                var b = double.Parse(outProbe[i].GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                Assert.InRange(b, a - 0.05, a + 0.05);
            }
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("MMW_KEEP") is null) File.Delete(output);
        }
    }
}
