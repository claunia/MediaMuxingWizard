namespace MMW.Core.Media.Codecs;

/// <summary>Decoded MPEG-4 AudioSpecificConfig.</summary>
/// <param name="ObjectType">Audio object type (2 = AAC LC, 5 = SBR, 29 = PS …).</param>
/// <param name="SampleRate">Core sampling frequency.</param>
/// <param name="Channels">Channels from channelConfiguration (0 when defined by a program config element).</param>
/// <param name="ExtensionSampleRate">Output rate with explicit SBR signalling, else 0.</param>
/// <param name="FrameLength">Samples per frame (1024 or 960).</param>
public readonly record struct AacConfig(int ObjectType, int SampleRate, int Channels, int ExtensionSampleRate, int FrameLength);

/// <summary>Header of an ADTS frame.</summary>
public readonly record struct AdtsHeader(int ObjectType, int SamplingIndex, int ChannelConfig, int FrameLength, int HeaderLength, int RawBlocks)
{
    public int SampleRate => Aac.SampleRates[SamplingIndex];
}

/// <summary>AAC AudioSpecificConfig and ADTS helpers.</summary>
public static class Aac
{
    public static readonly int[] SampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350, 0, 0, 0];

    public static int SamplingIndex(int rate)
    {
        var index = Array.IndexOf(SampleRates, rate);
        if (index >= 0)
            return index;

        // Nearest standard rate.
        var best = 0;
        for (var i = 1; i < 13; i++)
        {
            if (Math.Abs(SampleRates[i] - rate) < Math.Abs(SampleRates[best] - rate))
                best = i;
        }

        return best;
    }

    /// <summary>Parses an AudioSpecificConfig.</summary>
    public static AacConfig ParseConfig(ReadOnlySpan<byte> asc)
    {
        var r = new BitReader(asc);
        var aot = ReadObjectType(ref r);
        var rate = ReadRate(ref r);
        var channels = (int)r.Read(4);
        var extRate = 0;
        if (aot is 5 or 29)
        {
            extRate = ReadRate(ref r);
            aot = ReadObjectType(ref r);
            if (aot == 22)
                r.Skip(4);
        }

        var frameLength = 1024;
        if (aot is 1 or 2 or 3 or 4 or 6 or 7 or 17 or 19 or 20 or 21 or 22 or 23 && r.BitsLeft >= 1)
            frameLength = r.Flag() ? 960 : 1024;
        if (aot is 23 or 39)
            frameLength = frameLength == 960 ? 480 : 512;
        return new AacConfig(aot, rate, channels == 7 ? 8 : channels, extRate, frameLength);
    }

    private static int ReadObjectType(ref BitReader r)
    {
        var aot = (int)r.Read(5);
        return aot == 31 ? 32 + (int)r.Read(6) : aot;
    }

    private static int ReadRate(ref BitReader r)
    {
        var index = (int)r.Read(4);
        return index == 15 ? (int)r.Read(24) : SampleRates[index];
    }

    /// <summary>Builds a 2-byte (or 5-byte with explicit SBR) AudioSpecificConfig.</summary>
    /// <param name="objectType">Audio object type of the core (2 = LC).</param>
    /// <param name="sampleRate">Core sample rate.</param>
    /// <param name="channels">Channel count (mapped to channelConfiguration).</param>
    /// <param name="sbrOutputRate">Output sample rate when SBR is signalled explicitly; 0 for none.</param>
    public static byte[] BuildConfig(int objectType, int sampleRate, int channels, int sbrOutputRate = 0)
    {
        var w = new BitWriter();
        var channelConfig = channels switch
        {
            8 => 7,
            > 0 and < 7 => channels,
            _ => 2,
        };
        if (sbrOutputRate > 0)
        {
            w.Write(5, 5);
            w.Write((ulong)SamplingIndex(sampleRate), 4);
            w.Write((ulong)channelConfig, 4);
            w.Write((ulong)SamplingIndex(sbrOutputRate), 4);
            w.Write((ulong)objectType, 5);
        }
        else
        {
            w.Write((ulong)objectType, 5);
            w.Write((ulong)SamplingIndex(sampleRate), 4);
            w.Write((ulong)channelConfig, 4);
        }

        w.Write(0, 3); // frameLengthFlag, dependsOnCoreCoder, extensionFlag
        return w.ToArray();
    }

    /// <summary>Builds an AudioSpecificConfig from a legacy Matroska codec ID (A_AAC/MPEG4/LC/SBR …).</summary>
    public static byte[] ConfigFromCodecId(string codecId, int sampleRate, int channels, int outputSampleRate)
    {
        var profile = codecId.Length > 12 ? codecId[12..] : string.Empty; // after "A_AAC/MPEGx/"
        var aot = profile switch
        {
            "MAIN" => 1,
            "SSR" => 3,
            "LTP" => 4,
            _ => 2,
        };
        var sbr = profile.EndsWith("/SBR", StringComparison.Ordinal);
        if (sbr)
        {
            var output = outputSampleRate > 0 ? outputSampleRate : sampleRate * 2;
            var core = outputSampleRate > 0 && outputSampleRate == sampleRate ? sampleRate / 2 : sampleRate;
            return BuildConfig(2, core, channels, output);
        }

        return BuildConfig(aot, sampleRate, channels);
    }

    /// <summary>Parses an ADTS header; null when <paramref name="data"/> does not start with one.</summary>
    public static AdtsHeader? ParseAdts(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7 || data[0] != 0xFF || (data[1] & 0xF6) != 0xF0)
            return null;
        var protectionAbsent = (data[1] & 1) != 0;
        var profile = data[2] >> 6;
        var sfi = (data[2] >> 2) & 0xF;
        var channels = ((data[2] & 1) << 2) | (data[3] >> 6);
        var length = ((data[3] & 3) << 11) | (data[4] << 3) | (data[5] >> 5);
        var blocks = data[6] & 3;
        var header = protectionAbsent ? 7 : 9;
        if (sfi > 12 || length < header)
            return null;
        return new AdtsHeader(profile + 1, sfi, channels, length, header, blocks + 1);
    }
}

