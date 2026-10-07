using System.Buffers.Binary;
using System.Text;
using FFmpeg.AutoGen;
using MMW.Core.Diagnostics;
using MMW.Core.Languages;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Media.Conversion.Interop;

namespace MMW.Media.Conversion;

/// <summary>
/// The FFmpeg codec parameters of a track whose codec this application does not model (WMA, DVD LPCM, RealAudio…):
/// FFmpeg decodes it for conversions.
/// </summary>
public sealed record FFmpegCodec(AVCodecID Id, string Name, byte[]? Extradata, int BlockAlign, long BitRate, int BitsPerCodedSample, uint Tag);

/// <summary>
/// Opens the containers the native readers do not (AVI, MPEG program streams and DVD VOBs, ASF/WMV, WAV/AIFF, MP3,
/// DV, RealMedia, FLV, MXF, VobSub .idx/.sub …) with libavformat and copies their packets. Codecs this application
/// models get their canonical configuration (avcC, the MPEG-4 VOL, the MPEG-1/2 sequence header, STREAMINFO …);
/// others keep their FFmpeg parameters so they can be converted.
/// </summary>
public sealed class FFmpegDemuxerFactory : IDemuxerFactory
{
    /// <summary>File extensions opened through FFmpeg.</summary>
    public static IReadOnlyList<string> Extensions { get; } =
    [
        ".avi", ".divx", ".mpg", ".mpeg", ".m1v", ".m2v", ".mpv", ".vob", ".mod", ".tod", ".evo", ".asf", ".wmv", ".wma", ".wav", ".w64",
        ".aif", ".aiff", ".aifc", ".caf", ".au", ".mp3", ".mp2", ".mpa", ".m2a", ".dv", ".dif", ".rm", ".rmvb", ".ra", ".flv", ".mxf",
        ".nut", ".amr", ".idx", ".sub",
    ];

    public string Name => "FFmpeg";

    /// <summary>A low score: the native readers win for every format they know.</summary>
    public int Probe(string path, ReadOnlySpan<byte> header)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (!Extensions.Contains(ext))
            return 0;
        if (ext == ".sub" && VobSubIndex(path) is null)
            return 0; // MicroDVD or other text .sub files
        return FFmpegLoader.IsAvailable ? 5 : 0;
    }

    public IDemuxer Open(string path, DemuxOptions? options = null)
    {
        FFmpegLoader.EnsureAvailable();
        return FFmpegDemuxer.Open(VobSubIndex(path) ?? path);
    }

    /// <summary>The .idx of a VobSub pair given either file; null when <paramref name="path"/> is not one.</summary>
    public static string? VobSubIndex(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".idx")
            return path;
        if (ext != ".sub")
            return null;
        var idx = System.IO.Path.ChangeExtension(path, ".idx");
        if (File.Exists(idx))
            return idx;
        var upper = System.IO.Path.ChangeExtension(path, ".IDX");
        return File.Exists(upper) ? upper : null;
    }
}

/// <summary>A file read with libavformat: one track per supported stream.</summary>
internal sealed unsafe class FFmpegDemuxer : IDemuxer
{
    private FFmpegDemuxer(string path, string formatName, List<ISampleSource> tracks, TimeSpan duration)
    {
        Path = path;
        FormatName = formatName;
        Tracks = tracks;
        Duration = duration;
    }

    public string Path { get; }

    public string FormatName { get; }

    public ContainerKind Container => ContainerKind.Unknown;

    public IReadOnlyList<ISampleSource> Tracks { get; }

    public TimeSpan Duration { get; }

