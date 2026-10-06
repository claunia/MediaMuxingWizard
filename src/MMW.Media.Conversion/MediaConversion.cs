using MMW.Core.Media;
using MMW.Media.Conversion.Interop;

namespace MMW.Media.Conversion;

/// <summary>Entry point of the FFmpeg-based conversion component.</summary>
public static class MediaConversion
{
    /// <summary>
    /// Registers the FFmpeg audio converter with <see cref="MediaFormatRegistry"/> (idempotent). Does not load FFmpeg:
    /// the libraries are looked for on first use, and a missing FFmpeg only makes the converter report itself
    /// unavailable.
    /// </summary>
    public static void Register() => MediaFormatRegistry.Register(FFmpegAudioConverterFactory.Instance);

    /// <summary>True when FFmpeg is loaded (conversions, thumbnails and bitmap subtitle decoding work).</summary>
    public static bool IsAvailable => FFmpegLoader.IsAvailable;

    /// <summary>FFmpeg version, or null when unavailable.</summary>
    public static string? Version => FFmpegLoader.Version;

    /// <summary>Why FFmpeg is unavailable, or null.</summary>
    public static string? Error => FFmpegLoader.Error;
}
