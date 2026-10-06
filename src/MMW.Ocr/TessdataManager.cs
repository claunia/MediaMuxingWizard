using System.Buffers.Binary;
using MMW.Core.Diagnostics;

namespace MMW.Ocr;

/// <summary>
/// Finds and downloads Tesseract language models (<c>&lt;code&gt;.traineddata</c>).
/// </summary>
/// <remarks>
/// <para>Models are looked for, in order, in <see cref="Directory"/> (where downloads go; by default
/// <c>&lt;ApplicationData&gt;/MediaMetadataWizard/tessdata</c>), the bundled <c>&lt;AppContext.BaseDirectory&gt;/tessdata</c>
/// (the application ships English there), the directory in <c>TESSDATA_PREFIX</c>, and the system's tessdata
/// directories (unless disabled).</para>
/// <para>Downloads come from <see cref="DefaultBaseUri"/>, the <c>tessdata_fast</c> repository: integer LSTM models,
/// a few MB per language and several times faster than <c>tessdata_best</c> with nearly the same accuracy on clean
/// rendered subtitle text, which matters when a film has over a thousand cues. A file is written next to its
/// destination and moved into place once complete and validated (size and header), so a cancelled or failed download
/// never leaves a truncated model behind.</para>
/// </remarks>
public sealed class TessdataManager
{
    /// <summary>Model file extension.</summary>
    public const string Extension = ".traineddata";

    /// <summary>Smallest plausible model size (the smallest official fast model is several hundred KB).</summary>
    public const long MinimumSize = 64 * 1024;

    /// <summary>Largest accepted model size.</summary>
    public const long MaximumSize = 512L * 1024 * 1024;

    /// <summary>Base address of the official fast models: <c>{base}/{code}.traineddata</c>.</summary>
    public static Uri DefaultBaseUri { get; } = new("https://github.com/tesseract-ocr/tessdata_fast/raw/main/");

    /// <summary>The per-user model directory: <c>&lt;ApplicationData&gt;/MediaMetadataWizard/tessdata</c>.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "MediaMetadataWizard", "tessdata");

