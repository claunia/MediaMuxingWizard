using MMW.Formats.Mp4.Boxes;

namespace MMW.Formats.Mp4;

/// <summary>Per-document state kept by <see cref="Mp4Handler"/> between reading and saving.</summary>
internal sealed class Mp4State
{
    public required Mp4Layout Layout { get; init; }

    /// <summary>The parsed moov box as read from disk.</summary>
    public required Box Moov { get; init; }

    /// <summary>ilst items that are not mapped to tags and are written back unchanged.</summary>
    public List<Box> PreservedItems { get; } = [];

    /// <summary>IDs of tracks that carry chapter text (excluded from the track list).</summary>
    public HashSet<uint> ChapterTrackIds { get; } = [];
}
