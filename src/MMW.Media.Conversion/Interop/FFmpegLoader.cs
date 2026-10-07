using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using MMW.Core.Diagnostics;
using MMW.Media.Conversion.Resources;

namespace MMW.Media.Conversion.Interop;

/// <summary>
/// Finds and loads the FFmpeg shared libraries (libavutil, libswresample, libswscale, libavcodec, libavformat) the
/// FFmpeg.AutoGen bindings were generated for, and checks their major versions.
/// </summary>
/// <remarks>
/// <para>Search order (the first directory that holds every library with the expected major version wins):</para>
/// <list type="number">
/// <item><c>&lt;AppContext.BaseDirectory&gt;/runtimes/&lt;rid&gt;/native</c></item>
/// <item><c>&lt;AppContext.BaseDirectory&gt;/ffmpeg</c></item>
/// <item>the directory in the <c>MMW_FFMPEG_PATH</c> environment variable</item>
/// <item>platform defaults: Linux <c>/usr/lib</c>, <c>/usr/lib64</c>, the multiarch directories, <c>/usr/local/lib</c>,
/// <c>/home/linuxbrew/.linuxbrew/lib</c>; macOS <c>/opt/homebrew/lib</c>, <c>/usr/local/lib</c>; Windows the
/// executable's directory and <c>PATH</c></item>
/// <item>the system loader's own search (ld.so cache / <c>LD_LIBRARY_PATH</c> / <c>DYLD_LIBRARY_PATH</c> / <c>PATH</c>)</item>
/// </list>
/// <para>Loading happens once, on first use, and never throws: <see cref="IsAvailable"/> and <see cref="Error"/>
/// report the outcome (also written to <see cref="AppLog"/>).</para>
/// </remarks>
public static class FFmpegLoader
{
    /// <summary>The libraries used, in load (dependency) order.</summary>
    private static readonly string[] s_libraries = ["avutil", "swresample", "swscale", "avcodec", "avformat"];

    private static readonly Lazy<LoadResult> s_result = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when the libraries are loaded and match the bindings.</summary>
    public static bool IsAvailable => s_result.Value.Error is null;

    /// <summary>FFmpeg version string (e.g. "8.1.2"), or null when unavailable.</summary>
    public static string? Version => s_result.Value.Version;

    /// <summary>Why FFmpeg is unavailable, or null.</summary>
    public static string? Error => s_result.Value.Error;

    /// <summary>Directory the libraries were loaded from (empty when found by the system loader), or null.</summary>
    public static string? LibraryDirectory => s_result.Value.Directory;

    /// <summary>FFmpeg release the bindings are generated for (FFmpeg.AutoGen's major version).</summary>
    public static int BindingsMajor => typeof(ffmpeg).Assembly.GetName().Version?.Major ?? 0;

    /// <summary>Library major versions expected by the bindings ("avcodec" → 62, …).</summary>
    public static IReadOnlyDictionary<string, int> ExpectedVersions => s_libraries.ToDictionary(l => l, l => ffmpeg.LibraryVersionMap[l]);

