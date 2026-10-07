using System.Buffers.Binary;
using MMW.Core.Media.Codecs;

namespace MMW.Core.Media;

/// <summary>
/// A Video for Windows track (AVI, ASF, Matroska V_MS/VFW/FOURCC) whose codec MP4 stores natively, in that form:
/// MPEG-4 Part 2 (DivX, Xvid …) as MPEG-4 Visual with packed B-frames unpacked, VC-1 Advanced profile with a 'vc-1'
/// entry and H.263 with an 's263' entry. VFW frames are timed in decoding order; presentation times are rebuilt from
/// the picture types.
/// </summary>
public sealed class VfwNativeSource : ISampleSource
{
    private readonly ISampleSource _inner;
    private readonly Queue<MediaSample> _queue = new();
    private readonly bool _interlaced;
    private PictureReorder? _reorder;
    private readonly long _delay;
    private byte[]? _storedBFrame;
    private long _lastDts = long.MinValue;
    private bool _ended;

    private VfwNativeSource(ISampleSource inner, CodecConfig config, bool interlaced)
    {
        _inner = inner;
        Config = config;
        _interlaced = interlaced;
        _delay = config.Codec != CodecType.H263 && HasBFrames() ? Math.Max(1, config.DefaultSampleDuration) : 0;
    }

    /// <summary>The native form of a VFW track for MP4, or null when MP4 has none for its codec.</summary>
    public static VfwNativeSource? TryCreate(ISampleSource inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var c = inner.Config;
        if (c.Codec != CodecType.VfwVideo || Vfw.ParseBitmapInfoHeader(c.Extradata) is not { } bih)
            return null;
        var fourCc = bih.FourCc;
        switch (Kind(fourCc))
        {
            case CodecType.Mpeg4Visual:
            {
                // The VOL is in the extra data, or in front of the first VOP.
                var headers = bih.Extra.Length > 0 ? bih.Extra : FirstFrameHeaders(inner);
                return headers is null ? null : new VfwNativeSource(inner, c with { Codec = CodecType.Mpeg4Visual, Extradata = headers }, false);
            }

            case CodecType.Vc1:
            {
                var at = bih.Extra.AsSpan().IndexOf([(byte)0, (byte)0, (byte)1, (byte)0x0F]);
                if (at < 0)
                    return null;
                var headers = bih.Extra[at..];
                var sequence = Vc1.ParseSequence(headers.AsSpan(4));
                if (sequence.Profile != 3)
                    return null;
                sequence = sequence with { FrameRate = sequence.FrameRate > 0 ? sequence.FrameRate : c.FrameRate };
                return new VfwNativeSource(inner, c with { Codec = CodecType.Vc1, Extradata = Vc1.BuildEntry(headers, sequence) }, sequence.Interlaced);
            }

            case CodecType.H263:
            {
                // d263: vendor, decoder version, H.263 level (from the picture size) and profile 0 (baseline).
                byte level = c.Width * c.Height <= 176 * 144 ? (byte)10 : c.Width * c.Height <= 352 * 288 ? (byte)30 : (byte)70;
                var d263 = QuickTime.Box("d263", [.. "mmw "u8, 0, level, 0]);
                return new VfwNativeSource(inner, c with { Codec = CodecType.H263, Extradata = QuickTime.VisualEntry("s263", c.Width, c.Height, d263) }, false);
            }

            default:
                return null;
        }
    }

    /// <summary>The MP4 codec of a VFW FourCC this adapter converts; null for others.</summary>
    public static CodecType? Kind(string fourCc) => fourCc.ToUpperInvariant() switch
    {
        "DIVX" or "DX50" or "XVID" or "FMP4" or "MP4V" or "M4S2" or "3IV2" or "MP4S" or "DIV5" or "DIV6" => CodecType.Mpeg4Visual,
        "WVC1" or "WMVA" => CodecType.Vc1,
        "H263" or "S263" or "U263" => CodecType.H263,
        _ => null,
    };

    /// <summary>True when MP4 can store a VFW track through this adapter.</summary>
    public static bool CanConvert(CodecConfig config) =>
        config.Codec == CodecType.VfwVideo && Vfw.ParseBitmapInfoHeader(config.Extradata) is { } bih && Kind(bih.FourCc) is not null;

    private static byte[]? FirstFrameHeaders(ISampleSource inner)
    {
        inner.Reset();
        try
        {
            if (inner.ReadNext() is not { } first)
                return null;
            var data = first.GetData().ToArray();
            var vop = PictureReorder.IndexOfStartCode(data, 0xB6);
            return vop > 0 && Mpeg4Part2.LayerSize(data) is not null ? data[..vop] : null;
        }
        finally
        {
            inner.Reset();
        }
    }

    public uint TrackId => _inner.TrackId;

    public CodecConfig Config { get; }

