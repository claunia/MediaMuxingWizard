using MMW.Core.Resources;

namespace MMW.Core.Media.Codecs;

/// <summary>Header of one AC-3 or E-AC-3 syncframe.</summary>
public sealed record Ac3FrameHeader
{
    public bool IsEac3 { get; init; }

    /// <summary>E-AC-3 stream type: 0 independent, 1 dependent, 2 AC-3 converted (independent).</summary>
    public int StreamType { get; init; }

    public int SubstreamId { get; init; }

    public int FrameSize { get; init; }

    public int Fscod { get; init; }

    public int SampleRate { get; init; }

    public int Bsid { get; init; }

    public int Bsmod { get; init; }

    public int Acmod { get; init; }

    public bool Lfe { get; init; }

    /// <summary>AC-3 frmsizecod (bit rate code is frmsizecod / 2).</summary>
    public int FrmSizeCod { get; init; }

    /// <summary>Audio blocks per syncframe (6 for AC-3).</summary>
    public int Blocks { get; init; } = 6;

    /// <summary>E-AC-3 custom channel map of a dependent substream (16 bits, MSB = Left), or -1.</summary>
    public int ChanMap { get; init; } = -1;

    /// <summary>E-AC-3 flag_ec3_extension_type_a (Joint Object Coding, i.e. Dolby Atmos).</summary>
    public bool JocExtension { get; init; }

    public int JocComplexity { get; init; }

    public int Samples => Blocks * 256;

    public int Channels => Ac3.AcmodChannels(Acmod) + (Lfe ? 1 : 0);
}

