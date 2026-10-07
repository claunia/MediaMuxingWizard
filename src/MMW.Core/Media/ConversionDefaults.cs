using MMW.Core.Model;

namespace MMW.Core.Media;

/// <summary>One entry of a track's action list in the import UI.</summary>
/// <param name="Action">The import action.</param>
/// <param name="DisplayName">Subler's label ("Passthru", "AAC - Dolby Pro Logic II", "AAC + Passthru", …).</param>
/// <param name="Mixdown">The mixdown of an AAC conversion chosen by this entry; null to use the settings' mixdown.</param>
/// <param name="Ocr">The entry converts a bitmap subtitle track to text by OCR ("Tx3g (OCR)", "SRT (OCR)").</param>
public sealed record ImportChoice(ImportAction Action, string DisplayName, AudioMixdown? Mixdown = null, bool Ocr = false)
{
    /// <summary>The OCR settings this choice implies, starting from <paramref name="options"/>; null for non-OCR choices.</summary>
    public OcrOptions? OcrFrom(OcrOptions? options) => Ocr ? options ?? OcrOptions.Default : null;

    /// <summary>The conversion settings this choice implies, starting from <paramref name="settings"/>.</summary>
    public AudioConversionSettings? SettingsFrom(AudioConversionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!ConversionDefaults.IsConversion(Action))
            return null;
        return Mixdown is { } m ? settings with { Mixdown = m } : settings;
    }

    public override string ToString() => DisplayName;
}

/// <summary>
/// Which import actions are offered for a track and which one is preselected (Subler's audio conversion rules).
/// </summary>
/// <remarks>
/// <para>Defaults (Subler's out-of-the-box preferences, adapted when the converter is unavailable):</para>
/// <list type="bullet">
/// <item>AC-3 / E-AC-3: passthrough (<see cref="ConvertAc3"/> off); "AAC + Passthru" is offered.</item>
/// <item>DTS into MP4: "AAC + Passthru" (Apple players cannot decode DTS, the original is kept as the disabled
/// alternate); into Matroska: passthrough.</item>
/// <item>Vorbis, TrueHD, MLP, MP1 into MP4 (not storable or not playable): AAC with the default mixdown.</item>
/// <item>FLAC into MP4 (storable, but not playable by Apple players): ALAC, or AAC when ALAC cannot hold it (32-bit,
/// quadraphonic, 6.1, 7.1); passthrough and LPCM are offered.</item>
/// <item>PCM: passthrough ('ipcm' in MP4); PCM that MP4 cannot store (8-bit) is converted to 16-bit PCM.</item>
/// <item>Lossless sources (FLAC, ALAC, TrueHD, MLP, DTS-HD MA, integer PCM) are offered LPCM.</item>
/// <item>Opus into MP4: passthrough; AAC is offered.</item>
/// <item>Everything else (AAC, ALAC, MP3, MP2, …) and every storable track into Matroska: passthrough.</item>
/// </list>
/// </remarks>
public static class ConversionDefaults
{
    public const string PassthroughName = "Passthru";
    public const string SkipName = "Skip";
    public const string NotAvailableName = "Not available";
    public const string AacPlusPassthroughName = "AAC + Passthru";
    public const string AacPlusAc3Name = "AAC + AC3";
    public const string Ac3Name = "AC3";
    public const string PcmName = "LPCM";
    public const string AlacName = "ALAC";

    /// <summary>Default conversion settings (the application may replace them from its preferences).</summary>
    public static AudioConversionSettings Settings { get; set; } = AudioConversionSettings.Default;

    /// <summary>Preselect "AAC + Passthru" for AC-3/E-AC-3 tracks imported into MP4 (Subler: "Convert AC-3 to AAC").</summary>
    public static bool ConvertAc3 { get; set; }

    /// <summary>Preselect "AAC + Passthru" for DTS tracks imported into MP4.</summary>
    public static bool ConvertDts { get; set; } = true;

    /// <summary>True for the actions that need the audio converter.</summary>
    public static bool IsConversion(ImportAction action) =>
        action is ImportAction.ConvertToAac or ImportAction.ConvertToAc3 or ImportAction.AacPlusPassthrough or ImportAction.AacPlusAc3 or
            ImportAction.ConvertToPcm or ImportAction.ConvertToAlac;

