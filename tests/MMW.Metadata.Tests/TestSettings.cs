namespace MMW.Metadata.Tests;

internal static class TestSettings
{
    /// <summary>Settings with explicit keys (user override beats environment and file).</summary>
    public static ProviderSettings WithKeys(string tmdb = "tmdb-test-key", string? tvdb = null) => new()
    {
        UserApiKeys = new ApiKeys(tmdb, tvdb ?? "tvdb-" + Guid.NewGuid().ToString("N")),
        FileApiKeys = ApiKeys.Empty,
    };
}