/// <summary>AC-3 / E-AC-3 syncframe parsing and dac3/dec3 construction (ETSI TS 102 366).</summary>
public static class Ac3
{
    private static readonly int[] s_rates = [48000, 44100, 32000];
    private static readonly int[] s_reducedRates = [24000, 22050, 16000];
    private static readonly int[] s_bitrates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];

    private static readonly int[][] s_frameWords =
    [
        [64, 64, 80, 80, 96, 96, 112, 112, 128, 128, 160, 160, 192, 192, 224, 224, 256, 256, 320, 320, 384, 384, 448, 448, 512, 512, 640, 640, 768, 768, 896, 896, 1024, 1024, 1152, 1152, 1280, 1280],
        [69, 70, 87, 88, 104, 105, 121, 122, 139, 140, 174, 175, 208, 209, 243, 244, 278, 279, 348, 349, 417, 418, 487, 488, 557, 558, 696, 697, 835, 836, 975, 976, 1114, 1115, 1253, 1254, 1393, 1394],
        [96, 96, 120, 120, 144, 144, 168, 168, 192, 192, 240, 240, 288, 288, 336, 336, 384, 384, 480, 480, 576, 576, 672, 672, 768, 768, 960, 960, 1152, 1152, 1344, 1344, 1536, 1536, 1728, 1728, 1920, 1920],
    ];

    public static int AcmodChannels(int acmod) => acmod switch
    {
        0 => 2,
        1 => 1,
        2 => 2,
        3 => 3,
        4 => 3,
        5 => 4,
        6 => 4,
        _ => 5,
    };

    /// <summary>True when <paramref name="data"/> starts with an AC-3 sync word.</summary>
    public static bool HasSync(ReadOnlySpan<byte> data) => data.Length >= 2 && data[0] == 0x0B && data[1] == 0x77;

    /// <summary>Parses the syncframe at the start of <paramref name="data"/>; null when it is not a valid header.</summary>
    public static Ac3FrameHeader? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || !HasSync(data))
            return null;
        var bsid = data[5] >> 3;
        try
        {
            return bsid <= 10 ? ParseAc3(data, bsid) : bsid <= 16 ? ParseEac3(data) : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static Ac3FrameHeader? ParseAc3(ReadOnlySpan<byte> data, int bsid)
    {
        var r = new BitReader(data[4..]);
        var fscod = (int)r.Read(2);
        var frmsizecod = (int)r.Read(6);
        if (fscod == 3 || frmsizecod > 37)
            return null;
        r.Skip(5); // bsid
        var bsmod = (int)r.Read(3);
        var acmod = (int)r.Read(3);
        if ((acmod & 1) != 0 && acmod != 1)
            r.Skip(2);
        if ((acmod & 4) != 0)
            r.Skip(2);
        if (acmod == 2)
            r.Skip(2);
        var lfe = r.Flag();
        return new Ac3FrameHeader
        {
            IsEac3 = false,
            FrameSize = s_frameWords[fscod][frmsizecod] * 2,
            Fscod = fscod,
            SampleRate = s_rates[fscod] >> Math.Max(0, bsid - 8),
            Bsid = bsid,
            Bsmod = bsmod,
            Acmod = acmod,
            Lfe = lfe,
            FrmSizeCod = frmsizecod,
        };
    }

    private static Ac3FrameHeader? ParseEac3(ReadOnlySpan<byte> data)
    {
        var r = new BitReader(data[2..]);
        var strmtyp = (int)r.Read(2);
        var substreamId = (int)r.Read(3);
        var frmsiz = (int)r.Read(11);
        var fscod = (int)r.Read(2);
        int sampleRate, numblkscod;
        if (fscod == 3)
        {
            var fscod2 = (int)r.Read(2);
            if (fscod2 == 3)
                return null;
            sampleRate = s_reducedRates[fscod2];
            numblkscod = 3;
        }
        else
        {
            sampleRate = s_rates[fscod];
            numblkscod = (int)r.Read(2);
        }

        var blocks = numblkscod == 3 ? 6 : numblkscod + 1;
        var acmod = (int)r.Read(3);
        var lfe = r.Flag();
        var bsid = (int)r.Read(5);
        r.Skip(5); // dialnorm
        if (r.Flag())
            r.Skip(8);
        if (acmod == 0)
        {
            r.Skip(5);
            if (r.Flag())
                r.Skip(8);
        }

        var chanmap = -1;
        if (strmtyp == 1 && r.Flag())
            chanmap = (int)r.Read(16);

        if (r.Flag()) // mixmdate
        {
            if (acmod > 2)
                r.Skip(2);
            if ((acmod & 1) != 0 && acmod > 2)
                r.Skip(6);
            if ((acmod & 4) != 0)
                r.Skip(6);
            if (lfe && r.Flag())
                r.Skip(5);
            if (strmtyp == 0)
            {
                if (r.Flag())
                    r.Skip(6);
                if (acmod == 0 && r.Flag())
                    r.Skip(6);
                if (r.Flag())
                    r.Skip(6);
                var mixdef = (int)r.Read(2);
                if (mixdef == 1)
                    r.Skip(5);
                else if (mixdef == 2)
                    r.Skip(12);
                else if (mixdef == 3)
                    r.Skip(8 * ((int)r.Read(5) + 2));
                if (acmod < 2)
                {
                    if (r.Flag())
                        r.Skip(14);
                    if (acmod == 0 && r.Flag())
                        r.Skip(14);
                }

                if (r.Flag()) // frmmixcfginfoe
                {
                    if (numblkscod == 0)
                    {
                        r.Skip(5);
                    }
                    else
                    {
                        for (var blk = 0; blk < blocks; blk++)
                        {
                            if (r.Flag())
                                r.Skip(5);
                        }
                    }
                }
            }
        }

        var bsmod = 0;
        if (r.Flag()) // infomdate
        {
            bsmod = (int)r.Read(3);
            r.Skip(2); // copyrightb, origbs
            if (acmod == 2)
                r.Skip(4);
            if (acmod >= 6)
                r.Skip(2);
            if (r.Flag())
                r.Skip(8);
            if (acmod == 0 && r.Flag())
                r.Skip(8);
            if (fscod < 3)
                r.Skip(1);
        }

        if (strmtyp == 0 && numblkscod != 3)
            r.Skip(1); // convsync
        if (strmtyp == 2)
        {
            var blkid = numblkscod == 3 || r.Flag();
            if (blkid)
                r.Skip(6);
        }

        bool joc = false;
        var complexity = 0;
        if (r.Flag()) // addbsie
        {
            var addbsil = (int)r.Read(6);
            r.Skip(7);
            joc = r.Flag();
            if (joc && addbsil >= 1)
                complexity = (int)r.Read(8);
        }

        return new Ac3FrameHeader
        {
            IsEac3 = true,
            StreamType = strmtyp,
            SubstreamId = substreamId,
            FrameSize = (frmsiz + 1) * 2,
            Fscod = fscod,
            SampleRate = sampleRate,
            Bsid = bsid,
            Bsmod = bsmod,
            Acmod = acmod,
            Lfe = lfe,
            Blocks = blocks,
            ChanMap = chanmap,
            JocExtension = joc,
            JocComplexity = complexity,
        };
    }

    /// <summary>Builds a dac3 payload (AC3SpecificBox) from an AC-3 syncframe.</summary>
    public static byte[] BuildDac3(Ac3FrameHeader h)
    {
        var w = new BitWriter();
        w.Write((ulong)h.Fscod, 2);
        w.Write((ulong)h.Bsid, 5);
        w.Write((ulong)h.Bsmod, 3);
        w.Write((ulong)h.Acmod, 3);
        w.Flag(h.Lfe);
        w.Write((ulong)(h.FrmSizeCod >> 1), 5);
        w.Write(0, 5);
        return w.ToArray();
    }

    /// <summary>Reads the syncframes of one E-AC-3 access unit (independent substream 0 and what follows it).</summary>
    public static List<Ac3FrameHeader> ParseAccessUnit(ReadOnlySpan<byte> data)
    {
        var frames = new List<Ac3FrameHeader>();
        var pos = 0;
        while (pos + 8 <= data.Length)
        {
            var h = Parse(data[pos..]);
            if (h is null || h.FrameSize <= 0)
                break;
            frames.Add(h);
            pos += h.FrameSize;
        }

        return frames;
    }

    /// <summary>Builds a dec3 payload (EC3SpecificBox) from the syncframes of one access unit.</summary>
    public static byte[] BuildDec3(IReadOnlyList<Ac3FrameHeader> frames)
    {
        var independents = new List<(Ac3FrameHeader Header, int DepCount, int ChanLoc)>();
        long bits = 0;
        foreach (var f in frames)
        {
            bits += (long)f.FrameSize * 8;
            if (!f.IsEac3 || f.StreamType != 1)
            {
                independents.Add((f, 0, 0));
                continue;
            }

            if (independents.Count == 0)
                continue;
            var (parent, count, loc) = independents[^1];
            loc |= f.ChanMap >= 0 ? ChanLocFromChanMap(f.ChanMap) : 0;
            independents[^1] = (parent, count + 1, loc);
        }

        if (independents.Count == 0)
            throw new InvalidDataException(Strings.Error_NoIndependentEac3Substream);

        var first = independents[0].Header;
        var seconds = first.Samples / (double)first.SampleRate;
        var dataRate = (int)Math.Round(bits / seconds / 1000);
        var w = new BitWriter();
        w.Write((ulong)Math.Min(dataRate, 8191), 13);
        w.Write((ulong)(independents.Count - 1), 3);
        foreach (var (h, deps, loc) in independents)
        {
            w.Write((ulong)h.Fscod, 2);
            w.Write((ulong)Math.Min(h.Bsid, 16), 5);
            w.Write(0, 1); // reserved
            w.Write(0, 1); // asvc
            w.Write((ulong)h.Bsmod, 3);
            w.Write((ulong)h.Acmod, 3);
            w.Flag(h.Lfe);
            w.Write(0, 3);
            w.Write((ulong)deps, 4);
            if (deps > 0)
                w.Write((ulong)loc, 9);
            else
                w.Write(0, 1);
        }

        if (first.JocExtension)
        {
            w.Write(0, 7);
            w.Write(1, 1);
            w.Write((ulong)first.JocComplexity, 8);
        }

        return w.ToArray();
    }

    /// <summary>Maps a dependent substream chanmap (Table E.1.4) to the dec3 chan_loc field (Table F.6.1).</summary>
    public static int ChanLocFromChanMap(int chanmap)
    {
        static int Bit(int map, int index) => (map >> (15 - index)) & 1;
        var loc = 0;
        for (var i = 0; i < 8; i++)
            loc |= Bit(chanmap, 5 + i) << (8 - i);
        loc |= Bit(chanmap, 14);
        return loc;
    }

    /// <summary>Total channel count described by a dac3/dec3 payload.</summary>
    public static (int Channels, int SampleRate, bool Atmos) Describe(ReadOnlySpan<byte> config, bool eac3)
    {
        if (!eac3)
        {
            if (config.Length < 3)
                return (0, 0, false);
            var fscod = config[0] >> 6;
            var acmod = (config[1] >> 3) & 7;
            var lfe = (config[1] >> 2) & 1;
            return (AcmodChannels(acmod) + lfe, fscod < 3 ? s_rates[fscod] : 0, false);
        }

        if (config.Length < 5)
            return (0, 0, false);
        var r = new BitReader(config);
        r.Skip(13);
        var numInd = (int)r.Read(3) + 1;
        int channels = 0, rate = 0;
        for (var i = 0; i < numInd; i++)
        {
            var fscod = (int)r.Read(2);
            r.Skip(5 + 1 + 1 + 3);
            var acmod = (int)r.Read(3);
            var lfe = (int)r.Read(1);
            r.Skip(3);
            var deps = (int)r.Read(4);
            var loc = 0;
            if (deps > 0)
                loc = (int)r.Read(9);
            else
                r.Skip(1);
            if (i == 0)
            {
                rate = fscod < 3 ? s_rates[fscod] : 0;
                channels = AcmodChannels(acmod) + lfe + ChanLocChannels(loc);
            }
        }

        var atmos = false;
        if (r.BitsLeft >= 8)
        {
            r.Skip(7);
            atmos = r.Flag();
        }

        return (channels, rate, atmos);
    }

    private static int ChanLocChannels(int loc)
    {
        int[] counts = [2, 2, 1, 1, 2, 2, 2, 1, 1];
        var n = 0;
        for (var i = 0; i < 9; i++)
        {
            if ((loc & (1 << (8 - i))) != 0)
                n += counts[i];
        }

        return n;
    }

    /// <summary>Bit rate in kbit/s of an AC-3 frmsizecod.</summary>
    public static int BitRate(int frmsizecod) => s_bitrates[Math.Clamp(frmsizecod >> 1, 0, s_bitrates.Length - 1)];
}
