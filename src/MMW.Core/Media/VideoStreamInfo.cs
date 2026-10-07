using System.Buffers.Binary;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Core.Media;

/// <summary>
/// Colour description and static HDR10 metadata carried by the video bitstream itself (H.264/HEVC VUI and SEI, AV1
/// sequence header and metadata OBUs), independently of what the container signals.
/// </summary>
public sealed record VideoStreamInfo(ColorInfo Color, HdrInfo? Hdr)
{
    public static VideoStreamInfo None { get; } = new(ColorInfo.Unspecified, null);

    public bool IsEmpty => !Color.IsSpecified && Hdr is null;
}

/// <summary>Reads <see cref="VideoStreamInfo"/> from a track's codec configuration and first frames.</summary>
public static class VideoStreamInfoScanner
{
    /// <summary>Frames inspected after the configuration; static metadata comes with the first (key) frame.</summary>
    public const int MaxSamples = 4;

    private const long Av1MetadataHdrCll = 1;
    private const long Av1MetadataHdrMdcv = 2;

    public static bool CanScan(CodecConfig config) =>
        config.Kind == TrackKind.Video && config.Codec is CodecType.H264 or CodecType.Hevc or CodecType.Av1;

    /// <summary>Scans an open sample source; the source is rewound afterwards.</summary>
    public static VideoStreamInfo Scan(ISampleSource track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        var config = track.Config;
        if (!CanScan(config))
            return VideoStreamInfo.None;

        var state = new State(config.Codec);
        var lengthSize = 4;
        switch (config.Codec)
        {
            case CodecType.Hevc when config.Extradata is { Length: > 22 } hvcc:
                lengthSize = (hvcc[21] & 3) + 1;
                try
                {
                    foreach (var (_, _, nal) in Hevc.ParseHvcC(hvcc).Nals)
                        state.Nal(nal);
                }
                catch (InvalidDataException)
                {
                }

                break;
            case CodecType.H264 when config.Extradata is { Length: > 6 } avcc:
                lengthSize = (avcc[4] & 3) + 1;
                try
                {
                    foreach (var sps in H264.ParseAvcC(avcc).Sps)
                        state.Nal(sps);
                }
                catch (InvalidDataException)
                {
                }

                break;
            case CodecType.Av1 when config.Extradata is { Length: > 4 } av1c:
                state.Obus(av1c.AsSpan(4)); // configOBUs
                break;
        }

        track.Reset();
        try
        {
            for (var i = 0; i < MaxSamples && !state.Complete && track.ReadNext() is { } sample; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = sample.GetData().Span;
                if (config.Codec == CodecType.Av1)
                {
                    state.Obus(data);
                }
                else
                {
                    foreach (var range in NalUnits.SplitLengthPrefixed(data, lengthSize))
                        state.Nal(data[range]);
                }
            }
        }
        finally
        {
            track.Reset();
        }

        return state.Result();
    }

    /// <summary>Mastering display colour volume SEI (H.264/HEVC): primaries in G, B, R order, 0.00002 and 0.0001 cd/m² units.</summary>
    public static HdrInfo? ParseMasteringDisplaySei(ReadOnlySpan<byte> p)
    {
        if (p.Length < 24)
            return null;
        const double unit = 0.00002;
        return new HdrInfo
        {
            DisplayPrimaries = [Pair(p, 8, unit), Pair(p, 0, unit), Pair(p, 4, unit)], // R, G, B
            WhitePoint = Pair(p, 12, unit),
            MaxLuminance = BinaryPrimitives.ReadUInt32BigEndian(p[16..]) * 0.0001,
            MinLuminance = BinaryPrimitives.ReadUInt32BigEndian(p[20..]) * 0.0001,
        };
    }

    /// <summary>
    /// Ambient viewing environment (H.264/HEVC SEI 148, and the MP4 'amve' box with the same layout): illuminance in
    /// 0.0001 lux, light chromaticity in 0.00002 units. Null when the illuminance is 0 (invalid).
    /// </summary>
    public static HdrInfo? ParseAmbientViewingEnvironment(ReadOnlySpan<byte> p)
    {
        if (p.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(p) == 0)
            return null;
        return new HdrInfo { AmbientIlluminance = BinaryPrimitives.ReadUInt32BigEndian(p) * 0.0001, AmbientLight = Pair(p, 4, 0.00002) };
    }

