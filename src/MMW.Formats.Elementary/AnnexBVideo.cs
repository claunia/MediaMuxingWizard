using MMW.Core.Diagnostics;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Formats.Elementary;

/// <summary>A source of NAL units (header included).</summary>
internal interface INalReader : IDisposable
{
    /// <summary>Total bytes consumed so far (approximate progress).</summary>
    long Position { get; }

    /// <summary>The next NAL unit, or null at the end.</summary>
    byte[]? Next();
}

/// <summary>Reads NAL units each preceded by a 4-byte big-endian length (the raw MPEG-5 EVC format).</summary>
internal sealed class LengthPrefixedNalReader(Stream stream) : INalReader
{
    public long Position => stream.Position;

    public byte[]? Next()
    {
        Span<byte> length = stackalloc byte[4];
        if (stream.ReadAtLeast(length, 4, throwOnEndOfStream: false) < 4)
            return null;
        var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(length);
        if (size == 0 || size > int.MaxValue / 2)
            throw new InvalidDataException("Invalid NAL unit length.");
        var nal = new byte[size];
        if (stream.ReadAtLeast(nal, nal.Length, throwOnEndOfStream: false) < nal.Length)
            return null; // truncated last NAL unit
        return nal;
    }

    public void Dispose() => stream.Dispose();
}

/// <summary>Reads NAL units from an Annex B byte stream (start-code delimited), buffering only one NAL at a time.</summary>
internal sealed class AnnexBReader : INalReader
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

/// <summary>What the start of a raw H.264/HEVC/VVC/EVC stream reveals.</summary>
internal sealed record AnnexBProbe(CodecConfig Config, bool HasTiming, List<byte[]> ParameterSets);

