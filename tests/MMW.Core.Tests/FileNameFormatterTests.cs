using MMW.Core.Metadata;

namespace MMW.Core.Tests;

public class FileNameFormatterTests
{
    private static MetadataSet Tv()
    {
        var m = new MetadataSet();
        m.Set(TagId.TvShow, "The Show: Reborn");
        m.Set(TagId.TvSeason, 2);
        m.Set(TagId.TvEpisodeNumber, 5);
        m.Set(TagId.Name, "Pilot / Part 1");
        m.Set(TagId.MediaKind, 10);
        m.Set(TagId.ReleaseDate, "2021-03-04T00:00:00Z");
        return m;
    }

    [Fact]
    public void Default_tv_pattern_pads_and_sanitizes()
    {
        Assert.Equal("The Show Reborn s02e05", FileNameFormatter.FormatFor(Tv()));
    }

    [Theory]
    [InlineData("{Name}", "Pilot - Part 1")]
    [InlineData("{Name|upper}", "PILOT - PART 1")]
    [InlineData("{TV Show|snake}", "the_show_reborn")]
    [InlineData("{TV Show|dot}.S{TV Season:00}E{TV Episode #:000}", "The.Show.Reborn.S02E005")]
    [InlineData("{Name} ({Release Date:yyyy})", "Pilot - Part 1 (2021)")]
    [InlineData("{TV Show|camel}", "theShowReborn")]
    public void Tokens_support_formats_and_transforms(string pattern, string expected)
    {
        Assert.Equal(expected, FileNameFormatter.Format(pattern, Tv()));
    }

    [Fact]
    public void Returns_null_when_no_token_has_a_value()
    {
        Assert.Null(FileNameFormatter.Format("{Name} - {Album}", new MetadataSet()));
    }
}