    /// <summary>The codec a single-track conversion action produces, or null for other actions.</summary>
    public static AudioConversionTarget? Target(ImportAction action) => action switch
    {
        ImportAction.ConvertToAac => AudioConversionTarget.Aac,
        ImportAction.ConvertToAc3 => AudioConversionTarget.Ac3,
        ImportAction.ConvertToPcm => AudioConversionTarget.Pcm,
        ImportAction.ConvertToAlac => AudioConversionTarget.Alac,
        _ => null,
    };

    /// <summary>
    /// Lossless sources, which are offered a lossless conversion to LPCM: FLAC, ALAC, TrueHD, MLP, DTS-HD Master Audio
    /// (and DTS:X, built on it) and PCM (re-encoded as little-endian 16, 24 or 32-bit integers, or 32/64-bit floats for
    /// floating-point PCM: 8-bit or big-endian PCM that a container cannot store).
    /// </summary>
    public static bool IsLosslessSource(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Kind == TrackKind.Audio && config.Codec switch
        {
            CodecType.Flac or CodecType.Alac or CodecType.TrueHd or CodecType.Mlp or CodecType.Pcm => true,
            CodecType.Dts => config.AudioProfile is "DTS-HD MA" or "DTS:X" or "DTS:X IMAX",
            _ => false,
        };
    }

    /// <summary>
    /// True when ALAC can hold the source without loss and with the same speaker layout: at most 24 bits (FFmpeg's
    /// encoder stores 32-bit input as 24) and a channel count whose ALAC layout matches FLAC's (mono, stereo, L R C,
    /// 5.0 and 5.1; FLAC's quadraphonic, 6.1 and 7.1 have side or back channels ALAC places elsewhere).
    /// </summary>
    public static bool AlacCanHold(CodecConfig config) =>
        config.BitsPerSample <= 24 && config.Channels is 1 or 2 or 3 or 5 or 6;

    /// <summary>Subler's label of an AAC mixdown.</summary>
    public static string MixdownName(AudioMixdown mixdown) => mixdown switch
    {
        AudioMixdown.DolbyProLogicII => "AAC - Dolby Pro Logic II",
        AudioMixdown.DolbyProLogic => "AAC - Dolby Pro Logic",
        AudioMixdown.Stereo => "AAC - Stereo",
        AudioMixdown.Mono => "AAC - Mono",
        _ => "AAC - Multi-channel",
    };

    /// <summary>Label of an action (with the mixdown for <see cref="ImportAction.ConvertToAac"/>).</summary>
    public static string DisplayName(ImportAction action, AudioMixdown? mixdown = null) => action switch
    {
        ImportAction.Passthrough => PassthroughName,
        ImportAction.ConvertToTx3g => "Tx3g",
        ImportAction.ConvertToSrt => "SRT",
        ImportAction.ConvertToAss => "ASS",
        ImportAction.ConvertToSsa => "SSA",
        ImportAction.ConvertToWebVtt => "WebVTT",
        ImportAction.ConvertToAac => MixdownName(mixdown ?? Settings.Mixdown),
        ImportAction.ConvertToAc3 => Ac3Name,
        ImportAction.AacPlusPassthrough => AacPlusPassthroughName,
        ImportAction.AacPlusAc3 => AacPlusAc3Name,
        ImportAction.ConvertToPcm => PcmName,
        ImportAction.ConvertToAlac => AlacName,
        _ => SkipName,
    };

    /// <summary>True when the subtitle OCR converter is registered, available and can decode <paramref name="config"/>.</summary>
    public static bool CanOcr(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Kind == TrackKind.Subtitle && SubtitleConversions.IsBitmap(config.Codec) &&
               MediaFormatRegistry.AvailableSubtitleConverter is { } c && c.CanDecode(config);
    }

    /// <summary>True when the audio converter is registered, available and can decode <paramref name="config"/>.</summary>
    public static bool CanConvert(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Kind == TrackKind.Audio && MediaFormatRegistry.AvailableAudioConverter is { } c && c.CanDecode(config);
    }

