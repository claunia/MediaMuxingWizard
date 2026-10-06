using MMW.Metadata.Parsing;
using MMW.Metadata.Search;

namespace MMW.Metadata.Tests.Parsing;

public sealed class FileNameParserTests
{
    public static TheoryData<string, string, int?, int?> TvCases => new()
    {
        // file name, expected title, season, episode
        { "Breaking.Bad.S01E02.720p.HDTV.x264-CTU.mkv", "Breaking Bad", 1, 2 },
        { "breaking.bad.s1e2.mkv", "breaking bad", 1, 2 },
        { "The Office (US) - S02E01 - The Dundies.mp4", "The Office (US)", 2, 1 },
        { "Game of Thrones S08E06 1080p WEB H264-MEMENTO.mkv", "Game of Thrones", 8, 6 },
        { "game_of_thrones_s03e09_the_rains_of_castamere.mkv", "game of thrones", 3, 9 },
        { "Lost.1x02.Pilot.Part.2.avi", "Lost", 1, 2 },
        { "Lost 1x02.avi", "Lost", 1, 2 },
        { "Friends - 10x17-18 - The Last One.avi", "Friends", 10, 17 },
        { "Twin Peaks Season 2 Episode 7.mkv", "Twin Peaks", 2, 7 },
        { "Twin.Peaks.Season.2.Episode.7.mkv", "Twin Peaks", 2, 7 },
        { "Twin Peaks - Season 2 - Episode 07.mkv", "Twin Peaks", 2, 7 },
        { "The.Expanse.S02.E03.1080p.mkv", "The Expanse", 2, 3 },
        { "The Expanse S02 E03.mkv", "The Expanse", 2, 3 },
        { "Sherlock.S01E01E02.mkv", "Sherlock", 1, 1 },
        { "Sherlock.S01E01-E02.mkv", "Sherlock", 1, 1 },
        { "Sherlock.S01E01-02.mkv", "Sherlock", 1, 1 },
        { "Doctor.Who.2005.S10E01.The.Pilot.mkv", "Doctor Who", 10, 1 },
        { "Doctor Who (2005) - S10E01.mkv", "Doctor Who", 10, 1 },
        { "Marvels.Agents.of.S.H.I.E.L.D.S05E01.720p.mkv", "Marvels Agents of S.H.I.E.L.D.", 5, 1 },
        { "Star Trek - The Next Generation - S03E15 - Yesterday's Enterprise.mkv", "Star Trek - The Next Generation", 3, 15 },
        { "The.Simpsons.S35E100.mkv", "The Simpsons", 35, 100 },
        { "one_piece_s01e1071.mkv", "one piece", 1, 1071 },
        { "Mr. Robot S04E13.mkv", "Mr. Robot", 4, 13 },
        { "[Group] Breaking Bad S01E02 [720p].mkv", "Breaking Bad", 1, 2 },
        { "24.S01E01.12.00.A.M.-1.00.A.M.DVDRip.avi", "24", 1, 1 },
        { "Shogun.2024.S01E01.Anjin.2160p.DSNP.WEB-DL.DDP5.1.H.265-NTb.mkv", "Shogun", 1, 1 },
    };