/// <summary>DTS core frame header.</summary>
public sealed record DtsHeader
{
    public int FrameSize { get; init; }

    public int Samples { get; init; }

    public int SampleRate { get; init; }

    public int Channels { get; init; }

    public bool Lfe { get; init; }

    public int Amode { get; init; }

    public int BitsPerSample { get; init; }

    public int BitRateKbps { get; init; }

    /// <summary>True when the access unit carries a DTS-HD extension substream.</summary>
    public bool HasExtensionSubstream { get; init; }

    /// <summary>True when the extension substream carries lossless (XLL) data (DTS-HD Master Audio).</summary>
    public bool HasLossless { get; init; }
}

/// <summary>DTS frame parsing and ddts construction (ETSI TS 102 114).</summary>
public static class Dts
{
    private static readonly int[] s_rates = [0, 8000, 16000, 32000, 0, 0, 11025, 22050, 44100, 0, 0, 12000, 24000, 48000, 0, 0];
    private static readonly int[] s_amodeChannels = [1, 2, 2, 2, 2, 3, 3, 4, 4, 5, 6, 6, 6, 7, 8, 8];
    private static readonly int[] s_bitrates = [32, 56, 64, 96, 112, 128, 192, 224, 256, 320, 384, 448, 512, 576, 640, 768, 960, 1024, 1152, 1280, 1344, 1408, 1411, 1472, 1536, 1920, 2048, 3072, 3840, 0, 0, 0];
    private static readonly int[] s_pcmr = [16, 16, 20, 20, 0, 24, 24, 0];
    private static readonly int[] s_amodeLayout = [0x0001, 0x0002, 0x0002, 0x0002, 0x0002, 0x0003, 0x0012, 0x0013, 0x0006, 0x0007];

    public static bool HasCoreSync(ReadOnlySpan<byte> d) => d.Length >= 4 && d[0] == 0x7F && d[1] == 0xFE && d[2] == 0x80 && d[3] == 0x01;

    public static bool HasExssSync(ReadOnlySpan<byte> d) => d.Length >= 4 && d[0] == 0x64 && d[1] == 0x58 && d[2] == 0x20 && d[3] == 0x25;

