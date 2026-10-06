using System.Text.Json.Serialization;
using MMW.Metadata.Http;

namespace MMW.Metadata.Providers.TheTvDb;

// Minimal TheTVDB v4 response models; every member optional.

internal sealed class TvdbEnvelope<T>
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("data")] public T? Data { get; set; }
    [JsonPropertyName("links")] public TvdbLinks? Links { get; set; }
}

internal sealed class TvdbLinks
{
    [JsonPropertyName("next")][JsonConverter(typeof(FlexibleStringConverter))] public string? Next { get; set; }
}

internal sealed class TvdbLoginRequest
{
    [JsonPropertyName("apikey")] public string ApiKey { get; set; } = string.Empty;
    [JsonPropertyName("pin")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Pin { get; set; }
}

internal sealed class TvdbLogin
{
    [JsonPropertyName("token")] public string? Token { get; set; }
}

internal sealed class TvdbSearchItem
{
    [JsonPropertyName("objectID")][JsonConverter(typeof(FlexibleStringConverter))] public string? ObjectId { get; set; }
    [JsonPropertyName("tvdb_id")][JsonConverter(typeof(FlexibleStringConverter))] public string? TvdbId { get; set; }
    [JsonPropertyName("id")][JsonConverter(typeof(FlexibleStringConverter))] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("year")][JsonConverter(typeof(FlexibleStringConverter))] public string? Year { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
    [JsonPropertyName("network")] public string? Network { get; set; }
    [JsonPropertyName("first_air_time")] public string? FirstAirTime { get; set; }
    [JsonPropertyName("aliases")] public List<string>? Aliases { get; set; }
    [JsonPropertyName("translations")] public Dictionary<string, string>? Translations { get; set; }
    [JsonPropertyName("overviews")] public Dictionary<string, string>? Overviews { get; set; }

    /// <summary>Numeric series id ("81189") from tvdb_id, or from "series-81189".</summary>
    public string? SeriesId
    {
        get
        {
            if (!string.IsNullOrEmpty(TvdbId))
                return TvdbId;
            var raw = Id ?? ObjectId;
            if (raw is null)
                return null;
            var dash = raw.LastIndexOf('-');
            return dash >= 0 ? raw[(dash + 1)..] : raw;
        }
    }
}

internal sealed class TvdbNamed
{
    [JsonPropertyName("id")][JsonConverter(typeof(FlexibleIntConverter))] public int? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal sealed class TvdbCompanyType
{
    [JsonPropertyName("companyTypeName")] public string? Name { get; set; }
}

internal sealed class TvdbCompany
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("companyType")] public TvdbCompanyType? CompanyType { get; set; }
}

internal sealed class TvdbContentRating
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("contentType")] public string? ContentType { get; set; }
}

internal sealed class TvdbCharacter
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("peopleName")] public string? PeopleName { get; set; }
    [JsonPropertyName("personName")] public string? PersonName { get; set; }
    [JsonPropertyName("peopleType")] public string? PeopleType { get; set; }
    [JsonPropertyName("type")][JsonConverter(typeof(FlexibleIntConverter))] public int? Type { get; set; }
    [JsonPropertyName("sort")][JsonConverter(typeof(FlexibleIntConverter))] public int? Sort { get; set; }
    [JsonPropertyName("isFeatured")] public bool? IsFeatured { get; set; }

    public string? Person => PeopleName ?? PersonName;
}

internal sealed class TvdbArtwork
{
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("thumbnail")] public string? Thumbnail { get; set; }
    [JsonPropertyName("type")][JsonConverter(typeof(FlexibleIntConverter))] public int? Type { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("width")][JsonConverter(typeof(FlexibleIntConverter))] public int? Width { get; set; }
    [JsonPropertyName("height")][JsonConverter(typeof(FlexibleIntConverter))] public int? Height { get; set; }
    [JsonPropertyName("score")] public double? Score { get; set; }
}

internal sealed class TvdbNameTranslation
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
}

internal sealed class TvdbTranslations
{
    [JsonPropertyName("nameTranslations")] public List<TvdbNameTranslation>? NameTranslations { get; set; }
    [JsonPropertyName("overviewTranslations")] public List<TvdbNameTranslation>? OverviewTranslations { get; set; }
}

