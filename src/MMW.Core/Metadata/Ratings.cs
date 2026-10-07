using System.Text.Json;
using System.Text.Json.Serialization;
using MMW.Core.Resources;

namespace MMW.Core.Metadata;

/// <summary>One content rating as stored in the iTunes <c>iTunEXTC</c> item ("prefix|code|value|").</summary>
public sealed record ContentRatingEntry(string Country, string Media, string Prefix, string Code, string Value, string Name)
{
    /// <summary>The value stored in <see cref="TagId.Rating"/>.</summary>
    public string Encoded => $"{Prefix}|{Code}|{Value}|";

    public string DisplayName => Country == Ratings.AllCountries ? Name : $"{Country} {Media}: {Name}";

    public override string ToString() => DisplayName;
}

/// <summary>Per-country movie and TV content ratings known to the iTunes Store.</summary>
public static class Ratings
{
    private sealed record CountryDto(
        [property: JsonPropertyName("country")] string Country,
        [property: JsonPropertyName("store")] int Store,
        [property: JsonPropertyName("ratings")] List<RatingDto> Entries);

    private sealed record RatingDto(
        [property: JsonPropertyName("media")] string Media,
        [property: JsonPropertyName("prefix")] string Prefix,
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("name")] string Name);

    /// <summary>Pseudo-country whose entries apply everywhere (e.g. "Unrated").</summary>
    public const string AllCountries = "All countries";

    private static readonly Lazy<IReadOnlyList<ContentRatingEntry>> s_all = new(Load);

    public static IReadOnlyList<ContentRatingEntry> All => s_all.Value;

    public static IReadOnlyList<string> Countries => All.Select(r => r.Country).Where(c => c != AllCountries).Distinct().ToList();

    /// <summary>Ratings shown in the picker: the chosen country plus USA (always shown, like iTunes).</summary>
    public static IReadOnlyList<ContentRatingEntry> ForCountry(string? country) =>
        All.Where(r => r.Country == "USA" || r.Country == country || r.Country == AllCountries).ToList();

    /// <summary>Finds the entry matching an encoded rating string (annotation ignored).</summary>
    public static ContentRatingEntry? Find(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return null;
        var parts = encoded.Split('|');
        if (parts.Length < 3)
            return null;
        return All.FirstOrDefault(r =>
            string.Equals(r.Prefix, parts[0], StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Code, parts[1], StringComparison.OrdinalIgnoreCase));
    }

    private static List<ContentRatingEntry> Load()
    {
        using var stream = typeof(Ratings).Assembly.GetManifestResourceStream("MMW.Core.Resources.ratings.json")
                           ?? throw new InvalidOperationException(Strings.Error_RatingsResourceMissing);
        var countries = JsonSerializer.Deserialize<List<CountryDto>>(stream) ?? [];
        return countries.SelectMany(c => c.Entries.Select(r => new ContentRatingEntry(c.Country, r.Media, r.Prefix, r.Code, r.Value, r.Name))).ToList();
    }
}
