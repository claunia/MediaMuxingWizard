using MMW.TestSupport;
using static MMW.Media.Conversion.Tests.ConversionFixtures;

namespace MMW.Media.Conversion.Tests;

/// <summary>Chapter thumbnails: decoded with libavformat/libavcodec, scaled and encoded as JPEG.</summary>
public sealed class ThumbnailTests
{
    /// <summary>Writes the JPEG to a temporary file and returns ffprobe's codec/size.</summary>
    private static (string Codec, int Width, int Height) Probe(byte[] jpeg)
    {
        var path = MediaProbe.TempPath(".jpg");
        try
        {
            File.WriteAllBytes(path, jpeg);
            var s = Assert.Single(MediaProbe.Streams(path));
            Assert.Empty(DecodeErrors(path));
            return (s.Codec, s.Width, s.Height);
        }
        finally
        {
            MediaProbe.Delete(path);
        }
    }

    [Fact]
    public async Task Capture_returns_a_jpeg_at_most_320_wide()
    {
        var jpeg = await ThumbnailGenerator.CaptureAsync(VideoMp4(), TimeSpan.FromSeconds(1.5), cancellationToken: Ct);
        Assert.NotNull(jpeg);
        Assert.Equal([0xFF, 0xD8], jpeg[..2]);
        var (codec, width, height) = Probe(jpeg);
        Assert.Equal("mjpeg", codec);
        Assert.Equal(320, width);
        Assert.Equal(180, height);
    }

    [Fact]
    public async Task Capture_applies_the_pixel_aspect_ratio_and_honours_the_track_id()
    {
        var jpeg = await ThumbnailGenerator.CaptureAsync(AnamorphicMkv(), 1, TimeSpan.FromSeconds(2), 320, Ct);
        Assert.NotNull(jpeg);
        var (_, width, height) = Probe(jpeg);
        Assert.Equal(320, width);
        Assert.Equal(180, height); // 853.3×480 display → 320×180
    }

    [Fact]
    public async Task Frames_at_different_times_differ_and_times_past_the_end_give_the_last_frame()
    {
        var times = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3.9), TimeSpan.FromSeconds(60) };
        var images = await ThumbnailGenerator.CaptureManyAsync(VideoMp4(), null, times, 160, null, Ct);
        Assert.Equal(4, images.Count);
        Assert.All(images, Assert.NotNull);
        Assert.NotEqual(images[0], images[1]);
        Assert.Equal(160, Probe(images[3]!).Width);
    }

    [Fact]
    public async Task Files_without_video_are_rejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ThumbnailGenerator.CaptureAsync(FlacStereoMkv(), TimeSpan.Zero, cancellationToken: Ct));
        await Assert.ThrowsAsync<FileNotFoundException>(() => ThumbnailGenerator.CaptureAsync("/nonexistent/file.mp4", TimeSpan.Zero, cancellationToken: Ct));
    }
}
