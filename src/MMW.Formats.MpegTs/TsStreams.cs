using System.Buffers.Binary;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Formats.MpegTs;

/// <summary>
/// Turns the PES packets of one elementary stream into samples. Timestamps are 90 kHz and already unwrapped; samples
/// are produced in the stream's <see cref="Timescale"/>. During probing it also gathers the codec configuration.
/// </summary>
internal abstract class TsStream
{
    protected TsStream(TsStreamInfo info) => Info = info;

    public TsStreamInfo Info { get; }

    public abstract uint Timescale { get; }

    /// <summary>Enough has been seen to describe the track (<see cref="Describe"/>).</summary>
    public abstract bool Ready { get; }

    public abstract void OnPes(Pes pes, Queue<MediaSample> output);

    /// <summary>End of the file: emits what is still buffered.</summary>
    public virtual void Flush(Queue<MediaSample> output)
    {
    }

    public abstract CodecConfig Describe();

    /// <summary>
    /// The handler for a PMT entry from its stream type, registration and descriptors; null for unsupported streams.
    /// Private data streams without an identifying descriptor are recognised from their payload while probing; reading
    /// instances get the probed <paramref name="known"/> configuration.
    /// </summary>
    public static TsStream? Create(TsStreamInfo info, CodecConfig? known = null)
    {
        if (known is not null && info.StreamType == 0x06 && FromKnown(info, known) is { } identified)
            return identified;
        if (known is { Codec: CodecType.Avs2 })
            return new MpegVideoStream(info, CodecType.Avs2);
        var registration = info.Registration;
        switch (info.StreamType)
        {
            case 0x01:
                return new MpegVideoStream(info, CodecType.Mpeg1Video);
            case 0x02:
                return new MpegVideoStream(info, CodecType.Mpeg2Video);
            case 0x03 or 0x04:
                return new AudioStream(info, AudioKind.MpegAudio);
            case 0x0F:
                return new AudioStream(info, AudioKind.Adts);
            case 0x1B:
                return new NalVideoStream(info, CodecType.H264);
            case 0x24:
                return new NalVideoStream(info, CodecType.Hevc);
            case 0x33:
                return new NalVideoStream(info, CodecType.Vvc);
            case 0x42:
                return new MpegVideoStream(info, CodecType.Avs1);
            case 0xD2:
                return new MpegVideoStream(info, CodecType.Avs2);
            case 0xD4:
                return new MpegVideoStream(info, CodecType.Avs3);
            case 0x81 or 0x84 or 0x87 or 0xA1:
                return new AudioStream(info, AudioKind.Ac3);
            case 0x82 or 0x85 or 0x86:
                return new AudioStream(info, AudioKind.Dts);
            case 0x83:
                return new AudioStream(info, AudioKind.TrueHd);
            case 0x80 when registration == "HDMV" || info.Pid >= 0x1100:
                return new LpcmStream(info, known);
            case 0x90:
                return new PgsStream(info);
            case 0x06:
                if (info.Descriptor(0x6A) is not null || info.Descriptor(0x7A) is not null || registration is "AC-3" or "EAC3")
                    return new AudioStream(info, AudioKind.Ac3);
                if (info.Descriptor(0x7B) is not null || registration is "DTS1" or "DTS2" or "DTS3")
                    return new AudioStream(info, AudioKind.Dts);
                if (registration == "HEVC")
                    return new NalVideoStream(info, CodecType.Hevc);
                if (info.Descriptor(0x59) is { Length: >= 8 })
                    return new DvbSubtitleStream(info);
                return info.Descriptor(0x59) is null && info.Descriptor(0x56) is null ? new SniffedStream(info) : null; // not DVB subtitles/teletext
            default:
                return null;
        }
    }

    private static TsStream? FromKnown(TsStreamInfo info, CodecConfig known) => known.Codec switch
    {
        CodecType.Ac3 or CodecType.Eac3 => new AudioStream(info, AudioKind.Ac3),
        CodecType.Dts => new AudioStream(info, AudioKind.Dts),
        CodecType.Aac => new AudioStream(info, AudioKind.Adts),
        CodecType.Mp1 or CodecType.Mp2 or CodecType.Mp3 => new AudioStream(info, AudioKind.MpegAudio),
        _ => null,
    };

    protected CodecConfig Base(TrackKind kind) => new()
    {
        Kind = kind,
        SourceCodecId = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"0x{Info.StreamType:X2}"),
        Timescale = Timescale,
        Language = Info.Language is { Length: 3 } language ? MMW.Core.Languages.LanguageTable.ToBcp47(language) : "und",
    };
}

/// <summary>
/// H.264 / HEVC / VVC video as a continuous Annex B byte stream: NAL units are found across PES packets and access units
/// are delimited by their content (H.264 §7.4.1.2.3, H.265 §7.4.2.4.4, H.266 §7.4.2.4.4: delimiter, parameter sets,
/// picture header or SEI after a picture, the first slice of a new picture), since PES packets need not align with access units. An access unit takes the
/// timestamps of the PES it starts in; the second field of a field pair joins the first.
/// </summary>
internal sealed class NalVideoStream(TsStreamInfo info, CodecType codec) : TsStream(info)
{
    private readonly bool _hevc = codec == CodecType.Hevc;
    private readonly bool _vvc = codec == CodecType.Vvc;
    private readonly List<byte[]> _vps = [];
    private readonly List<byte[]> _sps = [];
    private readonly List<byte[]> _pps = [];
    private readonly Dictionary<int, H264Sps> _h264Sps = [];
    private readonly Dictionary<int, H264Pps> _h264Pps = [];
    private readonly List<long> _dts = [];
    private readonly Queue<(long Offset, long Pts, long Dts)> _markers = new();
    private byte[] _buffer = new byte[1 << 20];
    private int _end;
    private int _scan;
    private int _nalStart = -1;
    private long _base; // stream offset of _buffer[0]
    private AccessUnit? _current;
    private AccessUnit? _held;
    private long _lastAuOffset = -1;
    private (long Pts, long Dts)? _previous;
    private bool _seenSync;
    private bool _dropRasl;