    [Theory]
    [MemberData(nameof(TvCases))]
    public void RecognisesTvEpisodes(string fileName, string title, int? season, int? episode)
    {
        var parsed = FileNameParser.Parse(fileName);

        Assert.Equal(MediaSearchKind.TvEpisode, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
    }

    public static TheoryData<string, string, int?> MovieCases => new()
    {
        // file name, expected title, year
        { "The Matrix (1999).mkv", "The Matrix", 1999 },
        { "The.Matrix.1999.1080p.BluRay.x264-GROUP.mkv", "The Matrix", 1999 },
        { "Inception.2010.720p.BRRip.XviD.AC3-ViSiON.avi", "Inception", 2010 },
        { "Blade_Runner_2049_2017_2160p_UHD_BluRay_x265.mkv", "Blade Runner 2049", 2017 },
        { "2001.A.Space.Odyssey.1968.Remastered.1080p.mkv", "2001 A Space Odyssey", 1968 },
        { "1917 (2019) [1080p].mp4", "1917", 2019 },
        { "Alien [1979] Director's Cut.mkv", "Alien", 1979 },
        { "Amélie.2001.FRENCH.1080p.mkv", "Amélie", 2001 },
        { "Spider-Man.Into.the.Spider-Verse.2018.WEB-DL.mkv", "Spider-Man Into the Spider-Verse", 2018 },
        { "Mad Max Fury Road 2015.mp4", "Mad Max Fury Road", 2015 },
        { "Dune.Part.Two.2024.2160p.WEB-DL.DDP5.1.Atmos.DV.HDR.H.265-FLUX.mkv", "Dune Part Two", 2024 },
        { "Pulp.Fiction.1080p.BluRay.x264.mkv", "Pulp Fiction", null },
        { "Casablanca.mkv", "Casablanca", null },
        { "Some Home Video", "Some Home Video", null },
        { "Ocean's.Eleven.2001.DVDRip.avi", "Ocean's Eleven", 2001 },
        { "Star Wars Episode IV - A New Hope (1977).mkv", "Star Wars Episode IV - A New Hope", 1977 },
    };

    [Theory]
    [MemberData(nameof(MovieCases))]
    public void RecognisesMovies(string fileName, string title, int? year)
    {
        var parsed = FileNameParser.Parse(fileName);

        Assert.Equal(MediaSearchKind.Movie, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.Null(parsed.Season);
        Assert.Null(parsed.Episode);
    }

    public static TheoryData<string, string, int?, int?> AnimeCases => new()
    {
        { "[HorribleSubs] One Punch Man - 05 [1080p].mkv", "One Punch Man", 1, 5 },
        { "[SubsPlease] Frieren - 12 (1080p) [ABCD1234].mkv", "Frieren", 1, 12 },
        { "[Erai-raws] Kimetsu no Yaiba - 03v2 [720p][Multiple Subtitle].mkv", "Kimetsu no Yaiba", 1, 3 },
        { "[Group] Shingeki no Kyojin S3 - 01 [BD 1080p].mkv", "Shingeki no Kyojin", 3, 1 },
        { "[Judas] Cowboy Bebop - 026 [1080p][HEVC x265 10bit].mkv", "Cowboy Bebop", 1, 26 },
    };

    [Theory]
    [MemberData(nameof(AnimeCases))]
    public void RecognisesAnimeReleases(string fileName, string title, int? season, int? episode)
    {
        var parsed = FileNameParser.Parse(fileName);

        Assert.Equal(MediaSearchKind.TvEpisode, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
        Assert.NotNull(parsed.Group);
    }

    [Fact]
    public void MultiEpisode_ReportsLastEpisode()
    {
        Assert.Equal(2, FileNameParser.Parse("Sherlock.S01E01E02.mkv").LastEpisode);
        Assert.Equal(3, FileNameParser.Parse("Show.S02E01-E02-E03.mkv").LastEpisode);
        Assert.Equal(18, FileNameParser.Parse("Friends - 10x17-18 - The Last One.avi").LastEpisode);
        Assert.Null(FileNameParser.Parse("Show.S02E05.mkv").LastEpisode);
    }

    [Theory]
    [InlineData("The.Daily.Show.2024.03.05.Guest.Name.720p.WEB.h264.mkv", "The Daily Show", 2024, 3, 5)]
    [InlineData("Last Week Tonight with John Oliver 2023-11-12.mkv", "Last Week Tonight with John Oliver", 2023, 11, 12)]
    [InlineData("Jeopardy_2019_01_31.mp4", "Jeopardy", 2019, 1, 31)]
    public void RecognisesDateBasedEpisodes(string fileName, string title, int year, int month, int day)
    {
        var parsed = FileNameParser.Parse(fileName);

        Assert.Equal(MediaSearchKind.TvEpisode, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(new DateOnly(year, month, day), parsed.AirDate);
        Assert.Equal(year, parsed.Year);
        Assert.Null(parsed.Season);
    }

    [Theory]
    [InlineData("/media/TV/Breaking Bad/Season 1/S01E02.mkv", "Breaking Bad")]
    [InlineData("/media/TV/Doctor Who (2005)/Season 10/S10E01 - The Pilot.mkv", "Doctor Who")]
    [InlineData("/media/TV/The Wire/S02/1x02.mkv", "The Wire")]
    public void TakesSeriesNameFromFolders(string path, string title)
    {
        var parsed = FileNameParser.Parse(path);

        Assert.Equal(MediaSearchKind.TvEpisode, parsed.Kind);
        Assert.Equal(title, parsed.Title);
    }

    [Fact]
    public void ReleaseGroupIsReported()
    {
        Assert.Equal("CTU", FileNameParser.Parse("Breaking.Bad.S01E02.720p.HDTV.x264-CTU.mkv").Group);
        Assert.Equal("GROUP", FileNameParser.Parse("The.Matrix.1999.1080p.BluRay.x264-GROUP.mkv").Group);
    }

    [Fact]
    public void DoesNotStripUnknownExtensions()
    {
        var parsed = FileNameParser.Parse("Title.2010.1080p.BluRay.x264");

        Assert.Equal("Title", parsed.Title);
        Assert.Equal(2010, parsed.Year);
    }

    [Fact]
    public void ToQuery_CarriesEverything()
    {
        var q = FileNameParser.Parse("Lost.S01E02.mkv").ToQuery("eng");

        Assert.Equal(new SearchQuery(MediaSearchKind.TvEpisode, "Lost", null, 1, 2, "eng"), q);
    }
}
