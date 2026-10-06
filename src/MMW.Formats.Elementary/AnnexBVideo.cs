using MMW.Core.Diagnostics;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Formats.Elementary;

/// <summary>Reads NAL units from an Annex B byte stream (start-code delimited), buffering only one NAL at a time.</summary>
internal sealed class AnnexBReader : IDisposable
{
    private static readonly byte[] s_startCode = [0, 0, 1];
    private readonly Stream _stream;
    private byte[] _buffer = new byte[1 << 20];
    private int _start;
    private int _end;
    private bool _eof;
    private bool _synced;

    public AnnexBReader(Stream stream) => _stream = stream;

    /// <summary>Total bytes consumed so far (approximate progress).</summary>
    public long Position => _stream.Position - (_end - _start);

    private bool Fill()
    {
        if (_eof)
            return false;
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
            Array.Resize(ref _buffer, _buffer.Length * 2);
        var n = _stream.Read(_buffer, _end, _buffer.Length - _end);
        if (n == 0)
        {
            _eof = true;
            return false;
        }

        _end += n;
        return true;
    }

    /// <summary>The next NAL unit (header included, start code and trailing zero bytes removed), or null.</summary>
    public byte[]? Next()
    {
        while (!_synced)
        {
            var idx = _buffer.AsSpan(_start, _end - _start).IndexOf(s_startCode);
            if (idx >= 0)
            {
                _start += idx + 3;
                _synced = true;
                break;
            }

            _start = Math.Max(_start, _end - 2);
            if (!Fill())
                return null;
        }

        while (true)
        {
            var relative = 0;
            while (true)
            {
                var from = _start + relative;
                var idx = _buffer.AsSpan(from, _end - from).IndexOf(s_startCode);
                if (idx >= 0)
                {
                    var nalEnd = from + idx;
                    var nal = Trim(_start, nalEnd);
                    _start = nalEnd + 3;
                    if (nal.Length == 0)
                        break;
                    return nal;
                }

                relative = Math.Max(0, _end - _start - 2);
                if (!Fill())
                {
                    if (_start >= _end)
                        return null;
                    var last = Trim(_start, _end);
                    _start = _end;
                    return last.Length == 0 ? null : last;
                }
            }
        }
    }