    private sealed class AccessUnit
    {
        public long Pts { get; set; }

        public long Dts { get; set; }

        public bool Timed { get; set; }

        public List<byte[]> Nals { get; } = [];

        public bool HasVcl { get; set; }

        public bool Sync { get; set; }

        public int FirstVclType { get; set; } = -1;

        public H264SliceHeader? Field { get; set; }
    }

    public override uint Timescale => 90000;

    public override bool Ready => HasConfig && _dts.Count >= 24;

    /// <summary>Parameter sets and a random access point were seen (frame rate estimation uses what was seen).</summary>
    public bool HasConfig => _sps.Count > 0 && _pps.Count > 0 && (!_hevc || _vps.Count > 0) && _seenSync;

    public override void OnPes(Pes pes, Queue<MediaSample> output)
    {
        if (pes.Pts is { } pts)
            _markers.Enqueue((_base + _end, pts, pes.Dts ?? pts));
        Append(pes.Data);
        ScanNals(output);
    }

    public override void Flush(Queue<MediaSample> output)
    {
        if (_nalStart >= 0)
            OnNal(_buffer.AsSpan(_nalStart, _end - _nalStart), _base + _nalStart, output);
        _nalStart = -1;
        _end = 0;
        Finish(output);
        Emit(_held, output);
        _held = null;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_end + data.Length > _buffer.Length)
        {
            // Keep only the NAL unit in progress.
            var keep = _nalStart >= 0 ? _nalStart : Math.Max(0, _end - 3);
            var used = _end - keep;
            var buffer = used + data.Length > _buffer.Length ? new byte[Math.Max(_buffer.Length * 2, used + data.Length)] : _buffer;
            Buffer.BlockCopy(_buffer, keep, buffer, 0, used);
            _buffer = buffer;
            _base += keep;
            _end = used;
            _scan = Math.Max(0, _scan - keep);
            if (_nalStart >= 0)
                _nalStart -= keep;
        }

        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    /// <summary>Finds start codes; each NAL unit is processed once the next start code ends it.</summary>
    private void ScanNals(Queue<MediaSample> output)
    {
        var i = Math.Max(_scan, 0);
        while (i + 3 <= _end)
        {
            if (_buffer[i] == 0 && _buffer[i + 1] == 0 && _buffer[i + 2] == 1)
            {
                if (_nalStart >= 0)
                {
                    var nalEnd = i;
                    while (nalEnd > _nalStart && _buffer[nalEnd - 1] == 0)
                        nalEnd--; // the zero of a 4-byte start code, trailing_zero_8bits
                    OnNal(_buffer.AsSpan(_nalStart, nalEnd - _nalStart), _base + _nalStart, output);
                }

                _nalStart = i + 3;
                i += 3;
                continue;
            }

            i++;
        }

        _scan = Math.Max(_nalStart, _end - 2);
    }

