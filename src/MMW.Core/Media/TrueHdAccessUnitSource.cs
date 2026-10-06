using MMW.Core.Diagnostics;
using MMW.Core.Media.Codecs;

namespace MMW.Core.Media;

/// <summary>
/// Presents a Dolby TrueHD track as one sample per access unit with exact timing, as required to store it in an ISO
/// base media file ("Dolby TrueHD (MLP) bitstreams within the ISO base media file format", §2.7): the media timescale
/// is the sampling rate, every sample lasts 40/80/160 PCM samples, the track starts with a major sync and samples
/// with a major sync are sync samples. The configuration carries the <c>dmlp</c> payload from the first major sync.
/// </summary>
/// <remarks>
/// Source blocks may hold several access units (they are split on access_unit_length). Timing is rebuilt from the
/// access-unit count, starting at the source time of the first major sync, so container timestamp rounding (e.g.
/// Matroska's 1 ms) is not carried over. Every access unit is validated (check nibble, FBA format_sync); a damaged
/// stream stops the remux, as the specification recommends.
/// </remarks>
public sealed class TrueHdAccessUnitSource : ISampleSource
{
    private readonly ISampleSource _inner;
    private readonly Queue<MediaSample> _pending = new();
    private int _substreams;
    private long _next;
    private bool _started;
    private long _accessUnits;

    public TrueHdAccessUnitSource(ISampleSource inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        Config = Probe(inner);
    }

    public uint TrackId => _inner.TrackId;

    public CodecConfig Config { get; }

    public TimeSpan StartOffset => _inner.StartOffset;

    public long MediaStart => _inner.Config.Timescale == 0 ? 0 : (long)Math.Round((double)_inner.MediaStart * Config.Timescale / _inner.Config.Timescale);

    public TimeSpan Duration => _inner.Duration;

    public long SampleCountHint => _inner.SampleCountHint;

    /// <summary>Number of access units that preceded the first major sync and were dropped.</summary>
    public int DroppedLeadingAccessUnits { get; private set; }

    public MediaSample? ReadNext()
    {
        while (_pending.Count == 0)
        {
            if (_inner.ReadNext() is not { } block)
                return null;
            Split(block);
        }

        return _pending.Dequeue();
    }

    public void Reset()
    {
        _inner.Reset();
        _pending.Clear();
        _substreams = 0;
        _next = 0;
        _started = false;
        _accessUnits = 0;
        DroppedLeadingAccessUnits = 0;
    }

    private void Split(MediaSample block)
    {
        var data = block.GetData();
        var pos = 0;
        while (pos < data.Length)
        {
            var au = TrueHd.Parse(data.Span[pos..], _substreams)
                     ?? throw new InvalidDataException(
                         $"Invalid Dolby TrueHD access unit #{_accessUnits} (bad check nibble, length or format_sync); the stream is damaged or not FBA syntax.");
            if (pos + au.Length > data.Length)
                throw new InvalidDataException($"Dolby TrueHD access unit #{_accessUnits} is split across container blocks.");
            if (au.IsMajorSync)
                _substreams = au.Substreams;

            if (!_started)
            {
                if (!au.IsMajorSync)
                {
                    // The ISO track must start with a major sync (§2.7.2).
                    DroppedLeadingAccessUnits++;
                    pos += au.Length;
                    _accessUnits++;
                    continue;
                }

                _started = true;
                var seconds = _inner.Config.Timescale == 0 ? 0 : (double)block.Pts / _inner.Config.Timescale;
                _next = (long)Math.Round(seconds * Config.Timescale) + (long)AccessUnitsBefore(pos, data.Span) * Config.DefaultSampleDuration;
                if (DroppedLeadingAccessUnits > 0)
                    AppLog.Info($"Dropped {DroppedLeadingAccessUnits} Dolby TrueHD access unit(s) before the first major sync.");
            }

            _pending.Enqueue(new MediaSample
            {
                Dts = _next,
                Duration = Config.DefaultSampleDuration,
                IsSync = au.IsMajorSync,
                Data = data.Slice(pos, au.Length),
            });
            _next += Config.DefaultSampleDuration;
            _accessUnits++;
            pos += au.Length;
        }
    }

    /// <summary>Access units that precede <paramref name="offset"/> inside one block (for the first major sync's time).</summary>
    private static int AccessUnitsBefore(int offset, ReadOnlySpan<byte> data)
    {
        var count = 0;
        var pos = 0;
        var substreams = 0;
        while (pos < offset && TrueHd.Parse(data[pos..], substreams) is { } au)
        {
            if (au.IsMajorSync)
                substreams = au.Substreams;
            pos += au.Length;
            count++;
        }

        return count;
    }

    /// <summary>Reads up to the first major sync to build the track configuration, then rewinds the source.</summary>
    private static CodecConfig Probe(ISampleSource inner)
    {
        inner.Reset();
        try
        {
            var substreams = 0;
            for (var blocks = 0; blocks < 1024 && inner.ReadNext() is { } block; blocks++)
            {
                var data = block.GetData().Span;
                var pos = 0;
                while (pos < data.Length && TrueHd.Parse(data[pos..], substreams) is { } au)
                {
                    if (au.IsMajorSync)
                    {
                        if (au.SampleRate == 0)
                            throw new InvalidDataException("The Dolby TrueHD stream uses a reserved sampling frequency.");
                        return inner.Config with
                        {
                            Codec = CodecType.TrueHd,
                            Timescale = (uint)au.SampleRate,
                            SampleRate = au.SampleRate,
                            DefaultSampleDuration = au.SamplesPerAccessUnit,
                            Extradata = TrueHd.BuildDmlp(au),
                            IsAtmos = au.HasAtmos,
                        };
                    }

                    pos += au.Length;
                }
            }

            throw new InvalidDataException("No Dolby TrueHD major sync (FBA syntax) was found at the start of the track.");
        }
        finally
        {
            inner.Reset();
        }
    }
}
