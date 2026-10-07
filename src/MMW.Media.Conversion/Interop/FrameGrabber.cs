using System.Globalization;
using FFmpeg.AutoGen;
using MMW.Media.Conversion.Resources;

namespace MMW.Media.Conversion.Interop;

/// <summary>
/// Opens a file with libavformat, decodes the video frame shown at a given time and encodes it as a JPEG (scaled with
/// libswscale). All native objects are released by <see cref="Dispose"/>.
/// </summary>
internal sealed unsafe class FrameGrabber : IDisposable
{
    private AVFormatContext* _format;
    private AVCodecContext* _decoder;
    private AVFrame* _frame;
    private AVFrame* _best;
    private AVPacket* _packet;
    private readonly int _stream;
    private bool _disposed;

    /// <param name="path">Media file.</param>
    /// <param name="trackId">Container track ID (MP4 track ID / Matroska track number) of the video track; null for the main video track.</param>
    /// <exception cref="InvalidDataException">The file cannot be opened or has no (decodable) video track.</exception>
    public FrameGrabber(string path, uint? trackId)
    {
        try
        {
            AVFormatContext* format = null;
            AvUtil.Check(ffmpeg.avformat_open_input(&format, path, null, null), string.Format(CultureInfo.CurrentCulture, Strings.Op_Opening, Path.GetFileName(path)));
            _format = format;
            AvUtil.Check(ffmpeg.avformat_find_stream_info(format, null), Strings.Op_ReadingStreamInfo);

            _stream = -1;
            var hasIds = false;
            for (var i = 0; i < (int)format->nb_streams; i++)
            {
                var st = format->streams[i];
                hasIds |= st->id != 0;
                if (!IsVideo(st))
                    continue;
                if (trackId is { } id && st->id != id)
                    continue;
                _stream = i;
                break;
            }

            // Matroska streams carry no ID: libavformat creates one stream per TrackEntry, in order, and track
            // numbers are assigned sequentially from 1 by the common muxers.
            if (_stream < 0 && trackId is { } number && !hasIds && number >= 1 && number <= format->nb_streams && IsVideo(format->streams[number - 1]))
                _stream = (int)number - 1;

            if (trackId is null)
            {
                AVCodec* unused = null;
                var best = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &unused, 0);
                if (best >= 0 && (format->streams[best]->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
                    _stream = best;
            }

            if (_stream < 0)
                throw new InvalidDataException(trackId is null ? Strings.Conversion_NoVideoTrack : string.Format(CultureInfo.CurrentCulture, Strings.Conversion_VideoTrackNotFound, trackId));

            var stream = format->streams[_stream];
            var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec == null)
                throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Conversion_NoDecoderFor, ffmpeg.avcodec_get_name(stream->codecpar->codec_id)));
            _decoder = ffmpeg.avcodec_alloc_context3(codec);
            if (_decoder == null)
                throw new InsufficientMemoryException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_CallFailed, "avcodec_alloc_context3"));
            AvUtil.Check(ffmpeg.avcodec_parameters_to_context(_decoder, stream->codecpar), "avcodec_parameters_to_context");
            _decoder->pkt_timebase = stream->time_base;
            _decoder->thread_count = 0;
            AvUtil.Check(ffmpeg.avcodec_open2(_decoder, codec, null), Strings.Op_OpeningVideoDecoder);

            _frame = ffmpeg.av_frame_alloc();
            _best = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            if (_frame == null || _best == null || _packet == null)
                throw new InsufficientMemoryException(Strings.FFmpeg_FramesAllocFailed);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    ~FrameGrabber() => Release();

    private static bool IsVideo(AVStream* stream) =>
        stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && (stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0;

    /// <summary>Duration of the file (zero when unknown).</summary>
    public TimeSpan Duration => _format->duration > 0 ? TimeSpan.FromSeconds(_format->duration / (double)ffmpeg.AV_TIME_BASE) : TimeSpan.Zero;

    /// <summary>
    /// Decodes the frame displayed at <paramref name="time"/> (seeks to the preceding key frame, then decodes
    /// forward) and returns it as a JPEG at most <paramref name="maxWidth"/> pixels wide (display aspect ratio
    /// applied), or null when no frame could be decoded.
    /// </summary>
    public byte[]? Capture(TimeSpan time, int maxWidth, int quality, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stream = _format->streams[_stream];
        var tb = stream->time_base;
        var start = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time;
        var target = start + ffmpeg.av_rescale_q((long)(time.TotalSeconds * 1_000_000), new AVRational { num = 1, den = 1_000_000 }, tb);

        if (ffmpeg.av_seek_frame(_format, _stream, target, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
            ffmpeg.av_seek_frame(_format, _stream, start, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(_decoder);
        ffmpeg.av_frame_unref(_best);

        var haveFrame = false;
        var done = false;
        var packets = 0;
        while (!done)
        {
            if ((++packets & 0x1F) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var ret = ffmpeg.av_read_frame(_format, _packet);
            if (ret < 0)
            {
                ffmpeg.avcodec_send_packet(_decoder, null);
                done = true;
            }
            else
            {
                try
                {
                    if (_packet->stream_index != _stream)
                        continue;
                    var sent = ffmpeg.avcodec_send_packet(_decoder, _packet);
                    if (sent < 0 && sent != AvUtil.EAgain)
                        continue; // corrupt packet: skip
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }
            }

            while (true)
            {
                var r = ffmpeg.avcodec_receive_frame(_decoder, _frame);
                if (r == AvUtil.EAgain || r == ffmpeg.AVERROR_EOF)
                    break;
                if (r < 0)
                    break;
                var pts = _frame->best_effort_timestamp;
                var reached = pts != ffmpeg.AV_NOPTS_VALUE && pts >= target;

                // Keep the last frame before the target, or the first at/after it when nothing precedes.
                if (!reached || !haveFrame || pts == target)
                {
                    ffmpeg.av_frame_unref(_best);
                    ffmpeg.av_frame_move_ref(_best, _frame);
                    haveFrame = true;
                }
                else
                {
                    ffmpeg.av_frame_unref(_frame);
                }

                if (reached)
                {
                    done = true;
                    break;
                }
            }
        }

        return haveFrame ? EncodeJpeg(_best, maxWidth, quality) : null;
    }

    private static byte[] EncodeJpeg(AVFrame* frame, int maxWidth, int quality)
    {
        // Display size: apply the sample aspect ratio to the width.
        var sar = frame->sample_aspect_ratio;
        var displayWidth = sar.num > 0 && sar.den > 0 ? frame->width * (double)sar.num / sar.den : frame->width;
        var width = (int)Math.Min(Math.Round(displayWidth), maxWidth);
        var height = (int)Math.Round(width * frame->height / displayWidth);
        width = Math.Max(2, width & ~1);
        height = Math.Max(2, height & ~1);

        SwsContext* sws = null;
        AVFrame* scaled = null;
        AVCodecContext* encoder = null;
        AVPacket* packet = null;
        try
        {
            sws = ffmpeg.sws_getContext(frame->width, frame->height, (AVPixelFormat)frame->format, width, height, AVPixelFormat.AV_PIX_FMT_YUVJ420P,
                (int)SwsFlags.SWS_BICUBIC, null, null, null);
            if (sws == null)
                throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Conversion_CannotScale, ffmpeg.av_get_pix_fmt_name((AVPixelFormat)frame->format)));

            // Limited-range sources are expanded to the full range JPEG uses.
            var srcFull = frame->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0;
            var coefficients = Coefficients(frame->colorspace == AVColorSpace.AVCOL_SPC_BT709 ? ffmpeg.SWS_CS_ITU709 : ffmpeg.SWS_CS_DEFAULT);
            var dstCoefficients = Coefficients(ffmpeg.SWS_CS_DEFAULT);
            ffmpeg.sws_setColorspaceDetails(sws, coefficients, srcFull, dstCoefficients, 1, 0, 1 << 16, 1 << 16);

            scaled = ffmpeg.av_frame_alloc();
            if (scaled == null)
                throw new InsufficientMemoryException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_CallFailed, "av_frame_alloc"));
            scaled->format = (int)AVPixelFormat.AV_PIX_FMT_YUVJ420P;
            scaled->width = width;
            scaled->height = height;
            scaled->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            AvUtil.Check(ffmpeg.av_frame_get_buffer(scaled, 0), "av_frame_get_buffer");
            byte*[] srcData = [frame->data[0], frame->data[1], frame->data[2], frame->data[3]];
            int[] srcStride = [frame->linesize[0], frame->linesize[1], frame->linesize[2], frame->linesize[3]];
            byte*[] dstData = [scaled->data[0], scaled->data[1], scaled->data[2], scaled->data[3]];
            int[] dstStride = [scaled->linesize[0], scaled->linesize[1], scaled->linesize[2], scaled->linesize[3]];
            AvUtil.Check(ffmpeg.sws_scale(sws, srcData, srcStride, 0, frame->height, dstData, dstStride), Strings.Op_Scaling);

            var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
            if (codec == null)
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_NoEncoder, "JPEG"));
            encoder = ffmpeg.avcodec_alloc_context3(codec);
            if (encoder == null)
                throw new InsufficientMemoryException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_CallFailed, "avcodec_alloc_context3"));
            encoder->width = width;
            encoder->height = height;
            encoder->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUVJ420P;
            encoder->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            encoder->time_base = new AVRational { num = 1, den = 25 };
            encoder->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
            encoder->global_quality = ffmpeg.FF_QP2LAMBDA * quality;
            AvUtil.Check(ffmpeg.avcodec_open2(encoder, codec, null), string.Format(CultureInfo.CurrentCulture, Strings.Op_OpeningEncoder, "JPEG"));

            scaled->quality = encoder->global_quality;
            scaled->pts = 0;
            AvUtil.Check(ffmpeg.avcodec_send_frame(encoder, scaled), Strings.Op_EncodingJpeg);
            AvUtil.Check(ffmpeg.avcodec_send_frame(encoder, null), Strings.Op_EncodingJpeg);
            packet = ffmpeg.av_packet_alloc();
            AvUtil.Check(ffmpeg.avcodec_receive_packet(encoder, packet), Strings.Op_EncodingJpeg);
            return AvUtil.PacketData(packet);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&encoder);
            ffmpeg.av_frame_free(&scaled);
            if (sws != null)
                ffmpeg.sws_freeContext(sws);
        }
    }

    private static int_array4 Coefficients(int colorspace)
    {
        var table = ffmpeg.sws_getCoefficients(colorspace);
        var result = default(int_array4);
        for (var i = 0u; i < 4; i++)
            result[i] = table[i];
        return result;
    }

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    private void Release()
    {
        if (_disposed)
            return;
        _disposed = true;
        fixed (AVPacket** p = &_packet)
            ffmpeg.av_packet_free(p);
        fixed (AVFrame** f = &_frame)
            ffmpeg.av_frame_free(f);
        fixed (AVFrame** f = &_best)
            ffmpeg.av_frame_free(f);
        fixed (AVCodecContext** c = &_decoder)
            ffmpeg.avcodec_free_context(c);
        fixed (AVFormatContext** f = &_format)
            ffmpeg.avformat_close_input(f);
    }
}
