using System.Net;

namespace MMW.Ocr.Tests;

/// <summary>Language model lookup and downloads (fake HTTP), and the language table.</summary>
public sealed class TessdataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mmw-ocr-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>A plausible traineddata file: entry count 24 followed by filler.</summary>
    private static byte[] Model(int size = 200_000)
    {
        var data = new byte[size];
        data[0] = 24;
        for (var i = 4; i < data.Length; i++)
            data[i] = (byte)(i * 7);
        return data;
    }

    private TessdataManager Manager(HttpMessageHandler? handler = null, string sub = "user") =>
        new(Path.Combine(_root, sub), handler is null ? null : new HttpClient(handler), new Uri("https://example.test/models"), includeSystemDirectories: false);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Bytes(byte[] data, bool length = true)
    {
        var content = new ByteArrayContent(data);
        if (!length)
            content.Headers.ContentLength = null;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class Recorder : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    [Fact]
    public async Task Download_installs_the_model_atomically_with_progress()
    {
        var model = Model(300_000);
        var handler = new Handler(_ => Bytes(model));
        var manager = Manager(handler);
        Assert.False(manager.IsInstalled("fra"));

        var progress = new Recorder();
        var path = await manager.DownloadAsync("fra", progress, Ct);

        Assert.Equal(new Uri("https://example.test/models/fra.traineddata"), Assert.Single(handler.Requests));
        Assert.Equal(Path.Combine(manager.Directory, "fra.traineddata"), path);
        Assert.Equal(model, await File.ReadAllBytesAsync(path, Ct));
        Assert.True(manager.IsInstalled("fra"));
        Assert.True(manager.IsDownloaded("fra"));
        Assert.Equal(path, manager.FindFile("fra"));
        Assert.Contains(manager.InstalledLanguages(), l => l.Code == "fra");
        Assert.Equal(1.0, progress.Values[^1]);
        Assert.True(progress.Values.Count > 2);
        Assert.Equal(progress.Values.Order(), progress.Values);
        Assert.Single(Directory.GetFiles(manager.Directory)); // no temporary file left

        Assert.True(manager.Delete("fra"));
        Assert.False(manager.IsInstalled("fra"));
        Assert.False(manager.Delete("fra"));
    }

    [Fact]
    public async Task Download_without_a_content_length_still_works()
    {
        var manager = Manager(new Handler(_ => Bytes(Model(), length: false)));
        await manager.DownloadAsync("deu", null, Ct);
        Assert.True(manager.IsInstalled("deu"));
    }

    [Fact]
    public async Task Html_or_tiny_responses_are_rejected_and_nothing_is_left()
    {
        var html = System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE html><html>" + new string(' ', 100_000) + "</html>");
        var manager = Manager(new Handler(_ => Bytes(html)));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DownloadAsync("spa", null, Ct));
        Assert.False(manager.IsInstalled("spa"));
        Assert.Empty(Directory.GetFiles(manager.Directory));

        var tiny = Manager(new Handler(_ => Bytes(Model(1000))), "tiny");
        await Assert.ThrowsAsync<InvalidDataException>(() => tiny.DownloadAsync("spa", null, Ct));
        Assert.Empty(Directory.GetFiles(tiny.Directory));
    }

    [Fact]
    public async Task Http_errors_and_cancellation_leave_nothing_behind()
    {
        var missing = Manager(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<HttpRequestException>(() => missing.DownloadAsync("xyz", null, Ct));
        Assert.Empty(Directory.GetFiles(missing.Directory));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = Manager(new Handler(_ => Bytes(Model())), "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.DownloadAsync("ita", null, cts.Token));
        Assert.False(cancelled.IsInstalled("ita"));
    }

    [Fact]
    public async Task A_failed_download_keeps_the_previous_model()
    {
        var calls = 0;
        var good = Model();
        var manager = Manager(new Handler(_ => ++calls == 1 ? Bytes(good) : Bytes(new byte[100_000])));
        await manager.DownloadAsync("por", null, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DownloadAsync("por", null, Ct));
        Assert.Equal(good, await File.ReadAllBytesAsync(manager.FindFile("por")!, Ct));
    }

    [Theory]
    [InlineData("../eng")]
    [InlineData("eng/x")]
    [InlineData("")]
    [InlineData("e ng")]
    public void Invalid_codes_are_rejected(string code)
    {
        var manager = Manager();
        Assert.Throws<ArgumentException>(() => manager.FindFile(code));
        Assert.False(manager.IsInstalled(code));
        Assert.False(TessdataManager.IsValidCode(code));
    }

    [Fact]
    public void Language_sets_are_resolved_to_one_directory()
    {
        var manager = Manager();
        var other = Path.Combine(_root, "bundled");
        Directory.CreateDirectory(manager.Directory);
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(manager.Directory, "fra.traineddata"), Model());
        File.WriteAllBytes(Path.Combine(other, "eng.traineddata"), Model());

        // Only the user directory is searched by this manager.
        Assert.False(manager.IsInstalled("eng+fra"));
        Assert.Null(manager.ResolveDataDirectory("eng+fra"));
        Assert.Equal(manager.Directory, manager.ResolveDataDirectory("fra"));

        // A manager that also searches the bundled directory copies English next to French for "eng+fra".
        var combined = new TessdataManager(manager.Directory, includeSystemDirectories: false, additionalDirectories: [other]);
        Assert.True(combined.IsInstalled("eng+fra"));
        Assert.Equal(other, combined.ResolveDataDirectory("eng"));
        Assert.Equal(manager.Directory, combined.ResolveDataDirectory("eng+fra"));
        Assert.True(File.Exists(Path.Combine(manager.Directory, "eng.traineddata")));
    }

    [Fact]
    public void Default_directory_is_under_application_data()
    {
        Assert.EndsWith(Path.Combine("MediaMuxingWizard", "tessdata"), TessdataManager.DefaultDirectory, StringComparison.Ordinal);
        Assert.Equal("https://github.com/tesseract-ocr/tessdata_fast/raw/main/", TessdataManager.DefaultBaseUri.AbsoluteUri);
        var manager = new TessdataManager();
        Assert.Equal(TessdataManager.DefaultDirectory, manager.Directory);
        Assert.Contains(Path.Combine(AppContext.BaseDirectory, "tessdata"), manager.SearchDirectories);
    }

    [Theory]
    [InlineData("en", "eng")]
    [InlineData("en-US", "eng")]
    [InlineData("eng", "eng")]
    [InlineData("fr", "fra")]
    [InlineData("fre", "fra")]
    [InlineData("de", "deu")]
    [InlineData("ger", "deu")]
    [InlineData("es-419", "spa")]
    [InlineData("pt-BR", "por")]
    [InlineData("zh", "chi_sim")]
    [InlineData("zh-Hans", "chi_sim")]
    [InlineData("zh-Hant", "chi_tra")]
    [InlineData("zh-TW", "chi_tra")]
    [InlineData("yue", "chi_tra")]
    [InlineData("chi_tra", "chi_tra")]
    [InlineData("ja", "jpn")]
    [InlineData("ko", "kor")]
    [InlineData("ru", "rus")]
    [InlineData("ar", "ara")]
    [InlineData("he", "heb")]
    [InlineData("sr", "srp")]
    [InlineData("sr-Latn", "srp_latn")]
    [InlineData("nb", "nor")]
    [InlineData("no", "nor")]
    [InlineData("fa", "fas")]
    [InlineData("ms", "msa")]
    [InlineData("tl", "fil")]
    [InlineData("el", "ell")]
    [InlineData("nl", "nld")]
    [InlineData("cs", "ces")]
    public void Track_languages_map_to_models(string tag, string expected) => Assert.Equal(expected, TesseractLanguages.FromTrackLanguage(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("und")]
    [InlineData("zxx")]
    [InlineData("tlh")]
    public void Unknown_track_languages_have_no_model(string? tag) => Assert.Null(TesseractLanguages.FromTrackLanguage(tag));

    [Fact]
    public void Language_list_has_names_and_unique_codes()
    {
        Assert.True(TesseractLanguages.All.Count >= 100);
        Assert.Equal(TesseractLanguages.All.Count, TesseractLanguages.All.Select(l => l.Code).Distinct().Count());
        Assert.All(TesseractLanguages.All, l => Assert.True(TessdataManager.IsValidCode(l.Code) && l.Name.Length > 0));
        Assert.Equal("English", TesseractLanguages.Find("eng")!.Name);
        Assert.Equal("English + French", TesseractLanguages.DisplayName("eng+fra"));
        Assert.Equal(["eng", "fra"], TesseractLanguages.Split(" eng + fra "));
    }
}
