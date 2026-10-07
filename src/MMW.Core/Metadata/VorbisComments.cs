using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MMW.Core.Metadata;

/// <summary>
/// Vorbis comments (FLAC VORBIS_COMMENT blocks, Ogg comment headers) and FLAC PICTURE blocks as a
/// <see cref="MetadataSet"/>, using the field names of the Xiph recommendations and of common taggers (MusicBrainz
/// Picard, foobar2000). Repeated fields are joined; unknown fields are ignored. Ogg streams carry their pictures as
/// comments too: METADATA_BLOCK_PICTURE (a base64 FLAC PICTURE block) or the older COVERART (a base64 image).
/// </summary>
public static class VorbisComments
{
    private static readonly Dictionary<string, TagId> s_fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TITLE"] = TagId.Name,
        ["SUBTITLE"] = TagId.TrackSubtitle,
        ["ARTIST"] = TagId.Artist,
        ["ALBUMARTIST"] = TagId.AlbumArtist,
        ["ALBUM ARTIST"] = TagId.AlbumArtist,
        ["ALBUM_ARTIST"] = TagId.AlbumArtist,
        ["ALBUM"] = TagId.Album,
        ["GROUPING"] = TagId.Grouping,
        ["CONTENTGROUP"] = TagId.Grouping,
        ["COMPOSER"] = TagId.Composer,
        ["COMMENT"] = TagId.Comments,
        ["DESCRIPTION"] = TagId.Comments,
        ["GENRE"] = TagId.Genre,
        ["DATE"] = TagId.ReleaseDate,
        ["YEAR"] = TagId.ReleaseDate,
        ["BPM"] = TagId.Tempo,
        ["COMPILATION"] = TagId.Compilation,
        ["LYRICS"] = TagId.Lyrics,
        ["UNSYNCEDLYRICS"] = TagId.Lyrics,
        ["COPYRIGHT"] = TagId.Copyright,
        ["ENCODER"] = TagId.EncodingTool,
        ["ENCODED-BY"] = TagId.EncodedBy,
        ["ENCODEDBY"] = TagId.EncodedBy,
        ["LABEL"] = TagId.RecordCompany,
        ["ORGANIZATION"] = TagId.RecordCompany,
        ["PUBLISHER"] = TagId.Publisher,
        ["CONDUCTOR"] = TagId.Conductor,
        ["ARRANGER"] = TagId.Arranger,
        ["LYRICIST"] = TagId.Lyricist,
        ["PERFORMER"] = TagId.Performer,
        ["PRODUCER"] = TagId.SongProducer,
        ["ENGINEER"] = TagId.SoundEngineer,
        ["TITLESORT"] = TagId.SortName,
        ["ARTISTSORT"] = TagId.SortArtist,
        ["ALBUMARTISTSORT"] = TagId.SortAlbumArtist,
        ["ALBUMSORT"] = TagId.SortAlbum,
        ["COMPOSERSORT"] = TagId.SortComposer,
        ["WORK"] = TagId.WorkName,
        ["MOVEMENTNAME"] = TagId.MovementName,
        ["MOVEMENT"] = TagId.MovementNumber,
        ["MOVEMENTTOTAL"] = TagId.MovementCount,
    };

    /// <summary>Parses a Vorbis comment structure (vendor, then length-prefixed "NAME=value" fields, little-endian).</summary>
    /// <returns>The vendor string and the fields in order; empty when malformed.</returns>
    public static (string Vendor, List<KeyValuePair<string, string>> Fields) Parse(ReadOnlySpan<byte> data)
    {
        var fields = new List<KeyValuePair<string, string>>();
        if (data.Length < 8)
            return (string.Empty, fields);
        var vendorLength = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (vendorLength < 0 || vendorLength > data.Length - 8)
            return (string.Empty, fields);
        var vendor = Encoding.UTF8.GetString(data.Slice(4, vendorLength));
        var pos = 4 + vendorLength;
        var count = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
        pos += 4;
        for (var i = 0; i < count && pos + 4 <= data.Length; i++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
            pos += 4;
            if (length < 0 || length > data.Length - pos)
                break;
            var field = Encoding.UTF8.GetString(data.Slice(pos, length));
            pos += length;
            var eq = field.IndexOf('=');
            if (eq > 0)
                fields.Add(new(field[..eq], field[(eq + 1)..]));
        }

        return (vendor, fields);
    }

    /// <summary>The metadata of <paramref name="fields"/> and <paramref name="pictures"/> (FLAC PICTURE block bodies).</summary>
    public static MetadataSet ToMetadata(IEnumerable<KeyValuePair<string, string>> fields, IEnumerable<byte[]>? pictures = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var metadata = new MetadataSet();
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in fields)
        {
            if (value.Trim().Length == 0)
                continue;
            if (!values.TryGetValue(name, out var list))
                values[name] = list = [];
            list.Add(value.Trim());
        }

        string? First(params string[] names) => names.Select(n => values.GetValueOrDefault(n)?[0]).FirstOrDefault(v => v is not null);

        foreach (var (name, list) in values)
        {
            if (!s_fields.TryGetValue(name, out var id) || metadata.Contains(id))
                continue;
            try
            {
                var kind = TagCatalog.Get(id).Kind;
                object value = kind == TagValueKind.StringList ? list : kind is TagValueKind.String or TagValueKind.Text ? string.Join("; ", list) : list[0];
                if (kind == TagValueKind.Bool)
                    value = list[0] is "1" or "true" or "TRUE" or "yes";
                metadata.Set(id, value);
            }
            catch (FormatException)
            {
                // Not a number where one is expected (BPM "fast", MOVEMENT "II"): skipped.
            }
        }

        if (Pair(First("TRACKNUMBER"), First("TRACKTOTAL", "TOTALTRACKS")) is { } track)
            metadata.Set(TagId.TrackNumber, track);
        if (Pair(First("DISCNUMBER"), First("DISCTOTAL", "TOTALDISCS")) is { } disc)
            metadata.Set(TagId.DiskNumber, disc);

        // The front cover first, then the other pictures in order.
        var images = (pictures ?? []).Select(ParsePicture).ToList();
        foreach (var encoded in values.GetValueOrDefault("METADATA_BLOCK_PICTURE") ?? [])
        {
            if (FromBase64(encoded) is { } block)
                images.Add(ParsePicture(block));
        }

        foreach (var encoded in values.GetValueOrDefault("COVERART") ?? [])
        {
            if (FromBase64(encoded) is { } image)
                images.Add((3, image));
        }

        images.RemoveAll(p => p.Data.Length == 0);
        foreach (var picture in images.OrderBy(p => p.Type == 3 ? 0 : 1))
            metadata.Artworks.Add(new Artwork(picture.Data));
        return metadata;
    }

    private static byte[]? FromBase64(string text)
    {
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>"3", "3/12" with an optional separate total → (3, 12).</summary>
    private static IntPair? Pair(string? number, string? total)
    {
        if (number is null)
            return null;
        var parts = number.Split('/', 2, StringSplitOptions.TrimEntries);
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return null;
        var t = 0;
        if (parts.Length == 2)
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out t);
        else if (total is not null)
            int.TryParse(total, NumberStyles.Integer, CultureInfo.InvariantCulture, out t);
        return new IntPair(n, Math.Max(0, t));
    }

    /// <summary>A FLAC PICTURE block body (also the base64 METADATA_BLOCK_PICTURE comment): its type and image data.</summary>
    public static (int Type, byte[] Data) ParsePicture(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            var span = body.AsSpan();
            var type = (int)BinaryPrimitives.ReadUInt32BigEndian(span);
            var pos = 4;
            pos += 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(span[pos..]); // MIME type
            pos += 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(span[pos..]); // description
            pos += 16; // width, height, depth, colours
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(span[pos..]);
            pos += 4;
            return length > 0 && pos + length <= body.Length ? (type, body.AsSpan(pos, length).ToArray()) : (type, []);
        }
        catch (ArgumentOutOfRangeException)
        {
            return (0, []);
        }
    }
}