    private void OnNal(ReadOnlySpan<byte> nal, long offset, Queue<MediaSample> output)
    {
        if (nal.Length == 0)
            return;
        var type = _vvc ? Vvc.NalType(nal) : _hevc ? NalUnits.HevcType(nal) : NalUnits.H264Type(nal);
        var vcl = _vvc ? Vvc.IsVcl(type) : _hevc ? Hevc.IsVcl(type) : type is 1 or 5;
        bool startsAccessUnit;
        if (_vvc)
        {
            startsAccessUnit = type == Vvc.NalAud ||
                               _current is { HasVcl: true } && (type is (>= Vvc.NalOpi and <= Vvc.NalPrefixAps) or Vvc.NalPictureHeader or Vvc.NalSeiPrefix or (>= 26 and <= 29) ||
                                                                vcl && Vvc.HasPictureHeaderInSlice(nal));
        }
        else if (_hevc)
        {
            startsAccessUnit = type == 35 ||
                               _current is { HasVcl: true } && (type is 32 or 33 or 34 or 39 or (>= 41 and <= 44) or (>= 48 and <= 55) ||
                                                                vcl && Hevc.IsFirstSliceSegment(nal));
        }
        else
        {
            startsAccessUnit = type == 9 ||
                               _current is { HasVcl: true } && (type is 6 or 7 or 8 or (>= 14 and <= 18) ||
                                                                vcl && nal.Length > 1 && (nal[1] & 0x80) != 0); // first_mb_in_slice == 0
        }

        // Nothing starts a new access unit before the current one has a picture: repeated delimiters (a muxer adding its
        // own before the stream's) or delimiters between repeated parameter sets (broadcast encoders) must not make a
        // picture-less access unit, which would take the PES timestamps.
        if (startsAccessUnit && _current is { HasVcl: false })
            startsAccessUnit = false;
        if (startsAccessUnit || _current is null)
        {
            Finish(output);
            _current = new AccessUnit();
            Time(_current, offset);
        }

        if (_vvc ? type is Vvc.NalAud or Vvc.NalFiller : _hevc ? type is 35 or 38 : type is 9 or 12) // access unit delimiter, filler: not stored
            return;
        var bytes = nal.ToArray();
        TrackParameterSet(bytes, type);
        var au = _current;
        if (vcl)
        {
            if (au.FirstVclType < 0)
            {
                au.FirstVclType = type;
                if (_vvc)
                {
                    au.Sync = Vvc.IsIrap(type);
                }
                else if (_hevc)
                {
                    au.Sync = Hevc.IsIrap(type);
                }
                else
                {
                    au.Sync |= type == 5;
                    try
                    {
                        var slice = H264.ParseSliceHeader(bytes, _h264Sps, _h264Pps);
                        if (slice.FieldPic)
                            au.Field = slice;
                    }
                    catch (Exception ex) when (ex is KeyNotFoundException or IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
                    {
                    }
                }
            }

            au.HasVcl = true;
        }
        else if (codec == CodecType.H264 && type == 6 && !au.HasVcl)
        {
            var recovery = false;
            Sei.ForEachMessageInNal(bytes, CodecType.H264, (t, _) => recovery = t == 6); // recovery point: open-GOP random access
            au.Sync |= recovery;
        }

        au.Nals.Add(bytes);
    }

    /// <summary>Timestamps of an access unit starting at <paramref name="offset"/>: those of the PES it starts in.</summary>
    private void Time(AccessUnit au, long offset)
    {
        (long Pts, long Dts)? found = null;
        while (_markers.Count > 0 && _markers.Peek().Offset <= offset)
        {
            var marker = _markers.Dequeue();
            if (marker.Offset > _lastAuOffset)
                found = (marker.Pts, marker.Dts);
        }

        _lastAuOffset = offset;
        if (found is { } t)
        {
            (au.Pts, au.Dts, au.Timed) = (t.Pts, t.Dts, true);
        }
        else if (_previous is { } p)
        {
            var step = _dts.Count >= 2 ? Math.Max(1, _dts[^1] - _dts[^2]) : 3003;
            (au.Pts, au.Dts) = (p.Dts + step, p.Dts + step); // untimed access unit: the next frame time
        }
    }

    /// <summary>Completes the access unit being gathered; a held first field is paired with it or emitted.</summary>
    private void Finish(Queue<MediaSample> output)
    {
        if (_current is not { } au)
            return;
        _current = null;
        if (au.Nals.Count == 0 || !au.HasVcl && !au.Timed && _held is null)
            return;
        if (!au.HasVcl && _held is not null)
        {
            _held.Nals.AddRange(au.Nals); // trailing non-picture NAL units (end of sequence…)
            return;
        }

        _previous = (au.Pts, au.Dts);
        if (_held is { Field: { } first } && au.Field is { } second && first.BottomField != second.BottomField && first.FrameNum == second.FrameNum)
        {
            _held.Nals.AddRange(au.Nals); // the second field of the pair: one sample per frame
            Emit(_held, output);
            _held = null;
            return;
        }

        Emit(_held, output);
        _held = au;
    }

    private void TrackParameterSet(byte[] nal, int type)
    {
        List<byte[]>? list = codec switch
        {
            CodecType.Vvc => type switch { Vvc.NalVps => _vps, Vvc.NalSps => _sps, Vvc.NalPps => _pps, _ => null },
            CodecType.Hevc => type switch { 32 => _vps, 33 => _sps, 34 => _pps, _ => null },
            _ => type switch { 7 => _sps, 8 => _pps, _ => null },
        };
        if (list is null)
            return;
        if (!list.Any(n => n.AsSpan().SequenceEqual(nal)))
            list.Add(nal);
        if (codec != CodecType.H264)
            return;
        try
        {
            if (type == 7)
            {
                var sps = H264.ParseSps(nal);
                _h264Sps[sps.Id] = sps;
            }
            else
            {
                var pps = H264.ParsePps(nal);
                _h264Pps[pps.Id] = pps;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
        }
    }

    private void Emit(AccessUnit? au, Queue<MediaSample> output)
    {
        if (au is null || !au.HasVcl)
            return;
        if (!_seenSync && !au.Sync)
            return; // the stream starts mid-GOP: nothing before the first random access point decodes
        if (codec != CodecType.H264 && au.Sync)
            _dropRasl = au.FirstVclType == (_vvc ? Vvc.NalCra : 21) && !_seenSync; // RASL pictures of the leading CRA reference missing pictures
        else if (_dropRasl && !IsRasl(au.FirstVclType) && au.FirstVclType >= 0)
            _dropRasl = false;
        if (_dropRasl && IsRasl(au.FirstVclType))
            return;
        _seenSync = true;
        if (_dts.Count < 64)
            _dts.Add(au.Dts);

        var data = new byte[au.Nals.Sum(n => n.Length + 4)];
        var pos = 0;
        foreach (var nal in au.Nals)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(pos), nal.Length);
            nal.CopyTo(data, pos + 4);
            pos += nal.Length + 4;
        }

        output.Enqueue(new MediaSample { Dts = au.Dts, CtsOffset = au.Pts - au.Dts, IsSync = au.Sync, Data = data });
    }

    private bool IsRasl(int type) => _vvc ? Vvc.IsRasl(type) : _hevc && Hevc.IsRasl(type);