    public static FFmpegDemuxer Open(string path)
    {
        AVFormatContext* format = null;
        AvUtil.Check(ffmpeg.avformat_open_input(&format, path, null, null), $"Opening '{System.IO.Path.GetFileName(path)}'");
        try
        {
            AvUtil.Check(ffmpeg.avformat_find_stream_info(format, null), "Reading the stream information");
            var name = format->iformat->long_name is null ? "FFmpeg" : new string((sbyte*)format->iformat->long_name);
            var start = format->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : format->start_time / (double)ffmpeg.AV_TIME_BASE;
            var duration = format->duration > 0 ? TimeSpan.FromSeconds(format->duration / (double)ffmpeg.AV_TIME_BASE) : TimeSpan.Zero;
            var tracks = new List<ISampleSource>();
            for (var i = 0; i < (int)format->nb_streams; i++)
            {
                var st = format->streams[i];
                if ((st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
                    continue;
                var info = StreamInfo.From(st, start, path);
                if (info is null)
                {
                    var codec = ffmpeg.avcodec_get_name(st->codecpar->codec_id);
                    AppLog.Info($"{System.IO.Path.GetFileName(path)}: stream {i} ({codec}) is not supported.");
                    continue;
                }

                try
                {
                    tracks.Add(info.Form == PacketForm.AnnexB && FFmpegAnnexBTrack.IsAnnexB(path, info)
                        ? new FFmpegAnnexBTrack(path, info)
                        : new FFmpegTrackSource(path, info, duration));
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                {
                    AppLog.Warn($"{System.IO.Path.GetFileName(path)}: stream {i} skipped: {ex.Message}");
                }
            }

            if (tracks.Count == 0)
                throw new InvalidDataException($"'{System.IO.Path.GetFileName(path)}' has no stream that can be read.");
            return new FFmpegDemuxer(path, name, tracks, duration);
        }
        finally
        {
            ffmpeg.avformat_close_input(&format);
        }
    }

    public void Dispose()
    {
        foreach (var t in Tracks.OfType<IDisposable>())
            t.Dispose();
    }
}

/// <summary>How a stream's packets become samples.</summary>
internal enum PacketForm
{
    /// <summary>Copied unchanged.</summary>
    Copy,

    /// <summary>H.264 / HEVC Annex B (start codes) turned into length-prefixed NAL units.</summary>
    AnnexB,

    /// <summary>MPEG-1/2/4 Part 2 video: presentation times rebuilt from the picture types where the container lacks them.</summary>
    MpegVideo,

    /// <summary>AAC in ADTS frames: the header is dropped.</summary>
    Adts,
}

/// <summary>A stream's description: the configuration and how to read it.</summary>
internal sealed record StreamInfo(int Index, int Id, CodecConfig Config, PacketForm Form, AVRational TimeBase, double StartSeconds, long FrameTicks, string? Filter)
{
    public static unsafe StreamInfo? From(AVStream* st, double startSeconds, string path)
    {
        var p = st->codecpar;
        var tb = st->time_base.num > 0 && st->time_base.den > 0 ? st->time_base : new AVRational { num = 1, den = 90000 };
        var language = Metadata(st->metadata, "language");
        var title = Metadata(st->metadata, "title") ?? string.Empty;
        var extradata = p->extradata_size > 0 ? new ReadOnlySpan<byte>(p->extradata, p->extradata_size).ToArray() : null;
        var config = new CodecConfig
        {
            SourceCodecId = ffmpeg.avcodec_get_name(p->codec_id),
            Timescale = (uint)tb.den,
            Language = language is { Length: 3 } ? LanguageTable.ToBcp47(language) : "und",
            Name = title,
        };
        var frameRate = st->avg_frame_rate.num > 0 && st->avg_frame_rate.den > 0 ? st->avg_frame_rate : st->r_frame_rate;
        long frameTicks = 0;
        if (frameRate.num > 0 && frameRate.den > 0)
            frameTicks = (long)Math.Round((double)frameRate.den * tb.den / ((double)frameRate.num * tb.num));

        switch (p->codec_type)
        {
            case AVMediaType.AVMEDIA_TYPE_VIDEO:
            {
                var video = config with
                {
                    Kind = TrackKind.Video,
                    Width = p->width,
                    Height = p->height,
                    ParNumerator = p->sample_aspect_ratio.num > 0 ? p->sample_aspect_ratio.num : 1,
                    ParDenominator = p->sample_aspect_ratio.den > 0 ? p->sample_aspect_ratio.den : 1,
                    FrameRate = frameRate.num > 0 && frameRate.den > 0 ? frameRate.num / (double)frameRate.den : 0,
                    DefaultSampleDuration = frameTicks,
                    BitsPerSample = p->bits_per_raw_sample > 0 ? p->bits_per_raw_sample : 8,
                };
                return p->codec_id switch
                {
                    AVCodecID.AV_CODEC_ID_H264 => new StreamInfo(st->index, st->id, video with { Codec = CodecType.H264, Extradata = extradata }, PacketForm.AnnexB, tb, startSeconds, frameTicks, null),
                    AVCodecID.AV_CODEC_ID_HEVC => new StreamInfo(st->index, st->id, video with { Codec = CodecType.Hevc, Extradata = extradata }, PacketForm.AnnexB, tb, startSeconds, frameTicks, null),
                    AVCodecID.AV_CODEC_ID_MPEG4 => new StreamInfo(st->index, st->id, video with { Codec = CodecType.Mpeg4Visual, Extradata = extradata }, PacketForm.MpegVideo, tb, startSeconds, frameTicks,
                        "mpeg4_unpack_bframes"),
                    AVCodecID.AV_CODEC_ID_MPEG2VIDEO => new StreamInfo(st->index, st->id, video with { Codec = CodecType.Mpeg2Video, Extradata = extradata }, PacketForm.MpegVideo, tb, startSeconds, frameTicks, null),
                    AVCodecID.AV_CODEC_ID_MPEG1VIDEO => new StreamInfo(st->index, st->id, video with { Codec = CodecType.Mpeg1Video, Extradata = extradata }, PacketForm.MpegVideo, tb, startSeconds, frameTicks, null),
                    AVCodecID.AV_CODEC_ID_MJPEG => new StreamInfo(st->index, st->id, video with { Codec = CodecType.Mjpeg }, PacketForm.Copy, tb, startSeconds, frameTicks, null),
                    AVCodecID.AV_CODEC_ID_RV10 or AVCodecID.AV_CODEC_ID_RV20 or AVCodecID.AV_CODEC_ID_RV30 or AVCodecID.AV_CODEC_ID_RV40 or AVCodecID.AV_CODEC_ID_RV60 =>
                        new StreamInfo(st->index, st->id, video with
                        {
                            Codec = CodecType.RealVideo,
                            Extradata = RealMedia.VideoTypeData(path, st->id) ??
                                        Vfw.RealVideo(FourCc(p->codec_tag), p->width, p->height, video.FrameRate, extradata),
                        }, PacketForm.Copy, tb, startSeconds, frameTicks, null),
                    _ when RiffTag(p, video: true) is var fourCc and not 0 =>
                        // Video for Windows codecs (MS-MPEG4, WMV, VC-1, DV …) as mkvmerge stores them: a BITMAPINFOHEADER,
                        // and frames timed in decoding order as in AVI (Matroska readers take VFW timestamps as such).
                        new StreamInfo(st->index, st->id, video with { Codec = CodecType.VfwVideo, Extradata = Vfw.BitmapInfoHeader(p->width, p->height, FourCc(fourCc), extradata) },
                            PacketForm.Copy, tb, startSeconds, frameTicks, null),
                    _ => null,
                };
            }

            case AVMediaType.AVMEDIA_TYPE_AUDIO:
            {
                var audio = config with
                {
                    Kind = TrackKind.Audio,
                    SampleRate = p->sample_rate,
                    Channels = p->ch_layout.nb_channels,
                    Timescale = (uint)tb.den,
                };
                var bits = p->bits_per_raw_sample > 0 ? p->bits_per_raw_sample : p->bits_per_coded_sample;
                CodecConfig? mapped = p->codec_id switch
                {
                    AVCodecID.AV_CODEC_ID_MP3 => audio with { Codec = CodecType.Mp3 },
                    AVCodecID.AV_CODEC_ID_MP2 => audio with { Codec = CodecType.Mp2 },
                    AVCodecID.AV_CODEC_ID_MP1 => audio with { Codec = CodecType.Mp1 },
                    AVCodecID.AV_CODEC_ID_AC3 => audio with { Codec = CodecType.Ac3 },
                    AVCodecID.AV_CODEC_ID_EAC3 => audio with { Codec = CodecType.Eac3 },
                    AVCodecID.AV_CODEC_ID_DTS => audio with { Codec = CodecType.Dts },
                    AVCodecID.AV_CODEC_ID_TRUEHD => audio with { Codec = CodecType.TrueHd },
                    AVCodecID.AV_CODEC_ID_AAC when extradata is { Length: >= 2 } => audio with { Codec = CodecType.Aac, Extradata = extradata },
                    AVCodecID.AV_CODEC_ID_AAC => audio with { Codec = CodecType.Aac },
                    AVCodecID.AV_CODEC_ID_FLAC when extradata is { Length: >= 34 } =>
                        audio with { Codec = CodecType.Flac, Extradata = Flac.FixLastFlags([0, 0, 0, 34, .. extradata.AsSpan(extradata.Length >= 42 && extradata.AsSpan(0, 4).SequenceEqual("fLaC"u8) ? 8 : 0, 34)]), BitsPerSample = bits },
                    AVCodecID.AV_CODEC_ID_ALAC when extradata is { Length: >= 36 } => audio with { Codec = CodecType.Alac, Extradata = extradata[12..] },
                    AVCodecID.AV_CODEC_ID_OPUS when extradata is { Length: >= 19 } => audio with { Codec = CodecType.Opus, Extradata = extradata, Timescale = 48000 },
                    AVCodecID.AV_CODEC_ID_VORBIS when extradata is { Length: > 0 } => audio with { Codec = CodecType.Vorbis, Extradata = extradata },
                    _ => Pcm(audio, p->codec_id),
                };
                if (mapped is null)
                {
                    // A codec only FFmpeg knows: kept as is so it can be converted.
                    var native = new FFmpegCodec(p->codec_id, ffmpeg.avcodec_get_name(p->codec_id), extradata, p->block_align, p->bit_rate, p->bits_per_coded_sample, p->codec_tag);
                    if (RiffTag(p, video: false) is var tag and not 0)
                    {
                        // Audio Compression Manager codecs (WMA, ADPCM …) as mkvmerge stores them: a WAVEFORMATEX.
                        mapped = audio with
                        {
                            Codec = CodecType.AcmAudio,
                            BitsPerSample = bits,
                            Extradata = Vfw.WaveFormatEx((int)tag, p->ch_layout.nb_channels, p->sample_rate, p->bit_rate, p->block_align, p->bits_per_coded_sample, extradata),
                            Native = native,
                        };
                        return new StreamInfo(st->index, st->id, mapped, PacketForm.Copy, tb, startSeconds, 0, null);
                    }

                    if (ffmpeg.avcodec_find_decoder(p->codec_id) == null)
                        return null;
                    mapped = audio with
                    {
                        Codec = CodecType.Unknown,
                        BitsPerSample = bits,
                        Native = native,
                    };
                }

                var form = mapped.Codec == CodecType.Aac && mapped.Extradata is null ? PacketForm.Adts : PacketForm.Copy;
                return new StreamInfo(st->index, st->id, mapped, form, tb, startSeconds, 0, null);
            }

            case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
            {
                var sub = config with
                {
                    Kind = TrackKind.Subtitle,
                    SubtitleWidth = p->width,
                    SubtitleHeight = p->height,
                };
                CodecConfig? mapped = p->codec_id switch
                {
                    AVCodecID.AV_CODEC_ID_SUBRIP or AVCodecID.AV_CODEC_ID_TEXT => sub with { Codec = CodecType.TextUtf8 },
                    AVCodecID.AV_CODEC_ID_ASS when extradata is { Length: > 0 } => sub with { Codec = CodecType.Ass, Extradata = extradata },
                    AVCodecID.AV_CODEC_ID_DVD_SUBTITLE => sub with { Codec = CodecType.VobSub, Extradata = VobSubHeader(extradata, p->width, p->height) },
                    AVCodecID.AV_CODEC_ID_DVB_SUBTITLE => sub with { Codec = CodecType.DvbSub, Extradata = extradata },
                    AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE => sub with { Codec = CodecType.Pgs },
                    AVCodecID.AV_CODEC_ID_XSUB => sub with { Codec = CodecType.Xsub },
                    _ => null,
                };
                return mapped is null ? null : new StreamInfo(st->index, st->id, mapped, PacketForm.Copy, tb, startSeconds, 0, null);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// The Video for Windows FourCC or ACM format tag of a stream: the container's own when it is a RIFF one (AVI, ASF,
    /// WAV), otherwise (video only: raw DV …) the one AVI would use; 0 when the codec has none.
    /// </summary>
    private static unsafe uint RiffTag(AVCodecParameters* p, bool video)
    {
        var tags = stackalloc AVCodecTag*[] { video ? ffmpeg.avformat_get_riff_video_tags() : ffmpeg.avformat_get_riff_audio_tags(), null };
        if (p->codec_tag != 0 && ffmpeg.av_codec_get_id(tags, p->codec_tag) == p->codec_id)
            return p->codec_tag;
        return video ? ffmpeg.av_codec_get_tag(tags, p->codec_id) : 0;
    }

    /// <summary>A little-endian FourCC as text.</summary>
    internal static string FourCc(uint tag)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, tag);
        return Encoding.ASCII.GetString(bytes);
    }

    /// <summary>Linear PCM FFmpeg codecs as <see cref="CodecType.Pcm"/>; null for other codecs.</summary>
    private static CodecConfig? Pcm(CodecConfig audio, AVCodecID id) => id switch
    {
        AVCodecID.AV_CODEC_ID_PCM_U8 => audio with { Codec = CodecType.Pcm, BitsPerSample = 8 },
        AVCodecID.AV_CODEC_ID_PCM_S16LE => audio with { Codec = CodecType.Pcm, BitsPerSample = 16 },
        AVCodecID.AV_CODEC_ID_PCM_S16BE => audio with { Codec = CodecType.Pcm, BitsPerSample = 16, PcmBigEndian = true },
        AVCodecID.AV_CODEC_ID_PCM_S24LE => audio with { Codec = CodecType.Pcm, BitsPerSample = 24 },
        AVCodecID.AV_CODEC_ID_PCM_S24BE => audio with { Codec = CodecType.Pcm, BitsPerSample = 24, PcmBigEndian = true },
        AVCodecID.AV_CODEC_ID_PCM_S32LE => audio with { Codec = CodecType.Pcm, BitsPerSample = 32 },
        AVCodecID.AV_CODEC_ID_PCM_S32BE => audio with { Codec = CodecType.Pcm, BitsPerSample = 32, PcmBigEndian = true },
        AVCodecID.AV_CODEC_ID_PCM_F32LE => audio with { Codec = CodecType.Pcm, BitsPerSample = 32, PcmFloat = true },
        AVCodecID.AV_CODEC_ID_PCM_F32BE => audio with { Codec = CodecType.Pcm, BitsPerSample = 32, PcmFloat = true, PcmBigEndian = true },
        AVCodecID.AV_CODEC_ID_PCM_F64LE => audio with { Codec = CodecType.Pcm, BitsPerSample = 64, PcmFloat = true },
        AVCodecID.AV_CODEC_ID_PCM_F64BE => audio with { Codec = CodecType.Pcm, BitsPerSample = 64, PcmFloat = true, PcmBigEndian = true },
        _ => null,
    };

    /// <summary>The VobSub .idx header (FFmpeg's extradata for .idx files), or a minimal one for VOB streams without it.</summary>
    private static byte[] VobSubHeader(byte[]? extradata, int width, int height)
    {
        if (extradata is { Length: > 0 })
            return extradata;
        return Encoding.UTF8.GetBytes($"size: {(width > 0 ? width : 720)}x{(height > 0 ? height : 480)}\n");
    }

    private static unsafe string? Metadata(AVDictionary* dictionary, string key)
    {
        var entry = ffmpeg.av_dict_get(dictionary, key, null, 0);
        return entry == null ? null : new string((sbyte*)entry->value);
    }
}

/// <summary>RealMedia file headers.</summary>
internal static class RealMedia
{
    /// <summary>
    /// The type-specific data of a video stream's media properties ('MDPR' chunk) as the file holds it ('VIDO' …),
    /// which Matroska keeps as V_REAL CodecPrivate; null when the file is not RealMedia or has no such stream.
    /// </summary>
    public static byte[]? VideoTypeData(string path, int streamNumber)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[10];
            while (file.Position + 10 <= file.Length)
            {
                var start = file.Position;
                file.ReadExactly(header);
                var id = Encoding.ASCII.GetString(header[..4]);
                var size = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
                if (start == 0 && id != ".RMF" || id == "DATA" || size < 10)
                    return null;
                if (id == "MDPR")
                {
                    var chunk = new byte[size - 10];
                    file.ReadExactly(chunk);
                    var number = BinaryPrimitives.ReadUInt16BigEndian(chunk);
                    var at = 2 + 7 * 4; // bit rates, packet sizes, start time, preroll, duration
                    at += 1 + chunk[at]; // stream name
                    at += 1 + chunk[at]; // MIME type
                    var length = (int)BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(at));
                    var data = chunk.AsSpan(at + 4, length);
                    if (number == streamNumber && Vfw.RealVideoFourCc(data) is not null)
                        return data.ToArray();
                }

                file.Position = start + size;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or IndexOutOfRangeException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}

/// <summary>Reads the packets of one stream with its own libavformat context (other streams are discarded).</summary>
internal sealed unsafe class FFmpegPacketReader : IDisposable
{
    private AVFormatContext* _format;
    private AVPacket* _packet;
    private AVBSFContext* _filter;
    private readonly int _stream;
    private bool _drained;

    public FFmpegPacketReader(string path, int stream, string? filter)
    {
        _stream = stream;
        AVFormatContext* format = null;
        AvUtil.Check(ffmpeg.avformat_open_input(&format, path, null, null), $"Opening '{System.IO.Path.GetFileName(path)}'");
        _format = format;
        try
        {
            AvUtil.Check(ffmpeg.avformat_find_stream_info(format, null), "Reading the stream information");
            for (var i = 0; i < (int)format->nb_streams; i++)
                format->streams[i]->discard = i == stream ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
            _packet = ffmpeg.av_packet_alloc();
            if (filter is not null && ffmpeg.av_bsf_get_by_name(filter) is var bsf && bsf != null)
            {
                AVBSFContext* context = null;
                AvUtil.Check(ffmpeg.av_bsf_alloc(bsf, &context), "av_bsf_alloc");
                _filter = context;
                AvUtil.Check(ffmpeg.avcodec_parameters_copy(context->par_in, format->streams[stream]->codecpar), "avcodec_parameters_copy");
                context->time_base_in = format->streams[stream]->time_base;
                AvUtil.Check(ffmpeg.av_bsf_init(context), $"Initialising {filter}");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// The next packet of the stream: data, pts, dts (AV_NOPTS_VALUE when unknown), duration, key flag, and the samples
    /// to skip at its start and end (encoder delay and padding, e.g. from an MP3 LAME header).
    /// </summary>
    public (byte[] Data, long Pts, long Dts, long Duration, bool Key, int SkipStart, int SkipEnd)? Next()
    {
        while (true)
        {
            if (_filter != null)
            {
                var ret = ffmpeg.av_bsf_receive_packet(_filter, _packet);
                if (ret >= 0)
                    return Take();
                if (ret != AvUtil.EAgain && ret != ffmpeg.AVERROR_EOF)
                    AvUtil.Check(ret, "Filtering packets");
                if (ret == ffmpeg.AVERROR_EOF)
                    return null;
            }

            if (_drained)
                return null;
            var read = ffmpeg.av_read_frame(_format, _packet);
            if (read < 0)
            {
                _drained = true;
                if (_filter == null)
                    return null;
                ffmpeg.av_bsf_send_packet(_filter, null);
                continue;
            }

            if (_packet->stream_index != _stream)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            if (_filter == null)
                return Take();
            AvUtil.Check(ffmpeg.av_bsf_send_packet(_filter, _packet), "Filtering packets");
        }
    }

    private (byte[] Data, long Pts, long Dts, long Duration, bool Key, int SkipStart, int SkipEnd) Take()
    {
        try
        {
            ulong size = 0;
            var skip = ffmpeg.av_packet_get_side_data(_packet, AVPacketSideDataType.AV_PKT_DATA_SKIP_SAMPLES, &size);
            var (start, end) = skip != null && size >= 8
                ? ((int)BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(skip, 4)), (int)BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(skip + 4, 4)))
                : (0, 0);
            return (AvUtil.PacketData(_packet), _packet->pts, _packet->dts, _packet->duration, (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0, start, end);
        }
        finally
        {
            ffmpeg.av_packet_unref(_packet);
        }
    }

    public void Dispose()
    {
        fixed (AVPacket** p = &_packet)
            ffmpeg.av_packet_free(p);
        fixed (AVBSFContext** f = &_filter)
            ffmpeg.av_bsf_free(f);
        fixed (AVFormatContext** f = &_format)
            ffmpeg.avformat_close_input(f);
    }
}

/// <summary>One stream of a file read with FFmpeg, as samples in this application's canonical form.</summary>
internal sealed class FFmpegTrackSource : ISampleSource, IDisposable
{
    private const int ProbePackets = 600;
    private readonly string _path;
    private readonly StreamInfo _info;
    private readonly Queue<MediaSample> _queue = new();
    private FFmpegPacketReader? _reader;
    private MpegReorder? _reorder;
    private readonly long _presentationDelay;
    private readonly long _timescale;

    public FFmpegTrackSource(string path, StreamInfo info, TimeSpan duration)
    {
        _path = path;
        _info = info;
        Duration = duration;
        var config = info.Config;

        // The first packets complete the configuration and tell whether pictures are reordered.
        using (var reader = new FFmpegPacketReader(path, info.Index, info.Filter))
        {
            var types = new List<char>();
            long firstPts = ffmpeg.AV_NOPTS_VALUE, firstDts = ffmpeg.AV_NOPTS_VALUE;
            for (var i = 0; i < ProbePackets && reader.Next() is { } packet; i++)
            {
                if (i == 0)
                {
                    (firstPts, firstDts) = (packet.Pts, packet.Dts);
                    config = Complete(config, packet.Data);
                }

                if (info.Form == PacketForm.MpegVideo)
                    types.Add(MpegReorder.PictureType(config.Codec, packet.Data));
                else if (i > 0)
                    break;
            }

            // The delay between decoding and presentation: the container's when it gives both times, otherwise one frame
            // when pictures are reordered (B pictures), none when they are not.
            _presentationDelay = firstPts != ffmpeg.AV_NOPTS_VALUE && firstDts != ffmpeg.AV_NOPTS_VALUE && firstPts >= firstDts
                ? (firstPts - firstDts) * info.TimeBase.num
                : types.Contains('B') ? Math.Max(1, info.FrameTicks) * info.TimeBase.num : 0;
        }

        if (config.Codec is CodecType.H264 or CodecType.Hevc && config.Extradata is not { Length: > 0 } || config.Codec == CodecType.Mpeg4Visual && config.Extradata is null)
            throw new InvalidDataException($"No {config.FormatName} configuration was found in the first packet.");

        // Audio is timed in samples; other tracks in the stream's time base.
        _timescale = config.Kind == TrackKind.Audio && config.SampleRate > 0 ? config.SampleRate : info.TimeBase.den;
        Config = config with
        {
            Timescale = (uint)_timescale,
            DefaultSampleDuration = config.Kind == TrackKind.Audio && config.DefaultSampleDuration > 0 ? config.DefaultSampleDuration : Ticks(info.FrameTicks),
        };
        // FFmpeg's start time already counts the samples skipped at the start (decoder delay, encoder priming).
        MediaStart = (long)Math.Round(info.StartSeconds * _timescale);
        TrackId = (uint)(info.Index + 1);
    }

    public uint TrackId { get; }

    public CodecConfig Config { get; }

    public TimeSpan StartOffset => TimeSpan.Zero;

    public long MediaStart { get; }

    public TimeSpan Duration { get; }

    public long SampleCountHint => -1;

    /// <summary>Completes a configuration from the first packet: H.264/HEVC parameter sets, the MPEG-4 VOL, MPEG-1/2 sequence header, AAC from ADTS.</summary>
    private static CodecConfig Complete(CodecConfig config, byte[] first)
    {
        switch (config.Codec)
        {
            case CodecType.H264 or CodecType.Hevc:
            {
                if (config.Extradata is { Length: > 0 } x && x[0] == 1)
                    return config; // already avcC / hvcC
                var source = config.Extradata is { Length: > 0 } annexB ? [.. annexB, .. first] : first;
                return config with { Extradata = ParameterSetRecord(config.Codec, source) };
            }

            case CodecType.Mpeg4Visual:
            {
                if (config.Extradata is { Length: > 0 })
                    return config;
                var vop = IndexOfStartCode(first, 0xB6);
                return vop > 0 ? config with { Extradata = first[..vop] } : config;
            }

            case CodecType.Mpeg1Video or CodecType.Mpeg2Video:
            {
                var sequence = IndexOfStartCode(first, 0xB3);
                if (sequence < 0)
                    return config;
                var end = first.Length;
                foreach (var code in new byte[] { 0xB8, 0x00 })
                {
                    var at = IndexOfStartCode(first, code, sequence + 4);
                    if (at > sequence)
                        end = Math.Min(end, at);
                }

                return config with { Extradata = first[sequence..end] };
            }

            case CodecType.Aac when config.Extradata is null && Aac.ParseAdts(first) is { } h:
                return config with { Extradata = Aac.BuildConfig(h.ObjectType, config.SampleRate > 0 ? config.SampleRate : 48000, h.ChannelConfig), DefaultSampleDuration = 1024 };
            default:
                return config;
        }
    }

    /// <summary>avcC / hvcC from the parameter sets of Annex B data.</summary>
    private static byte[]? ParameterSetRecord(CodecType codec, byte[] data)
    {
        var nals = NalUnits.SplitAnnexB(data).Select(r => data[r]).ToList();
        if (codec == CodecType.H264)
        {
            var sps = nals.Where(n => NalUnits.H264Type(n) == H264.NalSps).ToList();
            var pps = nals.Where(n => NalUnits.H264Type(n) == H264.NalPps).ToList();
            return sps.Count > 0 && pps.Count > 0 ? H264.BuildAvcC(sps, pps) : null;
        }

        var vps = nals.Where(n => NalUnits.HevcType(n) == Hevc.NalVps).ToList();
        var hsps = nals.Where(n => NalUnits.HevcType(n) == Hevc.NalSps).ToList();
        var hpps = nals.Where(n => NalUnits.HevcType(n) == Hevc.NalPps).ToList();
        return vps.Count > 0 && hsps.Count > 0 && hpps.Count > 0 ? Hevc.BuildHvcC(vps, hsps, hpps) : null;
    }

    internal static int IndexOfStartCode(byte[] data, byte code, int from = 0)
    {
        for (var i = from; i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && data[i + 3] == code)
                return i;
        }

        return -1;
    }

    public MediaSample? ReadNext()
    {
        _reader ??= new FFmpegPacketReader(_path, _info.Index, _info.Filter);
        while (_queue.Count == 0)
        {
            if (_reader.Next() is not { } packet)
            {
                _reorder?.Flush(_queue);
                return _queue.Count > 0 ? _queue.Dequeue() : null;
            }

            Enqueue(packet);
        }

        return _queue.Dequeue();
    }

    /// <summary>A time in the stream's time base in this track's timescale.</summary>
    private long Ticks(long value) =>
        _timescale == _info.TimeBase.den ? value * _info.TimeBase.num : (long)Math.Round(value * (double)_info.TimeBase.num * _timescale / _info.TimeBase.den);

    private void Enqueue((byte[] Data, long Pts, long Dts, long Duration, bool Key, int SkipStart, int SkipEnd) packet)
    {
        var dts = packet.Dts != ffmpeg.AV_NOPTS_VALUE ? Ticks(packet.Dts) : packet.Pts != ffmpeg.AV_NOPTS_VALUE ? Ticks(packet.Pts) : long.MinValue;
        var pts = packet.Pts != ffmpeg.AV_NOPTS_VALUE ? Ticks(packet.Pts) : long.MinValue;
        var data = packet.Data;
        var sync = packet.Key;
        switch (_info.Form)
        {
            case PacketForm.AnnexB when data.Length > 4 && !(data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1))):
                break; // already length-prefixed
            case PacketForm.AnnexB:
            {
                var nals = NalUnits.SplitAnnexB(data);
                data = NalUnits.ToLengthPrefixed(data, nals);
                break;
            }

            case PacketForm.Adts when Aac.ParseAdts(data) is { } h:
                data = data[h.HeaderLength..];
                break;
            case PacketForm.MpegVideo:
            {
                var type = MpegReorder.PictureType(Config.Codec, data);
                sync = type == 'I';
                _reorder ??= new MpegReorder(_presentationDelay, Config.DefaultSampleDuration);
                _reorder.Add(new MediaSample { Dts = dts, IsSync = sync, Data = data, Duration = Config.DefaultSampleDuration }, type, pts, _queue);
                return;
            }
        }

        if (dts == long.MinValue)
            return; // no timing at all (a stray packet before the first timestamp)
        var duration = packet.Duration > 0 ? Ticks(packet.Duration) : Config.DefaultSampleDuration;
        if (duration <= 0 && Config.Codec == CodecType.VobSub && Spu.DisplayDuration(data) is { } shown)
            duration = (long)Math.Round(shown.TotalSeconds * _timescale); // .idx/.sub give start times only
        var sample = new MediaSample
        {
            Dts = dts,
            CtsOffset = pts == long.MinValue ? 0 : pts - dts,
            Duration = duration,
            IsSync = sync || Config.Kind != TrackKind.Video,
            Data = data,
            TrimEnd = Config.Kind == TrackKind.Audio ? packet.SkipEnd : 0, // encoder padding of the last packet
        };
        _queue.Enqueue(sample);
    }

    public void Reset()
    {
        _reader?.Dispose();
        _reader = null;
        _reorder = null;
        _queue.Clear();
    }

    public void Dispose() => Reset();
}

/// <summary>
/// Presentation times of MPEG-1/2/4 Part 2 pictures from their coding types, for containers that only give decoding
/// times (AVI, ASF, MPEG program stream pictures without a PTS): B pictures are shown when decoded, I and P pictures
/// when the next one arrives; the n-th picture shown takes the n-th decoding time plus the stream's delay. A time the
/// container gives is kept. Pictures leave in decoding order once their presentation time is known.
/// </summary>
internal sealed class MpegReorder(long delay, long frameTicks)
{
    private readonly List<long> _dts = [];
    private readonly LinkedList<(MediaSample Sample, bool Timed, long Pts)> _waiting = new();
    private LinkedListNode<(MediaSample Sample, bool Timed, long Pts)>? _pendingReference;
    private int _shown;

    /// <summary>'I', 'P', 'B' or 'S' (MPEG-4 sprite), '?' when the picture header cannot be found.</summary>
    public static char PictureType(CodecType codec, byte[] data)
    {
        if (codec == CodecType.Mpeg4Visual)
        {
            var vop = FFmpegTrackSource.IndexOfStartCode(data, 0xB6);
            return vop < 0 || vop + 4 >= data.Length ? '?' : "IPBS"[data[vop + 4] >> 6];
        }

        var picture = FFmpegTrackSource.IndexOfStartCode(data, 0x00);
        if (picture < 0 || picture + 5 >= data.Length)
            return '?';
        return ((data[picture + 5] >> 3) & 7) switch
        {
            1 => 'I',
            2 => 'P',
            3 => 'B',
            _ => '?',
        };
    }

    /// <param name="pts">The container's presentation time, used when known (long.MinValue otherwise).</param>
    public void Add(MediaSample sample, char type, long pts, Queue<MediaSample> output)
    {
        if (sample.Dts == long.MinValue)
            sample.Dts = _dts.Count > 0 ? _dts[^1] + frameTicks : 0;
        _dts.Add(sample.Dts);
        var node = _waiting.AddLast((sample, false, pts));
        if (type == 'B')
        {
            Show(node);
        }
        else
        {
            if (_pendingReference is { } reference)
                Show(reference);
            _pendingReference = node;
        }

        Release(output);
    }

    public void Flush(Queue<MediaSample> output)
    {
        if (_pendingReference is { } reference)
            Show(reference);
        _pendingReference = null;
        Release(output);
    }

    private void Show(LinkedListNode<(MediaSample Sample, bool Timed, long Pts)> node)
    {
        var slot = Math.Min(_shown++, _dts.Count - 1);
        var pts = node.Value.Pts != long.MinValue ? node.Value.Pts : _dts[slot] + delay;
        node.Value.Sample.CtsOffset = pts - node.Value.Sample.Dts;
        node.Value = (node.Value.Sample, true, node.Value.Pts);
    }

    private void Release(Queue<MediaSample> output)
    {
        while (_waiting.First is { Value.Timed: true } first)
        {
            output.Enqueue(first.Value.Sample);
            _waiting.RemoveFirst();
        }
    }
}

/// <summary>The packets of one stream as a forward-only byte stream (for parsers that find their own frame boundaries).</summary>
internal sealed class FFmpegPacketStream(string path, StreamInfo info) : Stream
{
    private readonly FFmpegPacketReader _reader = new(path, info.Index, null);
    private byte[] _current = [];
    private int _offset;
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (_offset >= _current.Length)
        {
            if (_reader.Next() is not { } packet)
                return 0;
            (_current, _offset) = (packet.Data, 0);
        }

        var n = Math.Min(count, _current.Length - _offset);
        Buffer.BlockCopy(_current, _offset, buffer, offset, n);
        _offset += n;
        _position += n;
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _reader.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// H.264 / HEVC stored as Annex B in a container without reliable presentation times (AVI stores decoding times only):
/// the raw video parser rebuilds access units and presentation times from the picture order count, as for raw
/// .264/.265 files; times are offset to the stream's first decoding time.
/// </summary>
internal sealed class FFmpegAnnexBTrack : ISampleSource, IDisposable
{
    private readonly ISampleSource _inner;
    private readonly long _offset;

    public FFmpegAnnexBTrack(string path, StreamInfo info)
    {
        var frameRate = info.Config.FrameRate > 0 ? info.Config.FrameRate : (double?)null;
        _inner = MMW.Formats.Elementary.ElementaryFormat.OpenAnnexBVideo(() => new FFmpegPacketStream(path, info), info.Config.Codec, frameRate);
        var timescale = Math.Max(1u, _inner.Config.Timescale);
        long firstDts = 0;
        using (var reader = new FFmpegPacketReader(path, info.Index, null))
        {
            if (reader.Next() is { } first && (first.Dts != ffmpeg.AV_NOPTS_VALUE ? first.Dts : first.Pts) is var t && t != ffmpeg.AV_NOPTS_VALUE)
                firstDts = t;
        }

        _offset = (long)Math.Round(firstDts * (double)info.TimeBase.num / info.TimeBase.den * timescale);
        Config = _inner.Config with { Language = info.Config.Language, Name = info.Config.Name };
        MediaStart = (long)Math.Round(info.StartSeconds * timescale);
        TrackId = (uint)(info.Index + 1);
    }

    /// <summary>True when the stream's first packet starts with an Annex B start code.</summary>
    public static bool IsAnnexB(string path, StreamInfo info)
    {
        using var reader = new FFmpegPacketReader(path, info.Index, null);
        return reader.Next() is { Data: { Length: > 4 } d } && d[0] == 0 && d[1] == 0 && (d[2] == 1 || (d[2] == 0 && d[3] == 1));
    }

    public uint TrackId { get; }

    public CodecConfig Config { get; }

    public TimeSpan StartOffset => TimeSpan.Zero;

    public long MediaStart { get; }

    public TimeSpan Duration => _inner.Duration;

    public long SampleCountHint => _inner.SampleCountHint;

    public MediaSample? ReadNext()
    {
        if (_inner.ReadNext() is not { } sample)
            return null;
        sample.Dts += _offset;
        return sample;
    }

    public void Reset() => _inner.Reset();

    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
