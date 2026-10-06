namespace MMW.Formats.Matroska;

/// <summary>
/// EBML element IDs used by Matroska/WebM (from <c>ebml_matroska.xml</c> of the IETF CELLAR specification).
/// IDs include their VINT marker bits, as they are written in files.
/// </summary>
internal static class MatroskaIds
{
    // EBML header
    public const ulong EbmlHeader = 0x1A45DFA3;
    public const ulong EbmlVersion = 0x4286;
    public const ulong EbmlReadVersion = 0x42F7;
    public const ulong EbmlMaxIdLength = 0x42F2;
    public const ulong EbmlMaxSizeLength = 0x42F3;
    public const ulong DocType = 0x4282;
    public const ulong DocTypeVersion = 0x4287;
    public const ulong DocTypeReadVersion = 0x4285;

    // Global elements
    public const ulong VoidElement = 0xEC;
    public const ulong Crc32Element = 0xBF;

    // Segment and top-level children
    public const ulong Segment = 0x18538067;
    public const ulong SeekHead = 0x114D9B74;
    public const ulong Info = 0x1549A966;
    public const ulong Tracks = 0x1654AE6B;
    public const ulong Cluster = 0x1F43B675;
    public const ulong Cues = 0x1C53BB6B;
    public const ulong Attachments = 0x1941A469;
    public const ulong Chapters = 0x1043A770;
    public const ulong Tags = 0x1254C367;

    // SeekHead
    public const ulong Seek = 0x4DBB;
    public const ulong SeekId = 0x53AB;
    public const ulong SeekPosition = 0x53AC;

    // Info
    public const ulong SegmentUuid = 0x73A4;
    public const ulong TimestampScale = 0x2AD7B1;
    public const ulong Duration = 0x4489;
    public const ulong DateUtc = 0x4461;
    public const ulong Title = 0x7BA9;
    public const ulong MuxingApp = 0x4D80;
    public const ulong WritingApp = 0x5741;

    // Cluster children (needed to scan unknown-size clusters)
    public const ulong Timestamp = 0xE7;
    public const ulong SimpleBlock = 0xA3;
    public const ulong BlockGroup = 0xA0;

    // Tracks
    public const ulong TrackEntry = 0xAE;
    public const ulong TrackNumber = 0xD7;
    public const ulong TrackUid = 0x73C5;
    public const ulong TrackType = 0x83;
    public const ulong FlagEnabled = 0xB9;
    public const ulong FlagDefault = 0x88;
    public const ulong FlagForced = 0x55AA;
    public const ulong FlagHearingImpaired = 0x55AB;
    public const ulong FlagVisualImpaired = 0x55AC;
    public const ulong FlagTextDescriptions = 0x55AD;
    public const ulong FlagOriginal = 0x55AE;
    public const ulong FlagCommentary = 0x55AF;
    public const ulong FlagLacing = 0x9C;
    public const ulong DefaultDuration = 0x23E383;
    public const ulong Name = 0x536E;
    public const ulong TrackLanguage = 0x22B59C;
    public const ulong LanguageBcp47 = 0x22B59D;
    public const ulong CodecId = 0x86;
    public const ulong CodecPrivate = 0x63A2;
    public const ulong CodecName = 0x258688;
    public const ulong CodecDelay = 0x56AA;
    public const ulong SeekPreRoll = 0x56BB;
    public const ulong BlockAdditionMapping = 0x41E4;
    public const ulong BlockAddIdValue = 0x41F0;
    public const ulong BlockAddIdName = 0x41A4;
    public const ulong BlockAddIdType = 0x41E7;
    public const ulong BlockAddIdExtraData = 0x41ED;
    public const ulong ContentEncodings = 0x6D80;

