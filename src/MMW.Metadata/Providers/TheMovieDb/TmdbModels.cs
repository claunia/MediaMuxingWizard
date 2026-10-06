using System.Text.Json.Serialization;
using MMW.Metadata.Http;

namespace MMW.Metadata.Providers.TheMovieDb;

// Minimal TMDb v3 response models. Every member is optional: TMDb omits fields freely.

internal sealed class TmdbPage<T>
{
    [JsonPropertyName("results")] public List<T>? Results { get; set; }
}

internal sealed class TmdbNamed
{
    [JsonPropertyName("id")][JsonConverter(typeof(FlexibleIntConverter))] public int? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal sealed class TmdbImage
{
    [JsonPropertyName("file_path")] public string? FilePath { get; set; }
    [JsonPropertyName("width")][JsonConverter(typeof(FlexibleIntConverter))] public int? Width { get; set; }
    [JsonPropertyName("height")][JsonConverter(typeof(FlexibleIntConverter))] public int? Height { get; set; }
    [JsonPropertyName("iso_639_1")] public string? Language { get; set; }
    [JsonPropertyName("vote_average")] public double? VoteAverage { get; set; }
}

internal sealed class TmdbImages
{
    [JsonPropertyName("posters")] public List<TmdbImage>? Posters { get; set; }
    [JsonPropertyName("backdrops")] public List<TmdbImage>? Backdrops { get; set; }
    [JsonPropertyName("stills")] public List<TmdbImage>? Stills { get; set; }
}

internal sealed class TmdbPerson
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("character")] public string? Character { get; set; }
    [JsonPropertyName("job")] public string? Job { get; set; }
    [JsonPropertyName("department")] public string? Department { get; set; }
    [JsonPropertyName("order")][JsonConverter(typeof(FlexibleIntConverter))] public int? Order { get; set; }
}

internal sealed class TmdbCredits
{
    [JsonPropertyName("cast")] public List<TmdbPerson>? Cast { get; set; }
    [JsonPropertyName("crew")] public List<TmdbPerson>? Crew { get; set; }
    [JsonPropertyName("guest_stars")] public List<TmdbPerson>? GuestStars { get; set; }
}

internal sealed class TmdbReleaseDate
{
    [JsonPropertyName("certification")] public string? Certification { get; set; }
    [JsonPropertyName("type")][JsonConverter(typeof(FlexibleIntConverter))] public int? Type { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
}

internal sealed class TmdbReleaseCountry
{
    [JsonPropertyName("iso_3166_1")] public string? Country { get; set; }
    [JsonPropertyName("release_dates")] public List<TmdbReleaseDate>? ReleaseDates { get; set; }
}

internal sealed class TmdbReleaseDates
{
    [JsonPropertyName("results")] public List<TmdbReleaseCountry>? Results { get; set; }
}

internal sealed class TmdbContentRating
{
    [JsonPropertyName("iso_3166_1")] public string? Country { get; set; }
    [JsonPropertyName("rating")] public string? Rating { get; set; }
}

internal sealed class TmdbContentRatings
{
    [JsonPropertyName("results")] public List<TmdbContentRating>? Results { get; set; }
}

internal sealed class TmdbMovie
{
    [JsonPropertyName("id")][JsonConverter(typeof(FlexibleIntConverter))] public int? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("tagline")] public string? Tagline { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; set; }
    [JsonPropertyName("imdb_id")] public string? ImdbId { get; set; }
    [JsonPropertyName("genres")] public List<TmdbNamed>? Genres { get; set; }
    [JsonPropertyName("production_companies")] public List<TmdbNamed>? ProductionCompanies { get; set; }
    [JsonPropertyName("credits")] public TmdbCredits? Credits { get; set; }
    [JsonPropertyName("release_dates")] public TmdbReleaseDates? ReleaseDates { get; set; }
    [JsonPropertyName("images")] public TmdbImages? Images { get; set; }
}

internal sealed class TmdbSeasonSummary
{
    [JsonPropertyName("season_number")][JsonConverter(typeof(FlexibleIntConverter))] public int? SeasonNumber { get; set; }
    [JsonPropertyName("episode_count")][JsonConverter(typeof(FlexibleIntConverter))] public int? EpisodeCount { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal sealed class TmdbTvShow
{
    [JsonPropertyName("id")][JsonConverter(typeof(FlexibleIntConverter))] public int? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("original_name")] public string? OriginalName { get; set; }
    [JsonPropertyName("first_air_date")] public string? FirstAirDate { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; set; }
    [JsonPropertyName("genres")] public List<TmdbNamed>? Genres { get; set; }
    [JsonPropertyName("networks")] public List<TmdbNamed>? Networks { get; set; }
    [JsonPropertyName("production_companies")] public List<TmdbNamed>? ProductionCompanies { get; set; }
    [JsonPropertyName("created_by")] public List<TmdbNamed>? CreatedBy { get; set; }
    [JsonPropertyName("seasons")] public List<TmdbSeasonSummary>? Seasons { get; set; }
    [JsonPropertyName("content_ratings")] public TmdbContentRatings? ContentRatings { get; set; }
    [JsonPropertyName("credits")] public TmdbCredits? Credits { get; set; }
    [JsonPropertyName("images")] public TmdbImages? Images { get; set; }
}

internal sealed class TmdbEpisode
{
    [JsonPropertyName("id")][JsonConverter(typeof(FlexibleIntConverter))] public int? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("air_date")] public string? AirDate { get; set; }
    [JsonPropertyName("episode_number")][JsonConverter(typeof(FlexibleIntConverter))] public int? EpisodeNumber { get; set; }
    [JsonPropertyName("season_number")][JsonConverter(typeof(FlexibleIntConverter))] public int? SeasonNumber { get; set; }
    [JsonPropertyName("still_path")] public string? StillPath { get; set; }
    [JsonPropertyName("production_code")] public string? ProductionCode { get; set; }
    [JsonPropertyName("crew")] public List<TmdbPerson>? Crew { get; set; }
    [JsonPropertyName("guest_stars")] public List<TmdbPerson>? GuestStars { get; set; }
    [JsonPropertyName("credits")] public TmdbCredits? Credits { get; set; }
    [JsonPropertyName("images")] public TmdbImages? Images { get; set; }
}

internal sealed class TmdbSeason
{
    [JsonPropertyName("season_number")][JsonConverter(typeof(FlexibleIntConverter))] public int? SeasonNumber { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("episodes")] public List<TmdbEpisode>? Episodes { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(TmdbPage<TmdbMovie>))]
[JsonSerializable(typeof(TmdbPage<TmdbTvShow>))]
[JsonSerializable(typeof(TmdbMovie))]
[JsonSerializable(typeof(TmdbTvShow))]
[JsonSerializable(typeof(TmdbSeason))]
[JsonSerializable(typeof(TmdbEpisode))]
internal sealed partial class TmdbJsonContext : JsonSerializerContext;