internal sealed class TvdbSeasonType
{
    [JsonPropertyName("type")] public string? Type { get; set; }
}

internal sealed class TvdbSeason
{
    [JsonPropertyName("id")][JsonConverter(typeof(FlexibleIntConverter))] public int? Id { get; set; }
    [JsonPropertyName("number")][JsonConverter(typeof(FlexibleIntConverter))] public int? Number { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("type")] public TvdbSeasonType? Type { get; set; }
}

internal sealed class TvdbSeries
{
    [JsonPropertyName("id")][JsonConverter(typeof(FlexibleIntConverter))] public int? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("firstAired")] public string? FirstAired { get; set; }
    [JsonPropertyName("year")][JsonConverter(typeof(FlexibleStringConverter))] public string? Year { get; set; }
    [JsonPropertyName("genres")] public List<TvdbNamed>? Genres { get; set; }
    [JsonPropertyName("originalNetwork")] public TvdbNamed? OriginalNetwork { get; set; }
    [JsonPropertyName("latestNetwork")] public TvdbNamed? LatestNetwork { get; set; }
    [JsonPropertyName("companies")] public List<TvdbCompany>? Companies { get; set; }
    [JsonPropertyName("contentRatings")] public List<TvdbContentRating>? ContentRatings { get; set; }
    [JsonPropertyName("characters")] public List<TvdbCharacter>? Characters { get; set; }
    [JsonPropertyName("artworks")] public List<TvdbArtwork>? Artworks { get; set; }
    [JsonPropertyName("seasons")] public List<TvdbSeason>? Seasons { get; set; }
    [JsonPropertyName("translations")] public TvdbTranslations? Translations { get; set; }
}

internal sealed class TvdbEpisode
{
    [JsonPropertyName("id")][JsonConverter(typeof(FlexibleIntConverter))] public int? Id { get; set; }
    [JsonPropertyName("seriesId")][JsonConverter(typeof(FlexibleIntConverter))] public int? SeriesId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("aired")] public string? Aired { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("number")][JsonConverter(typeof(FlexibleIntConverter))] public int? Number { get; set; }
    [JsonPropertyName("seasonNumber")][JsonConverter(typeof(FlexibleIntConverter))] public int? SeasonNumber { get; set; }
    [JsonPropertyName("absoluteNumber")][JsonConverter(typeof(FlexibleIntConverter))] public int? AbsoluteNumber { get; set; }
    [JsonPropertyName("productionCode")] public string? ProductionCode { get; set; }
    [JsonPropertyName("characters")] public List<TvdbCharacter>? Characters { get; set; }
    [JsonPropertyName("companies")] public List<TvdbCompany>? Companies { get; set; }
    [JsonPropertyName("contentRatings")] public List<TvdbContentRating>? ContentRatings { get; set; }
    [JsonPropertyName("networks")] public List<TvdbNamed>? Networks { get; set; }
    [JsonPropertyName("translations")] public TvdbTranslations? Translations { get; set; }
}

internal sealed class TvdbEpisodePage
{
    [JsonPropertyName("series")] public TvdbSeries? Series { get; set; }
    [JsonPropertyName("episodes")] public List<TvdbEpisode>? Episodes { get; set; }
}

internal sealed class TvdbArtworkPage
{
    [JsonPropertyName("artworks")] public List<TvdbArtwork>? Artworks { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(TvdbLoginRequest))]
[JsonSerializable(typeof(TvdbEnvelope<TvdbLogin>))]
[JsonSerializable(typeof(TvdbEnvelope<List<TvdbSearchItem>>))]
[JsonSerializable(typeof(TvdbEnvelope<TvdbSeries>))]
[JsonSerializable(typeof(TvdbEnvelope<TvdbEpisodePage>))]
[JsonSerializable(typeof(TvdbEnvelope<TvdbEpisode>))]
[JsonSerializable(typeof(TvdbEnvelope<TvdbArtworkPage>))]
internal sealed partial class TvdbJsonContext : JsonSerializerContext;
