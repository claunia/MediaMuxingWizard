using System.Globalization;
using System.Text;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Formats.Matroska.Ebml;
using static MMW.Formats.Matroska.MatroskaIds;

namespace MMW.Formats.Matroska.Media;

/// <summary>A Matroska TrackEntry kept for Matroska → Matroska passthrough.</summary>
/// <param name="Payload">The TrackEntry payload as read.</param>
/// <param name="SourceTrack">The track as parsed from it (its values are the baseline for edits).</param>
internal sealed record MatroskaNativeTrack(byte[] Payload, Track SourceTrack);

/// <summary>Translates between Matroska CodecIDs/CodecPrivate and <see cref="CodecConfig"/>.</summary>
internal static class MatroskaCodecMapping
{
    /// <summary>Describes a TrackEntry (codec details that need the first frame are filled in by the demuxer).</summary>
    public static CodecConfig Describe(ReadOnlyMemory<byte> entryPayload, string path)
    {
        var children = EbmlParser.Children(entryPayload);
        var track = MatroskaTrackParser.Parse(entryPayload, path);
        var codecId = children.GetString(CodecId) ?? string.Empty;
        var priv = children.Child(CodecPrivate)?.Data.ToArray();
        var config = new CodecConfig
        {
            SourceCodecId = codecId,
            Language = track.Language,
            Name = track.Name,
            Kind = track.Kind,
            CodecDelay = TimeSpan.FromTicks((long)(children.GetUInt(CodecDelay, 0) / 100)),
            SeekPreRoll = TimeSpan.FromTicks((long)(children.GetUInt(SeekPreRoll, 0) / 100)),
            Native = new MatroskaNativeTrack(entryPayload.ToArray(), track),
        };

        switch (track)
        {
            case VideoTrack v:
            {
                var defaultDuration = children.GetUInt(DefaultDuration, 0);
                config = config with
                {
                    Width = v.PixelWidth,
                    Height = v.PixelHeight,
                    ParNumerator = v.ParNumerator > 0 ? v.ParNumerator : 1,
                    ParDenominator = v.ParDenominator > 0 ? v.ParDenominator : 1,
                    Color = v.Color,
                    Hdr = v.Hdr,
                    FrameRate = defaultDuration > 0 ? 1e9 / defaultDuration : 0,
                    DolbyVisionConfig = DolbyVisionRecord(children),
                    BitsPerSample = ColourBitDepth(children),
                    Hdr10Plus = v.Hdr10Plus,
                    Hdr10PlusInBlockAdditions = v.Hdr10Plus,
                };
                break;
            }

            case AudioTrack a:
            {
                var audio = children.Child(Audio) is { } ae ? EbmlParser.Children(ae.Data) : [];
                config = config with
                {
                    Channels = a.Channels,
                    SampleRate = a.SampleRate,
                    BitsPerSample = (int)audio.GetUInt(BitDepth, 0),
                };
                break;
            }
        }

        return MapCodec(config, codecId, priv);
    }

    private static int ColourBitDepth(List<EbmlChild> entry)
    {
        if (entry.Child(Video) is not { } video || EbmlParser.Children(video.Data).Child(Colour) is not { } colour)
            return 0;
        return (int)EbmlParser.Children(colour.Data).GetUInt(BitsPerChannel, 0);
    }

    private static byte[]? DolbyVisionRecord(List<EbmlChild> entry)
    {
        foreach (var child in entry)
        {
            if (child.Id != BlockAdditionMapping)
                continue;
            var m = EbmlParser.Children(child.Data);
            if (m.GetUInt(BlockAddIdType, 0) is BlockAddTypeDvcC or BlockAddTypeDvvC && m.Child(BlockAddIdExtraData) is { } extra)
                return extra.Data.ToArray();
        }

        return null;
    }

