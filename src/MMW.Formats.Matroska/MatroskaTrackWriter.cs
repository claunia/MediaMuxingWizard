using MMW.Core.Languages;
using MMW.Core.Model;
using MMW.Formats.Matroska.Ebml;
using static MMW.Formats.Matroska.MatroskaIds;

namespace MMW.Formats.Matroska;

/// <summary>
/// Rewrites a TrackEntry with the edited values of a <see cref="Track"/>. Only the elements that correspond to an
/// edited value are touched; every other child is copied byte for byte.
/// </summary>
internal static class MatroskaTrackWriter
{
    /// <summary>Returns the new TrackEntry payload, or null when nothing changed.</summary>
    public static byte[]? Rewrite(TrackEntryState entry, Track track)
    {
        var original = entry.Snapshot;
        var current = TrackEditState.Capture(track);
        if (current == original)
            return null;

        var children = EbmlParser.Children(entry.Payload);
        var replacements = new Dictionary<ulong, byte[]?>();
        bool IsPresent(ulong id) => children.Child(id) is not null;

        void SetFlag(ulong id, bool oldValue, bool newValue, bool defaultValue)
        {
            if (oldValue == newValue)
                return;
            if (newValue == defaultValue && !IsPresent(id))
                return;
            var w = new EbmlWriter();
            w.UInt(id, newValue ? 1UL : 0UL);
            replacements[id] = w.ToArray();
        }

        if (current.Name != original.Name)
            replacements[Name] = current.Name.Length == 0 ? null : Encode(w => w.String(Name, current.Name));

        if (!string.Equals(current.Language, original.Language, StringComparison.Ordinal))
        {
            var tag = string.IsNullOrWhiteSpace(current.Language) ? LanguageTable.Undetermined : current.Language;
            replacements[TrackLanguage] = Encode(w => w.String(TrackLanguage, LanguageTable.ToIso639_2B(tag)));
            replacements[LanguageBcp47] = Encode(w => w.String(LanguageBcp47, tag));
        }

        SetFlag(FlagEnabled, original.Enabled, current.Enabled, true);
        SetFlag(FlagDefault, original.IsDefault, current.IsDefault, true);
        SetFlag(FlagForced, ForcedFlag(original, original), ForcedFlag(original, current), false);
        SetFlag(FlagHearingImpaired, HearingImpaired(original), HearingImpaired(current), false);
        SetFlag(FlagVisualImpaired, Has(original, MediaCharacteristics.DescribesVideo), Has(current, MediaCharacteristics.DescribesVideo), false);
        SetFlag(FlagCommentary, Has(original, MediaCharacteristics.AuxiliaryContent), Has(current, MediaCharacteristics.AuxiliaryContent), false);
        SetFlag(FlagOriginal, Has(original, MediaCharacteristics.OriginalContent), Has(current, MediaCharacteristics.OriginalContent), false);

        var colorChanged = current.Color != original.Color;
        var hdrChanged = !ReferenceEquals(current.Hdr, original.Hdr) && current.Hdr != original.Hdr;
        if ((colorChanged || hdrChanged) && track is VideoTrack video)
        {
            var oldVideo = children.Child(Video);
            replacements[Video] = RewriteVideo(oldVideo?.Data ?? ReadOnlyMemory<byte>.Empty, video, colorChanged, hdrChanged);
        }

        if (replacements.Count == 0)
            return null;

        return ApplyReplacements(children, replacements);
    }

    /// <summary>
    /// Copies <paramref name="children"/>, replacing the first occurrence of every ID in <paramref name="replacements"/>
    /// (null = remove), dropping later duplicates, appending replacements for absent IDs and recomputing a leading CRC-32.
    /// </summary>
    public static byte[] ApplyReplacements(List<EbmlChild> children, Dictionary<ulong, byte[]?> replacements)
    {
        var hadCrc = children.Count > 0 && children[0].Id == Crc32Element;
        var done = new HashSet<ulong>();
        var w = new EbmlWriter();
        foreach (var child in children)
        {
            if (child.Id == Crc32Element)
                continue;
            if (replacements.TryGetValue(child.Id, out var replacement))
            {
                if (done.Add(child.Id) && replacement is not null)
                    w.Raw(replacement);
                continue;
            }

            w.Raw(child.Element.Span);
        }

        foreach (var (id, replacement) in replacements)
        {
            if (!done.Contains(id) && replacement is not null)
                w.Raw(replacement);
        }

        return hadCrc ? EbmlWriter.WithCrc32(w.WrittenSpan) : w.ToArray();
    }

