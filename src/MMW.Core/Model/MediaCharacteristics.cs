using MMW.Core.Resources;

namespace MMW.Core.Model;

/// <summary>Apple media characteristic tags (<c>tagc</c> box), also mapped to Matroska track flags.</summary>
public static class MediaCharacteristics
{
    public const string MainProgramContent = "public.main-program-content";
    public const string AuxiliaryContent = "public.auxiliary-content";
    public const string OriginalContent = "public.original-content";
    public const string DescribesVideo = "public.accessibility.describes-video";
    public const string EnhancesSpeechIntelligibility = "public.accessibility.enhances-speech-intelligibility";
    public const string Dubbed = "public.translation.dubbed";
    public const string VoiceOver = "public.translation.voice-over";
    public const string Translation = "public.translation";
    public const string ForcedOnly = "public.subtitles.forced-only";
    public const string TranscribesSpokenDialog = "public.accessibility.transcribes-spoken-dialog";
    public const string DescribesMusicAndSound = "public.accessibility.describes-music-and-sound";
    public const string EasyToRead = "public.easy-to-read";

    public sealed record Characteristic(string Tag, string DisplayName);

    private static readonly Characteristic[] s_common =
    [
        new(MainProgramContent, Strings.Characteristic_MainProgramContent),
        new(AuxiliaryContent, Strings.Characteristic_AuxiliaryContent),
        new(OriginalContent, Strings.Characteristic_OriginalContent),
    ];

    private static readonly Characteristic[] s_audio =
    [
        new(DescribesVideo, Strings.Characteristic_DescribesVideo),
        new(EnhancesSpeechIntelligibility, Strings.Characteristic_EnhancesSpeech),
        new(Dubbed, Strings.Characteristic_Dubbed),
        new(VoiceOver, Strings.Characteristic_VoiceOver),
        new(Translation, Strings.Characteristic_Translation),
    ];

    private static readonly Characteristic[] s_subtitle =
    [
        new(ForcedOnly, Strings.Characteristic_ForcedOnly),
        new(TranscribesSpokenDialog, Strings.Characteristic_TranscribesDialog),
        new(DescribesMusicAndSound, Strings.Characteristic_DescribesMusic),
        new(EasyToRead, Strings.Characteristic_EasyToRead),
        new(Translation, Strings.Characteristic_Translation),
    ];

    /// <summary>Characteristics that apply to a given track kind, in display order.</summary>
    public static IReadOnlyList<Characteristic> For(TrackKind kind) => kind switch
    {
        TrackKind.Audio => [.. s_common, .. s_audio],
        TrackKind.Subtitle or TrackKind.ClosedCaption => [.. s_common, .. s_subtitle],
        TrackKind.Video => s_common,
        _ => [],
    };
}
