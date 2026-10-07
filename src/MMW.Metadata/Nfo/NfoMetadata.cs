using System.Globalization;
using System.Text;
using System.Xml.Linq;
using System.Xml;
using MMW.Core.Metadata;
using MMW.Metadata.Certifications;
using MMW.Metadata.Mapping;
using MMW.Metadata.Resources;
using MMW.Metadata.Search;

namespace MMW.Metadata.Nfo;

/// <summary>
/// Import and export of Kodi <c>.nfo</c> files (<c>&lt;movie&gt;</c>, <c>&lt;episodedetails&gt;</c> and
/// <c>&lt;tvshow&gt;</c>).
/// </summary>
public static class NfoMetadata
{
    /// <summary>Provider name used for results created from NFO files.</summary>
    public const string ProviderName = "NFO";

    /// <summary>Parses NFO XML into a token result (same shape as an online search result).</summary>
    /// <param name="xml">NFO text. Kodi allows a URL after the XML; it is ignored.</param>
    /// <param name="ratingCountry">ISO country used to resolve bare certifications (default US).</param>
    /// <exception cref="FormatException">The text has no recognisable NFO root element.</exception>
    public static MetadataResult ParseResult(string xml, string ratingCountry = "US")
    {
        ArgumentNullException.ThrowIfNull(xml);
        var root = LoadRoot(xml);
        var rootName = root.Name.LocalName.ToLowerInvariant();
        var kind = rootName switch
        {
            "movie" => MediaSearchKind.Movie,
            "episodedetails" or "tvshow" => MediaSearchKind.TvEpisode,
            _ => throw new FormatException(string.Format(CultureInfo.CurrentCulture, Strings.Nfo_UnsupportedRoot, root.Name.LocalName)),
        };

        var result = new MetadataResult(ProviderName, kind);
        var title = Text(root, "title");
        var plot = Text(root, "plot");
        var outline = Text(root, "outline");
        var cert = Certification(root, ratingCountry);
        var rating = RatingMapper.Encode(cert.Code, cert.Country, kind);

        if (rootName == "tvshow")
        {
            result.Set(MetadataTokens.SeriesName, title);
            result.Set(MetadataTokens.SeriesDescription, plot ?? outline);
            result.Set(MetadataTokens.Network, Texts(root, "studio").FirstOrDefault());
        }
        else
        {
            result.Set(MetadataTokens.Name, title);
            result.Set(MetadataTokens.Description, outline ?? plot);
            result.Set(MetadataTokens.LongDescription, plot ?? outline);
            result.Set(MetadataTokens.Director, Texts(root, "director"));
            result.Set(MetadataTokens.Screenwriters, Texts(root, "credits").Concat(Texts(root, "writer")));
        }

        result.Set(MetadataTokens.OriginalTitle, Text(root, "originaltitle"));
        result.Set(MetadataTokens.Genre, JoinGenres(Texts(root, "genre")));
        result.Set(MetadataTokens.Studio, ProviderJoin(Texts(root, "studio")));
        result.Set(MetadataTokens.Rating, rating);
        result.Set(MetadataTokens.ReleaseDate, ReleaseDate(root));
        result.Set(MetadataTokens.Cast, Actors(root));
        result.Set(MetadataTokens.Producers, Texts(root, "producer"));

        if (rootName == "episodedetails")
        {
            result.Set(MetadataTokens.SeriesName, Text(root, "showtitle"));
            var season = Int(root, "season");
            var episode = Int(root, "episode");
            result.Set(MetadataTokens.Season, season);
            result.Set(MetadataTokens.EpisodeNumber, episode);
            if (episode is { } e)
                result.Set(MetadataTokens.TrackNumber, e.ToString(CultureInfo.InvariantCulture));
            if (season is { } s && episode is { } ep)
                result.Set(MetadataTokens.EpisodeId, string.Create(CultureInfo.InvariantCulture, $"{s}{ep:00}"));
        }

        foreach (var id in root.Elements().Where(e => e.Name.LocalName == "uniqueid"))
        {
            var type = (string?)id.Attribute("type");
            var value = id.Value.Trim();
            if (value.Length == 0)
                continue;
            if (string.Equals(type, "imdb", StringComparison.OrdinalIgnoreCase) || value.StartsWith("tt", StringComparison.Ordinal))
                result.SetIfMissing(MetadataTokens.ImdbId, value);
            else if (kind == MediaSearchKind.TvEpisode && string.Equals(type, "tvdb", StringComparison.OrdinalIgnoreCase))
                result.SetIfMissing(rootName == "tvshow" ? MetadataTokens.ServiceSeriesId : MetadataTokens.ServiceEpisodeId, value);
        }

        result.SetIfMissing(MetadataTokens.ImdbId, Text(root, "imdbid") ?? Text(root, "imdb_id"));
        AddThumbs(result, root, rootName);
        result.IsDetailed = true;
        return result;
    }

