using System.Globalization;
using System.Text.RegularExpressions;
using MMW.Core.Metadata;
using MMW.Metadata.Search;

namespace MMW.Metadata.Certifications;

/// <summary>
/// Maps provider certifications ("PG-13", "TV-14", "TV_MA", "Rated R", "12") to iTunes content rating entries of
/// <see cref="MMW.Core.Metadata.Ratings"/>.
/// </summary>
public static partial class RatingMapper
{
    // MMW.Core's ratings.json names countries the way the iTunes Store does (in the local language).
    private static readonly Dictionary<string, string> s_isoToRatingsCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "USA", ["AU"] = "Australia", ["BR"] = "Brasil", ["PY"] = "Paraguay", ["CA"] = "Canada",
        ["DE"] = "Deutschland", ["FR"] = "France", ["IT"] = "Italia", ["IE"] = "Ireland", ["JP"] = "日本",
        ["MX"] = "México", ["NL"] = "Nederland", ["NZ"] = "New Zealand", ["SE"] = "Sverige", ["CH"] = "Schweiz",
        ["GB"] = "UK", ["UK"] = "UK", ["RU"] = "Россия", ["PH"] = "Pilipinas", ["HK"] = "Hong Kong",
        ["CZ"] = "Česká republika", ["IN"] = "India",
    };

    /// <summary>Name of the country in <see cref="MMW.Core.Metadata.Ratings"/> for an ISO 3166 code, or null.</summary>
    public static string? RatingsCountry(string? iso) =>
        iso is null ? null : s_isoToRatingsCountry.GetValueOrDefault(iso.Trim());

    /// <summary>Finds the rating entry for a certification.</summary>
    /// <param name="certification">Provider certification; may carry a "Rated " prefix or "US:" country prefix.</param>
    /// <param name="countryIso">ISO 3166-1 country of the certification (default US).</param>
    /// <param name="kind">Movie or TV (selects the movie or TV rating system).</param>
    public static ContentRatingEntry? Find(string? certification, string? countryIso, MediaSearchKind kind)
    {
        if (string.IsNullOrWhiteSpace(certification))
            return null;

        var cert = certification.Trim();
        var colon = cert.IndexOf(':', StringComparison.Ordinal);
        if (colon is > 0 and <= 3)
        {
            countryIso = cert[..colon];
            cert = cert[(colon + 1)..].Trim();
        }

        if (cert.StartsWith("Rated ", StringComparison.OrdinalIgnoreCase))
            cert = cert[6..].Trim();
        if (cert.StartsWith("TV_", StringComparison.OrdinalIgnoreCase))
            cert = "TV-" + cert[3..];
        if (cert.Length == 0)
            return null;

        var country = RatingsCountry(string.IsNullOrWhiteSpace(countryIso) ? "US" : countryIso);
        if (country is null)
            return null;

        // US TV certifications are recognisable on their own even if the provider labelled them as a movie rating.
        if (country == "USA" && cert.StartsWith("TV-", StringComparison.OrdinalIgnoreCase))
            kind = MediaSearchKind.TvEpisode;

        var candidates = MMW.Core.Metadata.Ratings.All.Where(r => r.Country == country).ToList();
        var preferred = candidates.Where(r => MatchesMedia(r.Media, kind)).ToList();

        return Match(preferred, cert) ?? Match(candidates, cert) ?? MatchUnrated(cert);
    }

    /// <summary>Returns the encoded iTunes rating string ("mpaa|PG-13|300|") or null.</summary>
    public static string? Encode(string? certification, string? countryIso, MediaSearchKind kind) =>
        Find(certification, countryIso, kind)?.Encoded;

    private static bool MatchesMedia(string media, MediaSearchKind kind) =>
        kind == MediaSearchKind.Movie
            ? media.Contains("Movie", StringComparison.OrdinalIgnoreCase)
            : media.Contains("TV", StringComparison.OrdinalIgnoreCase);

    private static ContentRatingEntry? Match(List<ContentRatingEntry> entries, string cert)
    {
        var exact = entries.FirstOrDefault(r => r.Code.Equals(cert, StringComparison.OrdinalIgnoreCase))
                    ?? entries.FirstOrDefault(r => r.Name.Equals(cert, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var compact = Compact(cert);
        exact = entries.FirstOrDefault(r => Compact(r.Code) == compact);
        if (exact is not null)
            return exact;

        // Numeric certifications ("12", "16+", "FSK 12") against codes such as "Ab 12 Jahren" or "-12".
        if (FirstNumber(cert) is { } n && OnlyNumber().IsMatch(cert.Replace("FSK", string.Empty, StringComparison.OrdinalIgnoreCase).Trim()))
            return entries.FirstOrDefault(r => FirstNumber(r.Code) == n);
        return null;
    }

    private static ContentRatingEntry? MatchUnrated(string cert) =>
        cert.ToUpperInvariant() is "NR" or "UNRATED" or "NOT RATED"
            ? MMW.Core.Metadata.Ratings.All.FirstOrDefault(r => r.Country == MMW.Core.Metadata.Ratings.AllCountries)
            : null;

    private static string Compact(string s) => new([.. s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]);

    private static int? FirstNumber(string s)
    {
        var m = Number().Match(s);
        return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    [GeneratedRegex(@"^[-+]?\s*\d+\s*\+?$")]
    private static partial Regex OnlyNumber();
}
