using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using MMW.Core.Diagnostics;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Media.Conversion.Resources;

namespace MMW.Media.Conversion.Interop;

/// <summary>An encoded output packet; <see cref="Pts"/> counts output samples from the first (priming) packet.</summary>
internal readonly record struct EncodedPacket(byte[] Data, long Pts, long Duration);

/// <summary>
/// Decodes audio packets (libavcodec), resamples/downmixes them (libswresample, with Dolby Pro Logic / Pro Logic II
/// matrix encoding), cuts them into encoder frames (AVAudioFifo) and encodes them with FFmpeg's native AAC or AC-3
/// encoder. Every native object is owned by this class and released by <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// Lossless targets (PCM, ALAC) keep the decoded layout, rate and integer samples: the resampler only repacks them
/// (interleaved ↔ planar) into the encoder's format, so the output decodes to the source's exact samples.
/// </remarks>
/// <remarks>
/// <para>
/// The encoder is configured from the first decoded frame (the decoder's real output layout and rate). Output packet
/// times are shifted by the encoder delay so the first packet starts at 0 and the decoded audio at
/// <see cref="InitialPadding"/>.
/// </para>
/// <para>
/// The output follows the source timestamps: when a decoded frame starts more than <see cref="SyncTolerance"/> after
/// the end of the previous one, the gap is filled with silence; when it overlaps it by more than that, the overlap is
/// dropped (libswresample <c>swr_inject_silence</c>/<c>swr_drop_output</c>), so the audio stays in sync with video.
/// </para>
/// </remarks>
internal sealed unsafe class AudioTranscoder : IDisposable
{
    private const int MaxConsecutiveErrors = 200;

    /// <summary>Timestamp deviation tolerated before silence is inserted or audio dropped.</summary>
    public static readonly TimeSpan SyncTolerance = TimeSpan.FromMilliseconds(20);

    private readonly CodecConfig _input;
    private readonly AudioConversionTarget _target;
    private readonly AudioConversionSettings _settings;
    private readonly Queue<EncodedPacket> _packets = new();

    private AVCodecContext* _decoder;
    private AVCodecContext* _encoder;
    private SwrContext* _swr;
    private AVAudioFifo* _fifo;
    private AVFrame* _decoded;
    private AVFrame* _converted;
    private AVFrame* _encodeFrame;
    private AVPacket* _packet;
    private AVChannelLayout _inLayout;
    private AVChannelLayout _outLayout;
    private AVSampleFormat _inFormat = AVSampleFormat.AV_SAMPLE_FMT_NONE;
    private AVSampleFormat _outFormat = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
    private int _inRate;
    private AVMatrixEncoding _matrix;
    private bool _smallLastFrame;
    private long _nextPts;
    private int _consecutiveErrors;
    private readonly double _sourceTimescale;
    private double _expectedTime = double.NaN;
    private bool _finished;
    private bool _disposed;