    private byte[] Trim(int start, int end)
    {
        while (end > start && _buffer[end - 1] == 0)
            end--;
        return _buffer.AsSpan(start, end - start).ToArray();
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>What the start of a raw H.264/HEVC stream reveals.</summary>
internal sealed record AnnexBProbe(CodecConfig Config, bool HasTiming, List<byte[]> ParameterSets);

/// <summary>
/// Parses a raw H.264 or HEVC Annex B stream into access units with length-prefixed NAL units, deriving
/// presentation times from the picture order count.
/// </summary>
/// <remarks>
/// Access units are delimited per H.264 7.4.1.2.3 / H.265 7.4.2.4.4 (AUD, parameter sets or SEI after a picture, or
/// the first slice of a new picture; the second field of a field pair stays with the first). Presentation order is
/// the picture order count order within each "POC period" (from an IDR / IRAP with NoRaslOutputFlag to the next),
/// sorted with a sliding window of <see cref="ReorderWindow"/> pictures; decoding times step by one frame duration.
/// Access unit delimiters and filler data are dropped, and so are in-band parameter sets identical to the ones in
/// the decoder configuration record (so the track can be stored as avc1/hvc1). RASL pictures of a leading CRA are
/// dropped as they cannot be decoded.
/// </remarks>
internal sealed class AnnexBVideoParser : IElementaryParser
{
    public const int ReorderWindow = 16;

    private sealed class Frame
    {
        public required byte[] Data { get; init; }

        public long DecodeIndex { get; init; }

        public long Poc { get; init; }

        public bool Sync { get; init; }

        public long Slot { get; set; } = -1;
    }

    private readonly AnnexBReader _reader;
    private readonly bool _hevc;
    private readonly long _frameTicks;
    private readonly List<byte[]> _configNals;
    private readonly Dictionary<int, H264Sps> _spsH = [];
    private readonly Dictionary<int, H264Pps> _ppsH = [];
    private readonly Dictionary<int, HevcSps> _spsV = [];
    private readonly Dictionary<int, HevcPps> _ppsV = [];
    private readonly Queue<Frame> _decode = new();
    private readonly PriorityQueue<Frame, long> _heap = new();
    private readonly Queue<MediaSample> _out = new();
    private byte[]? _pending;
    private long _decodeIndex;
    private long _nextSlot;
    private bool _ended;

    // POC state.
    private long _prevPocMsb;
    private long _prevPocLsb;
    private bool _firstPicture = true;
    private bool _skipRasl;

    public AnnexBVideoParser(Stream stream, CodecType codec, long frameTicks, IEnumerable<byte[]> configNals)
    {
        _reader = new AnnexBReader(stream);
        _hevc = codec == CodecType.Hevc;
        _frameTicks = frameTicks;
        _configNals = configNals.ToList();
    }

    /// <summary>Reads the start of a stream to build its codec configuration.</summary>
    public static AnnexBProbe Probe(Stream stream, CodecType codec, double? frameRate)
    {
        using var reader = new AnnexBReader(stream);
        var hevc = codec == CodecType.Hevc;
        List<byte[]> vps = [], sps = [], pps = [];
        var limit = 64L * 1024 * 1024;
        while (reader.Next() is { } nal && reader.Position < limit)
        {
            var type = hevc ? NalUnits.HevcType(nal) : NalUnits.H264Type(nal);
            List<byte[]>? list = hevc
                ? type switch
                {
                    Hevc.NalVps => vps,
                    Hevc.NalSps => sps,
                    Hevc.NalPps => pps,
                    _ => null,
                }
                : type switch
                {
                    H264.NalSps => sps,
                    H264.NalPps => pps,
                    _ => null,
                };
            if (list is not null)
            {
                if (!list.Any(x => x.AsSpan().SequenceEqual(nal)))
                    list.Add(nal);
                continue;
            }

            var vcl = hevc ? Hevc.IsVcl(type) : type is 1 or 5;
            if (vcl && sps.Count > 0 && pps.Count > 0)
                break;
        }

        if (sps.Count == 0 || pps.Count == 0 || (hevc && vps.Count == 0))
            throw new InvalidDataException($"No {(hevc ? "VPS/SPS/PPS" : "SPS/PPS")} found at the start of the {(hevc ? "HEVC" : "H.264")} stream.");

        int width, height, sarW, sarH;
        double vuiRate;
        ColorInfo color;
        byte[] extradata;
        if (hevc)
        {
            var info = Hevc.ParseSps(sps[0]);
            (width, height, sarW, sarH, vuiRate, color) = (info.Width, info.Height, info.SarWidth, info.SarHeight, info.FrameRate, info.Color);
            extradata = Hevc.BuildHvcC(vps, sps, pps);
        }
        else
        {
            var info = H264.ParseSps(sps[0]);
            (width, height, sarW, sarH, vuiRate, color) = (info.Width, info.Height, info.SarWidth, info.SarHeight, info.FrameRate, info.Color);
            extradata = H264.BuildAvcC(sps, pps);
        }

        var hasTiming = vuiRate is > 1 and < 1000;
        var fps = frameRate is > 0 ? frameRate.Value : hasTiming ? vuiRate : 25.0;
        if (frameRate is null && !hasTiming)
            AppLog.Warn($"The {(hevc ? "HEVC" : "H.264")} stream has no timing information; assuming 25 fps.");
        var (timescale, ticks) = FrameTiming(fps);
        var config = new CodecConfig
        {
            Codec = codec,
            Kind = TrackKind.Video,
            SourceCodecId = hevc ? "hevc" : "h264",
            Extradata = extradata,
            Timescale = timescale,
            DefaultSampleDuration = ticks,
            Width = width,
            Height = height,
            ParNumerator = sarW,
            ParDenominator = sarH,
            FrameRate = (double)timescale / ticks,
            Color = color,
        };
        return new AnnexBProbe(config, hasTiming, [.. vps, .. sps, .. pps]);
    }

    /// <summary>A timescale and frame duration representing <paramref name="fps"/> exactly (e.g. 24000/1001).</summary>
    public static (uint Timescale, long FrameTicks) FrameTiming(double fps)
    {
        foreach (var num in new[] { 24000u, 30000u, 48000u, 60000u, 120000u })
        {
            if (Math.Abs(fps - num / 1001.0) < 0.002)
                return (num, 1001);
        }

        return ((uint)Math.Round(fps * 1000), 1000);
    }

    public MediaSample? Next()
    {
        while (_out.Count == 0)
        {
            if (_ended)
                return null;
            var au = ReadAccessUnit();
            if (au is null)
            {
                _ended = true;
                FlushAll();
                continue;
            }

            Process(au);
        }

        return _out.Dequeue();
    }

    // ------------------------------------------------------------------ access units

    private List<byte[]>? ReadAccessUnit()
    {
        var nals = new List<byte[]>();
        var hasVcl = false;
        H264SliceHeader? first = null;
        var firstIsField = false;
        var fields = 0;
        while (true)
        {
            var nal = _pending ?? _reader.Next();
            _pending = null;
            if (nal is null)
                break;
            var type = _hevc ? NalUnits.HevcType(nal) : NalUnits.H264Type(nal);
            TrackParameterSet(nal, type);

            if (_hevc)
            {
                if (hasVcl && (type is >= 32 and <= 35 or 39 or >= 41 and <= 44 or >= 48 and <= 55 ||
                               (Hevc.IsVcl(type) && Hevc.IsFirstSliceSegment(nal))))
                {
                    _pending = nal;
                    break;
                }

                if (Hevc.IsVcl(type))
                    hasVcl = true;
            }
            else
            {
                if (hasVcl && type is 6 or 7 or 8 or 9 or >= 14 and <= 18)
                {
                    _pending = nal;
                    break;
                }

                if (type is 1 or 5)
                {
                    H264SliceHeader? slice = null;
                    try
                    {
                        slice = H264.ParseSliceHeader(nal, _spsH, _ppsH);
                    }
                    catch (InvalidDataException)
                    {
                        // Unparseable slice: treat it as part of the current picture.
                    }

                    if (slice is { } s && hasVcl && s.FirstMbInSlice == 0)
                    {
                        // The second field of a field pair belongs to the same access unit.
                        var secondField = firstIsField && fields == 1 && s.FieldPic && first is { } f && f.FrameNum == s.FrameNum && f.BottomField != s.BottomField;
                        if (!secondField)
                        {
                            _pending = nal;
                            break;
                        }

                        fields++;
                    }

                    if (!hasVcl && slice is { } firstSlice)
                    {
                        first = firstSlice;
                        firstIsField = firstSlice.FieldPic;
                        fields = 1;
                    }

                    hasVcl = true;
                }
            }

            nals.Add(nal);
        }

        return nals.Count == 0 ? null : nals;
    }

    private void TrackParameterSet(byte[] nal, int type)
    {
        try
        {
            if (_hevc)
            {
                if (type == Hevc.NalSps)
                {
                    var s = Hevc.ParseSps(nal);
                    _spsV[s.Id] = s;
                }
                else if (type == Hevc.NalPps)
                {
                    var p = Hevc.ParsePps(nal);
                    _ppsV[p.Id] = p;
                }
            }
            else if (type == H264.NalSps)
            {
                var s = H264.ParseSps(nal);
                _spsH[s.Id] = s;
            }
            else if (type == H264.NalPps)
            {
                var p = H264.ParsePps(nal);
                _ppsH[p.Id] = p;
            }
        }
        catch (InvalidDataException ex)
        {
            AppLog.Warn($"Skipping an invalid parameter set: {ex.Message}");
        }
    }

    private void Process(List<byte[]> nals)
    {
        var vclIndex = nals.FindIndex(n => _hevc ? Hevc.IsVcl(NalUnits.HevcType(n)) : NalUnits.H264Type(n) is 1 or 5);
        if (vclIndex < 0)
            return; // Trailing non-picture NAL units (end of stream).

        var vcl = nals[vclIndex];
        long poc;
        bool sync, reset;
        if (_hevc)
        {
            var type = NalUnits.HevcType(vcl);
            if (Hevc.IsRasl(type) && _skipRasl)
                return;
            sync = Hevc.IsIrap(type);
            var noRaslOutput = Hevc.IsIdr(type) || type is 16 or 17 or 18 || _firstPicture;
            if (sync)
                _skipRasl = noRaslOutput && type is 16 or 21; // RASL pictures of a leading CRA/BLA cannot be decoded
            reset = sync && noRaslOutput;
            poc = HevcPoc(vcl, type, reset);
        }
        else
        {
            H264SliceHeader slice;
            try
            {
                slice = H264.ParseSliceHeader(vcl, _spsH, _ppsH);
            }
            catch (InvalidDataException)
            {
                slice = default;
            }

            sync = slice.NalType == H264.NalIdr;
            reset = sync;
            poc = H264Poc(slice);
        }

        _firstPicture = false;
        if (reset)
            FlushAll();

        var buffer = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var nal in nals)
        {
            var type = _hevc ? NalUnits.HevcType(nal) : NalUnits.H264Type(nal);
            var drop = _hevc
                ? type is Hevc.NalAud or Hevc.NalFiller || (type is Hevc.NalVps or Hevc.NalSps or Hevc.NalPps && IsConfigNal(nal))
                : type is H264.NalAud or H264.NalFiller || (type is H264.NalSps or H264.NalPps && IsConfigNal(nal));
            if (drop)
                continue;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)nal.Length);
            buffer.Write(length);
            buffer.Write(nal);
        }

