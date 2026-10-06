using Microsoft.Win32.SafeHandles;

namespace MMW.Core.Media;

/// <summary>Positional reader of a file, shared by the lazily loaded samples of a demuxer.</summary>
public sealed class FileSampleReader : ISampleDataReader, IDisposable
{
    private readonly SafeFileHandle _handle;

    public FileSampleReader(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(_handle);
    }

    public long Length { get; }

    public SafeFileHandle Handle => _handle;

    /// <inheritdoc />
    public void Read(long position, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var n = RandomAccess.Read(_handle, destination[total..], position + total);
            if (n == 0)
                throw new EndOfStreamException($"Unexpected end of file at offset {position + total}.");
            total += n;
        }
    }

    /// <summary>Reads up to <paramref name="count"/> bytes (fewer at the end of the file).</summary>
    public byte[] ReadAvailable(long position, int count)
    {
        if (position >= Length)
            return [];
        var n = (int)Math.Min(count, Length - position);
        var buffer = new byte[n];
        Read(position, buffer);
        return buffer;
    }

    public void Dispose() => _handle.Dispose();
}
