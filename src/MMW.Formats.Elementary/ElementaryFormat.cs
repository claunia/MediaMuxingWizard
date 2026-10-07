using MMW.Core.Media;
using MMW.Core.Media.Codecs;

namespace MMW.Formats.Elementary;

/// <summary>The elementary stream and text subtitle formats that can be imported.</summary>
public enum ElementaryKind
{
    None,
    H264,
    Hevc,
    Vvc,
    Evc,
    Avs1,
    Avs2,
    Avs3,
    Aac,
    Ac3,
    Dts,
    SubRip,
    Ass,
    WebVtt,
}

/// <summary>Registers the elementary stream demuxer with <see cref="MediaFormatRegistry"/>.</summary>
public static class ElementaryFormat
{
    private static int s_registered;

    /// <summary>File extensions recognised as elementary streams or subtitle files.</summary>
    public static IReadOnlyList<string> Extensions { get; } =
        [".264", ".h264", ".avc", ".265", ".h265", ".hevc", ".266", ".h266", ".vvc", ".evc", ".avs", ".cavs", ".avs2", ".avs3", ".aac", ".adts", ".ac3", ".eac3", ".ec3", ".dts", ".dtshd", ".srt", ".ass", ".ssa", ".vtt"];

    public static void Register()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 1)
            return;
        MediaFormatRegistry.Register(new ElementaryDemuxerFactory());
    }

    /// <summary>Identifies the format of a file from its extension, confirmed by its first bytes.</summary>
    public static ElementaryKind Detect(string path, ReadOnlySpan<byte> header)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".264" or ".h264" or ".avc" => LooksLikeAnnexB(header) ? ElementaryKind.H264 : ElementaryKind.None,
            ".265" or ".h265" or ".hevc" => LooksLikeAnnexB(header) ? ElementaryKind.Hevc : ElementaryKind.None,
            ".266" or ".h266" or ".vvc" => LooksLikeAnnexB(header) ? ElementaryKind.Vvc : ElementaryKind.None,
            ".evc" => LooksLikeEvc(header) ? ElementaryKind.Evc : ElementaryKind.None,
            // .avs is used for AVS1 and AVS2 (and AviSynth scripts): the sequence header tells them apart.
            ".avs" when AvsSequenceStart(header) is { } at => Avs2Header(header[at..]) ? ElementaryKind.Avs2 : ElementaryKind.Avs1,
            ".cavs" => AvsSequenceStart(header) is not null ? ElementaryKind.Avs1 : ElementaryKind.None,
            ".avs2" => AvsSequenceStart(header) is not null ? ElementaryKind.Avs2 : ElementaryKind.None,
            ".avs3" => AvsSequenceStart(header) is not null ? ElementaryKind.Avs3 : ElementaryKind.None,
            ".aac" or ".adts" => header.IndexOf((byte)0xFF) >= 0 ? ElementaryKind.Aac : ElementaryKind.None,
            ".ac3" or ".eac3" or ".ec3" => header.IndexOf([(byte)0x0B, (byte)0x77]) >= 0 ? ElementaryKind.Ac3 : ElementaryKind.None,
            ".dts" or ".dtshd" => header.StartsWith("DTSHDHDR"u8) || header.IndexOf([(byte)0x7F, (byte)0xFE, (byte)0x80, (byte)0x01]) >= 0
                ? ElementaryKind.Dts
                : ElementaryKind.None,
            ".srt" => ElementaryKind.SubRip,
            ".ass" or ".ssa" => ElementaryKind.Ass,
            ".vtt" => ElementaryKind.WebVtt,
            _ => ElementaryKind.None,
        };
    }

    /// <summary>Position of the first AVS sequence header start code (00 00 01 B0) in the first bytes, or null.</summary>
    private static int? AvsSequenceStart(ReadOnlySpan<byte> header)
    {
        var at = header.IndexOf([(byte)0, (byte)0, (byte)1, Avs.SequenceHeader]);
        return at is >= 0 and < 64 ? at : null;
    }

    /// <summary>An AVS2 sequence header: an AVS2 profile and a level that AVS1/AVS+ does not use.</summary>
    private static bool Avs2Header(ReadOnlySpan<byte> d) =>
        d.Length >= 6 && d[4] is 0x12 or 0x20 or 0x22 or 0x30 or 0x32 &&
        d[5] is not (0x10 or 0x11 or 0x12 or 0x20 or 0x21 or 0x22 or 0x40 or 0x41 or 0x42);

    /// <summary>A raw EVC stream: a 4-byte NAL unit length, then a NAL unit header with forbidden_zero_bit clear.</summary>
    private static bool LooksLikeEvc(ReadOnlySpan<byte> header) =>
        header.Length >= 6 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header) is > 2 and < 1 << 24 &&
        (header[4] & 0x80) == 0 && Evc.NalType(header[4..]) is >= 0 and <= Evc.NalSei;

    private static bool LooksLikeAnnexB(ReadOnlySpan<byte> header) => header.IndexOf([(byte)0, (byte)0, (byte)1]) is >= 0 and < 64;

    /// <summary>Opens an elementary stream or subtitle file.</summary>
    /// <exception cref="InvalidDataException">The file is not valid.</exception>
    public static IDemuxer Open(string path, DemuxOptions? options = null)
    {
        Span<byte> header = stackalloc byte[4096];
        int read;
        using (var fs = File.OpenRead(path))
            read = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        var kind = Detect(path, header[..read]);
        var length = new FileInfo(path).Length;
        switch (kind)
        {
            case ElementaryKind.H264 or ElementaryKind.Hevc or ElementaryKind.Vvc or ElementaryKind.Evc or ElementaryKind.Avs1 or
                ElementaryKind.Avs2 or ElementaryKind.Avs3:
            {
                var codec = kind switch
                {
                    ElementaryKind.H264 => CodecType.H264,
                    ElementaryKind.Hevc => CodecType.Hevc,
                    ElementaryKind.Vvc => CodecType.Vvc,
                    ElementaryKind.Avs1 => CodecType.Avs1,
                    ElementaryKind.Avs2 => CodecType.Avs2,
                    ElementaryKind.Avs3 => CodecType.Avs3,
                    _ => CodecType.Evc,
                };
                AnnexBProbe probe;
                using (var fs = File.OpenRead(path))
                    probe = AnnexBVideoParser.Probe(fs, codec, options?.FrameRate);
                var config = probe.Config;
                var source = new ElementarySource(config, () => new AnnexBVideoParser(OpenStream(path), codec, config.DefaultSampleDuration, probe.ParameterSets),
                    TimeSpan.Zero, Math.Max(1, length / 20_000));
                return new ElementaryDemuxer(path, CodecNames.Display(codec) + (codec is CodecType.H264 or CodecType.Hevc or CodecType.Vvc ? " Annex B" : " elementary stream"), source)
                {
                    RequiresFrameRate = !probe.HasTiming,
                };
            }

            case ElementaryKind.Aac:
            {
                CodecConfig config;
                long count;
                using (var fs = File.OpenRead(path))
                    config = AdtsParser.Probe(fs, out count);
                var duration = TimeSpan.FromSeconds(count * 1024.0 / Math.Max(1, config.SampleRate));
                return new ElementaryDemuxer(path, "ADTS AAC", new ElementarySource(config, () => new AdtsParser(OpenStream(path)), duration, count));
            }

            case ElementaryKind.Ac3:
            {
                CodecConfig config;
                long count;
                using (var fs = File.OpenRead(path))
                    config = Ac3Parser.Probe(fs, out count);
                var duration = TimeSpan.FromSeconds(count * (double)config.DefaultSampleDuration / Math.Max(1, config.SampleRate));
                return new ElementaryDemuxer(path, config.Codec == CodecType.Eac3 ? "E-AC-3" : "AC-3",
                    new ElementarySource(config, () => new Ac3Parser(OpenStream(path)), duration, count));
            }

            case ElementaryKind.Dts:
            {
                CodecConfig config;
                long count;
                using (var fs = File.OpenRead(path))
                    config = DtsParser.Probe(fs, out count);
                var duration = TimeSpan.FromSeconds(count * (double)config.DefaultSampleDuration / Math.Max(1, config.Timescale));
                var name = config.AudioProfile.Length > 0 ? config.AudioProfile : "DTS";
                return new ElementaryDemuxer(path, name, new ElementarySource(config, () => new DtsParser(OpenStream(path)), duration, count));
            }

            case ElementaryKind.SubRip or ElementaryKind.Ass or ElementaryKind.WebVtt:
            {
                var text = SubtitleFiles.ReadText(path);
                var file = kind switch
                {
                    ElementaryKind.SubRip => SubtitleFiles.ParseSrt(text),
                    ElementaryKind.Ass => SubtitleFiles.ParseAss(text),
                    _ => SubtitleFiles.ParseWebVtt(text),
                };
                var duration = file.Cues.Count == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(file.Cues.Max(c => c.End));
                var name = kind switch
                {
                    ElementaryKind.SubRip => "SubRip",
                    ElementaryKind.Ass => file.Config.Codec == CodecType.Ssa ? "SubStation Alpha" : "Advanced SubStation Alpha",
                    _ => "WebVTT",
                };
                return new ElementaryDemuxer(path, name, new ElementarySource(file.Config, () => new SubtitleCueParser(file.Cues), duration, file.Cues.Count));
            }

            default:
                throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a supported elementary stream.");
        }
    }

    /// <summary>True when <paramref name="demuxer"/> is a raw video stream without timing, whose frame rate must be chosen.</summary>
    public static bool RequiresFrameRate(IDemuxer demuxer) => demuxer is ElementaryDemuxer { RequiresFrameRate: true };

    private static FileStream OpenStream(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
}

/// <summary>Opens elementary streams and text subtitle files as <see cref="IDemuxer"/>s.</summary>
public sealed class ElementaryDemuxerFactory : IDemuxerFactory
{
    public string Name => "Elementary";

    public int Probe(string path, ReadOnlySpan<byte> header) => ElementaryFormat.Detect(path, header) == ElementaryKind.None ? 0 : 60;

    public IDemuxer Open(string path, DemuxOptions? options = null) => ElementaryFormat.Open(path, options);
}
