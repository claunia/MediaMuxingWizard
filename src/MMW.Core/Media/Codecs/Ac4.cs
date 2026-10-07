// Dolby AC-4 sync frame helpers and the public face of the TOC parser / DSI writer.
//
// The TOC parser (Ac4Toc.cs) and the DSI writer (Ac4Dsi.cs) are ported from GPAC (https://github.com/gpac/gpac),
// src/media_tools/av_parsers.c and src/odf/descriptors.c, Copyright (c) Telecom ParisTech / the GPAC authors,
// licensed under the GNU Lesser General Public License version 2.1 or later; relicensed here under the GNU
// General Public License version 3 or later, as section 3 of the LGPL permits.

using System.Globalization;
using MMW.Core.Resources;

namespace MMW.Core.Media.Codecs;

/// <summary>One presentation of an AC-4 TOC, as described by the decoder specific information.</summary>
public sealed record Ac4Presentation
{
    /// <summary>presentation_version: 1 for regular presentations, 2 for immersive stereo (IMS).</summary>
    public int Version { get; init; }

    /// <summary>presentation_config (0x1F for a single substream group).</summary>
    public int Config { get; init; }

    /// <summary>presentation_id, or -1 when absent.</summary>
    public int PresentationId { get; init; } = -1;

    /// <summary>The presentation is channel based (b_presentation_channel_coded).</summary>
    public bool ChannelCoded { get; init; }

    /// <summary>dsi_presentation_ch_mode (ETSI TS 103 190-2 Table 78), or -1 for object based presentations.</summary>
    public int ChannelMode { get; init; } = -1;

    /// <summary>presentation_v1_channel_groups: the speaker group index mask (ETSI TS 103 190-2 Table A.27).</summary>
    public int ChannelGroups { get; init; }

    /// <summary>pres_top_channel_pairs (0, 1 or 2).</summary>
    public int TopChannelPairs { get; init; }

    /// <summary>pres_b_4_back_channels_present.</summary>
    public bool BackChannels { get; init; }

    /// <summary>immersive_audio_indicator.</summary>
    public bool Immersive { get; init; }

    /// <summary>de_indicator: dialogue enhancement is available.</summary>
    public bool DialogueEnhancement { get; init; }
}

/// <summary>What the table of contents (ac4_toc) of an AC-4 frame tells about the stream.</summary>
public sealed record Ac4Info
{
    /// <summary>bitstream_version (2 for current streams; versions 0 and 1 are not supported).</summary>
    public int BitstreamVersion { get; init; }

    /// <summary>fs_index: 0 for 44.1 kHz, 1 for 48 kHz.</summary>
    public int FsIndex { get; init; }

    /// <summary>frame_rate_index (ETSI TS 103 190-1 Table 83).</summary>
    public int FrameRateIndex { get; init; }

    /// <summary>b_iframe_global: the frame is an independent (I) frame.</summary>
    public bool IFrame { get; init; }

    /// <summary>Base sampling rate in Hz (44100 or 48000).</summary>
    public int SampleRate { get; init; }

    /// <summary>Duration of a frame in <see cref="MediaTimescale"/> units (ETSI TS 103 190-1 Table E.1); 0 when unknown.</summary>
    public int SampleDuration { get; init; }

    /// <summary>The media time scale the frame durations are expressed in (48000, 240000 or 44100); 0 when unknown.</summary>
    public int MediaTimescale { get; init; }

    /// <summary>Audio samples per frame at <see cref="SampleRate"/> (fractional for the NTSC rates); 0 when unknown.</summary>
    public double SamplesPerFrame => MediaTimescale == 0 ? 0 : (double)SampleDuration * SampleRate / MediaTimescale;

    /// <summary>Frames per second; 0 when unknown.</summary>
    public double FrameRate => SampleDuration == 0 ? 0 : (double)MediaTimescale / SampleDuration;

    /// <summary>Channel count of the default presentation, as GPAC derives it.</summary>
    public int ChannelCount { get; init; }

    /// <summary>Size in bytes of the ac4_toc().</summary>
    public int TocSize { get; init; }

    /// <summary>short_program_id, or -1 when absent.</summary>
    public int ShortProgramId { get; init; } = -1;

    /// <summary>The presentations, the first one being the default.</summary>
    public IReadOnlyList<Ac4Presentation> Presentations { get; init; } = [];

    /// <summary>The full GPAC-equivalent structures, for the DSI writer.</summary>
    internal Ac4StreamInfo? Stream { get; init; }
}

/// <summary>Dolby AC-4 bitstream helpers (ETSI TS 103 190-1/-2).</summary>
public static class Ac4
{
    /// <summary>ac4_syncframe sync word without CRC.</summary>
    public const int SyncWord = 0xAC40;

