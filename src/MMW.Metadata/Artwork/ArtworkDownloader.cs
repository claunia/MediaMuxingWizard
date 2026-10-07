using System.Globalization;
using System.Net.Http.Headers;
using CoreArtwork = MMW.Core.Metadata.Artwork;
using MMW.Core.Diagnostics;
using MMW.Core.Metadata;
using MMW.Metadata.Http;
using MMW.Metadata.Resources;
using MMW.Metadata.Search;

namespace MMW.Metadata.Artwork;

/// <summary>Downloads remote artwork into <see cref="CoreArtwork"/> instances (JPEG, PNG, BMP or GIF only).</summary>
public sealed class ArtworkDownloader
{
    /// <summary>Default maximum image size (25 MiB).</summary>
    public const long DefaultMaxBytes = 25L * 1024 * 1024;

    private readonly HttpClient _http;

    /// <summary>Creates the downloader.</summary>
    /// <param name="httpClient">Shared HTTP client.</param>
    /// <param name="maxBytes">Images larger than this are rejected.</param>
    public ArtworkDownloader(HttpClient httpClient, long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _http = httpClient;
        MaxBytes = maxBytes;
    }

    /// <summary>Size cap in bytes.</summary>
    public long MaxBytes { get; }

    /// <summary>Downloads an image.</summary>
    /// <exception cref="HttpRequestException">Network failure or non-success status.</exception>
    /// <exception cref="InvalidDataException">The response is too large or not a supported image format.</exception>
    public async Task<CoreArtwork> DownloadAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(ProviderHttp.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*", 0.5));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ProviderHttpException(string.Format(CultureInfo.CurrentCulture, Strings.Artwork_DownloadFailedStatus, (int)response.StatusCode, response.ReasonPhrase, ProviderHttp.Redact(url)), response.StatusCode);
        if (response.Content.Headers.ContentLength is { } length && length > MaxBytes)
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Artwork_TooLarge, length, MaxBytes));

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Artwork_LargerThanLimit, MaxBytes));
                buffer.Write(chunk, 0, read);
            }

            var data = buffer.ToArray();
            var format = ImageSniffer.Detect(data);
            if (format == ArtworkFormat.Unknown)
                throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Artwork_UnsupportedFormat, ProviderHttp.Redact(url), response.Content.Headers.ContentType?.MediaType ?? Strings.Artwork_UnknownType));
            return new CoreArtwork(data, format);
        }
    }

    /// <summary>Downloads the full-size image of a remote artwork.</summary>
    public Task<CoreArtwork> DownloadAsync(RemoteArtwork artwork, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        return DownloadAsync(artwork.FullUrl, cancellationToken);
    }

    /// <summary>Like <see cref="DownloadAsync(Uri, CancellationToken)"/> but logs failures and returns null.</summary>
    public async Task<CoreArtwork?> TryDownloadAsync(Uri url, CancellationToken cancellationToken = default)
    {
        try
        {
            return await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or OperationCanceledException or IOException)
        {
            AppLog.Warn(string.Format(CultureInfo.CurrentCulture, Strings.Artwork_DownloadFailed, ex.Message));
            return null;
        }
    }

    /// <summary>Downloads several artworks (full size), skipping the ones that fail.</summary>
    public async Task<IReadOnlyList<CoreArtwork>> DownloadAllAsync(IEnumerable<RemoteArtwork> artworks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artworks);
        var list = new List<CoreArtwork>();
        foreach (var art in artworks)
        {
            if (await TryDownloadAsync(art.FullUrl, cancellationToken).ConfigureAwait(false) is { } downloaded)
                list.Add(downloaded);
        }

        return list;
    }
}
