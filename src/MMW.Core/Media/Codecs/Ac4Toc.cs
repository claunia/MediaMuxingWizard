// Dolby AC-4 table of contents (ac4_toc) and substream metadata parser.
//
// Ported from GPAC (https://github.com/gpac/gpac), src/media_tools/av_parsers.c (gf_ac4_parser_bs,
// gf_ac4_raw_frame and the gf_ac4_* helpers), with the data structures of include/gpac/mpeg4_odf.h.
// Copyright (c) Telecom ParisTech / the GPAC authors, licensed under the GNU Lesser General Public License
// version 2.1 or later; relicensed here under the GNU General Public License version 3 or later, as section 3
// of the LGPL permits.
//
// The port keeps GPAC's behaviour, including its quirks (they decide the bytes of the 'dac4' box it writes).
// Paths GPAC never reaches (bitstream_version <= 1, object_info_block with num_obj_info_blocks = 0) are left out.

namespace MMW.Core.Media.Codecs;

/// <summary>GF_AC4SubStreamType.</summary>
internal enum Ac4SubStreamType
{
    None = 0,
    Info = 1,
    InfoChan = 2,
    InfoObj = 3,
    InfoAjoc = 4,
    HsfExt = 5,
    EmdfPayloads = 6,
    PresentationInfo = 7,
    Oamd = 8,
}

/// <summary>GF_AC4BedChannelAssignment.</summary>
internal sealed class Ac4BedChannelAssignment
{
    public byte ChAssignCode;
    public uint BedChanAssignCode;
    public byte NonstdBedChannelAssignmentFlagsPresent;
    public byte ChannelAssignmentFlagsPresent;
    public uint NonstdBedChannelAssignmentFlag;
    public uint StdBedChannelAssignmentFlag;
}

/// <summary>GF_AC4SubStream: one substream_info of the TOC plus what the substream itself tells.</summary>
internal sealed class Ac4SubStream
{
    public const int MaxObjects = 30;

    // Common attributes of the DSI.
    public byte FourBackChannelsPresent;
    public byte CentrePresent;
    public byte TopChannelsPresent;
    public byte DsiSfMultiplier;
    public byte SubstreamBitrateIndicatorPresent;
    public byte SubstreamBitrateIndicator;
    public uint DsiSubstreamChannelGroups;
    public byte Ajoc;
    public byte StaticDmx;
    public byte NDmxObjectsMinus1;
    public byte NUmxObjectsMinus1;
    public byte ContainsBedObjects;
    public byte ContainsDynamicObjects;
    public byte ContainsIsfObjects;

    // Auxiliary information used to parse the frame.
    public uint SubstreamIndex;
    public Ac4SubStreamType Type;
    public byte NObjectsCode;
    public readonly Ac4BedChannelAssignment BedObj = new();
    public readonly Ac4BedChannelAssignment DBedDynObj = new();
    public readonly Ac4BedChannelAssignment UBedDynObj = new();
    public uint ChMode;
    public uint ChannelMode;
    public uint NObjs;
    public readonly uint[] ObjTypes = new uint[MaxObjects];
    public readonly uint[] Lfes = new uint[MaxObjects];
    public readonly uint[] AjocCodeds = new uint[MaxObjects];
    public byte AudioNdot;
    public byte Lfe;
    public uint OtherObjs;
    public byte SusVer;
    public byte DeMethod;
    public byte DeChannelConfig;
    public byte DeDataPresent;
    public byte DialogMaxGain;
    public byte Alternative;
    public byte PresNdot;
    public byte OamdNdot;
    public byte DeiPreventDeProcessing;
    public byte DeiDialogGainCodePresent;
    public byte DeiDialogGainCode;
}

/// <summary>GF_AC4SubStreamGroupV1.</summary>
internal sealed class Ac4SubStreamGroup
{
    public byte SubstreamsPresent;
    public byte HsfExt;
    public byte ChannelCoded;
    public byte ContentTypePresent;
    public byte ContentClassifier;
    public byte LanguageIndicator;
    public byte NLanguageTagBytes;
    public readonly byte[] LanguageTagBytes = new byte[64];
    public byte ImmersiveAudioIndicator;
    public byte ImmersiveAudioIndicatorIms;
    public byte NLfSubstreams;
    public List<Ac4SubStream> Substreams = [];
}

/// <summary>GF_AC4BitrateDsi.</summary>
internal struct Ac4BitrateDsi
{
    public byte BitRateMode;
    public uint BitRate;
    public uint BitRatePrecision;
}

/// <summary>GF_AC4PresentationV1.</summary>
internal sealed class Ac4PresentationV1
{
    public byte PresentationVersion;
    public byte PresentationConfig;
    public byte MdCompat;
    public byte PresentationIdPresent;
    public byte PresentationId;
    public byte DsiFrameRateMultiplyInfo;
    public byte DsiFrameRateFractionInfo;
    public byte PresentationEmdfVersion;
    public ushort PresentationKeyId;
    public byte PresentationChannelCoded;
    public byte DsiPresentationChMode;
    public byte PresB4BackChannelsPresent;
    public byte PresTopChannelPairs;
    public uint PresentationV1ChannelGroups;
    public byte PresentationCoreDiffers;
    public byte PresentationCoreChannelCoded;
    public byte DsiPresentationChannelModeCore;
    public byte PresentationFilter;
    public byte EnablePresentation;
    public byte NFilterBytes;
    public byte MultiPid;
    public byte NSkipBytes;
    public byte PreVirtualized;
    public byte AddEmdfSubstreams;
    public byte NAddEmdfSubstreams;
    public byte[] SubstreamEmdfVersion = new byte[128];
    public ushort[] SubstreamKeyId = new ushort[128];
    public byte PresentationBitrateInfo;
    public Ac4BitrateDsi BitrateDsi;
    public byte Alternative;
    public byte DeIndicator;
    public byte ImmersiveAudioIndicator;
    public byte ExtendedPresentationIdPresent;
    public ushort ExtendedPresentationId;
    public byte NSubstreamGroups;
    public List<Ac4SubStreamGroup> SubstreamGroups = [];

    /// <summary>presentation_substream_info and emdf_payloads_substream_info of the presentation; null until needed.</summary>
    public List<Ac4SubStream>? Substreams;

    // Auxiliary information, not in the DSI.
    public List<uint> SubstreamGroupIndexes = [];
    public byte PresBCentrePresent; // never set by GPAC (stays 0)
    public uint NSubstreamsInPresentation;
    public byte AdditionalData;

    public Ac4PresentationV1 Clone() => (Ac4PresentationV1)MemberwiseClone();
}

/// <summary>GF_AC4StreamInfo plus the GF_AC4Config fields filled by the parser.</summary>
internal sealed class Ac4StreamInfo
{
    public byte Ac4DsiVersion;
    public byte BitstreamVersion;
    public byte FsIndex;
    public byte FrameRateIndex;
    public byte IFrameGlobal;
    public byte ProgramIdPresent;
    public ushort ShortProgramId;
    public byte Uuid;
    public readonly byte[] ProgramUuid = new byte[16];
    public Ac4BitrateDsi BitrateDsi;
    public ushort NPresentations;
    public List<Ac4PresentationV1>? Presentations;

    // GF_AC4Config
    public uint SampleRate;
    public uint ChannelCount;
    public uint SampleDuration;
    public uint MediaTimeScale;
    public uint TocSize;
}

/// <summary>
/// Bit reader with GPAC's GF_BitStream read semantics: reading past the end yields zeros (and flags an overflow),
/// the byte position counts a partly read byte, and seeking past the end is ignored.
/// </summary>
internal sealed class GpacBitReader
{
    private readonly byte[] _data;
    private long _position;
    private int _nbBits = 8;
    private byte _current;

    public GpacBitReader(byte[] data) => _data = data;

    public bool Overflow { get; private set; }

    /// <summary>gf_bs_get_position: bytes consumed, a partly read byte included.</summary>
    public long Position => _position;

    /// <summary>gf_bs_available.</summary>
    public long Available => _data.Length < _position ? 0 : _data.Length - _position;

    /// <summary>gf_bs_get_bit_offset.</summary>
    public long BitOffset => (_position - 1) * 8 + _nbBits;

    private uint ReadBit()
    {
        if (_nbBits == 8)
        {
            if (_position >= _data.Length)
            {
                Overflow = true;
                _current = 0;
            }
            else
            {
                _current = _data[_position++];
            }

            _nbBits = 0;
        }

        var bit = (uint)((_current >> (7 - _nbBits)) & 1);
        _nbBits++;
        return bit;
    }

    /// <summary>gf_bs_read_int: up to 32 bits, MSB first.</summary>
    public uint Read(int count)
    {
        uint v = 0;
        for (var i = 0; i < count; i++)
            v = (v << 1) | ReadBit();
        return v;
    }

    public byte ReadByteValue(int count) => (byte)Read(count);

    /// <summary>gf_bs_align.</summary>
    public void Align()
    {
        if (_nbBits != 8)
            Read(8 - _nbBits);
    }

    /// <summary>gf_bs_seek.</summary>
    public void Seek(long offset)
    {
        if (offset > _data.Length)
            return;
        _position = offset;
        _nbBits = 8;
        _current = 0;
    }

    /// <summary>gf_ac4_variable_bits (variable_bits() of ETSI TS 103 190-1 §4.2.2).</summary>
    public uint VariableBits(int bits)
    {
        uint value = 0;
        if (Available == 0)
            return value;
        uint more;
        do
        {
            value += Read(bits);
            more = Read(1);
            if (more == 1)
            {
                value <<= bits;
                value += 1u << bits;
            }
        } while (more == 1 && Available > 0);

        return value;
    }
}

/// <summary>The ac4_toc() parser (ETSI TS 103 190-2 §6.2.1) as GPAC implements it.</summary>
internal static class Ac4Toc
{
    // ch_mode - TS 103 190-2 table 78
    private const uint ChModeMono = 0;
    private const uint ChModeStereo = 1;
    private const uint ChMode30 = 2;
    private const uint ChMode50 = 3;
    private const uint ChMode51 = 4;
    private const uint ChMode70_34 = 5;
    private const uint ChMode71_34 = 6;
    private const uint ChMode70_52 = 7;
    private const uint ChMode71_52 = 8;
    private const uint ChMode70_322 = 9;
    private const uint ChMode71_322 = 10;
    private const uint ChMode704 = 11;
    private const uint ChMode714 = 12;
    private const uint ChMode904 = 13;
    private const uint ChMode914 = 14;
    private const uint ChMode222 = 15;
    private const uint ChModeReserved = 16;

    private const uint ObjTypeBed = 1;
    private const uint ObjTypeDyn = 2;
    private const uint ObjTypeIsf = 3;

    /// <summary>Speaker group index mask by ch_mode (TS 103 190-2 A.27).</summary>
    internal static readonly uint[] SpeakerGroupIndexMaskByChMode =
    [
        2, 1, 3, 7, 71, 15, 79, 131079, 131143, 262151, 262215, 63, 127, 65599, 65663, 196479, 0,
    ];

