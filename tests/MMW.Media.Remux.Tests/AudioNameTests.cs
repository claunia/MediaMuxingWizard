using MMW.Core.Actions;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>Prettified audio track names: layout, codec as listeners know it, Dolby Atmos and the DTS product.</summary>
public sealed class AudioNameTests
{
    [Theory]
    [InlineData("TrueHD", 8, true, "", "7.1 Surround (Dolby TrueHD Atmos)")]
    [InlineData("TrueHD", 6, false, "", "5.1 Surround (Dolby TrueHD)")]
    [InlineData("E-AC-3", 6, true, "", "5.1 Surround (Dolby Digital Plus Atmos)")]
    [InlineData("AC-3", 6, false, "", "5.1 Surround (Dolby Digital)")]
    [InlineData("DTS", 8, false, "DTS:X", "7.1 Surround (DTS:X)")]
    [InlineData("DTS", 6, false, "DTS-HD MA", "5.1 Surround (DTS-HD MA)")]
    [InlineData("DTS", 6, false, "", "5.1 Surround (DTS)")]
    [InlineData("AAC", 2, false, "", "Stereo (AAC)")]
    [InlineData("Opus", 1, false, "", "Mono (Opus)")]
    [InlineData("ALAC", 2, false, "", "Stereo (Apple Lossless)")]
    [InlineData("TrueHD", 0, true, "", "Dolby TrueHD Atmos")]
    public void Names_say_layout_codec_and_atmos(string format, int channels, bool atmos, string profile, string expected)
    {
        var audio = new AudioTrack { Format = format, Channels = channels, IsAtmos = atmos, Profile = profile };
        Assert.Equal(expected, TrackActions.PrettyAudioName(audio));
    }

    public static TheoryData<string, string> CorpusFiles() => new()
    {
        { "{Dolby Digital + Atmos - Matroska} LG Amaze.mkv", "5.1 Surround (Dolby Digital Plus Atmos)" },
        { "{Dolby Digital + Atmos - MP4} LG Amaze.mp4", "5.1 Surround (Dolby Digital Plus Atmos)" },
        { "{Dolby TrueHD 5.1 - Matroska} THX Tex Moo Can.mkv", "5.1 Surround (Dolby TrueHD)" },
        { "{Dolby TrueHD 5.1 - MP4} THX Tex Moo Can.mp4", "5.1 Surround (Dolby TrueHD)" },
        { "{Dolby TrueHD 7.1 - Matroska} Dolby Spheres.mkv", "7.1 Surround (Dolby TrueHD)" },
        { "{Dolby TrueHD 7.1 - MP4} Dolby Spheres.mp4", "7.1 Surround (Dolby TrueHD)" },
        { "{Dolby TrueHD + Atmos - Matroska} Unfold (15 objects).mkv", "7.1 Surround (Dolby TrueHD Atmos)" },
        { "{Dolby TrueHD + Atmos - MP4} Unfold (15 objects).mp4", "7.1 Surround (Dolby TrueHD Atmos)" },
    };

    /// <summary>Atmos is read from the frames whatever the container says (Matroska and MP4's TrueHD entry say nothing).</summary>
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public async Task Corpus_atmos_and_layout_are_named(string file, string expected)
    {
        var path = Path.Combine(Corpus.Directory ?? string.Empty, "Multichannel audio", file);
        Corpus.Require(Corpus.Directory is not null && File.Exists(path) ? path : string.Empty);
        MediaRemux.EnsureRegistered();
        var doc = await new ContainerRegistry([new Formats.Mp4.Mp4Handler(), new Formats.Matroska.MatroskaHandler()]).OpenAsync(path, Ct);
        await TrackActions.DescribeAudioAsync(doc, Ct);
        TrackActions.PrettifyAudioNames(doc);
        Assert.Equal(expected, Assert.Single(doc.Tracks.OfType<AudioTrack>()).Name);
    }
}
