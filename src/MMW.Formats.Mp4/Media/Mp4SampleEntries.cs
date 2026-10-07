using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Formats.Mp4.Boxes;

namespace MMW.Formats.Mp4.Media;

/// <summary>An MP4 track description kept verbatim for MP4 → MP4 passthrough.</summary>
/// <param name="Entry">The sample entry (stsd child).</param>
/// <param name="Handler">The hdlr handler type.</param>
/// <param name="MediaHeader">The media information header (vmhd/smhd/nmhd/gmhd/sthd …).</param>
internal sealed record Mp4NativeTrack(Box Entry, string Handler, Box? MediaHeader);

/// <summary>Statistics of a written track, used for esds/btrt/ddts bit-rate fields.</summary>
internal readonly record struct TrackStatistics(uint BufferSize, uint MaxBitrate, uint AvgBitrate);

/// <summary>Translates between MP4 sample entries and <see cref="CodecConfig"/>.</summary>
internal static class Mp4SampleEntries
{
    // ------------------------------------------------------------------ reading

    /// <summary>Describes a track of an MP4 file.</summary>
    public static CodecConfig Describe(Box trak, Box entry, string handler, uint timescale)
    {
        var tkhd = trak.Find("tkhd");
        var mdhd = trak.FindPath("mdia/mdhd");
        var kind = handler switch
        {
            "vide" => TrackKind.Video,
            "soun" => TrackKind.Audio,
            "sbtl" or "subt" or "text" or "subp" => TrackKind.Subtitle,
            "clcp" => TrackKind.ClosedCaption,
            _ => entry.Type == "mp4s" ? TrackKind.Subtitle : TrackKind.Other,
        };

        var language = mdhd is null ? "und" : Core.Languages.LanguageTable.ToBcp47(HeaderBoxes.UnpackLanguage(HeaderBoxes.MdhdLanguage(mdhd)));
        if (trak.FindPath("mdia/elng") is { Payload.Length: > 4 } elng)
        {
            var tag = Encoding.UTF8.GetString(elng.Payload, 4, elng.Payload.Length - 4).TrimEnd('\0');
            if (tag.Length > 0)
                language = tag;
        }

        var name = trak.FindPath("udta/name") is { } n ? Encoding.UTF8.GetString(n.Payload).TrimEnd('\0') : string.Empty;
        var config = new CodecConfig
        {
            Kind = kind,
            SourceCodecId = entry.Type,
            Timescale = timescale,
            Language = language,
            Name = name,
            Native = new Mp4NativeTrack(entry, handler, trak.FindPath("mdia/minf")?.Children?.FirstOrDefault(c => c.Type is "vmhd" or "smhd" or "nmhd" or "gmhd" or "sthd" or "hmhd")),
        };

        return kind switch
        {
            TrackKind.Video => DescribeVideo(config, entry, tkhd),
            TrackKind.Audio => DescribeAudio(config, entry),
            _ => DescribeOther(config, entry, tkhd),
        };
    }

    private static CodecConfig DescribeVideo(CodecConfig config, Box entry, Box? tkhd)
    {
        var video = new VideoTrack();
        CodecInfo.DescribeVideo(entry, video);
        var dv = entry.Find("dvcC") ?? entry.Find("dvvC") ?? entry.Find("dvwC");
        var (codec, extradata) = entry.Type switch
        {
            "avc1" or "avc2" or "avc3" or "avc4" or "dva1" or "dvav" => (CodecType.H264, entry.Find("avcC")?.Payload),
            "hvc1" or "hev1" or "dvh1" or "dvhe" => (CodecType.Hevc, entry.Find("hvcC")?.Payload),
            "vvc1" or "vvi1" => (CodecType.Vvc, entry.Find("vvcC") is { Payload.Length: > 4 } vvcC ? vvcC.Payload[4..] : null), // vvcC is a FullBox
            "evc1" => (CodecType.Evc, entry.Find("evcC")?.Payload),
            "av01" or "dav1" => (CodecType.Av1, entry.Find("av1C")?.Payload),
            "vp09" => (CodecType.Vp9, entry.Find("vpcC")?.Payload),
            "vp08" => (CodecType.Vp8, entry.Find("vpcC")?.Payload),
            "jpeg" or "mjpa" => (CodecType.Mjpeg, null),
            "apcn" or "apch" or "apcs" or "apco" or "ap4h" or "ap4x" => (CodecType.ProRes, null),
            "mp4v" => VisualFromEsds(entry),
            "avst" => (CodecType.Avs2, null),
            "avs3" => (CodecType.Avs3, entry.Find("av3c")?.Payload),
            _ => (CodecType.Unknown, null),
        };

        double displayWidth = 0, displayHeight = 0;
        if (tkhd is not null)
            (displayWidth, displayHeight) = HeaderBoxes.TkhdSize(tkhd);

        return config with
        {
            Codec = codec,
            Extradata = extradata,
            Width = video.PixelWidth,
            Height = video.PixelHeight,
            ParNumerator = video.ParNumerator > 0 ? video.ParNumerator : 1,
            ParDenominator = video.ParDenominator > 0 ? video.ParDenominator : 1,
            Color = video.Color,
            Hdr = video.Hdr,
            DolbyVisionConfig = dv?.Payload,
            SubtitleWidth = (int)displayWidth,
            SubtitleHeight = (int)displayHeight,
        };
    }

    private static (CodecType, byte[]?) VisualFromEsds(Box entry)
    {
        if (FindDescendant(entry, "esds") is not { } esds)
            return (CodecType.Mpeg4Visual, null);
        var (oti, dsi) = CodecInfo.ParseEsds(esds.Payload);
        return oti switch
        {
            0x20 => (CodecType.Mpeg4Visual, dsi),
            >= 0x60 and <= 0x65 => (CodecType.Mpeg2Video, dsi),
            0x6A => (CodecType.Mpeg1Video, dsi),
            0x6C => (CodecType.Mjpeg, dsi),
            _ => (CodecType.Unknown, dsi),
        };
    }

