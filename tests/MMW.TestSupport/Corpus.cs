namespace MMW.TestSupport;

/// <summary>
/// Optional real-world sample corpus. Set <c>MMW_CORPUS</c> to a directory of media files to enable corpus
/// tests; the files there are only ever read (write tests work on temporary copies).
/// </summary>
public static class Corpus
{
    public static string? Directory => Environment.GetEnvironmentVariable("MMW_CORPUS") is { Length: > 0 } d && System.IO.Directory.Exists(d) ? d : null;

    /// <summary>Corpus files with one of the given extensions, as theory data; a single empty entry when disabled.</summary>
    public static IEnumerable<string> Files(params string[] extensions)
    {
        if (Directory is not { } dir)
            return [string.Empty];

        var files = System.IO.Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal))
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Order(StringComparer.Ordinal)
            .ToList();
        return files.Count > 0 ? files : [string.Empty];
    }

    /// <summary>Skips the current test when the corpus is disabled.</summary>
    public static void Require(string file)
    {
        if (string.IsNullOrEmpty(file))
            Xunit.Assert.Skip("Set MMW_CORPUS to a directory of sample media to run corpus tests.");
    }
}
