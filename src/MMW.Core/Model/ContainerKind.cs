namespace MMW.Core.Model;

public enum ContainerKind
{
    Unknown,

    /// <summary>ISO base media file (MP4, M4V, M4A, M4B, M4R, MOV).</summary>
    Mp4,

    /// <summary>Matroska or WebM (MKV, MKA, MKS, WEBM).</summary>
    Matroska,
}

public static class ContainerKinds
{
    private static readonly string[] s_mp4 = [".mp4", ".m4v", ".m4a", ".m4b", ".m4r", ".mov", ".qt", ".3gp"];
    private static readonly string[] s_mkv = [".mkv", ".mka", ".mks", ".mk3d", ".webm"];

    public static IReadOnlyList<string> Mp4Extensions => s_mp4;

    public static IReadOnlyList<string> MatroskaExtensions => s_mkv;

    public static ContainerKind FromPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (s_mp4.Contains(ext))
            return ContainerKind.Mp4;
        if (s_mkv.Contains(ext))
            return ContainerKind.Matroska;
        return ContainerKind.Unknown;
    }

    /// <summary>Detects the container from the first bytes of the file.</summary>
    public static ContainerKind Sniff(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
            return ContainerKind.Matroska;
        if (header.Length >= 8)
        {
            var type = header.Slice(4, 4);
            if (type.SequenceEqual("ftyp"u8) || type.SequenceEqual("moov"u8) || type.SequenceEqual("mdat"u8) ||
                type.SequenceEqual("free"u8) || type.SequenceEqual("wide"u8) || type.SequenceEqual("skip"u8))
                return ContainerKind.Mp4;
        }

        return ContainerKind.Unknown;
    }
}
