using System.Globalization;
using System.Text.RegularExpressions;
using MMW.Metadata.Search;

namespace MMW.Metadata.Parsing;

/// <summary>
/// Guesses a title, year, season and episode from a media file name (a regex reimplementation of the ideas behind
/// Perl's Video::Filename used by Subler): <c>S01E02</c>, <c>1x02</c>, <c>Season 1 Episode 2</c>, multi-episode
/// <c>S01E01E02</c>, date-based <c>2024.03.05</c>, anime <c>[Group] Title - 05 [1080p]</c>, and movies
/// <c>Title (2010)</c> / <c>Title.2010.1080p.BluRay.x264-GROUP</c>.
/// </summary>
public static partial class FileNameParser
{
    private static readonly HashSet<string> s_mediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mk3d", ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".ts", ".m2ts", ".mts", ".webm", ".mpg", ".mpeg",
        ".ogm", ".ogv", ".flv", ".3gp", ".divx", ".vob", ".nfo", ".srt", ".ass", ".ssa", ".sub", ".idx", ".m4a", ".mka",
    };

    /// <summary>Parses a file name or path.</summary>
    public static ParsedName Parse(string fileNameOrPath)
    {
        ArgumentNullException.ThrowIfNull(fileNameOrPath);
        var fileName = Path.GetFileName(fileNameOrPath);
        var extension = Path.GetExtension(fileName);
        var stem = s_mediaExtensions.Contains(extension) ? fileName[..^extension.Length] : fileName;

        var parsed = ParseStem(stem);

        // "S01E02.mkv" inside "Show Name/Season 1/": take the series name from the folders.
        if (parsed.Title.Length == 0 && parsed.Kind == MediaSearchKind.TvEpisode)
        {
            var folderTitle = FolderTitle(fileNameOrPath);
            if (folderTitle is not null)
                parsed = parsed with { Title = folderTitle };
        }
        else if (parsed.Title.Length == 0)
        {
            parsed = parsed with { Title = Clean(stem) };
        }

        return parsed;
    }

    private static ParsedName ParseStem(string stem)
    {
        string? group = null;

        // Anime: "[Group] Title - 05 [1080p]" / "[Group] Title - 05v2 (BD 1080p) [ABCD1234]".
        var anime = AnimePattern().Match(stem);
        if (anime.Success)
        {
            var title = Clean(anime.Groups["title"].Value);
            var seasonInTitle = AnimeSeasonSuffix().Match(title);
            int? season = 1;
            if (seasonInTitle.Success)
            {
                season = int.Parse(seasonInTitle.Groups[1].Value, CultureInfo.InvariantCulture);
                title = title[..seasonInTitle.Index].Trim();
            }

            return new ParsedName(MediaSearchKind.TvEpisode, title, null, season, Int(anime.Groups["ep"]))
            {
                Group = anime.Groups["group"].Value.Trim(),
                LastEpisode = anime.Groups["ep2"].Success ? Int(anime.Groups["ep2"]) : null,
            };
        }

        var leadingGroup = LeadingGroup().Match(stem);
        if (leadingGroup.Success)
        {
            group = leadingGroup.Groups[1].Value.Trim();
            stem = stem[leadingGroup.Length..];
        }

        // Remove bracketed hashes / tags anywhere ("[ABCD1234]", "[1080p]"), keep parenthesised years.
        stem = BracketTag().Replace(stem, " ");

        foreach (var (pattern, multi) in TvPatterns())
        {
            var m = pattern.Match(stem);
            if (!m.Success)
                continue;
            var title = TitleBefore(stem, m.Index, out var titleYear);
            var episode = Int(m.Groups["e"]);
            int? last = null;
            if (multi)
            {
                var all = m.Groups["e2"].Captures.Select(c => int.Parse(c.Value, CultureInfo.InvariantCulture)).ToList();
                if (all.Count > 0 && all[^1] != episode)
                    last = all[^1];
            }

            return new ParsedName(MediaSearchKind.TvEpisode, title, titleYear, m.Groups["s"].Success ? Int(m.Groups["s"]) : null, episode)
            {
                LastEpisode = last,
                Group = group ?? TrailingGroup(stem),
            };
        }

        var date = DatePattern().Match(stem);
        if (date.Success && date.Index > 0 &&
            DateOnly.TryParseExact($"{date.Groups["y"].Value}-{date.Groups["m"].Value}-{date.Groups["d"].Value}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var airDate))
        {
            var title = TitleBefore(stem, date.Index, out _);
            return new ParsedName(MediaSearchKind.TvEpisode, title, airDate.Year, null, null) { AirDate = airDate, Group = group ?? TrailingGroup(stem) };
        }

        // Movie: the last plausible year that is not at the very start is the release year.
        var years = YearPattern().Matches(stem).Where(y => y.Index > 0).ToList();
        if (years.Count > 0)
        {
            var year = years[^1];
            var value = int.Parse(year.Groups["y"].Value, CultureInfo.InvariantCulture);
            if (value <= DateTime.UtcNow.Year + 2)
            {
                var title = Clean(stem[..year.Index]);
                if (title.Length > 0)
                    return new ParsedName(MediaSearchKind.Movie, title, value, null, null) { Group = group ?? TrailingGroup(stem) };
            }
        }

        var tag = FirstReleaseTag(stem);
        var movieTitle = Clean(tag >= 0 ? stem[..tag] : stem);
        return new ParsedName(MediaSearchKind.Movie, movieTitle, null, null, null) { Group = group ?? (tag >= 0 ? TrailingGroup(stem) : null) };
    }

    private static IEnumerable<(Regex Pattern, bool Multi)> TvPatterns()
    {
        yield return (SxxExx(), true);
        yield return (NxNN(), true);
        yield return (SeasonEpisodeWords(), false);
        yield return (SeasonOnlyEpisodeWord(), false);
        yield return (EpisodeOnly(), false);
    }

    private static string TitleBefore(string stem, int index, out int? year)
    {
        year = null;
        var title = Clean(stem[..index]);

        // "Doctor Who (2005)" / "Doctor.Who.2005" → keep the year as a disambiguator.
        var y = TrailingYear().Match(title);
        if (y.Success && y.Index > 0)
        {
            year = int.Parse(y.Groups["y"].Value, CultureInfo.InvariantCulture);
            title = title[..y.Index].Trim(' ', '-', '(', '[');
        }

        return title;
    }

    private static string? TrailingGroup(string stem)
    {
        var m = TrailingGroupPattern().Match(stem);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static int FirstReleaseTag(string stem)
    {
        var m = ReleaseTag().Match(stem);
        return m.Success ? m.Index : -1;
    }

    /// <summary>Turns separators into spaces, cuts release tags and tidies punctuation.</summary>
    internal static string Clean(string text)
    {
        var tag = ReleaseTag().Match(text);
        if (tag.Success && tag.Index > 0)
            text = text[..tag.Index];

        // Dots and underscores are word separators unless they are part of an acronym ("S.H.I.E.L.D.").
        text = Acronym().Replace(text, m => m.Value.Replace(".", "\u0001", StringComparison.Ordinal));
        text = Separators().Replace(text, " ");
        text = text.Replace('\u0001', '.');
        text = EmptyBrackets().Replace(text, " ");
        text = Spaces().Replace(text, " ");
        return text.Trim(' ', '-', '–', ',', '(', '[', '{', '~');
    }

    private static string? FolderTitle(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name))
                return null;
            if (!SeasonFolder().IsMatch(name))
            {
                var cleaned = Clean(name);
                var y = TrailingYear().Match(cleaned);
                return y.Success && y.Index > 0 ? cleaned[..y.Index].Trim(' ', '(', '[') : cleaned;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static int? Int(Group g) =>
        g.Success && int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    private const string Sep = @"[\s._\-]";

    [GeneratedRegex(@"^\s*\[(?<group>[^\]]+)\]\s*(?<title>.+?)\s+-\s+(?:EP?\s*)?(?<ep>\d{1,4})(?:\s*-\s*(?<ep2>\d{1,4}))?(?:v\d)?(?:\s*(?:END|FINAL))?\s*(?:[\[(].*)?(?:\.\w{2,4})?$", RegexOptions.IgnoreCase)]
    private static partial Regex AnimePattern();

    [GeneratedRegex(@"\s+(?:S|Season\s*)(\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex AnimeSeasonSuffix();

    [GeneratedRegex(@"^\s*\[([^\]]+)\]\s*")]
    private static partial Regex LeadingGroup();

    [GeneratedRegex(@"\[(?!(?:19|20)\d{2}\])[^\]]*\]")]
    private static partial Regex BracketTag();

    [GeneratedRegex(@"(?<![A-Za-z0-9])S(?<s>\d{1,4})" + Sep + @"?E(?<e>\d{1,4})(?:(?:" + Sep + @"?-?" + Sep + @"?E|-)(?<e2>\d{1,4}))*(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SxxExx();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<s>\d{1,2})x(?<e>\d{1,3})(?:(?:-|x|-\d{1,2}x)(?<e2>\d{1,3}))*(?![\dp])", RegexOptions.IgnoreCase)]
    private static partial Regex NxNN();

    [GeneratedRegex(@"(?<![A-Za-z])Season" + Sep + @"*(?<s>\d{1,4})" + Sep + @"*(?:,|-)?" + Sep + @"*(?:Episode|Ep\.?)" + Sep + @"*(?<e>\d{1,4})", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeWords();

    [GeneratedRegex(@"(?<![A-Za-z])S(?<s>\d{1,2})" + Sep + @"+(?:Episode|Ep\.?)" + Sep + @"*(?<e>\d{1,4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonOnlyEpisodeWord();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:Episode|Ep\.?|E)" + Sep + @"*(?<e>\d{1,4})(?![\dA-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeOnly();

    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})[.\-_ ](?<m>0[1-9]|1[0-2])[.\-_ ](?<d>0[1-9]|[12]\d|3[01])(?!\d)")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"(?<![\dA-Za-z])[\(\[]?(?<y>(?:19|20)\d{2})[\)\]]?(?![\dA-Za-z])")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"[\(\[]?(?<y>(?:19|20)\d{2})[\)\]]?$")]
    private static partial Regex TrailingYear();

    [GeneratedRegex(@"-(?<g>[A-Za-z0-9]+)$")]
    private static partial Regex TrailingGroupPattern();

    [GeneratedRegex(@"^(?:Season|Series|Staffel|Saison|Temporada|S)\s*\d{1,4}$|^Specials?$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[A-Za-z]\.){2,}(?:[A-Za-z](?![A-Za-z0-9]))?")]
    private static partial Regex Acronym();

    [GeneratedRegex(@"(?:_|\.(?!\s))+|\s+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\(\s*\)|\[\s*\]")]
    private static partial Regex EmptyBrackets();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:" +
        @"\d{3,4}[pi]|4k|8k|uhd|hdr(?:10)?(?:\+|plus)?|dv|dovi|sdr|" +
        @"blu-?ray|bdrip|brrip|bdremux|bd|dvd(?:rip|scr|r|9|5)?|web(?:-?dl|-?rip)?|hdtv|pdtv|sdtv|dsr|hdrip|tvrip|vhsrip|cam|ts|telesync|screener|" +
        @"[xh][\. ]?26[45]|hevc|avc|xvid|divx|vc-?1|mpeg-?2|av[12]|vp9|10-?bit|8-?bit|" +
        @"aac(?:2[\. ]0|5[\. ]1)?|ac-?3|e-?ac-?3|dts(?:-?hd|-?x|-?ma)?|dd(?:p|\+)?[257][\. ][01]|dd(?:p|\+)?|truehd|atmos|flac|mp3|opus|lpcm|" +
        @"remux|proper|repack|rerip|extended|unrated|uncut|directors[\. ]?cut|theatrical|limited|internal|multi|multisubs|subbed|dubbed|dual[\. ]?audio|complete|" +
        @"nf|amzn|dsnp|hmax|atvp|hulu|pcok|itunes|criterion|remastered|imax" +
        @")(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseTag();
}
