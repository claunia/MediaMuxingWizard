using System.Buffers.Binary;
using System.Globalization;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Formats.Mp4.Boxes;

/// <summary>Extracts codec descriptions from sample entries.</summary>
public static class CodecInfo
{
    public static string FormatName(Box entry)
    {
        return entry.Type switch
        {
            "avc1" or "avc2" or "avc3" or "avc4" => "H.264",
            "hvc1" or "hev1" => "HEVC",
            "dvh1" or "dvhe" => "HEVC Dolby Vision",
            "dva1" or "dvav" => "H.264 Dolby Vision",
            "dav1" => "AV1 Dolby Vision",
            "av01" => "AV1",
            "vvc1" or "vvi1" => "VVC",
            "evc1" => "EVC",
            "mp4v" => "MPEG-4 Visual",
            "avst" => "AVS2",
            "avs3" => "AVS3",
            "jpeg" => "JPEG",
            "png " => "PNG",
            "vp08" => "VP8",
            "vp09" => "VP9",
            "apcn" or "apch" or "apcs" or "apco" or "ap4h" or "ap4x" => "ProRes",
            "mp4a" => Mp4aName(entry),
            "ac-3" => "AC-3",
            "ec-3" => "E-AC-3",
            "ac-4" => "AC-4",
            "alac" => "ALAC",
            "dtsc" or "dtsh" or "dtsl" or "dtse" or "dtsx" => "DTS",
            "Opus" => "Opus",
            "fLaC" => "FLAC",
            ".mp3" => "MP3",
            "lpcm" or "sowt" or "twos" or "ipcm" or "fpcm" => "PCM",
            "mlpa" => "TrueHD",
            "tx3g" => "Tx3g",
            "text" => "Text",
            "wvtt" => "WebVTT",
            "c608" => "CEA-608",
            "c708" => "CEA-708",
            "stpp" => "TTML",
            "subp" => "VobSub",
            "mp4s" => "VobSub",
            "tmcd" => "Timecode",
            _ => entry.Type.Trim(),
        };
    }

    private static string Mp4aName(Box entry)
    {
        var esds = FindDescendant(entry, "esds");
        if (esds is null)
            return "AAC";
        var (objectType, asc) = ParseEsds(esds.Payload);
        return objectType switch
        {
            0x69 or 0x6B => "MP3",
            0xA5 => "AC-3",
            0xA6 => "E-AC-3",
            0xA9 => "DTS",
            0xAD => "Opus",
            _ => AacProfile(asc),
        };
    }

    private static string AacProfile(byte[]? asc)
    {
        if (asc is null || asc.Length < 2)
            return "AAC";
        var aot = asc[0] >> 3;
        if (aot == 31 && asc.Length >= 2)
            aot = 32 + (((asc[0] & 7) << 3) | (asc[1] >> 5));
        return aot switch
        {
            1 => "AAC Main",
            2 => "AAC",
            3 => "AAC SSR",
            4 => "AAC LTP",
            5 => "HE-AAC",
            29 => "HE-AACv2",
            23 => "AAC LD",
            39 => "AAC ELD",
            42 => "xHE-AAC",
            _ => "AAC",
        };
    }

    /// <summary>Returns the MPEG-4 object type and decoder-specific info from an esds payload.</summary>
    public static (int ObjectType, byte[]? DecoderSpecificInfo) ParseEsds(ReadOnlySpan<byte> payload)
    {
        // payload: version/flags (4) then ES_Descriptor.
        var p = payload[4..];
        var pos = 0;
        int objectType = 0;
        byte[]? dsi = null;
        while (pos < p.Length)
        {
            var tag = p[pos++];
            var len = ReadDescriptorLength(p, ref pos);
            if (len < 0 || pos + len > p.Length)
                break;
            switch (tag)
            {
                case 0x03: // ES_Descriptor
                {
                    var flags = p[pos + 2];
                    var skip = 3;
                    if ((flags & 0x80) != 0)
                        skip += 2;
                    if ((flags & 0x40) != 0)
                        skip += 1 + p[pos + skip];
                    if ((flags & 0x20) != 0)
                        skip += 2;
                    pos += skip;
                    continue;
                }

                case 0x04: // DecoderConfigDescriptor
                    objectType = p[pos];
                    pos += 13;
                    continue;
                case 0x05: // DecoderSpecificInfo
                    dsi = p.Slice(pos, len).ToArray();
                    pos += len;
                    continue;
                default:
                    pos += len;
                    continue;
            }
        }

        return (objectType, dsi);
    }