    /// <summary>Parses a DTS access unit (core frame, optionally followed by an extension substream).</summary>
    public static DtsHeader? Parse(ReadOnlySpan<byte> data)
    {
        if (!HasCoreSync(data) || data.Length < 15)
            return null;
        var r = new BitReader(data[4..]);
        r.Skip(1 + 5 + 1); // FTYPE, SHORT, CPF
        var nblks = (int)r.Read(7);
        var fsize = (int)r.Read(14) + 1;
        var amode = (int)r.Read(6);
        var sfreq = (int)r.Read(4);
        var rate = (int)r.Read(5);
        r.Skip(10); // MIX, DYNF, TIMEF, AUXF, HDCD, EXT_AUDIO_ID(3), EXT_AUDIO, ASPF
        var lff = (int)r.Read(2);
        r.Skip(1); // HFLAG
        // HCRC only when CPF; skip FILTS, VERNUM, CHIST then read PCMR.
        var cpf = ((data[4] >> 1) & 1) != 0;
        if (cpf)
            r.Skip(16);
        r.Skip(1 + 4 + 2);
        var pcmr = (int)r.Read(3);

        var exss = false;
        var xll = false;
        if (fsize + 4 <= data.Length && HasExssSync(data[fsize..]))
        {
            exss = true;
            var ext = data[fsize..];
            for (var i = 4; i + 4 <= ext.Length; i++)
            {
                if (ext[i] == 0x41 && ext[i + 1] == 0xA2 && ext[i + 2] == 0x95 && ext[i + 3] == 0x47)
                {
                    xll = true;
                    break;
                }
            }
        }

        return new DtsHeader
        {
            FrameSize = fsize,
            Samples = (nblks + 1) * 32,
            SampleRate = s_rates[sfreq],
            Amode = amode,
            Channels = (amode < 16 ? s_amodeChannels[amode] : 2) + (lff is 1 or 2 ? 1 : 0),
            Lfe = lff is 1 or 2,
            BitsPerSample = s_pcmr[pcmr] == 0 ? 16 : s_pcmr[pcmr],
            BitRateKbps = s_bitrates[rate],
            HasExtensionSubstream = exss,
            HasLossless = xll,
        };
    }

    /// <summary>MP4 sample entry type for a DTS stream.</summary>
    public static string SampleEntryType(DtsHeader h) => h.HasLossless ? "dtsl" : h.HasExtensionSubstream ? "dtsh" : "dtsc";

    /// <summary>Builds a ddts payload (DTSSpecificBox).</summary>
    public static byte[] BuildDdts(DtsHeader h, int channels, long maxBitrate, long avgBitrate)
    {
        var frameCode = h.Samples switch
        {
            <= 512 => 0,
            <= 1024 => 1,
            <= 2048 => 2,
            _ => 3,
        };
        var layout = h.Amode < s_amodeLayout.Length ? s_amodeLayout[h.Amode] : 0x0007;
        if (h.Lfe)
            layout |= 0x0008;
        if (channels >= 8)
            layout = 0x0007 | 0x0008 | 0x0040 | (layout & 0x0010);
        var w = new BitWriter();
        w.Write((ulong)h.SampleRate, 32);
        w.Write((ulong)maxBitrate, 32);
        w.Write((ulong)avgBitrate, 32);
        w.Write((ulong)h.BitsPerSample, 8);
        w.Write((ulong)frameCode, 2);
        w.Write(0, 5); // StreamConstruction (unspecified)
        w.Flag(h.Lfe);
        w.Write((ulong)Math.Min(h.Amode, 63), 6);
        w.Write((ulong)Math.Min(h.FrameSize, 16383), 14);
        w.Write(0, 1); // StereoDownmix
        w.Write(0, 3); // RepresentationType
        w.Write((ulong)layout, 16);
        w.Write(0, 1); // MultiAssetFlag
        w.Write(0, 1); // LBRDurationMod
        w.Write(0, 1); // ReservedBoxPresent
        w.Write(0, 5);
        return w.ToArray();
    }
}

/// <summary>Ogg Opus identification header ↔ MP4 dOps conversion, and packet durations.</summary>
public static class Opus
{
    /// <summary>Converts an OpusHead (RFC 7845, little-endian) to a dOps payload (big-endian).</summary>
    public static byte[] OpusHeadToDops(ReadOnlySpan<byte> head)
    {
        if (head.Length < 19 || !head[..8].SequenceEqual("OpusHead"u8))
            throw new InvalidDataException("Invalid OpusHead.");
        var channels = head[9];
        var family = head[18];
        var w = new List<byte>
        {
            0, // Version
            channels,
            head[11], head[10], // PreSkip
            head[15], head[14], head[13], head[12], // InputSampleRate
            head[17], head[16], // OutputGain
            family,
        };
        if (family != 0)
        {
            if (head.Length < 21 + channels)
                throw new InvalidDataException("Truncated OpusHead channel mapping table.");
            w.AddRange(head.Slice(19, 2 + channels).ToArray());
        }

        return [.. w];
    }