    /// <summary>Parses NFO XML and maps it to tags with the default <see cref="MetadataMap"/>.</summary>
    public static MetadataSet Parse(string xml, string ratingCountry = "US")
    {
        var result = ParseResult(xml, ratingCountry);
        return MetadataMap.CreateDefault(result.Kind).Apply(result);
    }

    /// <summary>Reads and parses an NFO file (see <see cref="Parse"/>).</summary>
    public static MetadataSet Read(string path, string ratingCountry = "US") => Parse(File.ReadAllText(path), ratingCountry);

    /// <summary>
    /// NFO files that may describe <paramref name="mediaPath"/>, most specific first: <c>{name}.nfo</c>,
    /// <c>movie.nfo</c>, then <c>tvshow.nfo</c> in the same folder or its parent (season folders).
    /// Only existing files are returned.
    /// </summary>
    public static IReadOnlyList<string> FindNfoFiles(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(mediaPath)) ?? ".";
        var candidates = new List<string>
        {
            Path.Combine(dir, Path.GetFileNameWithoutExtension(mediaPath) + ".nfo"),
            Path.Combine(dir, "movie.nfo"),
            Path.Combine(dir, "tvshow.nfo"),
        };
        if (Path.GetDirectoryName(dir) is { } parent)
            candidates.Add(Path.Combine(parent, "tvshow.nfo"));
        return candidates.Where(File.Exists).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Imports the NFO data found next to a media file as a token result: the file's own NFO (or movie.nfo),
    /// completed with series data from tvshow.nfo. Returns null when no usable NFO exists.
    /// </summary>
    public static MetadataResult? LoadResultForMedia(string mediaPath, string ratingCountry = "US")
    {
        MetadataResult? main = null;
        MetadataResult? show = null;
        foreach (var file in FindNfoFiles(mediaPath))
        {
            MetadataResult parsed;
            try
            {
                parsed = ParseResult(File.ReadAllText(file), ratingCountry);
            }
            catch (Exception ex) when (ex is FormatException or XmlException or IOException)
            {
                MMW.Core.Diagnostics.AppLog.Warn(string.Format(CultureInfo.CurrentCulture, Strings.Nfo_Ignoring, file, ex.Message));
                continue;
            }

            var isShow = Path.GetFileName(file).Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase);
            if (isShow)
                show ??= parsed;
            else
                main ??= parsed;
        }

        if (main is null)
            return show is null ? null : show;
        if (show is not null && main.Kind == MediaSearchKind.TvEpisode)
        {
            foreach (var (token, value) in show.Values)
                main.SetIfMissing(token, value);
            foreach (var art in show.Artworks.Where(a => main.Artworks.All(m => m.FullUrl != a.FullUrl)))
                main.Artworks.Add(art);
        }