/// <summary>
/// Parses a raw H.264, HEVC or VVC Annex B stream, or a raw (length-prefixed) MPEG-5 EVC stream, into access units with
/// length-prefixed NAL units, deriving presentation times from the picture order count.
/// </summary>
/// <remarks>
/// Access units are delimited per H.264 7.4.1.2.3 / H.265 7.4.2.4.4 / H.266 7.4.2.4.4 (AUD, parameter sets, picture
/// header or SEI after a picture, or the first slice of a new picture; the second field of a field pair stays with the
/// first; for EVC, parameter sets or SEI after a picture, or a slice starting at the picture's first tile). Presentation order is
/// the picture order count order within each "POC period" (from an IDR / IRAP with NoRaslOutputFlag to the next),
/// sorted with a sliding window of <see cref="ReorderWindow"/> pictures; decoding times step by one frame duration.
/// Access unit delimiters and filler data are dropped, and so are in-band parameter sets identical to the ones in
/// the decoder configuration record (so the track can be stored as avc1/hvc1/vvc1). RASL pictures of a leading CRA are
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

    private readonly INalReader _reader;
    private readonly bool _hevc;
    private readonly bool _vvc;
    private readonly bool _evc;
    private readonly AvsGeneration? _avs;
    private readonly Avs.OrderCounter _avsOrder = new();
    private AvsSequence? _avsSequence;
    private readonly long _frameTicks;
    private readonly List<byte[]> _configNals;
    private readonly Dictionary<int, H264Sps> _spsH = [];
    private readonly Dictionary<int, H264Pps> _ppsH = [];
    private readonly Dictionary<int, HevcSps> _spsV = [];
    private readonly Dictionary<int, HevcPps> _ppsV = [];
    private readonly Dictionary<int, VvcSps> _spsW = [];
    private readonly Dictionary<int, VvcPps> _ppsW = [];
    private readonly Dictionary<int, EvcSps> _spsE = [];
    private readonly Dictionary<int, EvcPps> _ppsE = [];
    private readonly Evc.PocCounter _evcPoc = new();
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
        _reader = Reader(stream, codec);
        _hevc = codec == CodecType.Hevc;
        _vvc = codec == CodecType.Vvc;
        _evc = codec == CodecType.Evc;
        _avs = Avs.Generation(codec);
        _frameTicks = frameTicks;
        _configNals = configNals.ToList();
    }

    /// <summary>Reads the start of a stream to build its codec configuration.</summary>
    public static AnnexBProbe Probe(Stream stream, CodecType codec, double? frameRate)
    {
        using var reader = Reader(stream, codec);
        if (Avs.Generation(codec) is { } generation)
            return ProbeAvs(reader, codec, generation, frameRate);
        var hevc = codec == CodecType.Hevc;
        var vvc = codec == CodecType.Vvc;
        var name = CodecNames.Display(codec);
        List<byte[]> vps = [], sps = [], pps = [];
        var limit = 64L * 1024 * 1024;
        while (reader.Next() is { } nal && reader.Position < limit)
        {
            var type = NalType(codec, nal);
            List<byte[]>? list = codec switch
            {
                CodecType.Evc => type switch
                {
                    Evc.NalSps => sps,
                    Evc.NalPps => pps,
                    _ => null,
                },
                CodecType.Vvc => type switch
                {
                    Vvc.NalVps => vps,
                    Vvc.NalSps => sps,
                    Vvc.NalPps => pps,
                    _ => null,
                },
                CodecType.Hevc => type switch
                {
                    Hevc.NalVps => vps,
                    Hevc.NalSps => sps,
                    Hevc.NalPps => pps,
                    _ => null,
                },
                _ => type switch
                {
                    H264.NalSps => sps,
                    H264.NalPps => pps,
                    _ => null,
                },
            };
            if (list is not null)
            {
                if (!list.Any(x => x.AsSpan().SequenceEqual(nal)))
                    list.Add(nal);
                continue;
            }

            if (IsVcl(codec, type) && sps.Count > 0 && pps.Count > 0)
                break;
        }

        // A VVC VPS is optional (single-layer streams may refer to VPS 0, "none").
        if (sps.Count == 0 || pps.Count == 0 || (hevc && vps.Count == 0))
            throw new InvalidDataException($"No {(hevc ? "VPS/SPS/PPS" : "SPS/PPS")} found at the start of the {name} stream.");

        int width, height, sarW, sarH;
        double vuiRate;
        ColorInfo color;
        byte[] extradata;
        if (codec == CodecType.Evc)
        {
            var info = Evc.ParseSps(sps[0]);
            (width, height, sarW, sarH, vuiRate, color) = (info.Width, info.Height, info.SarWidth, info.SarHeight, info.FrameRate, info.Color);
            extradata = Evc.BuildEvcC(sps, pps);
        }
        else if (vvc)
        {
            var info = Vvc.ParseSps(sps[0]);
            (width, height, sarW, sarH, vuiRate, color) = (info.Width, info.Height, info.SarWidth, info.SarHeight, info.FrameRate, info.Color);
            extradata = Vvc.BuildVvcC(vps, sps, pps);
        }
        else if (hevc)
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
            AppLog.Warn($"The {name} stream has no timing information; assuming 25 fps.");
        var (timescale, ticks) = FrameTiming(fps);
        var config = new CodecConfig
        {
            Codec = codec,
            Kind = TrackKind.Video,
            SourceCodecId = codec switch
            {
                CodecType.Evc => "evc",
                CodecType.Vvc => "vvc",
                CodecType.Hevc => "hevc",
                _ => "h264",
            },
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

    /// <summary>
    /// AVS: the first sequence header and its extensions give the configuration; the sequence headers stay in the
    /// samples (there is no configuration record, except AVS3's in MP4, built when muxing).
    /// </summary>
    private static AnnexBProbe ProbeAvs(INalReader reader, CodecType codec, AvsGeneration generation, double? frameRate)
    {
        var units = new MemoryStream();
        var limit = 64L * 1024 * 1024;
        var seenSequence = false;
        while (reader.Next() is { } unit && reader.Position < limit)
        {
            if (unit[0] == Avs.SequenceHeader)
                seenSequence = true;
            if (!seenSequence)
                continue;
            if (Avs.IsPicture(unit[0]))
                break;
            units.Write([0, 0, 1]);
            units.Write(unit);
        }

        var name = CodecNames.Display(codec);
        var sequence = Avs.ParseSequence(generation, units.ToArray())
                       ?? throw new InvalidDataException($"No sequence header found at the start of the {name} stream.");
        var hasTiming = sequence.FrameRate > 0;
        var fps = frameRate is > 0 ? frameRate.Value : hasTiming ? sequence.FrameRate : 25.0;
        var (timescale, ticks) = FrameTiming(fps);
        var (parN, parD) = AvsSampleAspect(sequence);
        var config = new CodecConfig
        {
            Codec = codec,
            Kind = TrackKind.Video,
            SourceCodecId = codec switch
            {
                CodecType.Avs1 => "cavs",
                CodecType.Avs2 => "avs2",
                _ => "avs3",
            },
            Timescale = timescale,
            DefaultSampleDuration = ticks,
            Width = sequence.Width,
            Height = sequence.Height,
            ParNumerator = parN,
            ParDenominator = parD,
            FrameRate = (double)timescale / ticks,
            BitsPerSample = sequence.BitDepth,
            Color = sequence.Color,
            Hdr = sequence.Hdr,
            VideoProfile = Avs.ProfileLevel(generation, sequence.ProfileId, sequence.LevelId),
        };
        return new AnnexBProbe(config, hasTiming, []);
    }

    /// <summary>The sample aspect ratio of an AVS aspect_ratio code (2, 3, 4: 4:3, 16:9, 2.21:1 display aspect ratio).</summary>
    private static (int Num, int Den) AvsSampleAspect(AvsSequence sequence)
    {
        (long n, long d) = sequence.AspectRatio switch
        {
            2 => (4L * sequence.Height, 3L * sequence.Width),
            3 => (16L * sequence.Height, 9L * sequence.Width),
            4 => (221L * sequence.Height, 100L * sequence.Width),
            _ => (1L, 1L),
        };
        if (n <= 0 || d <= 0)
            return (1, 1);
        var g = (long)System.Numerics.BigInteger.GreatestCommonDivisor(n, d);
        return ((int)(n / g), (int)(d / g));
    }

    private static INalReader Reader(Stream stream, CodecType codec) =>
        codec == CodecType.Evc ? new LengthPrefixedNalReader(stream) : new AnnexBReader(stream);

    private static int NalType(CodecType codec, ReadOnlySpan<byte> nal) => codec switch
    {
        CodecType.Avs1 or CodecType.Avs2 or CodecType.Avs3 => nal.Length > 0 ? nal[0] : -1, // the start code value
        CodecType.Evc => Evc.NalType(nal),
        CodecType.Vvc => Vvc.NalType(nal),
        CodecType.Hevc => NalUnits.HevcType(nal),
        _ => NalUnits.H264Type(nal),
    };

    private static bool IsVcl(CodecType codec, int type) => codec switch
    {
        CodecType.Avs1 or CodecType.Avs2 or CodecType.Avs3 => Avs.IsPicture(type), // a picture header starts the picture's data
        CodecType.Evc => Evc.IsVcl(type),
        CodecType.Vvc => Vvc.IsVcl(type),
        CodecType.Hevc => Hevc.IsVcl(type),
        _ => type is 1 or 5,
    };

    private CodecType Codec => _avs is { } avs ? Avs.Codec(avs) : _evc ? CodecType.Evc : _vvc ? CodecType.Vvc : _hevc ? CodecType.Hevc : CodecType.H264;

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
            var type = NalType(Codec, nal);
            TrackParameterSet(nal, type);

            if (_avs is not null)
            {
                // A sequence header, picture header or video edit code after a picture starts the next one.
                if (hasVcl && type is Avs.SequenceHeader or Avs.IntraPicture or Avs.InterPicture or Avs.VideoEdit)
                {
                    _pending = nal;
                    break;
                }

                if (Avs.IsPicture(type))
                    hasVcl = true;
            }
            else if (_evc)
            {
                if (hasVcl && (type is Evc.NalSps or Evc.NalPps or Evc.NalAps or Evc.NalSei || (Evc.IsVcl(type) && StartsEvcPicture(nal))))
                {
                    _pending = nal;
                    break;
                }

                if (Evc.IsVcl(type))
                    hasVcl = true;
            }
            else if (_vvc)
            {
                // OPI, DCI, VPS, SPS, PPS, prefix APS, PH, AUD, prefix SEI, reserved/unspecified 26–29, or a slice carrying
                // its picture header start a new picture unit.
                if (hasVcl && (type is >= Vvc.NalOpi and <= Vvc.NalPrefixAps or Vvc.NalPictureHeader or Vvc.NalAud or Vvc.NalSeiPrefix or >= 26 and <= 29 ||
                               (Vvc.IsVcl(type) && Vvc.HasPictureHeaderInSlice(nal))))
                {
                    _pending = nal;
                    break;
                }

                if (Vvc.IsVcl(type))
                    hasVcl = true;
            }
            else if (_hevc)
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
            if (_avs is { } generation)
            {
                if (type == Avs.SequenceHeader && Avs.ParseSequenceHeader(generation, [0, 0, 1, .. nal]) is { } sequence)
                    _avsSequence = sequence;
            }
            else if (_evc)
            {
                if (type == Evc.NalSps)
                {
                    var s = Evc.ParseSps(nal);
                    _spsE[s.Id] = s;
                }
                else if (type == Evc.NalPps)
                {
                    var p = Evc.ParsePps(nal);
                    _ppsE[p.Id] = p;
                }
            }
            else if (_vvc)
            {
                if (type == Vvc.NalSps)
                {
                    var s = Vvc.ParseSps(nal);
                    _spsW[s.Id] = s;
                }
                else if (type == Vvc.NalPps)
                {
                    var p = Vvc.ParsePps(nal);
                    _ppsW[p.Id] = p;
                }
            }
            else if (_hevc)
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
        var vclIndex = nals.FindIndex(n => IsVcl(Codec, NalType(Codec, n)));
        if (vclIndex < 0)
            return; // Trailing non-picture NAL units (end of stream).

        var vcl = nals[vclIndex];
        long poc;
        bool sync, reset;
        if (_avs is not null)
        {
            // Pictures are ordered by their order index; there is no reset (AVS1 picture distances and AVS2/AVS3 decode
            // order indexes run across sequences), and an I picture is a random access point.
            sync = vcl[0] == Avs.IntraPicture;
            reset = false;
            poc = _avsSequence is { } sequence && Avs.ParsePictureHeader(sequence, [0, 0, 1, .. vcl]) is { } picture
                ? _avsOrder.Next(sequence, picture)
                : _decodeIndex;
        }
        else if (_evc)
        {
            var type = Evc.NalType(vcl);
            sync = reset = type == Evc.NalIdr;
            poc = EvcPoc(vcl, type);
        }
        else if (_vvc)
        {
            var type = Vvc.NalType(vcl);
            if (Vvc.IsRasl(type) && _skipRasl)
                return;
            sync = Vvc.IsIrap(type);
            var noRaslOutput = Vvc.IsIdr(type) || _firstPicture;
            if (sync)
                _skipRasl = noRaslOutput && type == Vvc.NalCra; // RASL pictures of a leading CRA cannot be decoded
            reset = (sync || type == Vvc.NalGdr) && noRaslOutput;
            var header = nals.Find(n => Vvc.NalType(n) == Vvc.NalPictureHeader) ?? vcl;
            poc = VvcPoc(header, vcl, type, reset);
        }
        else if (_hevc)
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
            var type = NalType(Codec, nal);
            if (_avs is not null)
            {
                buffer.Write([0, 0, 1]); // AVS samples keep their start codes
                buffer.Write(nal);
                continue;
            }

            var drop = Codec switch
            {
                CodecType.Evc => type == Evc.NalFiller || (type is Evc.NalSps or Evc.NalPps && IsConfigNal(nal)),
                CodecType.Vvc => type is Vvc.NalAud or Vvc.NalFiller || (Vvc.IsParameterSet(type) && IsConfigNal(nal)),
                CodecType.Hevc => type is Hevc.NalAud or Hevc.NalFiller || (type is Hevc.NalVps or Hevc.NalSps or Hevc.NalPps && IsConfigNal(nal)),
                _ => type is H264.NalAud or H264.NalFiller || (type is H264.NalSps or H264.NalPps && IsConfigNal(nal)),
            };
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

    /// <summary>A slice that starts at its picture's first tile starts a new picture (unparseable slices are assumed to).</summary>
    private bool StartsEvcPicture(byte[] nal)
    {
        try
        {
            return Evc.ParseSliceHeader(nal, _spsE, _ppsE).FirstSliceOfPicture;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private long EvcPoc(byte[] vcl, int type)
    {
        try
        {
            return _evcPoc.Next(type, Evc.TemporalId(vcl), Evc.ParseSliceHeader(vcl, _spsE, _ppsE));
        }
        catch (InvalidDataException)
        {
            return _decodeIndex;
        }
    }

    private long VvcPoc(byte[] header, byte[] vcl, int type, bool reset)
    {
        int lsb, log2;
        bool nonRef;
        try
        {
            (lsb, log2, nonRef) = Vvc.ParsePocLsb(header, _spsW, _ppsW);
        }
        catch (InvalidDataException)
        {
            return _decodeIndex;
        }

        var max = 1L << log2;
        long msb;
        if (reset)
            msb = 0;
        else if (lsb < _prevPocLsb && _prevPocLsb - lsb >= max / 2)
            msb = _prevPocMsb + max;
        else if (lsb > _prevPocLsb && lsb - _prevPocLsb > max / 2)
            msb = _prevPocMsb - max;
        else
            msb = _prevPocMsb;

        // prevTid0Pic: TemporalId 0, not RASL or RADL, and not a non-reference picture.
        if (Vvc.TemporalId(vcl) == 0 && type is not (Vvc.NalRasl or Vvc.NalRadl) && !nonRef)
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
