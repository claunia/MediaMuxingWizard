using System.Buffers.Binary;
using MMW.Core.Media.Codecs;

namespace MMW.Core.Media.Export;

/// <summary>What a track writer writes to: the source track and the output stream(s).</summary>
internal sealed class ExportContext(ISampleSource source, Stream output, Stream? companion)
{
    public ISampleSource Source { get; } = source;

    public CodecConfig Config => Source.Config;

    /// <summary>The exported file (seekable).</summary>
    public Stream Output { get; } = output;

    /// <summary>The second file of a two-file format (the VobSub .sub); null for others.</summary>
    public Stream? Companion { get; } = companion;

    /// <summary>
    /// Presentation time of a sample on the output timeline, in milliseconds: media time <see cref="ISampleSource.MediaStart"/>
    /// is shown at <see cref="ISampleSource.StartOffset"/>.
    /// </summary>
    public double Milliseconds(long mediaTime) =>
        Config.Timescale == 0 ? 0 : (mediaTime - Source.MediaStart) * 1000.0 / Config.Timescale + Source.StartOffset.TotalMilliseconds;

    /// <summary>A duration in milliseconds.</summary>
    public double DurationMilliseconds(long ticks) => Config.Timescale == 0 ? 0 : ticks * 1000.0 / Config.Timescale;
}

/// <summary>Writes the samples of one track in a raw format.</summary>
internal abstract class TrackWriter(ExportContext context)
{
    protected ExportContext Context { get; } = context;

    protected CodecConfig Config => Context.Config;

    protected Stream Output => Context.Output;

    /// <summary>Writes the file header.</summary>
    public virtual void Start()
    {
    }

    /// <summary>Writes one sample (in decoding order).</summary>
    public abstract void Write(MediaSample sample);

    /// <summary>Completes the file (trailing data, header sizes).</summary>
    public virtual void Finish()
    {
    }

    protected void WriteU16BigEndian(int value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value);
        Output.Write(b);
    }

    protected void WriteU32BigEndian(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        Output.Write(b);
    }
}

/// <summary>Writes the samples one after the other, unchanged (with an optional file header).</summary>
internal sealed class ConcatWriter(ExportContext context, byte[]? header = null) : TrackWriter(context)
{
    public override void Start()
    {
        if (header is not null)
            Output.Write(header);
    }

    public override void Write(MediaSample sample) => Output.Write(sample.GetData().Span);
}

/// <summary>Chooses the raw format of a codec and creates its writer.</summary>
internal static class ExportWriters
{
    public static string? Extension(CodecConfig config) => config.Codec switch
    {
        CodecType.H264 => ".h264",
        CodecType.Hevc => ".h265",
        CodecType.Vvc => ".h266",
        CodecType.Evc => ".evc",
        CodecType.Av1 or CodecType.Av2 or CodecType.Vp8 or CodecType.Vp9 => ".ivf",
        CodecType.Mpeg1Video => ".m1v",
        CodecType.Mpeg2Video => ".m2v",
        CodecType.Mpeg4Visual => ".m4v",
        CodecType.Avs1 => ".avs",
        CodecType.Avs2 => ".avs2",
        CodecType.Avs3 => ".avs3",
        CodecType.Vc1 => ".vc1",
        CodecType.Dirac => ".drc",
        CodecType.Dnxhd => ".dnxhd",
        CodecType.H263 => ".h263",
        CodecType.Mjpeg => ".mjpg",
        CodecType.Aac when config.Extradata is { Length: >= 2 } => ".aac",
        CodecType.Ac3 => ".ac3",
        CodecType.Eac3 => ".eac3",
        CodecType.Ac4 => ".ac4",
        CodecType.Dts => ".dts",
        CodecType.TrueHd => ".thd",
        CodecType.Mlp => ".mlp",
        CodecType.Mp1 => ".mp1",
        CodecType.Mp2 => ".mp2",
        CodecType.Mp3 => ".mp3",
        CodecType.Flac when config.Extradata is { Length: >= 4 } => ".flac",
        CodecType.Alac when config.Extradata is { Length: >= 24 } => ".caf",
        CodecType.MpegH when MpegHWriter.CanWrite(config) => ".mhas",
        CodecType.AmrNb => ".amr",
        CodecType.AmrWb => ".awb",
        CodecType.Pcm when WavWriter.CanWrite(config) => ".wav",
        CodecType.AcmAudio when Vfw.ParseWaveFormatEx(config.Extradata) is not null => ".wav",
        CodecType.Opus => ".opus",
        CodecType.Vorbis when VorbisHeaders.Parse(config.Extradata) is not null => ".ogg",
        CodecType.TextUtf8 or CodecType.Tx3g => ".srt",
        CodecType.Ass => ".ass",
        CodecType.Ssa => ".ssa",
        CodecType.WebVtt => ".vtt",
        CodecType.Pgs => ".sup",
        CodecType.VobSub => ".idx",
        _ => null,
    };

    /// <summary>The extension of the second file a format writes next to the first (VobSub .sub); null for one-file formats.</summary>
    public static string? CompanionExtension(CodecConfig config) => config.Codec == CodecType.VobSub ? ".sub" : null;

    public static TrackWriter Create(ExportContext context)
    {
        var config = context.Config;
        return config.Codec switch
        {
            CodecType.H264 or CodecType.Hevc or CodecType.Vvc => new AnnexBWriter(context),
            CodecType.Evc => new EvcWriter(context),
            CodecType.Av1 or CodecType.Av2 or CodecType.Vp8 or CodecType.Vp9 => new IvfWriter(context),
            CodecType.Mpeg1Video or CodecType.Mpeg2Video or CodecType.Mpeg4Visual => new MpegVideoWriter(context),
            CodecType.Vc1 => new Vc1Writer(context),
            CodecType.Aac => new AdtsWriter(context),
            CodecType.Ac4 => new Ac4Writer(context),
            CodecType.Flac => new ConcatWriter(context, [.. "fLaC"u8, .. Flac.FixLastFlags(Flac.MetadataBlocks(config.Extradata))]),
            CodecType.Alac => new CafWriter(context),
            CodecType.MpegH => new MpegHWriter(context),
            CodecType.AmrNb => new ConcatWriter(context, "#!AMR\n"u8.ToArray()),
            CodecType.AmrWb => new ConcatWriter(context, "#!AMR-WB\n"u8.ToArray()),
            CodecType.Pcm or CodecType.AcmAudio => new WavWriter(context),
            CodecType.Opus => new OggOpusWriter(context),
            CodecType.Vorbis => new OggVorbisWriter(context),
            CodecType.TextUtf8 or CodecType.Tx3g => new SrtWriter(context),
            CodecType.Ass or CodecType.Ssa => new AssWriter(context),
            CodecType.WebVtt => new WebVttWriter(context),
            CodecType.Pgs => new SupWriter(context),
            CodecType.VobSub => new VobSubWriter(context),
            _ when Extension(config) is not null => new ConcatWriter(context),
            _ => throw new NotSupportedException($"{config.FormatName} tracks cannot be exported to a raw file."),
        };
    }
}
