using MMW.Core.Media.Codecs;

namespace MMW.Core.Tests;

/// <summary>AV1 raw stream helpers: profile and level names, sample normalisation, Annex B, sync samples.</summary>
public sealed class Av1Tests
{
    [Theory]
    [InlineData(0, 0, 0, "Main@L2.0")]
    [InlineData(0, 13, 1, "Main@L5.1 High tier")]
    [InlineData(1, 8, 0, "High@L4.0")]
    [InlineData(2, 31, 0, "Professional@max")]
    public void Names_profiles_and_levels(int profile, int level, int tier, string expected) =>
        Assert.Equal(expected, Av1.ProfileLevel(profile, level, tier));

    [Fact]
    public void Reads_the_profile_of_av1c() =>
        Assert.Equal("Main@L5.1 High tier", Av1.ProfileLevel([0x81, 0x0D, 0x80, 0x00]));

    /// <summary>A temporal delimiter, a key frame header (shown) and padding; as low-overhead and Annex B data.</summary>
    [Fact]
    public void Normalises_temporal_units()
    {
        byte[] frame = [0x32, 0x02, 0x10, 0xAA]; // OBU_FRAME_HEADER with size 2: show_existing 0, KEY_FRAME, show_frame 1
        byte[] lowOverhead = [0x12, 0x00, .. frame, 0x7A, 0x01, 0x00]; // TD, frame header, padding (type 15)
        var sample = Av1.ToSample(lowOverhead);
        Assert.Equal(frame, sample);
        Assert.True(Av1.IsSync(sample, reducedStillPictureHeader: false));
        Assert.False(Av1.IsSync([0x32, 0x02, 0x30, 0xAA], false)); // INTER_FRAME
        Assert.False(Av1.IsSync([0x32, 0x02, 0x00, 0xAA], false)); // key frame not shown

        // Annex B: temporal_unit_size, frame_unit_size, obu_length + OBUs without size fields.
        byte[] annexB = [7, 6, 1, 0x10, 3, 0x30, 0x10, 0xAA];
        Assert.Equal([0x12, 0x00, .. frame], Av1.FromAnnexB(annexB[1..]));
    }
}
