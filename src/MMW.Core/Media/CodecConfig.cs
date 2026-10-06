using MMW.Core.Model;

namespace MMW.Core.Media;

/// <summary>Container-neutral codec identifiers.</summary>
public enum CodecType
{
    Unknown,

    // Video
    H264,
    Hevc,
    Vvc,
    Av1,
    Vp8,
    Vp9,
    Mpeg4Visual,
    Mpeg2Video,
    Mpeg1Video,
    ProRes,
    Theora,
    Mjpeg,

    // Audio
    Aac,
    Ac3,
    Eac3,
    Ac4,
    Dts,
    TrueHd,
    Mlp,
    Opus,
    Vorbis,
    Flac,
    Alac,
    Mp3,
    Mp2,
    Mp1,
    Pcm,

    // Subtitles
    /// <summary>Plain UTF-8 text with SubRip-style HTML markup (Matroska S_TEXT/UTF8).</summary>
    TextUtf8,

    /// <summary>Advanced SubStation Alpha (Matroska S_TEXT/ASS block format).</summary>
    Ass,

    /// <summary>SubStation Alpha v4 (Matroska S_TEXT/SSA block format).</summary>
    Ssa,

    /// <summary>WebVTT cue text (Matroska S_TEXT/WEBVTT block format).</summary>
    WebVtt,

    /// <summary>3GPP timed text (MP4 tx3g samples).</summary>
    Tx3g,

    /// <summary>DVD subpictures (SPU packets).</summary>
    VobSub,
    Pgs,
    DvbSub,
    Cea608,
    Ttml,
}

/// <summary>
/// Container-neutral description of a track's codec and the parameters needed to write it into any container.
/// </summary>
/// <remarks>
/// <para><see cref="Extradata"/> holds the codec configuration in a canonical form:</para>
/// <list type="table">
/// <item><term>H.264</term><description>AVCDecoderConfigurationRecord (avcC); samples are length-prefixed NAL units.</description></item>
/// <item><term>HEVC</term><description>HEVCDecoderConfigurationRecord (hvcC); samples are length-prefixed NAL units.</description></item>
/// <item><term>AV1</term><description>AV1CodecConfigurationRecord (av1C).</description></item>
/// <item><term>VP8/VP9</term><description>VPCodecConfigurationRecord as stored in a vpcC box payload (version/flags included); optional.</description></item>
/// <item><term>MPEG-1/2/4 video</term><description>Decoder specific info (sequence / VOL headers).</description></item>
/// <item><term>AAC</term><description>AudioSpecificConfig.</description></item>
/// <item><term>AC-3, E-AC-3</term><description>dac3 / dec3 payload; optional (built from the first frame when missing).</description></item>
/// <item><term>DTS</term><description>ddts payload; optional.</description></item>
/// <item><term>Opus</term><description>Ogg Opus identification header ("OpusHead", little-endian, RFC 7845).</description></item>
/// <item><term>FLAC</term><description>FLAC metadata blocks (STREAMINFO first) without the "fLaC" marker.</description></item>
/// <item><term>ALAC</term><description>ALACSpecificConfig (24 bytes, optionally followed by a channel layout).</description></item>
/// <item><term>Vorbis</term><description>The three Xiph-laced headers (Matroska CodecPrivate form).</description></item>
/// <item><term>ASS/SSA/WebVTT</term><description>The UTF-8 text header (script info and styles / WebVTT header).</description></item>
/// <item><term>tx3g</term><description>The tx3g sample-entry payload after the 8-byte SampleEntry header (display flags, box, style, ftab).</description></item>
/// <item><term>VobSub</term><description>The .idx header text (size, palette).</description></item>
/// </list>
/// </remarks>
public sealed record CodecConfig
{
    public CodecType Codec { get; init; }

    public TrackKind Kind { get; init; }

    /// <summary>The identifier the source container used (MP4 sample entry four-cc or Matroska CodecID).</summary>
    public string SourceCodecId { get; init; } = string.Empty;

    /// <summary>Codec configuration in canonical form (see the remarks of <see cref="CodecConfig"/>).</summary>
    public byte[]? Extradata { get; init; }

    /// <summary>Units per second of the sample times produced by the source.</summary>
    public uint Timescale { get; init; }

    /// <summary>Typical sample duration in <see cref="Timescale"/> units (frame duration); 0 when unknown or variable.</summary>
    public long DefaultSampleDuration { get; init; }

    /// <summary>BCP-47 language tag.</summary>
    public string Language { get; init; } = "und";

    public string Name { get; init; } = string.Empty;

    // ----- video

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Pixel aspect ratio (1:1 when unknown).</summary>
    public int ParNumerator { get; init; } = 1;

    public int ParDenominator { get; init; } = 1;

    public double FrameRate { get; init; }

    public ColorInfo Color { get; init; } = ColorInfo.Unspecified;

