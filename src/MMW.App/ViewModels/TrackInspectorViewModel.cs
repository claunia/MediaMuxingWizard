using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MMW.App.Resources;
using MMW.Core.Languages;
using MMW.Core.Model;

namespace MMW.App.ViewModels;

/// <summary>A selectable choice with a display name.</summary>
public sealed record Choice<T>(T Value, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class CharacteristicItemViewModel : ViewModelBase
{
    private readonly Track _track;

    public CharacteristicItemViewModel(Track track, MediaCharacteristics.Characteristic characteristic)
    {
        _track = track;
        Tag = characteristic.Tag;
        Name = characteristic.DisplayName;
    }

    public string Tag { get; }

    public string Name { get; }

    public bool IsChecked
    {
        get => _track.MediaCharacteristics.Contains(Tag);
        set
        {
            if (value == IsChecked)
                return;
            if (value)
                _track.MediaCharacteristics.Add(Tag);
            else
                _track.MediaCharacteristics.Remove(Tag);
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>Inspector for a single audio, video, subtitle or other track.</summary>
public sealed partial class TrackInspectorViewModel : ViewModelBase
{
    private readonly MediaDocument _document;
    private readonly DocumentViewModel? _owner;
    private bool _loadingConversion;

    public TrackInspectorViewModel(Track track, MediaDocument document, DocumentViewModel? owner = null)
    {
        Track = track;
        _document = document;
        _owner = owner;
        // Pending tracks too (imported, or duplicated): their choices are those of their own source file.
        if (owner is not null && track is AudioTrack or SubtitleTrack && (track.Source is not null || !track.IsPending))
            _ = LoadConversionChoicesAsync();
        Characteristics = new ObservableCollection<CharacteristicItemViewModel>(
            MediaCharacteristics.For(track.Kind).Select(c => new CharacteristicItemViewModel(track, c)));
        track.MediaCharacteristics.CollectionChanged += OnCharacteristicsChanged;
        track.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Track.Language):
                    OnPropertyChanged(nameof(SelectedLanguage));
                    break;
                case nameof(Track.AlternateGroup):
                    OnPropertyChanged(nameof(SelectedAlternateGroup));
                    break;
                case nameof(Track.StartOffset):
                    OnPropertyChanged(nameof(StartOffsetMs));
                    break;
                case nameof(AudioTrack.Volume):
                    OnPropertyChanged(nameof(VolumeDb));
                    OnPropertyChanged(nameof(VolumeText));
                    break;
                case nameof(AudioTrack.Fallback):
                    OnPropertyChanged(nameof(SelectedFallback));
                    break;
                case nameof(AudioTrack.FollowsSubtitle):
                    OnPropertyChanged(nameof(SelectedFollowsSubtitle));
                    break;
                case nameof(SubtitleTrack.ForcedTrack):
                    OnPropertyChanged(nameof(SelectedForcedTrack));
                    break;
                case nameof(SubtitleTrack.ForcedMode):
                    OnPropertyChanged(nameof(SelectedForcedMode));
                    break;
                case nameof(VideoTrack.Color):
                    OnPropertyChanged(nameof(SelectedColorPreset));
                    break;
            }
        };
    }

    public Track Track { get; }

    public VideoTrack? Video => Track as VideoTrack;

    public AudioTrack? Audio => Track as AudioTrack;

    public SubtitleTrack? Subtitle => Track as SubtitleTrack;

    public bool IsVideo => Track is VideoTrack;

    public bool IsAudio => Track is AudioTrack;

    public bool IsSubtitle => Track is SubtitleTrack;

    public bool IsMp4 => _document.Container == ContainerKind.Mp4;

    public bool IsMatroska => _document.Container == ContainerKind.Matroska;

    public string Header => Track.Kind switch
    {
        TrackKind.Video => Strings.TrackInspector_Header_Video,
        TrackKind.Audio => Strings.TrackInspector_Header_Audio,
        TrackKind.Subtitle => Strings.TrackInspector_Header_Subtitle,
        TrackKind.ClosedCaption => Strings.TrackInspector_Header_ClosedCaptions,
        _ => Strings.TrackInspector_Header_Track,
    };

    public ObservableCollection<CharacteristicItemViewModel> Characteristics { get; }

    public bool HasCharacteristics => Characteristics.Count > 0;

    // ------------------------------------------------------------------ language

    public IReadOnlyList<Language> Languages => LanguageTable.All;

    public Language? SelectedLanguage
    {
        get => LanguageTable.Find(Track.Language) ?? new Language(Track.Language, Track.Language, Track.Language, LanguageTable.DisplayName(Track.Language));
        set
        {
            if (value is not null && value.Tag != Track.Language)
                Track.Language = value.Tag;
        }
    }

    // ------------------------------------------------------------------ alternate group

    public static IReadOnlyList<Choice<int>> AlternateGroups { get; } =
        [new(0, Strings.TrackInspector_AlternateGroupNone), .. Enumerable.Range(1, 6).Select(i => new Choice<int>(i, i.ToString(CultureInfo.InvariantCulture)))];

    public Choice<int>? SelectedAlternateGroup
    {
        get => AlternateGroups.FirstOrDefault(g => g.Value == Track.AlternateGroup) ?? new Choice<int>(Track.AlternateGroup, Track.AlternateGroup.ToString(CultureInfo.InvariantCulture));
        set
        {
            if (value is not null)
                Track.AlternateGroup = value.Value;
        }
    }

    /// <summary>Extra delay applied to the track when saving, in milliseconds (negative values advance it).</summary>
    public double StartOffsetMs
    {
        get => Track.StartOffset.TotalMilliseconds;
        set
        {
            var offset = TimeSpan.FromMilliseconds(Math.Round(value));
            if (offset != Track.StartOffset)
                Track.StartOffset = offset;
        }
    }

    // ------------------------------------------------------------------ conversion

    public System.Collections.ObjectModel.ObservableCollection<MMW.Core.Media.ImportChoice> ConversionChoices { get; } = [];

    public bool HasConversionChoices => ConversionChoices.Count > 1;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(IsOcr))]
    private MMW.Core.Media.ImportChoice? _selectedConversion;

    /// <summary>True when the chosen conversion uses OCR (show the Subtitle Edit advice).</summary>
    public bool IsOcr => OcrAdvice.IsOcr(SelectedConversion);

    public static string OcrWarning => OcrAdvice.Warning;

    partial void OnSelectedConversionChanged(MMW.Core.Media.ImportChoice? value)
    {
        if (!_loadingConversion && value is not null && _owner is not null)
            _ = _owner.SetConversionAsync(Track, value);
    }

    private async Task LoadConversionChoicesAsync()
    {
        try
        {
            var tracks = Track.Source is { } trackSource ? await _owner!.GetSourceTracksAsync(trackSource.Path) : await _owner!.GetSourceTracksAsync();
            var source = tracks.FirstOrDefault(t => t.TrackId == (Track.Source?.TrackId ?? Track.Id));
            if (source is null)
                return;
            _loadingConversion = true;
            foreach (var c in source.Choices.Where(c => c.Action != MMW.Core.Media.ImportAction.Skip))
                ConversionChoices.Add(c);
            var import = Track.Source?.Import;
            var current = import?.Action ?? MMW.Core.Media.ImportAction.Passthrough;
            // Several choices share an action (AAC mixdowns, OCR or not): prefer the one matching the stored settings.
            SelectedConversion = ConversionChoices.FirstOrDefault(c => c.Action == current && (c.Mixdown is null || c.Mixdown == import?.Conversion?.Mixdown) &&
                                                                       c.Ocr == (import?.Ocr is not null))
                                 ?? ConversionChoices.FirstOrDefault(c => c.Action == current)
                                 ?? ConversionChoices.FirstOrDefault();
            _loadingConversion = false;
            OnPropertyChanged(nameof(HasConversionChoices));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            MMW.Core.Diagnostics.AppLog.Debug(string.Format(CultureInfo.CurrentCulture, Strings.Log_NoConversionChoicesFormat, Track.Id, ex.Message));
        }
    }

    // ------------------------------------------------------------------ audio

    /// <summary>Volume in dB, -60 meaning silence (-∞).</summary>
    public double VolumeDb
    {
        get => Audio is { Volume: > 0 } a ? Math.Max(-60, 20 * Math.Log10(a.Volume)) : -60;
        set
        {
            if (Audio is null)
                return;
            Audio.Volume = value <= -60 ? 0 : Math.Round(Math.Pow(10, value / 20), 3);
        }
    }

    public string VolumeText => Audio is null ? string.Empty : VolumeDb <= -60 ? "-∞ dB" : string.Create(CultureInfo.InvariantCulture, $"{VolumeDb:+0.0;-0.0;0.0} dB");

    private IEnumerable<Choice<Track?>> TrackChoices(Func<Track, bool> filter) =>
        [new Choice<Track?>(null, Strings.TrackInspector_TrackNone), .. _document.Tracks.Where(t => t != Track && filter(t)).Select(t => new Choice<Track?>(t, Describe(t)))];

    public IReadOnlyList<Choice<Track?>> FallbackChoices => TrackChoices(t => t is AudioTrack).ToList();

    public Choice<Track?>? SelectedFallback
    {
        get => FallbackChoices.FirstOrDefault(c => c.Value == Audio?.Fallback);
        set
        {
            if (Audio is not null && value is not null)
                Audio.Fallback = value.Value;
        }
    }

    public IReadOnlyList<Choice<Track?>> SubtitleChoices => TrackChoices(t => t is SubtitleTrack or ClosedCaptionTrack).ToList();

    public Choice<Track?>? SelectedFollowsSubtitle
    {
        get => SubtitleChoices.FirstOrDefault(c => c.Value == Audio?.FollowsSubtitle);
        set
        {
            if (Audio is not null && value is not null)
                Audio.FollowsSubtitle = value.Value;
        }
    }

    // ------------------------------------------------------------------ subtitles

    public static IReadOnlyList<Choice<ForcedSubtitleMode>> ForcedModes { get; } =
    [
        new(ForcedSubtitleMode.None, Strings.TrackInspector_ForcedModeNo),
        new(ForcedSubtitleMode.SomeSamplesForced, Strings.TrackInspector_ForcedModeSome),
        new(ForcedSubtitleMode.AllSamplesForced, Strings.TrackInspector_ForcedModeAll),
    ];

    public Choice<ForcedSubtitleMode>? SelectedForcedMode
    {
        get => ForcedModes.FirstOrDefault(m => m.Value == Subtitle?.ForcedMode);
        set
        {
            if (Subtitle is not null && value is not null)
                Subtitle.ForcedMode = value.Value;
        }
    }

    public Choice<Track?>? SelectedForcedTrack
    {
        get => SubtitleChoices.FirstOrDefault(c => c.Value == Subtitle?.ForcedTrack);
        set
        {
            if (Subtitle is not null && value is not null)
                Subtitle.ForcedTrack = value.Value;
        }
    }

    // ------------------------------------------------------------------ video

    public IReadOnlyList<ColorPreset> ColorPresets => ColorPreset.All;

    public ColorPreset? SelectedColorPreset
    {
        get => Video is null ? null : ColorPreset.All.FirstOrDefault(p => p.Color with { FullRange = null } == Video.Color with { FullRange = null })
                                       ?? new ColorPreset(string.Format(CultureInfo.CurrentCulture, Strings.TrackInspector_CustomColorFormat, Video.Color), Video.Color);
        set
        {
            if (Video is not null && value is not null)
                Video.Color = value.Color with { FullRange = Video.Color.FullRange };
        }
    }

    private static void AddMastering(List<string> lines, HdrInfo? h, bool fromStream)
    {
        if (h?.MaxLuminance is not { } max)
            return;
        var line = string.Format(CultureInfo.InvariantCulture, Strings.TrackInspector_MasteringDisplayFormat, h.MinLuminance, max);
        if (h.DisplayPrimaries is { Length: 3 } p && h.WhitePoint is { } w)
            line += string.Format(CultureInfo.InvariantCulture, " — R {0:0.####},{1:0.####} G {2:0.####},{3:0.####} B {4:0.####},{5:0.####} W {6:0.####},{7:0.####}",
                p[0].X, p[0].Y, p[1].X, p[1].Y, p[2].X, p[2].Y, w.X, w.Y);
        lines.Add(fromStream ? line + Strings.TrackInspector_FromStreamSuffix : line);
    }

    private static void AddLightLevel(List<string> lines, HdrInfo? h, bool fromStream)
    {
        if (h?.MaxCll is not { } cll)
            return;
        var line = string.Format(CultureInfo.InvariantCulture, Strings.TrackInspector_LightLevelFormat, cll, h.MaxFall);
        lines.Add(fromStream ? line + Strings.TrackInspector_FromStreamSuffix : line);
    }

    /// <summary>Updates the HDR details after a background scan found more (e.g. HDR10+).</summary>
    public void RefreshHdr()
    {
        OnPropertyChanged(nameof(HdrText));
        OnPropertyChanged(nameof(InfoText));
    }

    public string HdrText
    {
        get
        {
            if (Video is null)
                return string.Empty;
            var lines = new List<string>();
            if (Video.DolbyVision is { } dv)
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.TrackInspector_DolbyVisionFormat, dv));
            if (Video.Hdr10Plus)
                lines.Add(Strings.TrackInspector_Hdr10Plus);
            if (Video.HdrVivid)
                lines.Add(Strings.TrackInspector_HdrVivid);
            if (Video.OtherDynamicHdr != MMW.Core.Media.Codecs.DynamicHdrFormats.None)
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.TrackInspector_OtherDynamicHdrFormat, MMW.Core.Media.Codecs.DynamicHdr.Describe(Video.OtherDynamicHdr)));

            // Container values first; what only the video stream carries is shown (and written on remux) as well.
            var stream = Video.StreamInfo;
            if (!Video.Color.IsSpecified && stream?.Color is { IsSpecified: true } streamColor)
            {
                var name = ColorPreset.All.FirstOrDefault(p => p.Color with { FullRange = null } == streamColor with { FullRange = null })?.Name ?? streamColor.ToString();
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.TrackInspector_StreamColorFormat, name));
            }

            var h = Video.Hdr;
            var s = stream?.Hdr;
            AddMastering(lines, h is { HasMasteringDisplay: true } || s is not { HasMasteringDisplay: true } ? h : s, h is not { HasMasteringDisplay: true } && s is { HasMasteringDisplay: true });
            AddLightLevel(lines, h is { HasLightLevel: true } || s is not { HasLightLevel: true } ? h : s, h is not { HasLightLevel: true } && s is { HasLightLevel: true });
            var ambient = h is { HasAmbient: true } || s is not { HasAmbient: true } ? h : s;
            if (ambient?.AmbientIlluminance is { } lux)
            {
                var line = string.Format(CultureInfo.InvariantCulture, Strings.TrackInspector_AmbientFormat, lux);
                lines.Add(ambient == h ? line : line + Strings.TrackInspector_FromStreamSuffix);
            }

            return lines.Count == 0 ? Strings.TrackInspector_HdrNone : string.Join("\n", lines);
        }
    }

    // ------------------------------------------------------------------ info

    public string InfoText
    {
        get
        {
            var lines = new List<string>
            {
                string.Format(CultureInfo.CurrentCulture, Strings.TrackInfo_FormatFormat,
                    Track is AudioTrack { Profile.Length: > 0 } audio ? $"{Track.Format} ({audio.Profile})" : Track.Format, Track.CodecId),
            };
            if (Track.FormatDetails.Length > 0)
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.TrackInfo_DetailsFormat, Track.FormatDetails));
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.TrackInfo_DurationFormat, TrackRowViewModel.FormatDuration(Track.Duration)));
            if (Track.Bitrate > 0)
                lines.Add(string.Format(CultureInfo.InvariantCulture, Strings.TrackInfo_BitrateFormat, Track.Bitrate / 1000.0));
            if (Track.DataLength > 0)
                lines.Add(string.Format(CultureInfo.InvariantCulture, Strings.TrackInfo_SizeFormat, Track.DataLength / 1048576.0));
            if (Video is { } v)
            {
                lines.Add(string.Format(CultureInfo.InvariantCulture, Strings.TrackInfo_FrameRateFormat, v.FrameRate));
                lines.Add(string.Format(CultureInfo.InvariantCulture, Strings.TrackInfo_PixelSizeFormat, v.PixelWidth, v.PixelHeight, v.ParNumerator, v.ParDenominator));
            }

            if (Audio is { } a)
            {
                lines.Add(string.Format(CultureInfo.InvariantCulture, Strings.TrackInfo_ChannelsFormat, a.Channels, a.ChannelLayout).TrimEnd());
                lines.Add(string.Format(CultureInfo.InvariantCulture, Strings.TrackInfo_SampleRateFormat, a.SampleRate));
            }

            if (Track.Source is { } s)
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.TrackInfo_SourceFormat, Path.GetFileName(s.Path), s.TrackId));
            return string.Join("\n", lines);
        }
    }

    public static string Describe(Track t) =>
        $"{t.Id}: {(t.Name.Length > 0 ? t.Name : t.Format)} ({LanguageTable.DisplayName(t.Language)})";

    private void OnCharacteristicsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var c in Characteristics)
            c.Refresh();
    }
}
