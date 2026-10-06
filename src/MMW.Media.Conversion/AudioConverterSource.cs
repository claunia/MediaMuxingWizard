using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Media.Conversion.Interop;

namespace MMW.Media.Conversion;

/// <summary>
/// A sample source producing the AAC or AC-3 conversion of another (audio) sample source. Decoding and encoding
/// happen on demand in <see cref="ReadNext"/>, one source packet at a time, so memory use stays constant.
/// </summary>
/// <remarks>
/// <para>
/// Timing: output samples are timed in output-sample units (<see cref="CodecConfig.Timescale"/> = output rate). The
/// first packet starts at 0; <see cref="MediaStart"/> covers the encoder delay (AAC priming, 1024 samples) plus any
/// decoder pre-roll of the source (its <see cref="ISampleSource.MediaStart"/>, e.g. Opus pre-skip or the edit list of
/// an MP4 source), so muxers hide it (MP4 edit list, Matroska CodecDelay). The last packet's duration is trimmed to
/// the decoded audio, so the presentation keeps the source's length.
/// </para>
/// <para>
/// Gaps in the source timestamps are filled with silence and overlaps dropped (beyond 20 ms), so the converted track
/// keeps its synchronisation with the other tracks.
/// </para>
/// </remarks>
public sealed class AudioConverterSource : ISampleSource, IDisposable
{
    private readonly ISampleSource _source;
    private readonly AudioConversionTarget _target;
    private readonly AudioConversionSettings _settings;
    private AudioTranscoder? _transcoder;
    private byte[] _buffer = new byte[64 * 1024];
    private bool _sourceDone;
    private TimeSpan _sourceTrim;
    private long _handedOut;
    private bool _disposed;

    /// <summary>Creates the converter and decodes the start of the source to learn its real format.</summary>
    /// <param name="source">The track to convert, positioned at its first sample (never disposed by this class).</param>
    /// <param name="target">Output codec.</param>
    /// <param name="settings">Mixdown, bitrate and DRC.</param>
    /// <exception cref="NotSupportedException">FFmpeg is unavailable or cannot decode the codec.</exception>
    /// <exception cref="InvalidDataException">No audio could be decoded.</exception>
    public AudioConverterSource(ISampleSource source, AudioConversionTarget target, AudioConversionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        if (source.Config.Kind != TrackKind.Audio)
            throw new NotSupportedException($"{source.Config.FormatName} is not an audio track.");
        FFmpegLoader.EnsureAvailable();
        _source = source;
        _target = target;
        _settings = settings;
        Start();
        var t = _transcoder!;
        var aac = target == AudioConversionTarget.Aac;
        Config = new CodecConfig
        {
            Codec = aac ? CodecType.Aac : CodecType.Ac3,
            Kind = TrackKind.Audio,
            SourceCodecId = aac ? "mp4a" : "ac-3",
            Extradata = aac ? t.Extradata : null,
            Timescale = (uint)t.OutputSampleRate,
            DefaultSampleDuration = t.FrameSize,
            Language = source.Config.Language,
            Name = source.Config.Name,
            Channels = t.OutputChannels,
            SampleRate = t.OutputSampleRate,
            CodecDelay = TimeSpan.FromSeconds((double)t.InitialPadding / t.OutputSampleRate),
        };
        InputLayout = t.InputLayout;
        OutputLayout = t.OutputLayout;
        Bitrate = t.Bitrate;
    }

    /// <summary>Layout of the decoded source ("5.1(side)").</summary>
    public string InputLayout { get; }

    /// <summary>Layout of the output ("stereo").</summary>
    public string OutputLayout { get; }

    /// <summary>Output bitrate in bit/s.</summary>
    public long Bitrate { get; }

    /// <summary>Source packets that could not be decoded so far (skipped, logged).</summary>
    public int DecodeErrors => _transcoder?.DecodeErrors ?? 0;

    public uint TrackId => _source.TrackId;

    public CodecConfig Config { get; }

    public TimeSpan StartOffset { get; private set; }

    public long MediaStart { get; private set; }

    public TimeSpan Duration => _source.Duration;

    public long SampleCountHint =>
        _source.Duration > TimeSpan.Zero ? (long)Math.Ceiling(_source.Duration.TotalSeconds * Config.SampleRate / Config.DefaultSampleDuration) + 2 : -1;

    public MediaSample? ReadNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var t = _transcoder!;
        while (true)
        {
            if (t.TryDequeue(out var packet))
            {
                _handedOut++;
                return new MediaSample { Dts = packet.Pts, Duration = packet.Duration, IsSync = true, Data = packet.Data };
            }

            if (_sourceDone)
                return null;
            Feed(t);
        }
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handedOut == 0 && !_sourceDone)
            return;
        _transcoder?.Dispose();
        _transcoder = null;
        _handedOut = 0;
        _sourceDone = false;
        _source.Reset();
        Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _transcoder?.Dispose();
        _transcoder = null;
    }

    /// <summary>Opens a transcoder and feeds packets until the encoder is configured.</summary>
    private void Start()
    {
        var t = new AudioTranscoder(_source.Config, _target, _settings);
        try
        {
            var first = true;
            while (!t.IsConfigured && !_sourceDone)
            {
                if (first)
                {
                    first = false;
                    if (!FeedFirst(t))
                        break;
                    continue;
                }

                Feed(t);
            }

            if (!t.IsConfigured)
                throw new InvalidDataException($"No {_source.Config.FormatName} audio could be decoded from track {_source.TrackId}.");
        }
        catch
        {
            t.Dispose();
            throw;
        }

        _transcoder = t;
        var preRoll = t.InitialPadding + (long)Math.Round(_sourceTrim.TotalSeconds * t.OutputSampleRate);
        MediaStart = Math.Max(0, preRoll);
    }

    /// <summary>Feeds the first sample and derives the start offset and pre-roll from its time.</summary>
    private bool FeedFirst(AudioTranscoder t)
    {
        var sample = _source.ReadNext();
        if (sample is null)
        {
            _sourceDone = true;
            t.Finish();
            return false;
        }

        // Source media time of the first presented instant vs. the first decoded sample.
        var scale = Math.Max(1u, _source.Config.Timescale);
        var lead = (sample.Pts - _source.MediaStart) / (double)scale;
        StartOffset = _source.StartOffset + (lead > 0 ? TimeSpan.FromSeconds(lead) : TimeSpan.Zero);
        _sourceTrim = lead < 0 ? TimeSpan.FromSeconds(-lead) : TimeSpan.Zero;
        Send(t, sample);
        return true;
    }

    private void Feed(AudioTranscoder t)
    {
        var sample = _source.ReadNext();
        if (sample is null)
        {
            _sourceDone = true;
            t.Finish();
            return;
        }

        Send(t, sample);
    }

    private void Send(AudioTranscoder t, MediaSample sample)
    {
        var size = sample.Size;
        if (_buffer.Length < size)
            _buffer = new byte[Math.Max(size, _buffer.Length * 2)];
        sample.CopyTo(_buffer.AsSpan(0, size));
        t.SendPacket(_buffer.AsSpan(0, size), sample.Pts);
    }
}
