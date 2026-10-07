using System.Buffers.Binary;
using MMW.Core.Media.Codecs;

namespace MMW.Core.Media.Export;

/// <summary>
/// The NAL unit codecs (H.264, HEVC, VVC, EVC): length-prefixed samples are rewritten with the raw format's framing, and
/// the configuration record's NAL units (parameter sets and the SEI arrays of an hvcC/vvcC) are inserted before the
/// first frame and before every key frame that does not carry its own SPS, as mkvextract does.
/// </summary>
internal abstract class NalWriter : TrackWriter
{
    private readonly int _lengthSize;
    private readonly IReadOnlyList<byte[]> _configNals;
    private bool _first = true;

    protected NalWriter(ExportContext context)
        : base(context)
    {
        (_lengthSize, _configNals) = ReadConfiguration(Config);
    }

    /// <summary>The NAL unit type of an SPS of this codec.</summary>
    protected abstract bool IsSps(ReadOnlySpan<byte> nal);

    /// <summary>True for an access unit delimiter (kept first, before inserted parameter sets).</summary>
    protected abstract bool IsDelimiter(ReadOnlySpan<byte> nal);

    /// <summary>Writes one NAL unit with the raw format's framing.</summary>
    protected abstract void WriteNal(ReadOnlySpan<byte> nal);

    public override void Write(MediaSample sample)
    {
        var data = sample.GetData();
        List<Range> nals;
        try
        {
            nals = NalUnits.SplitLengthPrefixed(data.Span, _lengthSize);
        }
        catch (InvalidDataException)
        {
            nals = [];
        }

        if (nals.Count == 0)
        {
            _first = false;
            return;
        }

        var span = data.Span;
        var insert = (_first || sample.IsSync) && _configNals.Count > 0;
        if (insert)
        {
            foreach (var r in nals)
            {
                if (IsSps(span[r]))
                {
                    insert = false;
                    break;
                }
            }
        }

        _first = false;
        var index = 0;
        if (insert && IsDelimiter(span[nals[0]]))
            WriteNal(span[nals[index++]]);
        if (insert)
        {
            foreach (var nal in _configNals)
                WriteNal(nal);
        }

        for (; index < nals.Count; index++)
            WriteNal(span[nals[index]]);
    }

    private static (int LengthSize, IReadOnlyList<byte[]> Nals) ReadConfiguration(CodecConfig config)
    {
        if (config.Extradata is not { Length: > 0 } extradata)
            return (4, []);
        try
        {
            switch (config.Codec)
            {
                case CodecType.H264:
                {
                    var avc = H264.ParseAvcC(extradata);
                    return (avc.LengthSize, [.. avc.Sps, .. avc.Pps]);
                }

                case CodecType.Hevc:
                {
                    var hevc = Hevc.ParseHvcC(extradata);
                    return (hevc.LengthSize, hevc.Nals.Select(n => n.Nal).ToList());
                }

                case CodecType.Vvc:
                {
                    var vvc = Vvc.ParseVvcC(extradata);
                    return (vvc.LengthSize, vvc.Nals.Select(n => n.Nal).ToList());
                }

                case CodecType.Evc:
                {
                    var evc = Evc.ParseEvcC(extradata);
                    return (evc.LengthSize, evc.Nals.Select(n => n.Nal).ToList());
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
        }

        return (4, []);
    }
}

/// <summary>H.264 / HEVC / VVC Annex B byte streams (4-byte start codes).</summary>
internal sealed class AnnexBWriter(ExportContext context) : NalWriter(context)
{
    private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

    protected override bool IsSps(ReadOnlySpan<byte> nal) => Config.Codec switch
    {
        CodecType.H264 => NalUnits.H264Type(nal) == H264.NalSps,
        CodecType.Hevc => NalUnits.HevcType(nal) == 33,
        _ => Vvc.NalType(nal) == Vvc.NalSps,
    };

    protected override bool IsDelimiter(ReadOnlySpan<byte> nal) => Config.Codec switch
    {
        CodecType.H264 => NalUnits.H264Type(nal) == H264.NalAud,
        CodecType.Hevc => NalUnits.HevcType(nal) == 35,
        _ => Vvc.NalType(nal) == Vvc.NalAud,
    };

    protected override void WriteNal(ReadOnlySpan<byte> nal)
    {
        Output.Write(StartCode);
        Output.Write(nal);
    }
}

/// <summary>Raw EVC streams (ISO/IEC 23094-1 Annex B): every NAL unit preceded by its 4-byte length.</summary>
internal sealed class EvcWriter(ExportContext context) : NalWriter(context)
{
    protected override bool IsSps(ReadOnlySpan<byte> nal) => Evc.NalType(nal) == Evc.NalSps;

    protected override bool IsDelimiter(ReadOnlySpan<byte> nal) => false;

    protected override void WriteNal(ReadOnlySpan<byte> nal)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)nal.Length);
        Output.Write(length);
        Output.Write(nal);
    }
}
