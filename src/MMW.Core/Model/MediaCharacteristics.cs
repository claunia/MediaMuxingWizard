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
        new(MainProgramContent, "Main program content"),
        new(AuxiliaryContent, "Auxiliary content"),
        new(OriginalContent, "Original content"),
    ];

    private static readonly Characteristic[] s_audio =
    [
        new(DescribesVideo, "Describes video"),
        new(EnhancesSpeechIntelligibility, "Enhances speech intelligibility"),
        new(Dubbed, "Dubbed translation"),
        new(VoiceOver, "Voice-over translation"),
        new(Translation, "Translation"),
    ];

    private static readonly Characteristic[] s_subtitle =
    [
        new(ForcedOnly, "Forced only"),
        new(TranscribesSpokenDialog, "Transcribes spoken dialog (SDH)"),
        new(DescribesMusicAndSound, "Describes music and sound"),
        new(EasyToRead, "Easy to read"),
        new(Translation, "Translation"),
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
