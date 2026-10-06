using MMW.Core.Metadata;
using MMW.Formats.Matroska.Ebml;
using static MMW.Formats.Matroska.MatroskaIds;

namespace MMW.Formats.Matroska;

/// <summary>Reads and builds the Attachments element. Image attachments are exposed as artwork.</summary>
internal static class MatroskaAttachments
{
    private static readonly string[] s_imageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"];

    /// <summary>
    /// Reads the AttachedFile children of an Attachments element directly from the file. Only image data is loaded;
    /// other attachments are recorded by position so they can be copied back verbatim.
    /// </summary>
    public static void Read(EbmlReader reader, EbmlElementHeader attachments, MatroskaLayout layout, List<Artwork> artworks)
    {
        foreach (var file in reader.Children(attachments))
        {
            if (file.Id != AttachedFile)
                continue;

            string name = string.Empty, mime = string.Empty;
            string? description = null;
            ulong uid = 0;
            EbmlElementHeader? data = null;
            foreach (var child in reader.Children(file))
            {
                switch (child.Id)
                {
                    case FileName:
                        name = EbmlParser.ReadString(reader.ReadData(child));
                        break;
                    case FileMediaType:
                        mime = EbmlParser.ReadString(reader.ReadData(child));
                        break;
                    case FileDescription:
                        description = EbmlParser.ReadString(reader.ReadData(child));
                        break;
                    case FileUid:
                        uid = EbmlParser.ReadUInt(reader.ReadData(child));
                        break;
                    case FileData:
                        data = child;
                        break;
                }
            }

            var state = new AttachmentState
            {
                FileName = name,
                MediaType = mime,
                Description = description,
                Uid = uid,
                ElementPosition = file.Position,
                ElementLength = file.End - file.Position,
            };

            if (data is { } d && IsImage(name, mime))
            {
                var artwork = new Artwork(reader.ReadData(d)) { FileName = name };
                state.Artwork = artwork;
                artworks.Add(artwork);
            }

            layout.Attachments.Add(state);
        }
    }

    private static bool IsImage(string name, string mime) =>
        mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
        s_imageExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    /// <summary>
    /// Builds the Attachments payload: preserved (non-artwork) attachments copied verbatim from the file, followed by
    /// the artwork. Returns null when there is nothing to attach.
    /// </summary>
    public static byte[]? Build(EbmlReader reader, MatroskaLayout layout, IReadOnlyList<Artwork> artworks)
    {
        var w = new EbmlWriter();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedUids = new HashSet<ulong>();
        foreach (var preserved in layout.Attachments.Where(a => a.Artwork is null))
        {
            w.Raw(reader.ReadBytes(preserved.ElementPosition, preserved.ElementLength));
            usedNames.Add(preserved.FileName);
            usedUids.Add(preserved.Uid);
        }

        for (var i = 0; i < artworks.Count; i++)
        {
            var artwork = artworks[i];
            // Unchanged artwork keeps its name, media type and UID; new artwork gets a conventional cover name.
            var existing = layout.Attachments.FirstOrDefault(a => ReferenceEquals(a.Artwork, artwork));
            var mediaType = existing?.MediaType is { Length: > 0 } mt ? mt : artwork.MimeType;
            var name = existing?.FileName is { Length: > 0 } en
                ? en
                : artwork.FileName is { Length: > 0 } fn && Path.GetExtension(fn).Equals(artwork.Extension, StringComparison.OrdinalIgnoreCase)
                    ? fn
                    : DefaultName(i, artwork.Extension);
            var baseName = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            for (var n = 2; usedNames.Contains(name); n++)
                name = $"{baseName}_{n}{extension}";
            usedNames.Add(name);

            var uid = existing?.Uid ?? 0;
            while (uid == 0 || usedUids.Contains(uid))
                uid = MatroskaChapters.RandomUid();
            usedUids.Add(uid);

            w.Master(AttachedFile, f =>
            {
                if (existing?.Description is { Length: > 0 } description)
                    f.String(FileDescription, description);
                f.String(FileName, name);
                f.String(FileMediaType, mediaType);
                f.Binary(FileData, artwork.Data);
                f.UInt(FileUid, uid);
            });
        }

        return w.Length == 0 ? null : w.ToArray();
    }

    /// <summary>Attachment names recognised by players as cover art (first one is the main cover).</summary>
    private static string DefaultName(int index, string extension) => index switch
    {
        0 => "cover" + extension,
        1 => "cover_land" + extension,
        2 => "small_cover" + extension,
        3 => "small_cover_land" + extension,
        _ => $"cover_{index + 1}{extension}",
    };
}