    public override CodecConfig Describe()
    {
        int width = 0, height = 0, sarW = 1, sarH = 1, depth = 8;
        double fps = 0;
        byte[] extradata;
        try
        {
            if (_vvc)
            {
                var sps = Vvc.ParseSps(_sps[0]);
                (width, height, sarW, sarH, depth, fps) = (sps.Width, sps.Height, sps.SarWidth, sps.SarHeight, sps.BitDepth, sps.FrameRate);
                extradata = Vvc.BuildVvcC(_vps, _sps, _pps);
            }
            else if (_hevc)
            {
                var sps = Hevc.ParseSps(_sps[0]);
                (width, height, sarW, sarH, depth, fps) = (sps.Width, sps.Height, sps.SarWidth, sps.SarHeight, sps.BitDepthLuma, sps.FrameRate);
                extradata = Hevc.BuildHvcC(_vps, _sps, _pps);
            }
            else
            {
                var sps = H264.ParseSps(_sps[0]);
                (width, height, sarW, sarH, depth, fps) = (sps.Width, sps.Height, sps.SarWidth, sps.SarHeight, sps.BitDepthLuma, sps.FrameRate);
                extradata = H264.BuildAvcC(_sps, _pps);
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            extradata = _vvc ? Vvc.BuildVvcC(_vps, _sps, _pps) : _hevc ? Hevc.BuildHvcC(_vps, _sps, _pps) : H264.BuildAvcC(_sps, _pps);
        }

        if (fps is <= 0 or > 300)
            fps = FrameRateFromTimestamps();
        return Base(TrackKind.Video) with
        {
            Codec = codec,
            Extradata = extradata,
            Width = width,
            Height = height,
            ParNumerator = sarW > 0 ? sarW : 1,
            ParDenominator = sarH > 0 ? sarH : 1,
            FrameRate = fps,
            DefaultSampleDuration = fps > 0 ? (long)Math.Round(90000 / fps) : 0,
            BitsPerSample = depth,
            DolbyVisionConfig = DolbyVisionDescriptor(),
        };
    }

    private double FrameRateFromTimestamps()
    {
        var deltas = _dts.Zip(_dts.Skip(1), (a, b) => b - a).Where(d => d > 0).Order().ToList();
        return deltas.Count == 0 ? 0 : 90000.0 / deltas[deltas.Count / 2];
    }

    /// <summary>The DOVI video stream descriptor (tag 0xB0) as a DOVIDecoderConfigurationRecord.</summary>
    private byte[]? DolbyVisionDescriptor()
    {
        if (Info.Descriptor(0xB0) is not { Length: >= 4 } d)
            return null;
        var profile = d[2] >> 1;
        var level = ((d[2] & 1) << 5) | (d[3] >> 3);
        bool rpu = (d[3] & 4) != 0, el = (d[3] & 2) != 0, bl = (d[3] & 1) != 0;
        var compatibilityIndex = bl ? 4 : 6; // without a base layer, dependency_pid (13 bits) + reserved (3) come first
        var compatibility = d.Length > compatibilityIndex ? d[compatibilityIndex] >> 4 : 0;
        return DolbyVision.BuildConfigurationRecord(profile, level, rpu, el, bl, compatibility);
    }
}

/// <summary>
/// MPEG-1/2 and AVS1/AVS2/AVS3 video: one picture per PES (PES without timestamp continue it). All use 00 00 01 start
/// codes: MPEG sequence header 0xB3 and picture 0x00 (intra when picture_coding_type is 1); AVS sequence header 0xB0
/// and intra picture 0xB3.
/// </summary>
internal sealed class MpegVideoStream(TsStreamInfo info, CodecType codec) : TsStream(info)
{
    private static readonly double[] s_rates = [0, 24000 / 1001.0, 24, 25, 30000 / 1001.0, 30, 50, 60000 / 1001.0, 60, 100, 120, 200, 240, 300];
    private bool IsAvs => Avs.Generation(codec) is not null;
    private byte[]? _sequenceHeader;
    private MediaSample? _pending;
    private bool _seenSync;
    private int _samples;

    public override uint Timescale => 90000;

    public override bool Ready => _sequenceHeader is not null && _samples >= 2;

    public override void OnPes(Pes pes, Queue<MediaSample> output)
    {
        if (pes.Pts is not { } pts)
        {
            if (_pending is not null)
                _pending.Data = (byte[])[.. _pending.Data.Span, .. pes.Data];
            return;
        }

        Emit(output);
        var sequence = IndexOf(pes.Data, IsAvs ? Avs.SequenceHeader : (byte)0xB3);
        if (sequence >= 0 && _sequenceHeader is null)
        {
            var end = pes.Data.Length;
            foreach (var code in IsAvs ? new byte[] { Avs.IntraPicture, Avs.InterPicture } : [0xB8, 0x00])
            {
                var at = IndexOf(pes.Data, code, sequence + 4);
                if (at > sequence)
                    end = Math.Min(end, at);
            }

            _sequenceHeader = pes.Data[sequence..end];
        }

        bool intra;
        if (IsAvs)
        {
            intra = sequence >= 0 && IndexOf(pes.Data, Avs.IntraPicture) >= 0; // intra picture with its sequence header: random access
        }
        else
        {
            var picture = IndexOf(pes.Data, 0x00);
            intra = picture >= 0 && picture + 5 < pes.Data.Length && ((pes.Data[picture + 5] >> 3) & 7) == 1;
        }

        var dts = pes.Dts ?? pts;
        _pending = new MediaSample { Dts = dts, CtsOffset = pts - dts, IsSync = intra, Data = pes.Data };
    }

    public override void Flush(Queue<MediaSample> output) => Emit(output);

    private void Emit(Queue<MediaSample> output)
    {
        if (_pending is not { } sample)
            return;
        _pending = null;
        if (!_seenSync && !sample.IsSync)
            return;
        _seenSync = true;
        _samples++;
        output.Enqueue(sample);
    }

    /// <summary>
    /// True for an AVS2 sequence header (00 00 01 B0, an AVS2 profile, and a level that AVS1/AVS+ does not use).
    /// </summary>
    public static bool IsAvs2SequenceHeader(ReadOnlySpan<byte> d) =>
        d.Length >= 6 && d[0] == 0 && d[1] == 0 && d[2] == 1 && d[3] == 0xB0 && d[4] is 0x12 or 0x20 or 0x22 or 0x30 or 0x32 &&
        d[5] is not (0x10 or 0x11 or 0x12 or 0x20 or 0x21 or 0x22 or 0x40 or 0x41 or 0x42);

