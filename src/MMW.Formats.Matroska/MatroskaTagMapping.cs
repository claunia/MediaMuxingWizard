using System.Globalization;
using MMW.Core.Metadata;

namespace MMW.Formats.Matroska;

/// <summary>
/// Bidirectional mapping between <see cref="MetadataSet"/> and global Matroska tags, following the conventions of
/// https://www.matroska.org/technical/tagging.html. The file is treated as one item (TargetTypeValue 50, MOVIE or
/// EPISODE); the TV show / album is the COLLECTION (70) and the season the SEASON (60).
/// </summary>
/// <remarks>
/// <para>Tags with no standard Matroska name are written as <c>MMW_&lt;TagId&gt;</c> so they round-trip.</para>
/// <para>
/// SimpleTags the mapping does not understand are exposed as <see cref="MetadataSet.CustomItems"/> keyed
/// <c>"&lt;TargetTypeValue&gt;/&lt;NAME&gt;[/&lt;CHILD&gt;...]"</c>, optionally followed by <c>@&lt;language&gt;</c> and a
/// <c>#n</c> counter for repeated names, and are written back from there.
/// </para>
/// </remarks>
internal static class MatroskaTagMapping
{
    public const int Collection = 70;
    public const int Season = 60;
    public const int Item = 50;

    private const string CustomPrefix = "MMW_";
    private const string SortWith = "SORT_WITH";

    private enum Context
    {
        Always,
        TvOnly,
        NonTvOnly,
    }

    private sealed record Rule(TagId Id, int Level, string Name, Context Context = Context.Always);

    /// <summary>One-to-one rules for String/Text/Date/Rating/Integer/StringList tags.</summary>
    private static readonly Rule[] s_rules =
    [
        new(TagId.Name, Item, "TITLE"),
        new(TagId.TrackSubtitle, Item, "SUBTITLE"),
        new(TagId.Artist, Item, "ARTIST"),
        new(TagId.AlbumArtist, Collection, "ARTIST"),
        new(TagId.Album, Collection, "TITLE", Context.NonTvOnly),
        new(TagId.TvShow, Collection, "TITLE", Context.TvOnly),
        new(TagId.Grouping, Item, "GROUPING"),
        new(TagId.Composer, Item, "COMPOSER"),
        new(TagId.Comments, Item, "COMMENT"),
        new(TagId.Genre, Item, "GENRE"),
        new(TagId.ReleaseDate, Item, "DATE_RELEASED"),
        new(TagId.Tempo, Item, "BPM"),
        new(TagId.Keywords, Item, "KEYWORDS"),
        new(TagId.TvNetwork, Collection, "PUBLISHER"),
        new(TagId.TvEpisodeId, Item, "TV_EPISODE_ID"),
        new(TagId.TvSeason, Season, "PART_NUMBER", Context.TvOnly),
        new(TagId.TvEpisodeNumber, Item, "PART_NUMBER", Context.TvOnly),
        new(TagId.Cast, Item, "ACTOR"),
        new(TagId.Director, Item, "DIRECTOR"),
        new(TagId.Codirector, Item, "ASSISTANT_DIRECTOR"),
        new(TagId.Producers, Item, "PRODUCER"),
        new(TagId.Screenwriters, Item, "WRITTEN_BY"),
        new(TagId.Studio, Item, "PRODUCTION_STUDIO"),
        new(TagId.Description, Item, "SUMMARY"),
        new(TagId.LongDescription, Item, "SYNOPSIS"),
        new(TagId.SeriesDescription, Collection, "SUMMARY"),
        new(TagId.Lyrics, Item, "LYRICS"),
        new(TagId.SongDescription, Item, "DESCRIPTION"),
        new(TagId.ArtDirector, Item, "ART_DIRECTOR"),
        new(TagId.Arranger, Item, "ARRANGER"),
        new(TagId.Lyricist, Item, "LYRICIST"),
        new(TagId.Conductor, Item, "CONDUCTOR"),
        new(TagId.RecordCompany, Item, "LABEL"),
        new(TagId.PhonogramRights, Item, "PRODUCTION_COPYRIGHT"),
        new(TagId.Performer, Item, "LEAD_PERFORMER"),
        new(TagId.Publisher, Item, "PUBLISHER"),
        new(TagId.SoundEngineer, Item, "SOUND_ENGINEER"),
        new(TagId.Thanks, Item, "THANKS_TO"),
        new(TagId.ExecutiveProducer, Item, "EXECUTIVE_PRODUCER"),
        new(TagId.Rating, Item, "LAW_RATING"),
        new(TagId.Copyright, Item, "COPYRIGHT"),
        new(TagId.EncodingTool, Item, "ENCODER"),
        new(TagId.EncodedBy, Item, "ENCODED_BY"),
        new(TagId.PurchaseDate, Item, "DATE_PURCHASED"),
    ];

