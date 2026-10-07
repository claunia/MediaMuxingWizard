using MMW.Core.Model;
using MMW.Core.Resources;

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
    Av2,
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

    /// <summary>DivX XSUB bitmap subtitles (AVI); only converted to text by OCR.</summary>
    Xsub,
    Cea608,
    Ttml,

    // Added later (kept at the end so stored values do not shift)

    /// <summary>AVS2 video (IEEE 1857.4 / GB/T 33475.2): start-code delimited stream, sequence header in-band.</summary>
    Avs2,

    /// <summary>MPEG-5 Essential Video Coding (ISO/IEC 23094-1).</summary>
    Evc,

    /// <summary>AVS3 video (T/AI 109.2 / IEEE 1857.10): start-code delimited stream, sequence header in-band.</summary>
    Avs3,

    /// <summary>AVS1-P2 / AVS+ video (GB/T 20090.2, GY/T 257.1): start-code delimited stream, sequence header in-band.</summary>
    Avs1,

    /// <summary>A Video for Windows codec this application does not model (MS-MPEG4, WMV, VC-1, DV, Cinepak…), kept as it is.</summary>
    VfwVideo,

    /// <summary>An Audio Compression Manager codec this application does not model (WMA, ADPCM…), kept as it is.</summary>
    AcmAudio,

    /// <summary>RealVideo (RV10/RV20/RV30/RV40).</summary>
    RealVideo,

    /// <summary>VC-1 (SMPTE 421M) as MP4 stores it ('vc-1' with 'dvc1').</summary>
    Vc1,

    /// <summary>H.263 as 3GPP stores it ('s263' with 'd263').</summary>
    H263,

    /// <summary>Dirac / SMPTE VC-2 ('drac').</summary>
    Dirac,

    /// <summary>Avid DNxHD / DNxHR (SMPTE VC-3; 'AVdn', 'AVdh').</summary>
    Dnxhd,

    /// <summary>AMR narrowband ('samr' with 'damr').</summary>
    AmrNb,

    /// <summary>AMR wideband ('sawb' with 'damr').</summary>
    AmrWb,

    /// <summary>MPEG-H 3D Audio (ISO/IEC 23008-3; 'mhm1'/'mhm2' MHAS packets, 'mha1'/'mha2' with 'mhaC').</summary>
    MpegH,
}