    private static int ReadDescriptorLength(ReadOnlySpan<byte> p, ref int pos)
    {
        var len = 0;
        for (var i = 0; i < 4 && pos < p.Length; i++)
        {
            var b = p[pos++];
            len = (len << 7) | (b & 0x7F);
            if ((b & 0x80) == 0)
                return len;
        }

        return -1;
    }

    /// <summary>Fills video-specific fields from a visual sample entry.</summary>
    public static void DescribeVideo(Box entry, VideoTrack track)
    {
        var p = entry.Payload;
        if (p.Length >= 78)
        {
            track.PixelWidth = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(24));
            track.PixelHeight = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(26));
        }

        if (entry.Find("pasp") is { Payload.Length: >= 8 } pasp)
        {
            track.ParNumerator = (int)BinaryPrimitives.ReadUInt32BigEndian(pasp.Payload);
            track.ParDenominator = (int)BinaryPrimitives.ReadUInt32BigEndian(pasp.Payload.AsSpan(4));
        }

        if (entry.Find("colr") is { Payload.Length: >= 10 } colr)
        {
            var kind = Box.Latin1.GetString(colr.Payload, 0, 4);
            if (kind is "nclx" or "nclc")
            {
                var full = kind == "nclx" && colr.Payload.Length >= 11 ? (colr.Payload[10] & 0x80) != 0 : (bool?)null;
                track.Color = new ColorInfo(
                    BinaryPrimitives.ReadUInt16BigEndian(colr.Payload.AsSpan(4)),
                    BinaryPrimitives.ReadUInt16BigEndian(colr.Payload.AsSpan(6)),
                    BinaryPrimitives.ReadUInt16BigEndian(colr.Payload.AsSpan(8)),
                    full);
            }
        }

        track.ProfileLevel = ProfileLevel(entry);
        track.Hdr = ParseHdr(entry);
        var dvBox = entry.Find("dvcC") ?? entry.Find("dvvC") ?? entry.Find("dvwC");
        if (dvBox is { Payload.Length: >= 5 })
            track.DolbyVisionRecord = dvBox.Payload;

        var details = string.Create(CultureInfo.InvariantCulture, $"{track.PixelWidth}×{track.PixelHeight}");
        if (track.ParNumerator > 0 && track.ParDenominator > 0 && track.ParNumerator != track.ParDenominator)
            details += string.Create(CultureInfo.InvariantCulture, $", PAR {track.ParNumerator}:{track.ParDenominator}");
        if (track.ProfileLevel.Length > 0)
            details += ", " + track.ProfileLevel;
        if (track.DolbyVision is { } dv)
            details += ", DV " + dv;
        else if (track.Hdr is not null || track.Color.Transfer is 16 or 18)
            details += track.Color.Transfer == 18 ? ", HLG" : ", HDR10";
        track.FormatDetails = details;
    }

    private static string ProfileLevel(Box entry)
    {
        if (entry.Find("avcC") is { Payload.Length: >= 4 } avcc)
        {
            var profile = avcc.Payload[1] switch
            {
                66 => "Baseline",
                77 => "Main",
                88 => "Extended",
                100 => "High",
                110 => "High 10",
                122 => "High 4:2:2",
                244 => "High 4:4:4",
                var x => x.ToString(CultureInfo.InvariantCulture),
            };
            var level = avcc.Payload[3];
            return string.Create(CultureInfo.InvariantCulture, $"{profile}@{level / 10}.{level % 10}");
        }

        if (entry.Find("hvcC") is { Payload.Length: >= 13 } hvcc)
        {
            var profile = (hvcc.Payload[1] & 0x1F) switch
            {
                1 => "Main",
                2 => "Main 10",
                3 => "Main Still",
                4 => "RExt",
                var x => x.ToString(CultureInfo.InvariantCulture),
            };
            var level = hvcc.Payload[12] / 30.0;
            return string.Create(CultureInfo.InvariantCulture, $"{profile}@L{level:0.#}");
        }

        if (entry.Find("av3c") is { } av3c && Avs.SequenceHeaderOfAv3C(av3c.Payload) is { IsEmpty: false } header &&
            Avs.ParseSequenceHeader(AvsGeneration.Avs3, header) is { } avs3)
        {
            return Avs.ProfileLevel(AvsGeneration.Avs3, avs3.ProfileId, avs3.LevelId);
        }

        if (entry.Find("evcC") is { Payload.Length: >= 3 } evcc)
            return Evc.ProfileLevel(evcc.Payload);

        if (entry.Find("vvcC") is { Payload.Length: > 4 } vvcc)
            return Vvc.ProfileLevel(vvcc.Payload.AsSpan(4)); // FullBox

        return string.Empty;
    }

    /// <summary>
    /// Static HDR metadata of a visual sample entry: 'mdcv' (primaries in G, B, R order like the HEVC SEI, 0.00002 and
    /// 0.0001 cd/m² units) or the VP9 binding's 'SmDm' (FullBox; R, G, B order, 0.16 fixed point chromaticity, 24.8
    /// and 18.14 fixed point luminance), 'clli' or 'CoLL' (FullBox), and 'amve'. Read the way FFmpeg reads them.
    /// </summary>
    private static HdrInfo? ParseHdr(Box entry)
    {
        var mdcv = entry.Find("mdcv")?.Payload;
        var smdm = mdcv is null ? entry.Find("SmDm")?.Payload : null;
        var clli = entry.Find("clli")?.Payload;
        var coll = clli is null ? entry.Find("CoLL")?.Payload : null;
        var amve = entry.Find("amve")?.Payload;
        if (mdcv is null && smdm is null && clli is null && coll is null && amve is null)
            return null;

        HdrInfo? mastering = null;
        if (mdcv is { Length: >= 24 })
            mastering = VideoStreamInfoScanner.ParseMasteringDisplaySei(mdcv); // same layout as the SEI message
        else if (smdm is { Length: >= 28 })
            mastering = VideoStreamInfoScanner.ParseAv1Mdcv(smdm.AsSpan(4)); // same layout as AV1 metadata_hdr_mdcv

        int? maxCll = null, maxFall = null;
        var light = clli ?? (coll is { Length: >= 8 } ? coll[4..] : null);
        if (light is { Length: >= 4 })
        {
            maxCll = BinaryPrimitives.ReadUInt16BigEndian(light);
            maxFall = BinaryPrimitives.ReadUInt16BigEndian(light.AsSpan(2));
        }

        var ambient = amve is { Length: >= 8 } ? VideoStreamInfoScanner.ParseAmbientViewingEnvironment(amve) : null;
        return new HdrInfo
        {
            DisplayPrimaries = mastering?.DisplayPrimaries,
            WhitePoint = mastering?.WhitePoint,
            MaxLuminance = mastering?.MaxLuminance,
            MinLuminance = mastering?.MinLuminance,
            MaxCll = maxCll,
            MaxFall = maxFall,
            AmbientIlluminance = ambient?.AmbientIlluminance,
            AmbientLight = ambient?.AmbientLight,
        };
    }

    /// <summary>Parses a DOVIDecoderConfigurationRecord (shared by MP4 and Matroska).</summary>
    public static DolbyVisionInfo ParseDolbyVisionRecord(ReadOnlySpan<byte> p)
    {
        var profile = p[2] >> 1;
        var level = ((p[2] & 1) << 5) | (p[3] >> 3);
        return new DolbyVisionInfo(p[0], p[1], profile, level, (p[3] & 4) != 0, (p[3] & 2) != 0, (p[3] & 1) != 0, p[4] >> 4);
    }

    /// <summary>Fills audio-specific fields from an audio sample entry.</summary>
    public static void DescribeAudio(Box entry, AudioTrack track)
    {
        var p = entry.Payload;
        if (p.Length >= 28)
        {
            var version = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(8));
            track.Channels = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(16));
            track.SampleRate = entry.Type == "mlpa"
                ? (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(24)) // MLPSampleEntry: 32-bit integer rate
                : (int)(BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(24)) >> 16);
            if (version == 2 && p.Length >= 64)
            {
                track.SampleRate = (int)BitConverter.Int64BitsToDouble((long)BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(32)));
                track.Channels = (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(40));
            }
        }

        // ISO version 1 entries hold rates above 65535 Hz in a SamplingRateBox (the 16.16 field is a divisor of it).
        if (entry.Find("srat") is { Payload.Length: >= 8 } srat && BinaryPrimitives.ReadUInt32BigEndian(srat.Payload.AsSpan(4)) is > 0 and <= int.MaxValue and var rate)
            track.SampleRate = (int)rate;

        if (entry.Find("dac3") is { Payload.Length: >= 3 } dac3)
        {
            var acmod = (dac3.Payload[1] >> 3) & 7;
            var lfe = (dac3.Payload[1] >> 2) & 1;
            track.Channels = AcmodChannels(acmod) + lfe;
            track.ChannelLayout = AcmodLayout(acmod, lfe);
        }
        else if (entry.Find("dec3") is { Payload.Length: >= 5 } dec3)
        {
            var acmod = (dec3.Payload[3] >> 1) & 7;
            var lfe = dec3.Payload[3] & 1;
            var numDepSub = (dec3.Payload[4] >> 1) & 0xF;
            var channels = AcmodChannels(acmod) + lfe;
            if (numDepSub > 0 && dec3.Payload.Length >= 6)
            {
                var chanLoc = ((dec3.Payload[4] & 1) << 8) | dec3.Payload[5];
                channels += ChanLocChannels(chanLoc);
            }

            track.Channels = channels;
            track.ChannelLayout = AcmodLayout(acmod, lfe);

            // flag_ec3_extension_type_a follows the independent substream info (Dolby Atmos JOC).
            var extIndex = numDepSub > 0 ? 6 : 5;
            if (dec3.Payload.Length > extIndex && (dec3.Payload[extIndex] & 1) != 0)
                track.IsAtmos = true;
        }
        else if (FindDescendant(entry, "esds") is { } esds)
        {
            var (_, asc) = ParseEsds(esds.Payload);
            if (asc is { Length: >= 2 })
            {
                var cfg = (asc[1] >> 3) & 0xF;
                if (cfg is > 0 and < 8)
                    track.Channels = cfg == 7 ? 8 : cfg;
            }
        }

        var parts = new List<string>();
        if (track.Channels > 0)
            parts.Add(ChannelDescription(track.Channels));
        if (track.SampleRate > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{track.SampleRate / 1000.0:0.###} kHz"));
        if (track.IsAtmos)
            parts.Add("Atmos");
        track.FormatDetails = string.Join(", ", parts);
    }

    public static string ChannelDescription(int channels) => channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        6 => "5.1",
        8 => "7.1",
        _ => string.Create(CultureInfo.InvariantCulture, $"{channels} ch"),
    };

    private static int AcmodChannels(int acmod) => acmod switch
    {
        0 => 2,
        1 => 1,
        2 => 2,
        3 => 3,
        4 => 3,
        5 => 4,
        6 => 4,
        7 => 5,
        _ => 2,
    };

    private static string AcmodLayout(int acmod, int lfe) => (acmod switch
    {
        0 => "1+1",
        1 => "1/0",
        2 => "2/0",
        3 => "3/0",
        4 => "2/1",
        5 => "3/1",
        6 => "2/2",
        7 => "3/2",
        _ => string.Empty,
    }) + (lfe != 0 ? ".1" : string.Empty);

    private static int ChanLocChannels(int chanLoc)
    {
        // Bit order per ETSI TS 102 366 Table E.1.4 (pairs except Cs, Ts and LFE2).
        int[] counts = [2, 2, 2, 1, 1, 2, 2, 1, 1];
        var n = 0;
        for (var bit = 0; bit < 9; bit++)
        {
            if ((chanLoc & (1 << (8 - bit))) != 0)
                n += counts[bit];
        }

        return n;
    }

    /// <summary>Fills subtitle-specific fields from a text sample entry.</summary>
    public static void DescribeSubtitle(Box entry, SubtitleTrack track, Box? tkhd)
    {
        if (tkhd is not null)
        {
            var (w, h) = HeaderBoxes.TkhdSize(tkhd);
            track.Width = (int)w;
            track.Height = (int)h;
        }

        if (entry.Type == "tx3g" && entry.Payload.Length >= 12)
        {
            var displayFlags = BinaryPrimitives.ReadUInt32BigEndian(entry.Payload.AsSpan(8));
            if ((displayFlags & 0x80000000) != 0)
                track.ForcedMode = ForcedSubtitleMode.AllSamplesForced;
            else if ((displayFlags & 0x40000000) != 0)
                track.ForcedMode = ForcedSubtitleMode.SomeSamplesForced;
            // Layout: reserved(6) dataRef(2) displayFlags(4) hJust(1) vJust(1) bgColor(4) box{top,left,bottom,right}(8).
            if (entry.Payload.Length >= 26 && track.Height > 0)
            {
                var top = BinaryPrimitives.ReadInt16BigEndian(entry.Payload.AsSpan(18));
                var bottom = BinaryPrimitives.ReadInt16BigEndian(entry.Payload.AsSpan(22));
                track.PlaceAtTop = top == 0 && bottom > 0 && bottom <= track.Height / 2;
            }
        }

        if (track.Width > 0 && track.Height > 0)
            track.FormatDetails = string.Create(CultureInfo.InvariantCulture, $"{track.Width}×{track.Height}");
    }

    private static Box? FindDescendant(Box box, string type)
    {
        if (box.Children is null)
            return null;
        foreach (var c in box.Children)
        {
            if (c.Type == type)
                return c;
            if (FindDescendant(c, type) is { } found)
                return found;
        }

        return null;
    }
}
