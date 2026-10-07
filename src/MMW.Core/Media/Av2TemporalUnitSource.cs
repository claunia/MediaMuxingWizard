using System.Globalization;
using MMW.Core.Diagnostics;
using MMW.Core.Media.Codecs;
using MMW.Core.Resources;

namespace MMW.Core.Media;

/// <summary>
/// AV2 from a stream of packets holding whole temporal units (IVF frames, Annex B units, Matroska blocks as the
/// reference encoder writes them, which may hold several temporal units each) as ISOBMFF-style samples: one per
/// temporal unit, without temporal delimiters or configuration OBUs, which move to the 'av2C' record.
/// </summary>
/// <remarks>
/// Decoding times count temporal units at the stream's frame rate (the smallest step between packet timestamps, or
/// <c>frameRate</c>); presentation times follow the display order of the units' output frames
/// (<see cref="Av2DisplayOrder"/>), which differs from decoding order unless the sequence asks for monotonic output.
/// </remarks>
public sealed class Av2TemporalUnitSource : ISampleSource, IDisposable
{
    private const int ProbePackets = 64;

    private readonly ISampleSource _inner;
    private readonly Queue<MediaSample> _queue = new();
    private readonly List<byte[]> _configuration;
    private readonly long _firstTime;
    private readonly long _frameTicks;
    private Av2DisplayOrder _order = new();
    private long _units;
    private bool _warnedConfiguration;

    public Av2TemporalUnitSource(ISampleSource inner, double frameRate = 0)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        var config = inner.Config;
        var timescale = Math.Max(1u, config.Timescale);

        // The first packets give the configuration OBUs, the first timestamp and the frame duration.
        // Packets are presentation-timed (Matroska blocks of single units may be out of order): the frame duration is
        // the smallest step between their sorted times.
        _configuration = [];
        var times = new List<long>();
        inner.Reset();
        for (var i = 0; i < ProbePackets && inner.ReadNext() is { } packet; i++)
        {
            times.Add(packet.Pts);
            if (i == 0)
            {
                var data = packet.GetData().Span;
                foreach (var range in Av2.SplitTemporalUnits(data))
                {
                    var configuration = new List<byte[]>();
                    Av2.ToSample(data[range], configuration);
                    if (configuration.Count > 0)
                    {
                        _configuration.AddRange(configuration);
                        break;
                    }
                }
            }
        }

        times.Sort();
        var step = long.MaxValue;
        for (var i = 1; i < times.Count; i++)
        {
            if (times[i] > times[i - 1])
                step = Math.Min(step, times[i] - times[i - 1]);
        }

        inner.Reset();
        if (_configuration.Count == 0)
            throw new InvalidDataException(Strings.Error_Av2NoSequenceHeader);
        _firstTime = times.Count == 0 ? 0 : times[0];
        _frameTicks = frameRate > 0 ? Math.Max(1, (long)Math.Round(timescale / frameRate))
            : step != long.MaxValue ? step
            : config.DefaultSampleDuration > 0 ? config.DefaultSampleDuration
            : Math.Max(1, timescale / 25);

        var av2C = Av2.BuildConfigurationBox(_configuration);
        var (sequence, interpretation) = Av2.Describe(av2C);
        Config = config with
        {
            Codec = CodecType.Av2,
            Extradata = av2C,
            Width = sequence?.Width ?? config.Width,
            Height = sequence?.Height ?? config.Height,
            BitsPerSample = sequence?.BitDepth ?? config.BitsPerSample,
            ParNumerator = interpretation is { SarWidth: > 0, SarHeight: > 0 } ? interpretation.SarWidth : config.ParNumerator,
            ParDenominator = interpretation is { SarWidth: > 0, SarHeight: > 0 } ? interpretation.SarHeight : config.ParDenominator,
            Color = config.Color.IsSpecified ? config.Color : interpretation?.Color ?? config.Color,
            StreamColor = interpretation?.Color ?? config.StreamColor,
            FrameRate = timescale / (double)_frameTicks,
            Timescale = timescale,
            DefaultSampleDuration = _frameTicks,
            VideoProfile = sequence is null ? config.VideoProfile : Av2.ProfileLevel(sequence),
        };
        Reset();
    }

    public uint TrackId => _inner.TrackId;

    public CodecConfig Config { get; }

    public TimeSpan StartOffset => _inner.StartOffset;

    public long MediaStart => _inner.MediaStart;

    public TimeSpan Duration => _inner.Duration;

    public long SampleCountHint => _inner.SampleCountHint;

    public MediaSample? ReadNext()
    {
        while (_queue.Count == 0)
        {
            if (_inner.ReadNext() is not { } packet)
                return null;
            var data = packet.GetData().Span;
            foreach (var range in Av2.SplitTemporalUnits(data))
                _queue.Enqueue(Unit(data[range]));
        }

        return _queue.Dequeue();
    }

    private MediaSample Unit(ReadOnlySpan<byte> temporalUnit)
    {
        var display = _order.Next(temporalUnit);
        var configuration = new List<byte[]>();
        var sample = Av2.ToSample(temporalUnit, configuration);

        // Repeated configuration OBUs move to 'av2C'; ones that differ from it (a new sequence) stay in the sample.
        if (configuration.Any(c => !_configuration.Any(k => k.AsSpan().SequenceEqual(c))))
        {
            if (!_warnedConfiguration)
            {
                _warnedConfiguration = true;
                AppLog.Warn(string.Format(CultureInfo.CurrentCulture, Strings.Log_Av2SequenceHeaderChanges, TrackId));
            }

            var o = new List<byte>();
            foreach (var obu in configuration)
            {
                Av2.WriteLeb128(o, obu.Length);
                o.AddRange(obu);
            }

            sample = [.. o, .. sample];
        }

        var index = _units++;
        var dts = _firstTime + index * _frameTicks;
        var pts = _firstTime + display * _frameTicks;
        return new MediaSample
        {
            Dts = dts,
            CtsOffset = pts - dts,
            Duration = _frameTicks,
            IsSync = Av2.IsSync(sample),
            Data = sample,
        };
    }

    public void Reset()
    {
        _inner.Reset();
        _queue.Clear();
        _units = 0;
        _order = new Av2DisplayOrder();
        _order.AddConfiguration(_configuration);
    }

    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