    /// <summary>ac4_syncframe sync word of a frame followed by a 16-bit CRC.</summary>
    public const int SyncWordCrc = 0xAC41;

    /// <summary>
    /// Parses an ac4_syncframe header (ETSI TS 103 190-1 Annex G): sync word 0xAC40 (or 0xAC41 when a CRC
    /// follows the frame) and a 16-bit frame_size, 0xFFFF escaping to a 24-bit size.
    /// </summary>
    /// <param name="data">Data starting at the sync word.</param>
    /// <param name="frameLength">Size of the raw_ac4_frame that follows the header.</param>
    /// <param name="crc">The frame is followed by a 2-byte CRC.</param>
    /// <returns>The header length (4 or 7 bytes), or 0 when <paramref name="data"/> holds no sync frame header.
    /// The whole sync frame is header + <paramref name="frameLength"/> + (CRC ? 2 : 0) bytes.</returns>
    public static int SyncFrameHeaderLength(ReadOnlySpan<byte> data, out int frameLength, out bool crc)
    {
        frameLength = 0;
        crc = false;
        if (data.Length < 4 || data[0] != 0xAC || (data[1] != 0x40 && data[1] != 0x41))
            return 0;
        crc = data[1] == 0x41;
        var size = (data[2] << 8) | data[3];
        if (size != 0xFFFF)
        {
            frameLength = size;
            return 4;
        }

        if (data.Length < 7)
        {
            crc = false;
            return 0;
        }

        frameLength = (data[4] << 16) | (data[5] << 8) | data[6];
        return 7;
    }

    /// <summary>Total size of the sync frame at the start of <paramref name="data"/> (header, frame and CRC); 0 when none.</summary>
    public static int SyncFrameLength(ReadOnlySpan<byte> data)
    {
        var header = SyncFrameHeaderLength(data, out var frameLength, out var crc);
        return header == 0 ? 0 : header + frameLength + (crc ? 2 : 0);
    }

