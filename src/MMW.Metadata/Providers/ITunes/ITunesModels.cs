using System.Text.Json.Serialization;
using MMW.Metadata.Http;

namespace MMW.Metadata.Providers.ITunes;

internal sealed class ITunesResponse
{
    [JsonPropertyName("resultCount")][JsonConverter(typeof(FlexibleIntConverter))] public int? ResultCount { get; set; }
    [JsonPropertyName("results")] public List<ITunesItem>? Results { get; set; }
}

internal sealed class ITunesItem
{
    [JsonPropertyName("wrapperType")] public string? WrapperType { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("collectionType")] public string? CollectionType { get; set; }
    [JsonPropertyName("artistId")][JsonConverter(typeof(FlexibleStringConverter))] public string? ArtistId { get; set; }
    [JsonPropertyName("collectionId")][JsonConverter(typeof(FlexibleStringConverter))] public string? CollectionId { get; set; }
    [JsonPropertyName("trackId")][JsonConverter(typeof(FlexibleStringConverter))] public string? TrackId { get; set; }
    [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
    [JsonPropertyName("collectionName")] public string? CollectionName { get; set; }
    [JsonPropertyName("trackName")] public string? TrackName { get; set; }
    [JsonPropertyName("trackViewUrl")] public string? TrackViewUrl { get; set; }
    [JsonPropertyName("collectionViewUrl")] public string? CollectionViewUrl { get; set; }
    [JsonPropertyName("artworkUrl100")] public string? ArtworkUrl100 { get; set; }
    [JsonPropertyName("artworkUrl60")] public string? ArtworkUrl60 { get; set; }
    [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("primaryGenreName")] public string? PrimaryGenreName { get; set; }
    [JsonPropertyName("contentAdvisoryRating")] public string? ContentAdvisoryRating { get; set; }
    [JsonPropertyName("shortDescription")] public string? ShortDescription { get; set; }
    [JsonPropertyName("longDescription")] public string? LongDescription { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("copyright")] public string? Copyright { get; set; }
    [JsonPropertyName("trackNumber")][JsonConverter(typeof(FlexibleIntConverter))] public int? TrackNumber { get; set; }
    [JsonPropertyName("trackCount")][JsonConverter(typeof(FlexibleIntConverter))] public int? TrackCount { get; set; }
    [JsonPropertyName("discNumber")][JsonConverter(typeof(FlexibleIntConverter))] public int? DiscNumber { get; set; }
    [JsonPropertyName("discCount")][JsonConverter(typeof(FlexibleIntConverter))] public int? DiscCount { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ITunesResponse))]
internal sealed partial class ITunesJsonContext : JsonSerializerContext;
