using MMW.Core.Chapters;
using MMW.Core.Model;
using MMW.Core.Undo;

namespace MMW.Core.Tests;

public class UndoTests
{
    [Fact]
    public void Property_changes_are_undoable_and_merged()
    {
        var stack = new UndoStack();
        var tracker = new ObservableUndoTracker(stack);
        var track = new AudioTrack { Name = "A" };
        tracker.Track(track);

        track.Name = "B";
        track.Name = "C";
        track.Language = "fr";

        stack.Undo();
        Assert.Equal("und", track.Language);
        Assert.Equal("C", track.Name);
        stack.Undo();
        Assert.Equal("A", track.Name);
        stack.Redo();
        Assert.Equal("C", track.Name);
    }

    [Fact]
    public void Collection_changes_are_undoable_and_new_items_tracked()
    {
        var stack = new UndoStack();
        var tracker = new ObservableUndoTracker(stack);
        var doc = new MediaDocument(null, ContainerKind.Mp4);
        tracker.TrackCollection(doc.Chapters, "Chapter");

        var chapter = new Chapter(TimeSpan.Zero, "One");
        doc.Chapters.Add(chapter);
        chapter.Title = "Uno";

        stack.Undo();
        Assert.Equal("One", chapter.Title);
        stack.Undo();
        Assert.Empty(doc.Chapters);
        stack.Redo();
        Assert.Same(chapter, Assert.Single(doc.Chapters));
    }

    [Fact]
    public void Media_characteristics_collection_inside_a_track_is_tracked()
    {
        var stack = new UndoStack();
        var tracker = new ObservableUndoTracker(stack);
        var track = new SubtitleTrack();
        tracker.Track(track);

        track.MediaCharacteristics.Add(MediaCharacteristics.ForcedOnly);
        stack.Undo();

        Assert.Empty(track.MediaCharacteristics);
    }
}

public class UndoTransactionTests
{
    [Fact]
    public void Transaction_groups_edits_into_one_step()
    {
        var stack = new UndoStack();
        var tracker = new ObservableUndoTracker(stack);
        var doc = new MediaDocument(null, ContainerKind.Mp4) { Duration = TimeSpan.FromMinutes(3) };
        tracker.TrackCollection(doc.Chapters, "Chapter");
        tracker.TrackCollection(doc.Tracks, "Track");
        doc.Chapters.Add(new MMW.Core.Chapters.Chapter(TimeSpan.Zero, "Old"));

        using (stack.Transaction("Insert chapters"))
            MMW.Core.Actions.TrackActions.InsertChaptersEvery(doc, TimeSpan.FromMinutes(1));

        Assert.Equal(3, doc.Chapters.Count);
        Assert.Equal("Insert chapters", stack.UndoDescription);
        stack.Undo();
        Assert.Equal("Old", Assert.Single(doc.Chapters).Title);
        Assert.Empty(doc.Tracks);
        stack.Redo();
        Assert.Equal(3, doc.Chapters.Count);
    }
}

public class ConversionUndoTests
{
    [Fact]
    public void Choosing_a_conversion_can_be_undone()
    {
        var stack = new UndoStack();
        var tracker = new ObservableUndoTracker(stack);
        var doc = new MediaDocument("/tmp/movie.mkv", ContainerKind.Matroska);
        var dts = new AudioTrack { Id = 2, Format = "DTS", CodecId = "A_DTS", Channels = 6, Enabled = true, Source = new TrackSource("/tmp/movie.mkv", ContainerKind.Matroska, 2) };
        doc.Tracks.Add(new VideoTrack { Id = 1, Source = new TrackSource("/tmp/movie.mkv", ContainerKind.Matroska, 1) });
        doc.Tracks.Add(dts);
        tracker.TrackCollection(doc.Tracks, "Track");

        using (stack.Transaction("Convert DTS"))
            MMW.Core.Media.TrackConversions.SetAction(doc, dts, MMW.Core.Media.ImportAction.AacPlusPassthrough);
        Assert.Equal(3, doc.Tracks.Count);

        stack.Undo();
        Assert.Equal(2, doc.Tracks.Count);
        Assert.Null(dts.Source!.Import);
        Assert.True(dts.Enabled);
        Assert.Null(dts.Fallback);
    }
}
