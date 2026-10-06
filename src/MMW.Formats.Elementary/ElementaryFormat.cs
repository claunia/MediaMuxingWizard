using MMW.Core.Media;
using MMW.Core.Media.Codecs;

namespace MMW.Formats.Elementary;

/// <summary>The elementary stream and text subtitle formats that can be imported.</summary>
public enum ElementaryKind
{
    None,
    H264,
    Hevc,
    Aac,
    Ac3,
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
        [".264", ".h264", ".avc", ".265", ".h265", ".hevc", ".aac", ".adts", ".ac3", ".eac3", ".ec3", ".srt", ".ass", ".ssa", ".vtt"];

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
            ".aac" or ".adts" => header.IndexOf((byte)0xFF) >= 0 ? ElementaryKind.Aac : ElementaryKind.None,
            ".ac3" or ".eac3" or ".ec3" => header.IndexOf([(byte)0x0B, (byte)0x77]) >= 0 ? ElementaryKind.Ac3 : ElementaryKind.None,
            ".srt" => ElementaryKind.SubRip,
            ".ass" or ".ssa" => ElementaryKind.Ass,
            ".vtt" => ElementaryKind.WebVtt,
            _ => ElementaryKind.None,
        };
    }

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
            case ElementaryKind.H264 or ElementaryKind.Hevc:
            {
                var codec = kind == ElementaryKind.H264 ? CodecType.H264 : CodecType.Hevc;
                AnnexBProbe probe;
                using (var fs = File.OpenRead(path))
                    probe = AnnexBVideoParser.Probe(fs, codec, options?.FrameRate);
                var config = probe.Config;
                var source = new ElementarySource(config, () => new AnnexBVideoParser(OpenStream(path), codec, config.DefaultSampleDuration, probe.ParameterSets),
                    TimeSpan.Zero, Math.Max(1, length / 20_000));
                return new ElementaryDemuxer(path, codec == CodecType.H264 ? "H.264 Annex B" : "HEVC Annex B", source)
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