    private static byte[]? RewriteVideo(ReadOnlyMemory<byte> videoPayload, VideoTrack track, bool colorChanged, bool hdrChanged)
    {
        var children = EbmlParser.Children(videoPayload);
        var colour = children.Child(Colour);
        var colourChildren = colour is { } c ? EbmlParser.Children(c.Data) : [];
        var replacements = new Dictionary<ulong, byte[]?>();

        if (colorChanged)
        {
            var color = track.Color;
            if (color.IsSpecified)
            {
                replacements[MatrixCoefficients] = Encode(w => w.UInt(MatrixCoefficients, (ulong)color.Matrix));
                replacements[TransferCharacteristics] = Encode(w => w.UInt(TransferCharacteristics, (ulong)color.Transfer));
                replacements[Primaries] = Encode(w => w.UInt(Primaries, (ulong)color.Primaries));
            }
            else
            {
                replacements[MatrixCoefficients] = null;
                replacements[TransferCharacteristics] = null;
                replacements[Primaries] = null;
            }

            replacements[ColourRange] = color.FullRange is { } full ? Encode(w => w.UInt(ColourRange, full ? 2UL : 1UL)) : null;
        }

        if (hdrChanged)
        {
            var hdr = track.Hdr;
            replacements[MaxCll] = hdr?.MaxCll is { } cll ? Encode(w => w.UInt(MaxCll, (ulong)cll)) : null;
            replacements[MaxFall] = hdr?.MaxFall is { } fall ? Encode(w => w.UInt(MaxFall, (ulong)fall)) : null;
            replacements[MasteringMetadata] = hdr is null ? null : EncodeMastering(hdr);
        }

        var newColourPayload = ApplyReplacements(colourChildren, replacements);
        var videoReplacements = new Dictionary<ulong, byte[]?>
        {
            [Colour] = newColourPayload.Length == 0 ? null : EbmlWriter.Element(Colour, newColourPayload),
        };
        var newVideo = ApplyReplacements(children, videoReplacements);
        return EbmlWriter.Element(Video, newVideo);
    }

    private static byte[]? EncodeMastering(HdrInfo hdr)
    {
        var w = new EbmlWriter();
        if (hdr.DisplayPrimaries is { Length: 3 } p)
        {
            w.Float(PrimaryRChromaticityX, p[0].X);
            w.Float(PrimaryRChromaticityY, p[0].Y);
            w.Float(PrimaryGChromaticityX, p[1].X);
            w.Float(PrimaryGChromaticityY, p[1].Y);
            w.Float(PrimaryBChromaticityX, p[2].X);
            w.Float(PrimaryBChromaticityY, p[2].Y);
        }

        if (hdr.WhitePoint is { } white)
        {
            w.Float(WhitePointChromaticityX, white.X);
            w.Float(WhitePointChromaticityY, white.Y);
        }

        if (hdr.MaxLuminance is { } max)
            w.Float(LuminanceMax, max);
        if (hdr.MinLuminance is { } min)
            w.Float(LuminanceMin, min);
        return w.Length == 0 ? null : EbmlWriter.Element(MasteringMetadata, w.WrittenSpan);
    }

    /// <summary>
    /// FlagForced is fed by both <see cref="Track.IsForced"/> and the "forced only" characteristic; whichever the
    /// user changed wins.
    /// </summary>
    private static bool ForcedFlag(TrackEditState original, TrackEditState state)
    {
        if (state.IsForced != original.IsForced)
            return state.IsForced;
        var hadForcedOnly = Has(original, MediaCharacteristics.ForcedOnly);
        var hasForcedOnly = Has(state, MediaCharacteristics.ForcedOnly);
        return hadForcedOnly != hasForcedOnly ? hasForcedOnly : original.IsForced;
    }

    private static bool HearingImpaired(TrackEditState s) =>
        Has(s, MediaCharacteristics.TranscribesSpokenDialog) || Has(s, MediaCharacteristics.DescribesMusicAndSound);

    private static bool Has(TrackEditState s, string characteristic) =>
        s.Characteristics.Split('|').Contains(characteristic, StringComparer.Ordinal);

    private static byte[] Encode(Action<EbmlWriter> body)
    {
        var w = new EbmlWriter();
        body(w);
        return w.ToArray();
    }
}
