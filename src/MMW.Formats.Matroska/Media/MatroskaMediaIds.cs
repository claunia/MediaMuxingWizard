namespace MMW.Formats.Matroska.Media;

/// <summary>Element IDs used by the demuxer and muxer in addition to <see cref="MatroskaIds"/>.</summary>
internal static class MatroskaMediaIds
{
    // Cluster
    public const ulong Block = 0xA1;
    public const ulong BlockDuration = 0x9B;
    public const ulong ReferenceBlock = 0xFB;
    public const ulong BlockAdditions = 0x75A1;
    public const ulong EncryptedBlock = 0xAF;

    // Cues
    public const ulong CuePoint = 0xBB;
    public const ulong CueTime = 0xB3;
    public const ulong CueTrackPositions = 0xB7;
    public const ulong CueTrack = 0xF7;
    public const ulong CueClusterPosition = 0xF1;
    public const ulong CueRelativePosition = 0xF0;

    // Content encoding
    public const ulong ContentEncoding = 0x6240;
    public const ulong ContentEncodingOrder = 0x5031;
    public const ulong ContentEncodingScope = 0x5032;
    public const ulong ContentEncodingType = 0x5033;
    public const ulong ContentCompression = 0x5034;
    public const ulong ContentCompAlgo = 0x4254;
    public const ulong ContentCompSettings = 0x4255;
    public const ulong ContentEncryption = 0x5035;
}