    /// <summary>Position of the start code 00 00 01 <paramref name="code"/>, or -1.</summary>
    private static int IndexOf(byte[] data, byte code, int from = 0)
    {
        for (var i = from; i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && data[i + 3] == code)
                return i;
        }

        return -1;
    }

    public override CodecConfig Describe()
    {
        var s = _sequenceHeader!;
        if (Avs.Generation(codec) is { } generation)
        {
            // AVS: the sequence header stays in the samples.
            var avs = Avs.ParseSequenceHeader(generation, s);
            var rate = avs?.FrameRate ?? 0;
            return Base(TrackKind.Video) with
            {
                Codec = codec,
                BitsPerSample = avs?.BitDepth ?? 8,
                Width = avs?.Width ?? 0,
                Height = avs?.Height ?? 0,
                FrameRate = rate,
                DefaultSampleDuration = rate > 0 ? (long)Math.Round(90000 / rate) : 0,
                VideoProfile = avs is null ? string.Empty : Avs.ProfileLevel(generation, avs.ProfileId, avs.LevelId),
            };
        }

        var width = s.Length > 6 ? (s[4] << 4) | (s[5] >> 4) : 0;
        var height = s.Length > 6 ? ((s[5] & 0x0F) << 8) | s[6] : 0;
        var fps = s.Length > 7 && (s[7] & 0x0F) < 9 ? s_rates[s[7] & 0x0F] : 0;
        return Base(TrackKind.Video) with
        {
            Codec = codec,
            Extradata = s,
            BitsPerSample = 8,
            Width = width,
            Height = height,
            FrameRate = fps,
            DefaultSampleDuration = fps > 0 ? (long)Math.Round(90000 / fps) : 0,
        };
    }
}

internal enum AudioKind
{
    Adts,
    Ac3,
    Dts,
    MpegAudio,
    TrueHd,
}

/// <summary>
/// Audio carried as a byte stream across PES packets: split into frames (access units), each timed from the PTS of the
/// PES it starts in, or continuing the previous frame's time when it starts without one (or the PTS only jitters).
/// </summary>
internal sealed class AudioStream(TsStreamInfo info, AudioKind kind) : TsStream(info)
{
    private static readonly int[] s_aacRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
    private byte[] _buffer = new byte[1 << 16];
    private int _start;
    private int _end;
    private long _consumed; // bytes dropped from the buffer's front
    private readonly Queue<(long Offset, long Pts)> _markers = new();
    private long _lastFrameOffset = -1;
    private long? _next;
    private int _substreams;
    private CodecConfig? _config;

    private enum Status
    {
        NeedMore,
        Invalid,
        Discard,
        Frame,
    }

    public override uint Timescale => Math.Max(1u, _config?.Timescale ?? 90000);

    public override bool Ready => _config is not null && (kind != AudioKind.Dts || _dtsFrames >= DtsProbeFrames);

    /// <summary>DTS frames examined while probing: the DTS:X marker and extension details need not be in the first.</summary>
    private const int DtsProbeFrames = 16;

    private readonly List<ReadOnlyMemory<byte>> _dtsProbe = [];
    private int _dtsFrames;

    /// <summary>The reading instance is created with the probed configuration (sample rate for timestamps).</summary>
    public AudioStream WithConfig(CodecConfig config)
    {
        _config = config;
        return this;
    }

    public override void OnPes(Pes pes, Queue<MediaSample> output)
    {
        if (pes.Pts is { } pts)
            _markers.Enqueue((_consumed + _end - _start, pts));
        Append(pes.Data);
        Extract(output, atEnd: false);
    }

