using MMW.Core.Actions;
using MMW.Core.Model;

namespace MMW.Core.Tests;

public class GroupActionsTests
{
    private static MediaDocument Sample()
    {
        var doc = new MediaDocument(null, ContainerKind.Mp4);
        doc.Tracks.Add(new VideoTrack { Id = 1, Language = "en" });
        doc.Tracks.Add(new AudioTrack { Id = 2, Language = "en", Format = "AAC", Name = "Stereo" });
        doc.Tracks.Add(new AudioTrack { Id = 3, Language = "en", Format = "AC-3", CodecId = "ac-3", Name = "Surround" });
        doc.Tracks.Add(new AudioTrack { Id = 4, Language = "en", Format = "AAC", Name = "Director's Commentary" });
        doc.Tracks.Add(new AudioTrack { Id = 5, Language = "fr", Format = "AC-3", CodecId = "ac-3" });
        doc.Tracks.Add(new SubtitleTrack { Id = 6, Language = "en", Name = "English SDH" });
        doc.Tracks.Add(new SubtitleTrack { Id = 7, Language = "en", Name = "Forced" });
        doc.Tracks.Add(new SubtitleTrack { Id = 8, Language = "zh", Name = "Chinese Traditional" });
        return doc;
    }

    [Fact]
    public void Organize_assigns_groups_and_enables_one_audio_track()
    {
        var doc = Sample();
        GroupActions.OrganizeAlternateGroups(doc);

        Assert.Equal(0, doc.Tracks[0].AlternateGroup);
        Assert.All(doc.Tracks.OfType<AudioTrack>(), a => Assert.Equal(1, a.AlternateGroup));
        Assert.All(doc.Tracks.OfType<SubtitleTrack>(), s => Assert.Equal(2, s.AlternateGroup));
        Assert.Equal([2u], doc.Tracks.OfType<AudioTrack>().Where(a => a.Enabled).Select(a => a.Id));
        Assert.All(doc.Tracks.OfType<SubtitleTrack>(), s => Assert.False(s.Enabled));
        Assert.Equal("zh-Hant", doc.Tracks[7].Language);
    }

    [Fact]
    public void Inference_marks_auxiliary_sdh_forced_and_main_tracks()
    {
        var doc = Sample();
        GroupActions.InferMediaCharacteristics(doc);

        Assert.Contains(MediaCharacteristics.AuxiliaryContent, doc.Tracks[3].MediaCharacteristics);
        Assert.Contains(MediaCharacteristics.TranscribesSpokenDialog, doc.Tracks[5].MediaCharacteristics);
        Assert.Contains(MediaCharacteristics.ForcedOnly, doc.Tracks[6].MediaCharacteristics);
        Assert.Contains(MediaCharacteristics.MainProgramContent, doc.Tracks[1].MediaCharacteristics);
        Assert.DoesNotContain(MediaCharacteristics.MainProgramContent, doc.Tracks[3].MediaCharacteristics);
        Assert.DoesNotContain(MediaCharacteristics.MainProgramContent, doc.Tracks[6].MediaCharacteristics);
    }

    [Fact]
    public void Fallbacks_prefer_an_aac_track_in_the_same_language()
    {
        var doc = Sample();
        GroupActions.FixAudioFallbacks(doc);

        var audio = doc.Tracks.OfType<AudioTrack>().ToList();
        Assert.Same(audio[0], audio[1].Fallback);
        Assert.Same(audio[2], audio[3].Fallback); // French AC-3: no French AAC, nearest earlier AAC
        Assert.Null(audio[0].Fallback);
    }

    [Fact]
    public void Complete_languages_and_enable_by_language()
    {
        var doc = Sample();
        doc.Tracks[0].Language = "und";
        GroupActions.CompleteLanguages(doc, "ja");
        Assert.Equal("ja", doc.Tracks[0].Language);

        GroupActions.OrganizeAlternateGroups(doc);
        Assert.True(GroupActions.EnableTrackWithLanguage(doc, TrackKind.Audio, "fra"));
        Assert.Equal([5u], doc.Tracks.OfType<AudioTrack>().Where(a => a.Enabled).Select(a => a.Id));
        Assert.False(GroupActions.EnableTrackWithLanguage(doc, TrackKind.Audio, "de"));
    }
}
