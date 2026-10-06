using MMW.Core.Diagnostics;
using MMW.Metadata.Providers.TheMovieDb;
using MMW.Metadata.Providers.TheTvDb;

namespace MMW.Metadata.Tests.Infrastructure;

[Collection(EnvironmentCollection.Name)]
public sealed class ApiKeysTests
{
    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "mmw-appsettings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Load_ReadsApiKeysSection()
    {
        var path = WriteTemp("""
            {
              // comments are tolerated
              "Logging": { "Level": "Info" },
              "ApiKeys": { "TheMovieDb": " tmdb-file ", "TheTvDb": "tvdb-file", "FanartTv": "" }
            }
            """);
        try
        {
            var keys = ApiKeys.Load(path);

            Assert.Equal("tmdb-file", keys.TheMovieDb);
            Assert.Equal("tvdb-file", keys.TheTvDb);
            Assert.Equal(string.Empty, keys.FanartTv);
            Assert.True(keys.HasTheMovieDb);
            Assert.False(keys.HasFanartTv);
            Assert.DoesNotContain("tmdb-file", keys.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFileOrSection_GivesEmptyKeys()
    {
        Assert.Equal(ApiKeys.Empty, ApiKeys.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".json")));

        var path = WriteTemp("""{ "Other": { "TheMovieDb": "x" } }""");
        try
        {
            Assert.Equal(ApiKeys.Empty, ApiKeys.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_DefaultPath_DoesNotThrow() => Assert.NotNull(ApiKeys.Load());

    [Fact]
    public void Load_MalformedFile_LogsWarningAndGivesEmptyKeys()
    {
        var path = WriteTemp("{ \"ApiKeys\": { \"TheMovieDb\": ");
        try
        {
            Assert.Equal(ApiKeys.Empty, ApiKeys.Load(path));
            Assert.Contains(AppLog.Snapshot(), e => e.Level == LogLevel.Warning && e.Message.Contains(path, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WithOverrides_OnlyReplacesNonEmptyKeys()
    {
        var file = new ApiKeys("file-tmdb", "file-tvdb", "file-fanart");

        var merged = file.WithOverrides(new ApiKeys(TheTvDb: "user-tvdb"));

        Assert.Equal(new ApiKeys("file-tmdb", "user-tvdb", "file-fanart"), merged);
        Assert.Same(file, file.WithOverrides(null));
    }

    [Fact]
    public void Resolve_PrecedenceIsUserThenEnvironmentThenFile()
    {
        var oldTmdb = Environment.GetEnvironmentVariable(ApiKeys.TmdbEnvironmentVariable);
        var oldTvdb = Environment.GetEnvironmentVariable(ApiKeys.TvdbEnvironmentVariable);
        try
        {
            var file = new ApiKeys("file-tmdb", "file-tvdb");

            Environment.SetEnvironmentVariable(ApiKeys.TmdbEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(ApiKeys.TvdbEnvironmentVariable, null);
            Assert.Equal(file, ApiKeys.Resolve(null, file));

            Environment.SetEnvironmentVariable(ApiKeys.TmdbEnvironmentVariable, "env-tmdb");
            Environment.SetEnvironmentVariable(ApiKeys.TvdbEnvironmentVariable, "env-tvdb");
            Assert.Equal(new ApiKeys("env-tmdb", "env-tvdb"), ApiKeys.Resolve(null, file));

            Assert.Equal(new ApiKeys("user-tmdb", "env-tvdb"), ApiKeys.Resolve(new ApiKeys(TheMovieDb: "user-tmdb"), file));

            var settings = new ProviderSettings { FileApiKeys = file, UserApiKeys = new ApiKeys(TheTvDb: "user-tvdb") };
            Assert.Equal(new ApiKeys("env-tmdb", "user-tvdb"), settings.ApiKeys);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApiKeys.TmdbEnvironmentVariable, oldTmdb);
            Environment.SetEnvironmentVariable(ApiKeys.TvdbEnvironmentVariable, oldTvdb);
        }
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", false)]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiIwMTIzNDU2Nzg5YWJjZGVmIiwic3ViIjoiNjAwMCJ9.c2lnbmF0dXJlLXNpZ25hdHVyZS1zaWduYXR1cmU", true)]
    [InlineData(null, false)]
    public void DetectsTmdbBearerTokens(string? credential, bool expected) =>
        Assert.Equal(expected, ApiKeys.IsTmdbBearerToken(credential));

    [Fact]
    public async Task ProvidersWithoutKeys_ReturnNothingAndSendNoRequests()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ApiKeys.TmdbEnvironmentVariable)) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ApiKeys.TvdbEnvironmentVariable)))
        {
            Assert.Skip("API key environment variables are set on this machine.");
        }

        var handler = new FakeHttpHandler();
        var settings = new ProviderSettings { FileApiKeys = ApiKeys.Empty };
        var tmdb = new TmdbProvider(new HttpClient(handler), settings);
        var tvdb = new TvdbProvider(new HttpClient(handler), settings);

        Assert.False(tmdb.IsConfigured);
        Assert.False(tvdb.IsConfigured);
        Assert.Empty(await tmdb.SearchMovieAsync("The Matrix", null, "en", TestContext.Current.CancellationToken));
        Assert.Empty(await tvdb.SearchTvAsync("Lost", 1, 1, "eng", TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
        Assert.Contains(AppLog.Snapshot(), e => e.Message.Contains("no API key configured", StringComparison.Ordinal));
    }
}
