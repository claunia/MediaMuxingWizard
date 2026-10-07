using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json;
using MMW.Core.Diagnostics;
using MMW.Metadata.Caching;
using MMW.Metadata.Resources;

namespace MMW.Metadata.Http;

/// <summary>Shared HTTP helper for providers: JSON GET/POST, response caching, rate limiting and error guarding.</summary>
internal sealed class ProviderHttp
{
    private readonly HttpClient _http;
    private readonly ProviderSettings _settings;
    private readonly RequestRateLimiter _limiter;

    public ProviderHttp(HttpClient http, ProviderSettings settings, string providerName, RequestRateLimiter? limiter = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(settings);
        _http = http;
        _settings = settings;
        ProviderName = providerName;
        _limiter = limiter ?? RequestRateLimiter.Unlimited;
    }

    public string ProviderName { get; }

    public static string UserAgent { get; } = $"MediaMuxingWizard/{typeof(ProviderHttp).Assembly.GetName().Version?.ToString(3) ?? "0.1"}";

    /// <summary>GETs and deserialises JSON. <paramref name="cacheKey"/> must not contain secrets.</summary>
    public async Task<T?> GetJsonAsync<T>(Uri uri, JsonTypeInfo<T> typeInfo, string? cacheKey, Action<HttpRequestMessage>? configure, CancellationToken cancellationToken)
    {
        var text = await GetStringAsync(uri, cacheKey, configure, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(text, typeInfo);
    }

    /// <summary>GETs text, using the response cache when <paramref name="cacheKey"/> is given.</summary>
    public async Task<string> GetStringAsync(Uri uri, string? cacheKey, Action<HttpRequestMessage>? configure, CancellationToken cancellationToken)
    {
        SearchCache? cache = _settings.Cache;
        var fullKey = cacheKey is null ? null : $"{ProviderName}|{cacheKey}";
        if (cache is not null && fullKey is not null && cache.TryGet(fullKey, _settings.CacheDuration) is { } cached)
            return cached;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        configure?.Invoke(request);
        var text = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (cache is not null && fullKey is not null)
            cache.Set(fullKey, text);
        return text;
    }

    /// <summary>Sends a request and returns the body, throwing <see cref="ProviderHttpException"/> on failure.</summary>
    public async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers.UserAgent.Count == 0)
            request.Headers.UserAgent.ParseAdd(UserAgent);
        if (request.Headers.Accept.Count == 0)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderHttpException(
                string.Format(CultureInfo.CurrentCulture, Strings.Provider_HttpError, ProviderName, (int)response.StatusCode, response.ReasonPhrase, Redact(request.RequestUri)),
                response.StatusCode);
        }

        return body;
    }

    /// <summary>
    /// Runs a provider operation, converting any failure (except caller cancellation) into a logged warning and
    /// <paramref name="fallback"/>.
    /// </summary>
    public async Task<T> GuardAsync<T>(string operation, Func<Task<T>> body, T fallback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn(string.Format(CultureInfo.CurrentCulture, Strings.Provider_OperationFailed, ProviderName, operation, ex.Message));
            return fallback;
        }
    }

    /// <summary>Removes credentials from a URL before it is logged.</summary>
    public static string Redact(Uri? uri)
    {
        if (uri is null)
            return string.Empty;
        var text = uri.GetLeftPart(UriPartial.Path);
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
            return text;
        var parts = query.Split('&').Select(p =>
            p.StartsWith("api_key=", StringComparison.OrdinalIgnoreCase) || p.StartsWith("apikey=", StringComparison.OrdinalIgnoreCase)
                ? p[..(p.IndexOf('=', StringComparison.Ordinal) + 1)] + "***"
                : p);
        return text + "?" + string.Join('&', parts);
    }
}
