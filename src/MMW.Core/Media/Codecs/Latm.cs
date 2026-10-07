using System.Globalization;
using MMW.Core.Resources;

namespace MMW.Core.Media.Codecs;

/// <summary>
/// MPEG-4 audio in LATM (ISO/IEC 14496-3 §1.7.3) inside LOAS AudioSyncStream frames (MPEG-TS stream type 0x11, as DVB
/// uses for AAC and HE-AAC): extracts the AudioSpecificConfig and the raw access units. One program and one layer
/// are supported (what broadcasters use); the configuration persists across frames that reuse it.
/// </summary>
public sealed class LatmParser
{
    private const int SyncWord = 0x2B7;

    private bool _configured;
    private int _audioMuxVersionA;
    private int _numSubFrames;
    private int _frameLengthType;
    private bool _otherDataPresent;
    private long _otherDataLenBits;
    private bool _crcCheckPresent;

    /// <summary>The current AudioSpecificConfig (byte-aligned copy), or null before the first StreamMuxConfig.</summary>
    public byte[]? AudioSpecificConfig { get; private set; }

    /// <summary>The decoded current configuration.</summary>
    public AacConfig? Config { get; private set; }

    /// <summary>Length of the LOAS frame at the start of <paramref name="data"/> (header included), 0 when not a LOAS sync, -1 when truncated.</summary>
    public static int FrameLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
            return -1;
        if (((data[0] << 3) | (data[1] >> 5)) != SyncWord)
            return 0;
        return 3 + (((data[1] & 0x1F) << 8) | data[2]);
    }

    /// <summary>
    /// Parses one LOAS frame (header included) and returns its access units (several when the frame carries sub-frames);
    /// empty when it reuses a configuration not seen yet.
    /// </summary>
    /// <exception cref="InvalidDataException">The frame is malformed or uses an unsupported multiplex.</exception>
    public List<byte[]> Parse(ReadOnlySpan<byte> frame)
    {
        var length = FrameLength(frame);
        if (length <= 0 || length > frame.Length)
            throw new InvalidDataException(Strings.Error_IncompleteLoasFrame);
        try
        {
            var r = new BitReader(frame[3..length]);
            var useSameStreamMux = r.Flag();
            if (!useSameStreamMux)
                ReadStreamMuxConfig(ref r);
            if (!_configured)
                return [];

            var units = new List<byte[]>();
            if (_audioMuxVersionA != 0)
                return units;
            for (var i = 0; i <= _numSubFrames; i++)
            {
                var bytes = ReadPayloadLength(ref r);
                var unit = new byte[bytes];
                for (var b = 0; b < bytes; b++)
                    unit[b] = (byte)r.Read(8); // PayloadMux is not byte aligned
                units.Add(unit);
            }

            return units;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException(Strings.Error_TruncatedLatmMuxElement, ex);
        }
    }

    private int ReadPayloadLength(ref BitReader r)
    {
        if (_frameLengthType != 0)
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_LatmFrameLengthType, _frameLengthType));
        var length = 0;
        int tmp;
        do
        {
            tmp = (int)r.Read(8);
            length += tmp;
        }
        while (tmp == 255);
        return length;
    }

    private void ReadStreamMuxConfig(ref BitReader r)
    {
        var audioMuxVersion = (int)r.Read(1);
        _audioMuxVersionA = audioMuxVersion == 1 ? (int)r.Read(1) : 0;
        if (_audioMuxVersionA != 0)
            throw new InvalidDataException(Strings.Error_LatmMuxVersionA);
        if (audioMuxVersion == 1)
            LatmGetValue(ref r); // taraBufferFullness
        r.Skip(1); // allStreamsSameTimeFraming
        _numSubFrames = (int)r.Read(6);
        var numProgram = (int)r.Read(4);
        var numLayer = (int)r.Read(3);
        if (numProgram != 0 || numLayer != 0)
            throw new InvalidDataException(Strings.Error_LatmSeveralPrograms);

        // The first layer of the first program always carries its AudioSpecificConfig.
        if (audioMuxVersion == 1)
        {
            var ascLength = LatmGetValue(ref r);
            var start = r.Position;
            ReadAudioSpecificConfig(ref r);
            r.Skip(ascLength - (r.Position - start)); // fill bits
        }
        else
        {
            ReadAudioSpecificConfig(ref r);
        }

        _frameLengthType = (int)r.Read(3);
        switch (_frameLengthType)
        {
            case 0:
                r.Skip(8); // latmBufferFullness
                break;
            case 1:
                r.Skip(9); // frameLength
                break;
            default:
                throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_LatmFrameLengthType, _frameLengthType));
        }

        _otherDataPresent = r.Flag();
        _otherDataLenBits = 0;
        if (_otherDataPresent)
        {
            if (audioMuxVersion == 1)
            {
                _otherDataLenBits = LatmGetValue(ref r);
            }
            else
            {
                bool escape;
                do
                {
                    _otherDataLenBits <<= 8;
                    escape = r.Flag();
                    _otherDataLenBits += r.Read(8);
                }
                while (escape);
            }
        }

        _crcCheckPresent = r.Flag();
        if (_crcCheckPresent)
            r.Skip(8);
        _configured = true;
    }

    private static long LatmGetValue(ref BitReader r)
    {
        var bytes = (int)r.Read(2);
        long value = 0;
        for (var i = 0; i <= bytes; i++)
            value = (value << 8) | r.Read(8);
        return value;
    }

    /// <summary>Reads an AudioSpecificConfig in place and keeps a byte-aligned copy of its bits.</summary>
    private void ReadAudioSpecificConfig(ref BitReader r)
    {
        var copy = r;
        var start = r.Position;
        var aot = ReadObjectType(ref r);
        var rateIndex = (int)r.Read(4);
        if (rateIndex == 15)
            r.Skip(24);
        var channels = (int)r.Read(4);
        if (aot is 5 or 29)
        {
            if (r.Read(4) == 15) // extensionSamplingFrequencyIndex
                r.Skip(24);
            aot = ReadObjectType(ref r);
            if (aot == 22)
                r.Skip(4);
        }

        if (aot is 1 or 2 or 3 or 4 or 6 or 7 or 17 or 19 or 20 or 21 or 22 or 23)
        {
            r.Skip(1); // frameLengthFlag
            if (r.Flag()) // dependsOnCoreCoder
                r.Skip(14);
            var extension = r.Flag();
            if (channels == 0)
                throw new InvalidDataException(Strings.Error_LatmProgramConfigElement);
            if (aot is 6 or 20)
                r.Skip(3); // layerNr
            if (extension)
            {
                if (aot == 22)
                    r.Skip(5 + 11);
                if (aot is 17 or 19 or 20 or 23)
                    r.Skip(3);
                r.Skip(1); // extensionFlag3
            }
        }
        else
        {
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_LatmObjectType, aot));
        }

        if (aot is 17 or >= 19 and <= 27 or 39)
            r.Skip(2); // epConfig

        var bits = r.Position - start;
        var w = new BitWriter();
        for (var i = 0L; i < bits; i++)
            w.Write(copy.Read(1), 1);
        AudioSpecificConfig = w.ToArray();
        Config = Aac.ParseConfig(AudioSpecificConfig);
    }

    private static int ReadObjectType(ref BitReader r)
    {
        var aot = (int)r.Read(5);
        return aot == 31 ? 32 + (int)r.Read(6) : aot;
    }
}
