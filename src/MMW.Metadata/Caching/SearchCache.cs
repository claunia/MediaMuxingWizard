using System.Security.Cryptography;
using System.Text;

namespace MMW.Metadata.Caching;

/// <summary>
/// Simple on-disk cache of provider responses (search results and details), one file per request keyed by a
/// SHA-256 hash of the (secret-free) request key. Expiry uses the file modification time.
/// </summary>
public sealed class SearchCache
{
    private readonly TimeProvider _time;

    /// <summary>Creates a cache stored in <paramref name="directory"/> (created on demand).</summary>
    public SearchCache(string directory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Default location: <c>%LocalAppData%/MediaMuxingWizard/Cache/Metadata</c>.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(MMW.Core.AppDataFolders.Local, "Cache", "Metadata");

    /// <summary>Directory holding the cache files.</summary>
    public string Directory { get; }

    /// <summary>Returns the cached payload for <paramref name="key"/> when it is younger than <paramref name="maxAge"/>.</summary>
    public string? TryGet(string key, TimeSpan maxAge)
    {
        var path = PathFor(key);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            if (_time.GetUtcNow() - info.LastWriteTimeUtc > maxAge)
            {
                info.Delete();
                return null;
            }

            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (IOException)
        {
            return null; // another process is writing the entry: treat as a miss
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Stores <paramref name="payload"/> for <paramref name="key"/>.</summary>
    public void Set(string key, string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(key);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, payload, Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Number of cached entries.</summary>
    public int Count => System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, "*.json").Length : 0;

    /// <summary>Total size of the cache in bytes.</summary>
    public long SizeInBytes => System.IO.Directory.Exists(Directory)
        ? new DirectoryInfo(Directory).EnumerateFiles("*.json").Sum(f => f.Length)
        : 0;

    /// <summary>Deletes every cached entry.</summary>
    public void Clear()
    {
        if (!System.IO.Directory.Exists(Directory))
            return;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
        {
            if (file.EndsWith(".json", StringComparison.Ordinal) || file.EndsWith(".tmp", StringComparison.Ordinal))
                File.Delete(file);
        }
    }

    private string PathFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(Directory, hash + ".json");
    }
}
