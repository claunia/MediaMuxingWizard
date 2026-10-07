using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.Formats.Elementary.Resources;

namespace MMW.Formats.Elementary;

/// <summary>
/// Native FLAC files (RFC 9639): the "fLaC" marker (after an optional ID3v2 tag), the metadata blocks, then frames.
/// The track's configuration keeps STREAMINFO and VORBIS_COMMENT (pictures and padding stay out of the codec
/// configuration); the tags and pictures are read separately as document metadata.
/// </summary>
internal static class FlacFile
{
    /// <summary>The metadata blocks of a native FLAC file and where its frames start.</summary>
    public sealed record Header(List<(int Type, byte[] Body)> Blocks, long AudioOffset);

    /// <summary>True when the file starts with "fLaC", or with an ID3v2 tag (the marker follows it).</summary>
    public static bool LooksLikeFlac(ReadOnlySpan<byte> header) => header.StartsWith("fLaC"u8) || header.StartsWith("ID3"u8);

    public static Header ReadHeader(Stream stream)
    {
        Span<byte> head = stackalloc byte[10];
        stream.Position = 0;
        stream.ReadExactly(head);
        long start = 0;
        if (head.StartsWith("ID3"u8))
            start = 10 + ((head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | (head[9] & 0x7F)) + ((head[5] & 0x10) != 0 ? 10 : 0);
        stream.Position = start;
        stream.ReadExactly(head[..4]);
        if (!head[..4].SequenceEqual("fLaC"u8))
            throw new InvalidDataException(Strings.Error_NotFlac);

        var blocks = new List<(int, byte[])>();
        while (true)
        {
            stream.ReadExactly(head[..4]);
            var last = (head[0] & 0x80) != 0;
            var type = head[0] & 0x7F;
            var length = (head[1] << 16) | (head[2] << 8) | head[3];
            if (type == 127)
                throw new InvalidDataException(Strings.Error_InvalidFlacMetadataBlock);
            var body = new byte[length];
            stream.ReadExactly(body);
            blocks.Add((type, body));
            if (last)
                break;
        }

        if (blocks.Count == 0 || blocks[0].Item1 != Flac.StreamInfoType || blocks[0].Item2.Length < 34)
            throw new InvalidDataException(Strings.Error_FlacNoStreamInfo);
        return new Header(blocks, stream.Position);
    }

    /// <summary>STREAMINFO and VORBIS_COMMENT as metadata blocks (the codec configuration).</summary>
    public static byte[] ConfigBlocks(Header header)
    {
        var o = new List<byte>();
        foreach (var (type, body) in header.Blocks.Where(b => b.Type is Flac.StreamInfoType or Flac.VorbisCommentType))
        {
            o.Add((byte)type);
            o.Add((byte)(body.Length >> 16));
            o.Add((byte)(body.Length >> 8));
            o.Add((byte)body.Length);
            o.AddRange(body);
        }

        return Flac.FixLastFlags(o.ToArray());
    }

    public static CodecConfig Probe(Stream stream, out Header header, out long totalSamples)
    {
        header = ReadHeader(stream);
        var blocks = ConfigBlocks(header);
        var info = Flac.ParseStreamInfo(blocks)!;
        totalSamples = info.TotalSamples;
        return new CodecConfig
        {
            Codec = CodecType.Flac,
            Kind = TrackKind.Audio,
            SourceCodecId = "fLaC",
            Extradata = blocks,
            SampleRate = info.SampleRate,
            Channels = info.Channels,
            BitsPerSample = info.BitsPerSample,
            Timescale = (uint)Math.Max(1, info.SampleRate),
            DefaultSampleDuration = info.MaxBlockSize,
            AudioProfile = Flac.DescribeStream(blocks),
            Language = "und",
        };
    }

    /// <summary>
    /// Samples in the file, from its last frame: its header (frame or sample number, block size) gives where the audio
    /// ends. STREAMINFO's total may be 0 (unknown) or wrong (FFmpeg writes the first block's count when it stores a
    /// picture); it is only used when the last frame cannot be found.
    /// </summary>
    public static long CountSamples(Stream stream, Header header, FlacStreamInfo info)
    {
        var end = stream.Length;
        if (end >= 128)
        {
            Span<byte> tag = stackalloc byte[3];
            stream.Position = end - 128;
            stream.ReadExactly(tag);
            if (tag.SequenceEqual("TAG"u8))
                end -= 128;
        }

        var size = (int)Math.Min(end - header.AudioOffset, Math.Max(1 << 20, 2L * info.MaxFrameSize));
        var tail = new byte[size];
        stream.Position = end - size;
        stream.ReadExactly(tail);
        for (var i = size - 8; i >= 0; i--)
        {
            if (tail[i] != 0xFF || (tail[i + 1] & 0xFE) != 0xF8 || Flac.ParseFrameHeader(tail.AsSpan(i), info) is not { } last)
                continue;
            if (Flac.Crc16(tail.AsSpan(i)) != 0)
                continue; // not the frame that runs to the end of the audio
            return last.VariableBlockSize ? last.Number + last.Samples : last.Number * info.MaxBlockSize + last.Samples;
        }

        return info.TotalSamples;
    }

    /// <summary>The tags (VORBIS_COMMENT) and pictures (PICTURE) of a FLAC file.</summary>
    public static MetadataSet ReadMetadata(string path)
    {
        using var stream = File.OpenRead(path);
        var header = ReadHeader(stream);
        var fields = header.Blocks.Where(b => b.Type == Flac.VorbisCommentType).SelectMany(b => VorbisComments.Parse(b.Body).Fields);
        var pictures = header.Blocks.Where(b => b.Type == Flac.PictureType).Select(b => b.Body);
        return VorbisComments.ToMetadata(fields.ToList(), pictures);
    }
}

/// <summary>
/// Splits the frames of a native FLAC file. Frames carry no length: a frame ends where the next valid header starts,
/// confirmed by the header's CRC-8, its frame (or sample) number continuing this one, and the CRC-16 of the frame
/// between them. An ID3v1 tag at the end of the file is not part of the last frame.
/// </summary>
internal sealed class FlacParser : IElementaryParser
{
    private const int ReadAhead = 64 * 1024;

