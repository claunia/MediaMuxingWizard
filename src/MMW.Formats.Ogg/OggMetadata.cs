using System.Buffers.Binary;
using MMW.Core.Media.Codecs;
using MMW.Core.Metadata;

namespace MMW.Formats.Ogg;

/// <summary>
/// The Vorbis comments of an Ogg file: the comment header of its first Opus ("OpusTags"), Vorbis (type 3) or FLAC
/// (VORBIS_COMMENT and PICTURE header packets) stream. Only the pages holding the headers are read.
/// </summary>
public static class OggMetadata
{
    /// <summary>Pages read at most while looking for the comment headers.</summary>
    private const int MaxPages = 512;

    /// <summary>The tags and pictures of an Ogg file; null when it has no comment header.</summary>
    public static MetadataSet? Read(string path)
    {
        using var stream = File.OpenRead(path);
        var packets = new Dictionary<uint, List<byte[]>>();
        var pending = new Dictionary<uint, List<byte>>();
        var kinds = new Dictionary<uint, string>();
        Span<byte> header = stackalloc byte[27];
        var segments = new byte[255];
        for (var page = 0; page < MaxPages; page++)
        {
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !header.StartsWith("OggS"u8))
                return null;
            var serial = BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
            var count = header[26];
            stream.ReadExactly(segments, 0, count);
            var bodyLength = 0;
            for (var i = 0; i < count; i++)
                bodyLength += segments[i];
            var body = new byte[bodyLength];
            stream.ReadExactly(body);

            if (!pending.TryGetValue(serial, out var current))
            {
                pending[serial] = current = [];
                packets[serial] = [];
            }

            if ((header[5] & 1) == 0)
                current.Clear();
            var at = 0;
            for (var i = 0; i < count; i++)
            {
                current.AddRange(body.AsSpan(at, segments[i]).ToArray());
                at += segments[i];
                if (segments[i] == 255)
                    continue;
                packets[serial].Add(current.ToArray());
                current.Clear();
                if (Comments(serial, packets[serial], kinds) is { } metadata)
                    return metadata;
            }
        }

        return null;
    }

    /// <summary>The metadata once a stream's comment headers are complete; null while they are not.</summary>
    private static MetadataSet? Comments(uint serial, List<byte[]> packets, Dictionary<uint, string> kinds)
    {
        var first = packets[0];
        if (!kinds.ContainsKey(serial))
        {
            kinds[serial] = first.AsSpan().StartsWith("OpusHead"u8) ? "opus"
                : first.Length > 7 && first[0] == 1 && first.AsSpan(1, 6).SequenceEqual("vorbis"u8) ? "vorbis"
                : first.Length > 13 && first[0] == 0x7F && first.AsSpan(1, 4).SequenceEqual("FLAC"u8) ? "flac"
                : "other";
        }

        switch (kinds[serial])
        {
            case "opus" when packets.Count >= 2 && packets[1].AsSpan().StartsWith("OpusTags"u8):
                return VorbisComments.ToMetadata(VorbisComments.Parse(packets[1].AsSpan(8)).Fields);
            case "vorbis" when packets.Count >= 2 && packets[1].Length > 7 && packets[1][0] == 3:
                return VorbisComments.ToMetadata(VorbisComments.Parse(packets[1].AsSpan(7)).Fields);
            case "flac" when packets.Count >= 2:
            {
                // Header packets after the mapping header are metadata blocks until the one with the last-block flag (an
                // audio frame, 0xFF…, also ends them).
                if (packets[^1] is not { Length: > 0 } last || (last[0] & 0x80) == 0)
                    return null;
                var blocks = packets.Skip(1).Where(p => p.Length >= 4 && (p[0] & 0x7F) != 0x7F).ToList();
                var fields = blocks.Where(b => (b[0] & 0x7F) == Flac.VorbisCommentType).SelectMany(b => VorbisComments.Parse(b.AsSpan(4)).Fields).ToList();
                var pictures = blocks.Where(b => (b[0] & 0x7F) == Flac.PictureType).Select(b => b[4..]);
                return VorbisComments.ToMetadata(fields, pictures);
            }

            default:
                return null;
        }
    }
}