    /// <summary>
    /// The actions offered for a track with <paramref name="config"/> going into a <paramref name="target"/> container
    /// whose muxer answered <paramref name="support"/>. The last entry is always <see cref="ImportAction.Skip"/>.
    /// </summary>
    /// <param name="canConvert">Whether the audio converter can convert the track (see <see cref="CanConvert"/>).</param>
    /// <param name="canOcr">
    /// Whether the bitmap subtitle track can be converted to text by OCR (see <see cref="CanOcr"/>): "Tx3g (OCR)" is
    /// offered for MP4 targets, "SRT (OCR)" for Matroska.
    /// </param>
    public static IReadOnlyList<ImportChoice> Choices(CodecConfig config, TrackSupport support, ContainerKind target, bool canConvert, bool canOcr = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(support);
        var list = new List<ImportChoice>();
        if (config.Kind == TrackKind.Subtitle && CodecNames.IsText(config.Codec))
        {
            foreach (var action in TextActions(config.Codec, target))
                list.Add(new ImportChoice(action, DisplayName(action)));
            list.Add(new ImportChoice(ImportAction.Skip, SkipName));
            return list;
        }

        switch (support.Level)
        {
            case TrackSupportLevel.Passthrough:
                list.Add(new ImportChoice(ImportAction.Passthrough, PassthroughName));
                break;
            case TrackSupportLevel.Converted:
                list.Add(new ImportChoice(support.SuggestedAction, DisplayName(support.SuggestedAction)));
                break;
        }

        if (canConvert && config.Kind == TrackKind.Audio)
        {
            var channels = config.Channels;
            if (channels is 0 or > 2)
            {
                list.Add(Aac(AudioMixdown.DolbyProLogicII));
                list.Add(Aac(AudioMixdown.DolbyProLogic));
            }

            list.Add(Aac(AudioMixdown.Stereo));
            list.Add(Aac(AudioMixdown.Mono));
            if (channels is 0 or > 2)
                list.Add(Aac(AudioMixdown.Multichannel));
            if (config.Codec is not (CodecType.Ac3 or CodecType.Eac3))
                list.Add(new ImportChoice(ImportAction.ConvertToAc3, Ac3Name));
            if (support.CanMux && (config.Codec is CodecType.Ac3 or CodecType.Eac3 or CodecType.Dts or CodecType.TrueHd || (config.Codec == CodecType.Aac && channels > 2)))
                list.Add(new ImportChoice(ImportAction.AacPlusPassthrough, AacPlusPassthroughName));
            if (config.Codec is CodecType.Dts or CodecType.TrueHd or CodecType.Mlp)
                list.Add(new ImportChoice(ImportAction.AacPlusAc3, AacPlusAc3Name));

            // Lossless sources can stay lossless in another form: PCM anywhere; FLAC also as ALAC in MP4, where Apple
            // players play it.
            if (IsLosslessSource(config))
            {
                list.Add(new ImportChoice(ImportAction.ConvertToPcm, PcmName));
                if (config.Codec == CodecType.Flac && target is ContainerKind.Mp4 or ContainerKind.Unknown && AlacCanHold(config))
                    list.Add(new ImportChoice(ImportAction.ConvertToAlac, AlacName));
            }
        }

        if (canOcr && config.Kind == TrackKind.Subtitle && SubtitleConversions.IsBitmap(config.Codec))
        {
            var ocr = SubtitleConversions.TargetFor(target);
            list.Add(new ImportChoice(SubtitleConversions.Action(ocr), SubtitleConversions.DisplayName(ocr), Ocr: true));
        }

        list.Add(new ImportChoice(ImportAction.Skip, list.Count == 0 ? NotAvailableName : SkipName));
        return list;
    }

    private static ImportChoice Aac(AudioMixdown mixdown) => new(ImportAction.ConvertToAac, MixdownName(mixdown), mixdown);