    public AudioTranscoder(CodecConfig input, AudioConversionTarget target, AudioConversionSettings settings)
    {
        _input = input;
        _target = target;
        _settings = settings;
        _sourceTimescale = Math.Max(1u, input.Timescale);
        try
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            if (input.Codec is CodecType.Ac3 or CodecType.Eac3)
                options["drc_scale"] = settings.EffectiveDrc.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _decoder = AvUtil.OpenDecoder(input, options);
            _decoded = ffmpeg.av_frame_alloc();
            _converted = ffmpeg.av_frame_alloc();
            _encodeFrame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            if (_decoded == null || _converted == null || _encodeFrame == null || _packet == null)
                throw new InsufficientMemoryException(Strings.FFmpeg_FramesAllocFailed);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    ~AudioTranscoder() => Release();

    /// <summary>True once the encoder has been opened (after the first decoded frame).</summary>
    public bool IsConfigured => _encoder != null;

    public int OutputSampleRate { get; private set; }

    public int OutputChannels { get; private set; }

    /// <summary>FFmpeg's name of the output layout ("stereo", "5.1(side)").</summary>
    public string OutputLayout { get; private set; } = string.Empty;

    /// <summary>Input layout as decoded ("5.1(side)").</summary>
    public string InputLayout { get; private set; } = string.Empty;

    public int InputSampleRate => _inRate;

    /// <summary>Bits per sample of a lossless output (PCM, ALAC); 0 otherwise.</summary>
    public int OutputBits { get; private set; }

    /// <summary>Encoder configuration (AAC AudioSpecificConfig, ALAC 'alac' atom); null for AC-3 and PCM.</summary>
    public byte[]? Extradata { get; private set; }

    /// <summary>Encoder delay in output samples (1024 for AAC, 256 for AC-3).</summary>
    public int InitialPadding { get; private set; }

    /// <summary>Samples per encoded frame.</summary>
    public int FrameSize { get; private set; }

    /// <summary>Output bitrate in bit/s.</summary>
    public long Bitrate { get; private set; }

    /// <summary>Output samples handed to the encoder so far (excluding padding).</summary>
    public long EncodedSamples => _nextPts;

    /// <summary>Packets that could not be decoded (skipped).</summary>
    public int DecodeErrors { get; private set; }

    /// <summary>Output samples of silence inserted for gaps in the source timestamps.</summary>
    public long InsertedSamples { get; private set; }

    /// <summary>Output samples dropped for overlaps in the source timestamps.</summary>
    public long DroppedSamples { get; private set; }

    /// <summary>Decodes one packet and encodes whatever output it completes.</summary>
    /// <param name="data">The packet.</param>
    /// <param name="pts">Presentation time in the source timescale.</param>
    /// <exception cref="InvalidDataException">Too many consecutive packets could not be decoded.</exception>
    public void SendPacket(ReadOnlySpan<byte> data, long pts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished || data.IsEmpty)
            return;
        AvUtil.FillPacket(_packet, data);
        _packet->pts = pts;
        _packet->dts = pts;
        var ret = ffmpeg.avcodec_send_packet(_decoder, _packet);
        ffmpeg.av_packet_unref(_packet);
        if (ret < 0 && ret != AvUtil.EAgain)
        {
            DecodeError(ret);
            return;
        }

        ReceiveFrames();
    }

    /// <summary>Flushes the decoder, the resampler, the FIFO and the encoder.</summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
            return;
        _finished = true;
        ffmpeg.avcodec_send_packet(_decoder, null);
        ReceiveFrames();
        if (_encoder == null)
            return;