    /// <summary>Converts a dOps payload to an OpusHead.</summary>
    public static byte[] DopsToOpusHead(ReadOnlySpan<byte> dops)
    {
        if (dops.Length < 11)
            throw new InvalidDataException("Invalid dOps box.");
        var channels = dops[1];
        var family = dops[10];
        var w = new List<byte>();
        w.AddRange("OpusHead"u8.ToArray());
        w.Add(1);
        w.Add(channels);
        w.Add(dops[3]);
        w.Add(dops[2]);
        w.Add(dops[7]);
        w.Add(dops[6]);
        w.Add(dops[5]);
        w.Add(dops[4]);
        w.Add(dops[9]);
        w.Add(dops[8]);
        w.Add(family);
        if (family != 0 && dops.Length >= 13 + channels)
            w.AddRange(dops.Slice(11, 2 + channels).ToArray());
        return [.. w];
    }

    /// <summary>Default OpusHead for a stream without a stored header.</summary>
    public static byte[] DefaultHead(int channels, int preSkip, int inputRate)
    {
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = (byte)Math.Clamp(channels, 1, 2);
        head[10] = (byte)preSkip;
        head[11] = (byte)(preSkip >> 8);
        head[12] = (byte)inputRate;
        head[13] = (byte)(inputRate >> 8);
        head[14] = (byte)(inputRate >> 16);
        head[15] = (byte)(inputRate >> 24);
        return head;
    }

    public static (int Channels, int PreSkip, int InputRate) Describe(ReadOnlySpan<byte> head) =>
        head.Length >= 19 ? (head[9], head[10] | (head[11] << 8), head[12] | (head[13] << 8) | (head[14] << 16) | (head[15] << 24)) : (2, 0, 48000);

    /// <summary>Duration of an Opus packet in 48 kHz samples (0 when it cannot be determined).</summary>
    public static int PacketSamples(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 1)
            return 0;
        var toc = packet[0];
        var config = toc >> 3;
        var frame = config switch
        {
            < 12 => (config & 3) switch
            {
                0 => 480,
                1 => 960,
                2 => 1920,
                _ => 2880,
            },
            < 16 => (config & 1) == 0 ? 480 : 960,
            _ => (config & 3) switch
            {
                0 => 120,
                1 => 240,
                2 => 480,
                _ => 960,
            },
        };
        var frames = (toc & 3) switch
        {
            0 => 1,
            1 or 2 => 2,
            _ => packet.Length >= 2 ? packet[1] & 0x3F : 0,
        };
        return frame * frames;
    }
}

/// <summary>FLAC STREAMINFO and frame header helpers.</summary>
public static class Flac
{
    /// <summary>Strips the "fLaC" marker of a Matroska CodecPrivate, returning the metadata blocks.</summary>
    public static byte[] MetadataBlocks(ReadOnlySpan<byte> codecPrivate) =>
        codecPrivate.Length >= 4 && codecPrivate[..4].SequenceEqual("fLaC"u8) ? codecPrivate[4..].ToArray() : codecPrivate.ToArray();

    /// <summary>Returns the metadata blocks with the "last block" flag set on the last one only.</summary>
    public static byte[] FixLastFlags(ReadOnlySpan<byte> blocks)
    {
        var copy = blocks.ToArray();
        var pos = 0;
        var lastHeader = -1;
        while (pos + 4 <= copy.Length)
        {
            copy[pos] &= 0x7F;
            lastHeader = pos;
            var len = (copy[pos + 1] << 16) | (copy[pos + 2] << 8) | copy[pos + 3];
            pos += 4 + len;
        }

        if (lastHeader >= 0)
            copy[lastHeader] |= 0x80;
        return copy;
    }