    public override void Flush(Queue<MediaSample> output) => Extract(output, atEnd: true);

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_end + data.Length > _buffer.Length)
        {
            var used = _end - _start;
            var buffer = used + data.Length > _buffer.Length ? new byte[Math.Max(_buffer.Length * 2, used + data.Length)] : _buffer;
            Buffer.BlockCopy(_buffer, _start, buffer, 0, used);
            _buffer = buffer;
            _consumed += _start;
            _start = 0;
            _end = used;
        }

        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    private void Extract(Queue<MediaSample> output, bool atEnd)
    {
        while (_start < _end)
        {
            var span = _buffer.AsSpan(_start, _end - _start);
            var (status, length, samples, sync, config) = FrameAt(span, atEnd);
            switch (status)
            {
                case Status.NeedMore:
                    return;
                case Status.Invalid:
                    _start++;
                    continue;
                case Status.Discard:
                    _start += length;
                    continue;
            }

            _config ??= config;
            if (kind == AudioKind.Dts && _dtsFrames < DtsProbeFrames)
            {
                _dtsFrames++;
                _dtsProbe.Add(span[..length].ToArray());
            }

            var offset = _consumed + _start;
            long? pts = null;
            while (_markers.Count > 0 && _markers.Peek().Offset <= offset)
            {
                var marker = _markers.Dequeue();
                if (marker.Offset > _lastFrameOffset)
                    pts = marker.Pts;
            }

            _lastFrameOffset = offset;
            var data = Payload(span[..length]);
            _start += length;
            var rate = Timescale;
            long time;
            if (pts is { } p)
            {
                time = (long)Math.Round(p * (double)rate / 90000);
                if (_next is { } expected && Math.Abs(time - expected) <= Math.Max(1, samples / 2))
                    time = expected; // PTS rounding jitter: keep frames contiguous
            }
            else if (_next is { } expected)
            {
                time = expected;
            }
            else
            {
                continue; // before the first timestamp
            }

            _next = time + samples;
            output.Enqueue(new MediaSample { Dts = time, Duration = samples, IsSync = sync, Data = data });
        }
    }

    /// <summary>The frame bytes stored in the sample (ADTS headers are removed: the configuration describes them).</summary>
    private byte[] Payload(ReadOnlySpan<byte> frame) =>
        kind == AudioKind.Adts && Aac.ParseAdts(frame) is { } h ? frame[h.HeaderLength..].ToArray() : frame.ToArray();

    private (Status, int Length, int Samples, bool Sync, CodecConfig? Config) FrameAt(ReadOnlySpan<byte> s, bool atEnd)
    {
        switch (kind)
        {
            case AudioKind.Adts:
            {
                if (s.Length < 9)
                    return (Status.NeedMore, 0, 0, false, null);
                if (Aac.ParseAdts(s) is not { } h || h.SamplingIndex >= s_aacRates.Length)
                    return (Status.Invalid, 0, 0, false, null);
                if (s.Length < h.FrameLength)
                    return (Status.NeedMore, 0, 0, false, null);
                var rate = s_aacRates[h.SamplingIndex];
                var config = Base(TrackKind.Audio) with
                {
                    Codec = CodecType.Aac,
                    SampleRate = rate,
                    Channels = h.ChannelConfig == 7 ? 8 : h.ChannelConfig,
                    Extradata = Aac.BuildConfig(h.ObjectType, rate, h.ChannelConfig),
                    Timescale = (uint)rate,
                    DefaultSampleDuration = 1024 * h.RawBlocks,
                };
                return (Status.Frame, h.FrameLength, 1024 * h.RawBlocks, true, config);
            }

            case AudioKind.Ac3:
            {
                if (s.Length < 8)
                    return (Status.NeedMore, 0, 0, false, null);
                if (Ac3.Parse(s) is not { FrameSize: > 0 } h)
                    return (Status.Invalid, 0, 0, false, null);
                if (s.Length < h.FrameSize)
                    return (Status.NeedMore, 0, 0, false, null);
                // Dependent E-AC-3 substreams belong to the access unit of the independent frame before them, which may
                // be an AC-3 core (Blu-ray E-AC-3 7.1: 5.1 AC-3 plus a dependent substream with the other channels).
                var length = h.FrameSize;
                var frames = new List<Ac3FrameHeader> { h };
                while (true)
                {
                    if (s.Length < length + 8)
                    {
                        if (atEnd)
                            break;
                        return (Status.NeedMore, 0, 0, false, null);
                    }

                    if (Ac3.Parse(s[length..]) is not { IsEac3: true, StreamType: 1, FrameSize: > 0 } dependent)
                        break;
                    if (s.Length < length + dependent.FrameSize)
                    {
                        if (atEnd)
                            break;
                        return (Status.NeedMore, 0, 0, false, null);
                    }

                    frames.Add(dependent);
                    length += dependent.FrameSize;
                }

                var eac3 = h.IsEac3 || frames.Count > 1;
                var channels = h.Channels;
                if (frames.Count > 1)
                {
                    var (described, _, _) = Ac3.Describe(Ac3.BuildDec3(frames), eac3: true);
                    channels = described > 0 ? described : channels;
                }

                var config = Base(TrackKind.Audio) with
                {
                    Codec = eac3 ? CodecType.Eac3 : CodecType.Ac3,
                    SampleRate = h.SampleRate,
                    Channels = channels,
                    Timescale = (uint)h.SampleRate,
                    DefaultSampleDuration = h.Samples,
                    IsAtmos = frames.Any(f => f.JocExtension),
                };
                return (Status.Frame, length, h.Samples, true, config);
            }

            case AudioKind.Dts:
            {
                var length = Dts.AccessUnitLength(s, atEnd);
                if (length < 0)
                    return (Status.NeedMore, 0, 0, false, null);
                if (length == 0)
                    return (Status.Invalid, 0, 0, false, null);
                if (Dts.Parse(s[..length]) is not { } h || h.SampleRate == 0)
                    return (Status.Invalid, 0, 0, false, null);
                // Timing follows the core (its sample rate and frame size); the description is the whole stream's.
                var config = Base(TrackKind.Audio) with
                {
                    Codec = CodecType.Dts,
                    SampleRate = h.OutputSampleRate,
                    Channels = h.OutputChannels,
                    BitsPerSample = h.OutputBitsPerSample,
                    Timescale = (uint)h.SampleRate,
                    DefaultSampleDuration = h.Samples,
                    AudioProfile = Dts.ProductName(h.Product),
                };
                return (Status.Frame, length, h.Samples, true, config);
            }

            case AudioKind.MpegAudio:
            {
                if (s.Length < 4)
                    return (Status.NeedMore, 0, 0, false, null);
                var length = MpegAudioFrameLength(s);
                if (length <= 0)
                    return (Status.Invalid, 0, 0, false, null);
                if (s.Length < length)
                    return (Status.NeedMore, 0, 0, false, null);
                var (rate, channels) = MpegAudio.Describe(s);
                var samples = MpegAudio.FrameSamples(s);
                var layer = (s[1] >> 1) & 3;
                var config = Base(TrackKind.Audio) with
                {
                    Codec = layer switch { 3 => CodecType.Mp1, 2 => CodecType.Mp2, _ => CodecType.Mp3 },
                    SampleRate = rate,
                    Channels = channels,
                    Timescale = (uint)rate,
                    DefaultSampleDuration = samples,
                };
                return (Status.Frame, length, samples, true, config);
            }

            default:
            {
                // Blu-ray TrueHD streams interleave an AC-3 track (for players without TrueHD); it is not kept.
                if (s.Length < 8)
                    return (Status.NeedMore, 0, 0, false, null);
                if (Ac3.HasSync(s) && Ac3.Parse(s) is { IsEac3: false, FrameSize: > 0 } ac3)
                    return s.Length < ac3.FrameSize ? (Status.NeedMore, 0, 0, false, null) : (Status.Discard, ac3.FrameSize, 0, false, null);
                var length = (BinaryPrimitives.ReadUInt16BigEndian(s) & 0x0FFF) * 2;
                if (length < 4)
                    return (Status.Invalid, 0, 0, false, null);
                if (s.Length < length)
                    return (Status.NeedMore, 0, 0, false, null);
                if (TrueHd.Parse(s[..length], _substreams) is not { } au)
                    return (Status.Invalid, 0, 0, false, null);
                if (au.IsMajorSync)
                    _substreams = au.Substreams;
                else if (_config is null)
                    return (Status.Discard, length, 0, false, null); // configuration comes from the first major sync
                CodecConfig? config = null;
                if (au.IsMajorSync)
                {
                    config = Base(TrackKind.Audio) with
                    {
                        Codec = CodecType.TrueHd,
                        SampleRate = au.SampleRate,
                        Channels = TrueHdChannels(au.FormatInfo),
                        Timescale = (uint)au.SampleRate,
                        DefaultSampleDuration = au.SamplesPerAccessUnit,
                        IsAtmos = au.HasAtmos,
                    };
                }

                var samples = au.SamplesPerAccessUnit > 0 ? au.SamplesPerAccessUnit : TrueHd.SamplesPerAccessUnit(_config?.SampleRate ?? au.SampleRate);
                return (Status.Frame, length, samples, au.IsMajorSync, config);
            }
        }
    }

    private static readonly int[,] s_mpaBitrates =
    {
        { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 }, // MPEG-1 layer I
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 }, // MPEG-1 layer II
        { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }, // MPEG-1 layer III
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 }, // MPEG-2 layer I
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 }, // MPEG-2 layers II and III
    };

    private static int MpegAudioFrameLength(ReadOnlySpan<byte> s)
    {
        if (s[0] != 0xFF || (s[1] & 0xE0) != 0xE0)
            return 0;
        var version = (s[1] >> 3) & 3;
        var layer = (s[1] >> 1) & 3;
        var bitrateIndex = s[2] >> 4;
        var padding = (s[2] >> 1) & 1;
        var (rate, _) = MpegAudio.Describe(s);
        if (version == 1 || layer == 0 || bitrateIndex is 0 or 15 || rate == 0)
            return 0;
        var mpeg1 = version == 3;
        var row = mpeg1 ? 3 - layer : layer == 3 ? 3 : 4;
        var bitrate = s_mpaBitrates[row, bitrateIndex] * 1000;
        return layer == 3
            ? (12 * bitrate / rate + padding) * 4
            : (layer == 1 && !mpeg1 ? 72 : 144) * bitrate / rate + padding;
    }

    /// <summary>Channels of a TrueHD stream from format_info: the 8-channel presentation's assignment, else the 6-channel one.</summary>
    private static int TrueHdChannels(uint formatInfo)
    {
        int[] counts = [2, 1, 1, 2, 2, 2, 2, 1, 1, 2, 2, 1, 1];
        var eight = (int)(formatInfo & 0x1FFF);
        var six = (int)((formatInfo >> 15) & 0x1F);
        var assignment = eight != 0 ? eight : six;
        var channels = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            if ((assignment & (1 << i)) != 0)
                channels += counts[i];
        }

        return channels > 0 ? channels : 2;
    }

    public override CodecConfig Describe() =>
        kind == AudioKind.Dts && Dts.Describe(_dtsProbe) is { } best ? DtsDetector.Apply(_config!, best) : _config!;
}

