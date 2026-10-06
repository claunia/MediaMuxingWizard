using System.Collections.Concurrent;
using FFmpeg.AutoGen;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Media.Conversion.Interop;

namespace MMW.Media.Conversion;

/// <summary>
/// <see cref="IAudioConverterFactory"/> backed by FFmpeg (libavcodec decoders, libswresample, the native "aac" and
/// "ac3" encoders). Registered by <see cref="MediaConversion.Register"/>.
/// </summary>
public sealed class FFmpegAudioConverterFactory : IAudioConverterFactory
{
    private readonly ConcurrentDictionary<AVCodecID, bool> _decoders = new();

    /// <summary>The shared instance.</summary>
    public static FFmpegAudioConverterFactory Instance { get; } = new();

    public string Name => FFmpegLoader.Version is { } v ? $"FFmpeg {v}" : "FFmpeg";

    public bool IsAvailable => FFmpegLoader.IsAvailable && HasEncoders.Value;

    public string? UnavailableReason => !FFmpegLoader.IsAvailable ? FFmpegLoader.Error
        : HasEncoders.Value ? null
        : "this FFmpeg build has no native AAC/AC-3 encoder";

    private static readonly Lazy<bool> HasEncoders = new(() => FFmpegLoader.IsAvailable && AvUtil.HasEncoder("aac") && AvUtil.HasEncoder("ac3"));

    public bool CanDecode(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Kind != TrackKind.Audio || !FFmpegLoader.IsAvailable)
            return false;
        var id = CodecMapping.DecoderId(config);
        return id != AVCodecID.AV_CODEC_ID_NONE && _decoders.GetOrAdd(id, AvUtil.HasDecoder);
    }

    public ISampleSource Create(ISampleSource source, AudioConversionTarget target, AudioConversionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsAvailable)
            throw new NotSupportedException($"Audio conversion is not available: {UnavailableReason}");
        if (!CanDecode(source.Config))
            throw new NotSupportedException($"{source.Config.FormatName} audio cannot be decoded by {Name}.");
        return new AudioConverterSource(source, target, settings);
    }
}
