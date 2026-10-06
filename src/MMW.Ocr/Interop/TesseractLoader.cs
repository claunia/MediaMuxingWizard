using System.Runtime.InteropServices;
using MMW.Core.Diagnostics;

namespace MMW.Ocr.Interop;

/// <summary>
/// Finds and loads the Tesseract OCR shared library (libtesseract 5, C API) and resolves the functions used by
/// <see cref="TesseractApi"/>.
/// </summary>
/// <remarks>
/// <para>Search order (the first file that loads and exports the C API wins):</para>
/// <list type="number">
/// <item><c>&lt;AppContext.BaseDirectory&gt;/runtimes/&lt;rid&gt;/native</c></item>
/// <item><c>&lt;AppContext.BaseDirectory&gt;/tesseract</c></item>
/// <item>the directory in the <c>MMW_TESSERACT_PATH</c> environment variable</item>
/// <item>platform defaults: Linux <c>/usr/lib</c>, <c>/usr/lib64</c>, the multiarch directories, <c>/usr/local/lib</c>,
/// <c>/home/linuxbrew/.linuxbrew/lib</c>; macOS <c>/opt/homebrew/lib</c>, <c>/usr/local/lib</c>, <c>/opt/local/lib</c>;
/// Windows the executable's directory, <c>%ProgramFiles%\Tesseract-OCR</c> and <c>PATH</c></item>
/// <item>the system loader's own search (ld.so cache / <c>LD_LIBRARY_PATH</c> / <c>DYLD_LIBRARY_PATH</c> / <c>PATH</c>)</item>
/// </list>
/// <para>File names: Linux <c>libtesseract.so.5</c>; macOS <c>libtesseract.5.dylib</c>; Windows <c>tesseract55.dll</c>
/// … <c>tesseract50.dll</c> (vcpkg / CMake builds) and <c>libtesseract-5.dll</c> (MSYS2 / UB Mannheim builds). Its
/// dependencies (Leptonica, …) are resolved by the system loader, from the same directory on Windows.</para>
/// <para>Loading happens once, on first use, and never throws: <see cref="IsAvailable"/> and <see cref="Error"/> report
/// the outcome (also written to <see cref="AppLog"/>).</para>
/// </remarks>
public static class TesseractLoader
{
    private static readonly Lazy<LoadResult> s_result = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when libtesseract is loaded and exports the functions used.</summary>
    public static bool IsAvailable => s_result.Value.Error is null;

    /// <summary>Tesseract version string (e.g. "5.5.0"), or null when unavailable.</summary>
    public static string? Version => s_result.Value.Version;

    /// <summary>Why Tesseract is unavailable, or null.</summary>
    public static string? Error => s_result.Value.Error;

    /// <summary>The library file that was loaded (a bare file name when found by the system loader), or null.</summary>
    public static string? LibraryPath => s_result.Value.Path;

    /// <summary>The resolved C API, or null when unavailable.</summary>
    internal static TesseractFunctions? Functions => s_result.Value.Functions;

    /// <summary>Throws when Tesseract is unavailable.</summary>
    /// <exception cref="NotSupportedException">libtesseract could not be loaded.</exception>
    public static void EnsureAvailable()
    {
        if (!IsAvailable)
            throw new NotSupportedException($"Tesseract OCR is not available: {Error}");
    }

    /// <summary>Library file names for this platform, most specific first.</summary>
    public static IReadOnlyList<string> LibraryFileNames() =>
        OperatingSystem.IsWindows() ? ["tesseract55.dll", "tesseract54.dll", "tesseract53.dll", "tesseract52.dll", "tesseract51.dll", "tesseract50.dll", "libtesseract-5.dll"]
        : OperatingSystem.IsMacOS() ? ["libtesseract.5.dylib"]
        : ["libtesseract.so.5"];

    /// <summary>The directories searched, in order (see the remarks of <see cref="TesseractLoader"/>).</summary>
    public static IReadOnlyList<string> SearchDirectories()
    {
        var dirs = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        dirs.Add(Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"));
        dirs.Add(Path.Combine(baseDir, "runtimes", PortableRid(), "native"));
        dirs.Add(Path.Combine(baseDir, "tesseract"));
        if (Environment.GetEnvironmentVariable("MMW_TESSERACT_PATH") is { Length: > 0 } env)
            dirs.Add(env);

        if (OperatingSystem.IsWindows())
        {
            dirs.Add(baseDir);
            foreach (var pf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                if (Environment.GetFolderPath(pf) is { Length: > 0 } p)
                    dirs.Add(Path.Combine(p, "Tesseract-OCR"));
            }

            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }
        else if (OperatingSystem.IsMacOS())
        {
            dirs.AddRange(["/opt/homebrew/lib", "/usr/local/lib", "/opt/local/lib"]);
        }
        else
        {
            dirs.AddRange(
            [
                "/usr/lib", "/usr/lib64", "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/lib/x86_64-linux-gnu",
                "/lib/aarch64-linux-gnu", "/usr/local/lib", "/usr/local/lib64", "/home/linuxbrew/.linuxbrew/lib",
            ]);
            if (Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home)
                dirs.Add(Path.Combine(home, ".linuxbrew", "lib"));
        }

        return dirs.Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    private static string PortableRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            var a => a.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }

    private sealed record LoadResult(string? Version, string? Error, string? Path, TesseractFunctions? Functions);

    private static LoadResult Load()
    {
        try
        {
            var result = LoadCore();
            if (result.Error is null)
                AppLog.Info($"Tesseract {result.Version} loaded from {result.Path}.");
            else
                AppLog.Warn($"Tesseract OCR is not available (bitmap subtitle OCR is disabled): {result.Error}");
            return result;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException or IOException)
        {
            AppLog.Warn($"Tesseract could not be loaded: {ex.Message}");
            return new LoadResult(null, ex.Message, null, null);
        }
    }

    private static LoadResult LoadCore()
    {
        var names = LibraryFileNames();
        var failures = new List<string>();
        var candidates = SearchDirectories().SelectMany(d => names.Select(n => Path.Combine(d, n))).Where(File.Exists).Concat(names);
        foreach (var candidate in candidates)
        {
            if (!NativeLibrary.TryLoad(candidate, out var handle))
            {
                if (Path.IsPathRooted(candidate))
                    failures.Add(candidate);
                continue;
            }

            var functions = TesseractFunctions.Resolve(handle, out var missing);
            if (functions is null)
            {
                failures.Add($"{candidate} (missing {missing})");
                NativeLibrary.Free(handle);
                continue;
            }

            var version = functions.GetVersion();
            if (!IsSupportedVersion(version))
            {
                failures.Add($"{candidate} (version {version})");
                NativeLibrary.Free(handle);
                continue;
            }

            return new LoadResult(version, null, candidate, functions);
        }

        return new LoadResult(null, failures.Count > 0
            ? $"libtesseract could not be used: {string.Join(", ", failures)}"
            : $"the Tesseract 5 library ({string.Join(" / ", names)}) was not found; install Tesseract 5 or set MMW_TESSERACT_PATH", null, null);
    }

    /// <summary>True for Tesseract 4.1 and later (the C API used exists since 4.0; 5.x is the tested version).</summary>
    internal static bool IsSupportedVersion(string? version)
    {
        if (string.IsNullOrEmpty(version))
            return false;
        var digits = new string(version.TrimStart('v').TakeWhile(c => char.IsDigit(c)).ToArray());
        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major) && major >= 4;
    }
}
