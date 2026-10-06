using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.Formats.Matroska.Ebml;
using static MMW.Formats.Matroska.MatroskaIds;

namespace MMW.Formats.Matroska;

/// <summary>A top-level element to (re)write: <see cref="Payload"/> null means "remove".</summary>
internal sealed record ElementUpdate(ulong Id, byte[]? Payload);

/// <summary>Works out which top-level elements a document's edits require rewriting, and builds their new payloads.</summary>
internal static class MatroskaUpdateBuilder
{
    /// <summary>Records the document's current editable state as the state of the file.</summary>
    public static void CaptureSnapshot(MediaDocument document, MatroskaLayout layout)
    {
        layout.TagsSnapshot = EncodeGlobalTags(document.Metadata, layout);
        layout.ChaptersSnapshot = MatroskaLayout.CaptureChapters(document.Chapters);
        layout.ArtworksSnapshot = [.. document.Metadata.Artworks];
        foreach (var entry in layout.Tracks)
        {
            if (FindTrack(document, entry.TrackNumber) is { } track)
                entry.Snapshot = TrackEditState.Capture(track);
        }
    }

    /// <summary>
    /// Throws <see cref="NotSupportedException"/> when the document's track list is not the file's (tracks added,
    /// removed or reordered), which requires remuxing.
    /// </summary>
    public static void ValidateTracks(MediaDocument document, MatroskaLayout layout)
    {
        var tracks = document.Tracks.Where(t => t is not ChapterTrack).ToList();
        if (tracks.Any(t => t.IsPending))
            throw new NotSupportedException("Adding tracks to a Matroska file requires remuxing, which is not supported yet.");

        var fileOrder = layout.Tracks.Select(t => t.TrackNumber).ToList();
        var docOrder = tracks.Select(t => (ulong)t.Id).ToList();
        if (docOrder.Count != fileOrder.Count || docOrder.Except(fileOrder).Any() || fileOrder.Except(docOrder).Any())
            throw new NotSupportedException("Removing or replacing tracks of a Matroska file requires remuxing, which is not supported yet.");
        if (!docOrder.SequenceEqual(fileOrder))
            throw new NotSupportedException("Reordering the tracks of a Matroska file requires remuxing, which is not supported yet.");
    }

    /// <summary>Builds the updates needed to bring the file in line with <paramref name="document"/>.</summary>
    public static List<ElementUpdate> Build(MediaDocument document, MatroskaLayout layout, EbmlReader reader)
    {
        var updates = new List<ElementUpdate>();

        // Info: only the title is editable.
        var title = document.Metadata.GetString(TagId.Name) ?? string.Empty;
        if (layout.InfoPayload is not null && title != (layout.Title ?? string.Empty))
        {
            var replacement = title.Length == 0 ? null : EbmlWriter.Element(Title, System.Text.Encoding.UTF8.GetBytes(title));
            var children = EbmlParser.Children(layout.InfoPayload);
            updates.Add(new ElementUpdate(Info, MatroskaTrackWriter.ApplyReplacements(children, new() { [Title] = replacement })));
        }

        // Tracks: unedited entries are copied verbatim.
        if (layout.TracksPayload is not null)
        {
            var changed = false;
            var replacements = new Dictionary<ulong, byte[]>();
            foreach (var entry in layout.Tracks)
            {
                if (FindTrack(document, entry.TrackNumber) is { } track && MatroskaTrackWriter.Rewrite(entry, track) is { } payload)
                {
                    replacements[entry.TrackNumber] = EbmlWriter.Element(TrackEntry, payload);
                    changed = true;
                }
            }

            if (changed)
                updates.Add(new ElementUpdate(Tracks, RewriteTracks(layout.TracksPayload, replacements)));
        }

        // Tags: global tags are regenerated, targeted tags are preserved.
        var tags = EncodeGlobalTags(document.Metadata, layout);
        if (!tags.AsSpan().SequenceEqual(layout.TagsSnapshot))
        {
            var w = new EbmlWriter();
            w.Raw(tags);
            foreach (var preserved in layout.PreservedTags)
                w.Raw(preserved);
            updates.Add(new ElementUpdate(Tags, w.Length == 0 ? null : WithCrc(layout, Tags, w.ToArray())));
        }

        // Chapters.
        var chapters = MatroskaLayout.CaptureChapters(document.Chapters);
        if (!chapters.SequenceEqual(layout.ChaptersSnapshot))
        {
            var payload = chapters.Count == 0 ? null : WithCrc(layout, Chapters, MatroskaChapters.Build(document.Chapters, document.Duration));
            updates.Add(new ElementUpdate(Chapters, payload));
        }

        // Attachments.
        var artworks = document.Metadata.Artworks;
        if (!artworks.SequenceEqual(layout.ArtworksSnapshot, ReferenceEqualityComparer.Instance))
        {
            var payload = MatroskaAttachments.Build(reader, layout, artworks);
            updates.Add(new ElementUpdate(Attachments, payload is null ? null : WithCrc(layout, Attachments, payload)));
        }

        return updates;
    }

    /// <summary>Canonical encoding of the global tags of <paramref name="metadata"/>.</summary>
    public static byte[] EncodeGlobalTags(MetadataSet metadata, MatroskaLayout layout)
    {
        var w = new EbmlWriter();
        foreach (var tag in MatroskaTagMapping.Write(metadata, layout.PreservedBinaryTags))
        {
            if (tag.SimpleTags.Count > 0 || tag.RawSimpleTags.Count > 0)
                tag.Write(w);
        }

        return w.ToArray();
    }

    private static byte[] RewriteTracks(byte[] tracksPayload, Dictionary<ulong, byte[]> replacements)
    {
        var children = EbmlParser.Children(tracksPayload);
        var hadCrc = children.Count > 0 && children[0].Id == Crc32Element;
        var w = new EbmlWriter();
        foreach (var child in children)
        {
            if (child.Id == Crc32Element)
                continue;
            if (child.Id == TrackEntry &&
                replacements.TryGetValue(EbmlParser.Children(child.Data).GetUInt(TrackNumber, 0), out var replacement))
            {
                w.Raw(replacement);
                continue;
            }

            w.Raw(child.Element.Span);
        }

        return hadCrc ? EbmlWriter.WithCrc32(w.WrittenSpan) : w.ToArray();
    }

    private static byte[] WithCrc(MatroskaLayout layout, ulong id, byte[] payload) =>
        layout.ElementsWithCrc.Contains(id) ? EbmlWriter.WithCrc32(payload) : payload;

    private static Track? FindTrack(MediaDocument document, ulong trackNumber) =>
        document.Tracks.FirstOrDefault(t => t is not ChapterTrack && t.Id == trackNumber);
}