    public TimeSpan StartOffset => _inner.StartOffset;

    public long MediaStart => _inner.MediaStart;

    public TimeSpan Duration => _inner.Duration;

    public long SampleCountHint => _inner.SampleCountHint;

    public MediaSample? ReadNext()
    {
        while (_queue.Count == 0 && !_ended)
        {
            if (_inner.ReadNext() is not { } sample)
            {
                _ended = true;
                if (_storedBFrame is { } stored)
                    Add(stored, _lastDts + Config.DefaultSampleDuration);
                _storedBFrame = null;
                _reorder?.Flush(_queue);
                break;
            }

            var data = sample.GetData().ToArray();
            if (Config.Codec == CodecType.Mpeg4Visual)
                Unpack(data, sample.Dts);
            else if (Config.Codec == CodecType.Vc1 && !(data.Length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1))
                Add([0, 0, 1, 0x0D, .. data], sample.Dts); // MP4 holds start-code EBDUs (SMPTE RP 2025); VFW frames lack the frame start code
            else
                Add(data, sample.Dts);
        }

        return _queue.Count > 0 ? _queue.Dequeue() : null;
    }

    /// <summary>
    /// DivX "packed bitstream": a reference VOP and the following B-VOP share a sample, and the next sample holds a
    /// not-coded placeholder VOP. Each VOP becomes its own sample; the placeholder gives its slot to the B-VOP.
    /// </summary>
    private void Unpack(byte[] data, long dts)
    {
        var first = PictureReorder.IndexOfStartCode(data, 0xB6);
        var second = first < 0 ? -1 : PictureReorder.IndexOfStartCode(data, 0xB6, first + 4);
        if (_storedBFrame is { } stored)
        {
            _storedBFrame = null;
            if (first >= 0 && second < 0 && IsPlaceholder(data, first))
            {
                Add(stored, dts);
                return;
            }

            Add(stored, dts); // no placeholder: the B-VOP takes this slot and the frame follows
            dts += Config.DefaultSampleDuration;
        }

        if (second > 0)
        {
            Add(data[..second], dts);
            _storedBFrame = data[second..];
            return;
        }

        if (first >= 0 && IsPlaceholder(data, first) && data.Length - first <= 8)
            return; // a placeholder without a stored B-VOP: nothing to show (DivX drops it too)
        Add(data, dts);
    }

    /// <summary>A VOP of a few bytes: the not-coded placeholder DivX writes after a packed pair.</summary>
    private static bool IsPlaceholder(byte[] data, int vop) => data.Length - vop <= 12;

    /// <summary>Whether the first frames hold B pictures (packed pairs count): presentation then lags decoding by a frame.</summary>
    private bool HasBFrames()
    {
        _inner.Reset();
        try
        {
            for (var i = 0; i < 300 && _inner.ReadNext() is { } sample; i++)
            {
                var data = sample.GetData().ToArray();
                if (Config.Codec == CodecType.Vc1 ? Vc1.PictureType(data, _interlaced) == 'B' : PictureType(data) == 'B')
                    return true;
            }

            return false;
        }
        finally
        {
            _inner.Reset();
        }
    }

    /// <summary>The type of an MPEG-4 sample: 'B' when it holds a packed pair (its second VOP is a B-VOP).</summary>
    private static char PictureType(byte[] data)
    {
        var first = PictureReorder.IndexOfStartCode(data, 0xB6);
        var second = first < 0 ? -1 : PictureReorder.IndexOfStartCode(data, 0xB6, first + 4);
        return second > 0 ? 'B' : PictureReorder.PictureType(CodecType.Mpeg4Visual, data);
    }

    private void Add(byte[] data, long dts)
    {
        if (dts == long.MinValue)
            dts = _lastDts == long.MinValue ? 0 : _lastDts + Config.DefaultSampleDuration;
        _lastDts = dts;
        var type = Config.Codec switch
        {
            CodecType.Mpeg4Visual => PictureReorder.PictureType(CodecType.Mpeg4Visual, data),
            CodecType.Vc1 => Vc1.PictureType(data, _interlaced),
            _ => 'P',
        };
        var sync = Config.Codec switch
        {
            CodecType.H263 => data.Length > 4 && (data[4] & 0x02) == 0, // picture_coding_type of PTYPE: 0 = intra
            CodecType.Vc1 => type == 'I',
            _ => type == 'I',
        };
        _reorder ??= new PictureReorder(_delay, Config.DefaultSampleDuration);
        _reorder.Add(new MediaSample { Dts = dts, IsSync = sync, Data = data, Duration = Config.DefaultSampleDuration }, type, long.MinValue, _queue);
    }

    public void Reset()
    {
        _inner.Reset();
        _queue.Clear();
        _reorder = null;
        _storedBFrame = null;
        _lastDts = long.MinValue;
        _ended = false;
    }
}