    private static readonly Lazy<HttpClient> s_sharedClient = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MediaMetadataWizard");
        return client;
    });

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly List<string> _searchDirectories;

    /// <summary>Creates a manager.</summary>
    /// <param name="directory">Directory downloads are stored in (created on demand); null for <see cref="DefaultDirectory"/>.</param>
    /// <param name="httpClient">Client used for downloads; null for a shared one.</param>
    /// <param name="baseUri">Where models are downloaded from; null for <see cref="DefaultBaseUri"/>.</param>
    /// <param name="includeSystemDirectories">Also use the bundled, <c>TESSDATA_PREFIX</c> and system tessdata directories.</param>
    /// <param name="additionalDirectories">More directories to search, after <paramref name="directory"/> and before the system ones.</param>
    public TessdataManager(string? directory = null, HttpClient? httpClient = null, Uri? baseUri = null, bool includeSystemDirectories = true,
        IEnumerable<string>? additionalDirectories = null)
    {
        Directory = Path.GetFullPath(directory ?? DefaultDirectory);
        _http = httpClient ?? s_sharedClient.Value;
        var b = baseUri ?? DefaultBaseUri;
        _baseUri = b.AbsoluteUri.EndsWith('/') ? b : new Uri(b.AbsoluteUri + "/");
        _searchDirectories = [Directory];
        if (additionalDirectories is not null)
            _searchDirectories.AddRange(additionalDirectories.Where(d => !string.IsNullOrWhiteSpace(d)).Select(Path.GetFullPath));
        if (includeSystemDirectories)
            _searchDirectories.AddRange(SystemDirectories());
        _searchDirectories = _searchDirectories.Distinct(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Directory downloads are stored in.</summary>
    public string Directory { get; }

    /// <summary>Directories searched for models, in order.</summary>
    public IReadOnlyList<string> SearchDirectories => _searchDirectories;

    /// <summary>The bundled, <c>TESSDATA_PREFIX</c> and platform tessdata directories.</summary>
    public static IReadOnlyList<string> SystemDirectories()
    {
        var dirs = new List<string> { Path.Combine(AppContext.BaseDirectory, "tessdata") };
        if (Environment.GetEnvironmentVariable("TESSDATA_PREFIX") is { Length: > 0 } prefix)
        {
            dirs.Add(prefix);
            dirs.Add(Path.Combine(prefix, "tessdata"));
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var pf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                if (Environment.GetFolderPath(pf) is { Length: > 0 } p)
                    dirs.Add(Path.Combine(p, "Tesseract-OCR", "tessdata"));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            dirs.AddRange(["/opt/homebrew/share/tessdata", "/usr/local/share/tessdata", "/opt/local/share/tessdata"]);
        }
        else
        {
            dirs.AddRange(
            [
                "/usr/share/tessdata", "/usr/share/tesseract-ocr/5/tessdata", "/usr/share/tesseract-ocr/4.00/tessdata",
                "/usr/local/share/tessdata", "/home/linuxbrew/.linuxbrew/share/tessdata",
            ]);
        }

        return dirs;
    }

    /// <summary>Path of the model for <paramref name="language"/> (a single code), or null when it is not installed.</summary>
    public string? FindFile(string language)
    {
        ValidateCode(language);
        foreach (var dir in _searchDirectories)
        {
            var path = Path.Combine(dir, language + Extension);
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length >= MinimumSize)
                    return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Debug($"Cannot inspect '{path}': {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>True when every language of <paramref name="languages"/> ("eng" or "eng+fra") is installed.</summary>
    public bool IsInstalled(string languages)
    {
        var codes = TesseractLanguages.Split(languages ?? throw new ArgumentNullException(nameof(languages)));
        return codes.Count > 0 && codes.All(c => IsValidCode(c) && FindFile(c) is not null);
    }

    /// <summary>True when the model of <paramref name="language"/> is in <see cref="Directory"/> (i.e. it was downloaded and can be deleted).</summary>
    public bool IsDownloaded(string language)
    {
        ValidateCode(language);
        var path = Path.Combine(Directory, language + Extension);
        return File.Exists(path) && new FileInfo(path).Length >= MinimumSize;
    }

    /// <summary>The installed languages of <see cref="TesseractLanguages.All"/>.</summary>
    public IReadOnlyList<TesseractLanguage> InstalledLanguages() => TesseractLanguages.All.Where(l => FindFile(l.Code) is not null).ToList();

    /// <summary>
    /// A directory holding the models of every language of <paramref name="languages"/>, as Tesseract needs a single
    /// data path. When they are installed in different directories, the missing ones are copied into
    /// <see cref="Directory"/>. Null when a language is not installed.
    /// </summary>
    public string? ResolveDataDirectory(string languages)
    {
        var codes = TesseractLanguages.Split(languages ?? throw new ArgumentNullException(nameof(languages)));
        if (codes.Count == 0 || !codes.All(IsValidCode))
            return null;
        var files = codes.Select(FindFile).ToList();
        if (files.Any(f => f is null))
            return null;
        var dirs = files.Select(f => Path.GetDirectoryName(f)!).Distinct(StringComparer.Ordinal).ToList();
        if (dirs.Count == 1)
            return dirs[0];

        System.IO.Directory.CreateDirectory(Directory);
        foreach (var (code, file) in codes.Zip(files))
        {
            var target = Path.Combine(Directory, code + Extension);
            if (!File.Exists(target))
            {
                var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
                File.Copy(file!, temp);
                File.Move(temp, target, overwrite: true);
            }
        }

        return Directory;
    }

    /// <summary>
    /// Downloads the model of <paramref name="language"/> into <see cref="Directory"/> (replacing an existing download).
    /// </summary>
    /// <param name="language">A Tesseract language code (see <see cref="TesseractLanguages.All"/>).</param>
    /// <param name="progress">Receives the fraction downloaded (0–1; only when the server reports the size).</param>
    /// <param name="cancellationToken">Cancels the download (nothing is left behind).</param>
    /// <returns>The path of the installed model.</returns>
    /// <exception cref="ArgumentException">The code is not a valid language name.</exception>
    /// <exception cref="HttpRequestException">The download failed.</exception>
    /// <exception cref="InvalidDataException">The downloaded file is not a plausible traineddata file.</exception>
    public async Task<string> DownloadAsync(string language, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateCode(language);
        System.IO.Directory.CreateDirectory(Directory);
        var target = Path.Combine(Directory, language + Extension);
        var temp = Path.Combine(Directory, "." + language + Extension + "." + Guid.NewGuid().ToString("N")[..8] + ".part");
        var uri = new Uri(_baseUri, Uri.EscapeDataString(language) + Extension);
        AppLog.Info($"Downloading the {TesseractLanguages.DisplayName(language)} OCR model from {uri}.");
        try
        {
            using (var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                if (total is > MaximumSize)
                    throw new InvalidDataException($"The {language} model is too large ({total} bytes).");

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long written = 0;
                double lastReported = -1;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > MaximumSize)
                        throw new InvalidDataException($"The {language} model is larger than {MaximumSize} bytes.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (progress is not null && total is > 0)
                    {
                        var p = Math.Min(1.0, (double)written / total.Value);
                        if (p - lastReported >= 0.01)
                        {
                            lastReported = p;
                            progress.Report(p);
                        }
                    }
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (total is { } expected && written != expected)
                    throw new InvalidDataException($"The {language} model download is incomplete ({written} of {expected} bytes).");
            }

            Validate(temp, language);
            File.Move(temp, target, overwrite: true);
            progress?.Report(1.0);
            AppLog.Info($"Installed the {TesseractLanguages.DisplayName(language)} OCR model ({new FileInfo(target).Length / 1024} KB).");
            return target;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Deletes a downloaded model from <see cref="Directory"/>; false when it was not there.</summary>
    public bool Delete(string language)
    {
        ValidateCode(language);
        var path = Path.Combine(Directory, language + Extension);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Checks that <paramref name="path"/> looks like a traineddata file: plausible size and a component table header
    /// (a little-endian entry count between 1 and 64).
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a plausible model.</exception>
    internal static void Validate(string path, string language)
    {
        var info = new FileInfo(path);
        if (info.Length < MinimumSize)
            throw new InvalidDataException($"The {language} model is too small ({info.Length} bytes); the server did not return a traineddata file.");
        Span<byte> header = stackalloc byte[4];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            fs.ReadExactly(header);
        var entries = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (entries is < 1 or > 64)
            throw new InvalidDataException($"The {language} model is not a traineddata file.");
    }

    /// <summary>True for a syntactically valid language code (letters, digits and '_' only: no paths).</summary>
    public static bool IsValidCode(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length <= 32 && code.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');

    private static void ValidateCode(string language)
    {
        if (!IsValidCode(language))
            throw new ArgumentException($"'{language}' is not a Tesseract language code.", nameof(language));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not delete '{path}': {ex.Message}");
        }
    }
}
