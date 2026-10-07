using System.Buffers.Binary;

namespace MMW.Core.Media.Codecs;

/// <summary>What an MPEG-H 3D Audio configuration says.</summary>
/// <param name="ProfileLevel">mpegh3daProfileLevelIndication.</param>
/// <param name="SampleRate">Output sample rate.</param>
/// <param name="FrameLength">Output samples per access unit.</param>
/// <param name="Cicp">The reference channel layout (ISO/IEC 23091-3 ChannelConfiguration); 0 when signalled otherwise.</param>
public sealed record MpegHConfig(int ProfileLevel, int SampleRate, int FrameLength, int Cicp);

/// <summary>MPEG-H 3D Audio (ISO/IEC 23008-3): MHAS packets, the configuration and the 'mhm1' sample entry.</summary>
public static class MpegH
{
    public const int PacketConfig = 1;
    public const int PacketFrame = 2;
    public const int PacketSync = 6;
    public const int PacketAudioTruncation = 17;

    /// <summary>
    /// audioTruncationInfo(): the samples to drop from the end (positive) or the start (negative) of the access unit;
    /// 0 when inactive.
    /// </summary>
    public static int Truncation(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
            return 0;
        var r = new BitReader(payload);
        var active = r.Flag();
        r.Skip(1); // reserved
        var fromBegin = r.Flag();
        var samples = (int)r.Read(13);
        return !active ? 0 : fromBegin ? -samples : samples;
    }

    private static readonly int[] s_sampleRates =
    [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350, 0, 0, 57600,
        51200, 40000, 38400, 34150, 28800, 25600, 20000, 19200, 17075, 14400, 12800, 9600,
    ];

    /// <summary>escapedValue(nBits1, nBits2, nBits3) (§5.2).</summary>
    private static long Escaped(ref BitReader r, int bits1, int bits2, int bits3)
    {
        long value = r.Read(bits1);
        if (value == (1L << bits1) - 1)
        {
            long more = r.Read(bits2);
            value += more;
            if (more == (1L << bits2) - 1)
                value += r.Read64(bits3) is var extra ? (long)extra : 0;
        }

        return value;
    }

    /// <summary>
    /// Walks the MHAS packets at the start of <paramref name="data"/>; returns the bytes consumed by whole packets (a
    /// packet cut short at the end is left for later).
    /// </summary>
    public static int ForEachPacket(ReadOnlySpan<byte> data, PacketVisitor visit)
    {
        var at = 0;
        while (at < data.Length)
        {
            int type, headerBytes;
            long length;
            try
            {
                var r = new BitReader(data[at..]);
                type = (int)Escaped(ref r, 3, 8, 8);
                Escaped(ref r, 2, 8, 32); // label
                length = Escaped(ref r, 11, 24, 24);
                headerBytes = (int)((data.Length - at) * 8L - r.BitsLeft + 7) / 8;
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
            {
                break;
            }

            if (length < 0 || at + headerBytes + length > data.Length)
                break;
            visit(type, data.Slice(at, headerBytes + (int)length), data.Slice(at + headerBytes, (int)length));
            at += headerBytes + (int)length;
        }

        return at;
    }

    public delegate void PacketVisitor(int type, ReadOnlySpan<byte> packet, ReadOnlySpan<byte> payload);

    /// <summary>The leading fields of mpegh3daConfig(); null when it is too short.</summary>
    public static MpegHConfig? ParseConfig(ReadOnlySpan<byte> config)
    {
        try
        {
            var r = new BitReader(config);
            var profileLevel = (int)r.Read(8);
            var index = (int)r.Read(5);
            var rate = index == 0x1F ? (int)r.Read(24) : index < s_sampleRates.Length ? s_sampleRates[index] : 0;
            var frameLength = r.Read(3) switch
            {
                0 => 768,
                1 => 1024,
                2 or 3 => 2048,
                4 => 4096,
                _ => 1024,
            };
            r.Skip(2); // cfg_reserved, receiverDelayCompensation
            var cicp = r.Read(2) == 0 ? (int)r.Read(6) : 0; // speakerLayoutType 0: CICPspeakerLayoutIdx
            return new MpegHConfig(profileLevel, rate, frameLength, cicp);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Channels of an ISO/IEC 23091-3 ChannelConfiguration; 0 when unknown.</summary>
    public static int Channels(int cicp) => cicp switch
    {
        1 => 1,
        2 => 2,
        3 => 3,
        4 => 4,
        5 => 5,
        6 => 6,
        7 => 8,
        9 => 3,
        10 => 4,
        11 => 7,
        12 => 8,
        13 => 24,
        14 => 8,
        15 => 12,
        16 => 10,
        17 => 12,
        18 => 14,
        19 => 12,
        20 => 14,
        _ => 0,
    };

    /// <summary>The layout of a ChannelConfiguration ("5.1", "5.1.2" …); empty when unknown.</summary>
    public static string Layout(int cicp) => cicp switch
    {
        1 => "1.0",
        2 => "2.0",
        3 => "3.0",
        4 => "4.0",
        5 => "5.0",
        6 => "5.1",
        7 => "7.1",
        9 => "2.1",
        10 => "2.2",
        11 => "6.1",
        12 => "7.1",
        13 => "22.2",
        14 => "5.1.2",
        15 => "5.2.4",
        16 => "5.1.4",
        17 => "6.1.4",
        18 => "7.1.4 (6.1.4 + LFE2)",
        19 => "7.1.4",
        20 => "9.1.4",
        _ => string.Empty,
    };

    /// <summary>mpegh3daProfileLevelIndication as "Baseline L3", "LC L2" …</summary>
    public static string ProfileLevel(int indication) => indication switch
    {
        >= 0x01 and <= 0x05 => $"Main L{indication}",
        >= 0x06 and <= 0x0A => $"High L{indication - 5}",
        >= 0x0B and <= 0x0F => $"LC L{indication - 10}",
        >= 0x10 and <= 0x14 => $"Baseline L{indication - 15}",
        _ => string.Empty,
    };

    /// <summary>The 'mhaC' payload (MHADecoderConfigurationRecord) of a configuration.</summary>
    public static byte[] BuildMhaC(ReadOnlySpan<byte> config, MpegHConfig parsed)
    {
        var b = new byte[5 + config.Length];
        b[0] = 1; // configurationVersion
        b[1] = (byte)parsed.ProfileLevel;
        b[2] = (byte)parsed.Cicp; // referenceChannelLayout
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(3), (ushort)config.Length);
        config.CopyTo(b.AsSpan(5));
        return b;
    }

    /// <summary>An 'mhm1' sample entry (MHAS packets in the samples) with the 'mhaC' of the first configuration.</summary>
    public static byte[] BuildEntry(ReadOnlySpan<byte> config, MpegHConfig parsed) =>
        QuickTime.AudioEntry("mhm1", 0, parsed.SampleRate, QuickTime.Box("mhaC", BuildMhaC(config, parsed)));

    /// <summary>The configuration of a sample entry's 'mhaC'; null when it has none.</summary>
    public static (byte[] Config, MpegHConfig Parsed)? EntryConfig(ReadOnlySpan<byte> entry)
    {
        if (QuickTime.EntryBox(entry, "mhaC") is not { Length: > 5 } mhaC)
            return null;
        var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(mhaC.AsSpan(3)), mhaC.Length - 5);
        var config = mhaC.AsSpan(5, length).ToArray();
        return ParseConfig(config) is { } parsed ? (config, parsed) : null;
    }
}