    /// <summary>Throws when FFmpeg is unavailable.</summary>
    /// <exception cref="NotSupportedException">FFmpeg could not be loaded.</exception>
    public static void EnsureAvailable()
    {
        if (!IsAvailable)
            throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_NotAvailable, Error));
    }

    /// <summary>The directories searched, in order (see the remarks of <see cref="FFmpegLoader"/>).</summary>
    public static IReadOnlyList<string> SearchDirectories()
    {
        var dirs = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        dirs.Add(Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"));
        dirs.Add(Path.Combine(baseDir, "runtimes", PortableRid(), "native"));
        dirs.Add(Path.Combine(baseDir, "ffmpeg"));
        if (Environment.GetEnvironmentVariable("MMW_FFMPEG_PATH") is { Length: > 0 } env)
            dirs.Add(env);

        if (OperatingSystem.IsWindows())
        {
            dirs.Add(baseDir);
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

    /// <summary>File name of a library for this platform ("libavcodec.so.62", "libavcodec.62.dylib", "avcodec-62.dll").</summary>
    public static string LibraryFileName(string library, int major) =>
        OperatingSystem.IsWindows() ? $"{library}-{major}.dll"
        : OperatingSystem.IsMacOS() ? $"lib{library}.{major}.dylib"
        : $"lib{library}.so.{major}";

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

    private sealed record LoadResult(string? Version, string? Error, string? Directory);

    private static LoadResult Load()
    {
        try
        {
            var result = LoadCore();
            if (result.Error is null)
                AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_Loaded, result.Version, result.Directory is { Length: > 0 } d ? d : Strings.FFmpeg_SystemLibraryPath));
            else
                AppLog.Warn(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_NotAvailableFeaturesDisabled, result.Error));
            return result;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException or IOException)
        {
            AppLog.Warn(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_CouldNotLoad, ex.Message));
            return new LoadResult(null, ex.Message, null);
        }
    }

    private static LoadResult LoadCore()
    {
        var names = s_libraries.ToDictionary(l => l, l => LibraryFileName(l, ffmpeg.LibraryVersionMap[l]));
        var handles = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        string? directory = null;
        var tried = new List<string>();

        foreach (var dir in SearchDirectories())
        {
            if (!names.Values.All(n => File.Exists(Path.Combine(dir, n))))
                continue;
            tried.Add(dir);
            if (TryLoadAll(names, n => Path.Combine(dir, n), handles))
            {
                directory = dir;
                break;
            }
        }

        if (directory is null && TryLoadAll(names, n => n, handles))
            directory = string.Empty;

        if (directory is null)
        {
            var expected = string.Join(", ", names.Values);
            return new LoadResult(null, tried.Count > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_LibrariesNotLoaded, string.Join(", ", tried))
                : string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_LibrariesNotFound, BindingsMajor, expected), null);
        }

        ffmpeg.RootPath = directory;
        DynamicallyLoadedBindings.FunctionResolver = new PreloadedFunctionResolver(handles);
        DynamicallyLoadedBindings.Initialize();

        var actual = new Dictionary<string, uint>
        {
            ["avutil"] = ffmpeg.avutil_version(),
            ["swresample"] = ffmpeg.swresample_version(),
            ["swscale"] = ffmpeg.swscale_version(),
            ["avcodec"] = ffmpeg.avcodec_version(),
            ["avformat"] = ffmpeg.avformat_version(),
        };
        var mismatch = actual.Where(kv => (int)(kv.Value >> 16) != ffmpeg.LibraryVersionMap[kv.Key])
            .Select(kv => string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_LibraryVersionMismatch, kv.Key, kv.Value >> 16, ffmpeg.LibraryVersionMap[kv.Key]))
            .ToList();
        if (mismatch.Count > 0)
            return new LoadResult(null, string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_IncompatibleLibraries, string.Join(", ", mismatch)), directory);

        FFmpegLog.Install();
        // Release builds report their tag ("n9.0.1"): shown without the "n".
        var version = ffmpeg.av_version_info();
        if (version is ['n', >= '0' and <= '9', ..])
            version = version[1..];
        return new LoadResult(version, null, directory);
    }

    private static bool TryLoadAll(Dictionary<string, string> names, Func<string, string> path, Dictionary<string, IntPtr> handles)
    {
        var loaded = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        foreach (var library in s_libraries)
        {
            if (!NativeLibrary.TryLoad(path(names[library]), out var handle))
            {
                foreach (var h in loaded.Values)
                    NativeLibrary.Free(h);
                return false;
            }

            loaded[library] = handle;
        }

        foreach (var (k, v) in loaded)
            handles[k] = v;
        return true;
    }

    /// <summary>Resolves binding functions from the library handles loaded by <see cref="FFmpegLoader"/>.</summary>
    private sealed class PreloadedFunctionResolver(Dictionary<string, IntPtr> handles) : IFunctionResolver
    {
        public T GetFunctionDelegate<T>(string libraryName, string functionName, bool throwOnError = true)
        {
            if (handles.TryGetValue(libraryName, out var handle) && NativeLibrary.TryGetExport(handle, functionName, out var address))
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            if (throwOnError)
                throw new EntryPointNotFoundException(string.Format(CultureInfo.CurrentCulture, Strings.FFmpeg_FunctionNotFound, functionName, libraryName));
            return default!;
        }
    }
}
