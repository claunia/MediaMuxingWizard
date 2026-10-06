using System.Net;
using System.Text;

namespace MMW.Metadata.Tests;

/// <summary>Fake HTTP handler serving recorded fixtures by URL substring rules, recording every request.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _rules = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string?> Bodies { get; } = [];

    /// <summary>Serves the fixture file for requests whose path+query contains every fragment.</summary>
    public FakeHttpHandler OnJson(string fixture, params string[] fragments) =>
        On(r => Matches(r, fragments), _ => Json(Fixture.Read(fixture)));

    public FakeHttpHandler OnStatus(HttpStatusCode status, params string[] fragments) =>
        On(r => Matches(r, fragments), _ => new HttpResponseMessage(status) { Content = new StringContent("{\"status\":\"failure\"}") });

    public FakeHttpHandler OnBytes(byte[] data, string contentType, params string[] fragments) =>
        On(r => Matches(r, fragments), _ =>
        {
            var content = new ByteArrayContent(data);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

    public FakeHttpHandler On(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _rules.Add((match, respond));
        return this;
    }

    public int Count(params string[] fragments) => Requests.Count(r => Matches(r, fragments));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        foreach (var (match, respond) in _rules)
        {
            if (match(request))
                return respond(request);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"no fixture for {request.RequestUri}") };
    }

    private static bool Matches(HttpRequestMessage request, string[] fragments)
    {
        var text = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        return fragments.All(f => text.Contains(f, StringComparison.Ordinal));
    }

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal static class Fixture
{
    public static string Directory { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string Path(string name) => System.IO.Path.Combine(Directory, name.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public static string Read(string name) => File.ReadAllText(Path(name));
}

/// <summary>Opt-in live tests: run only with MMW_LIVE_TESTS=1.</summary>
internal static class Live
{
    public static void RequireEnabled()
    {
        if (Environment.GetEnvironmentVariable("MMW_LIVE_TESTS") != "1")
            Assert.Skip("Live tests are disabled (set MMW_LIVE_TESTS=1 to run them).");
    }
}