/// <summary>Presentation graphic stream (Blu-ray subtitles): segments gathered into display sets, ended by an END segment.</summary>
internal sealed class PgsStream(TsStreamInfo info) : TsStream(info)
{
    private readonly List<byte> _set = [];
    private long? _pts;
    private bool _seen;

    public override uint Timescale => 90000;

    public override bool Ready => _seen;

    public override void OnPes(Pes pes, Queue<MediaSample> output)
    {
        var data = pes.Data;
        var pos = 0;
        while (pos + 3 <= data.Length)
        {
            var type = data[pos];
            var size = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 1));
            var end = Math.Min(data.Length, pos + 3 + size);
            if (_set.Count == 0)
                _pts = pes.Pts;
            _set.AddRange(data.AsSpan(pos, end - pos));
            pos = end;
            if (type == 0x80 && _pts is { } pts) // END of display set
            {
                output.Enqueue(new MediaSample { Dts = pts, IsSync = true, Data = _set.ToArray() });
                _set.Clear();
                _seen = true;
            }
        }
    }

    public override CodecConfig Describe() => Base(TrackKind.Subtitle) with { Codec = CodecType.Pgs };
}

/// <summary>A private data stream (type 0x06) without an identifying descriptor: the audio codec is recognised from the payload.</summary>
internal sealed class SniffedStream(TsStreamInfo info) : TsStream(info)
{
    private TsStream? _inner;
    private bool _unknown;

    public override uint Timescale => _inner?.Timescale ?? 90000;

    public override bool Ready => _inner?.Ready ?? false;

    public override void OnPes(Pes pes, Queue<MediaSample> output)
    {
        if (_inner is null && !_unknown && pes.Data.Length >= 4)
        {
            var d = pes.Data;
            AudioKind? kind = Ac3.HasSync(d) ? AudioKind.Ac3
                : Dts.HasCoreSync(d) ? AudioKind.Dts
                : d[0] == 0xFF && (d[1] & 0xF6) == 0xF0 ? AudioKind.Adts
                : d[0] == 0xFF && (d[1] & 0xE0) == 0xE0 && ((d[1] >> 1) & 3) != 0 ? AudioKind.MpegAudio
                : null;
            if (kind is { } k)
                _inner = new AudioStream(Info, k);
            else if (MpegVideoStream.IsAvs2SequenceHeader(d))
                _inner = new MpegVideoStream(Info, CodecType.Avs2);
            else
                _unknown = true;
        }

        _inner?.OnPes(pes, output);
    }