    // Video
    public const ulong Video = 0xE0;
    public const ulong FlagInterlaced = 0x9A;
    public const ulong StereoMode = 0x53B8;
    public const ulong PixelWidth = 0xB0;
    public const ulong PixelHeight = 0xBA;
    public const ulong DisplayWidth = 0x54B0;
    public const ulong DisplayHeight = 0x54BA;
    public const ulong DisplayUnit = 0x54B2;
    public const ulong Colour = 0x55B0;
    public const ulong MatrixCoefficients = 0x55B1;
    public const ulong BitsPerChannel = 0x55B2;
    public const ulong ColourRange = 0x55B9;
    public const ulong TransferCharacteristics = 0x55BA;
    public const ulong Primaries = 0x55BB;
    public const ulong MaxCll = 0x55BC;
    public const ulong MaxFall = 0x55BD;
    public const ulong MasteringMetadata = 0x55D0;
    public const ulong PrimaryRChromaticityX = 0x55D1;
    public const ulong PrimaryRChromaticityY = 0x55D2;
    public const ulong PrimaryGChromaticityX = 0x55D3;
    public const ulong PrimaryGChromaticityY = 0x55D4;
    public const ulong PrimaryBChromaticityX = 0x55D5;
    public const ulong PrimaryBChromaticityY = 0x55D6;
    public const ulong WhitePointChromaticityX = 0x55D7;
    public const ulong WhitePointChromaticityY = 0x55D8;
    public const ulong LuminanceMax = 0x55D9;
    public const ulong LuminanceMin = 0x55DA;

    // Audio
    public const ulong Audio = 0xE1;
    public const ulong SamplingFrequency = 0xB5;
    public const ulong OutputSamplingFrequency = 0x78B5;
    public const ulong Channels = 0x9F;
    public const ulong BitDepth = 0x6264;

    // Attachments
    public const ulong AttachedFile = 0x61A7;
    public const ulong FileDescription = 0x467E;
    public const ulong FileName = 0x466E;
    public const ulong FileMediaType = 0x4660;
    public const ulong FileData = 0x465C;
    public const ulong FileUid = 0x46AE;

    // Chapters
    public const ulong EditionEntry = 0x45B9;
    public const ulong EditionUid = 0x45BC;
    public const ulong EditionFlagHidden = 0x45BD;
    public const ulong EditionFlagDefault = 0x45DB;
    public const ulong EditionFlagOrdered = 0x45DD;
    public const ulong ChapterAtom = 0xB6;
    public const ulong ChapterUid = 0x73C4;
    public const ulong ChapterStringUid = 0x5654;
    public const ulong ChapterTimeStart = 0x91;
    public const ulong ChapterTimeEnd = 0x92;
    public const ulong ChapterFlagHidden = 0x98;
    public const ulong ChapterFlagEnabled = 0x4598;
    public const ulong ChapterDisplay = 0x80;
    public const ulong ChapString = 0x85;
    public const ulong ChapLanguage = 0x437C;
    public const ulong ChapLanguageBcp47 = 0x437D;
    public const ulong ChapCountry = 0x437E;

    // Tags
    public const ulong Tag = 0x7373;
    public const ulong Targets = 0x63C0;
    public const ulong TargetTypeValue = 0x68CA;
    public const ulong TargetType = 0x63CA;
    public const ulong TagTrackUid = 0x63C5;
    public const ulong TagEditionUid = 0x63C9;
    public const ulong TagChapterUid = 0x63C4;
    public const ulong TagAttachmentUid = 0x63C6;
    public const ulong SimpleTag = 0x67C8;
    public const ulong TagName = 0x45A3;
    public const ulong TagLanguage = 0x447A;
    public const ulong TagLanguageBcp47 = 0x447B;
    public const ulong TagDefault = 0x4484;
    public const ulong TagString = 0x4487;
    public const ulong TagBinary = 0x4485;

    /// <summary>Block addition type of a Dolby Vision configuration record ('dvcC').</summary>
    public const ulong BlockAddTypeDvcC = 0x64766343;

    /// <summary>Block addition type of a Dolby Vision configuration record for profiles above 7 ('dvvC').</summary>
    public const ulong BlockAddTypeDvvC = 0x64767643;

    /// <summary>Block addition type of ITU-T T.35 metadata (HDR10+ in WebM VP9, BlockAddIDValue 4).</summary>
    public const ulong BlockAddTypeItuT35 = 4;

    /// <summary>TrackEntry: highest BlockAddID used by the track's blocks.</summary>
    public const ulong MaxBlockAdditionId = 0x55EE;

    /// <summary>True for IDs that may appear as direct children of a Segment.</summary>
    public static bool IsTopLevel(ulong id) => id is SeekHead or Info or Tracks or Cluster or Cues or Attachments or Chapters or Tags;
}
