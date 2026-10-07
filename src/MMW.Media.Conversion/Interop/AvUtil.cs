using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using MMW.Core.Media;
using MMW.Media.Conversion.Resources;

namespace MMW.Media.Conversion.Interop;

/// <summary>Small helpers around libavutil/libavcodec used by the interop classes.</summary>
internal static unsafe class AvUtil
{
    public static readonly int EAgain = ffmpeg.AVERROR(ffmpeg.EAGAIN);

    /// <summary>True when the loaded FFmpeg has a decoder for <paramref name="id"/>.</summary>
    public static bool HasDecoder(AVCodecID id) => id != AVCodecID.AV_CODEC_ID_NONE && ffmpeg.avcodec_find_decoder(id) != null;

    /// <summary>True when the loaded FFmpeg has the encoder named <paramref name="name"/>.</summary>
    public static bool HasEncoder(string name) => ffmpeg.avcodec_find_encoder_by_name(name) != null;

    /// <summary>FFmpeg's description of an error code.</summary>
    public static string ErrorText(int error)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];
        return ffmpeg.av_strerror(error, buffer, size) == 0 ? Marshal.PtrToStringUTF8((IntPtr)buffer) ?? string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_ErrorCode, error) : string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_ErrorCode, error);
    }

    /// <summary>Throws <see cref="InvalidDataException"/> when <paramref name="result"/> is an error code.</summary>
    public static int Check(int result, string operation)
    {
        if (result < 0)
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_OperationFailed, operation, ErrorText(result)));
        return result;
    }

    /// <summary>Copies <paramref name="data"/> into a padded av_malloc buffer (for AVCodecContext.extradata).</summary>
    public static void SetExtradata(AVCodecContext* context, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;
        var buffer = (byte*)ffmpeg.av_mallocz((ulong)(data.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        if (buffer == null)
            throw new InsufficientMemoryException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_CallFailed, "av_mallocz"));
        data.CopyTo(new Span<byte>(buffer, data.Length));
        context->extradata = buffer;
        context->extradata_size = data.Length;
    }

    /// <summary>Copies a codec context's extradata, or null.</summary>
    public static byte[]? GetExtradata(AVCodecContext* context) =>
        context->extradata == null || context->extradata_size <= 0 ? null : new ReadOnlySpan<byte>(context->extradata, context->extradata_size).ToArray();

    /// <summary>Human-readable channel layout ("5.1(side)").</summary>
    public static string Describe(AVChannelLayout* layout)
    {
        const int size = 128;
        var buffer = stackalloc byte[size];
        return ffmpeg.av_channel_layout_describe(layout, buffer, size) >= 0 ? Marshal.PtrToStringUTF8((IntPtr)buffer) ?? string.Empty : string.Empty;
    }

    /// <summary>Copies a packet's payload.</summary>
    public static byte[] PacketData(AVPacket* packet) =>
        packet->size <= 0 || packet->data == null ? [] : new ReadOnlySpan<byte>(packet->data, packet->size).ToArray();

    /// <summary>Fills <paramref name="packet"/> (unreferenced) with a padded copy of <paramref name="data"/>.</summary>
    public static void FillPacket(AVPacket* packet, ReadOnlySpan<byte> data)
    {
        Check(ffmpeg.av_new_packet(packet, data.Length), "av_new_packet");
        data.CopyTo(new Span<byte>(packet->data, data.Length));
    }

    /// <summary>
    /// Creates and opens a decoder for a track described by <paramref name="config"/> (codec ID, parameters and
    /// extradata translated from the canonical <see cref="CodecConfig"/> form).
    /// </summary>
    /// <exception cref="NotSupportedException">The codec has no FFmpeg decoder.</exception>
    public static AVCodecContext* OpenDecoder(CodecConfig config, IReadOnlyDictionary<string, string>? options = null)
    {
        var id = CodecMapping.DecoderId(config);
        if (id == AVCodecID.AV_CODEC_ID_NONE)
            throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.Conversion_CannotDecode, config.FormatName));
        var codec = ffmpeg.avcodec_find_decoder(id);
        if (codec == null)
            throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_NoDecoder, config.FormatName));

        var context = ffmpeg.avcodec_alloc_context3(codec);
        if (context == null)
            throw new InsufficientMemoryException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_CallFailed, "avcodec_alloc_context3"));
        AVDictionary* dict = null;
        try
        {
            if (config.Timescale > 0)
                context->pkt_timebase = new AVRational { num = 1, den = (int)Math.Min(int.MaxValue, config.Timescale) };
            switch (config.Kind)
            {
                case Core.Model.TrackKind.Audio:
                    if (config.SampleRate > 0)
                        context->sample_rate = config.SampleRate;
                    if (config.Channels > 0)
                        ffmpeg.av_channel_layout_default(&context->ch_layout, config.Channels);
                    if (config.BitsPerSample > 0)
                        context->bits_per_coded_sample = config.BitsPerSample;
                    if (config.Codec == CodecType.Pcm && config.Channels > 0 && config.BitsPerSample > 0)
                        context->block_align = config.Channels * ((config.BitsPerSample + 7) / 8);
                    break;
                case Core.Model.TrackKind.Subtitle:
                case Core.Model.TrackKind.Video:
                    context->width = config.Kind == Core.Model.TrackKind.Video ? config.Width : config.SubtitleWidth;
                    context->height = config.Kind == Core.Model.TrackKind.Video ? config.Height : config.SubtitleHeight;
                    break;
            }

            if (CodecMapping.IsNativeOnly(config) && CodecMapping.Native(config) is { } native)
            {
                // A codec only FFmpeg knows (WMA, DVD LPCM, …): its own parameters as the demuxer gave them.
                context->block_align = native.BlockAlign;
                context->bit_rate = native.BitRate;
                if (native.BitsPerCodedSample > 0)
                    context->bits_per_coded_sample = native.BitsPerCodedSample;
                context->codec_tag = native.Tag;
                if (native.Extradata is { Length: > 0 } nativeExtradata)
                    SetExtradata(context, nativeExtradata);
            }
            else if (CodecMapping.DecoderExtradata(config) is { Length: > 0 } extradata)
            {
                SetExtradata(context, extradata);
            }
            if (options is not null)
            {
                foreach (var (key, value) in options)
                    ffmpeg.av_dict_set(&dict, key, value, 0);
            }

            Check(ffmpeg.avcodec_open2(context, codec, &dict), string.Format(CultureInfo.CurrentCulture, Strings.Op_OpeningDecoder, config.FormatName));
            return context;
        }
        catch
        {
            ffmpeg.avcodec_free_context(&context);
            throw;
        }
        finally
        {
            ffmpeg.av_dict_free(&dict);
        }
    }

    /// <summary>Configurations an encoder supports (null when it accepts any).</summary>
    public static T[]? SupportedConfigs<T>(AVCodec* codec, AVCodecConfig kind, Func<IntPtr, int, T> read)
    {
        void* configs = null;
        var count = 0;
        if (ffmpeg.avcodec_get_supported_config(null, codec, kind, 0, &configs, &count) < 0 || configs == null)
            return null;
        var result = new T[count];
        for (var i = 0; i < count; i++)
            result[i] = read((IntPtr)configs, i);
        return result;
    }
}