    /// <summary>
    /// What a text subtitle track can become in a container, the recommended action first. Matroska keeps SubRip,
    /// ASS, SSA and WebVTT as they are and offers the others; tx3g has no Matroska form and becomes ASS, the format
    /// that keeps most of its features (fonts, colours, positions, vertical text, karaoke). MP4 recommends tx3g (what
    /// Apple players show) for everything, offering WebVTT ('wvtt') too.
    /// </summary>
    public static IReadOnlyList<ImportAction> TextActions(CodecType codec, ContainerKind target)
    {
        var matroska = target == ContainerKind.Matroska;
        return (codec, matroska) switch
        {
            (CodecType.TextUtf8, true) => [ImportAction.Passthrough, ImportAction.ConvertToAss, ImportAction.ConvertToSsa, ImportAction.ConvertToWebVtt],
            (CodecType.Ass, true) => [ImportAction.Passthrough, ImportAction.ConvertToSsa, ImportAction.ConvertToSrt, ImportAction.ConvertToWebVtt],
            (CodecType.Ssa, true) => [ImportAction.Passthrough, ImportAction.ConvertToAss, ImportAction.ConvertToSrt, ImportAction.ConvertToWebVtt],
            (CodecType.WebVtt, true) => [ImportAction.Passthrough, ImportAction.ConvertToSrt, ImportAction.ConvertToAss, ImportAction.ConvertToSsa],
            (CodecType.Tx3g, true) => [ImportAction.ConvertToAss, ImportAction.ConvertToSsa, ImportAction.ConvertToSrt, ImportAction.ConvertToWebVtt],
            // tx3g is what Apple players show in MP4; 'wvtt' (ISO/IEC 14496-30) is kept as an option.
            (CodecType.WebVtt, false) => [ImportAction.ConvertToTx3g, ImportAction.Passthrough],
            (CodecType.Tx3g, false) => [ImportAction.Passthrough, ImportAction.ConvertToWebVtt],
            (_, false) => [ImportAction.ConvertToTx3g, ImportAction.ConvertToWebVtt],
            _ => [ImportAction.Passthrough],
        };
    }

    /// <summary>The preselected entry of <paramref name="choices"/> (see the remarks of <see cref="ConversionDefaults"/>).</summary>
    public static ImportChoice Suggest(CodecConfig config, TrackSupport support, ContainerKind target, IReadOnlyList<ImportChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(support);
        ArgumentNullException.ThrowIfNull(choices);
        var canConvert = choices.Any(c => IsConversion(c.Action));
        var mixdown = Settings.EffectiveMixdown(config.Channels);
        ImportChoice? Find(ImportAction action, AudioMixdown? m = null) =>
            choices.FirstOrDefault(c => c.Action == action && (m is null || c.Mixdown == m));

        if (config.Kind == TrackKind.Audio && canConvert)
        {
            var mp4 = target is ContainerKind.Mp4 or ContainerKind.Unknown;
            ImportChoice? suggested = null;
            if (!support.CanMux)
            {
                // PCM the container cannot store as it is (8-bit, unsigned) is re-encoded as PCM, not compressed.
                suggested = (config.Codec == CodecType.Pcm ? Find(ImportAction.ConvertToPcm) : null) ?? Find(ImportAction.ConvertToAac, mixdown);
            }
            else if (mp4)
            {
                suggested = config.Codec switch
                {
                    CodecType.Ac3 or CodecType.Eac3 when ConvertAc3 => Find(ImportAction.AacPlusPassthrough),
                    CodecType.Dts when ConvertDts => Find(ImportAction.AacPlusPassthrough),
                    // FLAC becomes ALAC (lossless, and played by Apple devices), or AAC where ALAC cannot hold it.
                    CodecType.Flac => Find(ImportAction.ConvertToAlac) ?? Find(ImportAction.ConvertToAac, mixdown),
                    // Opus stays Opus in MP4 ('Opus' + 'dOps'): browsers, VLC and mpv play it; AAC is still offered.
                    // PCM stays PCM ('ipcm').
                    CodecType.Vorbis or CodecType.TrueHd or CodecType.Mlp or CodecType.Mp1 =>
                        Find(ImportAction.ConvertToAac, mixdown),
                    _ => null,
                };
            }

            if (suggested is not null)
                return suggested;
        }

        // Text subtitles: the recommended conversion of TextActions (listed first).
        if (config.Kind == TrackKind.Subtitle && CodecNames.IsText(config.Codec))
            return choices[0];

        // Bitmap subtitles the container cannot store (PGS/DVB into MP4): OCR when available.
        if (!support.CanMux && choices.FirstOrDefault(c => c.Ocr) is { } ocr)
            return ocr;

        return choices.FirstOrDefault(c => c.Action == support.SuggestedAction && !c.Ocr) ?? choices[0];
    }
}