    public override void Flush(Queue<MediaSample> output) => _inner?.Flush(output);

    public override CodecConfig Describe() => _inner!.Describe();
}

/// <summary>
/// Blu-ray LPCM (stream type 0x80): each PES carries a 4-byte header (payload size, channel assignment, sampling
/// frequency, bits per sample) and big-endian samples, odd channel counts padded to an even number. Stored as
/// little-endian PCM (16 or 24 bits; 20-bit samples are left-aligned in 24) without the padding channel.
/// </summary>
internal sealed class LpcmStream(TsStreamInfo info, CodecConfig? known) : TsStream(info)
{
    private static readonly int[] s_channels = [0, 1, 0, 2, 3, 3, 4, 4, 5, 6, 7, 8, 0, 0, 0, 0];
    private CodecConfig? _config = known;
    private long? _next;

    public override uint Timescale => (uint)Math.Max(1, _config?.SampleRate ?? 48000);

    public override bool Ready => _config is not null;

    public override void OnPes(Pes pes, Queue<MediaSample> output)
    {
        var d = pes.Data;
        if (d.Length < 4)
            return;
        var channels = s_channels[d[2] >> 4];
        var rate = (d[2] & 0x0F) switch { 1 => 48000, 4 => 96000, 5 => 192000, _ => 0 };
        var bits = (d[3] >> 6) switch { 1 => 16, 2 => 20, 3 => 24, _ => 0 };
        if (channels == 0 || rate == 0 || bits == 0)
            return;
        var stored = (channels + 1) & ~1;
        var bytes = bits == 16 ? 2 : 3;
        var payload = d.AsSpan(4, Math.Min(BinaryPrimitives.ReadUInt16BigEndian(d), d.Length - 4));
        var frames = payload.Length / (stored * bytes);
        if (frames == 0)
            return;
        _config ??= Base(TrackKind.Audio) with
        {
            Codec = CodecType.Pcm,
            SampleRate = rate,
            Channels = channels,
            BitsPerSample = bytes * 8,
            Timescale = (uint)rate,
            DefaultSampleDuration = frames,
        };

        // Blu-ray orders 5.1 as L R C Ls Rs LFE and 7.x as L R C Ls Lb Rb Rs LFE; WAVE/Matroska order puts LFE fourth
        // and the back channels before the side ones (the mapping FFmpeg's pcm_bluray decoder applies).
        int[] map = channels switch
        {
            6 => [0, 1, 2, 4, 5, 3],
            7 => [0, 1, 2, 5, 3, 4, 6],
            8 => [0, 1, 2, 6, 4, 5, 7, 3],
            _ => [.. Enumerable.Range(0, channels)],
        };
        var data = new byte[frames * channels * bytes];
        for (var f = 0; f < frames; f++)
        {
            var frame = payload.Slice(f * stored * bytes, stored * bytes);
            var outFrame = data.AsSpan(f * channels * bytes, channels * bytes);
            for (var c = 0; c < channels; c++)
            {
                var sample = frame.Slice(c * bytes, bytes);
                var target = outFrame.Slice(map[c] * bytes, bytes);
                for (var b = 0; b < bytes; b++)
                    target[b] = sample[bytes - 1 - b]; // big- to little-endian
            }
        }

        long time;
        if (pes.Pts is { } pts)
        {
            time = (long)Math.Round(pts * (double)rate / 90000);
            if (_next is { } expected && Math.Abs(time - expected) <= frames / 2)
                time = expected;
        }
        else if (_next is { } expected)
        {
            time = expected;
        }
        else
        {
            return;
        }

        _next = time + frames;
        output.Enqueue(new MediaSample { Dts = time, Duration = frames, IsSync = true, Data = data });
    }

    public override CodecConfig Describe() => _config!;
}

/// <summary>
/// DVB subtitles (EN 300 743; stream type 0x06 with a subtitling descriptor): bitmap pages, one per PES. Samples hold
/// the subtitling segments (data_identifier, subtitle_stream_id and the end marker removed), as decoders and Matroska
/// S_DVBSUB expect; the configuration carries composition page, ancillary page and subtitling type.
/// </summary>
internal sealed class DvbSubtitleStream(TsStreamInfo info) : TsStream(info)
{
    private bool _seen;

    public override uint Timescale => 90000;

    public override bool Ready => _seen;

    public override void OnPes(Pes pes, Queue<MediaSample> output)
    {
        var d = pes.Data;
        if (pes.Pts is not { } pts || d.Length < 3 || d[0] != 0x20 || d[1] != 0x00)
            return;
        var end = d.Length;
        while (end > 2 && d[end - 1] == 0xFF)
            end--; // end_of_PES_data_field_marker (and stuffing)
        if (end - 2 < 6 || d[2] != 0x0F)
            return;
        _seen = true;
        output.Enqueue(new MediaSample { Dts = pts, IsSync = true, Data = d[2..end] });
    }

    public override CodecConfig Describe()
    {
        var descriptor = Info.Descriptor(0x59)!; // ISO 639 language (3), subtitling_type, composition_page_id, ancillary_page_id
        var language = System.Text.Encoding.ASCII.GetString(descriptor, 0, 3);
        return Base(TrackKind.Subtitle) with
        {
            Codec = CodecType.DvbSub,
            Extradata = [descriptor[4], descriptor[5], descriptor[6], descriptor[7], descriptor[3]],
            Language = MMW.Core.Languages.LanguageTable.ToBcp47(language),
        };
    }
}
