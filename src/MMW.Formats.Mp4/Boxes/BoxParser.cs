using System.Buffers.Binary;

namespace MMW.Formats.Mp4.Boxes;

/// <summary>Parses in-memory box trees (used for <c>moov</c> and other small top-level boxes).</summary>
public static class BoxParser
{
    private static readonly HashSet<string> s_containers =
    [
        "moov", "trak", "edts", "mdia", "minf", "dinf", "stbl", "udta", "tref", "ilst", "mvex", "moof", "traf",
        "gmhd", "wave", "sinf", "schi", "rinf", "tapt",
    ];

    private static readonly HashSet<string> s_visualEntries =
    [
        "avc1", "avc2", "avc3", "avc4", "hvc1", "hev1", "dvh1", "dvhe", "dva1", "dvav", "dav1", "av01", "vvc1", "vvi1",
        "mp4v", "jpeg", "png ", "encv", "s263", "vp08", "vp09", "apcn", "apch", "apcs", "apco", "ap4h", "ap4x",
    ];

    private static readonly HashSet<string> s_audioEntries =
    [
        "mp4a", "ac-3", "ec-3", "ac-4", "alac", "dtsc", "dtsh", "dtsl", "dtse", "dtsx", "Opus", "fLaC", "enca", ".mp3",
        "lpcm", "sowt", "twos", "ipcm", "fpcm", "samr", "mlpa",
    ];

    public static bool IsVisualSampleEntry(string type) => s_visualEntries.Contains(type);

    public static bool IsAudioSampleEntry(string type) => s_audioEntries.Contains(type);

    /// <summary>Parses a complete box (header included) from <paramref name="data"/>.</summary>
    public static Box ParseSingle(ReadOnlySpan<byte> data)
    {
        var list = ParseList(data, parentType: null);
        if (list.Count != 1)
            throw new InvalidDataException($"Expected one box, found {list.Count}.");
        return list[0];
    }

    /// <summary>Parses a sequence of boxes. Trailing garbage shorter than a header is ignored.</summary>
    public static List<Box> ParseList(ReadOnlySpan<byte> data, string? parentType)
    {
        var result = new List<Box>();
        var pos = 0;
        while (data.Length - pos >= 8)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
            var type = Box.Latin1.GetString(data.Slice(pos + 4, 4));
            var header = 8;
            if (size == 1)
            {
                if (data.Length - pos < 16)
                    throw new InvalidDataException($"Truncated large-size header for '{type}'.");
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(pos + 8)..]);
                header = 16;
            }
            else if (size == 0)
            {
                size = data.Length - pos;
            }

            // Some writers terminate udta/ilst with a 32-bit zero; tolerate it.
            if (size < header || size > data.Length - pos)
            {
                if (size == 0 || BinaryPrimitives.ReadUInt32BigEndian(data[pos..]) == 0)
                    break;
                throw new InvalidDataException($"Box '{type}' at {pos} has invalid size {size}.");
            }

            byte[]? userType = null;
            if (type == "uuid")
            {
                userType = data.Slice(pos + header, 16).ToArray();
                header += 16;
            }

            var body = data.Slice(pos + header, (int)size - header);
            result.Add(ParseBody(type, body, userType, parentType));
            pos += (int)size;
        }

        return result;
    }

    private static Box ParseBody(string type, ReadOnlySpan<byte> body, byte[]? userType, string? parentType)
    {
        var prefix = ContainerPrefixLength(type, body, parentType);
        if (prefix >= 0 && prefix <= body.Length)
        {
            try
            {
                var children = ParseList(body[prefix..], type);
                return new Box(type, body[..prefix].ToArray(), children) { UserType = userType };
            }
            catch (InvalidDataException)
            {
                // Not really a container (vendor-specific layout): keep it as an opaque leaf.
            }
        }

        return new Box(type, body.ToArray()) { UserType = userType };
    }

    /// <summary>Bytes before the children of a container box, or -1 when the box is a leaf.</summary>
    private static int ContainerPrefixLength(string type, ReadOnlySpan<byte> body, string? parentType)
    {
        if (s_containers.Contains(type))
            return 0;

        // iTunes metadata items: every child of ilst is a container of data/mean/name boxes.
        if (parentType == "ilst")
            return 0;

        switch (type)
        {
            case "meta":
                // ISO meta is a FullBox (version/flags, then children); QuickTime meta starts directly with a
                // child, so bytes 4..8 are that child's type instead of part of a size field.
                return body.Length >= 8 && LooksLikeBoxType(body.Slice(4, 4)) ? 0 : 4;
            case "stsd":
                return 8;
            case "dref":
                return 8;
        }

        if (parentType == "stsd")
        {
            if (s_visualEntries.Contains(type))
                return 78;
            if (s_audioEntries.Contains(type))
                return AudioEntryPrefix(body);
            return type switch
            {
                "tx3g" => 38,
                "wvtt" or "c608" or "c708" or "stpp" or "mp4s" or "subp" => 8,
                _ => -1,
            };
        }

        return -1;
    }

    private static int AudioEntryPrefix(ReadOnlySpan<byte> body)
    {
        if (body.Length < 28)
            return -1;
        var version = BinaryPrimitives.ReadUInt16BigEndian(body[8..]);
        return version switch
        {
            1 => 44,
            2 => 64,
            _ => 28,
        };
    }

    private static bool LooksLikeBoxType(ReadOnlySpan<byte> type)
    {
        foreach (var b in type)
        {
            if (b is < 0x20 or > 0x7E)
                return false;
        }

        return true;
    }
}
