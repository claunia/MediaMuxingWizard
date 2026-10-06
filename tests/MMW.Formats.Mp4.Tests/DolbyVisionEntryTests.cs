using MMW.Core.Media.Codecs;
using MMW.Formats.Mp4.Boxes;

namespace MMW.Formats.Mp4.Tests;

/// <summary>Dolby Vision signalling of visual sample entries ("Dolby Vision Streams Within the ISO Base Media File Format").</summary>
public sealed class DolbyVisionEntryTests
{
    private static Box Entry(string type, string config, params Box[] extra) =>
        new(type, new byte[78], [new Box(config, [1, 2, 3, 4]), .. extra, new Box("colr", [.. "nclx"u8, 0, 9, 0, 16, 0, 9, 0])]);

    private static string Layout(Box entry) => entry.Type + ":" + string.Join(",", entry.Children!.Select(c => c.Type));

    [Fact]
    public void Profile_5_gets_dvh1_and_dvcC_after_hvcC()
    {
        var entry = Entry("hvc1", "hvcC");
        var info = DolbyVisionEntry.Apply(entry, DolbyVision.BuildConfigurationRecord(5, 6, true, false, true, 0));
        Assert.Equal(5, info!.Profile);
        Assert.Equal("dvh1:hvcC,dvcC,colr", Layout(entry));
    }

    [Fact]
    public void Existing_configuration_is_normalised_and_replaced()
    {
        // A profile 8.1 stream wrongly stored as dvhe with a dvcC box: back to hev1 + dvvC.
        var entry = Entry("dvhe", "hvcC", new Box("dvcC", DolbyVision.BuildConfigurationRecord(8, 6, true, false, true, 1)));
        DolbyVisionEntry.Apply(entry);
        Assert.Equal("hev1:hvcC,dvvC,colr", Layout(entry));

        // Repair with a different record replaces the box.
        var record = DolbyVision.BuildConfigurationRecord(8, 9, true, false, true, 4);
        DolbyVisionEntry.Apply(entry, record);
        Assert.Equal(record, Assert.Single(entry.Children!, c => c.Type == "dvvC").Payload);
    }

    [Fact]
    public void Single_track_dual_layer_gets_hvcE()
    {
        var entry = Entry("hev1", "hvcC");
        DolbyVisionEntry.Apply(entry, DolbyVision.BuildConfigurationRecord(7, 6, true, true, true, 6));
        Assert.Equal("hev1:hvcC,dvcC,hvcE,colr", Layout(entry));
        Assert.Equal(entry.Children![0].Payload, entry.Children[2].Payload);

        // Applying again does not duplicate it; an EL-only track (dual-track storage) has none.
        DolbyVisionEntry.Apply(entry);
        Assert.Single(entry.Children!, c => c.Type == "hvcE");
        var el = Entry("hev1", "hvcC");
        DolbyVisionEntry.Apply(el, DolbyVision.BuildConfigurationRecord(7, 6, true, true, false, 6));
        Assert.Equal("hev1:hvcC,dvcC,colr", Layout(el));
    }

    [Fact]
    public void Av1_profile_10_without_compatible_base_layer_is_av01_unless_dav1_is_requested()
    {
        var record = DolbyVision.BuildConfigurationRecord(10, 6, true, false, true, 0);
        var entry = Entry("av01", "av1C");
        DolbyVisionEntry.Apply(entry, record);
        Assert.Equal("av01:av1C,dvvC,colr", Layout(entry));

        DolbyVisionEntry.Av1UsesDav1 = true;
        try
        {
            DolbyVisionEntry.Apply(entry);
            Assert.Equal("dav1:av1C,dvvC,colr", Layout(entry));
        }
        finally
        {
            DolbyVisionEntry.Av1UsesDav1 = false;
        }

        DolbyVisionEntry.Apply(entry); // back to av01
        Assert.Equal("av01:av1C,dvvC,colr", Layout(entry));
    }

    [Fact]
    public void Entries_without_dolby_vision_are_left_alone()
    {
        var entry = Entry("hvc1", "hvcC");
        Assert.Null(DolbyVisionEntry.Apply(entry));
        Assert.Equal("hvc1:hvcC,colr", Layout(entry));
    }
}
