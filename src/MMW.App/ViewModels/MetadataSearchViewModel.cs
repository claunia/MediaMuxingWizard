using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Resources;
using MMW.App.Services;
using MMW.Core.Diagnostics;
using MMW.Core.Metadata;
using MMW.Metadata.Search;

namespace MMW.App.ViewModels;

/// <summary>A tag/value row previewing what a search result will write.</summary>
public sealed record PreviewRow(string Tag, string Value);

/// <summary>An artwork offered by a provider, with a lazily downloaded thumbnail.</summary>
public sealed partial class RemoteArtworkViewModel(RemoteArtwork artwork) : ViewModelBase
{
    public RemoteArtwork Artwork { get; } = artwork;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isSelected;

    public string Description =>
        QueueViewModel.ArtworkKindName(Artwork.Kind) + (Artwork.Width is { } w && Artwork.Height is { } h ? $" · {w}×{h}" : string.Empty) +
        (Artwork.Season is { } s ? " · " + string.Format(CultureInfo.CurrentCulture, Strings.Search_SeasonFormat, s) : string.Empty) + $" · {Artwork.Provider}";
}

/// <summary>Searches online providers and applies the chosen result to a document (Subler's search sheet).</summary>
public sealed partial class MetadataSearchViewModel : DialogViewModel<bool>, IDisposable
{
    private readonly DocumentViewModel _document;
    private readonly MetadataService _service;
    private CancellationTokenSource? _detailsCts;
    private CancellationTokenSource? _searchCts;

