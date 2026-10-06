using System.Globalization;
using MMW.Core.Languages;
using MMW.Core.Model;
using MMW.Formats.Matroska.Ebml;
using static MMW.Formats.Matroska.MatroskaIds;

namespace MMW.Formats.Matroska;

/// <summary>Converts TrackEntry elements to <see cref="Track"/> objects.</summary>
internal static class MatroskaTrackParser
{
    public const int TrackTypeVideo = 1;
    public const int TrackTypeAudio = 2;
    public const int TrackTypeSubtitle = 17;

    /// <summary>Builds a track from a TrackEntry payload.</summary>
    public static Track Parse(ReadOnlyMemory<byte> entryPayload, string path)
    {
        var c = EbmlParser.Children(entryPayload);
        var number = c.GetUInt(TrackNumber, 0);
        var type = c.GetUInt(TrackType, 0);
        var codecId = c.GetString(CodecId) ?? string.Empty;

        Track track = type switch
        {
            TrackTypeVideo => ParseVideo(c, codecId),
            TrackTypeAudio => ParseAudio(c),
            TrackTypeSubtitle => new SubtitleTrack(),
            _ => new OtherTrack(),
        };

        track.Id = (uint)number;
        track.CodecId = codecId;
        track.Format = MatroskaCodecs.FormatName(codecId);
        track.Source = new TrackSource(path, ContainerKind.Matroska, (uint)number);
        track.Name = c.GetString(Name) ?? string.Empty;
        track.Language = ReadLanguage(c.GetString(LanguageBcp47), c.GetString(TrackLanguage));
        track.Enabled = c.GetUInt(FlagEnabled, 1) != 0;
        track.IsDefault = c.GetUInt(FlagDefault, 1) != 0;
        track.IsForced = c.GetUInt(FlagForced, 0) != 0;

        if (c.GetUInt(FlagHearingImpaired, 0) != 0)
        {
            track.MediaCharacteristics.Add(MediaCharacteristics.TranscribesSpokenDialog);
            track.MediaCharacteristics.Add(MediaCharacteristics.DescribesMusicAndSound);
        }

        if (c.GetUInt(FlagVisualImpaired, 0) != 0)
            track.MediaCharacteristics.Add(MediaCharacteristics.DescribesVideo);
        if (c.GetUInt(FlagCommentary, 0) != 0)
            track.MediaCharacteristics.Add(MediaCharacteristics.AuxiliaryContent);
        if (c.GetUInt(FlagOriginal, 0) != 0)
            track.MediaCharacteristics.Add(MediaCharacteristics.OriginalContent);
        if (track is SubtitleTrack sub && track.IsForced)
        {
            track.MediaCharacteristics.Add(MediaCharacteristics.ForcedOnly);
            sub.ForcedMode = ForcedSubtitleMode.AllSamplesForced;
        }

        return track;
    }

    /// <summary>BCP-47 language of a track: LanguageBCP47 wins, else the legacy ISO 639-2 code (default "eng").</summary>
    public static string ReadLanguage(string? bcp47, string? legacy)
    {
        if (!string.IsNullOrWhiteSpace(bcp47))
            return bcp47;
        return LanguageTable.ToBcp47(string.IsNullOrWhiteSpace(legacy) ? "eng" : legacy);
    }

