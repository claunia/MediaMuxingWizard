using MMW.Core.Actions;
using MMW.Core.Chapters;
using MMW.Core.Model;

namespace MMW.Core.Tests;

public class ChapterTextTests
{
    [Fact]
    public void Parses_mp4chaps()
    {
        var chapters = ChapterTextFormat.Parse("00:00:00.000 Opening\n00:00:19.987 Part one\n00:01:00.000\n");
        Assert.Equal(["Opening", "Part one", "Chapter 3"], chapters.Select(c => c.Title));
        Assert.Equal(TimeSpan.FromMilliseconds(19_987), chapters[1].Start);
    }

    [Fact]
    public void Parses_ogg_and_round_trips()
    {
        var text = "CHAPTER01=00:00:00.000\r\nCHAPTER01NAME=Intro\r\nCHAPTER02=00:05:00.500\r\nCHAPTER02NAME=Main = part\r\n";
        var chapters = ChapterTextFormat.Parse(text);
        Assert.Equal(["Intro", "Main = part"], chapters.Select(c => c.Title));
        Assert.Equal(TimeSpan.FromMilliseconds(300_500), chapters[1].Start);

        var again = ChapterTextFormat.Parse(ChapterTextFormat.ToOgg(chapters));
        Assert.Equal(chapters.Select(c => (c.Start, c.Title)), again.Select(c => (c.Start, c.Title)));
    }

    [Fact]
    public void Csv_titles_ignore_the_index_column()
    {
        Assert.Equal(["One", "Two, with comma"], ChapterTextFormat.ParseTitlesCsv("1,One\n2,\"Two, with comma\"\n"));
    }

    [Fact]
    public void Insert_chapters_every_interval_covers_the_duration()
    {
        var doc = new MediaDocument(null, ContainerKind.Mp4) { Duration = TimeSpan.FromMinutes(12) };
        TrackActions.InsertChaptersEvery(doc, TimeSpan.FromMinutes(5));
        Assert.Equal([0, 5, 10], doc.Chapters.Select(c => (int)c.Start.TotalMinutes));
        Assert.Single(doc.Tracks.OfType<ChapterTrack>());
    }
}
