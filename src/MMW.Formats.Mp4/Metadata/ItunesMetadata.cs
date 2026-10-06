using System.Buffers.Binary;
using System.Text;
using MMW.Core.Metadata;
using MMW.Formats.Mp4.Boxes;

namespace MMW.Formats.Mp4.Metadata;

/// <summary>Reads and writes the iTunes-style <c>moov/udta/meta/ilst</c> item list.</summary>
public static class ItunesMetadata
{
    private enum AtomKind
    {
        Utf8,
        Int8,
        Int16,
        Int32,
        Int64,
        Track,
        Disk,
    }

    private sealed record AtomSpec(string Atom, AtomKind Kind);

    private const string AppleMean = "com.apple.iTunes";

    // Well-known data types (QuickTime File Format, "Well-known types").
    private const int TypeImplicit = 0;
    private const int TypeUtf8 = 1;
    private const int TypeGif = 12;
    private const int TypeJpeg = 13;
    private const int TypePng = 14;
    private const int TypeSigned = 21;
    private const int TypeUnsigned = 22;
    private const int TypeBmp = 27;

    private static readonly Dictionary<TagId, AtomSpec> s_atoms = new()
    {
        [TagId.Name] = new("©nam", AtomKind.Utf8),
        [TagId.TrackSubtitle] = new("©st3", AtomKind.Utf8),
        [TagId.Artist] = new("©ART", AtomKind.Utf8),
        [TagId.AlbumArtist] = new("aART", AtomKind.Utf8),
        [TagId.Album] = new("©alb", AtomKind.Utf8),
        [TagId.Grouping] = new("©grp", AtomKind.Utf8),
        [TagId.Composer] = new("©wrt", AtomKind.Utf8),
        [TagId.Comments] = new("©cmt", AtomKind.Utf8),
        [TagId.Genre] = new("©gen", AtomKind.Utf8),
        [TagId.ReleaseDate] = new("©day", AtomKind.Utf8),
        [TagId.TrackNumber] = new("trkn", AtomKind.Track),
        [TagId.DiskNumber] = new("disk", AtomKind.Disk),
        [TagId.Tempo] = new("tmpo", AtomKind.Int16),
        [TagId.Compilation] = new("cpil", AtomKind.Int8),
        [TagId.Keywords] = new("keyw", AtomKind.Utf8),
        [TagId.Category] = new("catg", AtomKind.Utf8),
        [TagId.MediaKind] = new("stik", AtomKind.Int8),
        [TagId.HdVideo] = new("hdvd", AtomKind.Int8),
        [TagId.Gapless] = new("pgap", AtomKind.Int8),
        [TagId.Podcast] = new("pcst", AtomKind.Int8),
        [TagId.ITunesU] = new("itnu", AtomKind.Int8),
        [TagId.TvShow] = new("tvsh", AtomKind.Utf8),
        [TagId.TvNetwork] = new("tvnn", AtomKind.Utf8),
        [TagId.TvEpisodeId] = new("tven", AtomKind.Utf8),
        [TagId.TvSeason] = new("tvsn", AtomKind.Int32),
        [TagId.TvEpisodeNumber] = new("tves", AtomKind.Int32),
        [TagId.Description] = new("desc", AtomKind.Utf8),
        [TagId.LongDescription] = new("ldes", AtomKind.Utf8),
        [TagId.SeriesDescription] = new("sdes", AtomKind.Utf8),
        [TagId.Lyrics] = new("©lyr", AtomKind.Utf8),
        [TagId.SortName] = new("sonm", AtomKind.Utf8),
        [TagId.SortArtist] = new("soar", AtomKind.Utf8),
        [TagId.SortAlbumArtist] = new("soaa", AtomKind.Utf8),
        [TagId.SortAlbum] = new("soal", AtomKind.Utf8),
        [TagId.SortComposer] = new("soco", AtomKind.Utf8),
        [TagId.SortTvShow] = new("sosn", AtomKind.Utf8),
        [TagId.WorkName] = new("©wrk", AtomKind.Utf8),
        [TagId.MovementName] = new("©mvn", AtomKind.Utf8),
        [TagId.MovementNumber] = new("©mvi", AtomKind.Int16),
        [TagId.MovementCount] = new("©mvc", AtomKind.Int16),
        [TagId.ShowWorkAndMovement] = new("shwm", AtomKind.Int8),
        [TagId.SongDescription] = new("©des", AtomKind.Utf8),
        [TagId.ArtDirector] = new("©ard", AtomKind.Utf8),
        [TagId.Arranger] = new("©arg", AtomKind.Utf8),
        [TagId.Lyricist] = new("©aut", AtomKind.Utf8),
        [TagId.Acknowledgement] = new("©cak", AtomKind.Utf8),
        [TagId.Conductor] = new("©con", AtomKind.Utf8),
        [TagId.LinerNotes] = new("©lnt", AtomKind.Utf8),
        [TagId.RecordCompany] = new("©mak", AtomKind.Utf8),
        [TagId.OriginalArtist] = new("©ope", AtomKind.Utf8),
        [TagId.PhonogramRights] = new("©phg", AtomKind.Utf8),
        [TagId.SongProducer] = new("©prd", AtomKind.Utf8),
        [TagId.Performer] = new("©prf", AtomKind.Utf8),
        [TagId.Publisher] = new("©pub", AtomKind.Utf8),
        [TagId.SoundEngineer] = new("©sne", AtomKind.Utf8),
        [TagId.Soloist] = new("©sol", AtomKind.Utf8),
        [TagId.Credits] = new("©src", AtomKind.Utf8),
        [TagId.Thanks] = new("©thx", AtomKind.Utf8),
        [TagId.OnlineExtras] = new("©url", AtomKind.Utf8),
        [TagId.ExecutiveProducer] = new("©xpd", AtomKind.Utf8),
        [TagId.ContentRating] = new("rtng", AtomKind.Int8),
        [TagId.Copyright] = new("cprt", AtomKind.Utf8),
        [TagId.EncodingTool] = new("©too", AtomKind.Utf8),
        [TagId.EncodedBy] = new("©enc", AtomKind.Utf8),
        [TagId.PurchaseDate] = new("purd", AtomKind.Utf8),
        [TagId.ITunesAccount] = new("apID", AtomKind.Utf8),
        [TagId.ITunesAccountType] = new("akID", AtomKind.Int8),
        [TagId.ITunesCountry] = new("sfID", AtomKind.Int32),
        [TagId.ContentId] = new("cnID", AtomKind.Int32),
        [TagId.ArtistId] = new("atID", AtomKind.Int32),
        [TagId.PlaylistId] = new("plID", AtomKind.Int64),
        [TagId.GenreId] = new("geID", AtomKind.Int32),
        [TagId.ComposerId] = new("cmID", AtomKind.Int32),
        [TagId.Xid] = new("xid ", AtomKind.Utf8),
    };