    /// <summary>Offset of the next AC-4 sync word at or after <paramref name="start"/>, or -1.</summary>
    public static int FindSync(ReadOnlySpan<byte> data, int start = 0)
    {
        for (var i = Math.Max(0, start); i + 1 < data.Length; i++)
        {
            if (data[i] == 0xAC && (data[i + 1] == 0x40 || data[i + 1] == 0x41))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// The raw_ac4_frame of a sync frame (what MP4 and Matroska samples hold), without header and CRC; null when
    /// <paramref name="syncFrame"/> does not start with a complete sync frame.
    /// </summary>
    public static byte[]? RawFrame(ReadOnlySpan<byte> syncFrame)
    {
        var header = SyncFrameHeaderLength(syncFrame, out var frameLength, out _);
        if (header == 0 || frameLength == 0 || header + frameLength > syncFrame.Length)
            return null;
        return syncFrame.Slice(header, frameLength).ToArray();
    }

    /// <summary>
    /// Parses the table of contents of a raw_ac4_frame (and the substream metadata GPAC reads to fill the DSI).
    /// Null when the frame is not a supported AC-4 frame (bitstream_version 0/1, inconsistent TOC).
    /// </summary>
    public static Ac4Info? Parse(ReadOnlySpan<byte> rawFrame)
    {
        if (rawFrame.Length < 2)
            return null;
        Ac4StreamInfo? stream;
        try
        {
            stream = Ac4Toc.Parse(rawFrame.ToArray());
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }

        if (stream is null)
            return null;

        var presentations = new List<Ac4Presentation>();
        if (stream.Presentations is { } list)
        {
            foreach (var p in list.Take(stream.NPresentations))
            {
                presentations.Add(new Ac4Presentation
                {
                    Version = p.PresentationVersion,
                    Config = p.PresentationConfig,
                    PresentationId = p.PresentationIdPresent != 0 ? p.PresentationId : -1,
                    ChannelCoded = p.PresentationChannelCoded != 0,
                    ChannelMode = p.PresentationChannelCoded != 0 ? p.DsiPresentationChMode : -1,
                    ChannelGroups = (int)p.PresentationV1ChannelGroups,
                    TopChannelPairs = p.PresTopChannelPairs,
                    BackChannels = p.PresB4BackChannelsPresent != 0,
                    Immersive = p.ImmersiveAudioIndicator != 0,
                    DialogueEnhancement = p.DeIndicator != 0,
                });
            }
        }

        return new Ac4Info
        {
            BitstreamVersion = stream.BitstreamVersion,
            FsIndex = stream.FsIndex,
            FrameRateIndex = stream.FrameRateIndex,
            IFrame = stream.IFrameGlobal != 0,
            SampleRate = (int)stream.SampleRate,
            SampleDuration = (int)stream.SampleDuration,
            MediaTimescale = (int)stream.MediaTimeScale,
            ChannelCount = (int)stream.ChannelCount,
            TocSize = (int)stream.TocSize,
            ShortProgramId = stream.ProgramIdPresent != 0 ? stream.ShortProgramId : -1,
            Presentations = presentations,
            Stream = stream,
        };
    }

    /// <summary>
    /// Builds the ac4_dsi_v1 (ETSI TS 103 190-2 Annex E.6), the payload of the ISO BMFF 'dac4' box, from a
    /// raw_ac4_frame, exactly as GPAC writes it. Use the first frame of the stream, which must be an I-frame
    /// (<see cref="Ac4Info.IFrame"/>): the TOC is complete in every frame, but the dialogue enhancement
    /// configuration that sets de_indicator is only guaranteed in independent frames.
    /// </summary>
    public static byte[]? BuildDsi(ReadOnlySpan<byte> rawFrame) => Parse(rawFrame) is { } info ? BuildDsi(info) : null;

    /// <summary>Builds the ac4_dsi_v1 ('dac4' payload) from a parsed frame.</summary>
    public static byte[]? BuildDsi(Ac4Info info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.Stream is { } stream ? Ac4Dsi.Write(stream) : null;
    }

    private static readonly string[] ChannelModeNames =
    [
        "1.0", "2.0", "3.0", "5.0", "5.1", "7.0", "7.1", "7.0 (wide)", "7.1 (wide)", "5.0.2", "5.1.2", "7.0.4", "7.1.4", "9.0.4", "9.1.4", "22.2",
    ];

    // Speaker group index mask (ETSI TS 103 190-2 Table A.27) split in listener-level, LFE and height speakers.
    private const int LfeGroups = (1 << 6) | (1 << 12);
    private const int TopGroups = (1 << 4) | (1 << 5) | (1 << 7) | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 11) | (1 << 18);

    /// <summary>
    /// A short description of the stream for the UI: the layout of the default presentation ("2.0", "5.1",
    /// "5.1.4", "Immersive Stereo", "Object based"), plus the number of presentations when there are several.
    /// </summary>
    public static string Describe(Ac4Info info) => DescribeInfo(info);

    /// <summary>
    /// The 'ac-4' sample entry (ETSI TS 103 190-2 Annex E) for a stream whose first raw frame is
    /// <paramref name="rawFrame"/>, with the 'dac4' built from it; null when the frame cannot be parsed.
    /// </summary>
    public static byte[]? BuildEntry(ReadOnlySpan<byte> rawFrame)
    {
        if (Parse(rawFrame) is not { } info || BuildDsi(info) is not { } dsi)
            return null;
        return QuickTime.AudioEntry("ac-4", info.ChannelCount > 0 ? info.ChannelCount : 2, info.SampleRate, QuickTime.Box("dac4", dsi));
    }

    private static string DescribeInfo(Ac4Info info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Presentations.Count == 0)
            return "AC-4";

        var p = info.Presentations[0];
        string layout;
        if (p.Version == 2)
        {
            layout = "Immersive Stereo";
        }
        else if (p.ChannelCoded && p.ChannelMode is >= 11 and <= 14)
        {
            // x.x.4 families: the actual speakers depend on the back/centre/top flags, so count the groups.
            var mask = p.ChannelGroups;
            var lfe = ChannelCount(mask & LfeGroups);
            var top = ChannelCount(mask & TopGroups);
            var main = ChannelCount(mask & ~(LfeGroups | TopGroups));
            layout = top > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{main}.{lfe}.{top}")
                : string.Create(CultureInfo.InvariantCulture, $"{main}.{lfe}");
        }
        else if (p.ChannelCoded && p.ChannelMode is >= 0 and < 16)
        {
            layout = ChannelModeNames[p.ChannelMode];
        }
        else
        {
            layout = p.Immersive ? Strings.Detail_ImmersiveObjectBased : Strings.Detail_ObjectBased;
            if (info.ChannelCount > 0)
                layout += string.Create(CultureInfo.InvariantCulture, $" ({info.ChannelCount} ch)");
        }

        return info.Presentations.Count > 1
            ? string.Format(CultureInfo.CurrentCulture, Strings.Detail_Presentations, layout, info.Presentations.Count)
            : layout;
    }

    /// <summary>Speakers in a speaker group index mask.</summary>
    private static int ChannelCount(int speakerGroups)
    {
        var count = 0;
        for (var i = 0; i < Ac4Toc.SpeakerGroupChannels.Length; i++)
        {
            if ((speakerGroups & (1 << i)) != 0)
                count += Ac4Toc.SpeakerGroupChannels[i];
        }

        return count;
    }
}