    public MetadataSearchViewModel(DocumentViewModel document, MetadataService service)
    {
        _document = document;
        _service = service;
        _replaceArtworks = service.Settings.ReplaceArtworkOnSearch;

        var prefill = SearchPrefill.From(document.Document);
        _isTv = prefill.Kind == MediaSearchKind.TvEpisode;
        if (_isTv)
        {
            _seriesName = prefill.Title;
            _season = prefill.Season?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            _episode = prefill.Episode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
        else
        {
            _movieTitle = prefill.Title;
            _year = prefill.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        SelectProviderForKind();
    }

    public override string Title => Strings.Search_Title;

    // ------------------------------------------------------------------ query

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTab), nameof(Providers))]
    private bool _isTv;

    /// <summary>0 = movie, 1 = TV episode (bound to the tab control).</summary>
    public int SelectedTab
    {
        get => IsTv ? 1 : 0;
        set => IsTv = value == 1;
    }

    public IReadOnlyList<IMetadataProvider> Providers => _service.Registry.For(Kind);

    private MediaSearchKind Kind => IsTv ? MediaSearchKind.TvEpisode : MediaSearchKind.Movie;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Languages), nameof(ProviderWarning))]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private IMetadataProvider? _selectedProvider;

    public IReadOnlyList<string> Languages => SelectedProvider?.Languages ?? [];

    [ObservableProperty]
    private string? _selectedLanguage;

    public string? ProviderWarning => SelectedProvider is { IsConfigured: false } p
        ? string.Format(CultureInfo.CurrentCulture, Strings.Search_ProviderNeedsKeyFormat, p.Name)
        : null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _movieTitle = string.Empty;

    [ObservableProperty]
    private string _year = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _seriesName = string.Empty;

    [ObservableProperty]
    private string _season = string.Empty;

    [ObservableProperty]
    private string _episode = string.Empty;

    /// <summary>Recent series names for the autocomplete box.</summary>
    public IReadOnlyList<string> RecentSeries => _service.RecentSearches.Titles(MediaSearchKind.TvEpisode);

    /// <summary>Asks the provider for series names starting with <paramref name="text"/> (autocomplete).</summary>
    public async Task<IEnumerable<object>> SuggestSeriesAsync(string? text, CancellationToken cancellationToken)
    {
        var recent = RecentSeries.Where(r => text is null || r.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (SelectedProvider is null || string.IsNullOrWhiteSpace(text) || text.Length < 2)
            return recent;
        var remote = await SelectedProvider.SearchSeriesNamesAsync(text, SelectedLanguage ?? SelectedProvider.DefaultLanguage, cancellationToken);
        return recent.Concat(remote).Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToList();
    }

    partial void OnIsTvChanged(bool value) => SelectProviderForKind();

    partial void OnSelectedProviderChanged(IMetadataProvider? value)
    {
        SelectedLanguage = value is null ? null : _service.LanguageFor(value);
        Results.Clear();
    }

    private void SelectProviderForKind()
    {
        SelectedProvider = _service.Registry.DefaultFor(Kind) ?? (Providers.Count > 0 ? Providers[0] : null);
    }

    // ------------------------------------------------------------------ results

    public ObservableCollection<MetadataResult> Results { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private MetadataResult? _selectedResult;

    public ObservableCollection<PreviewRow> Preview { get; } = [];

    public ObservableCollection<RemoteArtworkViewModel> Artworks { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(ApplyCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Replace existing artwork with the selected images (otherwise they are added).</summary>
    [ObservableProperty]
    private bool _replaceArtworks;

    partial void OnSelectedResultChanged(MetadataResult? value) => _ = LoadDetailsAsync(value);

    private bool CanSearch() => !IsBusy && SelectedProvider is not null && (IsTv ? SeriesName.Trim().Length > 0 : MovieTitle.Trim().Length > 0);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task Search()
    {
        var provider = SelectedProvider!;
        var language = SelectedLanguage ?? provider.DefaultLanguage;
        _service.RememberLanguage(provider, language);
        var query = new SearchQuery(Kind, IsTv ? SeriesName.Trim() : MovieTitle.Trim(), ParseInt(Year), IsTv ? ParseInt(Season) : null, IsTv ? ParseInt(Episode) : null, language);

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        IsBusy = true;
        Status = string.Format(CultureInfo.CurrentCulture, IsTv ? Strings.Search_SearchingEpisodeFormat : Strings.Search_SearchingMovieFormat, provider.Name);
        Results.Clear();
        try
        {
            var results = await provider.SearchAsync(query, _searchCts.Token);
            foreach (var r in results)
                Results.Add(r);
            _service.RecentSearches.Add(query);
            Status = results.Count switch
            {
                0 => provider.IsConfigured ? Strings.Search_NoResults : ProviderWarning ?? Strings.Search_NoResults,
                1 => Strings.Search_OneResult,
                _ => string.Format(CultureInfo.CurrentCulture, Strings.Search_ResultsFormat, results.Count),
            };
            SelectedResult = Results.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadDetailsAsync(MetadataResult? result)
    {
        _detailsCts?.Cancel();
        Preview.Clear();
        foreach (var a in Artworks)
            a.Thumbnail?.Dispose();
        Artworks.Clear();
        if (result is null || SelectedProvider is not { } provider)
            return;

        var cts = _detailsCts = new CancellationTokenSource();
        Status = Strings.Search_LoadingDetails;
        try
        {
            var detailed = result.IsDetailed ? result : await provider.LoadDetailsAsync(result, SelectedLanguage ?? provider.DefaultLanguage, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            if (!ReferenceEquals(detailed, result))
            {
                var index = Results.IndexOf(result);
                if (index >= 0)
                {
                    Results[index] = detailed;
                    SelectedResult = detailed;
                    return;
                }
            }

            ShowPreview(detailed);
            Status = string.Format(CultureInfo.CurrentCulture, Strings.Search_DetailsFormat, detailed.DisplayTitle, detailed.Artworks.Count);
            await LoadThumbnailsAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ShowPreview(MetadataResult result)
    {
        var set = _service.Maps.For(result.Kind).Apply(result);
        foreach (var id in set.Keys)
            Preview.Add(new PreviewRow(TagCatalog.Get(id).DisplayName, MetadataSet.FormatValue(id, set[id]!)));

        var first = true;
        foreach (var art in result.Artworks)
        {
            Artworks.Add(new RemoteArtworkViewModel(art) { IsSelected = first && art.Kind is ArtworkKind.Poster or ArtworkKind.Season or ArtworkKind.Square });
            if (Artworks[^1].IsSelected)
                first = false;
        }

        if (first && Artworks.Count > 0)
            Artworks[0].IsSelected = true;
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadThumbnailsAsync(CancellationToken cancellationToken)
    {
        foreach (var item in Artworks.Take(40).ToList())
        {
            try
            {
                var bytes = await _service.Http.GetByteArrayAsync(item.Artwork.ThumbnailUrl, cancellationToken);
                using var ms = new MemoryStream(bytes);
                item.Thumbnail = Bitmap.DecodeToWidth(ms, 240);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                AppLog.Debug(string.Format(CultureInfo.CurrentCulture, Strings.Log_ThumbnailFailedFormat, item.Artwork.ThumbnailUrl, ex.Message));
            }
        }
    }

    private bool CanApply() => !IsBusy && SelectedResult is { IsDetailed: true };

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        var result = SelectedResult!;
        IsBusy = true;
        try
        {
            var set = _service.Maps.For(result.Kind).Apply(result);
            var chosen = Artworks.Where(a => a.IsSelected).Select(a => a.Artwork).ToList();
            if (chosen.Count > 0)
            {
                Status = Strings.Search_DownloadingArtwork;
                foreach (var art in await _service.Downloader.DownloadAllAsync(chosen))
                    set.Artworks.Add(art);
            }

            var options = _service.ApplyOptionsFor(result.Kind, ReplaceArtworks && set.Artworks.Count > 0);
            _document.ApplyMetadata(string.Format(CultureInfo.CurrentCulture, Strings.Undo_ApplyResultFormat, result.Provider), doc => MMW.Metadata.Mapping.MetadataApplier.Apply(doc, set, options));
            Close(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            Status = string.Format(CultureInfo.CurrentCulture, Strings.Search_CouldNotDownloadFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        _detailsCts?.Cancel();
        _detailsCts?.Dispose();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        foreach (var a in Artworks)
            a.Thumbnail?.Dispose();
    }

    private static int? ParseInt(string text) =>
        int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
}
