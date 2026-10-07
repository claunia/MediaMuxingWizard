using System.Buffers.Binary;
using FFmpeg.AutoGen;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;

namespace MMW.Media.Conversion;

/// <summary>Translates the container-neutral <see cref="CodecConfig"/> into FFmpeg codec IDs and extradata.</summary>
internal static class CodecMapping
{
    /// <summary>The FFmpeg codec ID of a track, or <see cref="AVCodecID.AV_CODEC_ID_NONE"/>.</summary>
    public static AVCodecID DecoderId(CodecConfig config) => config.Codec switch
    {
        CodecType.Aac => AVCodecID.AV_CODEC_ID_AAC,
        CodecType.Ac3 => AVCodecID.AV_CODEC_ID_AC3,
        CodecType.Eac3 => AVCodecID.AV_CODEC_ID_EAC3,
        CodecType.Dts => AVCodecID.AV_CODEC_ID_DTS,
        CodecType.TrueHd => AVCodecID.AV_CODEC_ID_TRUEHD,
        CodecType.Mlp => AVCodecID.AV_CODEC_ID_MLP,
        CodecType.Opus => AVCodecID.AV_CODEC_ID_OPUS,
        CodecType.Vorbis => AVCodecID.AV_CODEC_ID_VORBIS,
        CodecType.Flac => AVCodecID.AV_CODEC_ID_FLAC,
        CodecType.Alac => AVCodecID.AV_CODEC_ID_ALAC,
        CodecType.Mp3 => AVCodecID.AV_CODEC_ID_MP3,
        CodecType.Mp2 => AVCodecID.AV_CODEC_ID_MP2,
        CodecType.Mp1 => AVCodecID.AV_CODEC_ID_MP1,
        CodecType.Pcm => PcmId(config),
        CodecType.Pgs => AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE,
        CodecType.VobSub => AVCodecID.AV_CODEC_ID_DVD_SUBTITLE,
        CodecType.DvbSub => AVCodecID.AV_CODEC_ID_DVB_SUBTITLE,
        CodecType.Xsub => AVCodecID.AV_CODEC_ID_XSUB,
        _ => Native(config)?.Id ?? AVCodecID.AV_CODEC_ID_NONE,
    };

    /// <summary>
    /// The FFmpeg parameters of a codec this application does not model: as the FFmpeg demuxer gave them, or from the
    /// Video for Windows / ACM / RealMedia description it is stored with (Matroska V_MS/VFW/FOURCC, A_MS/ACM, V_REAL).
    /// </summary>
    public static unsafe FFmpegCodec? Native(CodecConfig config)
    {
        switch (config.Codec)
        {
            case CodecType.Unknown or CodecType.AcmAudio when config.Native is FFmpegCodec native:
                return native;
            case CodecType.AcmAudio when Vfw.ParseWaveFormatEx(config.Extradata) is { } wfx:
            {
                var tags = stackalloc AVCodecTag*[] { ffmpeg.avformat_get_riff_audio_tags(), null };
                var id = ffmpeg.av_codec_get_id(tags, (uint)wfx.Tag);
                return id == AVCodecID.AV_CODEC_ID_NONE ? null : new FFmpegCodec(id, ffmpeg.avcodec_get_name(id), wfx.Extra, wfx.BlockAlign, wfx.BitRate, wfx.BitsPerSample, (uint)wfx.Tag);
            }

            case CodecType.VfwVideo when Vfw.ParseBitmapInfoHeader(config.Extradata) is { } bih:
            {
                var tag = BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(bih.FourCc));
                var tags = stackalloc AVCodecTag*[] { ffmpeg.avformat_get_riff_video_tags(), null };
                var id = ffmpeg.av_codec_get_id(tags, tag);
                return id == AVCodecID.AV_CODEC_ID_NONE ? null : new FFmpegCodec(id, ffmpeg.avcodec_get_name(id), bih.Extra, 0, 0, 0, tag);
            }

            case CodecType.RealVideo when Vfw.RealVideoFourCc(config.Extradata) is { } fourCc && Vfw.ParseRealVideo(config.Extradata) is { } rv:
            {
                var id = fourCc switch
                {
                    "RV10" => AVCodecID.AV_CODEC_ID_RV10,
                    "RV20" => AVCodecID.AV_CODEC_ID_RV20,
                    "RV30" => AVCodecID.AV_CODEC_ID_RV30,
                    "RV40" => AVCodecID.AV_CODEC_ID_RV40,
                    "RV60" => AVCodecID.AV_CODEC_ID_RV60,
                    _ => AVCodecID.AV_CODEC_ID_NONE,
                };
                return id == AVCodecID.AV_CODEC_ID_NONE ? null : new FFmpegCodec(id, ffmpeg.avcodec_get_name(id), rv.Extra, 0, 0, 0, BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(fourCc)));
            }

            default:
                return null;
        }
    }

    private static AVCodecID PcmId(CodecConfig c) => (c.BitsPerSample, c.PcmFloat, c.PcmBigEndian) switch
    {
        (8, _, _) => AVCodecID.AV_CODEC_ID_PCM_U8,
        (16, _, false) => AVCodecID.AV_CODEC_ID_PCM_S16LE,
        (16, _, true) => AVCodecID.AV_CODEC_ID_PCM_S16BE,
        (24, _, false) => AVCodecID.AV_CODEC_ID_PCM_S24LE,
        (24, _, true) => AVCodecID.AV_CODEC_ID_PCM_S24BE,
        (32, true, false) => AVCodecID.AV_CODEC_ID_PCM_F32LE,
        (32, true, true) => AVCodecID.AV_CODEC_ID_PCM_F32BE,
        (32, false, false) => AVCodecID.AV_CODEC_ID_PCM_S32LE,
        (32, false, true) => AVCodecID.AV_CODEC_ID_PCM_S32BE,
        (64, _, false) => AVCodecID.AV_CODEC_ID_PCM_F64LE,
        (64, _, true) => AVCodecID.AV_CODEC_ID_PCM_F64BE,
        _ => AVCodecID.AV_CODEC_ID_NONE,
    };

    /// <summary>True when the track's codec is known only by its FFmpeg parameters (<see cref="Native"/>).</summary>
    public static bool IsNativeOnly(CodecConfig config) => config.Codec is CodecType.Unknown or CodecType.AcmAudio or CodecType.VfwVideo or CodecType.RealVideo;

    /// <summary>Extradata in the form FFmpeg's decoder expects.</summary>
    public static byte[]? DecoderExtradata(CodecConfig config)
    {
        var data = config.Extradata;
        if (data is null || data.Length == 0)
            return null;
        switch (config.Codec)
        {
            case CodecType.Flac:
                // Canonical form: metadata blocks without the marker; FFmpeg wants "fLaC" + blocks (or a bare STREAMINFO).
                if (data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("fLaC"u8))
                    return data;
                return [.. "fLaC"u8, .. data];
            case CodecType.Alac:
            {
                // Canonical form: ALACSpecificConfig; FFmpeg wants the full 'alac' atom (size, type, version/flags).
                if (data.Length >= 12 && data.AsSpan(4, 4).SequenceEqual("alac"u8))
                    return data;
                var atom = new byte[12 + data.Length];
                BinaryPrimitives.WriteUInt32BigEndian(atom, (uint)atom.Length);
                "alac"u8.CopyTo(atom.AsSpan(4));
                data.CopyTo(atom.AsSpan(12));
                return atom;
            }

            default:
                return data;
        }
    }
}
