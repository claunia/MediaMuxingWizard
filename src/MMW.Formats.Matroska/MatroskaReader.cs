using System.Globalization;
using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.Formats.Matroska.Ebml;
using MMW.Formats.Matroska.Resources;
using static MMW.Formats.Matroska.MatroskaIds;

namespace MMW.Formats.Matroska;

/// <summary>Parses a Matroska/WebM file into a <see cref="MediaDocument"/> and its <see cref="MatroskaLayout"/>.</summary>
internal static class MatroskaReader
{
    /// <summary>Largest metadata element (anything but Clusters, Cues and Attachments) that will be loaded.</summary>
    private const long MaxMetadataElementSize = 256L * 1024 * 1024;

    public static (MediaDocument Document, MatroskaLayout Layout) Read(string path, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        var reader = new EbmlReader(handle);

        var segment = ReadSegmentHeader(reader);
        var layout = Scan(reader, path, fileInfo.LastWriteTimeUtc, segment, cancellationToken);

        var document = new MediaDocument(path, ContainerKind.Matroska) { FileSize = reader.Length };
        var artworks = new List<Artwork>();
        var globalTags = new List<MatroskaTag>();
        var trackTags = new List<MatroskaTag>();
        ReadOnlyMemory<byte>? chapters = null;
        double? duration = null;
        var seen = new HashSet<ulong>();

        foreach (var element in ElementsToLoad(reader, layout))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = seen.Add(element.Id);
            if (element.Id == Attachments)
            {
                MatroskaAttachments.Read(reader, element, layout, artworks);
                continue;
            }

            if (element.Size > MaxMetadataElementSize)
                throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_ImplausiblyLargeElement, element.Id, element.Position, element.Size));

            var payload = reader.ReadData(element);
            var children = EbmlParser.Children(payload);
            if (children.Count > 0 && children[0].Id == Crc32Element)
                layout.ElementsWithCrc.Add(element.Id);

            switch (element.Id)
            {
                case SeekHead:
                    layout.SeekHeads.Add(ParseSeekHead(element.Position, children));
                    break;
                case Info when first:
                    layout.InfoPayload = payload;
                    layout.TimestampScale = children.GetUInt(TimestampScale, 1_000_000);
                    if (layout.TimestampScale == 0)
                        layout.TimestampScale = 1_000_000;
                    duration = children.GetFloat(Duration);
                    layout.Title = children.GetString(Title);
                    break;
                case Tracks when first:
                    layout.TracksPayload = payload;
                    foreach (var entry in children.Where(c => c.Id == TrackEntry))
                    {
                        var track = MatroskaTrackParser.Parse(entry.Data, path);
                        var entryChildren = EbmlParser.Children(entry.Data);
                        document.Tracks.Add(track);
                        layout.Tracks.Add(new TrackEntryState
                        {
                            TrackNumber = entryChildren.GetUInt(TrackNumber, 0),
                            TrackUid = entryChildren.GetUInt(TrackUid, 0),
                            Payload = entry.Data.ToArray(),
                            Snapshot = TrackEditState.Capture(track),
                        });
                    }

                    break;
                case Chapters when first:
                    chapters = payload;
                    break;
                case Tags:
                    foreach (var tagElement in children.Where(c => c.Id == Tag))
                    {
                        var tag = MatroskaTag.Parse(tagElement.Data);
                        if (tag.IsGlobal)
                        {
                            globalTags.Add(tag);
                        }
                        else
                        {
                            layout.PreservedTags.Add(tagElement.Element.ToArray());
                            trackTags.Add(tag);
                        }
                    }

                    break;
            }
        }

        if (duration is { } d && d > 0)
            document.Duration = TimeSpan.FromTicks((long)(d * layout.TimestampScale / 100));

        foreach (var track in document.Tracks)
            track.Duration = document.Duration;
        ApplyStatistics(document, layout, trackTags);

        MatroskaTagMapping.Read(globalTags, document.Metadata, layout.PreservedBinaryTags);
        if (!document.Metadata.Contains(TagId.Name) && !string.IsNullOrWhiteSpace(layout.Title))
            document.Metadata.Set(TagId.Name, layout.Title);
        document.Metadata.Artworks.AddRange(artworks);

        if (chapters is { } chapterPayload)
        {
            foreach (var chapter in MatroskaChapters.Read(chapterPayload))
                document.Chapters.Add(chapter);
        }

        MatroskaUpdateBuilder.CaptureSnapshot(document, layout);
        document.ContainerState = layout;
        document.IsDirty = false;
        return (document, layout);
    }

    private static EbmlElementHeader ReadSegmentHeader(EbmlReader reader)
    {
        if (!reader.TryReadHeader(0, reader.Length, out var ebml) || ebml.Id != EbmlHeader || ebml.IsUnknownSize)
            throw new InvalidDataException(Strings.Error_NotEbml);

        var header = EbmlParser.Children(reader.ReadData(ebml));
        var docType = header.GetString(DocType) ?? "matroska";
        if (docType is not ("matroska" or "webm"))
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_UnsupportedDocType, docType));
        if (header.GetUInt(EbmlMaxIdLength, 4) > 4 || header.GetUInt(EbmlMaxSizeLength, 8) > 8)
            throw new InvalidDataException(Strings.Error_LongIdsOrSizes);

        var pos = ebml.End;
        while (reader.TryReadHeader(pos, reader.Length, out var h))
        {
            if (h.Id == Segment)
                return h;
            if (h.Id != VoidElement || h.IsUnknownSize)
                break;
            pos = h.End;
        }

        throw new InvalidDataException(Strings.Error_NoSegment);
    }

    /// <summary>Walks the direct children of the Segment, skipping Clusters by their size.</summary>
    private static MatroskaLayout Scan(EbmlReader reader, string path, DateTime lastWrite, EbmlElementHeader segment, CancellationToken ct)
    {
        string? problem = null;
        var limit = segment.IsUnknownSize ? reader.Length : segment.End;
        if (limit > reader.Length)
        {
            problem = Strings.ScanProblem_Truncated;
            limit = reader.Length;
        }

        var elements = new List<TopLevelElement>();
        var pos = segment.DataPosition;
        var iterations = 0;
        while (pos < limit)
        {
            if ((++iterations & 0xFF) == 0)
                ct.ThrowIfCancellationRequested();

            if (!reader.TryReadHeader(pos, limit, out var h))
            {
                problem ??= string.Format(CultureInfo.CurrentCulture, Strings.ScanProblem_InvalidElement, pos);
                break;
            }

            if (h.Id is EbmlHeader or Segment)
            {
                // A following segment: only legal after an unknown-size segment.
                if (segment.IsUnknownSize)
                    limit = pos;
                else
                    problem ??= string.Format(CultureInfo.CurrentCulture, h.Id == Segment ? Strings.ScanProblem_UnexpectedSegment : Strings.ScanProblem_UnexpectedEbmlHeader, pos);
                break;
            }

            long end;
            if (h.IsUnknownSize)
            {
                if (h.Id != Cluster)
                {
                    problem ??= string.Format(CultureInfo.CurrentCulture, Strings.Error_ElementUnknownSize, h.Id, pos);
                    break;
                }

                end = ScanUnknownSizeCluster(reader, h, limit);
            }
            else
            {
                end = h.End;
            }

            if (end > limit)
            {
                problem ??= string.Format(CultureInfo.CurrentCulture, Strings.Error_ElementPastEndOfSegment, h.Id, pos);
                break;
            }

            elements.Add(new TopLevelElement(h.Id, h.Position, h.HeaderLength, end));
            pos = end;
        }

        var layout = new MatroskaLayout
        {
            Path = path,
            FileLength = reader.Length,
            LastWriteTimeUtc = lastWrite,
            SegmentPosition = segment.Position,
            SegmentSizeLength = segment.SizeLength,
            SegmentSizeUnknown = segment.IsUnknownSize,
            SegmentDataPosition = segment.DataPosition,
            SegmentEnd = problem is null ? (segment.IsUnknownSize ? pos : segment.End) : limit,
            ScanProblem = problem,
        };
        layout.Elements.AddRange(elements);
        return layout;
    }

    /// <summary>Finds the end of an unknown-size Cluster: the first element that cannot be a Cluster child.</summary>
    private static long ScanUnknownSizeCluster(EbmlReader reader, EbmlElementHeader cluster, long limit)
    {
        var pos = cluster.DataPosition;
        while (pos < limit)
        {
            if (!reader.TryReadHeader(pos, limit, out var child) || child.IsUnknownSize)
                return pos;
            if (IsTopLevel(child.Id) || child.Id is EbmlHeader or Segment)
                return pos;
            pos = Math.Min(child.End, limit);
        }

        return limit;
    }

    /// <summary>Metadata elements found by the scan, plus SeekHead targets beyond it when the scan stopped early.</summary>
    private static IEnumerable<EbmlElementHeader> ElementsToLoad(EbmlReader reader, MatroskaLayout layout)
    {
        static bool Wanted(ulong id) => id is SeekHead or Info or Tracks or Chapters or Tags or Attachments;

        var found = new HashSet<long>();
        var queue = new Queue<long>();
        foreach (var e in layout.Elements)
        {
            if (Wanted(e.Id))
                queue.Enqueue(e.Position);
        }

        while (queue.Count > 0)
        {
            var position = queue.Dequeue();
            if (!found.Add(position) || !reader.TryReadHeader(position, layout.SegmentEnd, out var h) || h.IsUnknownSize || h.End > reader.Length)
                continue;

            yield return h;

            // SeekHeads are processed before the rest is yielded, so follow their entries when the scan was partial.
            if (h.Id == SeekHead && layout.ScanProblem is not null && layout.SeekHeads.Count > 0)
            {
                foreach (var entry in layout.SeekHeads[^1].Entries)
                {
                    var target = layout.SegmentDataPosition + entry.Position;
                    if (Wanted(entry.Id) && !found.Contains(target) && layout.Elements.All(e => e.Position != target))
                        queue.Enqueue(target);
                }
            }
        }
    }

    private static SeekHeadInfo ParseSeekHead(long position, List<EbmlChild> children)
    {
        var entries = new List<SeekEntry>();
        foreach (var seek in children.Where(c => c.Id == Seek))
        {
            var s = EbmlParser.Children(seek.Data);
            if (s.Child(SeekId) is not { } id || s.Child(SeekPosition) is not { } seekPos || id.Data.Length is 0 or > 4)
                continue;
            entries.Add(new SeekEntry(EbmlParser.ReadUInt(id.Data.Span), (long)seekPos.UInt));
        }

        return new SeekHeadInfo(position, entries, children.Count > 0 && children[0].Id == Crc32Element);
    }

    /// <summary>Uses the statistics tags written by mkvmerge/ffmpeg (DURATION, BPS, NUMBER_OF_BYTES) when present.</summary>
    private static void ApplyStatistics(MediaDocument document, MatroskaLayout layout, List<MatroskaTag> trackTags)
    {
        foreach (var tag in trackTags)
        {
            foreach (var uid in tag.TrackUids)
            {
                var number = layout.Tracks.FirstOrDefault(t => t.TrackUid == uid)?.TrackNumber;
                var track = document.Tracks.FirstOrDefault(t => t.Id == number);
                if (track is null)
                    continue;
                foreach (var simple in tag.SimpleTags)
                {
                    switch (simple.Name.ToUpperInvariant())
                    {
                        case "DURATION" when TimeSpan.TryParse(TrimFraction(simple.Value), CultureInfo.InvariantCulture, out var duration):
                            track.Duration = duration;
                            break;
                        case "BPS" when long.TryParse(simple.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bps):
                            track.Bitrate = bps;
                            break;
                        case "NUMBER_OF_BYTES" when long.TryParse(simple.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes):
                            track.DataLength = bytes;
                            break;
                    }
                }
            }
        }
    }

    /// <summary>"00:01:02.123456789" has more fractional digits than <see cref="TimeSpan"/> accepts.</summary>
    private static string? TrimFraction(string? value)
    {
        if (value is null)
            return null;
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        return dot >= 0 && value.Length - dot - 1 > 7 ? value[..(dot + 8)] : value;
    }
}