    /// <summary>Freeform (<c>----</c>, mean com.apple.iTunes) names mapped to plain string tags.</summary>
    private static readonly Dictionary<TagId, string> s_freeform = new()
    {
        [TagId.AudiobookSubtitle] = "SUBTITLE",
        [TagId.AudiobookLanguage] = "LANGUAGE",
        [TagId.Asin] = "ASIN",
        [TagId.Abridged] = "ABRIDGED",
    };

    /// <summary>iTunMOVI plist keys for people tags.</summary>
    private static readonly (TagId Id, string Key)[] s_movi =
    [
        (TagId.Cast, "cast"),
        (TagId.Director, "directors"),
        (TagId.Codirector, "codirectors"),
        (TagId.Producers, "producers"),
        (TagId.Screenwriters, "screenwriters"),
        (TagId.Studio, "studio"),
    ];

    private static readonly Dictionary<string, TagId> s_byAtom = s_atoms.ToDictionary(kv => kv.Value.Atom, kv => kv.Key);

    /// <summary>Reads items into <paramref name="set"/>. Items that cannot be represented are added to <paramref name="preserved"/>.</summary>
    public static void Read(Box ilst, MetadataSet set, List<Box> preserved)
    {
        ArgumentNullException.ThrowIfNull(ilst);
        foreach (var item in ilst.Children ?? [])
        {
            if (!TryReadItem(item, set))
                preserved.Add(item);
        }
    }

