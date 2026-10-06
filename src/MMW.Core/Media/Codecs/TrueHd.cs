using System.Buffers.Binary;

namespace MMW.Core.Media.Codecs;

/// <summary>Information from the start of one Dolby TrueHD (MLP, FBA syntax) access unit.</summary>
public sealed record TrueHdAccessUnit
{
    /// <summary>Length of the whole access unit in bytes (access_unit_length × 2).</summary>
    public int Length { get; init; }

    /// <summary>True when the access unit starts with a major sync (decoder restart point).</summary>
    public bool IsMajorSync { get; init; }

    /// <summary>input_timing: decoder input time in sample periods, modulo 65536.</summary>
    public int InputTiming { get; init; }

    /// <summary>format_info of the major sync (0 for minor syncs).</summary>
    public uint FormatInfo { get; init; }

    /// <summary>peak_data_rate of the major sync (0 for minor syncs).</summary>
    public int PeakDataRate { get; init; }

    /// <summary>Number of substreams (from the major sync, or the last one seen).</summary>
    public int Substreams { get; init; }

    /// <summary>True when the access unit carries the Dolby Atmos (16-channel) presentation.</summary>
    public bool HasAtmos { get; init; }

    /// <summary>Sampling rate in Hz (major syncs only; 0 when unknown).</summary>
    public int SampleRate { get; init; }

    /// <summary>PCM samples per access unit: 40, 80 or 160 depending on the sampling rate.</summary>
    public int SamplesPerAccessUnit { get; init; }
}

/// <summary>
/// Parses Dolby TrueHD access units as described in "Dolby TrueHD (MLP) high-level bitstream description" and
/// builds the <c>dmlp</c> box of "Dolby TrueHD (MLP) bitstreams within the ISO base media file format".
/// </summary>
public static class TrueHd
{
    /// <summary>format_sync of FBA-syntax (Dolby TrueHD) major syncs; the only syntax allowed in ISO media files.</summary>
    public const uint FormatSyncFba = 0xF8726FBA;

    /// <summary>format_sync of FBB-syntax (DVD-Audio MLP) major syncs.</summary>
    public const uint FormatSyncFbb = 0xF8726FBB;

    /// <summary>Size of mlp_sync (check nibble, length, input timing) in bytes.</summary>
    private const int MinorSyncSize = 4;

    /// <summary>Size of major_sync_info without extra channel meaning: format_sync … major_sync_info_CRC.</summary>
    private const int MajorSyncInfoSize = 28;

    /// <summary>Sampling rate for an audio_sampling_frequency code (0 when reserved).</summary>
    public static int SampleRate(int code) => code switch
    {
        0 => 48000,
        1 => 96000,
        2 => 192000,
        8 => 44100,
        9 => 88200,
        10 => 176400,
        _ => 0,
    };

    /// <summary>
    /// Parses the access unit at the start of <paramref name="data"/>.
    /// </summary>
    /// <param name="data">Bytes starting at an access unit (at least its header and substream directory).</param>
    /// <param name="substreams">Substream count of the last major sync (needed to check minor syncs).</param>
    /// <returns>The access unit, or null when the header is invalid (bad length, check nibble or format_sync).</returns>
    public static TrueHdAccessUnit? Parse(ReadOnlySpan<byte> data, int substreams = 0)
    {
        if (data.Length < MinorSyncSize)
            return null;
        var length = (BinaryPrimitives.ReadUInt16BigEndian(data) & 0x0FFF) * 2;
        if (length < MinorSyncSize)
            return null;
        var inputTiming = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);

