using System.Collections.Concurrent;

namespace MMW.Metadata.Providers.TheTvDb;

/// <summary>Process-wide cache of TheTVDB bearer tokens (valid for one month; refreshed after ~29 days or on 401).</summary>
internal static class TvdbTokenCache
{
    /// <summary>How long a token is reused before logging in again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(29);

    private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> s_tokens = new(StringComparer.Ordinal);

    public static string? Get(string key, DateTimeOffset now) =>
        s_tokens.TryGetValue(key, out var entry) && entry.Expires > now ? entry.Token : null;

    public static void Set(string key, string token, DateTimeOffset now) => s_tokens[key] = (token, now + Lifetime);

    public static void Invalidate(string key) => s_tokens.TryRemove(key, out _);

    public static void Clear() => s_tokens.Clear();
}
