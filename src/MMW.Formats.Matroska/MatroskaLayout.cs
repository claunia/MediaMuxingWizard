using MMW.Core.Chapters;
using MMW.Core.Metadata;
using MMW.Core.Model;

namespace MMW.Formats.Matroska;

/// <summary>A direct child of the Segment as found on disk.</summary>
/// <param name="Id">Element ID.</param>
/// <param name="Position">Absolute offset of the element header.</param>
/// <param name="HeaderLength">Size of the ID and size fields.</param>
/// <param name="End">Absolute end offset (resolved by scanning for unknown-size Clusters).</param>
internal readonly record struct TopLevelElement(ulong Id, long Position, int HeaderLength, long End)
{
    public long TotalSize => End - Position;
}

/// <summary>One SeekHead entry.</summary>
/// <param name="Id">Indexed element ID.</param>
/// <param name="Position">Position relative to the start of the Segment data.</param>
internal readonly record struct SeekEntry(ulong Id, long Position);

/// <summary>A parsed SeekHead.</summary>
internal sealed record SeekHeadInfo(long Position, IReadOnlyList<SeekEntry> Entries, bool HasCrc);

/// <summary>The values of a track that the in-place editor can change, used to detect edits.</summary>
internal sealed record TrackEditState(
    string Name,
    string Language,
    bool Enabled,
    bool IsDefault,
    bool IsForced,
    string Characteristics,
    ColorInfo Color,
    HdrInfo? Hdr,
    byte[]? DolbyVisionRecord)
{
    public static TrackEditState Capture(Track track) => new(
        track.Name,
        track.Language,
        track.Enabled,
        track.IsDefault,
        track.IsForced,
        string.Join('|', track.MediaCharacteristics.Order(StringComparer.Ordinal)),
        track is VideoTrack v ? v.Color : ColorInfo.Unspecified,
        track is VideoTrack h ? h.Hdr : null,
        track is VideoTrack d ? d.DolbyVisionRecord : null);
}

/// <summary>A TrackEntry as read from the file.</summary>
internal sealed class TrackEntryState
{
    public required ulong TrackNumber { get; init; }

    public required ulong TrackUid { get; init; }

    /// <summary>Raw TrackEntry payload; unedited children are copied from it verbatim.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>State of the track's editable values as last read/written.</summary>
    public required TrackEditState Snapshot { get; set; }
}

/// <summary>An AttachedFile as read from the file.</summary>
internal sealed class AttachmentState
{
    public required string FileName { get; init; }

    public required string MediaType { get; init; }

    public string? Description { get; init; }

    public required ulong Uid { get; init; }

    /// <summary>Absolute position and length of the whole AttachedFile element (for verbatim copies).</summary>
    public required long ElementPosition { get; init; }

    public required long ElementLength { get; init; }

    /// <summary>The artwork this attachment was exposed as, or null for attachments that are preserved unchanged.</summary>
    public Artwork? Artwork { get; set; }
}

/// <summary>
/// The parsed layout of a Matroska file, stored in <see cref="MediaDocument.ContainerState"/> so the file can be
/// edited in place on save.
/// </summary>
internal sealed class MatroskaLayout
{
    public required string Path { get; init; }

    public required long FileLength { get; init; }

    public required DateTime LastWriteTimeUtc { get; init; }

    public required long SegmentPosition { get; init; }

    public required int SegmentSizeLength { get; init; }

    public required bool SegmentSizeUnknown { get; init; }

    public required long SegmentDataPosition { get; init; }

    /// <summary>End of the Segment (its declared end, or where the scan ended for unknown sizes).</summary>
    public required long SegmentEnd { get; init; }

    /// <summary>
    /// Null when the whole Segment could be scanned; otherwise the reason in-place editing is refused
    /// (for example a truncated or corrupt file).
    /// </summary>
    public string? ScanProblem { get; set; }

    public List<TopLevelElement> Elements { get; } = [];

    public List<SeekHeadInfo> SeekHeads { get; } = [];

    /// <summary>IDs of top-level elements whose first child was a CRC-32.</summary>
    public HashSet<ulong> ElementsWithCrc { get; } = [];

    public ulong TimestampScale { get; set; } = 1_000_000;

    /// <summary>Payload of the first Info element.</summary>
    public byte[]? InfoPayload { get; set; }

    /// <summary>Info Title as read (null when absent).</summary>
    public string? Title { get; set; }

    /// <summary>Payload of the first Tracks element.</summary>
    public byte[]? TracksPayload { get; set; }

    public List<TrackEntryState> Tracks { get; } = [];

    /// <summary>Raw Tag elements that target tracks, editions, chapters or attachments; written back untouched.</summary>
    public List<byte[]> PreservedTags { get; } = [];

    /// <summary>Global SimpleTags holding binary values (TargetTypeValue, raw element); written back untouched.</summary>
    public List<(int TargetTypeValue, byte[] Element)> PreservedBinaryTags { get; } = [];

    public List<AttachmentState> Attachments { get; } = [];

    /// <summary>Global tags as last read/written, encoded canonically; compared on save to detect edits.</summary>
    public byte[] TagsSnapshot { get; set; } = [];

    public List<(TimeSpan Start, string Title)> ChaptersSnapshot { get; set; } = [];

    /// <summary>The artwork list as last read/written; compared by reference on save to detect edits.</summary>
    public List<Artwork> ArtworksSnapshot { get; set; } = [];

    public static List<(TimeSpan Start, string Title)> CaptureChapters(IEnumerable<Chapter> chapters) =>
        chapters.Select(c => (c.Start, c.Title)).ToList();
}