        var major = data.Length >= MinorSyncSize + MajorSyncInfoSize &&
                    BinaryPrimitives.ReadUInt32BigEndian(data[4..]) is FormatSyncFba;
        var directory = MinorSyncSize;
        uint formatInfo = 0;
        var peak = 0;
        var rate = 0;
        var atmos = false;
        if (major)
        {
            formatInfo = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
            peak = BinaryPrimitives.ReadUInt16BigEndian(data[18..]) & 0x7FFF;
            substreams = data[20] >> 4;
            var substreamInfo = data[21];
            atmos = (substreamInfo & 0x80) != 0;
            rate = SampleRate((int)(formatInfo >> 28));

            // channel_meaning() ends with extra_channel_meaning_present; the optional extension that follows is
            // (extra_channel_meaning_length + 1) 16-bit words long, starting with the 4-bit length itself.
            var extra = 0;
            if ((data[29] & 0x01) != 0)
            {
                if (data.Length < 31)
                    return null;
                extra = ((data[30] >> 4) + 1) * 2;
            }

            directory = MinorSyncSize + MajorSyncInfoSize + extra;
        }
        else if (data.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(data[4..]) == FormatSyncFbb)
        {
            // DVD-Audio MLP (FBB syntax) cannot be stored as Dolby TrueHD.
            return null;
        }

        if (substreams <= 0)
            return null;

        // The check nibble makes the exclusive OR of every nibble of mlp_sync and the substream directory 0xF
        // (the major_sync_info expansion is excluded: it has its own CRC).
        var check = Nibbles(data[..MinorSyncSize]);
        var pos = directory;
        for (var i = 0; i < substreams; i++)
        {
            if (pos + 2 > data.Length || pos + 2 > length)
                return null;
            var word = data.Slice(pos, (data[pos] & 0x80) != 0 ? 4 : 2);
            if (pos + word.Length > data.Length)
                return null;
            check ^= Nibbles(word);
            pos += word.Length;
        }

        if (check != 0xF)
            return null;

        return new TrueHdAccessUnit
        {
            Length = length,
            IsMajorSync = major,
            InputTiming = inputTiming,
            FormatInfo = formatInfo,
            PeakDataRate = peak,
            Substreams = substreams,
            HasAtmos = atmos,
            SampleRate = rate,
            SamplesPerAccessUnit = rate == 0 ? 0 : SamplesPerAccessUnit(rate),
        };
    }

    /// <summary>PCM samples per access unit: 40 at 44.1/48 kHz, 80 at 88.2/96 kHz, 160 at 176.4/192 kHz.</summary>
    public static int SamplesPerAccessUnit(int sampleRate) => sampleRate switch
    {
        <= 48000 => 40,
        <= 96000 => 80,
        _ => 160,
    };

    /// <summary>
    /// Payload of the <c>dmlp</c> (MLPSpecificBox) box: format_info, peak_data_rate (15 bits) and reserved bits,
    /// taken from the first access unit of the track.
    /// </summary>
    public static byte[] BuildDmlp(TrueHdAccessUnit majorSync)
    {
        ArgumentNullException.ThrowIfNull(majorSync);
        if (!majorSync.IsMajorSync)
            throw new ArgumentException("The dmlp box is built from an access unit with a major sync.", nameof(majorSync));
        var box = new byte[10];
        BinaryPrimitives.WriteUInt32BigEndian(box, majorSync.FormatInfo);
        BinaryPrimitives.WriteUInt16BigEndian(box.AsSpan(4), (ushort)(majorSync.PeakDataRate << 1));
        return box;
    }

    /// <summary>Reads format_info and peak_data_rate back from a <c>dmlp</c> payload.</summary>
    public static (uint FormatInfo, int PeakDataRate) ParseDmlp(ReadOnlySpan<byte> payload) =>
        payload.Length < 6 ? (0, 0) : (BinaryPrimitives.ReadUInt32BigEndian(payload), BinaryPrimitives.ReadUInt16BigEndian(payload[4..]) >> 1);

    private static int Nibbles(ReadOnlySpan<byte> bytes)
    {
        var x = 0;
        foreach (var b in bytes)
            x ^= (b >> 4) ^ (b & 0x0F);
        return x;
    }
}
