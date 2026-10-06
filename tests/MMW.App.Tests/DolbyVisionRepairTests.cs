using System.Text;
using Avalonia.Headless.XUnit;
using MMW.App.Services;
using MMW.App.ViewModels;
using MMW.Core.Model;
using MMW.TestSupport;

namespace MMW.App.Tests;

public class DolbyVisionRepairTests
{
    /// <summary>A corpus profile 8.4 MP4 whose dvvC box was hidden: the document offers to rebuild it.</summary>
    [AvaloniaFact]
    public async Task Missing_dolby_vision_configuration_is_offered_for_repair_and_undoable()
    {
        var source = Corpus.Directory is { } dir
            ? Directory.EnumerateFiles(Path.Combine(dir, "High Dynamic Range", "Dolby Vision", "Profile 8"), "*.mp4").MinBy(f => new FileInfo(f).Length)
            : null;
        Corpus.Require(source ?? string.Empty);
        var copy = Fixtures.CopyToTemp(source!);
        try
        {
            var data = File.ReadAllBytes(copy);
            var at = data.AsSpan().IndexOf("dvvC"u8);
            Assert.True(at > 0);
            Encoding.ASCII.GetBytes("free").CopyTo(data, at);
            File.WriteAllBytes(copy, data);

            var settings = new SettingsService(Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"), "settings.json"));
            var vm = new MainWindowViewModel(new DocumentService(), new FakeDialogService(), settings);
            await vm.OpenPathsAsync([copy]);
            var doc = vm.Documents.Single();
            var video = doc.Document.Tracks.OfType<VideoTrack>().First();
            Assert.Null(video.DolbyVisionRecord);

            await doc.ScanVideoAsync();
            Assert.True(doc.HasDolbyVisionNotice);
            Assert.Contains("8.4", doc.DolbyVisionNotice!, StringComparison.Ordinal);
            Assert.False(doc.IsDirty);

            doc.RepairDolbyVisionCommand.Execute(null);
            Assert.False(doc.HasDolbyVisionNotice);
            Assert.True(doc.IsDirty);
            Assert.Equal((8, 4), (video.DolbyVision!.Profile, video.DolbyVision.BlSignalCompatibilityId));

            doc.UndoCommand.Execute(null);
            Assert.Null(video.DolbyVisionRecord);
            Assert.Null(video.DolbyVision);
            doc.RedoCommand.Execute(null);
            Assert.Equal(8, video.DolbyVision!.Profile);
        }
        finally
        {
            MediaProbe.Delete(copy);
        }
    }
}
