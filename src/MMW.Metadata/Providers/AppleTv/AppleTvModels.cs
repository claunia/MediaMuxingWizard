using System.Text.Json.Serialization;
using MMW.Metadata.Http;

namespace MMW.Metadata.Providers.AppleTv;

// Models for the undocumented Apple TV "UTS" API; everything optional.

internal sealed class AtvEnvelope<T>
{
    [JsonPropertyName("data")] public T? Data { get; set; }
}

internal sealed class AtvSearchData
{
    [JsonPropertyName("canvas")] public AtvCanvas? Canvas { get; set; }
}

internal sealed class AtvCanvas
{
    [JsonPropertyName("shelves")] public List<AtvShelf>? Shelves { get; set; }
}

internal sealed class AtvShelf
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("items")] public List<AtvItem>? Items { get; set; }
}

internal sealed class AtvImage
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("width")][JsonConverter(typeof(FlexibleIntConverter))] public int? Width { get; set; }
    [JsonPropertyName("height")][JsonConverter(typeof(FlexibleIntConverter))] public int? Height { get; set; }
}

internal sealed class AtvRating
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("system")] public string? System { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
}

internal sealed class AtvGenre
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal sealed class AtvItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("releaseDate")] public long? ReleaseDate { get; set; }
    [JsonPropertyName("rating")] public AtvRating? Rating { get; set; }
    [JsonPropertyName("genres")] public List<AtvGenre>? Genres { get; set; }
    [JsonPropertyName("images")] public Dictionary<string, AtvImage>? Images { get; set; }
    [JsonPropertyName("studio")] public string? Studio { get; set; }
    [JsonPropertyName("network")] public string? Network { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("adamId")][JsonConverter(typeof(FlexibleStringConverter))] public string? AdamId { get; set; }

    // Episode fields
    [JsonPropertyName("showId")] public string? ShowId { get; set; }
    [JsonPropertyName("seasonId")] public string? SeasonId { get; set; }
    [JsonPropertyName("showTitle")] public string? ShowTitle { get; set; }
    [JsonPropertyName("seasonNumber")][JsonConverter(typeof(FlexibleIntConverter))] public int? SeasonNumber { get; set; }
    [JsonPropertyName("episodeNumber")][JsonConverter(typeof(FlexibleIntConverter))] public int? EpisodeNumber { get; set; }
    [JsonPropertyName("showImages")] public Dictionary<string, AtvImage>? ShowImages { get; set; }
    [JsonPropertyName("seasonImages")] public Dictionary<string, AtvImage>? SeasonImages { get; set; }
}

internal sealed class AtvRole
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("personName")] public string? PersonName { get; set; }
}

internal sealed class AtvProductData
{
    [JsonPropertyName("content")] public AtvItem? Content { get; set; }
    [JsonPropertyName("show")] public AtvItem? Show { get; set; }
    [JsonPropertyName("roles")] public List<AtvRole>? Roles { get; set; }
}

internal sealed class AtvEpisodesData
{
    [JsonPropertyName("episodes")] public List<AtvItem>? Episodes { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(AtvEnvelope<AtvSearchData>))]
[JsonSerializable(typeof(AtvEnvelope<AtvProductData>))]
[JsonSerializable(typeof(AtvEnvelope<AtvEpisodesData>))]
internal sealed partial class AppleTvJsonContext : JsonSerializerContext;
