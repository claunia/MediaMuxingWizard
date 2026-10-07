using MMW.Core.Resources;

namespace MMW.Core.Media;

/// <summary>Reads byte ranges of a source file (used to load sample payloads lazily).</summary>
public interface ISampleDataReader
{
    /// <summary>Reads exactly <paramref name="destination"/>.Length bytes at <paramref name="position"/>.</summary>
    /// <exception cref="EndOfStreamException">The source ends before the requested range.</exception>
    void Read(long position, Span<byte> destination);
}

/// <summary>
/// One access unit (video frame, audio frame, subtitle cue) of a track.
/// </summary>
/// <remarks>
/// <para>
/// Times are integers in the timescale of the <see cref="ISampleSource"/> that produced the sample. Samples are
/// delivered in decoding order; <see cref="Dts"/> + <see cref="CtsOffset"/> is the presentation time. The offset may
/// be negative (consumers normalise it, e.g. the MP4 muxer shifts it into the edit list).
/// </para>
/// <para>
/// The payload is either held in memory (<see cref="Data"/>) or read lazily from the source file
/// (<see cref="Reader"/>, <see cref="Position"/>, <see cref="StoredSize"/>, optionally preceded by
/// <see cref="Prefix"/> for Matroska header stripping), so demuxers can queue many samples without holding their data.
/// </para>
/// </remarks>
public sealed class MediaSample
{
    /// <summary>Decoding time in the source timescale.</summary>
    public long Dts { get; set; }

    /// <summary>Presentation time minus decoding time (may be negative).</summary>
    public long CtsOffset { get; set; }

    /// <summary>Duration in the source timescale; 0 when unknown (the next sample's DTS decides).</summary>
    public long Duration { get; set; }

    /// <summary>
    /// Samples at the end of this frame that are not presented (encoder padding of the last audio frame: Matroska
    /// DiscardPadding, the end of an MP4 edit list, the Ogg end granule position), in the source timescale; 0 for none.
    /// Counted from the end of the decoded frame (its full sample count), which <see cref="Duration"/> may already stop
    /// short of (FFmpeg gives the last Matroska block the played duration only).
    /// </summary>
    public long TrimEnd { get; set; }

    /// <summary>True for random access points (key frames).</summary>
    public bool IsSync { get; set; }

    /// <summary>True when no other sample depends on this one (Matroska "discardable", MP4 sdtp).</summary>
    public bool IsDiscardable { get; set; }

    /// <summary>Payload held in memory; empty when the payload is read lazily.</summary>
    public ReadOnlyMemory<byte> Data { get; set; }

    /// <summary>Bytes that precede the stored payload (Matroska header stripping); empty otherwise.</summary>
    public ReadOnlyMemory<byte> Prefix { get; set; }

    /// <summary>Reader for a lazily loaded payload; null when <see cref="Data"/> holds it.</summary>
    public ISampleDataReader? Reader { get; set; }

    /// <summary>Absolute position of the stored payload in the source file (lazy payloads only).</summary>
    public long Position { get; set; } = -1;

    /// <summary>Length of the stored payload in the source file (lazy payloads only).</summary>
    public int StoredSize { get; set; }

    /// <summary>
    /// Data stored next to the frame (Matroska BlockAdditions), e.g. HDR10+ metadata of VP9 video (ID 4, ITU-T T.35);
    /// null when there is none. Containers without such storage drop it.
    /// </summary>
    public IReadOnlyList<BlockAddition>? Additions { get; set; }

    /// <summary>
    /// WebVTT cue settings of a WebVTT cue sample ("position:10% line:0 align:start vertical:rl …", as on the cue's
    /// timing line); null when the cue has none. The payload holds the cue text only.
    /// </summary>
    public string? CueSettings { get; set; }

    /// <summary>Presentation time.</summary>
    public long Pts => Dts + CtsOffset;

    /// <summary>Payload size in bytes.</summary>
    public int Size => Reader is null ? Data.Length : Prefix.Length + StoredSize;

    /// <summary>Copies the payload into <paramref name="destination"/>, which must be <see cref="Size"/> bytes long.</summary>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException(Strings.Error_DestinationTooSmall, nameof(destination));
        if (Reader is null)
        {
            Data.Span.CopyTo(destination);
            return;
        }

        Prefix.Span.CopyTo(destination);
        Reader.Read(Position, destination.Slice(Prefix.Length, StoredSize));
    }

    /// <summary>Copies the first <paramref name="destination"/>.Length bytes of the payload.</summary>
    public void CopyHead(Span<byte> destination)
    {
        if (destination.Length > Size)
            throw new ArgumentException(Strings.Error_SampleShorterThanHead, nameof(destination));
        if (Reader is null)
        {
            Data.Span[..destination.Length].CopyTo(destination);
            return;
        }

        var fromPrefix = Math.Min(Prefix.Length, destination.Length);
        Prefix.Span[..fromPrefix].CopyTo(destination);
        if (destination.Length > fromPrefix)
            Reader.Read(Position, destination[fromPrefix..]);
    }

    /// <summary>Returns the payload, loading it when it is stored lazily.</summary>
    public ReadOnlyMemory<byte> GetData()
    {
        if (Reader is null)
            return Data;
        var buffer = new byte[Size];
        CopyTo(buffer);
        return buffer;
    }

    /// <summary>A shallow copy (the payload is shared).</summary>
    public MediaSample Clone() => (MediaSample)MemberwiseClone();

    public override string ToString() => $"dts={Dts} cts={CtsOffset} dur={Duration} size={Size}{(IsSync ? " sync" : string.Empty)}";
}

/// <summary>A Matroska BlockMore: data of type <paramref name="Id"/> (BlockAddID) attached to a frame.</summary>
public readonly record struct BlockAddition(ulong Id, ReadOnlyMemory<byte> Data)
{
    /// <summary>BlockAddID of ITU-T T.35 metadata (HDR10+ in WebM VP9).</summary>
    public const ulong ItuT35 = 4;
}