        // Resampler delay, then the partial last frame.
        if (_swr != null)
            Resample(null, 0);
        EncodeFromFifo(final: true);
        AvUtil.Check(ffmpeg.avcodec_send_frame(_encoder, null), Strings.Op_FlushingEncoder);
        ReceivePackets();
    }

    /// <summary>Takes the next encoded packet.</summary>
    public bool TryDequeue(out EncodedPacket packet) => _packets.TryDequeue(out packet);

    private void ReceiveFrames()
    {
        while (true)
        {
            var ret = ffmpeg.avcodec_receive_frame(_decoder, _decoded);
            if (ret == AvUtil.EAgain || ret == ffmpeg.AVERROR_EOF)
                return;
            if (ret < 0)
            {
                DecodeError(ret);
                return;
            }

            try
            {
                _consecutiveErrors = 0;
                if (_decoded->nb_samples > 0)
                    ProcessFrame(_decoded);
            }
            finally
            {
                ffmpeg.av_frame_unref(_decoded);
            }
        }
    }

    private void DecodeError(int error)
    {
        DecodeErrors++;
        if (DecodeErrors <= 10)
            AppLog.Debug(string.Format(CultureInfo.CurrentCulture, Strings.Conversion_DecodingErrorSkipped, _input.FormatName, AvUtil.ErrorText(error)));
        if (++_consecutiveErrors > MaxConsecutiveErrors)
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Conversion_TrackNotDecodable, _input.FormatName, AvUtil.ErrorText(error)));
    }

    private void ProcessFrame(AVFrame* frame)
    {
        var layout = frame->ch_layout;
        AVChannelLayout normalized = default;
        try
        {
            if (layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
            {
                ffmpeg.av_channel_layout_default(&normalized, layout.nb_channels);
                layout = normalized;
            }

            if (_encoder == null)
                Configure(&layout, (AVSampleFormat)frame->format, frame->sample_rate);
            else if (frame->sample_rate != _inRate || (AVSampleFormat)frame->format != _inFormat || !SameLayout(&layout))
            {
                // Mid-stream format change (e.g. broadcast AC-3 switching between 2.0 and 5.1): drain and rebuild.
                AppLog.Debug(string.Format(CultureInfo.CurrentCulture, Strings.Conversion_DecoderOutputChanged, _input.FormatName));
                Resample(null, 0);
                OpenResampler(&layout, (AVSampleFormat)frame->format, frame->sample_rate);
            }

            Synchronise(frame);
            Resample(frame->extended_data, frame->nb_samples);
            EncodeFromFifo(final: false);
        }
        finally
        {
            ffmpeg.av_channel_layout_uninit(&normalized);
        }
    }

    /// <summary>Fills gaps / drops overlaps between the end of the previous frame and the start of <paramref name="frame"/>.</summary>
    private void Synchronise(AVFrame* frame)
    {
        var pts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
        var duration = frame->nb_samples / (double)Math.Max(1, frame->sample_rate);
        if (pts == ffmpeg.AV_NOPTS_VALUE)
        {
            if (!double.IsNaN(_expectedTime))
                _expectedTime += duration;
            return;
        }

        var time = pts / _sourceTimescale;
        if (!double.IsNaN(_expectedTime))
        {
            var delta = time - _expectedTime;
            if (Math.Abs(delta) > SyncTolerance.TotalSeconds)
            {
                var samples = (int)Math.Min(int.MaxValue, Math.Round(Math.Abs(delta) * OutputSampleRate));
                if (delta > 0)
                {
                    AvUtil.Check(ffmpeg.swr_inject_silence(_swr, samples), Strings.Op_InsertingSilence);
                    InsertedSamples += samples;
                }
                else
                {
                    AvUtil.Check(ffmpeg.swr_drop_output(_swr, samples), Strings.Op_DroppingOverlap);
                    DroppedSamples += samples;
                }

                AppLog.Debug(string.Format(CultureInfo.CurrentCulture, delta > 0 ? Strings.Conversion_TimestampJumpInserting : Strings.Conversion_TimestampJumpDropping,
                             _input.FormatName, delta * 1000, time, samples));
            }
            else
            {
                time = _expectedTime; // within tolerance: keep counting samples (no drift from rounded timestamps)
            }
        }

        _expectedTime = time + duration;
    }

    private bool SameLayout(AVChannelLayout* layout)
    {
        fixed (AVChannelLayout* current = &_inLayout)
            return ffmpeg.av_channel_layout_compare(current, layout) == 0;
    }

    private void Configure(AVChannelLayout* inLayout, AVSampleFormat inFormat, int inRate)
    {
        if (_target is AudioConversionTarget.Pcm or AudioConversionTarget.Alac)
        {
            ConfigureLossless(inLayout, inFormat, inRate);
            return;
        }

        var aac = _target == AudioConversionTarget.Aac;
        var codec = ffmpeg.avcodec_find_encoder_by_name(aac ? "aac" : "ac3");
        if (codec == null)
            throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_NoEncoder, aac ? "AAC" : "AC-3"));
        _smallLastFrame = (codec->capabilities & ffmpeg.AV_CODEC_CAP_SMALL_LAST_FRAME) != 0;

        // Output layout.
        var inChannels = inLayout->nb_channels;
        var mixdown = aac ? _settings.EffectiveMixdown(inChannels) : AudioMixdown.Multichannel;
        var matrix = AVMatrixEncoding.AV_MATRIX_ENCODING_NONE;
        AVChannelLayout outLayout = default;
        switch (mixdown)
        {
            case AudioMixdown.Mono:
                ffmpeg.av_channel_layout_default(&outLayout, 1);
                break;
            case AudioMixdown.Stereo:
            case AudioMixdown.DolbyProLogic:
            case AudioMixdown.DolbyProLogicII:
                ffmpeg.av_channel_layout_default(&outLayout, 2);
                if (inChannels > 2)
                {
                    matrix = mixdown switch
                    {
                        AudioMixdown.DolbyProLogic => AVMatrixEncoding.AV_MATRIX_ENCODING_DOLBY,
                        AudioMixdown.DolbyProLogicII => AVMatrixEncoding.AV_MATRIX_ENCODING_DPLII,
                        _ => AVMatrixEncoding.AV_MATRIX_ENCODING_NONE,
                    };
                }

                break;
            default:
                ChooseLayout(codec, inLayout, aac ? 8 : 6, &outLayout);
                break;
        }

        // Output rate.
        var wanted = aac ? AudioConversionSettings.AacSampleRate(inRate) : AudioConversionSettings.Ac3SampleRate(inRate);
        var rates = AvUtil.SupportedConfigs(codec, AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_RATE, (p, i) => ((int*)p)[i]);
        if (rates is { Length: > 0 } && !rates.Contains(wanted))
            wanted = rates.Where(r => r <= Math.Max(wanted, AudioConversionSettings.MaxAacSampleRate)).OrderBy(r => Math.Abs(r - wanted)).FirstOrDefault(rates[0]);

        var channels = outLayout.nb_channels;
        var bitrate = aac ? (long)_settings.EffectiveBitratePerChannel * 1000 * channels : AudioConversionSettings.Ac3Bitrate(channels) * 1000L;

        var encoder = ffmpeg.avcodec_alloc_context3(codec);
        if (encoder == null)
            throw new InsufficientMemoryException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_CallFailed, "avcodec_alloc_context3"));
        _encoder = encoder;
        encoder->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        encoder->sample_rate = wanted;
        AvUtil.Check(ffmpeg.av_channel_layout_copy(&encoder->ch_layout, &outLayout), "av_channel_layout_copy");
        encoder->bit_rate = bitrate;
        encoder->time_base = new AVRational { num = 1, den = wanted };
        encoder->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        AvUtil.Check(ffmpeg.avcodec_open2(encoder, codec, null), string.Format(CultureInfo.CurrentCulture, Strings.Op_OpeningEncoder, aac ? "AAC" : "AC-3"));

        fixed (AVChannelLayout* target = &_outLayout)
            AvUtil.Check(ffmpeg.av_channel_layout_copy(target, &outLayout), "av_channel_layout_copy");
        ffmpeg.av_channel_layout_uninit(&outLayout);

        OutputSampleRate = wanted;
        OutputChannels = channels;
        fixed (AVChannelLayout* l = &_outLayout)
            OutputLayout = AvUtil.Describe(l);
        InputLayout = AvUtil.Describe(inLayout);
        Extradata = AvUtil.GetExtradata(encoder);
        InitialPadding = encoder->initial_padding;
        FrameSize = encoder->frame_size > 0 ? encoder->frame_size : aac ? 1024 : 1536;
        Bitrate = encoder->bit_rate;
        _matrix = matrix;

        _fifo = ffmpeg.av_audio_fifo_alloc(AVSampleFormat.AV_SAMPLE_FMT_FLTP, channels, FrameSize * 4);
        if (_fifo == null)
            throw new InsufficientMemoryException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_CallFailed, "av_audio_fifo_alloc"));
        OpenResampler(inLayout, inFormat, inRate);
        AppLog.Debug(string.Format(CultureInfo.CurrentCulture, Strings.Conversion_ConvertingLossy, _input.FormatName, InputLayout, inRate, aac ? "AAC" : "AC-3", OutputLayout, wanted,
                     Bitrate / 1000, matrix != AVMatrixEncoding.AV_MATRIX_ENCODING_NONE ? $", {mixdown}" : string.Empty));
    }

    /// <summary>
    /// PCM or ALAC encoder for the decoded layout and rate. The bit depth is the decoder's (FLAC's STREAMINFO) rounded
    /// up to 16, 24 or 32; integer samples are only repacked, never converted through floating point.
    /// </summary>
    private void ConfigureLossless(AVChannelLayout* inLayout, AVSampleFormat inFormat, int inRate)
    {
        var pcm = _target == AudioConversionTarget.Pcm;
        var sourceBits = _decoder->bits_per_raw_sample > 0 ? _decoder->bits_per_raw_sample
            : _input.BitsPerSample > 0 ? _input.BitsPerSample
            : ffmpeg.av_get_bytes_per_sample(inFormat) * 8;
        // Floating-point PCM stays floating point (only the byte order may change).
        var floating = pcm && _input.Codec == CodecType.Pcm && _input.PcmFloat;
        var bits = floating ? (_input.BitsPerSample > 32 ? 64 : 32) : AudioConversionSettings.LosslessBits(sourceBits);
        if (!pcm && bits > 24)
            throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.Conversion_AlacBitDepth, sourceBits));
        var name = floating ? $"pcm_f{bits}le" : pcm ? $"pcm_s{bits}le" : "alac";
        var codec = ffmpeg.avcodec_find_encoder_by_name(name);
        if (codec == null)
            throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_NoEncoder, name));
        _smallLastFrame = (codec->capabilities & (ffmpeg.AV_CODEC_CAP_SMALL_LAST_FRAME | ffmpeg.AV_CODEC_CAP_VARIABLE_FRAME_SIZE)) != 0;

        // The encoder's layout with the source's channel count: the same channels, or a relabelling of the same
        // positions (5.1 with side surrounds → ALAC's 5.1 with back surrounds); never a remix.
        AVChannelLayout outLayout = default;
        var layouts = AvUtil.SupportedConfigs(codec, AVCodecConfig.AV_CODEC_CONFIG_CHANNEL_LAYOUT, (p, i) => (IntPtr)(((AVChannelLayout*)p) + i));
        if (layouts is { Length: > 0 })
        {
            AVChannelLayout* match = null;
            foreach (var p in layouts)
            {
                var candidate = (AVChannelLayout*)p;
                if (ffmpeg.av_channel_layout_compare(candidate, inLayout) == 0)
                {
                    match = candidate;
                    break;
                }

                if (match == null && candidate->nb_channels == inLayout->nb_channels)
                    match = candidate;
            }

            if (match == null)
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.Conversion_TooManyChannels, name, inLayout->nb_channels));
            AvUtil.Check(ffmpeg.av_channel_layout_copy(&outLayout, match), "av_channel_layout_copy");
        }
        else
        {
            AvUtil.Check(ffmpeg.av_channel_layout_copy(&outLayout, inLayout), "av_channel_layout_copy");
        }

        var format = floating
            ? bits == 64 ? AVSampleFormat.AV_SAMPLE_FMT_DBL : AVSampleFormat.AV_SAMPLE_FMT_FLT
            : pcm
            ? bits == 16 ? AVSampleFormat.AV_SAMPLE_FMT_S16 : AVSampleFormat.AV_SAMPLE_FMT_S32
            : bits == 16 ? AVSampleFormat.AV_SAMPLE_FMT_S16P : AVSampleFormat.AV_SAMPLE_FMT_S32P;
        var encoder = ffmpeg.avcodec_alloc_context3(codec);
        if (encoder == null)
            throw new InsufficientMemoryException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_CallFailed, "avcodec_alloc_context3"));
        _encoder = encoder;
        encoder->sample_fmt = format;
        encoder->sample_rate = inRate;
        encoder->bits_per_raw_sample = bits;
        AvUtil.Check(ffmpeg.av_channel_layout_copy(&encoder->ch_layout, &outLayout), "av_channel_layout_copy");
        encoder->time_base = new AVRational { num = 1, den = inRate };
        encoder->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        AvUtil.Check(ffmpeg.avcodec_open2(encoder, codec, null), string.Format(CultureInfo.CurrentCulture, Strings.Op_OpeningEncoder, name));

        fixed (AVChannelLayout* target = &_outLayout)
            AvUtil.Check(ffmpeg.av_channel_layout_copy(target, &outLayout), "av_channel_layout_copy");
        ffmpeg.av_channel_layout_uninit(&outLayout);

        _outFormat = format;
        OutputSampleRate = inRate;
        OutputChannels = encoder->ch_layout.nb_channels;
        OutputBits = bits;
        fixed (AVChannelLayout* l = &_outLayout)
            OutputLayout = AvUtil.Describe(l);
        InputLayout = AvUtil.Describe(inLayout);
        Extradata = pcm ? null : AvUtil.GetExtradata(encoder);
        InitialPadding = 0;
        FrameSize = encoder->frame_size > 0 ? encoder->frame_size : pcm ? AudioConversionSettings.PcmBlockSize : AudioConversionSettings.AlacFrameSize;
        Bitrate = encoder->bit_rate > 0 ? encoder->bit_rate : (long)inRate * OutputChannels * bits;
        _fifo = ffmpeg.av_audio_fifo_alloc(format, OutputChannels, FrameSize * 4);
        if (_fifo == null)
            throw new InsufficientMemoryException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_CallFailed, "av_audio_fifo_alloc"));

        // The resampler sees the output layout on both sides: a relabelling, not a remix.
        fixed (AVChannelLayout* l = &_outLayout)
            OpenResampler(l, inFormat, inRate);
        fixed (AVChannelLayout* l = &_inLayout)
        {
            ffmpeg.av_channel_layout_uninit(l);
            AvUtil.Check(ffmpeg.av_channel_layout_copy(l, inLayout), "av_channel_layout_copy");
        }

        AppLog.Debug(string.Format(CultureInfo.CurrentCulture, Strings.Conversion_ConvertingLossless, _input.FormatName, InputLayout, inRate, sourceBits, pcm ? "PCM" : "ALAC", OutputLayout, bits));
    }

    /// <summary>Picks the encoder layout closest to the input (same layout, else most shared channels).</summary>
    private static void ChooseLayout(AVCodec* codec, AVChannelLayout* input, int maxChannels, AVChannelLayout* result)
    {
        var layouts = AvUtil.SupportedConfigs(codec, AVCodecConfig.AV_CODEC_CONFIG_CHANNEL_LAYOUT, (p, i) => (IntPtr)(((AVChannelLayout*)p) + i));
        if (layouts is null || layouts.Length == 0)
        {
            if (input->nb_channels <= maxChannels)
                ffmpeg.av_channel_layout_copy(result, input);
            else
                ffmpeg.av_channel_layout_default(result, maxChannels);
            return;
        }

        var inMask = input->order == AVChannelOrder.AV_CHANNEL_ORDER_NATIVE ? input->u.mask : 0UL;
        AVChannelLayout* best = null;
        var bestScore = long.MinValue;
        foreach (var p in layouts)
        {
            var candidate = (AVChannelLayout*)p;
            if (ffmpeg.av_channel_layout_compare(candidate, input) == 0)
            {
                best = candidate;
                break;
            }

            if (candidate->nb_channels > Math.Min(input->nb_channels, maxChannels))
                continue;
            var mask = candidate->order == AVChannelOrder.AV_CHANNEL_ORDER_NATIVE ? candidate->u.mask : 0UL;
            var score = candidate->nb_channels * 100L + (long)ulong.PopCount(mask & inMask);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (best != null)
            ffmpeg.av_channel_layout_copy(result, best);
        else
            ffmpeg.av_channel_layout_default(result, Math.Min(2, maxChannels));
    }

    private void OpenResampler(AVChannelLayout* inLayout, AVSampleFormat inFormat, int inRate)
    {
        if (_swr != null)
        {
            fixed (SwrContext** s = &_swr)
                ffmpeg.swr_free(s);
        }

        fixed (AVChannelLayout* inCopy = &_inLayout)
        {
            ffmpeg.av_channel_layout_uninit(inCopy);
            AvUtil.Check(ffmpeg.av_channel_layout_copy(inCopy, inLayout), "av_channel_layout_copy");
        }

        _inFormat = inFormat;
        _inRate = inRate;
        SwrContext* swr = null;
        fixed (AVChannelLayout* outLayout = &_outLayout)
        {
            AvUtil.Check(ffmpeg.swr_alloc_set_opts2(&swr, outLayout, _outFormat, OutputSampleRate, inLayout, inFormat, inRate, 0, null),
                "swr_alloc_set_opts2");
        }

        _swr = swr;
        if (_matrix != AVMatrixEncoding.AV_MATRIX_ENCODING_NONE)
            AvUtil.Check(ffmpeg.av_opt_set_int(swr, "matrix_encoding", (long)_matrix, 0), Strings.Op_SettingMatrixEncoding);
        if (inLayout->nb_channels > OutputChannels)
        {
            // Downmixes are normalised so the mix cannot clip.
            AvUtil.Check(ffmpeg.av_opt_set_double(swr, "rematrix_maxval", 1.0, 0), Strings.Op_SettingDownmixNormalisation);
        }

        AvUtil.Check(ffmpeg.swr_init(swr), Strings.Op_InitialisingResampler);
    }

    /// <summary>Resamples <paramref name="count"/> input samples (null = flush) into the FIFO.</summary>
    private void Resample(byte** input, int count)
    {
        var capacity = ffmpeg.swr_get_out_samples(_swr, count);
        if (capacity <= 0)
            return;
        if (_converted->nb_samples < capacity || _converted->data[0] == null)
        {
            ffmpeg.av_frame_unref(_converted);
            _converted->format = (int)_outFormat;
            _converted->sample_rate = OutputSampleRate;
            fixed (AVChannelLayout* l = &_outLayout)
                AvUtil.Check(ffmpeg.av_channel_layout_copy(&_converted->ch_layout, l), "av_channel_layout_copy");
            _converted->nb_samples = Math.Max(capacity, FrameSize * 2);
            AvUtil.Check(ffmpeg.av_frame_get_buffer(_converted, 0), "av_frame_get_buffer");
        }

        var produced = AvUtil.Check(ffmpeg.swr_convert(_swr, _converted->extended_data, _converted->nb_samples, input, count), Strings.Op_Resampling);
        if (produced > 0 && ffmpeg.av_audio_fifo_write(_fifo, (void**)_converted->extended_data, produced) < produced)
            throw new InsufficientMemoryException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_CallFailed, "av_audio_fifo_write"));
    }

    private void EncodeFromFifo(bool final)
    {
        while (true)
        {
            var available = ffmpeg.av_audio_fifo_size(_fifo);
            if (available == 0 || (available < FrameSize && !final))
                return;
            var count = Math.Min(FrameSize, available);

            var frame = _encodeFrame;
            if (frame->data[0] == null)
            {
                frame->format = (int)_outFormat;
                frame->sample_rate = OutputSampleRate;
                fixed (AVChannelLayout* l = &_outLayout)
                    AvUtil.Check(ffmpeg.av_channel_layout_copy(&frame->ch_layout, l), "av_channel_layout_copy");
                frame->nb_samples = FrameSize;
                AvUtil.Check(ffmpeg.av_frame_get_buffer(frame, 0), "av_frame_get_buffer");
            }

            frame->nb_samples = FrameSize;
            AvUtil.Check(ffmpeg.av_frame_make_writable(frame), "av_frame_make_writable");
            if (ffmpeg.av_audio_fifo_read(_fifo, (void**)frame->extended_data, count) < count)
                throw new InvalidOperationException(Strings.FFmpeg_FifoShortRead);
            if (count < FrameSize)
            {
                if (_smallLastFrame)
                    frame->nb_samples = count;
                else
                    ffmpeg.av_samples_set_silence(frame->extended_data, count, FrameSize - count, OutputChannels, _outFormat);
            }

            frame->pts = _nextPts;
            _nextPts += count;
            AvUtil.Check(ffmpeg.avcodec_send_frame(_encoder, frame), Strings.Op_Encoding);
            ReceivePackets();
        }
    }

    private void ReceivePackets()
    {
        while (true)
        {
            var ret = ffmpeg.avcodec_receive_packet(_encoder, _packet);
            if (ret == AvUtil.EAgain || ret == ffmpeg.AVERROR_EOF)
                return;
            AvUtil.Check(ret, Strings.Op_Encoding);
            try
            {
                var pts = _packet->pts == ffmpeg.AV_NOPTS_VALUE ? 0 : _packet->pts + InitialPadding;
                var duration = _packet->duration > 0 ? _packet->duration : FrameSize;

                // The decoded audio ends at _nextPts (+ the delay): trim the padding of the last frame.
                if (_finished)
                    duration = Math.Max(1, Math.Min(duration, _nextPts + InitialPadding - pts));
                _packets.Enqueue(new EncodedPacket(AvUtil.PacketData(_packet), pts, duration));
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
            }
        }
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
        fixed (AVCodecContext** d = &_decoder)
            ffmpeg.avcodec_free_context(d);
        fixed (AVCodecContext** e = &_encoder)
            ffmpeg.avcodec_free_context(e);
        fixed (SwrContext** s = &_swr)
            ffmpeg.swr_free(s);
        if (_fifo != null)
        {
            ffmpeg.av_audio_fifo_free(_fifo);
            _fifo = null;
        }

        fixed (AVFrame** f = &_decoded)
            ffmpeg.av_frame_free(f);
        fixed (AVFrame** f = &_converted)
            ffmpeg.av_frame_free(f);
        fixed (AVFrame** f = &_encodeFrame)
            ffmpeg.av_frame_free(f);
        fixed (AVPacket** p = &_packet)
            ffmpeg.av_packet_free(p);
        fixed (AVChannelLayout* l = &_inLayout)
            ffmpeg.av_channel_layout_uninit(l);
        fixed (AVChannelLayout* l = &_outLayout)
            ffmpeg.av_channel_layout_uninit(l);
    }
}