    /// <summary>AV1 metadata_hdr_mdcv: primaries in R, G, B order as 0.16 fixed point, luminance 24.8 / 18.14 fixed point.</summary>
    public static HdrInfo? ParseAv1Mdcv(ReadOnlySpan<byte> p)
    {
        if (p.Length < 24)
            return null;
        const double chroma = 1.0 / (1 << 16);
        return new HdrInfo
        {
            DisplayPrimaries = [Pair(p, 0, chroma), Pair(p, 4, chroma), Pair(p, 8, chroma)],
            WhitePoint = Pair(p, 12, chroma),
            MaxLuminance = BinaryPrimitives.ReadUInt32BigEndian(p[16..]) / 256.0,
            MinLuminance = BinaryPrimitives.ReadUInt32BigEndian(p[20..]) / 16384.0,
        };
    }

    /// <summary>Two big-endian 16-bit values at <paramref name="offset"/> scaled by <paramref name="unit"/>.</summary>
    internal static (double X, double Y) Pair(ReadOnlySpan<byte> p, int offset, double unit) =>
        (BinaryPrimitives.ReadUInt16BigEndian(p[offset..]) * unit, BinaryPrimitives.ReadUInt16BigEndian(p[(offset + 2)..]) * unit);

    private sealed class State(CodecType codec)
    {
        private ColorInfo _color = ColorInfo.Unspecified;
        private HdrInfo? _mastering;
        private (int Cll, int Fall)? _light;
        private HdrInfo? _ambient;
        private int? _preferredTransfer;

        public bool Complete => _color.IsSpecified && _mastering is not null && _light is not null && _ambient is not null && _preferredTransfer is not null;

        public void Nal(ReadOnlySpan<byte> nal)
        {
            var hevc = codec == CodecType.Hevc;
            var type = hevc ? NalUnits.HevcType(nal) : NalUnits.H264Type(nal);
            try
            {
                if (!_color.IsSpecified && type == (hevc ? Hevc.NalSps : 7))
                    _color = Meaningful(hevc ? Hevc.ParseSps(nal).Color : H264.ParseSps(nal).Color);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
            {
            }

            Sei.ForEachMessageInNal(nal, hevc, (seiType, payload) =>
            {
                if (seiType == Sei.MasteringDisplayColourVolume)
                    _mastering ??= ParseMasteringDisplaySei(payload);
                else if (seiType == Sei.ContentLightLevelInfo && payload.Length >= 4)
                    _light ??= (BinaryPrimitives.ReadUInt16BigEndian(payload), BinaryPrimitives.ReadUInt16BigEndian(payload[2..]));
                else if (seiType == Sei.AmbientViewingEnvironment)
                    _ambient ??= ParseAmbientViewingEnvironment(payload);
                else if (seiType == Sei.AlternativeTransferCharacteristics && payload.Length >= 1 && payload[0] is not (0 or 2))
                    _preferredTransfer ??= payload[0]; // e.g. HLG (18) signalled as BT.2020 SDR (14) for older decoders
                return false;
            });
        }

        public void Obus(ReadOnlySpan<byte> data) => Av1.ForEachObu(data, (type, payload) =>
        {
            if (type == Av1.ObuSequenceHeader && !_color.IsSpecified)
            {
                _color = Meaningful(Av1.SequenceHeaderColor(payload));
            }
            else if (type == Av1.ObuMetadata)
            {
                var q = 0;
                if (DolbyVision.Leb128(payload, ref q, out var metadataType))
                {
                    if (metadataType == Av1MetadataHdrMdcv)
                        _mastering ??= ParseAv1Mdcv(payload[q..]);
                    else if (metadataType == Av1MetadataHdrCll && payload.Length >= q + 4)
                        _light ??= (BinaryPrimitives.ReadUInt16BigEndian(payload[q..]), BinaryPrimitives.ReadUInt16BigEndian(payload[(q + 2)..]));
                }
            }

            return false;
        });

        public VideoStreamInfo Result()
        {
            HdrInfo? hdr = _mastering;
            if (_light is { } light)
                hdr = (hdr ?? new HdrInfo()) with { MaxCll = light.Cll, MaxFall = light.Fall };
            if (_ambient is { } ambient)
                hdr = (hdr ?? new HdrInfo()) with { AmbientIlluminance = ambient.AmbientIlluminance, AmbientLight = ambient.AmbientLight };

            // The alternative transfer characteristics SEI names the transfer the stream really uses (FFmpeg applies it
            // the same way, and writes it to MP4 'colr').
            var color = _color.IsSpecified && _preferredTransfer is { } preferred ? _color with { Transfer = preferred } : _color;
            return new VideoStreamInfo(color, hdr);
        }

        /// <summary>A colour description of "unspecified" code points (2/2/2) says nothing.</summary>
        private static ColorInfo Meaningful(ColorInfo c) =>
            c is { Primaries: 2, Transfer: 2, Matrix: 2 } ? ColorInfo.Unspecified : c;
    }
}