    /// <summary>Sort tags, written as SORT_WITH nested under the tag they sort.</summary>
    private static readonly (TagId Sort, TagId Parent)[] s_sortRules =
    [
        (TagId.SortName, TagId.Name),
        (TagId.SortArtist, TagId.Artist),
        (TagId.SortAlbumArtist, TagId.AlbumArtist),
        (TagId.SortAlbum, TagId.Album),
        (TagId.SortComposer, TagId.Composer),
        (TagId.SortTvShow, TagId.TvShow),
    ];

    /// <summary>
    /// Whether a document is a TV episode for the purpose of the mapping. Must agree with the decision made when
    /// reading (<see cref="Read"/>): an explicit media kind wins; without one, a season number implies TV.
    /// </summary>
    private static bool IsTv(MetadataSet metadata) => metadata.GetInt(TagId.MediaKind) is { } kind
        ? kind == TagCatalog.MediaKindTvShow
        : metadata.Contains(TagId.TvSeason);

    // ------------------------------------------------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Fills <paramref name="metadata"/> from the global tags of a file.</summary>
    /// <param name="globalTags">Tags whose targets have no UID.</param>
    /// <param name="metadata">Destination.</param>
    /// <param name="binaryTags">Receives SimpleTags carrying binary data, which cannot be represented in the set.</param>
    public static void Read(IEnumerable<MatroskaTag> globalTags, MetadataSet metadata, List<(int Level, byte[] Element)> binaryTags)
    {
        var entries = new List<(int Level, MatroskaSimpleTag Tag)>();
        foreach (var tag in globalTags)
        {
            foreach (var simple in tag.SimpleTags)
            {
                if (simple.Binary is not null && simple.Value is null)
                    binaryTags.Add((tag.TargetTypeValue, simple.RawElement.ToArray()));
                else
                    entries.Add((tag.TargetTypeValue, simple));
            }
        }

        var consumed = new HashSet<MatroskaSimpleTag>(ReferenceEqualityComparer.Instance);
        MatroskaSimpleTag? FirstOf(int level, string name)
        {
            foreach (var (l, t) in entries)
            {
                if (l == level && !consumed.Contains(t) && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                    return t;
            }

            return null;
        }

        IEnumerable<MatroskaSimpleTag> AllOf(int level, string name) => entries
            .Where(e => e.Level == level && !consumed.Contains(e.Tag) && string.Equals(e.Tag.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Tag)
            .ToList();

        bool TrySet(TagId id, string? text)
        {
            if (text is null)
                return false;
            try
            {
                var value = MetadataSet.Normalize(TagCatalog.Get(id).Kind, text);
                if (value is null)
                    return false;
                metadata.Set(id, value);
                return true;
            }
            catch (FormatException)
            {
                // Not a valid value for this tag: leave the SimpleTag to the custom items.
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        // Media kind decides whether the collection title is a TV show or an album.
        int? mediaKind = null;
        if (FirstOf(Item, "CONTENT_TYPE") is { } contentType && ParseMediaKind(contentType.Value) is { } mk)
        {
            mediaKind = mk;
            metadata.Set(TagId.MediaKind, mk);
            consumed.Add(contentType);
        }

        var isTv = mediaKind is { } k ? k == TagCatalog.MediaKindTvShow : FirstOf(Season, "PART_NUMBER") is not null;

        var parents = new Dictionary<TagId, MatroskaSimpleTag>();
        foreach (var rule in s_rules)
        {
            if ((rule.Context == Context.TvOnly && !isTv) || (rule.Context == Context.NonTvOnly && isTv))
                continue;

            var kind = TagCatalog.Get(rule.Id).Kind;
            if (kind == TagValueKind.StringList)
            {
                var values = new List<string>();
                var names = rule.Id == TagId.Screenwriters ? new[] { rule.Name, "SCREENPLAY_BY" } : [rule.Name];
                foreach (var name in names)
                {
                    foreach (var t in AllOf(rule.Level, name))
                    {
                        if (string.IsNullOrEmpty(t.Value))
                            continue;
                        values.Add(t.Value);
                        consumed.Add(t);
                    }
                }

                if (values.Count > 0)
                    metadata.Set(rule.Id, values);
                continue;
            }

            if (FirstOf(rule.Level, rule.Name) is { } simple && TrySet(rule.Id, simple.Value))
            {
                consumed.Add(simple);
                parents[rule.Id] = simple;
            }
        }

        if (!isTv)
        {
            ReadPair(TagId.TrackNumber, FirstOf(Item, "PART_NUMBER"), FirstOf(Collection, "TOTAL_PARTS"));
            ReadPair(TagId.DiskNumber, FirstOf(Season, "PART_NUMBER"), FirstOf(Season, "TOTAL_PARTS"));
        }

        void ReadPair(TagId id, MatroskaSimpleTag? number, MatroskaSimpleTag? total)
        {
            int n = 0, t = 0;
            var ok = (number is null || int.TryParse(number.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) &&
                     (total is null || int.TryParse(total.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out t));
            if (!ok || (number is null && total is null))
                return;
            metadata.Set(id, new IntPair(n, t));
            if (number is not null)
                consumed.Add(number);
            if (total is not null)
                consumed.Add(total);
        }

        // Sort names nested under the tag they sort.
        foreach (var (sortId, parentId) in s_sortRules)
        {
            if (parents.TryGetValue(parentId, out var parent) &&
                parent.Children.FirstOrDefault(c => string.Equals(c.Name, SortWith, StringComparison.OrdinalIgnoreCase)) is { } sort)
            {
                TrySet(sortId, sort.Value);
            }
        }

        // Our own fallback names.
        var lists = new Dictionary<TagId, List<string>>();
        foreach (var (_, t) in entries)
        {
            if (consumed.Contains(t) || !t.Name.StartsWith(CustomPrefix, StringComparison.Ordinal))
                continue;
            if (!Enum.TryParse<TagId>(t.Name[CustomPrefix.Length..], ignoreCase: false, out var id) || !Enum.IsDefined(id) || t.Value is null)
                continue;
            if (TagCatalog.Get(id).Kind == TagValueKind.StringList)
            {
                if (!lists.TryGetValue(id, out var list))
                    lists[id] = list = [];
                list.Add(t.Value);
                consumed.Add(t);
            }
            else if (!metadata.Contains(id) && TrySet(id, t.Value))
            {
                consumed.Add(t);
            }
        }

        foreach (var (id, list) in lists)
            metadata.Set(id, list);

        // Everything else becomes a custom item.
        foreach (var (level, t) in entries)
        {
            if (!consumed.Contains(t))
                AddCustom(metadata.CustomItems, level.ToString(CultureInfo.InvariantCulture), t);
        }
    }

    private static void AddCustom(Dictionary<string, string> items, string prefix, MatroskaSimpleTag tag)
    {
        var key = prefix + "/" + tag.Name;
        var lang = tag.EffectiveLanguage;
        var suffix = lang is "und" or "" ? string.Empty : "@" + lang;
        var unique = key + suffix;
        for (var n = 2; items.ContainsKey(unique); n++)
            unique = key + suffix + "#" + n.ToString(CultureInfo.InvariantCulture);
        items[unique] = tag.Value ?? string.Empty;

        foreach (var child in tag.Children)
        {
            if (child.Binary is null || child.Value is not null)
                AddCustom(items, unique, child);
        }
    }

    private static int? ParseMediaKind(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        foreach (var choice in TagCatalog.MediaKinds)
        {
            if (string.Equals(choice.Name, text, StringComparison.OrdinalIgnoreCase))
                return choice.Value;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Builds the global tags for <paramref name="metadata"/>, ordered COLLECTION, SEASON, item, then others.</summary>
    /// <param name="metadata">Source.</param>
    /// <param name="binaryTags">Binary SimpleTags read from the file, written back unchanged.</param>
    public static List<MatroskaTag> Write(MetadataSet metadata, IReadOnlyList<(int Level, byte[] Element)> binaryTags)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var levels = new SortedDictionary<int, MatroskaTag>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        MatroskaTag Level(int ttv)
        {
            if (!levels.TryGetValue(ttv, out var t))
                levels[ttv] = t = new MatroskaTag { TargetTypeValue = ttv };
            return t;
        }

        MatroskaSimpleTag Add(int ttv, string name, string value)
        {
            var simple = new MatroskaSimpleTag(name, value);
            Level(ttv).SimpleTags.Add(simple);
            return simple;
        }

        var isTv = IsTv(metadata);
        var hasExplicitKind = metadata.Contains(TagId.MediaKind);
        var written = new Dictionary<TagId, MatroskaSimpleTag>();
        var sortIds = s_sortRules.ToDictionary(r => r.Sort, r => r.Parent);

        foreach (var id in metadata.Keys)
        {
            var value = metadata[id]!;
            // Sort tags are nested under their parents once those are written.
            if (sortIds.ContainsKey(id))
                continue;

            if (id == TagId.MediaKind)
            {
                var kind = (int)value;
                var name = TagCatalog.MediaKinds.FirstOrDefault(c => c.Value == kind)?.Name ?? kind.ToString(CultureInfo.InvariantCulture);
                Add(Item, "CONTENT_TYPE", name);
                continue;
            }

            if (id == TagId.TrackNumber && !isTv)
            {
                var pair = (IntPair)value;
                Add(Item, "PART_NUMBER", pair.Number.ToString(CultureInfo.InvariantCulture));
                if (pair.Total > 0)
                    Add(Collection, "TOTAL_PARTS", pair.Total.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            // A season-level PART_NUMBER without an explicit media kind would make the file read back as TV.
            if (id == TagId.DiskNumber && !isTv && hasExplicitKind)
            {
                var pair = (IntPair)value;
                Add(Season, "PART_NUMBER", pair.Number.ToString(CultureInfo.InvariantCulture));
                if (pair.Total > 0)
                    Add(Season, "TOTAL_PARTS", pair.Total.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            var rule = s_rules.FirstOrDefault(r => r.Id == id &&
                                                   (r.Context == Context.Always || (r.Context == Context.TvOnly) == isTv));
            var level = rule?.Level ?? Item;
            var tagName = rule?.Name ?? CustomPrefix + id;
            if (value is IReadOnlyList<string> list)
            {
                foreach (var item in list)
                    Add(level, tagName, item);
            }
            else
            {
                written[id] = Add(level, tagName, FormatRaw(value));
            }
        }

        foreach (var (sortId, parentId) in s_sortRules)
        {
            if (metadata[sortId] is not string sort)
                continue;
            if (written.TryGetValue(parentId, out var parent) && !parent.Name.StartsWith(CustomPrefix, StringComparison.Ordinal))
                parent.Children.Add(new MatroskaSimpleTag(SortWith, sort));
            else
                Add(Item, CustomPrefix + sortId, sort);
        }

        WriteCustomItems(metadata.CustomItems, Level);

        foreach (var (lvl, element) in binaryTags)
            Level(lvl).RawSimpleTags.Add(element);

        return levels.Values.ToList();
    }

    private static void WriteCustomItems(Dictionary<string, string> items, Func<int, MatroskaTag> level)
    {
        // Ordinal order guarantees parents ("50/FOO") precede their children ("50/FOO/BAR").
        var byKey = new Dictionary<string, MatroskaSimpleTag>(StringComparer.Ordinal);
        foreach (var (key, value) in items.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var slash = key.LastIndexOf('/');
            string parentKey;
            string segment;
            if (slash < 0)
            {
                parentKey = Item.ToString(CultureInfo.InvariantCulture);
                segment = key;
            }
            else
            {
                parentKey = key[..slash];
                segment = key[(slash + 1)..];
            }

            var (name, language) = ParseSegment(segment);
            if (name.Length == 0)
                continue;
            var simple = new MatroskaSimpleTag(name, value);
            if (language is not null)
                simple.LanguageBcp47 = language;

            if (byKey.TryGetValue(parentKey, out var parent))
            {
                parent.Children.Add(simple);
            }
            else if (int.TryParse(parentKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ttv) && ttv is > 0 and <= 70)
            {
                level(ttv).SimpleTags.Add(simple);
            }
            else
            {
                // Not one of our keys (e.g. merged from another container): keep the whole key as the name.
                simple.Name = key;
                level(Item).SimpleTags.Add(simple);
            }

            byKey[key] = simple;
        }
    }

    private static (string Name, string? Language) ParseSegment(string segment)
    {
        var hash = segment.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
            segment = segment[..hash];
        var at = segment.IndexOf('@', StringComparison.Ordinal);
        return at < 0 ? (segment, null) : (segment[..at], segment[(at + 1)..]);
    }

    /// <summary>Formats a value so that <see cref="MetadataSet.Normalize"/> parses it back to the same value.</summary>
    private static string FormatRaw(object value) => value switch
    {
        bool b => b ? "1" : "0",
        int i => i.ToString(CultureInfo.InvariantCulture),
        IntPair p => p.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