    /// <summary>Sample rate, channels and bits per sample from STREAMINFO (the first metadata block).</summary>
    public static (int SampleRate, int Channels, int Bits) Describe(ReadOnlySpan<byte> blocks)
    {
        if (blocks.Length < 4 + 18 || (blocks[0] & 0x7F) != 0)
            return (0, 0, 0);
        var si = blocks[4..];
        var rate = (si[10] << 12) | (si[11] << 4) | (si[12] >> 4);
        var channels = ((si[12] >> 1) & 7) + 1;
        var bits = (((si[12] & 1) << 4) | (si[13] >> 4)) + 1;
        return (rate, channels, bits);
    }

    /// <summary>Block size (samples) of a FLAC frame, or 0 when the header cannot be read.</summary>
    public static int FrameSamples(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6 || frame[0] != 0xFF || (frame[1] & 0xFE) != 0xF8)
            return 0;
        var code = frame[2] >> 4;
        switch (code)
        {
            case 1:
                return 192;
            case >= 2 and <= 5:
                return 576 << (code - 2);
            case >= 8:
                return 256 << (code - 8);
        }

        // 6/7: block size stored after the UTF-8 coded frame/sample number.
        var pos = 4;
        var first = frame[pos];
        var extra = first < 0x80 ? 0 : first >= 0xFE ? 6 : first >= 0xFC ? 5 : first >= 0xF8 ? 4 : first >= 0xF0 ? 3 : first >= 0xE0 ? 2 : 1;
        pos += 1 + extra;
        if (code == 6)
            return pos < frame.Length ? frame[pos] + 1 : 0;
        return pos + 1 < frame.Length ? ((frame[pos] << 8) | frame[pos + 1]) + 1 : 0;
    }
}

/// <summary>MPEG-1/2 audio frame headers (MP1/MP2/MP3).</summary>
public static class MpegAudio
{
    /// <summary>Samples per frame of the MPEG audio frame starting <paramref name="frame"/>, or 0.</summary>
    public static int FrameSamples(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4 || frame[0] != 0xFF || (frame[1] & 0xE0) != 0xE0)
            return 0;
        var version = (frame[1] >> 3) & 3; // 3 = MPEG-1
        var layer = (frame[1] >> 1) & 3; // 3 = Layer I
        return layer switch
        {
            3 => 384,
            2 => 1152,
            1 => version == 3 ? 1152 : 576,
            _ => 0,
        };
    }

    /// <summary>Sample rate and channel count of an MPEG audio frame header.</summary>
    public static (int SampleRate, int Channels) Describe(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4 || frame[0] != 0xFF || (frame[1] & 0xE0) != 0xE0)
            return (0, 0);
        int[] rates = [44100, 48000, 32000, 0];
        var version = (frame[1] >> 3) & 3;
        var rate = rates[(frame[2] >> 2) & 3];
        rate = version switch
        {
            3 => rate,
            2 => rate / 2,
            0 => rate / 4,
            _ => 0,
        };
        return (rate, (frame[3] >> 6) == 3 ? 1 : 2);
    }
}

/// <summary>Durations of audio frames derived from the bitstream.</summary>
public static class AudioFrames
{
    /// <summary>
    /// Duration of <paramref name="sample"/> in samples at the codec's sample rate, or 0 when it cannot be derived
    /// from the bitstream (the container's timing is used instead).
    /// </summary>
    public static int Samples(CodecConfig config, ReadOnlySpan<byte> sample)
    {
        ArgumentNullException.ThrowIfNull(config);
        switch (config.Codec)
        {
            case CodecType.Aac:
            {
                if (config.Extradata is not { Length: >= 2 } asc)
                    return 1024;
                var aac = Aac.ParseConfig(asc);

                // With SBR the output rate (the codec's sample rate) is a multiple of the core rate.
                return aac.SampleRate > 0 && config.SampleRate > aac.SampleRate
                    ? aac.FrameLength * (config.SampleRate / aac.SampleRate)
                    : aac.FrameLength;
            }

            case CodecType.Ac3:
            case CodecType.Eac3:
                return Ac3.Parse(sample)?.Samples ?? 0;
            case CodecType.Dts:
                return Dts.Parse(sample)?.Samples ?? 0;
            case CodecType.Opus:
                return Opus.PacketSamples(sample);
            case CodecType.Flac:
                return Flac.FrameSamples(sample);
            case CodecType.Mp3:
            case CodecType.Mp2:
            case CodecType.Mp1:
                return MpegAudio.FrameSamples(sample);
            default:
                return 0;
        }
    }
}