        var frame = new Frame { Data = buffer.ToArray(), DecodeIndex = _decodeIndex++, Poc = poc, Sync = sync };
        _decode.Enqueue(frame);
        _heap.Enqueue(frame, poc);
        while (_heap.Count > ReorderWindow)
            _heap.Dequeue().Slot = _nextSlot++;
        EmitReady();
    }

    private bool IsConfigNal(byte[] nal) => _configNals.Any(c => c.AsSpan().SequenceEqual(nal));

    private long H264Poc(H264SliceHeader slice)
    {
        if (!_ppsH.TryGetValue(slice.PpsId, out var pps) || !_spsH.TryGetValue(pps.SpsId, out var sps) || sps.PocType != 0)
            return _decodeIndex; // POC types 1/2: decoding order is presentation order (types with reordering are rare).
        if (slice.NalType == H264.NalIdr)
        {
            _prevPocMsb = 0;
            _prevPocLsb = 0;
        }

        var max = 1L << sps.Log2MaxPocLsb;
        long msb;
        if (slice.PocLsb < _prevPocLsb && _prevPocLsb - slice.PocLsb >= max / 2)
            msb = _prevPocMsb + max;
        else if (slice.PocLsb > _prevPocLsb && slice.PocLsb - _prevPocLsb > max / 2)
            msb = _prevPocMsb - max;
        else
            msb = _prevPocMsb;
        var poc = msb + slice.PocLsb;
        if (slice.NalRefIdc != 0)
        {
            _prevPocMsb = msb;
            _prevPocLsb = slice.PocLsb;
        }

        return poc;
    }

    private long HevcPoc(byte[] vcl, int type, bool noRaslOutput)
    {
        int lsb, log2;
        try
        {
            (lsb, log2) = Hevc.ParsePocLsb(vcl, _spsV, _ppsV);
        }
        catch (InvalidDataException)
        {
            return _decodeIndex;
        }

        var max = 1L << log2;
        long msb;
        if (Hevc.IsIrap(type) && noRaslOutput)
            msb = 0;
        else if (lsb < _prevPocLsb && _prevPocLsb - lsb >= max / 2)
            msb = _prevPocMsb + max;
        else if (lsb > _prevPocLsb && lsb - _prevPocLsb > max / 2)
            msb = _prevPocMsb - max;
        else
            msb = _prevPocMsb;

        // prevTid0Pic: TemporalId 0 and not RASL, RADL or a sub-layer non-reference picture.
        var subLayerNonRef = type <= 14 && type % 2 == 0;
        if (Hevc.TemporalId(vcl) == 0 && !Hevc.IsRasl(type) && type is not (6 or 7) && !subLayerNonRef)
        {
            _prevPocMsb = msb;
            _prevPocLsb = lsb;
        }

        return msb + lsb;
    }

    private void FlushAll()
    {
        while (_heap.Count > 0)
            _heap.Dequeue().Slot = _nextSlot++;
        EmitReady();
    }

    private void EmitReady()
    {
        while (_decode.Count > 0 && _decode.Peek().Slot >= 0)
        {
            var f = _decode.Dequeue();
            _out.Enqueue(new MediaSample
            {
                Dts = f.DecodeIndex * _frameTicks,
                CtsOffset = (f.Slot - f.DecodeIndex) * _frameTicks,
                Duration = _frameTicks,
                IsSync = f.Sync,
                Data = f.Data,
            });
        }
    }

    public void Dispose() => _reader.Dispose();
}