    private static CodecConfig DescribeAudio(CodecConfig config, Box entry)
    {
        var audio = new AudioTrack();
        CodecInfo.DescribeAudio(entry, audio);
        var p = entry.Payload;
        var bits = p.Length >= 28 ? BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(18)) : 16;
        config = config with { Channels = audio.Channels, SampleRate = audio.SampleRate > 0 ? audio.SampleRate : (int)config.Timescale, BitsPerSample = bits };

        switch (entry.Type)
        {
            case "mp4a":
            {
                var esds = FindDescendant(entry, "esds");
                if (esds is null)
                    return config with { Codec = CodecType.Aac };
                var (oti, dsi) = CodecInfo.ParseEsds(esds.Payload);
                switch (oti)
                {
                    case 0x40 or 0x66 or 0x67 or 0x68:
                    {
                        var codecConfig = config with { Codec = CodecType.Aac, Extradata = dsi, BitsPerSample = 0 };
                        if (dsi is { Length: >= 2 })
                        {
                            var asc = Aac.ParseConfig(dsi);
                            var rate = asc.ExtensionSampleRate > 0 ? asc.ExtensionSampleRate : codecConfig.SampleRate > 0 ? codecConfig.SampleRate : asc.SampleRate;
                            codecConfig = codecConfig with { Channels = asc.Channels > 0 ? asc.Channels : codecConfig.Channels, SampleRate = rate };
                        }

                        return codecConfig;
                    }

                    case 0x69 or 0x6B:
                        return config with { Codec = CodecType.Mp3, BitsPerSample = 0 };
                    case 0xA5:
                        return config with { Codec = CodecType.Ac3, BitsPerSample = 0 };
                    case 0xA6:
                        return config with { Codec = CodecType.Eac3, BitsPerSample = 0 };
                    case 0xA9:
                        return config with { Codec = CodecType.Dts, BitsPerSample = 0 };
                    case 0xAD:
                        return config with { Codec = CodecType.Opus, BitsPerSample = 0 };
                    case 0xDD:
                        return config with { Codec = CodecType.Vorbis, Extradata = dsi, BitsPerSample = 0 };
                    default:
                        return config with { Codec = CodecType.Unknown };
                }
            }

            case ".mp3":
                return config with { Codec = CodecType.Mp3, BitsPerSample = 0 };
            case "ac-3":
                return config with { Codec = CodecType.Ac3, Extradata = entry.Find("dac3")?.Payload, BitsPerSample = 0 };
            case "ec-3":
                return config with { Codec = CodecType.Eac3, Extradata = entry.Find("dec3")?.Payload, IsAtmos = audio.IsAtmos, BitsPerSample = 0 };
            case "dtsc" or "dtsh" or "dtsl" or "dtse" or "dtsx":
                return config with { Codec = CodecType.Dts, Extradata = entry.Find("ddts")?.Payload, BitsPerSample = 0 };
            case "Opus":
            {
                var dops = entry.Find("dOps")?.Payload;
                var head = dops is not null ? Opus.DopsToOpusHead(dops) : Opus.DefaultHead(config.Channels, 312, 48000);
                var (_, preSkip, _) = Opus.Describe(head);
                return config with
                {
                    Codec = CodecType.Opus,
                    Extradata = head,
                    SampleRate = 48000,
                    BitsPerSample = 0,
                    CodecDelay = TimeSpan.FromSeconds(preSkip / 48000.0),
                    SeekPreRoll = TimeSpan.FromMilliseconds(80),
                };
            }

            case "fLaC":
            {
                var dfla = entry.Find("dfLa")?.Payload;
                var blocks = dfla is { Length: > 4 } ? dfla[4..] : null;
                var (rate, ch, b) = blocks is null ? (0, 0, 0) : Flac.Describe(blocks);
                return config with
                {
                    Codec = CodecType.Flac,
                    Extradata = blocks,
                    SampleRate = rate > 0 ? rate : config.SampleRate,
                    Channels = ch > 0 ? ch : config.Channels,
                    BitsPerSample = b > 0 ? b : config.BitsPerSample,
                };
            }

            case "alac":
            {
                // ISO: a direct 'alac' child; QuickTime v1: inside the 'wave' atom.
                var cookie = FindDescendant(entry, "alac")?.Payload;
                var specific = cookie is { Length: >= 28 } ? cookie[4..] : null;
                if (specific is { Length: >= 24 })
                {
                    return config with
                    {
                        Codec = CodecType.Alac,
                        Extradata = specific,
                        BitsPerSample = specific[5],
                        Channels = specific[9],
                        SampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(specific.AsSpan(20)),
                    };
                }

                return config with { Codec = CodecType.Alac };
            }

            case "mlpa":
            {
                // MLPSampleEntry stores the sampling rate as a 32-bit integer, not 16.16 (Dolby, §2.4).
                var mlpRate = entry.Payload.Length >= 28 ? (int)BinaryPrimitives.ReadUInt32BigEndian(entry.Payload.AsSpan(24)) : 0;
                var dmlp = entry.Find("dmlp")?.Payload;
                return config with
                {
                    Codec = CodecType.TrueHd,
                    BitsPerSample = 0,
                    SampleRate = mlpRate > 0 ? mlpRate : config.SampleRate,
                    Extradata = dmlp is { Length: >= 10 } ? dmlp : null,
                };
            }
            case "sowt":
                return config with { Codec = CodecType.Pcm, PcmBigEndian = false };
            case "twos":
                return config with { Codec = CodecType.Pcm, PcmBigEndian = bits > 8 };
            case "raw ":
                return config with { Codec = CodecType.Pcm, BitsPerSample = 8 };
            case "in24":
                return config with { Codec = CodecType.Pcm, BitsPerSample = 24, PcmBigEndian = !HasLittleEndianAtom(entry) };
            case "in32":
                return config with { Codec = CodecType.Pcm, BitsPerSample = 32, PcmBigEndian = !HasLittleEndianAtom(entry) };
            case "fl32" or "fl64":
                return config with { Codec = CodecType.Pcm, BitsPerSample = entry.Type == "fl32" ? 32 : 64, PcmFloat = true, PcmBigEndian = !HasLittleEndianAtom(entry) };
            case "lpcm":
                return DescribeLpcm(config, entry);
            case "ipcm" or "fpcm":
            {
                var pcmC = entry.Find("pcmC")?.Payload;
                var little = pcmC is { Length: >= 6 } && (pcmC[4] & 1) != 0;
                var size = pcmC is { Length: >= 6 } ? pcmC[5] : bits;
                return config with { Codec = CodecType.Pcm, BitsPerSample = size, PcmBigEndian = !little, PcmFloat = entry.Type == "fpcm" };
            }

            default:
                return config with { Codec = CodecType.Unknown };
        }
    }

    private static bool HasLittleEndianAtom(Box entry) =>
        FindDescendant(WithChildren(entry), "enda") is { Payload.Length: >= 2 } enda && BinaryPrimitives.ReadUInt16BigEndian(enda.Payload) != 0;

    /// <summary>
    /// QuickTime PCM entries ('in24', 'in32', 'fl32', …) are not known to the box parser, which keeps them as leaves:
    /// parse their children (after the version-dependent sound description fields) here.
    /// </summary>
    private static Box WithChildren(Box entry)
    {
        if (entry.Children is not null || entry.Payload.Length < 28)
            return entry;
        var prefix = BinaryPrimitives.ReadUInt16BigEndian(entry.Payload.AsSpan(8)) switch
        {
            1 => 44,
            2 => 64,
            _ => 28,
        };
        if (prefix >= entry.Payload.Length)
            return entry;
        try
        {
            return new Box(entry.Type, entry.Payload[..prefix], BoxParser.ParseList(entry.Payload.AsSpan(prefix), entry.Type));
        }
        catch (InvalidDataException)
        {
            return entry;
        }
    }

    private static CodecConfig DescribeLpcm(CodecConfig config, Box entry)
    {
        var p = entry.Payload;
        if (p.Length < 64 || BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(8)) != 2)
            return config with { Codec = CodecType.Pcm };

        // QuickTime sound description v2: flags at 52, bits per channel at 48.
        var bits = (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(48));
        var flags = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(52));
        return config with { Codec = CodecType.Pcm, BitsPerSample = bits, PcmFloat = (flags & 1) != 0, PcmBigEndian = (flags & 2) != 0 };
    }

    private static CodecConfig DescribeOther(CodecConfig config, Box entry, Box? tkhd)
    {
        int width = 0, height = 0;
        if (tkhd is not null)
        {
            var (w, h) = HeaderBoxes.TkhdSize(tkhd);
            (width, height) = ((int)w, (int)h);
        }

        config = config with { SubtitleWidth = width, SubtitleHeight = height };
        switch (entry.Type)
        {
            case "tx3g" or "text":
            {
                var payload = new MemoryStream();
                if (entry.Payload.Length > 8)
                    payload.Write(entry.Payload.AsSpan(8));
                foreach (var child in entry.Children ?? [])
                    payload.Write(BoxWriter.ToArray(child));
                return config with { Codec = CodecType.Tx3g, Extradata = payload.ToArray(), Kind = TrackKind.Subtitle };
            }

            case "wvtt":
                return config with
                {
                    Codec = CodecType.WebVtt,
                    Kind = TrackKind.Subtitle,
                    Extradata = entry.Find("vttC") is { } vttC ? vttC.Payload : Encoding.UTF8.GetBytes("WEBVTT"),
                };
            case "mp4s" or "subp":
            {
                var esds = FindDescendant(entry, "esds");
                var dsi = esds is null ? null : CodecInfo.ParseEsds(esds.Payload).DecoderSpecificInfo;
                return config with { Codec = CodecType.VobSub, Kind = TrackKind.Subtitle, Extradata = Encoding.UTF8.GetBytes(VobSubIdx(dsi, width, height)) };
            }

            case "c608":
                return config with { Codec = CodecType.Cea608 };
            case "stpp":
                return config with { Codec = CodecType.Ttml, Kind = TrackKind.Subtitle };
            default:
                return config with { Codec = CodecType.Unknown };
        }
    }

    /// <summary>Builds a VobSub .idx header from an MP4 palette (16 × [0, Y, Cr, Cb]).</summary>
    private static string VobSubIdx(byte[]? palette, int width, int height)
    {
        var sb = new StringBuilder();
        if (width > 0 && height > 0)
            sb.Append(CultureInfo.InvariantCulture, $"size: {width}x{height}\n");
        if (palette is { Length: >= 64 })
        {
            sb.Append("palette: ");
            for (var i = 0; i < 16; i++)
            {
                var (r, g, b) = YuvToRgb(palette[i * 4 + 1], palette[i * 4 + 2], palette[i * 4 + 3]);
                sb.Append(CultureInfo.InvariantCulture, $"{r:x2}{g:x2}{b:x2}");
                if (i < 15)
                    sb.Append(", ");
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static (int R, int G, int B) YuvToRgb(int y, int cr, int cb)
    {
        var r = y + 1.402 * (cr - 128);
        var g = y - 0.344136 * (cb - 128) - 0.714136 * (cr - 128);
        var b = y + 1.772 * (cb - 128);
        return (Clamp(r), Clamp(g), Clamp(b));

        static int Clamp(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);
    }

    private static (int Y, int Cr, int Cb) RgbToYuv(int r, int g, int b)
    {
        var y = 0.299 * r + 0.587 * g + 0.114 * b;
        var cr = 0.5 * r - 0.418688 * g - 0.081312 * b + 128;
        var cb = -0.168736 * r - 0.331264 * g + 0.5 * b + 128;
        return (Clamp(y), Clamp(cr), Clamp(cb));

        static int Clamp(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);
    }

    internal static Box? FindDescendant(Box box, string type)
    {
        foreach (var c in box.Children ?? [])
        {
            if (c.Type == type)
                return c;
            if (FindDescendant(c, type) is { } found)
                return found;
        }

        return null;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Whether an MP4 file can store a track described by <paramref name="config"/>.</summary>
    public static TrackSupport CheckSupport(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var support = CheckCodecSupport(config);
        // HDR10+ kept next to the frames (Matroska BlockAdditions, VP9) has no place in MP4: the video is stored
        // with its static HDR10 metadata only.
        return config.Hdr10PlusInBlockAdditions && support.Level == TrackSupportLevel.Passthrough
            ? support with { Reason = "its HDR10+ dynamic metadata is stored next to the frames (Matroska block additions) and cannot be kept in MP4; it will play as HDR10. Save as Matroska/WebM to keep it" }
            : support;
    }

    private static TrackSupport CheckCodecSupport(CodecConfig config)
    {
        if (config.Native is Mp4NativeTrack && config.Codec != CodecType.Pcm)
            return TrackSupport.Passthrough;
        return config.Codec switch
        {
            CodecType.H264 or CodecType.Hevc or CodecType.Vvc or CodecType.Evc or CodecType.Av1 or CodecType.Vp9 or CodecType.Vp8 or CodecType.ProRes or
                CodecType.Mpeg4Visual or CodecType.Mpeg2Video or CodecType.Mpeg1Video or CodecType.Mjpeg or CodecType.Avs2 or CodecType.Avs3 =>
                config.Extradata is null && config.Codec is CodecType.H264 or CodecType.Hevc or CodecType.Vvc or CodecType.Evc or CodecType.Av1
                    ? new TrackSupport(TrackSupportLevel.Unsupported, ImportAction.Skip, "the codec configuration is missing")
                    : TrackSupport.Passthrough,
            CodecType.Aac or CodecType.Ac3 or CodecType.Eac3 or CodecType.Dts or CodecType.Opus or CodecType.Flac or CodecType.Alac or
                CodecType.Mp3 or CodecType.Mp2 or CodecType.Mp1 => TrackSupport.Passthrough,
            CodecType.Tx3g or CodecType.VobSub => TrackSupport.Passthrough,
            CodecType.TextUtf8 or CodecType.Ass or CodecType.Ssa or CodecType.WebVtt =>
                new TrackSupport(TrackSupportLevel.Converted, ImportAction.ConvertToTx3g, "converted to 3GPP timed text (tx3g)"),
            // Dolby TrueHD (FBA syntax) is stored as mlpa + dmlp; DVD-Audio MLP (FBB) is not allowed in ISO files.
            CodecType.TrueHd => TrackSupport.Passthrough,
            CodecType.Pcm when PcmWritable(config) => TrackSupport.Passthrough,
            CodecType.Pcm => new TrackSupport(TrackSupportLevel.NeedsConversion, ImportAction.ConvertToPcm,
                $"{config.BitsPerSample}-bit PCM cannot be stored in MP4 ('ipcm' holds 16, 24 and 32-bit integers and 32/64-bit floats); convert it to LPCM"),
            CodecType.Vorbis or CodecType.Mlp or CodecType.Pcm =>
                new TrackSupport(TrackSupportLevel.NeedsConversion, ImportAction.ConvertToAac, $"{config.FormatName} audio is not supported in MP4 by most players; convert it to AAC or AC-3"),
            CodecType.Avs1 => new TrackSupport(TrackSupportLevel.Unsupported, ImportAction.Skip,
                "AVS (AVS1-P2 / AVS+) video has no MP4 sample entry (none is registered), so it cannot be stored in MP4; save as Matroska instead"),
            CodecType.Pgs or CodecType.DvbSub =>
                new TrackSupport(TrackSupportLevel.NeedsConversion, ImportAction.Skip, $"{config.FormatName} bitmap subtitles cannot be stored in MP4; they need OCR to text"),
            _ => new TrackSupport(TrackSupportLevel.Unsupported, ImportAction.Skip, $"{config.FormatName} cannot be stored in MP4"),
        };
    }

    /// <summary>PCM that an ISO/IEC 23003-5 'ipcm' / 'fpcm' entry describes: 16/24/32-bit integers or 32/64-bit floats.</summary>
    public static bool PcmWritable(CodecConfig config) =>
        config.PcmFloat ? config.BitsPerSample is 32 or 64 : config.BitsPerSample is 16 or 24 or 32;

    /// <summary>Bytes of one PCM frame (all channels), or 0 when unknown.</summary>
    public static int PcmFrameBytes(CodecConfig config) =>
        config.Codec == CodecType.Pcm && config.BitsPerSample % 8 == 0 ? config.Channels * config.BitsPerSample / 8 : 0;

    /// <summary>
    /// The 16.16 SampleRate field for rates it cannot hold (above 65535 Hz): the rate halved until it fits, as FFmpeg
    /// writes it (192 kHz → 48 kHz). Readers take the real rate from the decoder configuration or the 'srat' box.
    /// </summary>
    private static uint SampleRateField(int rate)
    {
        if (rate <= 0)
            return 0;
        while (rate > ushort.MaxValue)
            rate /= 2;
        return (uint)rate << 16;
    }

    /// <summary>
    /// ISO/IEC 23091-3 speaker positions of PCM channels in the WAVE / FLAC default order for their count (back
    /// surrounds as Lsr/Rsr, side as Ls/Rs, as FFmpeg writes them); null when there is no default.
    /// </summary>
    private static byte[]? DefaultSpeakerPositions(int channels) => channels switch
    {
        3 => [0, 1, 2],
        4 => [0, 1, 8, 9],
        5 => [0, 1, 2, 8, 9],
        6 => [0, 1, 2, 3, 8, 9],
        7 => [0, 1, 2, 3, 10, 4, 5],
        8 => [0, 1, 2, 3, 8, 9, 4, 5],
        _ => null,
    };

    /// <summary>The 'chnl' box (ISO/IEC 14496-12 ChannelLayout v0): CICP mono / stereo, else explicit positions.</summary>
    private static Box? BuildChnl(int channels)
    {
        var b = new PayloadBuilder().FullBox(0, 0).U8(1); // stream_structure: channels
        if (channels is 1 or 2)
            return new Box("chnl", b.U8((byte)channels).U32(0).U32(0).ToArray()); // defined layout, no omitted channels
        if (DefaultSpeakerPositions(channels) is not { } positions)
            return null;
        b.U8(0);
        foreach (var position in positions)
            b.U8(position);
        return new Box("chnl", b.ToArray());
    }

    /// <summary>Handler type for a new track.</summary>
    public static string HandlerFor(CodecConfig config) => config.Native is Mp4NativeTrack native ? native.Handler : config.Kind switch
    {
        TrackKind.Video => "vide",
        TrackKind.Audio => "soun",
        TrackKind.Subtitle => config.Codec == CodecType.VobSub ? "subp" : "sbtl",
        TrackKind.ClosedCaption => "clcp",
        _ => "meta",
    };

    /// <summary>Context for building a sample entry.</summary>
    public sealed record EntryContext
    {
        /// <summary>First sample of the track (used to derive missing AC-3/E-AC-3/DTS configuration).</summary>
        public byte[]? FirstSample { get; init; }

        public TrackStatistics Statistics { get; init; }

        /// <summary>Samples carry HEVC parameter sets in-band (hev1 instead of hvc1).</summary>
        public bool InBandParameterSets { get; init; }

        /// <summary>Text track box size (video size).</summary>
        public int TextWidth { get; init; }

        public int TextHeight { get; init; }

        public uint Tx3gDisplayFlags { get; init; }

        public ushort EsId { get; init; }
    }

    /// <summary>Builds the sample entry for <paramref name="config"/>.</summary>
    /// <exception cref="NotSupportedException">The codec cannot be stored in MP4.</exception>
    public static Box Build(CodecConfig config, EntryContext ctx)
    {
        if (config.Native is Mp4NativeTrack native)
        {
            var entry = BoxParser.ParseList(BoxWriter.ToArray(native.Entry), "stsd")[0];
            // A 'vvc1' source whose samples repeat parameter sets is stored as the 'vvi1' it should have been.
            if (entry.Type == "vvc1" && ctx.InBandParameterSets && entry.Find("vvcC") is { Payload.Length: > 4 } vvcC)
            {
                entry.Type = "vvi1";
                vvcC.Payload = [.. vvcC.Payload.AsSpan(0, 4), .. Vvc.MarkArraysComplete(vvcC.Payload[4..], false)];
            }

            return entry;
        }

        return config.Kind switch
        {
            TrackKind.Video => BuildVideo(config, ctx),
            TrackKind.Audio => BuildAudio(config, ctx),
            TrackKind.Subtitle => BuildSubtitle(config, ctx),
            _ => throw new NotSupportedException($"{config.FormatName} tracks cannot be stored in MP4."),
        };
    }

    private static Box BuildVideo(CodecConfig c, EntryContext ctx)
    {
        var children = new List<Box>();
        string type; // Dolby Vision variants (dvh1, dav1…) are chosen by DolbyVisionEntry.Apply below
        switch (c.Codec)
        {
            case CodecType.H264:
                type = "avc1";
                children.Add(new Box("avcC", c.Extradata!));
                break;
            case CodecType.Hevc:
            {
                var inBand = ctx.InBandParameterSets && !Mp4RemuxOptions.ForceHvc1;
                type = inBand ? "hev1" : "hvc1";
                children.Add(new Box("hvcC", Hevc.MarkArraysComplete(c.Extradata!, !inBand)));
                break;
            }

            case CodecType.Vvc:
                // 'vvi1' when the samples repeat parameter sets (ISO/IEC 14496-15 §11.2.1).
                type = ctx.InBandParameterSets ? "vvi1" : "vvc1";
                children.Add(new Box("vvcC", [0, 0, 0, 0, .. Vvc.MarkArraysComplete(c.Extradata!, !ctx.InBandParameterSets)]));
                break;
            case CodecType.Evc:
                type = "evc1";
                children.Add(new Box("evcC", c.Extradata!));
                break;
            case CodecType.Av1:
                type = "av01";
                children.Add(new Box("av1C", c.Extradata!));
                break;
            case CodecType.Vp9:
            case CodecType.Vp8:
                type = c.Codec == CodecType.Vp9 ? "vp09" : "vp08";
                children.Add(new Box("vpcC", c.Extradata is { Length: >= 12 } vpcc ? vpcc : BuildVpcC(c)));
                break;
            case CodecType.ProRes:
            {
                // Matroska stores the ProRes four-cc (apcn, apch, …) as CodecPrivate.
                var fourcc = c.Extradata is { Length: 4 } priv ? System.Text.Encoding.ASCII.GetString(priv) : c.SourceCodecId;
                type = fourcc is "apco" or "apcs" or "apcn" or "apch" or "ap4h" or "ap4x" ? fourcc : "apcn";
                break;
            }

            case CodecType.Avs2:
                // MP4RA 'avst'. No configuration box is published for it: the sequence header stays in the samples.
                type = "avst";
                break;

            case CodecType.Avs3:
            {
                // MP4RA 'avs3' with its configuration record (as GPAC writes it); the sequence header also stays in the samples.
                type = "avs3";
                var av3c = c.Extradata is { } x && !Avs.SequenceHeaderOfAv3C(x).IsEmpty ? x
                    : ctx.FirstSample is { } first && Avs.FindSequenceHeader(first) is { IsEmpty: false } header ? Avs.BuildAv3C(header)
                    : null;
                if (av3c is not null)
                    children.Add(new Box("av3c", av3c));
                break;
            }

            case CodecType.Mpeg4Visual or CodecType.Mpeg2Video or CodecType.Mpeg1Video or CodecType.Mjpeg:
            {
                type = "mp4v";
                var oti = c.Codec switch
                {
                    CodecType.Mpeg4Visual => 0x20,
                    CodecType.Mpeg2Video => 0x61,
                    CodecType.Mpeg1Video => 0x6A,
                    _ => 0x6C,
                };
                children.Add(BuildEsds(oti, 0x04, c.Extradata, ctx.Statistics, ctx.EsId));
                break;
            }

            default:
                throw new NotSupportedException($"{c.FormatName} video cannot be stored in MP4.");
        }

        // The container's colour and HDR10 metadata, or what the bitstream carries when the source container had none.
        if (c.EffectiveColor is { IsSpecified: true } color)
            children.Add(BuildColr(color));

        if (c.EffectiveHdr is { } hdr)
        {
            if (BuildMdcv(hdr) is { } mdcv)
                children.Add(mdcv);
            if (BuildClli(hdr) is { } clli)
                children.Add(clli);
            if (BuildAmve(hdr) is { } amve)
                children.Add(amve);
        }

        if (c.ParNumerator > 0 && c.ParDenominator > 0 && c.ParNumerator != c.ParDenominator)
            children.Add(new Box("pasp", new PayloadBuilder().U32((uint)c.ParNumerator).U32((uint)c.ParDenominator).ToArray()));
        if (ctx.Statistics.AvgBitrate > 0)
            children.Add(new Box("btrt", new PayloadBuilder().U32(ctx.Statistics.BufferSize).U32(ctx.Statistics.MaxBitrate).U32(ctx.Statistics.AvgBitrate).ToArray()));

        var compressor = new byte[32];
        var payload = new PayloadBuilder()
            .Zeros(6).U16(1) // reserved, data_reference_index
            .U16(0).U16(0).Zeros(12)
            .U16(Math.Clamp(c.Width, 0, ushort.MaxValue)).U16(Math.Clamp(c.Height, 0, ushort.MaxValue))
            .U32(0x00480000).U32(0x00480000).U32(0).U16(1)
            .Bytes(compressor).U16(0x18).U16(0xFFFF)
            .ToArray();
        var entry = new Box(type, payload, children);
        DolbyVisionEntry.Apply(entry, c.DolbyVisionConfig);
        return entry;

    }

    /// <summary>
    /// 'mdcv' from the model's R, G, B primaries: stored in G, B, R order like the HEVC SEI it mirrors (ISO/IEC 23001-8;
    /// FFmpeg and Apple write it so); null without primaries, white point and maximum luminance.
    /// </summary>
    public static Box? BuildMdcv(HdrInfo hdr)
    {
        ArgumentNullException.ThrowIfNull(hdr);
        if (hdr.DisplayPrimaries is not { Length: 3 } p || hdr.WhitePoint is not { } w || hdr.MaxLuminance is not { } max)
            return null;
        var b = new PayloadBuilder();
        foreach (var (x, y) in new[] { p[1], p[2], p[0] })
            b.U16(Chroma(x)).U16(Chroma(y));
        b.U16(Chroma(w.X)).U16(Chroma(w.Y)).U32((uint)Math.Round(max * 10000)).U32((uint)Math.Round((hdr.MinLuminance ?? 0) * 10000));
        return new Box("mdcv", b.ToArray());
    }

    /// <summary>'colr' of type 'nclx'.</summary>
    public static Box BuildColr(ColorInfo color) =>
        new("colr", new PayloadBuilder().Type("nclx").U16(color.Primaries).U16(color.Transfer).U16(color.Matrix)
            .U8(color.FullRange == true ? 0x80 : 0).ToArray());

    /// <summary>
    /// Adds to a copied sample entry the colour and HDR10 boxes it lacks, from what the bitstream carries
    /// (<see cref="CodecConfig.StreamColor"/>, <see cref="CodecConfig.StreamHdr"/>).
    /// </summary>
    public static void AddMissingColorBoxes(Box entry, CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(config);
        if (entry.Children is not { } children)
            return;
        var at = children.FindIndex(c => c.Type is "pasp" or "btrt");
        void Insert(Box box)
        {
            if (at < 0)
            {
                children.Add(box);
            }
            else
            {
                children.Insert(at, box);
                at++;
            }
        }

        if (config.StreamColor.IsSpecified && !children.Any(c => c.Type == "colr"))
            Insert(BuildColr(config.StreamColor));
        if (config.StreamHdr is { } hdr)
        {
            if (!children.Any(c => c.Type is "mdcv" or "SmDm") && BuildMdcv(hdr) is { } mdcv)
                Insert(mdcv);
            if (!children.Any(c => c.Type is "clli" or "CoLL") && BuildClli(hdr) is { } clli)
                Insert(clli);
            if (!children.Any(c => c.Type == "amve") && BuildAmve(hdr) is { } amve)
                Insert(amve);
        }
    }

    private static int Chroma(double v) => (int)Math.Clamp(Math.Round(v / 0.00002), 0, ushort.MaxValue);

    /// <summary>
    /// 'amve' (ambient viewing environment: illuminance in 0.0001 lux, light chromaticity in 0.00002 units, as the
    /// SEI message); null when unknown. A missing chromaticity defaults to D65.
    /// </summary>
    public static Box? BuildAmve(HdrInfo hdr)
    {
        ArgumentNullException.ThrowIfNull(hdr);
        if (hdr.AmbientIlluminance is not { } lux || lux <= 0)
            return null;
        var (x, y) = hdr.AmbientLight ?? (0.3127, 0.329);
        return new Box("amve", new PayloadBuilder().U32((uint)Math.Round(lux * 10000)).U16(Chroma(x)).U16(Chroma(y)).ToArray());
    }

    /// <summary>'clli' (MaxCLL, MaxFALL); null when neither is known.</summary>
    public static Box? BuildClli(HdrInfo hdr)
    {
        ArgumentNullException.ThrowIfNull(hdr);
        return hdr.MaxCll is null && hdr.MaxFall is null
            ? null
            : new Box("clli", new PayloadBuilder().U16(hdr.MaxCll ?? 0).U16(hdr.MaxFall ?? 0).ToArray());
    }

    private static byte[] BuildVpcC(CodecConfig c)
    {
        // VPCodecConfigurationRecord version 1 (profile/level unknown → 0 / 10, 8-bit 4:2:0).
        var color = c.Color;
        var depth = c.BitsPerSample > 0 ? c.BitsPerSample : 8;
        return new PayloadBuilder().FullBox(1, 0)
            .U8(0).U8(10)
            .U8((depth << 4) | (1 << 1) | (color.FullRange == true ? 1 : 0))
            .U8(color.IsSpecified ? color.Primaries : 2).U8(color.IsSpecified ? color.Transfer : 2).U8(color.IsSpecified ? color.Matrix : 2)
            .U16(0).ToArray();
    }

    private static Box BuildAudio(CodecConfig c, EntryContext ctx)
    {
        var children = new List<Box>();
        var channels = c.Channels > 0 ? c.Channels : 2;
        var rate = c.SampleRate > 0 ? c.SampleRate : (int)c.Timescale;
        var sampleSize = 16;
        var mlpSampleRate = false;
        var entryVersion = 0;
        string type;
        switch (c.Codec)
        {
            case CodecType.Aac:
            {
                type = "mp4a";
                var asc = c.Extradata is { Length: >= 2 } ? c.Extradata : Aac.BuildConfig(2, rate, channels);
                children.Add(BuildEsds(0x40, 0x05, asc, ctx.Statistics, ctx.EsId));
                var parsed = Aac.ParseConfig(asc);
                if (parsed.Channels > 0)
                    channels = parsed.Channels;
                break;
            }

            case CodecType.Mp3 or CodecType.Mp2 or CodecType.Mp1:
            {
                type = "mp4a";
                var (frameRate, _) = ctx.FirstSample is { } first ? MpegAudio.Describe(first) : (rate, channels);
                var oti = frameRate is > 0 and < 32000 ? 0x69 : 0x6B;
                children.Add(BuildEsds(oti, 0x05, null, ctx.Statistics, ctx.EsId));
                break;
            }

            case CodecType.Ac3:
            {
                type = "ac-3";
                var dac3 = c.Extradata is { Length: 3 } ? c.Extradata : ctx.FirstSample is { } s && Ac3.Parse(s) is { } h ? Ac3.BuildDac3(h) : null;
                children.Add(new Box("dac3", dac3 ?? throw new InvalidDataException("AC-3 track without a decodable first frame.")));
                break;
            }

            case CodecType.Eac3:
            {
                type = "ec-3";
                var dec3 = c.Extradata is { Length: >= 5 } ? c.Extradata : ctx.FirstSample is { } s ? Ac3.BuildDec3(Ac3.ParseAccessUnit(s)) : null;
                children.Add(new Box("dec3", dec3 ?? throw new InvalidDataException("E-AC-3 track without a decodable first frame.")));
                break;
            }

            case CodecType.Dts:
            {
                var header = ctx.FirstSample is { } s ? Dts.Parse(s) : null;
                if (header is null && c.Extradata is null)
                    throw new InvalidDataException("DTS track without a decodable first frame.");
                type = header is not null ? Dts.SampleEntryType(header) : c.SourceCodecId is "dtsc" or "dtsh" or "dtsl" or "dtse" ? c.SourceCodecId : "dtsc";
                var ddts = c.Extradata is { Length: >= 20 } ? c.Extradata : Dts.BuildDdts(header!, channels, ctx.Statistics.MaxBitrate, ctx.Statistics.AvgBitrate);
                children.Add(new Box("ddts", ddts));
                break;
            }

            case CodecType.Opus:
            {
                type = "Opus";
                var head = c.Extradata is { Length: >= 19 } ? c.Extradata : Opus.DefaultHead(channels, (int)Math.Round(c.CodecDelay.TotalSeconds * 48000), rate);
                children.Add(new Box("dOps", Opus.OpusHeadToDops(head)));
                channels = head[9];
                rate = 48000;
                break;
            }

            case CodecType.Flac:
            {
                type = "fLaC";
                var blocks = Flac.FixLastFlags(c.Extradata ?? throw new InvalidDataException("FLAC track without STREAMINFO."));
                children.Add(new Box("dfLa", [0, 0, 0, 0, .. blocks]));
                var (r, ch, bits) = Flac.Describe(blocks);
                (rate, channels, sampleSize) = (r > 0 ? r : rate, ch > 0 ? ch : channels, bits > 0 ? bits : 16);
                break;
            }

            case CodecType.TrueHd:
            {
                // Dolby TrueHD (MLP) bitstreams within the ISO base media file format, §2.4–2.5.
                type = "mlpa";
                var first = ctx.FirstSample is { } s ? TrueHd.Parse(s) : null;
                var dmlp = c.Extradata is { Length: 10 } ? c.Extradata
                    : first is { IsMajorSync: true } ? TrueHd.BuildDmlp(first)
                    : throw new InvalidDataException("Dolby TrueHD track without a major sync in its first access unit.");
                children.Add(new Box("dmlp", dmlp));
                if (first is { SampleRate: > 0 })
                    rate = first.SampleRate;
                mlpSampleRate = true;
                channels = 2;
                sampleSize = 16;
                break;
            }

            case CodecType.Pcm:
            {
                // ISO/IEC 23003-5: integer or floating-point PCM, endianness and depth in 'pcmC'; above 65535 Hz the
                // version 1 entry carries the rate in 'srat'.
                type = c.PcmFloat ? "fpcm" : "ipcm";
                if (rate > ushort.MaxValue)
                {
                    entryVersion = 1;
                    children.Add(new Box("srat", new PayloadBuilder().FullBox(0, 0).U32((uint)rate).ToArray()));
                }

                if (BuildChnl(channels) is { } chnl)
                    children.Add(chnl);
                children.Add(new Box("pcmC", new PayloadBuilder().FullBox(0, 0).U8(c.PcmBigEndian ? 0 : 1).U8(c.BitsPerSample).ToArray()));
                break;
            }

            case CodecType.Alac:
            {
                type = "alac";
                var cookie = c.Extradata ?? throw new InvalidDataException("ALAC track without a magic cookie.");
                children.Add(new Box("alac", [0, 0, 0, 0, .. cookie]));
                if (cookie.Length >= 24)
                {
                    sampleSize = cookie[5];
                    channels = cookie[9];
                    rate = (int)BinaryPrimitives.ReadUInt32BigEndian(cookie.AsSpan(20));
                }

                break;
            }

            default:
                throw new NotSupportedException($"{c.FormatName} audio cannot be stored in MP4.");
        }

        if (ctx.Statistics.AvgBitrate > 0 && c.Codec is not (CodecType.Aac or CodecType.Mp3 or CodecType.Mp2 or CodecType.Mp1))
            children.Add(new Box("btrt", new PayloadBuilder().U32(ctx.Statistics.BufferSize).U32(ctx.Statistics.MaxBitrate).U32(ctx.Statistics.AvgBitrate).ToArray()));

        var payload = new PayloadBuilder()
            .Zeros(6).U16(1)
            .U16(entryVersion).U16(0).U32(0) // version, revision, vendor
            .U16(Math.Clamp(channels, 1, ushort.MaxValue)).U16(sampleSize)
            .U16(0).U16(0)
            .U32(mlpSampleRate ? (uint)rate : SampleRateField(rate))
            .ToArray();
        return new Box(type, payload, children);
    }

    private static Box BuildSubtitle(CodecConfig c, EntryContext ctx)
    {
        switch (c.Codec)
        {
            case CodecType.Tx3g when c.Extradata is { Length: >= 30 } extra && c.SourceCodecId == "tx3g":
            {
                // Keep the source description, updating the forced flags.
                var prefix = extra[..30].ToArray();
                var flags = BinaryPrimitives.ReadUInt32BigEndian(prefix) & 0x3FFFFFFF;
                BinaryPrimitives.WriteUInt32BigEndian(prefix, flags | ctx.Tx3gDisplayFlags);
                var children = BoxParser.ParseList(extra.AsSpan(30), "tx3g");
                return new Box("tx3g", [0, 0, 0, 0, 0, 0, 0, 1, .. prefix], children);
            }

            case CodecType.Tx3g or CodecType.TextUtf8 or CodecType.Ass or CodecType.Ssa or CodecType.WebVtt:
            {
                var entry = SubtitleText.BuildTx3gEntry(ctx.TextWidth, ctx.TextHeight, SubtitleText.DefaultFontSize(ctx.TextHeight), ctx.Tx3gDisplayFlags);
                var children = BoxParser.ParseList(entry.AsSpan(30), "tx3g");
                return new Box("tx3g", [0, 0, 0, 0, 0, 0, 0, 1, .. entry[..30]], children);
            }

            case CodecType.VobSub:
            {
                var palette = VobSubPalette(c.Extradata is null ? string.Empty : Encoding.UTF8.GetString(c.Extradata));
                var esds = BuildEsds(0xE0, 0x38, palette, ctx.Statistics, ctx.EsId);
                return new Box("mp4s", [0, 0, 0, 0, 0, 0, 0, 1], [esds]);
            }

            default:
                throw new NotSupportedException($"{c.FormatName} subtitles cannot be stored in MP4.");
        }
    }

    /// <summary>MP4 VobSub palette (16 × [0, Y, Cr, Cb]) from an .idx header.</summary>
    private static byte[] VobSubPalette(string idx)
    {
        var result = new byte[64];
        foreach (var line in idx.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("palette:", StringComparison.OrdinalIgnoreCase))
                continue;
            var colors = trimmed[8..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < 16 && i < colors.Length; i++)
            {
                if (!int.TryParse(colors[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                    continue;
                var (y, cr, cb) = RgbToYuv((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
                result[i * 4 + 1] = (byte)y;
                result[i * 4 + 2] = (byte)cr;
                result[i * 4 + 3] = (byte)cb;
            }
        }

        return result;
    }

    /// <summary>Builds an esds box (ES_Descriptor with DecoderConfig and SLConfig descriptors).</summary>
    public static Box BuildEsds(int objectType, int streamType, byte[]? dsi, TrackStatistics stats, ushort esId)
    {
        var decoderConfig = new PayloadBuilder()
            .U8(objectType).U8((streamType << 2) | 1).U24((int)Math.Min(stats.BufferSize, 0xFFFFFF)).U32(stats.MaxBitrate).U32(stats.AvgBitrate);
        if (dsi is { Length: > 0 })
            decoderConfig.Bytes(Descriptor(0x05, dsi));
        var es = new PayloadBuilder()
            .U16(esId).U8(0)
            .Bytes(Descriptor(0x04, decoderConfig.ToArray()))
            .Bytes(Descriptor(0x06, [0x02]));
        return new Box("esds", new PayloadBuilder().FullBox(0, 0).Bytes(Descriptor(0x03, es.ToArray())).ToArray());
    }

    private static byte[] Descriptor(int tag, byte[] body)
    {
        var len = body.Length;
        return [(byte)tag, (byte)(0x80 | ((len >> 21) & 0x7F)), (byte)(0x80 | ((len >> 14) & 0x7F)), (byte)(0x80 | ((len >> 7) & 0x7F)), (byte)(len & 0x7F), .. body];
    }
}

/// <summary>Options of the MP4 remux engine.</summary>
public static class Mp4RemuxOptions
{
    /// <summary>
    /// Always write HEVC tracks as 'hvc1' (parameter sets only in the hvcC record), like Subler's "force hvc1": in-band
    /// parameter sets identical to the record are removed from the samples. Off by default, in which case tracks
    /// whose samples carry parameter sets are written as 'hev1'.
    /// </summary>
    public static bool ForceHvc1 { get; set; }
}