/// <summary>
/// Container-neutral description of a track's codec and the parameters needed to write it into any container.
/// </summary>
/// <remarks>
/// <para><see cref="Extradata"/> holds the codec configuration in a canonical form:</para>
/// <list type="table">
/// <item><term>H.264</term><description>AVCDecoderConfigurationRecord (avcC); samples are length-prefixed NAL units.</description></item>
/// <item><term>HEVC</term><description>HEVCDecoderConfigurationRecord (hvcC); samples are length-prefixed NAL units.</description></item>
/// <item><term>VVC</term><description>VVCDecoderConfigurationRecord (vvcC without its FullBox header); samples are length-prefixed NAL units.</description></item>
/// <item><term>EVC</term><description>EVCDecoderConfigurationRecord (evcC); samples are length-prefixed NAL units.</description></item>
/// <item><term>AVS1/AVS2/AVS3</term><description>None (AVS3 may hold the sequence header unit); samples are start-code delimited pictures.</description></item>
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
/// <item><term>ASS/SSA/WebVTT</term><description>The UTF-8 text header (script info and styles / WebVTT header with its STYLE and REGION blocks). WebVTT samples hold the cue text; the cue settings travel in <see cref="MediaSample.CueSettings"/>.</description></item>
/// <item><term>tx3g</term><description>The tx3g sample-entry payload after the 8-byte SampleEntry header (display flags, box, style, ftab).</description></item>
/// <item><term>VobSub</term><description>The .idx header text (size, palette).</description></item>
/// <item><term>VFW video</term><description>BITMAPINFOHEADER followed by the codec's extra data (Matroska V_MS/VFW/FOURCC CodecPrivate).</description></item>
/// <item><term>ACM audio</term><description>WAVEFORMATEX followed by the codec's extra data (Matroska A_MS/ACM CodecPrivate).</description></item>
/// <item><term>RealVideo</term><description>The RealMedia 'VIDO' type-specific data (Matroska V_REAL/* CodecPrivate).</description></item>
/// <item><term>VC-1, H.263, Dirac, DNxHD, AMR, AC-4, MPEG-H</term><description>The QuickTime / ISO sample entry, header included (Matroska V_QUICKTIME / A_QUICKTIME CodecPrivate).</description></item>
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

    /// <summary>
    /// Colour description found in the bitstream (VUI, AV1 sequence header). Writers use it when the container has
    /// none (<see cref="Color"/> unspecified), so remuxed files are signalled at container level too.
    /// </summary>
    public ColorInfo StreamColor { get; init; } = ColorInfo.Unspecified;

    /// <summary>Static HDR10 metadata found in the bitstream (SEI, AV1 metadata OBUs); fills what <see cref="Hdr"/> lacks.</summary>
    public HdrInfo? StreamHdr { get; init; }

    /// <summary>Container colour, or the bitstream's when the container has none.</summary>
    public ColorInfo EffectiveColor => Color.IsSpecified ? Color : StreamColor;

    /// <summary>Container static HDR metadata completed with the bitstream's.</summary>
    public HdrInfo? EffectiveHdr => HdrInfo.Merge(Hdr, StreamHdr);

    /// <summary>The frames carry HDR10+ (SMPTE ST 2094-40) dynamic metadata, in the bitstream or next to it.</summary>
    public bool Hdr10Plus { get; init; }

    /// <summary>Video profile and level found in the bitstream when the configuration record has none (AVS).</summary>
    public string VideoProfile { get; init; } = string.Empty;

    /// <summary>The frames carry HDR Vivid (CUVA, T/UWA 005.1) dynamic metadata in the bitstream.</summary>
    public bool HdrVivid { get; init; }

    /// <summary>Other dynamic HDR metadata the frames carry (ST 2094-10, SL-HDR).</summary>
    public Codecs.DynamicHdrFormats OtherDynamicHdr { get; init; }

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

    /// <summary>Codec profile or product shown with the format (e.g. "DTS-HD MA", "DTS:X"); empty when plain.</summary>
    public string AudioProfile { get; init; } = string.Empty;

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
    public string FormatName => Codec switch
    {
        CodecType.VfwVideo when Codecs.Vfw.ParseBitmapInfoHeader(Extradata) is { } bih => Codecs.Vfw.VideoName(bih.FourCc),
        CodecType.AcmAudio when Codecs.Vfw.ParseWaveFormatEx(Extradata) is { } wfx => Codecs.Vfw.AudioName(wfx.Tag),
        CodecType.RealVideo when Codecs.Vfw.RealVideoFourCc(Extradata) is { } fourCc => Codecs.Vfw.RealVideoName(fourCc),
        _ => CodecNames.Display(Codec, SourceCodecId),
    };

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
        CodecType.Av2 => "AV2",
        CodecType.Vp8 => "VP8",
        CodecType.Vp9 => "VP9",
        CodecType.Mpeg4Visual => "MPEG-4 Visual",
        CodecType.Mpeg2Video => "MPEG-2",
        CodecType.Mpeg1Video => "MPEG-1",
        CodecType.ProRes => "ProRes",
        CodecType.Theora => "Theora",
        CodecType.Mjpeg => "Motion JPEG",
        CodecType.Avs2 => "AVS2",
        CodecType.Evc => "EVC",
        CodecType.Avs3 => "AVS3",
        CodecType.Avs1 => "AVS",
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
        CodecType.Xsub => "XSUB",
        CodecType.Cea608 => "CEA-608",
        CodecType.Ttml => "TTML",
        CodecType.VfwVideo => "VFW video",
        CodecType.AcmAudio => "ACM audio",
        CodecType.RealVideo => "RealVideo",
        CodecType.Vc1 => "VC-1",
        CodecType.H263 => "H.263",
        CodecType.Dirac => "Dirac",
        CodecType.Dnxhd => "DNxHD",
        CodecType.AmrNb => "AMR-NB",
        CodecType.AmrWb => "AMR-WB",
        CodecType.MpegH => "MPEG-H",
        _ => fallback.Length > 0 ? fallback : Strings.Label_Unknown,
    };

    /// <summary>True for text subtitle formats that can be converted to each other.</summary>
    public static bool IsText(CodecType codec) =>
        codec is CodecType.TextUtf8 or CodecType.Ass or CodecType.Ssa or CodecType.WebVtt or CodecType.Tx3g;

    /// <summary>True for video codecs whose samples may be stored out of presentation order (B-frames).</summary>
    public static bool MayReorder(CodecType codec) =>
        codec is CodecType.H264 or CodecType.Hevc or CodecType.Vvc or CodecType.Mpeg4Visual or CodecType.Mpeg2Video or CodecType.Mpeg1Video or CodecType.Avs2 or
                 CodecType.Evc or CodecType.Avs3 or CodecType.Avs1 or CodecType.Av2 or CodecType.Vc1;
}