    private static VideoTrack ParseVideo(List<EbmlChild> entry, string codecId)
    {
        var track = new VideoTrack();
        var defaultDuration = entry.GetUInt(DefaultDuration, 0);
        if (defaultDuration > 0)
            track.FrameRate = Math.Round(1e9 / defaultDuration, 3);

        if (entry.Child(Video) is { } videoElement)
        {
            var v = EbmlParser.Children(videoElement.Data);
            var pw = (int)v.GetUInt(PixelWidth, 0);
            var ph = (int)v.GetUInt(PixelHeight, 0);
            track.PixelWidth = pw;
            track.PixelHeight = ph;
            var unit = v.GetUInt(DisplayUnit, 0);
            var dw = (int)v.GetUInt(DisplayWidth, (ulong)pw);
            var dh = (int)v.GetUInt(DisplayHeight, (ulong)ph);
            if (unit == 0 && pw > 0 && ph > 0 && dw > 0 && dh > 0)
            {
                track.DisplayWidth = dw;
                track.DisplayHeight = dh;
                long num = (long)dw * ph;
                long den = (long)dh * pw;
                var g = Gcd(num, den);
                track.ParNumerator = (int)(num / g);
                track.ParDenominator = (int)(den / g);
            }
            else
            {
                track.DisplayWidth = pw;
                track.DisplayHeight = ph;
            }

            if (v.Child(Colour) is { } colour)
                ParseColour(track, EbmlParser.Children(colour.Data));
        }

        if (entry.Child(CodecPrivate) is { } priv)
        {
            track.ProfileLevel = codecId switch
            {
                "V_MPEG4/ISO/AVC" => MatroskaCodecs.AvcProfileLevel(priv.Data.Span),
                "V_MPEGH/ISO/HEVC" => MatroskaCodecs.HevcProfileLevel(priv.Data.Span),
                _ => string.Empty,
            };
        }

        foreach (var child in entry)
        {
            if (child.Id != BlockAdditionMapping)
                continue;
            var m = EbmlParser.Children(child.Data);
            var addType = m.GetUInt(BlockAddIdType, 0);
            if (addType is BlockAddTypeDvcC or BlockAddTypeDvvC && m.Child(BlockAddIdExtraData) is { Data.Length: >= 5 } extra)
                track.DolbyVisionRecord = extra.Data.ToArray();
        }

        var details = string.Create(CultureInfo.InvariantCulture, $"{track.PixelWidth}×{track.PixelHeight}");
        if (track.ProfileLevel.Length > 0)
            details += ", " + track.ProfileLevel;
        track.FormatDetails = details;
        return track;
    }

    private static void ParseColour(VideoTrack track, List<EbmlChild> c)
    {
        var matrix = (int)c.GetUInt(MatrixCoefficients, 2);
        var transfer = (int)c.GetUInt(TransferCharacteristics, 2);
        var primaries = (int)c.GetUInt(Primaries, 2);
        bool? fullRange = c.GetUInt(ColourRange, 0) switch
        {
            1 => false,
            2 => true,
            _ => null,
        };
        if (c.Child(MatrixCoefficients) is not null || c.Child(TransferCharacteristics) is not null || c.Child(Primaries) is not null)
            track.Color = new ColorInfo(primaries, transfer, matrix, fullRange);

        int? maxCll = c.Child(MaxCll) is { } cll ? (int)cll.UInt : null;
        int? maxFall = c.Child(MaxFall) is { } fall ? (int)fall.UInt : null;
        (double X, double Y)[]? display = null;
        (double X, double Y)? white = null;
        double? lumMax = null, lumMin = null;
        if (c.Child(MasteringMetadata) is { } mmElement)
        {
            var mm = EbmlParser.Children(mmElement.Data);
            if (mm.Child(PrimaryRChromaticityX) is not null)
            {
                display =
                [
                    (mm.GetFloat(PrimaryRChromaticityX) ?? 0, mm.GetFloat(PrimaryRChromaticityY) ?? 0),
                    (mm.GetFloat(PrimaryGChromaticityX) ?? 0, mm.GetFloat(PrimaryGChromaticityY) ?? 0),
                    (mm.GetFloat(PrimaryBChromaticityX) ?? 0, mm.GetFloat(PrimaryBChromaticityY) ?? 0),
                ];
            }

            if (mm.Child(WhitePointChromaticityX) is not null)
                white = (mm.GetFloat(WhitePointChromaticityX) ?? 0, mm.GetFloat(WhitePointChromaticityY) ?? 0);
            lumMax = mm.GetFloat(LuminanceMax);
            lumMin = mm.GetFloat(LuminanceMin);
        }

        if (maxCll is not null || maxFall is not null || display is not null || white is not null || lumMax is not null || lumMin is not null)
        {
            track.Hdr = new HdrInfo
            {
                DisplayPrimaries = display,
                WhitePoint = white,
                MaxLuminance = lumMax,
                MinLuminance = lumMin,
                MaxCll = maxCll,
                MaxFall = maxFall,
            };
        }
    }

    private static AudioTrack ParseAudio(List<EbmlChild> entry)
    {
        var track = new AudioTrack();
        if (entry.Child(Audio) is { } audioElement)
        {
            var a = EbmlParser.Children(audioElement.Data);
            track.Channels = (int)a.GetUInt(Channels, 1);
            var rate = a.GetFloat(OutputSamplingFrequency) ?? a.GetFloat(SamplingFrequency) ?? 8000;
            track.SampleRate = (int)Math.Round(rate);
        }
        else
        {
            track.Channels = 1;
            track.SampleRate = 8000;
        }

        track.ChannelLayout = MatroskaCodecs.ChannelLayout(track.Channels);
        track.FormatDetails = string.Create(CultureInfo.InvariantCulture, $"{track.Channels} ch, {track.SampleRate} Hz");
        return track;
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }
}