    private static bool TryReadItem(Box item, MetadataSet set)
    {
        var dataBoxes = item.FindAll("data").ToList();
        if (item.Type == "covr")
        {
            foreach (var d in dataBoxes)
            {
                var (_, image) = SplitData(d);
                if (image.Count > 0)
                    set.Artworks.Add(new Artwork(image.ToArray()));
            }

            return true;
        }

        if (item.Type == "----")
            return TryReadFreeform(item, set);

        if (dataBoxes.Count == 0)
            return false;
        var (type, segment) = SplitData(dataBoxes[0]);
        ReadOnlySpan<byte> data = segment;

        if (item.Type == "gnre")
        {
            if (data.Length >= 2 && !set.Contains(TagId.Genre))
                set.Set(TagId.Genre, Id3Genres.FromIndex(BinaryPrimitives.ReadUInt16BigEndian(data)));
            return true;
        }

        if (!s_byAtom.TryGetValue(item.Type, out var id))
            return false;

        var spec = s_atoms[id];
        object? value = spec.Kind switch
        {
            AtomKind.Utf8 => type == TypeUtf8 || type == TypeImplicit ? Encoding.UTF8.GetString(data).TrimEnd('\0') : null,
            AtomKind.Track or AtomKind.Disk => data.Length >= 6
                ? new IntPair(BinaryPrimitives.ReadUInt16BigEndian(data[2..]), BinaryPrimitives.ReadUInt16BigEndian(data[4..]))
                : null,
            _ => ReadInteger(data, type),
        };

        if (value is null)
            return false;

        var kind = TagCatalog.Get(id).Kind;
        if (kind == TagValueKind.Bool && value is long b)
            value = b != 0;
        else if (value is long l)
            value = (int)l;

        set.Set(id, value);
        return true;
    }

    private static bool TryReadFreeform(Box item, MetadataSet set)
    {
        var mean = ReadFullBoxString(item.Find("mean"));
        var name = ReadFullBoxString(item.Find("name"));
        var data = item.Find("data");
        if (mean is null || name is null || data is null)
            return false;

        var (type, bytes) = SplitData(data);
        if (type is not (TypeUtf8 or TypeImplicit))
            return false;
        var text = Encoding.UTF8.GetString(bytes.AsSpan()).TrimEnd('\0');

        if (mean == AppleMean)
        {
            switch (name)
            {
                case "iTunEXTC":
                    ReadRating(text, set);
                    return true;
                case "iTunMOVI":
                    ReadMovi(text, set);
                    return true;
            }

            foreach (var (id, ffName) in s_freeform)
            {
                if (ffName == name)
                {
                    if (TagCatalog.Get(id).Kind == TagValueKind.Bool)
                        set.Set(id, text is "1" or "true" or "Yes" or "yes");
                    else
                        set.Set(id, text);
                    return true;
                }
            }
        }

        set.CustomItems[$"----:{mean}:{name}"] = text;
        return true;
    }

    private static void ReadRating(string text, MetadataSet set)
    {
        var parts = text.Split('|');
        if (parts.Length >= 3)
        {
            set.Set(TagId.Rating, $"{parts[0]}|{parts[1]}|{parts[2]}|");
            if (parts.Length >= 4 && parts[3].Length > 0)
                set.Set(TagId.RatingAnnotation, parts[3]);
        }
        else if (text.Length > 0)
        {
            set.Set(TagId.Rating, text);
        }
    }

    private static void ReadMovi(string xml, MetadataSet set)
    {
        var dict = PropertyList.ParseDictionary(xml);
        foreach (var (id, key) in s_movi)
        {
            if (!dict.TryGetValue(key, out var value))
                continue;
            set.Set(id, value switch
            {
                string[] names when TagCatalog.Get(id).Kind == TagValueKind.String => string.Join(", ", names),
                _ => value,
            });
        }
    }

    private static long? ReadInteger(ReadOnlySpan<byte> data, int type)
    {
        if (type is not (TypeSigned or TypeUnsigned or TypeImplicit))
            return null;
        var unsigned = type == TypeUnsigned;
        return data.Length switch
        {
            1 => unsigned ? data[0] : (sbyte)data[0],
            2 => unsigned ? BinaryPrimitives.ReadUInt16BigEndian(data) : BinaryPrimitives.ReadInt16BigEndian(data),
            3 => (data[0] << 16) | (data[1] << 8) | data[2],
            4 => unsigned ? BinaryPrimitives.ReadUInt32BigEndian(data) : BinaryPrimitives.ReadInt32BigEndian(data),
            8 => BinaryPrimitives.ReadInt64BigEndian(data),
            _ => null,
        };
    }

    private static (int Type, ArraySegment<byte> Value) SplitData(Box data)
    {
        var p = data.Payload;
        if (p.Length < 8)
            return (-1, ArraySegment<byte>.Empty);
        var type = (p[1] << 16) | (p[2] << 8) | p[3];
        return (type, new ArraySegment<byte>(p, 8, p.Length - 8));
    }

    private static string? ReadFullBoxString(Box? box) =>
        box is { Payload.Length: >= 4 } ? Encoding.UTF8.GetString(box.Payload, 4, box.Payload.Length - 4).TrimEnd('\0') : null;

    // ------------------------------------------------------------------ writing

