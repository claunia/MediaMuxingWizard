namespace MMW.Core.Media;

/// <summary>Channel layout produced by an AAC conversion (Subler's mixdown choices).</summary>
public enum AudioMixdown
{
    /// <summary>Stereo with Dolby Pro Logic II matrix encoding of the surround channels.</summary>
    DolbyProLogicII,

    /// <summary>Stereo with Dolby Surround / Pro Logic matrix encoding of the surround channels.</summary>
    DolbyProLogic,

    /// <summary>Plain stereo downmix.</summary>
    Stereo,

    /// <summary>Mono downmix.</summary>
    Mono,

    /// <summary>Keep the source channels (up to the 7.1 layouts AAC supports).</summary>
    Multichannel,
}

/// <summary>Codec an audio track is converted to.</summary>
public enum AudioConversionTarget
{
    Aac,
    Ac3,
}

/// <summary>Settings of an audio conversion (Subler's audio preferences).</summary>
public sealed record AudioConversionSettings
{
    /// <summary>Lowest AAC bitrate per channel, in kbit/s.</summary>
    public const int MinBitratePerChannel = 64;

    /// <summary>Highest AAC bitrate per channel, in kbit/s.</summary>
    public const int MaxBitratePerChannel = 320;

    /// <summary>Default AAC bitrate per channel, in kbit/s.</summary>
    public const int DefaultBitratePerChannel = 96;

    /// <summary>Highest dynamic range compression factor.</summary>
    public const double MaxDrc = 4;

    /// <summary>Highest sample rate of a converted AAC track; higher rates are resampled.</summary>
    public const int MaxAacSampleRate = 48000;

    /// <summary>Dolby Pro Logic II, 96 kbit/s per channel, no dynamic range compression.</summary>
    public static AudioConversionSettings Default { get; } = new();

    /// <summary>Channel layout of an AAC conversion (AC-3 conversions always keep up to 5.1 channels).</summary>
    public AudioMixdown Mixdown { get; init; } = AudioMixdown.DolbyProLogicII;

    /// <summary>AAC bitrate per output channel in kbit/s (clamped to 64–320).</summary>
    public int BitratePerChannel { get; init; } = DefaultBitratePerChannel;

    /// <summary>
    /// Dynamic range compression applied when decoding AC-3/E-AC-3 (0 = off, 1 = the stream's own DRC, up to 4 =
    /// stronger), as FFmpeg's <c>drc_scale</c>.
    /// </summary>
    public double Drc { get; init; }

    /// <summary><see cref="BitratePerChannel"/> clamped to the valid range.</summary>
    public int EffectiveBitratePerChannel => Math.Clamp(BitratePerChannel, MinBitratePerChannel, MaxBitratePerChannel);

    /// <summary><see cref="Drc"/> clamped to the valid range.</summary>
    public double EffectiveDrc => double.IsFinite(Drc) ? Math.Clamp(Drc, 0, MaxDrc) : 0;

    /// <summary>The mixdown actually used for a source with <paramref name="inputChannels"/> channels.</summary>
    /// <remarks>
    /// Matrix encodings and "multichannel" only make sense for surround sources: a mono source stays mono and a
    /// stereo source stays stereo with them (explicit "Stereo"/"Mono" choices are always honoured).
    /// </remarks>
    public AudioMixdown EffectiveMixdown(int inputChannels) => Mixdown switch
    {
        AudioMixdown.Mono or AudioMixdown.Stereo => Mixdown,
        _ when inputChannels == 1 => AudioMixdown.Mono,
        _ when inputChannels == 2 => AudioMixdown.Stereo,
        _ => Mixdown,
    };

    /// <summary>Number of channels of the AAC conversion of a source with <paramref name="inputChannels"/> channels.</summary>
    public int AacChannels(int inputChannels) => EffectiveMixdown(inputChannels) switch
    {
        AudioMixdown.Mono => 1,
        AudioMixdown.Multichannel => inputChannels <= 0 ? 2 : Math.Min(inputChannels, 8),
        _ => 2,
    };

