using FFmpeg.AutoGen;
using MMW.Core.Media;

namespace MMW.Media.Conversion.Interop;

/// <summary>One bitmap of a decoded subtitle event, converted to RGBA.</summary>
internal sealed record DecodedRect(int X, int Y, int Width, int Height, byte[] Rgba, bool Forced);

/// <summary>A decoded subtitle event (no rectangles = the screen is cleared).</summary>
/// <param name="StartMs">Display start relative to the packet time, in milliseconds.</param>
/// <param name="EndMs">Display end relative to the packet time, in milliseconds; null when the event has no end.</param>
internal sealed record DecodedSubtitle(uint StartMs, uint? EndMs, IReadOnlyList<DecodedRect> Rects);

/// <summary>Decodes bitmap subtitle packets (PGS, VobSub, DVB) with libavcodec.</summary>
internal sealed unsafe class SubtitleDecoderContext : IDisposable
{
    private AVCodecContext* _decoder;
    private AVPacket* _packet;
    private bool _disposed;

    /// <exception cref="NotSupportedException">The codec has no decoder.</exception>
    public SubtitleDecoderContext(CodecConfig config)
    {
        try
        {
            _decoder = AvUtil.OpenDecoder(config);
            _packet = ffmpeg.av_packet_alloc();
            if (_packet == null)
                throw new InsufficientMemoryException("av_packet_alloc failed.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    ~SubtitleDecoderContext() => Release();

    /// <summary>Canvas width known to the decoder (VobSub "size:" line, PGS presentation size); 0 when unknown.</summary>
    public int CanvasWidth => _decoder->width;

    public int CanvasHeight => _decoder->height;

    /// <summary>Decodes one packet; returns null when it produced no event (or could not be decoded).</summary>
    public DecodedSubtitle? Decode(ReadOnlySpan<byte> data, long pts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (data.IsEmpty)
            return null;
        AvUtil.FillPacket(_packet, data);
        _packet->pts = pts;
        _packet->dts = pts;
        AVSubtitle subtitle = default;
        var got = 0;
        try
        {
            var ret = ffmpeg.avcodec_decode_subtitle2(_decoder, &subtitle, &got, _packet);
            if (ret < 0 || got == 0)
                return null;

            var rects = new List<DecodedRect>((int)subtitle.num_rects);
            for (var i = 0; i < subtitle.num_rects; i++)
            {
                var rect = subtitle.rects[i];
                if (rect->type != AVSubtitleType.SUBTITLE_BITMAP || rect->w <= 0 || rect->h <= 0 || rect->data[0] == null)
                    continue;
                rects.Add(new DecodedRect(rect->x, rect->y, rect->w, rect->h, ToRgba(rect), (rect->flags & ffmpeg.AV_SUBTITLE_FLAG_FORCED) != 0));
            }

            var end = subtitle.end_display_time;
            return new DecodedSubtitle(subtitle.start_display_time, end is 0 or uint.MaxValue || end <= subtitle.start_display_time ? null : end, rects);
        }
        finally
        {
            if (got != 0)
                ffmpeg.avsubtitle_free(&subtitle);
            ffmpeg.av_packet_unref(_packet);
        }
    }

    /// <summary>Expands a palettised rectangle (indices in data[0], 0xAARRGGBB palette in data[1]) to RGBA.</summary>
    private static byte[] ToRgba(AVSubtitleRect* rect)
    {
        var width = rect->w;
        var height = rect->h;
        var stride = rect->linesize[0];
        var indices = rect->data[0];
        var palette = (uint*)rect->data[1];
        var colors = Math.Clamp(rect->nb_colors, 0, 256);
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = indices + (long)y * stride;
            var o = y * width * 4;
            for (var x = 0; x < width; x++, o += 4)
            {
                var index = row[x];
                var c = palette != null && index < colors ? palette[index] : 0u;
                rgba[o] = (byte)(c >> 16);
                rgba[o + 1] = (byte)(c >> 8);
                rgba[o + 2] = (byte)c;
                rgba[o + 3] = (byte)(c >> 24);
            }
        }

        return rgba;
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
        fixed (AVCodecContext** c = &_decoder)
            ffmpeg.avcodec_free_context(c);
    }
}
