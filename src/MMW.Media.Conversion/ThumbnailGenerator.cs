using MMW.Media.Conversion.Interop;
using MMW.Media.Conversion.Resources;

namespace MMW.Media.Conversion;

/// <summary>
/// Captures video frames as small JPEG images (chapter previews), reading the file with libavformat so any container
/// and codec FFmpeg supports works.
/// </summary>
/// <remarks>
/// A capture seeks to the key frame before the requested time and decodes forward to the frame displayed at that
/// time; the image is scaled (bicubic, display aspect ratio applied, limited-range video expanded to full range) to at
/// most the requested width. HDR video is not tone mapped and rotation metadata is ignored.
/// </remarks>
public static class ThumbnailGenerator
{
    /// <summary>Default largest width of a thumbnail, in pixels.</summary>
    public const int DefaultMaxWidth = 320;

    /// <summary>JPEG quantiser scale (2 = best … 31 = worst).</summary>
    public const int DefaultQuality = 3;

    /// <summary>Captures the frame of the main video track displayed at <paramref name="time"/>.</summary>
    /// <returns>JPEG bytes, or null when FFmpeg is unavailable or no frame could be decoded.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file cannot be read or has no decodable video track.</exception>
    public static Task<byte[]?> CaptureAsync(string path, TimeSpan time, int maxWidth = DefaultMaxWidth, CancellationToken cancellationToken = default) =>
        CaptureAsync(path, null, time, maxWidth, cancellationToken);

    /// <summary>Captures the frame of video track <paramref name="trackId"/> (container track ID) displayed at <paramref name="time"/>.</summary>
    /// <param name="path">Media file.</param>
    /// <param name="trackId">MP4 track ID or Matroska track number; null for the main video track.</param>
    /// <param name="time">Presentation time.</param>
    /// <param name="maxWidth">Largest width in pixels.</param>
    /// <param name="cancellationToken">Cancels the capture.</param>
    /// <returns>JPEG bytes, or null when FFmpeg is unavailable or no frame could be decoded.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file cannot be read or has no decodable video track.</exception>
    public static async Task<byte[]?> CaptureAsync(string path, uint? trackId, TimeSpan time, int maxWidth = DefaultMaxWidth,
        CancellationToken cancellationToken = default)
    {
        var images = await CaptureManyAsync(path, trackId, [time], maxWidth, null, cancellationToken);
        return images.Count > 0 ? images[0] : null;
    }

    /// <summary>
    /// Captures several frames with one opened file (e.g. one per chapter); entries are null where no frame could be
    /// decoded. Returns an empty list when FFmpeg is unavailable.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file cannot be read or has no decodable video track.</exception>
    public static Task<IReadOnlyList<byte[]?>> CaptureManyAsync(string path, uint? trackId, IReadOnlyList<TimeSpan> times, int maxWidth = DefaultMaxWidth,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 16);
        if (!File.Exists(path))
            throw new FileNotFoundException(Strings.Conversion_FileNotFound, path);
        if (!FFmpegLoader.IsAvailable)
            return Task.FromResult<IReadOnlyList<byte[]?>>([]);

        return Task.Run<IReadOnlyList<byte[]?>>(() =>
        {
            using var grabber = new FrameGrabber(path, trackId);
            var result = new List<byte[]?>(times.Count);
            for (var i = 0; i < times.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var time = times[i] < TimeSpan.Zero ? TimeSpan.Zero : times[i];
                result.Add(grabber.Capture(time, maxWidth, DefaultQuality, cancellationToken));
                progress?.Report((i + 1) / (double)times.Count);
            }

            return result;
        }, cancellationToken);
    }
}
