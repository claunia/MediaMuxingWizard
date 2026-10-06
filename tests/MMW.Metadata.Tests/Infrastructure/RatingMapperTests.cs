using MMW.Metadata.Certifications;
using MMW.Metadata.Search;

namespace MMW.Metadata.Tests.Infrastructure;

public sealed class RatingMapperTests
{
    [Theory]
    [InlineData("PG-13", "US", MediaSearchKind.Movie, "mpaa|PG-13|300|")]
    [InlineData("Rated R", "US", MediaSearchKind.Movie, "mpaa|R|400|")]
    [InlineData("pg-13", null, MediaSearchKind.Movie, "mpaa|PG-13|300|")]
    [InlineData("US:PG", "DE", MediaSearchKind.Movie, "mpaa|PG|200|")]
    [InlineData("TV-14", "US", MediaSearchKind.TvEpisode, "us-tv|TV-14|500|")]
    [InlineData("TV_MA", "US", MediaSearchKind.TvEpisode, "us-tv|TV-MA|600|")]
    [InlineData("TV-PG", "US", MediaSearchKind.Movie, "us-tv|TV-PG|400|")]
    [InlineData("16", "DE", MediaSearchKind.Movie, "de-movie|Ab 16 Jahren|500|")]
    [InlineData("FSK 12", "DE", MediaSearchKind.TvEpisode, "de-tv|Ab 12 Jahren|200|")]
    [InlineData("15", "GB", MediaSearchKind.Movie, "uk-movie|15|350|")]
    [InlineData("12A", "GB", MediaSearchKind.Movie, "uk-movie|12A|325|")]
    [InlineData("NR", "US", MediaSearchKind.Movie, "mpaa|NR|000|")]
    [InlineData("Not Rated", "ES", MediaSearchKind.Movie, null)]
    [InlineData("", "US", MediaSearchKind.Movie, null)]
    [InlineData("XYZ", "US", MediaSearchKind.Movie, null)]
    public void MapsCertifications(string certification, string? country, MediaSearchKind kind, string? expected) =>
        Assert.Equal(expected, RatingMapper.Encode(certification, country, kind));

    [Fact]
    public void KnowsRatingsCountryNames()
    {
        Assert.Equal("USA", RatingMapper.RatingsCountry("us"));
        Assert.Equal("UK", RatingMapper.RatingsCountry("GB"));
        Assert.Equal("Deutschland", RatingMapper.RatingsCountry("DE"));
        Assert.Null(RatingMapper.RatingsCountry("ZZ"));
    }
}