    /// <summary>Builds a new <c>ilst</c> box from the metadata set plus preserved raw items.</summary>
    public static Box Build(MetadataSet set, IEnumerable<Box> preserved)
    {
        ArgumentNullException.ThrowIfNull(set);
        var items = new List<Box>();

        foreach (var id in set.Keys)
        {
            if (s_atoms.TryGetValue(id, out var spec) && set[id] is { } value)
                items.Add(new Box(spec.Atom, null, [DataBox(spec.Kind, value)]));
        }

        if (set.GetString(TagId.Rating) is { Length: > 0 } rating)
        {
            var parts = rating.Split('|');
            var annotation = set.GetString(TagId.RatingAnnotation) ?? string.Empty;
            var full = parts.Length >= 3 ? $"{parts[0]}|{parts[1]}|{parts[2]}|{annotation}" : rating;
            items.Add(Freeform(AppleMean, "iTunEXTC", full));
        }

        var movi = new List<KeyValuePair<string, object>>();
        foreach (var (id, key) in s_movi)
        {
            switch (set[id])
            {
                case IReadOnlyList<string> { Count: > 0 } list:
                    movi.Add(new(key, list));
                    break;
                case string s when s.Length > 0:
                    movi.Add(new(key, s));
                    break;
            }
        }

        if (movi.Count > 0)
            items.Add(Freeform(AppleMean, "iTunMOVI", PropertyList.BuildDictionary(movi)));

        foreach (var (id, name) in s_freeform)
        {
            if (set[id] is { } value)
                items.Add(Freeform(AppleMean, name, value is bool b ? (b ? "1" : "0") : MetadataSet.FormatValue(id, value)));
        }

        foreach (var (key, value) in set.CustomItems)
        {
            if (key.StartsWith("----:", StringComparison.Ordinal))
            {
                var rest = key[5..];
                var sep = rest.LastIndexOf(':');
                if (sep > 0)
                    items.Add(Freeform(rest[..sep], rest[(sep + 1)..], value));
            }
        }

        if (set.Artworks.Count > 0)
        {
            var covr = new Box("covr", null, []);
            foreach (var art in set.Artworks)
            {
                var type = art.Format switch
                {
                    ArtworkFormat.Png => TypePng,
                    ArtworkFormat.Bmp => TypeBmp,
                    ArtworkFormat.Gif => TypeGif,
                    _ => TypeJpeg,
                };
                covr.Children!.Add(new Box("data", new PayloadBuilder().U32((uint)type).U32(0).Bytes(art.Data).ToArray()));
            }

            items.Add(covr);
        }

        // Items we could not interpret are written back unchanged, unless we now write the same atom ourselves.
        var written = items.Where(i => i.Type != "----").Select(i => i.Type).ToHashSet();
        foreach (var raw in preserved)
        {
            if (!written.Contains(raw.Type))
                items.Add(raw);
        }

        return new Box("ilst", null, items);
    }

    private static Box DataBox(AtomKind kind, object value)
    {
        var b = new PayloadBuilder();
        switch (kind)
        {
            case AtomKind.Utf8:
                b.U32(TypeUtf8).U32(0).Utf8(value is IReadOnlyList<string> l ? string.Join(", ", l) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                break;
            case AtomKind.Track:
            {
                var p = (IntPair)value;
                b.U32(TypeImplicit).U32(0).U16(0).U16(p.Number).U16(p.Total).U16(0);
                break;
            }

            case AtomKind.Disk:
            {
                var p = (IntPair)value;
                b.U32(TypeImplicit).U32(0).U16(0).U16(p.Number).U16(p.Total);
                break;
            }

            case AtomKind.Int8:
                b.U32(TypeSigned).U32(0).U8((int)ToLong(value));
                break;
            case AtomKind.Int16:
                b.U32(TypeSigned).U32(0).U16((int)ToLong(value));
                break;
            case AtomKind.Int32:
                b.U32(TypeSigned).U32(0).I32((int)ToLong(value));
                break;
            case AtomKind.Int64:
                b.U32(TypeSigned).U32(0).U64((ulong)ToLong(value));
                break;
        }

        return new Box("data", b.ToArray());
    }

    private static long ToLong(object value) => value switch
    {
        bool b => b ? 1 : 0,
        int i => i,
        long l => l,
        _ => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static Box Freeform(string mean, string name, string value) => new("----", null,
    [
        new Box("mean", new PayloadBuilder().FullBox(0, 0).Utf8(mean).ToArray()),
        new Box("name", new PayloadBuilder().FullBox(0, 0).Utf8(name).ToArray()),
        new Box("data", new PayloadBuilder().U32(TypeUtf8).U32(0).Utf8(value).ToArray()),
    ]);
}
