using MMW.Core.Model;

namespace MMW.Core.Media;

/// <summary>A track of a demuxed file, read as a sequence of samples in decoding order.</summary>
public interface ISampleSource
{
    /// <summary>ID of the track in its file (MP4 track ID, Matroska TrackNumber, 1 for elementary streams).</summary>
    uint TrackId { get; }

    CodecConfig Config { get; }

    /// <summary>Presentation time at which media time <see cref="MediaStart"/> is shown (MP4 initial empty edit).</summary>
    TimeSpan StartOffset { get; }

    /// <summary>
    /// Media time (in <see cref="CodecConfig.Timescale"/> units) of the first presented instant; samples presented
    /// earlier are decoder pre-roll (AAC priming, Opus pre-skip, MP4 edit list media_time).
    /// </summary>
    long MediaStart { get; }

    /// <summary>Duration of the track (0 when unknown).</summary>
    TimeSpan Duration { get; }

    /// <summary>Exact or estimated number of samples; -1 when unknown.</summary>
    long SampleCountHint { get; }

    /// <summary>Returns the next sample in decoding order, or null at the end of the track.</summary>
    MediaSample? ReadNext();

    /// <summary>Rewinds to the first sample.</summary>
    void Reset();
}

/// <summary>An opened media file exposing its tracks as <see cref="ISampleSource"/>s.</summary>
/// <remarks>
/// Implementations read the file in position order and buffer only sample descriptors (payloads stay on disk and are
/// read lazily), so pulling the tracks at different rates costs little memory.
/// </remarks>
public interface IDemuxer : IDisposable
{
    string Path { get; }

    /// <summary>Short name of the file format ("MP4", "Matroska", "H.264 Annex B", "SubRip", …).</summary>
    string FormatName { get; }

    /// <summary>Container family; <see cref="ContainerKind.Unknown"/> for elementary streams.</summary>
    ContainerKind Container { get; }

    IReadOnlyList<ISampleSource> Tracks { get; }

    TimeSpan Duration { get; }
}

/// <summary>Options passed to a demuxer when a file is opened.</summary>
public sealed record DemuxOptions
{
    /// <summary>Frame rate for raw video streams that carry no timing (H.264/HEVC Annex B).</summary>
    public double? FrameRate { get; init; }
}

/// <summary>Per-track settings handed to <see cref="IMuxer.AddTrack"/>.</summary>
public sealed record MuxTrackSettings
{
    /// <summary>The document track whose properties (name, language, flags, references …) are written.</summary>
    public Track? Model { get; init; }

    /// <summary>Expected number of samples (sizes header space); -1 when unknown.</summary>
    public long EstimatedSampleCount { get; init; } = -1;

    /// <summary>Expected duration (sizes header space and progress); zero when unknown.</summary>
    public TimeSpan EstimatedDuration { get; init; }

    /// <summary>Decoder pre-roll at the start of the track (samples presented before time zero).</summary>
    public TimeSpan PreRoll { get; init; }
}

/// <summary>Document-level settings of a muxer.</summary>
public sealed record MuxerSettings
{
    /// <summary>The document whose metadata, artwork and chapters are written.</summary>
    public required MediaDocument Document { get; init; }

    /// <summary>Final path of the file (its extension selects brands / doc type); the muxer may write elsewhere first.</summary>
    public required string OutputPath { get; init; }

    public SaveOptions Options { get; init; } = new();
}

/// <summary>Writes tracks into a container.</summary>
/// <remarks>
/// Usage: <see cref="AddTrack"/> for every track, then <see cref="WriteSample"/> in interleaved decoding order, then
/// <see cref="Finish"/>. Sample times are in the track's <see cref="CodecConfig.Timescale"/> on the output timeline
/// (time zero is the start of the presentation; earlier samples are pre-roll).
/// </remarks>
public interface IMuxer : IDisposable
{
    /// <summary>Preferred amount of one track's media written contiguously (MP4 chunks); zero for per-sample interleaving.</summary>
    TimeSpan InterleaveDuration { get; }

    /// <summary>
    /// True when video frames decoded before time zero can be hidden (MP4 edit lists). When false, a video track
    /// starting before zero shifts the whole presentation so it starts at zero (as mkvmerge does).
    /// </summary>
    bool SupportsVideoPreRoll { get; }

    /// <summary>Adds a track and returns its index for <see cref="WriteSample"/>.</summary>
    /// <exception cref="NotSupportedException">The codec cannot be stored in this container without conversion.</exception>
    int AddTrack(CodecConfig config, MuxTrackSettings settings);

    void WriteSample(int track, MediaSample sample);

    /// <summary>Writes the indexes and headers; the output is complete afterwards.</summary>
    void Finish(CancellationToken cancellationToken);
}

/// <summary>Opens files of one or more formats as <see cref="IDemuxer"/>s.</summary>
public interface IDemuxerFactory
{
    string Name { get; }

    /// <summary>Confidence (0 = not this format, 100 = certain) that the file is in a format of this factory.</summary>
    int Probe(string path, ReadOnlySpan<byte> header);

    /// <exception cref="InvalidDataException">The file is not valid.</exception>
    IDemuxer Open(string path, DemuxOptions? options = null);
}

/// <summary>Creates muxers of one container family and answers what it can store.</summary>
public interface IMuxerFactory
{
    ContainerKind Kind { get; }

    /// <summary>Whether (and how) a track with <paramref name="config"/> can be written into this container.</summary>
    TrackSupport CheckSupport(CodecConfig config);

    /// <summary>Creates a muxer writing to <paramref name="output"/> (seekable, writable).</summary>
    IMuxer Create(Stream output, MuxerSettings settings);

    /// <summary>
    /// After a remux to <paramref name="path"/>, re-reads the file and points <paramref name="document"/> at it (path,
    /// container, container state, track IDs of <paramref name="written"/>, which are listed in output order). Must
    /// be awaited on the document's (UI) context.
    /// </summary>
    Task AdoptAsync(MediaDocument document, string path, IReadOnlyList<Track> written, CancellationToken cancellationToken);
}
