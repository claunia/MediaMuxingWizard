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