    private readonly FrameReader _reader;
    private readonly FlacStreamInfo _info;
    private readonly long _end;
    private long _time;

    public FlacParser(Stream stream, FlacFile.Header header, FlacStreamInfo info)
    {
        _info = info;
        _end = stream.Length - (HasId3V1(stream) ? 128 : 0);
        stream.Position = header.AudioOffset;
        _reader = new FrameReader(stream);
    }

    public MediaSample? Next()
    {
        while (true)
        {
            var remaining = _end - _reader.Position;
            if (remaining <= 0 || !_reader.Ensure((int)Math.Min(Flac.MaxHeaderLength, remaining)))
                return null;
            var available = _reader.Available[..(int)Math.Min(_reader.Available.Length, remaining)];
            if (Flac.ParseFrameHeader(available, _info) is not { } header)
            {
                // Not at a frame (garbage, or a damaged frame): resynchronise on the next valid header.
                var next = FindNext(available, 1, null);
                _reader.Skip(next > 0 ? next : available.Length);
                continue;
            }

            var length = FrameLength(header);
            var data = _reader.Take(length);
            var sample = new MediaSample { Dts = _time, Duration = header.Samples, IsSync = true, Data = data };
            _time += header.Samples;
            return sample;
        }
    }

    /// <summary>Length of the frame at the reader's position: up to the next confirmed header, or the end of the audio.</summary>
    private int FrameLength(FlacFrameHeader header)
    {
        var expected = header.VariableBlockSize ? header.Number + header.Samples : header.Number + 1;
        var from = header.HeaderLength;
        while (true)
        {
            var remaining = _end - _reader.Position;
            var available = _reader.Available[..(int)Math.Min(_reader.Available.Length, remaining)];
            var next = FindNext(available, from, (header.VariableBlockSize, expected));
            if (next > 0)
                return next;
            if (available.Length >= remaining)
                return available.Length; // the last frame
            from = Math.Max(from, available.Length - Flac.MaxHeaderLength);
            _reader.Ensure((int)Math.Min(available.Length + ReadAhead, remaining));
        }
    }

    /// <summary>
    /// Offset of the next frame header in <paramref name="data"/> from <paramref name="from"/> (with the number it must
    /// carry, and the frame before it passing its CRC-16, when <paramref name="expected"/> is given); -1 when none yet.
    /// </summary>
    private int FindNext(ReadOnlySpan<byte> data, int from, (bool Variable, long Number)? expected)
    {
        // A header within the last bytes may be incomplete: the caller scans that part again with more data.
        for (var i = from; i + 6 <= data.Length; i++)
        {
            var at = data[i..].IndexOf((byte)0xFF);
            if (at < 0)
                return -1;
            i += at;
            if (i + 6 > data.Length)
                return -1;
            if ((data[i + 1] & 0xFE) != 0xF8 || Flac.ParseFrameHeader(data[i..], _info) is not { } h)
                continue;
            if (expected is { } e)
            {
                if (h.VariableBlockSize != e.Variable || h.Number != e.Number || Flac.Crc16(data[..i]) != 0)
                    continue;
            }

            return i;
        }

        return -1;
    }

    private static bool HasId3V1(Stream stream)
    {
        if (stream.Length < 128)
            return false;
        Span<byte> tag = stackalloc byte[3];
        stream.Position = stream.Length - 128;
        stream.ReadExactly(tag);
        return tag.SequenceEqual("TAG"u8);
    }

    public void Dispose() => _reader.Dispose();
}