    private static CodecConfig MapCodec(CodecConfig c, string id, byte[]? priv)
    {
        switch (id)
        {
            case "V_MPEG4/ISO/AVC":
                return c with { Codec = CodecType.H264, Extradata = priv };
            case "V_MPEGH/ISO/HEVC":
                return c with { Codec = CodecType.Hevc, Extradata = priv };
            case "V_MPEGI/ISO/VVC":
                return c with { Codec = CodecType.Vvc, Extradata = priv };
            case "V_AV1":
                return c with { Codec = CodecType.Av1, Extradata = priv };
            case "V_AV2":
                // As the AV2 reference encoder writes it; the demuxer turns the blocks into samples and 'av2C'.
                return c with { Codec = CodecType.Av2, Extradata = priv };
            case "V_VP8":
                return c with { Codec = CodecType.Vp8 };
            case "V_VP9":
                return c with { Codec = CodecType.Vp9 };
            case "V_MPEG2":
                return c with { Codec = CodecType.Mpeg2Video, Extradata = priv };
            case "V_AVS2":
                return c with { Codec = CodecType.Avs2, Extradata = priv };
            case "V_AVS3":
                return c with { Codec = CodecType.Avs3 }; // the sequence header is in the frames
            case "V_MS/VFW/FOURCC" when MatroskaCodecs.VfwFourCc(priv) == "CAVS":
                return c with { Codec = CodecType.Avs1 }; // AVS1-P2 in a BITMAPINFOHEADER, sequence header in the frames
            case "V_MS/VFW/FOURCC" when Vfw.ParseBitmapInfoHeader(priv) is { } bih:
                return c with { Codec = CodecType.VfwVideo, Extradata = priv, Width = c.Width > 0 ? c.Width : bih.Width, Height = c.Height > 0 ? c.Height : bih.Height };
            case "A_MS/ACM" when Vfw.ParseWaveFormatEx(priv) is { } wfx:
                return AcmCodec(c with { SampleRate = c.SampleRate > 0 ? c.SampleRate : wfx.SampleRate, Channels = c.Channels > 0 ? c.Channels : wfx.Channels }, priv!, wfx.Tag, wfx.BitsPerSample, wfx.Extra);
            case "V_MPEG1":
                return c with { Codec = CodecType.Mpeg1Video, Extradata = priv };
            case "V_MJPEG":
                return c with { Codec = CodecType.Mjpeg };
            case "V_THEORA":
                return c with { Codec = CodecType.Theora, Extradata = priv };
            case "V_PRORES":
                return c with { Codec = CodecType.ProRes, Extradata = priv };
            case "A_AC3" or "A_AC3/BSID9" or "A_AC3/BSID10":
                return c with { Codec = CodecType.Ac3 };
            case "A_EAC3":
                return c with { Codec = CodecType.Eac3 };
            case "A_DTS" or "A_DTS/EXPRESS" or "A_DTS/LOSSLESS":
                return c with { Codec = CodecType.Dts };
            case "A_TRUEHD":
                return c with { Codec = CodecType.TrueHd };
            case "A_MLP":
                return c with { Codec = CodecType.Mlp };
            case "A_AC4":
                // An unofficial ID with the 'dac4' payload as CodecPrivate: kept as the 'ac-4' sample entry it describes.
                return c with
                {
                    Codec = CodecType.Ac4,
                    Extradata = priv is { Length: > 0 } ? QuickTime.AudioEntry("ac-4", Math.Max(2, c.Channels), c.SampleRate, QuickTime.Box("dac4", priv)) : null,
                    BitsPerSample = 0,
                };
            case "A_OPUS":
            {
                var head = priv is { Length: >= 19 } ? priv : Opus.DefaultHead(c.Channels, (int)Math.Round(c.CodecDelay.TotalSeconds * 48000), c.SampleRate);
                return c with { Codec = CodecType.Opus, Extradata = head, SampleRate = 48000 };
            }

            case "A_VORBIS":
                return c with { Codec = CodecType.Vorbis, Extradata = priv };
            case "A_FLAC":
            {
                var blocks = priv is null ? null : Flac.MetadataBlocks(priv);
                var (rate, ch, bits) = blocks is null ? (0, 0, 0) : Flac.Describe(blocks);
                return c with
                {
                    Codec = CodecType.Flac,
                    Extradata = blocks,
                    SampleRate = rate > 0 ? rate : c.SampleRate,
                    Channels = ch > 0 ? ch : c.Channels,
                    BitsPerSample = bits > 0 ? bits : c.BitsPerSample,
                };
            }

            case "A_ALAC":
                return c with { Codec = CodecType.Alac, Extradata = priv is { Length: >= 24 } && priv.AsSpan(4, 4).SequenceEqual("alac"u8) ? priv[12..] : priv };
            case "A_MPEG/L3":
                return c with { Codec = CodecType.Mp3 };
            case "A_MPEG/L2":
                return c with { Codec = CodecType.Mp2 };
            case "A_MPEG/L1":
                return c with { Codec = CodecType.Mp1 };
            case "A_PCM/INT/LIT":
                return c with { Codec = CodecType.Pcm };
            case "A_PCM/INT/BIG":
                return c with { Codec = CodecType.Pcm, PcmBigEndian = true };
            case "A_PCM/FLOAT/IEEE":
                return c with { Codec = CodecType.Pcm, PcmFloat = true };
            case "S_TEXT/UTF8" or "S_TEXT/ASCII":
                return c with { Codec = CodecType.TextUtf8 };
            case "S_TEXT/ASS" or "S_ASS":
                return c with { Codec = CodecType.Ass, Extradata = priv };
            case "S_TEXT/SSA" or "S_SSA":
                return c with { Codec = CodecType.Ssa, Extradata = priv };
            case "S_TEXT/WEBVTT" or "D_WEBVTT/SUBTITLES" or "D_WEBVTT/CAPTIONS" or "D_WEBVTT/DESCRIPTIONS":
                return c with { Codec = CodecType.WebVtt, Extradata = priv };
            case "S_VOBSUB":
            {
                var (w, h) = VobSubSize(priv);
                return c with { Codec = CodecType.VobSub, Extradata = priv, SubtitleWidth = w, SubtitleHeight = h };
            }

            case "S_HDMV/PGS":
                return c with { Codec = CodecType.Pgs, Extradata = priv };
            case "S_DVBSUB":
                return c with { Codec = CodecType.DvbSub, Extradata = priv };
        }

        if (id is "V_QUICKTIME" or "A_QUICKTIME" && priv is { Length: >= 8 })
        {
            // The QuickTime sample description, as mkvmerge stores it (older files may lack the size and type header).
            var entry = priv;
            if (QuickTime.CodecFor(QuickTime.EntryType(priv) ?? string.Empty) is null && QuickTime.CodecFor(Encoding.ASCII.GetString(priv, 0, 4)) is not null)
            {
                entry = new byte[priv.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(entry, entry.Length);
                priv.CopyTo(entry, 4);
            }

            return QuickTime.CodecFor(QuickTime.EntryType(entry) ?? string.Empty) is { } codec
                ? c with { Codec = codec, Extradata = entry, BitsPerSample = c.Kind == TrackKind.Audio ? 0 : c.BitsPerSample }
                : c with { Codec = CodecType.Unknown, Extradata = priv };
        }

        if (id.StartsWith("V_REAL/", StringComparison.Ordinal) && Vfw.RealVideoFourCc(priv) is not null)
            return c with { Codec = CodecType.RealVideo, Extradata = priv };
        if (id.StartsWith("V_MPEG4/ISO/", StringComparison.Ordinal))
            return c with { Codec = CodecType.Mpeg4Visual, Extradata = priv };
        if (id.StartsWith("A_AAC", StringComparison.Ordinal))
        {
            var asc = priv is { Length: >= 2 } ? priv : Aac.ConfigFromCodecId(id, c.SampleRate, c.Channels, 0);
            return c with { Codec = CodecType.Aac, Extradata = asc, BitsPerSample = 0 };
        }

        return c with { Codec = CodecType.Unknown, Extradata = priv };
    }

    /// <summary>An A_MS/ACM track: the codecs this application models by their format tag, the others kept as ACM audio.</summary>
    private static CodecConfig AcmCodec(CodecConfig c, byte[] wfx, int tag, int bits, byte[] extra)
    {
        if (tag == 0xFFFE && extra.Length >= 8)
            tag = BitConverter.ToUInt16(extra, 6); // WAVE_FORMAT_EXTENSIBLE: the sub-format GUID starts with the tag
        return tag switch
        {
            0x0001 when bits is 8 or 16 or 24 or 32 => c with { Codec = CodecType.Pcm, BitsPerSample = bits },
            0x0003 when bits is 32 or 64 => c with { Codec = CodecType.Pcm, BitsPerSample = bits, PcmFloat = true },
            0x0050 => c with { Codec = CodecType.Mp2 },
            0x0055 => c with { Codec = CodecType.Mp3 },
            0x2000 => c with { Codec = CodecType.Ac3 },
            0x2001 => c with { Codec = CodecType.Dts },
            _ => c with { Codec = CodecType.AcmAudio, Extradata = wfx, BitsPerSample = bits },
        };
    }

    private static (int Width, int Height) VobSubSize(byte[]? idx)
    {
        if (idx is null)
            return (0, 0);
        foreach (var line in Encoding.UTF8.GetString(idx).Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
                continue;
            var parts = t[5..].Trim().Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
                return (w, h);
        }

        return (0, 0);
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Whether a Matroska file can store a track described by <paramref name="config"/>.</summary>
    public static TrackSupport CheckSupport(CodecConfig config)
    {
        if (config.Native is MatroskaNativeTrack)
            return TrackSupport.Passthrough;
        if (config.Codec == CodecType.Tx3g)
            return new TrackSupport(TrackSupportLevel.Converted, ImportAction.ConvertToAss, "converted to Advanced SubStation Alpha (S_TEXT/ASS), which keeps its styles, positions and karaoke");
        if (config.Codec == CodecType.Evc)
        {
            return new TrackSupport(TrackSupportLevel.Unsupported, ImportAction.Skip,
                "MPEG-5 EVC has no Matroska codec ID (neither the Matroska specification nor other tools define one), so it cannot be stored in Matroska; save as MP4 instead");
        }

        if (config.Codec == CodecType.Pcm && config.PcmFloat && config.PcmBigEndian)
            return new TrackSupport(TrackSupportLevel.NeedsConversion, ImportAction.ConvertToAac, "big-endian floating-point PCM cannot be stored in Matroska");
        return CodecIdFor(config) is null
            ? new TrackSupport(TrackSupportLevel.Unsupported, ImportAction.Skip, $"{config.FormatName} cannot be stored in Matroska")
            : TrackSupport.Passthrough;
    }

    /// <summary>
    /// The 4-byte configuration record the AV2 reference encoder stores as CodecPrivate, from the sequence header and
    /// content interpretation of 'av2C' (kept as it is when it already is that record).
    /// </summary>
    private static byte[]? Av2CodecPrivate(byte[]? extradata)
    {
        if (extradata is { Length: 4 } && (extradata[0] & 0x80) != 0)
            return extradata;
        var (sequence, interpretation) = Av2.Describe(extradata);
        return sequence is null ? null : Av2.BuildMatroskaRecord(sequence, interpretation);
    }

    /// <summary>The Matroska CodecID for <paramref name="c"/>, or null when there is none.</summary>
    public static string? CodecIdFor(CodecConfig c) => c.Codec switch
    {
        CodecType.H264 => "V_MPEG4/ISO/AVC",
        CodecType.Hevc => "V_MPEGH/ISO/HEVC",
        CodecType.Vvc => "V_MPEGI/ISO/VVC",
        CodecType.Av1 => "V_AV1",
        CodecType.Av2 => "V_AV2", // as the AV2 reference encoder (AVM) writes it; no mapping is published yet
        CodecType.Vp8 => "V_VP8",
        CodecType.Vp9 => "V_VP9",
        CodecType.Mpeg4Visual => "V_MPEG4/ISO/ASP",
        CodecType.Mpeg2Video => "V_MPEG2",
        CodecType.Mpeg1Video => "V_MPEG1",
        CodecType.Avs2 => "V_AVS2", // as FFmpeg reads and writes it
        CodecType.Avs3 => "V_AVS3", // as FFmpeg reads and writes it
        CodecType.Avs1 => "V_MS/VFW/FOURCC", // FourCC 'CAVS', as FFmpeg and mkvmerge write it
        CodecType.VfwVideo => "V_MS/VFW/FOURCC", // the BITMAPINFOHEADER as CodecPrivate, as mkvmerge writes it
        CodecType.RealVideo when Vfw.RealVideoFourCc(c.Extradata) is { } fourCc => "V_REAL/" + fourCc,
        CodecType.AcmAudio => "A_MS/ACM", // the WAVEFORMATEX as CodecPrivate, as mkvmerge writes it
        // The QuickTime sample description as CodecPrivate, as mkvmerge writes these codecs from MP4/MOV.
        CodecType.Vc1 or CodecType.H263 or CodecType.Dirac or CodecType.Dnxhd when c.Extradata is { Length: >= 8 } => "V_QUICKTIME",
        CodecType.AmrNb or CodecType.AmrWb or CodecType.Ac4 or CodecType.MpegH when c.Extradata is { Length: >= 8 } => "A_QUICKTIME",
        CodecType.Mjpeg => "V_MJPEG",
        CodecType.Theora => "V_THEORA",
        CodecType.ProRes => "V_PRORES",
        CodecType.Aac => "A_AAC",
        CodecType.Ac3 => "A_AC3",
        CodecType.Eac3 => "A_EAC3",
        CodecType.Dts => "A_DTS",
        CodecType.TrueHd => "A_TRUEHD",
        CodecType.Mlp => "A_MLP",
        CodecType.Opus => "A_OPUS",
        CodecType.Vorbis => "A_VORBIS",
        CodecType.Flac => "A_FLAC",
        CodecType.Alac => "A_ALAC",
        CodecType.Mp3 => "A_MPEG/L3",
        CodecType.Mp2 => "A_MPEG/L2",
        CodecType.Mp1 => "A_MPEG/L1",
        CodecType.Pcm => c.PcmFloat ? "A_PCM/FLOAT/IEEE" : c.PcmBigEndian ? "A_PCM/INT/BIG" : "A_PCM/INT/LIT",
        CodecType.TextUtf8 or CodecType.Tx3g => "S_TEXT/UTF8",
        CodecType.Ass => "S_TEXT/ASS",
        CodecType.Ssa => "S_TEXT/SSA",
        CodecType.WebVtt => "D_WEBVTT/SUBTITLES", // what ffmpeg reads and writes (mkvmerge accepts both)
        CodecType.VobSub => "S_VOBSUB",
        CodecType.Pgs => "S_HDMV/PGS",
        CodecType.DvbSub => "S_DVBSUB",
        _ => null,
    };

    /// <summary>CodecPrivate for <paramref name="c"/> (null when the codec has none).</summary>
    public static byte[]? CodecPrivateFor(CodecConfig c) => c.Codec switch
    {
        CodecType.H264 or CodecType.Hevc or CodecType.Vvc or CodecType.Av1 or CodecType.Mpeg4Visual or CodecType.Mpeg2Video or CodecType.Mpeg1Video or
            CodecType.Theora or CodecType.Aac or CodecType.Vorbis or CodecType.Ass or CodecType.Ssa or CodecType.VobSub or CodecType.Pgs or
            CodecType.DvbSub => c.Extradata,
        CodecType.ProRes => c.Extradata is { Length: 4 } ? c.Extradata : c.SourceCodecId.Length == 4 ? Encoding.ASCII.GetBytes(c.SourceCodecId) : null,
        CodecType.Avs1 => MatroskaCodecs.BitmapInfoHeader(c.Width, c.Height, "CAVS"),
        CodecType.Opus => c.Extradata,
        CodecType.Flac => c.Extradata is null ? null : [.. "fLaC"u8, .. Flac.FixLastFlags(c.Extradata)],
        CodecType.Alac => c.Extradata,
        CodecType.Av2 => Av2CodecPrivate(c.Extradata),
        CodecType.VfwVideo or CodecType.AcmAudio or CodecType.RealVideo => c.Extradata,
        _ when QuickTime.IsEntryCodec(c.Codec) => c.Extradata,
        CodecType.WebVtt => c.Extradata is { Length: > 0 } header && Encoding.UTF8.GetString(header).StartsWith("WEBVTT", StringComparison.Ordinal) ? header : null,
        _ => null,
    };
}