    /// <summary>Number of channels of the AC-3 conversion (up to 5.1).</summary>
    public static int Ac3Channels(int inputChannels) => inputChannels <= 0 ? 2 : Math.Min(inputChannels, 6);

    /// <summary>AC-3 bitrate in kbit/s: 640 for 5.1, 448 for 3–4 channels, 192 for stereo, 96 for mono.</summary>
    public static int Ac3Bitrate(int channels) => channels switch
    {
        >= 5 => 640,
        >= 3 => 448,
        2 => 192,
        _ => 96,
    };

    /// <summary>Sample rate of the AAC conversion: the source rate, resampled to at most 48 kHz.</summary>
    public static int AacSampleRate(int inputRate) => inputRate switch
    {
        <= 0 => 48000,
        > MaxAacSampleRate when inputRate % 44100 == 0 => 44100,
        > MaxAacSampleRate => 48000,
        _ => inputRate,
    };

    /// <summary>Sample rate of the AC-3 conversion (AC-3 only supports 32, 44.1 and 48 kHz).</summary>
    public static int Ac3SampleRate(int inputRate) => inputRate switch
    {
        32000 or 44100 or 48000 => inputRate,
        _ when inputRate > 0 && inputRate % 44100 == 0 => 44100,
        _ => 48000,
    };

    /// <summary>
    /// The configuration a conversion of <paramref name="input"/> is expected to produce (codec, channels, sample
    /// rate; no extradata), used to check container support and to describe pending tracks before converting.
    /// </summary>
    public CodecConfig PredictOutput(CodecConfig input, AudioConversionTarget target)
    {
        ArgumentNullException.ThrowIfNull(input);
        var aac = target == AudioConversionTarget.Aac;
        var rate = aac ? AacSampleRate(input.SampleRate) : Ac3SampleRate(input.SampleRate);
        return input with
        {
            Codec = aac ? CodecType.Aac : CodecType.Ac3,
            SourceCodecId = aac ? "mp4a" : "ac-3",
            Extradata = null,
            Native = null,
            Channels = aac ? AacChannels(input.Channels) : Ac3Channels(input.Channels),
            SampleRate = rate,
            Timescale = (uint)rate,
            DefaultSampleDuration = aac ? 1024 : 1536,
            BitsPerSample = 0,
            PcmBigEndian = false,
            PcmFloat = false,
            IsAtmos = false,
            CodecDelay = TimeSpan.Zero,
            SeekPreRoll = TimeSpan.Zero,
        };
    }
}

/// <summary>
/// Creates audio converters (decode → resample/downmix → encode). The implementation lives in an optional component
/// (FFmpeg) registered with <see cref="MediaFormatRegistry.Register(IAudioConverterFactory)"/>, so the remuxer does not
/// depend on it.
/// </summary>
public interface IAudioConverterFactory
{
    /// <summary>Name and version of the implementation (e.g. "FFmpeg 9.0").</summary>
    string Name { get; }

    /// <summary>True when conversions can be performed (the native libraries were loaded).</summary>
    bool IsAvailable { get; }

    /// <summary>Why <see cref="IsAvailable"/> is false; null when available.</summary>
    string? UnavailableReason { get; }

    /// <summary>True when the codec of <paramref name="config"/> can be decoded.</summary>
    bool CanDecode(CodecConfig config);

    /// <summary>
    /// Wraps <paramref name="source"/> (positioned at its first sample) into a sample source producing the converted
    /// track. The returned source implements <see cref="IDisposable"/> and must be disposed; it never disposes
    /// <paramref name="source"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">The converter is unavailable or the codec cannot be decoded.</exception>
    /// <exception cref="InvalidDataException">The track could not be decoded.</exception>
    ISampleSource Create(ISampleSource source, AudioConversionTarget target, AudioConversionSettings settings);
}