    private static readonly byte[,] SuperSetChMode =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        { 1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        { 2, 2, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        { 3, 3, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        { 4, 4, 4, 4, 4, 6, 6, 8, 8, 10, 10, 12, 12, 14, 14, 15 },
        { 5, 5, 5, 5, 6, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        { 6, 6, 6, 6, 6, 6, 6, 6, 8, 6, 10, 12, 12, 14, 14, 15 },
        { 7, 7, 7, 7, 8, 7, 6, 7, 8, 9, 10, 12, 12, 13, 14, 15 },
        { 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 10, 11, 12, 14, 14, 15 },
        { 9, 9, 9, 9, 10, 9, 10, 9, 9, 9, 10, 11, 12, 13, 14, 15 },
        { 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 12, 13, 14, 15 },
        { 11, 11, 11, 11, 12, 11, 12, 11, 12, 11, 12, 11, 13, 13, 14, 15 },
        { 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 13, 14, 15 },
        { 13, 13, 13, 13, 14, 13, 14, 13, 14, 13, 14, 13, 14, 13, 14, 15 },
        { 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 15 },
        { 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15 },
    };

    internal static readonly uint[] SamplingFrequency = [44100, 48000];

    /// <summary>Sample deltas by frame_rate_index at 48 kHz (ETSI TS 103 190-1 Table E.1), in media time scale units.</summary>
    internal static readonly uint[] SampleDelta48 = [2002, 2000, 1920, 8008, 1600, 1001, 1000, 960, 4004, 800, 480, 2002, 400, 2048];

    internal static readonly uint[] SampleDelta441 = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2048];

    internal static readonly uint[] MediaTimeScale48 =
        [48000, 48000, 48000, 240000, 48000, 48000, 48000, 48000, 240000, 48000, 48000, 240000, 48000, 48000];

    internal static readonly uint[] MediaTimeScale441 = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 44100];

    /// <summary>
    /// gf_ac4_parser_bs with start_from_toc and full_parse: parses a raw_ac4_frame. Null when GPAC would fail
    /// (bitstream_version &lt;= 1, inconsistent TOC, unknown frame rate).
    /// </summary>
    public static Ac4StreamInfo? Parse(byte[] rawFrame)
    {
        var bs = new GpacBitReader(rawFrame);
        var stream = new Ac4StreamInfo();
        if (!RawFrame(bs, stream))
            return null;
        if (stream.BitstreamVersion <= 1)
            return null;

        stream.Ac4DsiVersion = 1;
        if (stream.FsIndex >= SamplingFrequency.Length)
            return null;
        stream.SampleRate = SamplingFrequency[stream.FsIndex];
        if (stream.FsIndex == 0)
        {
            if (stream.FrameRateIndex >= SampleDelta441.Length)
                return null;
            stream.SampleDuration = SampleDelta441[stream.FrameRateIndex];
            stream.MediaTimeScale = MediaTimeScale441[stream.FrameRateIndex];
        }
        else
        {
            if (stream.FrameRateIndex >= SampleDelta48.Length)
                return null;
            stream.SampleDuration = SampleDelta48[stream.FrameRateIndex];
            stream.MediaTimeScale = MediaTimeScale48[stream.FrameRateIndex];
        }

        return stream;
    }

    private static uint ReadSubstreamIndex(GpacBitReader bs)
    {
        var index = bs.Read(2);
        if (index == 3)
            index += bs.VariableBits(2);
        return index;
    }

    private static void FrameRateMultiplyInfo(GpacBitReader bs, Ac4PresentationV1 p, uint frameRateIndex)
    {
        switch (frameRateIndex)
        {
            case 2:
            case 3:
            case 4:
                if (bs.Read(1) != 0)
                    p.DsiFrameRateMultiplyInfo = (byte)(bs.Read(1) == 0 ? 1 : 2);
                else
                    p.DsiFrameRateMultiplyInfo = 0;
                break;
            case 0:
            case 1:
            case 7:
            case 8:
            case 9:
                p.DsiFrameRateMultiplyInfo = (byte)(bs.Read(1) != 0 ? 1 : 0);
                break;
            default:
                p.DsiFrameRateMultiplyInfo = 0;
                break;
        }
    }

    private static void FrameRateFractionsInfo(GpacBitReader bs, Ac4PresentationV1 p, uint frameRateIndex)
    {
        p.DsiFrameRateFractionInfo = 0;
        switch (frameRateIndex)
        {
            case 5:
            case 6:
            case 7:
            case 8:
            case 9:
                if (bs.Read(1) != 0)
                    p.DsiFrameRateFractionInfo = 1;
                break;
            case 10:
            case 11:
            case 12:
                if (bs.Read(1) != 0)
                    p.DsiFrameRateFractionInfo = (byte)(bs.Read(1) == 1 ? 2 : 1);
                break;
        }
    }

    private static void EmdfReserved(GpacBitReader bs)
    {
        var primary = bs.Read(2);
        var secondary = bs.Read(2);
        uint skip = 0;
        if (primary > 0)
            skip += 1u << (int)(2 * (primary - 1));
        if (secondary > 0)
            skip += 1u << (int)(2 * (secondary - 1));
        for (uint i = 0; i < skip; i++)
            bs.Read(8);
    }

    private static void EmdfInfo(GpacBitReader bs, List<Ac4SubStream> substreams, out uint emdfVersion, out uint keyId)
    {
        emdfVersion = bs.Read(2);
        if (emdfVersion == 3)
            emdfVersion += bs.VariableBits(2);
        keyId = bs.Read(3);
        if (keyId == 7)
            keyId += bs.VariableBits(3);

        if (bs.Read(1) != 0)
        {
            var substream = new Ac4SubStream { Type = Ac4SubStreamType.EmdfPayloads };
            substream.SubstreamIndex = ReadSubstreamIndex(bs);
            substreams.Add(substream);
        }

        EmdfReserved(bs);
    }

    private static uint GetChannelMode(GpacBitReader bs, Ac4SubStream s, uint presentationVersion, ref byte immersiveIms)
    {
        // ETSI TS 103 190-2 Table 56
        s.ChannelMode = bs.Read(1);
        if (s.ChannelMode == 0)
            return ChModeMono;
        s.ChannelMode = (s.ChannelMode << 1) | bs.Read(1);
        if (s.ChannelMode == 2)
            return ChModeStereo;
        s.ChannelMode = (s.ChannelMode << 2) | bs.Read(2);
        switch (s.ChannelMode)
        {
            case 12:
                return ChMode30;
            case 13:
                return ChMode50;
            case 14:
                return ChMode51;
        }

        s.ChannelMode = (s.ChannelMode << 3) | bs.Read(3);
        switch (s.ChannelMode)
        {
            case 120:
                // Dolby AC-4 in MPEG-DASH for Broadcast Services Specification 2.5.3: IMS signalling
                if (presentationVersion == 2)
                {
                    immersiveIms = 0;
                    return ChModeStereo;
                }

                return ChMode70_34;
            case 121:
                if (presentationVersion == 2)
                {
                    immersiveIms |= 1;
                    return ChModeStereo;
                }

                return ChMode71_34;
            case 122:
                return ChMode70_52;
            case 123:
                return ChMode71_52;
            case 124:
                return ChMode70_322;
            case 125:
                return ChMode71_322;
        }

        s.ChannelMode = (s.ChannelMode << 1) | bs.Read(1);
        switch (s.ChannelMode)
        {
            case 252:
                return ChMode704;
            case 253:
                return ChMode714;
        }

        s.ChannelMode = (s.ChannelMode << 1) | bs.Read(1);
        switch (s.ChannelMode)
        {
            case 508:
                return ChMode904;
            case 509:
                return ChMode914;
            case 510:
                return ChMode222;
            default:
                bs.VariableBits(2);
                return ChModeReserved;
        }
    }

    private static void DsiSfMultiplier(GpacBitReader bs, Ac4SubStream s, uint fsIndex)
    {
        if (fsIndex != 1)
            return;
        s.DsiSfMultiplier = bs.Read(1) != 0 ? (byte)(bs.Read(1) + 1) : (byte)0;
    }

    private static void BitrateIndicator(GpacBitReader bs, Ac4SubStream s)
    {
        s.SubstreamBitrateIndicator = bs.ReadByteValue(3);
        if ((s.SubstreamBitrateIndicator & 1) == 1)
            s.SubstreamBitrateIndicator = (byte)((s.SubstreamBitrateIndicator << 2) + bs.Read(2));
    }

    private static void SubstreamInfoChan(GpacBitReader bs, Ac4SubStream s, uint presentationVersion, bool defaultPresentation,
        uint fsIndex, ref uint speakerIndexMask, uint frameRateFactor, byte substreamsPresent, ref byte immersiveIms)
    {
        s.ChMode = GetChannelMode(bs, s, presentationVersion, ref immersiveIms);

        // ETSI TS 103 190-2 E.10.3
        var mask = SpeakerGroupIndexMaskByChMode[s.ChMode];
        if (s.ChMode is ChMode704 or ChMode714 or ChMode904 or ChMode914)
        {
            s.FourBackChannelsPresent = bs.ReadByteValue(1);
            s.CentrePresent = bs.ReadByteValue(1);
            s.TopChannelsPresent = bs.ReadByteValue(2);
            if (s.FourBackChannelsPresent == 0)
                mask &= ~0x8u;
            if (s.CentrePresent == 0)
                mask &= ~0x2u;
            switch (s.TopChannelsPresent)
            {
                case 0:
                    mask &= ~0x30u;
                    break;
                case 1:
                case 2:
                    mask &= ~0x30u;
                    mask |= 0x80;
                    break;
            }
        }

        // ETSI TS 103 190-2 E.11.2
        s.DsiSubstreamChannelGroups = mask;
        if (defaultPresentation)
            speakerIndexMask |= mask;

        DsiSfMultiplier(bs, s, fsIndex);

        s.SubstreamBitrateIndicatorPresent = bs.ReadByteValue(1);
        if (s.SubstreamBitrateIndicatorPresent != 0)
            BitrateIndicator(bs, s);

        if (s.ChMode is ChMode70_52 or ChMode71_52 or ChMode70_322 or ChMode71_322)
            bs.Read(1); // add_ch_base
        for (uint i = 0; i < frameRateFactor; i++)
            s.AudioNdot = bs.ReadByteValue(1);

        if (substreamsPresent == 1)
            s.SubstreamIndex = ReadSubstreamIndex(bs);
        s.Type = Ac4SubStreamType.InfoChan;
    }

    private static void HsfExtSubstreamInfo(GpacBitReader bs, Ac4SubStream s, byte substreamsPresent)
    {
        s.Type = Ac4SubStreamType.HsfExt;
        if (substreamsPresent == 1)
            s.SubstreamIndex = ReadSubstreamIndex(bs);
    }

    private static void OamdSubstreamInfo(GpacBitReader bs, Ac4SubStream s, byte substreamsPresent)
    {
        s.Type = Ac4SubStreamType.Oamd;
        s.OamdNdot = bs.ReadByteValue(1);
        if (substreamsPresent == 1)
            s.SubstreamIndex = ReadSubstreamIndex(bs);
    }

    /// <summary>Appends an object to the substream; false when GF_AC4_MAX_NUM_OBJECTS is reached.</summary>
    private static bool AddObject(Ac4SubStream s, uint type, uint lfe, uint ajocCoded)
    {
        if (s.NObjs >= Ac4SubStream.MaxObjects)
            return false;
        s.ObjTypes[s.NObjs] = type;
        s.Lfes[s.NObjs] = lfe;
        s.AjocCodeds[s.NObjs] = ajocCoded;
        s.NObjs++;
        return true;
    }

    private static readonly uint[] IsfConfigToNumObjects = [4, 8, 10, 14, 15, 30];
    private static readonly uint[] StdBedFlagToNumObjects = [2, 1, 1, 2, 2, 2, 2, 2, 2, 1];

    private static int CeilLog2(uint n)
    {
        var bits = 0;
        while ((1UL << bits) < n)
            bits++;
        return bits;
    }

    private static bool BedDynObjAssignment(GpacBitReader bs, Ac4SubStream s, Ac4BedChannelAssignment a, uint nSignals, bool isUpmix)
    {
        uint[] bedChanAssignCodeToNumObjects = [2, 3, 5, 7, 9, 7, 9, 11];

        if (bs.Read(1) == 0) // b_dyn_objects_only
        {
            if (bs.Read(1) != 0) // b_isf
            {
                var isfConfig = bs.Read(3);
                if (isfConfig >= IsfConfigToNumObjects.Length)
                    return false;
                for (uint i = 0; i < IsfConfigToNumObjects[isfConfig]; i++)
                {
                    if (!AddObject(s, ObjTypeIsf, 0, 1))
                        return false;
                }

                if (isUpmix)
                {
                    s.ContainsIsfObjects |= 1;
                    if (nSignals > s.NObjs)
                        s.ContainsDynamicObjects |= 1;
                }
            }
            else
            {
                a.ChAssignCode = bs.ReadByteValue(1);
                if (a.ChAssignCode != 0)
                {
                    a.BedChanAssignCode = bs.Read(3);
                    for (uint i = 0; i < bedChanAssignCodeToNumObjects[a.BedChanAssignCode]; i++)
                    {
                        if (!AddObject(s, ObjTypeBed, 0, 1))
                            return false;
                    }

                    if (isUpmix)
                    {
                        s.ContainsBedObjects |= 1;
                        if (nSignals > s.NObjs)
                            s.ContainsDynamicObjects |= 1;
                    }
                }
                else
                {
                    a.ChannelAssignmentFlagsPresent = bs.ReadByteValue(1);
                    if (a.ChannelAssignmentFlagsPresent != 0)
                    {
                        a.NonstdBedChannelAssignmentFlagsPresent = bs.ReadByteValue(1);
                        if (a.NonstdBedChannelAssignmentFlagsPresent != 0)
                        {
                            a.NonstdBedChannelAssignmentFlag = bs.Read(17);
                            for (var i = 0; i < 17; i++)
                            {
                                if (((a.NonstdBedChannelAssignmentFlag >> i) & 1) != 0 && i != 3 && i != 16 && !AddObject(s, ObjTypeBed, 0, 1))
                                    return false;
                            }
                        }
                        else
                        {
                            a.StdBedChannelAssignmentFlag = bs.Read(10);
                            for (var i = 0; i < 10; i++)
                            {
                                if (((a.StdBedChannelAssignmentFlag >> i) & 1) == 0 || i == 2 || i == 9)
                                    continue;
                                for (uint j = 0; j < StdBedFlagToNumObjects[i]; j++)
                                {
                                    if (!AddObject(s, ObjTypeBed, 0, 1))
                                        return false;
                                }
                            }
                        }

                        if (isUpmix)
                        {
                            if (s.NObjs > 0)
                                s.ContainsBedObjects |= 1;
                            if (nSignals > s.NObjs)
                                s.ContainsDynamicObjects |= 1;
                        }
                    }
                    else
                    {
                        uint nBedSignals = nSignals > 1 ? bs.Read(CeilLog2(nSignals)) + 1 : 1;
                        a.NonstdBedChannelAssignmentFlag = 0;
                        for (uint b = 0; b < nBedSignals; b++)
                        {
                            var tmp = bs.ReadByteValue(4);
                            a.ChannelAssignmentFlagsPresent = 1;
                            a.NonstdBedChannelAssignmentFlag |= 1u << tmp;
                            if (tmp != 3 && !AddObject(s, ObjTypeBed, 0, 1))
                                return false;
                        }

                        a.NonstdBedChannelAssignmentFlagsPresent = 1;
                        if (isUpmix)
                        {
                            s.ContainsBedObjects |= 1;
                            if (nSignals > nBedSignals)
                                s.ContainsDynamicObjects |= 1;
                        }
                    }
                }
            }
        }
        else if (isUpmix)
        {
            // ETSI TS 103 190-2 6.3.2.10.3: only dynamic objects
            s.ContainsDynamicObjects |= 1;
        }

        return true;
    }

    private static void Trim(GpacBitReader bs)
    {
        if (bs.Read(1) == 0) // b_trim_present
            return;
        bs.Read(2); // warp_mode
        bs.Read(2); // reserved
        if (bs.Read(2) != 2) // global_trim_mode
            return;
        for (var i = 0; i < 9; i++)
        {
            if (bs.Read(1) != 0 || bs.Read(1) != 0) // b_default_trim, b_disable_trim
                continue;
            var p0 = bs.Read(1);
            var p1 = bs.Read(1);
            var p2 = bs.Read(1);
            var p3 = bs.Read(1);
            var p4 = bs.Read(1);
            if (p4 != 0)
                bs.Read(4); // trim_centre
            if (p3 != 0)
                bs.Read(4); // trim_surround
            if (p2 != 0)
                bs.Read(4); // trim_height
            if (p1 != 0)
                bs.Read(5); // bal3D_Y_sign_tb_code, bal3D_Y_amount_tb
            if (p0 != 0)
                bs.Read(5); // bal3D_Y_sign_lis_code, bal3D_Y_amount_lis
        }
    }

    /// <summary>tool_*_to_f_s_b: a front flag, else a side flag, then a 3-bit gain.</summary>
    private static void ToolToFsb(GpacBitReader bs)
    {
        if (bs.Read(1) == 0) // b_*_to_front
            bs.Read(1); // b_*_to_side
        bs.Read(3); // gain code
    }

    /// <summary>tool_*_to_f_s: a front flag then a 3-bit gain.</summary>
    private static void ToolToFs(GpacBitReader bs)
    {
        bs.Read(1);
        bs.Read(3);
    }

    private static void StereoDmxCoeff(GpacBitReader bs)
    {
        bs.Read(3); // loro_centre_mixgain
        bs.Read(3); // loro_surround_mixgain
        if (bs.Read(1) != 0) // b_ltrt_mixinfo
            bs.Read(6);
        if (bs.Read(1) != 0) // b_lfe_mixinfo
            bs.Read(5);
        bs.Read(2); // preferred_dmx_method
    }

    private static void BedRenderInfo(GpacBitReader bs)
    {
        // ETSI TS 103 190-2 6.2.8.8
        if (bs.Read(1) == 0) // b_bed_render_info
            return;
        if (bs.Read(1) != 0) // b_stereo_dmx_coeff
            StereoDmxCoeff(bs);
        if (bs.Read(1) == 0) // b_cdmx_data_present
            return;
        if (bs.Read(1) != 0) // b_cdmx_w_to_f
            bs.Read(3);
        if (bs.Read(1) != 0) // b_cdmx_b4_to_b2
            bs.Read(3);
        if (bs.Read(1) != 0) // b_tm_ch_present
        {
            if (bs.Read(1) != 0)
                ToolToFsb(bs);
            if (bs.Read(1) != 0)
                ToolToFs(bs);
        }

        var tb = bs.Read(1);
        if (tb != 0)
        {
            if (bs.Read(1) != 0)
                ToolToFsb(bs);
            if (bs.Read(1) != 0)
                ToolToFs(bs);
        }

        var tf = bs.Read(1);
        if (tf != 0)
        {
            if (bs.Read(1) != 0)
                ToolToFsb(bs);
            if (bs.Read(1) != 0)
                ToolToFs(bs);
        }

        if ((tb != 0 || tf != 0) && bs.Read(1) != 0) // b_cdmx_tfb_to_tm
            bs.Read(3);
    }

    private static void Headphone(GpacBitReader bs)
    {
        if (bs.Read(1) == 0)
            return;
        var mode = bs.Read(3);
        if (mode is 1 or 2)
            bs.Read(1); // b_head_track_disable_all
    }

    private static void OamdCommonData(GpacBitReader bs)
    {
        if (bs.Read(1) == 0) // b_default_screen_size_ratio
            bs.Read(5);
        bs.Read(1); // b_bed_object_chan_distribute
        if (bs.Read(1) == 0) // b_additional_data
            return;
        var addDataBytes = bs.Read(1) + 1;
        if (addDataBytes == 2)
            addDataBytes += bs.VariableBits(2);

        var pos = bs.BitOffset;
        Trim(bs);
        var used = (uint)(bs.BitOffset - pos);
        if (used < addDataBytes * 8)
            BedRenderInfo(bs);
        used = (uint)(bs.BitOffset - pos);
        if (used < addDataBytes * 8)
            Headphone(bs);
        used = (uint)(bs.BitOffset - pos);

        var bitsToRead = Math.Min((uint)(8 * bs.Available), unchecked(addDataBytes * 8 - used));
        bs.Read((int)Math.Min(32u, bitsToRead));
    }

    private static uint ConvertMaskToChannels(uint mask)
    {
        mask &= 0x7FFFFFF7u; // INT_MAX ^ 0x8: without the LFE
        return (uint)System.Numerics.BitOperations.PopCount(mask);
    }

    // bed_chan_assign_code to channel mask (bit 0-16: L R C LFE Ls Rs Lb Rb Tfl Tfr Tsl Tsr Tbl Tbr Lw Rw LFE2), AC-4 Table 62
    private static readonly uint[] BedChanAssignMaskAjoc = [0x3, 0x7, 0x37, 0xC37, 0x3337, 0xF7, 0xCF7, 0x33F7];
    private static readonly uint[] BedChanAssignMask = [0x3, 0x7, 0x3F, 0xC3F, 0x333F, 0xFF, 0xCFF, 0x33FF];

    // std_bed_channel_assignment_flag bit to channel mask, AC-4 Table 65
    private static readonly uint[] StdBedFlagMask = [1u << 16, 0xC000, 0x3000, 0xC00, 0x300, 0xC0, 0x30, 0x8, 0x4, 0x3];

    private static uint GetObjChannelMask(Ac4SubStream s)
    {
        uint mask = 0;
        var a = s.Ajoc != 0 ? s.UBedDynObj : s.BedObj;
        if (a.ChAssignCode != 0 && a.BedChanAssignCode < 8)
            mask |= s.Ajoc != 0 ? BedChanAssignMaskAjoc[a.BedChanAssignCode] : BedChanAssignMask[a.BedChanAssignCode];

        if (a.NonstdBedChannelAssignmentFlagsPresent != 0)
        {
            // AC-4 Table 64: flag bit 16 - k is channel k
            for (var k = 0; k <= 16; k++)
            {
                if ((a.NonstdBedChannelAssignmentFlag & (1u << (16 - k))) != 0)
                    mask |= 1u << k;
            }
        }

        if (a.ChannelAssignmentFlagsPresent != 0)
        {
            for (var i = 0; i < 10; i++)
            {
                if ((a.StdBedChannelAssignmentFlag & (1u << i)) != 0)
                    mask |= StdBedFlagMask[i];
            }
        }

        return mask;
    }

    private static void SubstreamInfoAjoc(GpacBitReader bs, Ac4SubStream s, ref uint channelCount, bool defaultPresentation,
        uint fsIndex, uint frameRateFactor, byte substreamsPresent)
    {
        s.Lfe = bs.ReadByteValue(1);
        s.StaticDmx = bs.ReadByteValue(1);
        if (s.StaticDmx != 0)
        {
            if (defaultPresentation)
                channelCount += 5;
        }
        else
        {
            // ETSI TS 103 190-2 E.11.2 n_dmx_objects_minus1 = n_fullband_dmx_signals_minus1
            s.NDmxObjectsMinus1 = bs.ReadByteValue(4);
            uint nDmx = s.NDmxObjectsMinus1 + 1u;
            BedDynObjAssignment(bs, s, s.DBedDynObj, nDmx, false);
            if (defaultPresentation)
                channelCount += nDmx;
        }

        if (bs.Read(1) != 0) // b_oamd_common_data_present
            OamdCommonData(bs);

        var nUmx = bs.Read(4) + 1;
        if (nUmx == 16)
            nUmx += bs.VariableBits(3);
        s.NUmxObjectsMinus1 = (byte)(nUmx - 1);

        BedDynObjAssignment(bs, s, s.UBedDynObj, nUmx, true);

        var objMask = GetObjChannelMask(s);
        s.OtherObjs = unchecked(nUmx - ConvertMaskToChannels(objMask));

        if (fsIndex == 1 && bs.Read(1) != 0) // b_sf_multiplier
            bs.Read(1);

        s.SubstreamBitrateIndicatorPresent = bs.ReadByteValue(1);
        if (s.SubstreamBitrateIndicatorPresent != 0)
            BitrateIndicator(bs, s);

        for (uint i = 0; i < frameRateFactor; i++)
            s.AudioNdot = bs.ReadByteValue(1);

        if (substreamsPresent == 1)
            s.SubstreamIndex = ReadSubstreamIndex(bs);
        s.Type = Ac4SubStreamType.InfoAjoc;
        s.SusVer = 1;
    }

    private static bool SubstreamInfoObj(GpacBitReader bs, Ac4SubStream s, ref uint channelCount, bool defaultPresentation,
        uint fsIndex, uint frameRateFactor, byte substreamsPresent)
    {
        uint[] nObjectsCodeToNumObjects = [0, 1, 2, 3, 5, 7];
        uint[] bedChanAssignCodeToNumObjects = [2, 3, 6, 8, 10, 8, 10, 12];

        s.NObjectsCode = bs.ReadByteValue(3);
        uint nSignals = s.NObjectsCode switch
        {
            <= 3 => (uint)s.NObjectsCode,
            4 => 5u,
            _ => 0u,
        };

        // ETSI TS 103 190-2 Table 60: the default presentation gives the channel count
        if (defaultPresentation)
        {
            if (s.NObjectsCode <= 3)
                channelCount += s.NObjectsCode;
            else if (s.NObjectsCode == 4)
                channelCount += 5;
        }

        if (bs.Read(1) != 0) // b_dynamic_objects
        {
            s.Lfe = bs.ReadByteValue(1);
            nSignals += s.Lfe;
            if (s.NObjectsCode >= nObjectsCodeToNumObjects.Length)
                return false;
            for (uint i = 0; i < nObjectsCodeToNumObjects[s.NObjectsCode]; i++)
            {
                var ok = s.Lfe != 0 && i == 0 ? AddObject(s, ObjTypeBed, 1, 0) : AddObject(s, ObjTypeDyn, 0, 0);
                if (!ok)
                    return false;
            }

            s.ContainsDynamicObjects |= 1;
            if (defaultPresentation && s.Lfe != 0)
                channelCount += 1;
            if (s.NObjectsCode != 0 || s.Lfe == 0)
                s.OtherObjs = 1;
        }
        else if (bs.Read(1) != 0) // b_bed_objects
        {
            if (bs.Read(1) != 0) // b_bed_start
            {
                s.BedObj.ChAssignCode = bs.ReadByteValue(1);
                if (s.BedObj.ChAssignCode != 0)
                {
                    s.BedObj.BedChanAssignCode = bs.Read(3);
                    var count = bedChanAssignCodeToNumObjects[s.BedObj.BedChanAssignCode];
                    for (uint i = 0; i < count; i++)
                    {
                        if (!AddObject(s, ObjTypeBed, i == 3 ? 1u : 0u, 0))
                            return false;
                    }

                    if (nSignals > count)
                        s.ContainsDynamicObjects |= 1;
                }
                else
                {
                    s.BedObj.NonstdBedChannelAssignmentFlagsPresent = bs.ReadByteValue(1);
                    if (s.BedObj.NonstdBedChannelAssignmentFlagsPresent != 0)
                    {
                        s.BedObj.NonstdBedChannelAssignmentFlag = bs.Read(17);
                        for (var i = 0; i < 17; i++)
                        {
                            if (((s.BedObj.NonstdBedChannelAssignmentFlag >> i) & 1) != 0 && !AddObject(s, ObjTypeBed, i is 3 or 16 ? 1u : 0u, 0))
                                return false;
                        }
                    }
                    else
                    {
                        s.BedObj.StdBedChannelAssignmentFlag = bs.Read(10);
                        for (var i = 0; i < 10; i++)
                        {
                            if (((s.BedObj.StdBedChannelAssignmentFlag >> i) & 1) == 0)
                                continue;
                            for (uint j = 0; j < StdBedFlagToNumObjects[i]; j++)
                            {
                                if (!AddObject(s, ObjTypeBed, i is 2 or 9 ? 1u : 0u, 0))
                                    return false;
                            }
                        }
                    }

                    if (s.NObjs > 0)
                        s.ContainsBedObjects |= 1;
                    if (nSignals > s.NObjs)
                        s.ContainsDynamicObjects |= 1;
                }
            }

            // ETSI TS 103 190-2 E.11.2
            s.ContainsBedObjects = 1;
        }
        else
        {
            if (bs.Read(1) != 0) // b_isf
            {
                if (bs.Read(1) != 0) // b_isf_start
                {
                    var isfConfig = bs.Read(3);
                    if (isfConfig >= IsfConfigToNumObjects.Length)
                        return false;
                    for (uint i = 0; i < IsfConfigToNumObjects[isfConfig]; i++)
                    {
                        if (!AddObject(s, ObjTypeIsf, 0, 0))
                            return false;
                    }

                    if (nSignals > s.NObjs)
                        s.ContainsDynamicObjects |= 1;
                }

                s.ContainsIsfObjects = 1;
            }
            else
            {
                var resBytes = bs.Read(4);
                for (uint i = 0; i < resBytes; i++)
                    bs.Read(8);
            }

            s.OtherObjs = 1;
        }

        DsiSfMultiplier(bs, s, fsIndex);

        s.SubstreamBitrateIndicatorPresent = bs.ReadByteValue(1);
        if (s.SubstreamBitrateIndicatorPresent != 0)
            BitrateIndicator(bs, s);

        for (uint i = 0; i < frameRateFactor; i++)
            s.AudioNdot = bs.ReadByteValue(1);

        if (substreamsPresent == 1)
            s.SubstreamIndex = ReadSubstreamIndex(bs);
        s.Type = Ac4SubStreamType.InfoObj;
        s.SusVer = 1;
        return true;
    }

    private static void ContentType(GpacBitReader bs, Ac4SubStreamGroup g)
    {
        g.ContentClassifier = bs.ReadByteValue(3);
        g.LanguageIndicator = bs.ReadByteValue(1);
        if (g.LanguageIndicator != 1)
            return;
        if (bs.Read(1) != 0) // b_serialized_language_tag
        {
            bs.Read(1); // b_start_tag
            bs.Read(16); // language_tag_chunk
        }
        else
        {
            g.NLanguageTagBytes = bs.ReadByteValue(6);
            for (var i = 0; i < g.NLanguageTagBytes; i++)
                g.LanguageTagBytes[i] = bs.ReadByteValue(8);
        }
    }

    private static void SubstreamGroupInfo(GpacBitReader bs, Ac4SubStreamGroup g, byte bitstreamVersion, byte presentationVersion,
        bool defaultPresentation, uint frameRateFactor, uint fsIndex, ref uint channelCount, ref uint speakerIndexMask, ref uint objOrAjoc)
    {
        g.SubstreamsPresent = bs.ReadByteValue(1);
        g.HsfExt = bs.ReadByteValue(1);
        if (bs.Read(1) != 0) // b_single_substream
        {
            g.NLfSubstreams = 1;
        }
        else
        {
            g.NLfSubstreams = (byte)(bs.Read(2) + 2);
            if (g.NLfSubstreams == 5)
                g.NLfSubstreams += (byte)bs.VariableBits(2);
        }

        g.Substreams = [];
        g.ChannelCoded = bs.ReadByteValue(1);
        if (g.ChannelCoded != 0)
        {
            for (var i = 0; i < g.NLfSubstreams; i++)
            {
                var substream = new Ac4SubStream { SusVer = bitstreamVersion == 1 ? bs.ReadByteValue(1) : (byte)1 };
                SubstreamInfoChan(bs, substream, presentationVersion, defaultPresentation, fsIndex, ref speakerIndexMask,
                    frameRateFactor, g.SubstreamsPresent, ref g.ImmersiveAudioIndicatorIms);
                g.Substreams.Add(substream);

                if (g.HsfExt != 0)
                {
                    substream = new Ac4SubStream();
                    HsfExtSubstreamInfo(bs, substream, g.SubstreamsPresent);
                    g.Substreams.Add(substream);
                }

                // GPAC ORs the b_ajoc of the last substream allocated (the HSF one when present).
                g.ImmersiveAudioIndicator |= substream.Ajoc;
            }
        }
        else
        {
            // a non channel based substream is present
            objOrAjoc = 1;

            if (bs.Read(1) != 0) // b_oamd_substream
            {
                var oamd = new Ac4SubStream();
                OamdSubstreamInfo(bs, oamd, g.SubstreamsPresent);
                g.Substreams.Add(oamd);
            }

            for (var i = 0; i < g.NLfSubstreams; i++)
            {
                var substream = new Ac4SubStream();
                uint localChannelCount = 0;
                substream.Ajoc = bs.ReadByteValue(1);
                if (substream.Ajoc != 0)
                    SubstreamInfoAjoc(bs, substream, ref localChannelCount, defaultPresentation, fsIndex, frameRateFactor, g.SubstreamsPresent);
                else
                    SubstreamInfoObj(bs, substream, ref localChannelCount, defaultPresentation, fsIndex, frameRateFactor, g.SubstreamsPresent);
                g.Substreams.Add(substream);

                if (g.HsfExt != 0)
                {
                    substream = new Ac4SubStream();
                    HsfExtSubstreamInfo(bs, substream, g.SubstreamsPresent);
                    g.Substreams.Add(substream);
                }

                if (channelCount < localChannelCount)
                    channelCount = localChannelCount;

                g.ImmersiveAudioIndicator |= substream.Ajoc;
            }
        }

        g.ContentTypePresent = bs.ReadByteValue(1);
        if (g.ContentTypePresent != 0)
            ContentType(bs, g);
    }

    private static void PresentationConfigExtInfo(GpacBitReader bs, Ac4PresentationV1 p)
    {
        p.NSkipBytes = bs.ReadByteValue(5);
        if (bs.Read(1) != 0) // b_more_skip_bytes
            p.NSkipBytes += (byte)(bs.VariableBits(2) << 5);
        for (var i = 0; i < p.NSkipBytes; i++)
            bs.Read(8);
    }

    private static void PresentationSubstreamInfo(GpacBitReader bs, Ac4SubStream s)
    {
        s.Alternative = bs.ReadByteValue(1);
        s.PresNdot = bs.ReadByteValue(1);
        s.Type = Ac4SubStreamType.PresentationInfo;
        s.SubstreamIndex = ReadSubstreamIndex(bs);
    }

    private static void SgiSpecifier(GpacBitReader bs, List<uint> indexes, byte bitstreamVersion, ref uint groupIndex)
    {
        if (bitstreamVersion == 1)
            return;
        var value = bs.Read(3);
        if (value == 7)
            value += bs.VariableBits(2);
        indexes.Add(value);
        groupIndex = Math.Max(groupIndex, value);
    }

    private static void PresentationV1Info(GpacBitReader bs, Ac4PresentationV1 p, byte bitstreamVersion, uint frameRateIndex, ref uint maxGroupIndex)
    {
        uint groupIndex = 0;
        var indexes = new List<uint>();

        var singleSubstreamGroup = bs.Read(1);
        if (singleSubstreamGroup != 1)
        {
            p.PresentationConfig = bs.ReadByteValue(3);
            if (p.PresentationConfig == 7)
                p.PresentationConfig += (byte)bs.VariableBits(2);
        }
        else
        {
            // ETSI TS 103 190-2 6.3.2.2.1: presentation_config is not used; 0x1f for the DSI writer
            p.PresentationConfig = 0x1f;
        }

        if (bitstreamVersion != 1)
        {
            p.PresentationVersion = 0;
            while (bs.Read(1) == 1 && !bs.Overflow)
                p.PresentationVersion++;
        }

        if (singleSubstreamGroup != 1 && p.PresentationConfig == 6)
        {
            p.AddEmdfSubstreams = 1;
        }
        else
        {
            if (bitstreamVersion != 1)
                p.MdCompat = bs.ReadByteValue(3);
            p.PresentationIdPresent = bs.ReadByteValue(1);
            if (p.PresentationIdPresent != 0)
                p.PresentationId = (byte)bs.VariableBits(2);

            FrameRateMultiplyInfo(bs, p, frameRateIndex);
            FrameRateFractionsInfo(bs, p, frameRateIndex);

            p.Substreams ??= [];
            EmdfInfo(bs, p.Substreams, out var emdfVersion, out var keyId);
            p.PresentationEmdfVersion = (byte)emdfVersion;
            p.PresentationKeyId = (ushort)keyId;

            p.PresentationFilter = bs.ReadByteValue(1);
            if (p.PresentationFilter != 0)
                p.EnablePresentation = bs.ReadByteValue(1);

            if (singleSubstreamGroup == 1)
            {
                SgiSpecifier(bs, indexes, bitstreamVersion, ref groupIndex);
                p.NSubstreamGroups = 1;
            }
            else
            {
                p.MultiPid = bs.ReadByteValue(1);
                switch (p.PresentationConfig)
                {
                    case 0: // Music and Effects + Dialogue
                    case 1: // Main + DE
                    case 2: // Main + Associated Audio
                        SgiSpecifier(bs, indexes, bitstreamVersion, ref groupIndex);
                        SgiSpecifier(bs, indexes, bitstreamVersion, ref groupIndex);
                        p.NSubstreamGroups = 2;
                        break;
                    case 3: // Music and Effects + Dialogue + Associated Audio
                    case 4: // Main + DE + Associated Audio
                        SgiSpecifier(bs, indexes, bitstreamVersion, ref groupIndex);
                        SgiSpecifier(bs, indexes, bitstreamVersion, ref groupIndex);
                        SgiSpecifier(bs, indexes, bitstreamVersion, ref groupIndex);
                        p.NSubstreamGroups = 3;
                        break;
                    case 5: // Arbitrary number of roles and substream groups
                        p.NSubstreamGroups = (byte)(bs.Read(2) + 2);
                        if (p.NSubstreamGroups == 5)
                            p.NSubstreamGroups += (byte)bs.VariableBits(2);
                        for (var i = 0; i < p.NSubstreamGroups; i++)
                            SgiSpecifier(bs, indexes, bitstreamVersion, ref groupIndex);
                        break;
                    default: // EMDF and other data
                        PresentationConfigExtInfo(bs, p);
                        break;
                }
            }

            p.PreVirtualized = bs.ReadByteValue(1);
            // IMS sets b_pre_virtualized (Dolby AC-4 in MPEG-DASH for Broadcast Services Specification)
            if (p.PresentationVersion == 2)
                p.PreVirtualized = 1;
            p.AddEmdfSubstreams = bs.ReadByteValue(1);

            var substream = new Ac4SubStream();
            PresentationSubstreamInfo(bs, substream);
            p.Substreams.Add(substream);
            p.Alternative = substream.Alternative;
        }

        if (p.AddEmdfSubstreams != 0)
        {
            p.NAddEmdfSubstreams = bs.ReadByteValue(2);
            if (p.NAddEmdfSubstreams == 0)
                p.NAddEmdfSubstreams = (byte)(bs.VariableBits(2) + 4);

            p.Substreams ??= [];
            for (var i = 0; i < p.NAddEmdfSubstreams && i < p.SubstreamEmdfVersion.Length; i++)
            {
                EmdfInfo(bs, p.Substreams, out var emdfVersion, out var keyId);
                // ETSI TS 103 190-2 E.8.16 & E.8.17
                p.SubstreamEmdfVersion[i] = (byte)emdfVersion;
                p.SubstreamKeyId[i] = (ushort)keyId;
            }
        }

        maxGroupIndex = Math.Max(maxGroupIndex, groupIndex);
        p.SubstreamGroupIndexes = indexes;
    }

    private static Ac4PresentationV1? GetPresentationBySubstreamGroup(Ac4StreamInfo stream, uint idx)
    {
        for (var i = 0; i < stream.NPresentations; i++)
        {
            if (stream.Presentations is null || i >= stream.Presentations.Count)
                continue;
            var p = stream.Presentations[i];
            for (var j = 0; j < p.NSubstreamGroups && j < p.SubstreamGroupIndexes.Count; j++)
            {
                if (p.SubstreamGroupIndexes[j] == idx)
                    return p;
            }
        }

        return null;
    }

    private static bool IsSubstreamGroupPartOfDefaultPresentation(List<Ac4PresentationV1> presentations, uint idx)
    {
        if (presentations.Count == 0)
            return false;
        var p = presentations[0];
        for (var i = 0; i < p.NSubstreamGroups && i < p.SubstreamGroupIndexes.Count; i++)
        {
            if (p.SubstreamGroupIndexes[i] == idx)
                return true;
        }

        return false;
    }

    /// <summary>Speakers per speaker group index (ETSI TS 103 190-2 Table A.27).</summary>
    internal static readonly int[] SpeakerGroupChannels = [2, 1, 2, 2, 2, 2, 1, 2, 2, 1, 1, 1, 1, 2, 1, 1, 2, 2, 2];

    internal static uint ChannelCountFromSpeakerGroupIndexMask(uint mask)
    {
        uint ch = 0;
        for (var i = 0; i < SpeakerGroupChannels.Length; i++)
        {
            if ((mask & (1u << i)) != 0)
                ch += (uint)SpeakerGroupChannels[i];
        }

        if ((mask & 1) != 0 && (mask & 2) != 0 && ch == 3) // mono_stereo
            ch = 2;
        return ch;
    }

    private static int SuperSet(int l, int r)
    {
        // the lowest ch_mode including all channels of both
        if (l is -1 or > 15)
            return r;
        if (r is -1 or > 15)
            return l;
        return SuperSetChMode[l, r];
    }

    private static int ConvertSpeakerLayoutToChannelMode(uint channelMask) => channelMask switch
    {
        // ETSI TS 103 190-1 Table 88
        0x00001 => 0,
        0x00003 => 1,
        0x00007 => 2,
        0x00037 => 3,
        0x0003F => 4,
        0x000F7 => 5,
        0x000FF => 6,
        0xC037 => 7,
        0xC03F => 8,
        0x00337 => 9,
        0x0033F => 10,
        _ => -1,
    };

    private static Ac4SubStreamGroup? GroupAt(Ac4PresentationV1 p, int i) => i < p.SubstreamGroups.Count ? p.SubstreamGroups[i] : null;

    private static Ac4SubStream? SubstreamAt(Ac4SubStreamGroup g, int j) => j < g.Substreams.Count ? g.Substreams[j] : null;

    private static int PresentationChMode(Ac4PresentationV1 p)
    {
        var presChMode = -1;
        var objOrAjoc = false;

        // ETSI TS 103 190-2 6.3.3.1.27
        for (var i = 0; i < p.NSubstreamGroups; i++)
        {
            if (GroupAt(p, i) is not { } group)
                continue;
            for (var j = 0; j < group.NLfSubstreams; j++)
            {
                if (SubstreamAt(group, j) is not { } substream)
                    continue;
                // Dolby AC-4 Streams Within the ISO Base Media File Format Guidelines for multiplexers 3.1.1.1
                if (group.ChannelCoded != 0)
                    presChMode = SuperSet(presChMode, (int)substream.ChMode);
                else
                    objOrAjoc = true;
            }
        }

        if (objOrAjoc)
        {
            // derive the channel configuration from the first substream group
            uint otherObjs = 0;
            var chMode = -1;
            uint mergedMask = 0;
            var group = GroupAt(p, 0);
            if (group is null)
                return presChMode;
            for (var j = 0; j < group.NLfSubstreams; j++)
            {
                if (SubstreamAt(group, j) is not { } substream)
                    continue;
                if (group.ChannelCoded != 0)
                    chMode = SuperSet(presChMode, (int)substream.ChMode);
                mergedMask |= GetObjChannelMask(substream);
                otherObjs += substream.OtherObjs;
            }

            if (otherObjs != 0)
                presChMode = -1; // objects beside the bed: object based
            else if (group.ChannelCoded != 0)
                presChMode = chMode;
            else
                presChMode = ConvertSpeakerLayoutToChannelMode(mergedMask);
        }

        return presChMode;
    }

    private static uint ChannelMaskFromChMode(Ac4PresentationV1 p)
    {
        var chMode = p.DsiPresentationChMode;
        var mask = chMode < SpeakerGroupIndexMaskByChMode.Length ? SpeakerGroupIndexMaskByChMode[chMode] : 0;
        if (chMode is >= (byte)ChMode704 and <= (byte)ChMode914)
        {
            if (p.PresB4BackChannelsPresent == 0)
                mask &= ~0x8u;
            if (p.PresBCentrePresent == 0)
                mask &= ~0x2u;
            switch (p.PresTopChannelPairs)
            {
                case 0:
                    mask &= ~0x30u;
                    break;
                case 1:
                case 2:
                    mask &= ~0x30u;
                    mask |= 0x80;
                    break;
            }
        }

        return mask;
    }

    private static uint PresentationV1ChannelGroups(Ac4PresentationV1 p)
    {
        uint channelMask = 0;
        var objOrAjoc = false;
        for (var i = 0; i < p.NSubstreamGroups; i++)
        {
            if (GroupAt(p, i) is not { } group)
                continue;
            for (var j = 0; j < group.NLfSubstreams; j++)
            {
                if (group.ChannelCoded != 0)
                    channelMask |= SubstreamAt(group, j)?.DsiSubstreamChannelGroups ?? 0;
                else
                    objOrAjoc = true;
            }
        }

        // GPAC: "temporary solution according to Dolby's internal discussion"
        if (channelMask == 0x03)
            channelMask = 0x01;

        // If one substream contains Tfl, Tfr, Tbl, Tbr, Tl and Tr shall be removed.
        if ((channelMask & 0x30) != 0 && (channelMask & 0x80) != 0)
            channelMask &= ~0x80u;

        if (objOrAjoc && GroupAt(p, 0) is { } first)
        {
            if (first.ChannelCoded != 0)
            {
                for (var j = 0; j < first.NLfSubstreams; j++)
                    channelMask |= SubstreamAt(first, j)?.DsiSubstreamChannelGroups ?? 0;
            }
            else
            {
                channelMask = ChannelMaskFromChMode(p);
            }
        }

        return channelMask;
    }

    private static byte PresB4BackChannelsPresent(Ac4PresentationV1 p)
    {
        // ETSI TS 103 190-2 E.10.12
        byte mask = 0;
        for (var i = 0; i < p.NSubstreamGroups; i++)
        {
            if (GroupAt(p, i) is not { } group)
                continue;
            for (var j = 0; j < group.NLfSubstreams; j++)
                mask |= SubstreamAt(group, j)?.FourBackChannelsPresent ?? 0;
        }

        return mask;
    }

    private static byte PresTopChannelPairs(Ac4PresentationV1 p)
    {
        // ETSI TS 103 190-2 6.3.3.1.30 Table 94
        byte top = 0;
        for (var i = 0; i < p.NSubstreamGroups; i++)
        {
            if (GroupAt(p, i) is not { } group)
                continue;
            for (var j = 0; j < group.NLfSubstreams; j++)
            {
                if (SubstreamAt(group, j) is { } s && top < s.TopChannelsPresent)
                    top = s.TopChannelsPresent;
            }
        }

        return top switch
        {
            1 or 2 => 1,
            3 => 2,
            _ => 0,
        };
    }

    private static int ChModeCore(byte channelCoded, byte ajoc, byte staticDmx, byte lfe, uint chMode)
    {
        // ETSI TS 103 190-2 Table 92
        if (channelCoded == 0 && ajoc == 1 && staticDmx == 1)
            return lfe == 0 ? 3 : lfe == 1 ? 4 : -1;
        if (channelCoded == 1 && chMode is 11 or 13)
            return 5;
        if (channelCoded == 1 && chMode is 12 or 14)
            return 6;
        return -1;
    }

    private static int PresentationCoreDiffers(Ac4PresentationV1 p, int presChMode)
    {
        var presChModeCore = -1;
        var adaptive = false;

        // ETSI TS 103 190-2 Pseudocode 26
        for (var i = 0; i < p.NSubstreamGroups; i++)
        {
            if (GroupAt(p, i) is not { } group)
                continue;
            for (var j = 0; j < group.NLfSubstreams; j++)
            {
                if (SubstreamAt(group, j) is not { } s)
                    continue;
                if (group.ChannelCoded != 0 || (s.Ajoc != 0 && s.StaticDmx != 0))
                    presChModeCore = SuperSet(presChModeCore, ChModeCore(group.ChannelCoded, s.Ajoc, s.StaticDmx, s.Lfe, s.ChMode));
                else
                    adaptive = true;
            }
        }

        if (adaptive || presChModeCore == presChMode)
            presChModeCore = -1;
        return presChModeCore;
    }

    private sealed class SubStreamInfo
    {
        public uint Size;
        public Ac4SubStream? Substream;
        public Ac4PresentationV1? Presentation;
    }

    private static void AssignSubstreamInfo(uint index, SubStreamInfo info, List<Ac4PresentationV1> presentations)
    {
        foreach (var p in presentations)
        {
            if (p.Substreams is not null)
            {
                foreach (var s in p.Substreams)
                {
                    if (s.SubstreamIndex == index)
                    {
                        info.Substream = s;
                        info.Presentation = p;
                        return;
                    }
                }
            }

            foreach (var g in p.SubstreamGroups)
            {
                if (g.SubstreamsPresent == 0)
                    continue;
                foreach (var s in g.Substreams)
                {
                    if (s.SubstreamIndex == index)
                    {
                        info.Substream = s;
                        info.Presentation = p;
                        return;
                    }
                }
            }
        }
    }

    private static uint SubstreamIndexTable(GpacBitReader bs, List<SubStreamInfo> table, List<Ac4PresentationV1> presentations)
    {
        var nSubstreams = bs.Read(2);
        if (nSubstreams == 0)
            nSubstreams = bs.VariableBits(2) + 4;
        var sizePresent = nSubstreams != 1 || bs.Read(1) != 0;

        if (sizePresent)
        {
            for (uint i = 0; i < nSubstreams && bs.Available > 0; i++)
            {
                var moreBits = bs.Read(1);
                var size = bs.Read(10);
                if (moreBits != 0)
                    size += bs.VariableBits(2) << 10;
                var info = new SubStreamInfo { Size = size };
                AssignSubstreamInfo(i, info, presentations);
                table.Add(info);
            }
        }

        return nSubstreams;
    }

    private static void DeConfig(GpacBitReader bs, Ac4SubStream s)
    {
        s.DeMethod = bs.ReadByteValue(2);
        bs.Read(2); // de_max_gain
        s.DeChannelConfig = bs.ReadByteValue(3);
    }

    // ETSI TS 103 190-1 Annex A.4 DE Huffman codebooks
    private static readonly byte[] DeHcbAbs0Len =
    [
        3, 3, 4, 4, 5, 5, 5, 5, 4, 4, 3, 7, 7, 7, 8, 7,
        7, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 8,
    ];

    private static readonly uint[] DeHcbAbs0Cw =
    [
        0x06, 0x03, 0x09, 0x01, 0x1f, 0x1d, 0x15, 0x17, 0x00, 0x03, 0x02, 0x79, 0x15, 0x10, 0x29, 0x78,
        0x5a, 0x20, 0x3d, 0x39, 0x22, 0x21, 0x28, 0x2c, 0x29, 0x38, 0x23, 0x0b, 0x09, 0x5b, 0x11, 0x28,
    ];

    private static readonly byte[] DeHcbDiff0Len =
    [
        14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14,
        14, 14, 13, 13, 13, 10, 10, 9, 8, 8, 7, 6, 5, 4, 3, 1,
        3, 5, 5, 6, 7, 7, 7, 8, 8, 8, 10, 10, 10, 11, 11, 11,
        11, 12, 12, 13, 13, 13, 14, 14, 14, 14, 14, 14, 14, 14, 13,
    ];

    private static readonly uint[] DeHcbDiff0Cw =
    [
        0x0002b0, 0x0002b1, 0x0002b4, 0x0002b5, 0x0002b7, 0x0002b6, 0x0002ba, 0x0002bb, 0x0002bc, 0x00079d, 0x00148d, 0x0014b1, 0x00079e, 0x00079f, 0x00148f, 0x0014b6,
        0x0014b4, 0x0014b5, 0x00014c, 0x000159, 0x0003cc, 0x00002a, 0x00014a, 0x00003d, 0x00000b, 0x000057, 0x000028, 0x000003, 0x000002, 0x000004, 0x000003, 0x000001,
        0x000001, 0x00000b, 0x000000, 0x000006, 0x00002a, 0x00000e, 0x000004, 0x000053, 0x00001f, 0x000056, 0x000149, 0x000078, 0x000028, 0x000297, 0x000290, 0x0000f2,
        0x000052, 0x000522, 0x0000a7, 0x000a59, 0x0003cd, 0x00015c, 0x0014b7, 0x0014b0, 0x00148e, 0x00148c, 0x00079c, 0x0002bf, 0x0002bd, 0x0002be, 0x00014d,
    ];

    private static readonly byte[] DeHcbAbs1Len =
    [
        9, 12, 12, 12, 12, 12, 12, 11, 10, 11, 11, 10, 10, 10, 10, 10,
        10, 10, 9, 9, 9, 9, 8, 8, 8, 7, 6, 6, 6, 5, 1, 4,
        5, 5, 5, 5, 5, 5, 6, 6, 5, 6, 7, 6, 7, 8, 8, 9,
        9, 9, 9, 10, 10, 10, 11, 10, 11, 11, 12, 12, 10,
    ];

    private static readonly uint[] DeHcbAbs1Cw =
    [
        0x00015c, 0x000aea, 0x000aeb, 0x000c56, 0x000c78, 0x000c79, 0x000e51, 0x000427, 0x000210, 0x00062a, 0x00063d, 0x000212, 0x00021d, 0x00031d, 0x0002bb, 0x000314,
        0x000390, 0x000395, 0x00010b, 0x00015f, 0x00018b, 0x0001cb, 0x000086, 0x0000ab, 0x0000c6, 0x000054, 0x000020, 0x000024, 0x000038, 0x00001e, 0x000000, 0x00000d,
        0x00001f, 0x000017, 0x000016, 0x000014, 0x000013, 0x000011, 0x00003b, 0x00003a, 0x000019, 0x000025, 0x000073, 0x000030, 0x000056, 0x0000c4, 0x0000aa, 0x0001c9,
        0x00015e, 0x00010f, 0x00010a, 0x000391, 0x00031c, 0x00021c, 0x000729, 0x000211, 0x000574, 0x000426, 0x000e50, 0x000c57, 0x00031f,
    ];

    private static readonly byte[] DeHcbDiff1Len =
    [
        13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13,
        13, 13, 13, 13, 12, 13, 13, 13, 13, 13, 13, 12, 12, 12, 11, 12,
        12, 12, 12, 12, 12, 12, 11, 11, 11, 11, 11, 10, 10, 10, 10, 9,
        9, 9, 8, 8, 7, 7, 7, 6, 6, 5, 4, 3, 1, 4, 5, 5,
        6, 6, 7, 7, 8, 8, 8, 9, 9, 9, 10, 10, 11, 11, 11, 11,
        11, 12, 11, 12, 12, 12, 12, 12, 12, 12, 12, 13, 13, 13, 13, 13,
        13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13,
        13, 13, 13, 13, 13, 13, 13, 13, 13,
    ];

    private static readonly uint[] DeHcbDiff1Cw =
    [
        0x001cf0, 0x001cbd, 0x001cbc, 0x001ccc, 0x001cb8, 0x001ccb, 0x001ccf, 0x001cc6, 0x001cca, 0x001cc9, 0x001cc8, 0x001cce, 0x001cc5, 0x001cd8, 0x001cc4, 0x001cdf,
        0x001cd1, 0x001cd5, 0x001ce2, 0x001ce3, 0x000c21, 0x001cfc, 0x001cf1, 0x001cf2, 0x001cf6, 0x001cf4, 0x001cf7, 0x000c20, 0x000c24, 0x000c25, 0x000729, 0x000d1e,
        0x000d1f, 0x000d59, 0x000d5b, 0x000d5f, 0x000e6d, 0x000e77, 0x00061b, 0x000613, 0x0006a8, 0x0006ae, 0x000730, 0x00030a, 0x00030c, 0x000355, 0x000396, 0x00016c,
        0x0001a2, 0x0001bb, 0x0000d0, 0x0000dc, 0x00005a, 0x000069, 0x00006f, 0x000031, 0x000038, 0x000019, 0x00000a, 0x000004, 0x000000, 0x00000f, 0x00001d, 0x000017,
        0x000036, 0x00002c, 0x00006b, 0x000060, 0x0000e4, 0x0000d4, 0x0000b7, 0x0001ba, 0x000187, 0x00016d, 0x000395, 0x00030b, 0x00073e, 0x000728, 0x0006a9, 0x00068d,
        0x00068e, 0x000e7f, 0x000611, 0x000d5e, 0x000d5a, 0x000d58, 0x000d19, 0x000d18, 0x000c34, 0x000c35, 0x000e74, 0x001cfd, 0x001cf3, 0x001cf5, 0x001cec, 0x001ced,
        0x001cea, 0x001ce7, 0x001ce5, 0x001ce6, 0x001ce4, 0x001cdd, 0x001ce1, 0x001ce0, 0x001cd4, 0x001cde, 0x001cd7, 0x001cdc, 0x001cd2, 0x001cd6, 0x001ccd, 0x001cd3,
        0x001cd9, 0x001cbf, 0x001cbe, 0x001cd0, 0x001cbb, 0x001cba, 0x001cb9, 0x001cc7, 0x001ceb,
    ];

    /// <summary>Reads one Huffman codeword (bit by bit until a code of the table matches, as GPAC does).</summary>
    private static void ReadHuffman(GpacBitReader bs, byte[] lengths, uint[] codes)
    {
        uint code = 0;
        var length = 0;
        while (bs.Available > 0)
        {
            code = (code << 1) | bs.Read(1);
            length++;
            for (var i = 0; i < codes.Length; i++)
            {
                if (lengths[i] == length && codes[i] == code)
                    return;
            }
        }
    }

    private static void DeData(GpacBitReader bs, byte deMethod, int deNrChannels, bool iframe, bool simulcast)
    {
        const int deNrBands = 8;
        if (deNrChannels <= 0)
            return;
        var (absLen, absCw, diffLen, diffCw) = deMethod % 2 == 0
            ? (DeHcbAbs0Len, DeHcbAbs0Cw, DeHcbDiff0Len, DeHcbDiff0Cw)
            : (DeHcbAbs1Len, DeHcbAbs1Cw, DeHcbDiff1Len, DeHcbDiff1Cw);

        if (deMethod is 1 or 3 && deNrChannels > 1 && !simulcast)
        {
            var keepPos = !iframe && bs.Read(1) != 0;
            if (!keepPos)
            {
                bs.Read(5); // de_mix_coef1_idx
                if (deNrChannels == 3)
                    bs.Read(5); // de_mix_coef2_idx
            }
        }

        var keepData = !iframe && bs.Read(1) != 0;
        if (keepData)
            return;

        var msProc = deMethod is 0 or 2 && deNrChannels == 2 ? (int)bs.Read(1) : 0;

        // the parameter values themselves are not needed, only the bit positions
        for (var ch = 0; ch < deNrChannels - msProc; ch++)
        {
            if (iframe && ch == 0)
            {
                ReadHuffman(bs, absLen, absCw);
                for (var band = 1; band < deNrBands; band++)
                    ReadHuffman(bs, diffLen, diffCw);
            }
            else
            {
                for (var band = 0; band < deNrBands; band++)
                    ReadHuffman(bs, diffLen, diffCw);
            }
        }

        if (deMethod >= 2)
            bs.Read(5); // de_signal_contribution
    }

    private static void DialogEnhancement(GpacBitReader bs, Ac4SubStream s, bool iframe)
    {
        // dialog_enhancement() of ETSI TS 103 190-1 4.2.14.11
        s.DeDataPresent = bs.ReadByteValue(1);
        if (s.DeDataPresent == 0)
            return;
        if (iframe || bs.Read(1) != 0) // b_de_config_flag
            DeConfig(bs, s);

        // ETSI TS 103 190-1 Table 171
        int deNrChannels = s.DeChannelConfig switch
        {
            0 or 2 or 4 => 1,
            1 or 3 or 5 or 6 => 2,
            7 => 3,
            _ => 0,
        };
        if (deNrChannels == 0)
            return;

        DeData(bs, s.DeMethod, deNrChannels, iframe, false);
        if (s.ChMode is 13 or 14 && bs.Read(1) != 0) // b_de_simulcast
            DeData(bs, s.DeMethod, deNrChannels, iframe, true);
    }

    private static bool ChannelModeContainsLfe(uint chMode) => chMode is 4 or 6 or 8 or 10 or 12 or 14 or 15;

    private static void FurtherLoudnessInfo(GpacBitReader bs, byte susVer, bool presentationLdn)
    {
        if (presentationLdn || susVer == 0)
        {
            if (bs.Read(2) == 3) // loudness_version
                bs.Read(4); // extended_loudness_version
            if (bs.Read(4) != 0) // loud_prac_type
            {
                if (bs.Read(1) != 0) // b_loudcorr_dialgate
                    bs.Read(3); // dialgate_prac_type
                bs.Read(1); // b_loudcorr_type
            }
        }
        else
        {
            bs.Read(1); // b_loudcorr_dialgate
        }

        if (bs.Read(1) != 0)
            bs.Read(11); // loudrelgat
        if (bs.Read(1) != 0)
            bs.Read(14); // loudspchgat, dialgate_prac_type
        if (bs.Read(1) != 0)
            bs.Read(11); // loudstrm3s
        if (bs.Read(1) != 0)
            bs.Read(11); // max_loudstrm3s
        if (bs.Read(1) != 0)
            bs.Read(11); // truepk
        if (bs.Read(1) != 0)
            bs.Read(11); // max_truepk
        if ((presentationLdn || susVer == 0) && bs.Read(1) != 0) // b_prgmbndy
        {
            while (bs.Read(1) == 0 && !bs.Overflow)
            {
                // prgmbndy_bit
            }

            bs.Read(1); // b_end_or_start
            if (bs.Read(1) != 0)
                bs.Read(11); // prgmbndy_offset
        }

        if (bs.Read(1) != 0)
            bs.Read(13); // lra, lra_prac_type
        if (bs.Read(1) != 0)
            bs.Read(11); // loudmntry
        if (bs.Read(1) != 0)
            bs.Read(11); // max_loudmntry

        if (susVer >= 1)
        {
            if (bs.Read(1) != 0)
                bs.Read(8); // rtll_comp
            if (bs.Read(1) != 0) // b_extension
            {
                var eBits = bs.Read(5);
                if (eBits == 31)
                    eBits += bs.VariableBits(4);
                SkipBits(bs, eBits);
            }
        }
        else if (bs.Read(1) != 0) // b_extension
        {
            var eBits = bs.Read(5);
            if (eBits == 31)
                eBits += bs.VariableBits(4);
            if (bs.Read(1) != 0) // b_rtllcomp
            {
                bs.Read(8);
                SkipBits(bs, unchecked(eBits - 9));
            }
            else
            {
                SkipBits(bs, unchecked(eBits - 1));
            }
        }
    }

    /// <summary>Skips bits one at a time, stopping at the end of the data (where GPAC would read zeros).</summary>
    private static void SkipBits(GpacBitReader bs, ulong count)
    {
        for (ulong i = 0; i < count && !bs.Overflow; i++)
            bs.Read(1);
    }

    private static void BasicMetadata(GpacBitReader bs, Ac4SubStream s)
    {
        var susVer = s.SusVer;
        if (susVer == 0)
            bs.Read(7); // dialnorm_bits
        if (bs.Read(1) == 0) // b_more_basic_metadata
            return;

        if (susVer == 0)
        {
            if (bs.Read(1) != 0) // b_further_loudness_info
                FurtherLoudnessInfo(bs, susVer, false);
        }
        else if (bs.Read(1) != 0) // b_substream_loudness_info
        {
            bs.Read(8); // substream_loudness_bits
            if (bs.Read(1) != 0) // b_further_substream_loudness_info
                FurtherLoudnessInfo(bs, susVer, false);
        }

        if (s.ChMode == 1 && bs.Read(1) != 0) // stereo: b_prev_dmx_info
            bs.Read(5); // pre_dmixtyp_2ch, phase90_info_2ch

        if (s.ChMode > 1)
        {
            if (susVer == 0 && bs.Read(1) != 0) // b_stereo_dmx_coeff
            {
                bs.Read(6); // loro_centre_mixgain, loro_surround_mixgain
                if (bs.Read(1) != 0)
                    bs.Read(5); // loro_dmx_loud_corr
                if (bs.Read(1) != 0)
                    bs.Read(6); // ltrt_centre_mixgain, ltrt_surround_mixgain
                if (bs.Read(1) != 0)
                    bs.Read(5); // ltrt_dmx_loud_corr
                if (ChannelModeContainsLfe(s.ChMode) && bs.Read(1) != 0)
                    bs.Read(5); // lfe_mixgain
                bs.Read(2); // preferred_dmx_method
            }

            if (s.ChMode is 3 or 4) // 5.X
            {
                if (bs.Read(1) != 0)
                    bs.Read(3); // pre_dmixtyp_5ch
                if (bs.Read(1) != 0)
                    bs.Read(4); // pre_upmixtyp_5ch
            }

            if (s.ChMode is >= 5 and <= 10 && bs.Read(1) != 0) // 7.X: b_upmixtyp_7ch
            {
                if (s.ChMode == 5)
                    bs.Read(2); // pre_upmixtyp_3_4
                else if (s.ChMode == 9)
                    bs.Read(1); // pre_upmixtyp_3_2_2
            }

            bs.Read(2); // phase90_info_mc
            bs.Read(1); // b_surround_attenuation_known
            bs.Read(1); // b_lfe_attenuation_known
        }

        if (bs.Read(1) != 0) // b_dc_blocking
            bs.Read(1); // dc_block_on
    }

    private static void ExtendedMetadata(GpacBitReader bs, Ac4SubStream s, Ac4PresentationV1 p)
    {
        var chMode = s.ChMode;
        // ETSI TS 103 190-1 4.3.12.4.1
        var associated = p.PresentationConfig is 2 or 3 or 4;
        var dialog = false;

        if (s.SusVer >= 1)
        {
            dialog = bs.Read(1) != 0;
        }
        else if (associated)
        {
            if (bs.Read(1) != 0)
                bs.Read(8); // scale_main
            if (bs.Read(1) != 0)
                bs.Read(8); // scale_main_centre
            if (bs.Read(1) != 0)
                bs.Read(8); // scale_main_front
            if (chMode == 0)
                bs.Read(8); // pan_associated
        }

        if (dialog)
        {
            s.DialogMaxGain = bs.ReadByteValue(1);
            if (s.DialogMaxGain != 0)
                bs.Read(2); // dialog_max_gain
            if (bs.Read(1) != 0) // b_pan_dialog_present
            {
                if (chMode == 0)
                    bs.Read(8); // pan_dialog
                else
                    bs.Read(18); // pan_dialog[0], pan_dialog[1], pan_signal_selector
            }
        }

        if (bs.Read(1) != 0) // b_channels_classifier
        {
            if ((chMode == 0 || chMode is >= 2 and <= 15) && bs.Read(1) != 0) // contains C: b_c_active
                bs.Read(1); // b_c_has_dialog
            if (chMode is >= 1 and <= 15)
            {
                if (bs.Read(1) != 0) // b_l_active
                    bs.Read(1);
                if (bs.Read(1) != 0) // b_r_active
                    bs.Read(1);
            }

            if (chMode is >= 3 and <= 15)
                bs.Read(2); // b_ls_active, b_rs_active
            if (chMode is 5 or 6 or >= 11 and <= 15)
                bs.Read(2); // b_lb_active, b_rb_active
            if (chMode is 7 or 8 or 15)
                bs.Read(2); // b_lw_active, b_rw_active
            if (chMode is 9 or 10)
                bs.Read(2); // b_tfl_active, b_tfr_active
            if (ChannelModeContainsLfe(chMode))
                bs.Read(1); // b_lfe_active
        }

        if (bs.Read(1) != 0) // b_event_probability
            bs.Read(4);
    }

    private static void DialogEnhancementInfo(GpacBitReader bs, Ac4SubStream s)
    {
        // dialog_enhancement_info() of the Dolby AC-4 in ISO BMFF guidelines for multiplexers 7.2.2
        if (bs.Read(2) != 0) // dei_version
            return;
        s.DeiDialogGainCodePresent = bs.ReadByteValue(1);
        if (s.DeiDialogGainCodePresent == 1)
        {
            s.DeiDialogGainCode = bs.ReadByteValue(6);
            s.DeiPreventDeProcessing = 1;
        }
        else
        {
            s.DeiPreventDeProcessing = bs.ReadByteValue(1);
        }

        if (s.DeiPreventDeProcessing != 1)
            return;
        if (s.DeiDialogGainCodePresent != 0 && bs.Read(4) == 0xF) // dei_drc_offset_code
            bs.Read(4); // dei_drc_offset_ext
        if (bs.Read(1) != 0) // dei_group_id_present
            bs.Read(2);
        if (bs.Read(1) != 0) // dei_dialog_is_separated
            bs.Read(1); // dei_main_contains_dialog
    }

    private static void EmdfPayloadsSubstream(GpacBitReader bs, Ac4SubStream s)
    {
        var payloadId = bs.Read(5);
        while (bs.Available > 0 && payloadId != 0)
        {
            if (payloadId == 31)
                payloadId += bs.VariableBits(5);

            // emdf_payload_config
            var smpoffst = bs.Read(1);
            if (smpoffst != 0)
                bs.VariableBits(11);
            if (bs.Read(1) != 0) // b_duration
                bs.VariableBits(11);
            if (bs.Read(1) != 0) // b_groupid
                bs.VariableBits(2);
            if (bs.Read(1) != 0) // b_codecdata
                bs.Read(8);
            if (bs.Read(1) == 0) // b_discard_unknown_payload
            {
                uint frameAligned = 0;
                if (smpoffst == 0)
                {
                    frameAligned = bs.Read(1);
                    if (frameAligned != 0)
                        bs.Read(2); // b_create_duplicate, b_remove_duplicate
                }

                if (smpoffst == 1 || frameAligned == 1)
                    bs.Read(7); // priority, proc_allowed
            }

            var payloadSize = bs.VariableBits(8);
            if (bs.Available < payloadSize * 8L)
                break;

            var pos = bs.BitOffset;
            if (payloadId == 0x14)
                DialogEnhancementInfo(bs, s);

            var remaining = unchecked((uint)(payloadSize * 8 - (bs.BitOffset - pos)));
            bs.Read((int)(remaining % 8));
            var bytes = Math.Min(remaining / 8, (ulong)bs.Available);
            for (ulong i = 0; i < bytes; i++)
                bs.Read(8);

            payloadId = bs.Read(5);
        }

        bs.Align();
    }

    private static uint ExtPrecAltPos(GpacBitReader bs, uint nObjs, uint keep, uint[] objType, uint[] lfe)
    {
        if (keep == 0)
        {
            for (var obj = 0; obj < nObjs && obj < objType.Length; obj++)
            {
                if (objType[obj] != ObjTypeDyn || lfe[obj] != 0 || bs.Read(1) == 0) // b_ext_prec_alt_pos
                    continue;
                var p0 = bs.Read(1);
                var p1 = bs.Read(1);
                var p2 = bs.Read(1);
                if (p2 != 0)
                    bs.Read(2);
                if (p1 != 0)
                    bs.Read(2);
                if (p0 != 0)
                    bs.Read(2);
            }
        }

        return 1; // GPAC subtracts the Bool result (GF_TRUE) from the bits to skip
    }

    private static void OamdDyndataSingle(GpacBitReader bs, Ac4SubStream s, Ac4PresentationV1 p)
    {
        // num_obj_info_blocks is taken as 0 by GPAC: no object_info_block() is read.
        var nObjs = s.NObjs;
        var objType = s.ObjTypes;
        var lfe = s.Lfes;
        if (p.Alternative == 0)
            return;

        bs.Read(1); // b_ducking_disabled
        if (bs.Read(2) == 3) // object_sound_category
            bs.VariableBits(2);
        var nAltDataSets = bs.Read(2);
        if (nAltDataSets == 3)
            nAltDataSets += bs.VariableBits(2);
        for (uint set = 0; set < nAltDataSets; set++)
        {
            var keep = bs.Read(1);
            if (keep == 0)
            {
                var nDataPoints = nObjs;
                if (objType[0] == ObjTypeIsf || bs.Read(1) != 0) // b_common_data
                    nDataPoints = 1;
                for (var dp = 0; dp < nDataPoints && dp < objType.Length; dp++)
                {
                    if (objType[dp] is ObjTypeBed or ObjTypeIsf)
                    {
                        if (bs.Read(1) != 0) // b_alt_gain
                            bs.Read(6);
                    }
                    else if (objType[dp] == ObjTypeDyn)
                    {
                        if (bs.Read(1) != 0) // b_alt_gain
                            bs.Read(6);
                        if (lfe[dp] == 0 && bs.Read(1) != 0) // b_alt_position
                            bs.Read(17);
                    }
                }
            }

            if (bs.Read(1) != 0) // b_additional_data
            {
                var skip = (bs.VariableBits(2) + 1) * 8;
                skip = unchecked(skip - ExtPrecAltPos(bs, nObjs, keep, objType, lfe));
                SkipBits(bs, skip);
            }
        }
    }

    private static void Metadata(GpacBitReader bs, Ac4SubStream s, Ac4PresentationV1 p)
    {
        var iframe = s.AudioNdot != 0;
        BasicMetadata(bs, s);
        ExtendedMetadata(bs, s, p);
        if (p.Alternative != 0 && s.Ajoc == 0)
            OamdDyndataSingle(bs, s, p);

        bs.Read(7); // tools_metadata_size_value
        if (bs.Read(1) != 0) // b_more_bits
            bs.VariableBits(3);

        if (s.SusVer == 0)
            return; // drc_frame() of sus_ver 0 is not parsed (never reached: bitstream_version > 1)

        DialogEnhancement(bs, s, iframe);
        if (bs.Read(1) != 0) // b_emdf_payloads_substream
            EmdfPayloadsSubstream(bs, s);
    }

    private static void Substream(GpacBitReader bs, Ac4SubStream s, Ac4PresentationV1 p)
    {
        var audioSize = bs.Read(15);
        if (bs.Read(1) != 0) // b_more_bits
            audioSize += bs.VariableBits(7) << 15;

        // skip audio_data(), the metadata follows it
        bs.Seek(bs.Position + audioSize);
        bs.Align();
        Metadata(bs, s, p);
        bs.Align();
    }

    private static void PresentationSubstream(GpacBitReader bs, Ac4PresentationV1 p)
    {
        if (p.Alternative != 0)
        {
            if (bs.Read(1) != 0) // b_name_present
            {
                var nameLength = bs.Read(1) != 0 ? bs.Read(5) : 32u;
                for (uint i = 0; i < nameLength && bs.Available > 0; i++)
                    bs.Read(8);
            }

            var nTargets = bs.Read(2) + 1;
            if (nTargets == 4)
                nTargets += bs.VariableBits(2);
            for (uint t = 0; t < nTargets; t++)
            {
                bs.Read(3); // target_level
                bs.Read(4); // target_device_category
                if (bs.Read(1) == 1) // b_tdc_extension
                    bs.Read(4);
                if (bs.Read(1) != 0) // b_ducking_depth_present
                    bs.Read(6);
                if (bs.Read(1) != 0) // b_loud_corr_target
                    bs.Read(5);
                for (uint sus = 0; sus < p.NSubstreamsInPresentation; sus++)
                {
                    if (bs.Read(1) != 0 && bs.Read(1) == 1) // b_active, alt_data_set_index
                        bs.VariableBits(2);
                }
            }
        }

        p.AdditionalData = bs.ReadByteValue(1);
        if (p.AdditionalData != 0)
        {
            var addDataBytes = bs.Read(4) + 1;
            if (addDataBytes == 16)
                bs.VariableBits(2);
            bs.Align();
            p.ImmersiveAudioIndicator = bs.ReadByteValue(1);
        }
    }

    private static bool RawFrame(GpacBitReader bs, Ac4StreamInfo stream)
    {
        var ok = true;
        var tocPos = bs.Position;

        var bitstreamVersion = bs.Read(2);
        if (bitstreamVersion == 3)
            bitstreamVersion += bs.VariableBits(2);

        bs.Read(10); // sequence_counter
        int waitFrames;
        if (bs.Read(1) != 0) // b_wait_frames
        {
            waitFrames = (int)bs.Read(3);
            if (waitFrames > 0)
                bs.Read(2); // reserved
        }
        else
        {
            waitFrames = -1;
        }

        var fsIndex = bs.ReadByteValue(1);
        var frameRateIndex = bs.Read(4);
        var iframeGlobal = bs.ReadByteValue(1);
        uint nPresentations;
        if (bs.Read(1) == 1) // b_single_presentation
            nPresentations = 1;
        else if (bs.Read(1) == 1) // b_more_presentations
            nPresentations = bs.VariableBits(2) + 2;
        else
            nPresentations = 0;

        uint payloadBase = 0;
        if (bs.Read(1) == 1) // b_payload_base
        {
            payloadBase = bs.Read(5) + 1;
            if (payloadBase == 0x20)
                payloadBase += bs.VariableBits(3);
        }

        stream.BitstreamVersion = (byte)bitstreamVersion;
        stream.FsIndex = fsIndex;
        stream.IFrameGlobal = iframeGlobal;
        stream.FrameRateIndex = (byte)frameRateIndex;
        stream.NPresentations = (ushort)nPresentations;

        // ac4_presentation_info() of bitstream_version <= 1 is deprecated and not parsed
        if (bitstreamVersion <= 1)
            return true;

        stream.ProgramIdPresent = bs.ReadByteValue(1);
        if (stream.ProgramIdPresent == 1)
        {
            stream.ShortProgramId = (ushort)bs.Read(16);
            stream.Uuid = bs.ReadByteValue(1);
            if (stream.Uuid == 1)
            {
                for (var i = 0; i < 16; i++)
                    stream.ProgramUuid[i] = bs.ReadByteValue(8);
            }
        }

        // bit rate mode, ETSI TS 103 190-2 Annex B
        if (waitFrames == 0)
            stream.BitrateDsi.BitRateMode = 1;
        else if (waitFrames is >= 1 and <= 6)
            stream.BitrateDsi.BitRateMode = 2;
        else if (waitFrames > 6)
            stream.BitrateDsi.BitRateMode = 3;
        stream.BitrateDsi.BitRate = 0;
        stream.BitrateDsi.BitRatePrecision = 0xFFFFFFFF;

        if (nPresentations == 0)
        {
            stream.Presentations = null;
            return true;
        }

        var presentations = new List<Ac4PresentationV1>();
        stream.Presentations = presentations;
        uint maxGroupIndex = 0;
        for (uint i = 0; i < nPresentations; i++)
        {
            var p = new Ac4PresentationV1();
            PresentationV1Info(bs, p, (byte)bitstreamVersion, frameRateIndex, ref maxGroupIndex);
            presentations.Add(p);
            if (bs.Overflow)
            {
                ok = false;
                break;
            }
        }

        var parsedGroups = new List<Ac4SubStreamGroup>();
        uint channelCount = 0;
        uint speakerGroupIndexMask = 0;
        uint objOrAjoc = 0;
        for (uint i = 0; i < maxGroupIndex + 1; i++)
        {
            var p = GetPresentationBySubstreamGroup(stream, i);
            if (p is null)
                break;

            uint localChannelCount = 0;
            uint frameRateFactor = p.DsiFrameRateMultiplyInfo == 0 ? 1u : p.DsiFrameRateMultiplyInfo * 2u;
            var defaultPresentation = IsSubstreamGroupPartOfDefaultPresentation(presentations, i);
            var group = new Ac4SubStreamGroup();
            SubstreamGroupInfo(bs, group, (byte)bitstreamVersion, p.PresentationVersion, defaultPresentation, frameRateFactor,
                fsIndex, ref localChannelCount, ref speakerGroupIndexMask, ref objOrAjoc);
            parsedGroups.Add(group);
            if (channelCount < localChannelCount)
                channelCount = localChannelCount;
        }

        for (var i = 0; i < nPresentations && i < presentations.Count; i++)
        {
            var p = presentations[i];
            p.SubstreamGroups = [];
            for (var j = 0; j < p.NSubstreamGroups; j++)
            {
                if (j >= p.SubstreamGroupIndexes.Count)
                    continue;
                var idx = p.SubstreamGroupIndexes[j];
                if (idx < parsedGroups.Count)
                {
                    var group = parsedGroups[(int)idx];
                    p.SubstreamGroups.Add(group);
                    p.NSubstreamsInPresentation += (uint)group.Substreams.Count;
                }
                else
                {
                    ok = false; // substream group of the presentation not found
                    break;
                }
            }

            // ETSI TS 103 190-2 E.10.2
            var presChMode = PresentationChMode(p);
            p.PresentationChannelCoded = (byte)(presChMode == -1 ? 0 : 1);
            if (p.PresentationChannelCoded == 1)
            {
                p.DsiPresentationChMode = (byte)presChMode;
                if (presChMode is >= 11 and <= 14)
                {
                    p.PresB4BackChannelsPresent = PresB4BackChannelsPresent(p);
                    p.PresTopChannelPairs = PresTopChannelPairs(p);
                }

                p.PresentationV1ChannelGroups = PresentationV1ChannelGroups(p);
            }

            var presChModeCore = PresentationCoreDiffers(p, presChMode);
            p.PresentationCoreDiffers = (byte)(presChModeCore == -1 ? 0 : 1);
            if (p.PresentationCoreDiffers == 1)
            {
                p.PresentationCoreChannelCoded = 1;
                // ETSI TS 103 190-2 Table E.14
                p.DsiPresentationChannelModeCore = (byte)(presChModeCore - 3);
            }
        }

        // channel based: from the speaker groups of the default presentation; otherwise the largest count
        stream.ChannelCount = objOrAjoc == 0 ? ChannelCountFromSpeakerGroupIndexMask(speakerGroupIndexMask) : channelCount;

        var table = new List<SubStreamInfo>();
        var nSubstreams = SubstreamIndexTable(bs, table, presentations);
        bs.Align();
        stream.TocSize = (uint)(bs.Position - tocPos);

        if (nSubstreams == 0 || nSubstreams != table.Count)
        {
            ok = false;
            nSubstreams = 0;
        }

        // fill area and byte align
        bs.Seek(bs.Position + payloadBase);
        bs.Align();

        // ETSI TS 103 190-2 Table 50: ac4_substream_data mapping
        for (var i = 0; i < nSubstreams; i++)
        {
            var pos = bs.Position;
            var info = table[i];
            if (info.Substream is { } substream && info.Presentation is { } presentation)
            {
                switch (substream.Type)
                {
                    case Ac4SubStreamType.Info:
                    case Ac4SubStreamType.InfoChan:
                    case Ac4SubStreamType.InfoObj:
                    case Ac4SubStreamType.InfoAjoc:
                        Substream(bs, substream, presentation);
                        break;
                    case Ac4SubStreamType.EmdfPayloads:
                        EmdfPayloadsSubstream(bs, substream);
                        break;
                    case Ac4SubStreamType.PresentationInfo:
                        PresentationSubstream(bs, presentation);
                        break;
                    case Ac4SubStreamType.HsfExt:
                    case Ac4SubStreamType.Oamd:
                        break;
                    default:
                        ok = false;
                        break;
                }
            }

            bs.Seek(pos + info.Size);
            bs.Align();
        }

        // Dolby AC-4 in ISO BMFF guidelines for multiplexers: de_indicator and immersive_audio_indicator of the
        // presentations from their substreams; then only the audio substreams stay in the groups.
        foreach (var p in presentations)
        {
            if (p.Substreams is not null)
            {
                foreach (var s in p.Substreams)
                    p.DeIndicator |= (byte)(s.DeiPreventDeProcessing | s.DeDataPresent | s.DialogMaxGain);
            }

            // for IMS content, immersive_audio_indicator derives from the channel mode
            if (p.PresentationVersion == 2)
                p.ImmersiveAudioIndicator = 0;

            foreach (var group in p.SubstreamGroups)
            {
                if (p.PresentationVersion == 2)
                {
                    p.ImmersiveAudioIndicator |= group.ImmersiveAudioIndicatorIms;
                }
                else if (p.AdditionalData == 0)
                {
                    // without immersive_audio_indicator in the presentation substream, from the substream groups
                    p.ImmersiveAudioIndicator |= group.ImmersiveAudioIndicator;
                    if (p.PresTopChannelPairs != 0)
                        p.ImmersiveAudioIndicator = 1; // channel based immersive
                }

                foreach (var s in group.Substreams)
                    p.DeIndicator |= (byte)(s.DeiPreventDeProcessing | s.DeDataPresent | s.DialogMaxGain);
            }

            foreach (var group in p.SubstreamGroups)
            {
                group.Substreams = group.Substreams
                    .Where(s => s.Type is Ac4SubStreamType.Info or Ac4SubStreamType.InfoChan or Ac4SubStreamType.InfoObj or Ac4SubStreamType.InfoAjoc)
                    .ToList();
            }
        }

        return ok;
    }
}