    public HdrInfo? Hdr { get; init; }

    /// <summary>Raw Dolby Vision decoder configuration record (dvcC/dvvC payload).</summary>
    public byte[]? DolbyVisionConfig { get; init; }

    /// <summary>The frames carry HDR10+ (SMPTE ST 2094-40) dynamic metadata, in the bitstream or next to it.</summary>
    public bool Hdr10Plus { get; init; }

    /// <summary>
    /// HDR10+ metadata is stored outside the bitstream, in Matroska BlockAdditions (VP9): only Matroska can keep it.
    /// </summary>
    public bool Hdr10PlusInBlockAdditions { get; init; }

    // ----- audio

    public int Channels { get; init; }

    public int SampleRate { get; init; }

    /// <summary>Bits per sample (PCM, FLAC, ALAC); 0 when not applicable.</summary>
    public int BitsPerSample { get; init; }

    /// <summary>PCM sample format: big-endian byte order.</summary>
    public bool PcmBigEndian { get; init; }

    /// <summary>PCM sample format: IEEE float.</summary>
    public bool PcmFloat { get; init; }

    /// <summary>Samples the decoder discards at the start (Opus pre-skip, AAC priming), as a duration.</summary>
    public TimeSpan CodecDelay { get; init; }

    /// <summary>Decoder pre-roll after a seek (Opus).</summary>
    public TimeSpan SeekPreRoll { get; init; }

    /// <summary>E-AC-3 with Joint Object Coding (Dolby Atmos).</summary>
    public bool IsAtmos { get; init; }

    // ----- subtitles

    /// <summary>Subtitle canvas size (tx3g track size, VobSub frame size); 0 when unknown.</summary>
    public int SubtitleWidth { get; init; }

    public int SubtitleHeight { get; init; }

    /// <summary>
    /// The source container's own description of the track (e.g. the MP4 sample entry or the Matroska TrackEntry).
    /// A muxer of the same container family may reuse it verbatim to keep details the neutral fields do not model;
    /// other muxers ignore it.
    /// </summary>
    public object? Native { get; init; }

    /// <summary>Short display name of the codec.</summary>
    public string FormatName => CodecNames.Display(Codec, SourceCodecId);

    public override string ToString() => $"{FormatName} ({SourceCodecId})";
}

/// <summary>Display names of <see cref="CodecType"/> values.</summary>
public static class CodecNames
{
    public static string Display(CodecType codec, string fallback = "") => codec switch
    {
        CodecType.H264 => "H.264",
        CodecType.Hevc => "HEVC",
        CodecType.Vvc => "VVC",
        CodecType.Av1 => "AV1",
        CodecType.Vp8 => "VP8",
        CodecType.Vp9 => "VP9",
        CodecType.Mpeg4Visual => "MPEG-4 Visual",
        CodecType.Mpeg2Video => "MPEG-2",
        CodecType.Mpeg1Video => "MPEG-1",
        CodecType.ProRes => "ProRes",
        CodecType.Theora => "Theora",
        CodecType.Mjpeg => "Motion JPEG",
        CodecType.Aac => "AAC",
        CodecType.Ac3 => "AC-3",
        CodecType.Eac3 => "E-AC-3",
        CodecType.Ac4 => "AC-4",
        CodecType.Dts => "DTS",
        CodecType.TrueHd => "TrueHD",
        CodecType.Mlp => "MLP",
        CodecType.Opus => "Opus",
        CodecType.Vorbis => "Vorbis",
        CodecType.Flac => "FLAC",
        CodecType.Alac => "ALAC",
        CodecType.Mp3 => "MP3",
        CodecType.Mp2 => "MP2",
        CodecType.Mp1 => "MP1",
        CodecType.Pcm => "PCM",
        CodecType.TextUtf8 => "SRT",
        CodecType.Ass => "ASS",
        CodecType.Ssa => "SSA",
        CodecType.WebVtt => "WebVTT",
        CodecType.Tx3g => "Tx3g",
        CodecType.VobSub => "VobSub",
        CodecType.Pgs => "PGS",
        CodecType.DvbSub => "DVB",
        CodecType.Cea608 => "CEA-608",
        CodecType.Ttml => "TTML",
        _ => fallback.Length > 0 ? fallback : "Unknown",
    };

    /// <summary>True for text subtitle formats that can be converted to each other.</summary>
    public static bool IsText(CodecType codec) =>
        codec is CodecType.TextUtf8 or CodecType.Ass or CodecType.Ssa or CodecType.WebVtt or CodecType.Tx3g;

    /// <summary>True for video codecs whose samples may be stored out of presentation order (B-frames).</summary>
    public static bool MayReorder(CodecType codec) =>
        codec is CodecType.H264 or CodecType.Hevc or CodecType.Vvc or CodecType.Mpeg4Visual or CodecType.Mpeg2Video or CodecType.Mpeg1Video;
}
