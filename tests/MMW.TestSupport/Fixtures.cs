using System.Diagnostics;

namespace MMW.TestSupport;

/// <summary>
/// Generates small media files with ffmpeg/mkvmerge for round-trip tests. Files are cached under
/// <c>tests/fixtures/generated</c>; tests that need a tool which is not installed are skipped.
/// </summary>
public static class Fixtures
{
    private static readonly Lock s_lock = new();

    public static string GeneratedDirectory { get; } = Path.Combine(FindRepoRoot(), "tests", "fixtures", "generated");

    public static bool HasTool(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(name, name.StartsWith("mkv", StringComparison.Ordinal) || name is "oggenc" or "flac" ? "--version" : "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p!.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Returns the path of a generated fixture, creating it with <paramref name="tool"/> if needed.</summary>
    public static string Get(string fileName, string tool, string arguments)
    {
        var path = Path.Combine(GeneratedDirectory, fileName);
        lock (s_lock)
        {
            if (File.Exists(path))
                return path;

            Directory.CreateDirectory(GeneratedDirectory);
            var tmp = Path.Combine(GeneratedDirectory, "tmp-" + Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
            Run(tool, arguments.Replace("{out}", Quote(tmp), StringComparison.Ordinal).Replace("{dir}", Quote(GeneratedDirectory), StringComparison.Ordinal));
            File.Move(tmp, path, overwrite: true);
            return path;
        }
    }

    /// <summary>Copies a fixture to a fresh temporary file the test may modify.</summary>
    public static string CopyToTemp(string path)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mmw-tests");
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        File.Copy(path, copy);
        return copy;
    }

    /// <summary>Runs a tool and returns its standard output; throws when it fails.</summary>
    public static string Run(string tool, string arguments)
    {
        var psi = new ProcessStartInfo(tool, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {tool}.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0 && !(tool == "mkvmerge" && p.ExitCode == 1))
            throw new InvalidOperationException($"{tool} {arguments} failed ({p.ExitCode}): {stderr.Result}");
        return stdout.Result;
    }

    public static string Quote(string s) => "\"" + s + "\"";

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaMuxingWizard.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }
}