        return main;
    }

    /// <summary>Imports the NFO next to <paramref name="mediaPath"/> as tags; null when there is none.</summary>
    public static MetadataSet? ImportForMedia(string mediaPath, string ratingCountry = "US") =>
        LoadResultForMedia(mediaPath, ratingCountry) is { } result ? MetadataMap.CreateDefault(result.Kind).Apply(result) : null;

    /// <summary>Path of the NFO written for a media file (<c>{name}.nfo</c> next to it).</summary>
    public static string NfoPathFor(string mediaPath) =>
        Path.Combine(Path.GetDirectoryName(mediaPath) ?? ".", Path.GetFileNameWithoutExtension(mediaPath) + ".nfo");

    /// <summary>Exports tags as a Kodi NFO document (<c>episodedetails</c> for media kind TV Show, else <c>movie</c>).</summary>
    public static string Export(MetadataSet set, MediaSearchKind? kind = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        var isTv = kind == MediaSearchKind.TvEpisode || (kind is null && set.GetInt(TagId.MediaKind) == TagCatalog.MediaKindTvShow);
        var root = new XElement(isTv ? "episodedetails" : "movie");

        Add(root, "title", set.GetString(TagId.Name));
        if (isTv)
        {
            Add(root, "showtitle", set.GetString(TagId.TvShow));
            Add(root, "season", set.GetInt(TagId.TvSeason)?.ToString(CultureInfo.InvariantCulture));
            Add(root, "episode", (set.GetInt(TagId.TvEpisodeNumber) ?? set.GetPair(TagId.TrackNumber)?.Number)?.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            Add(root, "sorttitle", set.GetString(TagId.SortName));
        }

        var date = set.GetString(TagId.ReleaseDate);
        if (date is { Length: >= 4 })
        {
            if (!isTv)
                Add(root, "year", date[..4]);
            if (date.Length >= 10)
                Add(root, isTv ? "aired" : "premiered", date[..10]);
        }

        var description = set.GetString(TagId.Description);
        var longDescription = set.GetString(TagId.LongDescription);
        Add(root, "plot", longDescription ?? description);
        if (description is not null && description != longDescription)
            Add(root, "outline", description);

        foreach (var genre in Split(set.GetString(TagId.Genre)))
            Add(root, "genre", genre);
        if (isTv)
            Add(root, "studio", set.GetString(TagId.TvNetwork));
        foreach (var studio in Split(set.GetString(TagId.Studio)))
            Add(root, "studio", studio);

        if (MMW.Core.Metadata.Ratings.Find(set.GetString(TagId.Rating)) is { } rating && rating.Country != MMW.Core.Metadata.Ratings.AllCountries)
            Add(root, "mpaa", rating.Prefix == "mpaa" ? "Rated " + rating.Code : rating.Code);

        foreach (var name in set.GetList(TagId.Director))
            Add(root, "director", name);
        foreach (var name in set.GetList(TagId.Screenwriters))
            Add(root, "credits", name);
        foreach (var name in set.GetList(TagId.Producers))
            Add(root, "producer", name);

        var order = 0;
        foreach (var name in set.GetList(TagId.Cast))
            root.Add(new XElement("actor", new XElement("name", name), new XElement("order", order++)));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);
        using var writer = new Utf8StringWriter();
        doc.Save(writer, SaveOptions.None);
        return writer.ToString();
    }

    /// <summary>Writes <see cref="Export"/> output to <paramref name="path"/> (UTF-8).</summary>
    public static void Write(string path, MetadataSet set, MediaSearchKind? kind = null) =>
        File.WriteAllText(path, Export(set, kind), new UTF8Encoding(false));

    private static XElement LoadRoot(string xml)
    {
        // Kodi accepts "XML followed by a scraper URL": keep only the XML part.
        var text = xml.TrimStart('﻿', ' ', '\r', '\n', '\t');
        var start = text.IndexOf('<', StringComparison.Ordinal);
        if (start < 0)
            throw new FormatException(Strings.Nfo_NoXml);
        foreach (var name in new[] { "movie", "episodedetails", "tvshow" })
        {
            var close = $"</{name}>";
            var end = text.LastIndexOf(close, StringComparison.OrdinalIgnoreCase);
            if (end > 0)
            {
                text = text[..(end + close.Length)];
                break;
            }
        }

        try
        {
            var doc = XDocument.Parse(text, LoadOptions.None);
            return doc.Root ?? throw new FormatException(Strings.Nfo_NoRoot);
        }
        catch (XmlException ex)
        {
            throw new FormatException(string.Format(CultureInfo.CurrentCulture, Strings.Nfo_InvalidXml, ex.Message), ex);
        }
    }

    private static string? Text(XElement root, string name) =>
        root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } e && e.Value.Trim() is { Length: > 0 } v ? v : null;

    private static string[] Texts(XElement root, string name) =>
        root.Elements().Where(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    private static int? Int(XElement root, string name) =>
        int.TryParse(Text(root, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

    private static string? ReleaseDate(XElement root)
    {
        foreach (var name in new[] { "premiered", "aired", "releasedate", "firstaired" })
        {
            if (Text(root, name) is { } d)
                return MMW.Metadata.Providers.ProviderText.Date(d);
        }

        return Text(root, "year") is { Length: 4 } year ? year : null;
    }

    private static string[] Actors(XElement root) =>
        root.Elements().Where(e => e.Name.LocalName == "actor")
            .Select((a, i) => (Name: a.Element("name")?.Value.Trim(), Order: int.TryParse(a.Element("order")?.Value, out var o) ? o : 1000 + i))
            .Where(a => !string.IsNullOrEmpty(a.Name))
            .OrderBy(a => a.Order)
            .Select(a => a.Name!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static (string? Code, string Country) Certification(XElement root, string defaultCountry)
    {
        var text = Text(root, "mpaa") ?? Text(root, "certification");
        if (text is null)
            return (null, defaultCountry);

        // "US:PG-13 / GB:15" or "Rated PG-13" or "PG-13".
        var parts = text.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && part[..colon].Trim().Equals(defaultCountry, StringComparison.OrdinalIgnoreCase))
                return (part[(colon + 1)..].Trim(), defaultCountry);
        }

        var first = parts.FirstOrDefault() ?? text;
        var c = first.IndexOf(':', StringComparison.Ordinal);
        return c is > 0 and <= 3 ? (first[(c + 1)..].Trim(), first[..c].Trim()) : (first, defaultCountry);
    }

    private static void AddThumbs(MetadataResult result, XElement root, string rootName)
    {
        var thumbs = root.Elements().Where(e => e.Name.LocalName == "thumb")
            .Concat(root.Elements().Where(e => e.Name.LocalName == "fanart").SelectMany(f => f.Elements().Where(e => e.Name.LocalName == "thumb")
                .Select(t => { t.SetAttributeValue("aspect", (string?)t.Attribute("aspect") ?? "fanart"); return t; })));
        foreach (var thumb in thumbs)
        {
            var url = thumb.Value.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var full) || full.Scheme is not ("http" or "https"))
                continue;
            var preview = (string?)thumb.Attribute("preview") is { } p && Uri.TryCreate(p, UriKind.Absolute, out var pu) ? pu : full;
            var kind = ((string?)thumb.Attribute("aspect"))?.ToLowerInvariant() switch
            {
                "poster" => (string?)thumb.Attribute("type") == "season" ? ArtworkKind.Season : ArtworkKind.Poster,
                "fanart" or "landscape" => ArtworkKind.Backdrop,
                "banner" => ArtworkKind.Rectangle,
                "thumb" => ArtworkKind.Episode,
                _ => rootName == "episodedetails" ? ArtworkKind.Episode : ArtworkKind.Poster,
            };
            if (result.Artworks.All(a => a.FullUrl != full))
                result.Artworks.Add(new RemoteArtwork(preview, full, kind, ProviderName));
        }
    }

    private static string? JoinGenres(IEnumerable<string> genres) =>
        ProviderJoin(genres.SelectMany(g => g.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)));

    private static string? ProviderJoin(IEnumerable<string> values) => MMW.Metadata.Providers.ProviderText.Join(values);

    private static string[] Split(string? text) =>
        text is null ? [] : text.Split([", ", "/"], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void Add(XElement root, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            root.Add(new XElement(name, value));
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter()
            : base(CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
