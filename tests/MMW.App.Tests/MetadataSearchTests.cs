using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MMW.App.Services;
using MMW.App.ViewModels;
using MMW.App.Views;
using MMW.Core.Metadata;
using MMW.Metadata.Search;
using MMW.TestSupport;

namespace MMW.App.Tests;

public class MetadataSearchTests
{
    private sealed class FakeProvider : IMetadataProvider
    {
        public string Name => "Fake";

        public bool SupportsMovies => true;

        public bool SupportsTv => true;

        public bool IsConfigured => true;

        public IReadOnlyList<string> Languages => ["en", "fr"];

        public ProviderLanguageType LanguageType => ProviderLanguageType.Language;

        public string DefaultLanguage => "en";

        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<MetadataResult>> SearchMovieAsync(string title, int? year, string language, CancellationToken cancellationToken = default)
        {
            Queries.Add(title);
            var r = new MetadataResult(Name, MediaSearchKind.Movie);
            r.Set(MetadataTokens.Name, "The Fixture Movie");
            r.Set(MetadataTokens.ReleaseDate, "1999-03-31");
            return Task.FromResult<IReadOnlyList<MetadataResult>>([r]);
        }

        public Task<IReadOnlyList<string>> SearchSeriesNamesAsync(string partial, string language, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<MetadataResult>> SearchTvAsync(string seriesName, int? season, int? episode, string language, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MetadataResult>>([]);

        public Task<MetadataResult> LoadDetailsAsync(MetadataResult result, string language, CancellationToken cancellationToken = default)
        {
            var r = new MetadataResult(Name, MediaSearchKind.Movie) { IsDetailed = true };
            foreach (var (k, v) in result.Values)
                r.Set(k, v);
            r.Set(MetadataTokens.Genre, "Drama");
            r.Set(MetadataTokens.Cast, new[] { "Ana", "Bob" });
            r.Set(MetadataTokens.Director, new[] { "Dee" });
            r.Set(MetadataTokens.Description, "A movie used by the tests.");
            return Task.FromResult(r);
        }
    }

    [AvaloniaFact]
    public async Task Search_prefills_finds_previews_and_applies_with_undo()
    {
        var fixture = Path.Combine(Fixtures.GeneratedDirectory, "mp4-moov-end.mp4");
        if (!File.Exists(fixture))
            Assert.Skip("Run the MP4 format tests first to generate fixtures.");
        var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        var provider = new FakeProvider();
        var registry = new MetadataProviderRegistry([provider]) { DefaultMovieProviderName = "Fake", DefaultTvProviderName = "Fake" };
        using var service = new MetadataService(settings, new HttpClient(), dir, registry);
        var main = new MainWindowViewModel(new DocumentService(), new FakeDialogService(), settings, metadata: service);
        await main.OpenPathsAsync([Fixtures.CopyToTemp(fixture)]);
        var doc = main.Documents.Single();

        var search = new MetadataSearchViewModel(doc, service);
        Assert.False(search.IsTv);
        Assert.Equal("Fixture", search.MovieTitle); // from the file's Name tag
        Assert.Same(provider, search.SelectedProvider);

        await search.SearchCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        for (var i = 0; i < 50 && search.Preview.Count == 0; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(["Fixture"], provider.Queries);
        Assert.Contains(search.Preview, r => r.Tag == "Genre" && r.Value == "Drama");

        var window = new Window { Content = new MetadataSearchView { DataContext = search }, Width = 1020, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var shots = Environment.GetEnvironmentVariable("MMW_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "mmw-screenshots");
        Directory.CreateDirectory(shots);
        window.CaptureRenderedFrame()?.Save(Path.Combine(shots, "09-search.png"));

        await search.ApplyCommand.ExecuteAsync(null);
        var m = doc.Document.Metadata;
        Assert.Equal("The Fixture Movie", m.GetString(TagId.Name));
        Assert.Equal(["Ana", "Bob"], m.GetList(TagId.Cast));
        Assert.Equal(TagCatalog.MediaKindMovie, m.GetInt(TagId.MediaKind));
        Assert.True(m.GetInt(TagId.HdVideo) is null or 0); // 320×240 is not HD

        doc.UndoCommand.Execute(null);
        Assert.Equal("Fixture", doc.Document.Metadata.GetString(TagId.Name));
        Assert.False(doc.Document.Metadata.Contains(TagId.Cast));
    }
}
