using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.Metadata.Search;

namespace MMW.Metadata.Tests.Mapping;

public sealed class SearchPrefillTests
{
    [Fact]
    public void TvTags_WinOverFileName()
    {
        var doc = new MediaDocument("/media/Some.Movie.2010.mkv", ContainerKind.Matroska);
        doc.Metadata.Set(TagId.MediaKind, 10);
        doc.Metadata.Set(TagId.TvShow, "Breaking Bad");
        doc.Metadata.Set(TagId.TvSeason, 2);
        doc.Metadata.Set(TagId.TvEpisodeNumber, 5);

        Assert.Equal(new SearchQuery(MediaSearchKind.TvEpisode, "Breaking Bad", null, 2, 5, "eng"), SearchPrefill.From(doc, "eng"));
    }

    [Fact]
    public void MovieTags_UseNameAndReleaseYear()
    {
        var doc = new MediaDocument("/media/whatever.mp4", ContainerKind.Mp4);
        doc.Metadata.Set(TagId.MediaKind, 9);
        doc.Metadata.Set(TagId.Name, "The Matrix");
        doc.Metadata.Set(TagId.ReleaseDate, "1999-03-31T08:00:00Z");

        Assert.Equal(new SearchQuery(MediaSearchKind.Movie, "The Matrix", 1999, null, null, ""), SearchPrefill.From(doc));
    }

    [Fact]
    public void WithoutIdentifyingTags_ParsesFileName()
    {
        var doc = new MediaDocument("/media/Lost.S01E02.720p.mkv", ContainerKind.Matroska);
        doc.Metadata.Set(TagId.Name, "Some random title"); // no media kind → not trusted

        Assert.Equal(new SearchQuery(MediaSearchKind.TvEpisode, "Lost", null, 1, 2, ""), SearchPrefill.From(doc));
    }

    [Fact]
    public void UnsavedDocument_FallsBackToNameTag()
    {
        var doc = new MediaDocument(null, ContainerKind.Mp4);
        doc.Metadata.Set(TagId.Name, "Casablanca");

        Assert.Equal(new SearchQuery(MediaSearchKind.Movie, "Casablanca", null, null, null, ""), SearchPrefill.From(doc));
        Assert.Equal(string.Empty, SearchPrefill.From(new MediaDocument(null, ContainerKind.Mp4)).Title);
    }
}
