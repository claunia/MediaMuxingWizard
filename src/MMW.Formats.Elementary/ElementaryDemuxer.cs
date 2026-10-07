using MMW.Core.Media;
using MMW.Core.Model;

namespace MMW.Formats.Elementary;

/// <summary>Produces the samples of an elementary stream in decoding order.</summary>
internal interface IElementaryParser : IDisposable
{
    /// <summary>The next sample, or null at the end of the stream.</summary>
    MediaSample? Next();
}

/// <summary>A demuxer exposing the single track of an elementary stream or subtitle file.</summary>
internal sealed class ElementaryDemuxer : IDemuxer
{
    public ElementaryDemuxer(string path, string formatName, ElementarySource source)
        : this(path, formatName, source, source.Duration)
    {
    }

    public ElementaryDemuxer(string path, string formatName, ISampleSource source, TimeSpan duration)
    {
        Path = path;
        FormatName = formatName;
        Tracks = [source];
        Duration = duration;
    }

    public string Path { get; }

    public string FormatName { get; }

    public ContainerKind Container => ContainerKind.Unknown;

    public IReadOnlyList<ISampleSource> Tracks { get; }

    public TimeSpan Duration { get; }

    /// <summary>Raw video without timing information (the frame rate is assumed unless given).</summary>
    public bool RequiresFrameRate { get; init; }

    public void Dispose()
    {
        foreach (var t in Tracks.OfType<IDisposable>())
            t.Dispose();
    }
}

/// <summary>The track of an elementary stream; <see cref="Reset"/> restarts the parser.</summary>
internal sealed class ElementarySource : ISampleSource, IDisposable
{
    private readonly Func<IElementaryParser> _factory;
    private IElementaryParser? _parser;

    public ElementarySource(CodecConfig config, Func<IElementaryParser> factory, TimeSpan duration, long sampleCountHint)
    {
        Config = config;
        _factory = factory;
        Duration = duration;
        SampleCountHint = sampleCountHint;
    }

    /// <summary>Elementary streams have a single track with ID 1.</summary>
    public uint TrackId => 1;

    public CodecConfig Config { get; }

    public TimeSpan StartOffset => TimeSpan.Zero;

    public long MediaStart => 0;

    public TimeSpan Duration { get; }

    public long SampleCountHint { get; }

    public MediaSample? ReadNext()
    {
        _parser ??= _factory();
        return _parser.Next();
    }

    public void Reset()
    {
        _parser?.Dispose();
        _parser = null;
    }

    public void Dispose() => Reset();
}
