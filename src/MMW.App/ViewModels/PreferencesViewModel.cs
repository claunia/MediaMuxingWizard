using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Services;
using MMW.Core.Metadata;
using MMW.Core.Metadata;

namespace MMW.App.ViewModels;

/// <summary>Preferences dialog; changes are applied when the user presses OK.</summary>
public sealed partial class PreferencesViewModel : DialogViewModel<bool>
{
    private readonly AppSettings _settings;
    private readonly MetadataService? _metadata;
    private ObservableCollection<MapEntryViewModel> _movieMap = [];
    private ObservableCollection<MapEntryViewModel> _tvMap = [];

    public PreferencesViewModel(AppSettings settings, MetadataService? metadata = null)
    {
        _settings = settings;
        _metadata = metadata;
        _defaultMovieProvider = settings.DefaultMovieProvider ?? metadata?.Registry.DefaultMovieProviderName;
        _defaultTvProvider = settings.DefaultTvProvider ?? metadata?.Registry.DefaultTvProviderName;
        _metadataOverwrite = settings.MetadataOverwrite;
        _metadataKeepEmpty = settings.MetadataKeepEmpty;
        _autodetect4K = settings.Autodetect4K;
        _replaceArtworkOnSearch = settings.ReplaceArtworkOnSearch;
        _tmdbApiKey = settings.TmdbApiKey ?? string.Empty;
        _mixdown = Mixdowns.First(m => m.Value == settings.Mixdown);
        _bitratePerChannel = settings.BitratePerChannel;
        _drc = settings.Drc;
        _convertAc3 = settings.ConvertAc3;
        _convertDts = settings.ConvertDts;
        _tvdbApiKey = settings.TvdbApiKey ?? string.Empty;
        if (metadata is not null)
        {
            _movieMap = new ObservableCollection<MapEntryViewModel>(metadata.Maps.Movie.Entries.Select(e => new MapEntryViewModel(e.Tag, e.Template)));
            _tvMap = new ObservableCollection<MapEntryViewModel>(metadata.Maps.Tv.Entries.Select(e => new MapEntryViewModel(e.Tag, e.Template)));
        }
        _theme = Themes.First(t => t.Value == settings.Theme);
        _rememberWindowSize = settings.RememberWindowSize;
        _createChapterPreviews = settings.CreateChapterPreviews;
        _chapterPreviewPosition = PreviewPositions.OrderBy(p => Math.Abs(p.Value - settings.ChapterPreviewPosition)).First();
        _ratingsCountry = settings.RatingsCountry;
        _use64BitOffsets = settings.Use64BitOffsets;
        _use64BitTimes = settings.Use64BitTimes;
        _optimizeOnSave = settings.OptimizeOnSave;
        _useFileNameFormat = settings.UseFileNameFormat;
        _movieFormat = settings.MovieFileNameFormat;
        _tvFormat = settings.TvFileNameFormat;
        Presets = new ObservableCollection<MetadataPreset>(settings.Presets);
        UpdatePreviews();
    }

    public override string Title => "Preferences";

    public static IReadOnlyList<Choice<ThemeChoice>> Themes { get; } =
        [new(ThemeChoice.System, "Follow the system"), new(ThemeChoice.Light, "Light"), new(ThemeChoice.Dark, "Dark")];

    public static IReadOnlyList<string> RatingCountries => Ratings.Countries;

    public static IReadOnlyList<string> Tokens => TagCatalog.All.Select(d => "{" + d.Name + "}").ToList();

    [ObservableProperty]
    private Choice<ThemeChoice> _theme;

    [ObservableProperty]
    private bool _rememberWindowSize;

    public static IReadOnlyList<Choice<double>> PreviewPositions { get; } = [new(0, "Beginning"), new(0.5, "Middle"), new(0.95, "End")];

    [ObservableProperty]
    private bool _createChapterPreviews;

    [ObservableProperty]
    private Choice<double> _chapterPreviewPosition;

    [ObservableProperty]
    private string _ratingsCountry;

    [ObservableProperty]
    private bool _use64BitOffsets;

    [ObservableProperty]
    private bool _use64BitTimes;

    [ObservableProperty]
    private bool _optimizeOnSave;

    [ObservableProperty]
    private bool _useFileNameFormat;

    [ObservableProperty]
    private string _movieFormat;

    [ObservableProperty]
    private string _tvFormat;

    [ObservableProperty]
    private string _moviePreview = string.Empty;

    [ObservableProperty]
    private string _tvPreview = string.Empty;

    public ObservableCollection<MetadataPreset> Presets { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemovePresetCommand), nameof(MovePresetUpCommand))]
    private MetadataPreset? _selectedPreset;

    partial void OnMovieFormatChanged(string value) => UpdatePreviews();

    partial void OnTvFormatChanged(string value) => UpdatePreviews();

    private void UpdatePreviews()
    {
        var movie = new MetadataSet();
        movie.Set(TagId.Name, "The Movie");
        movie.Set(TagId.ReleaseDate, "1999-03-31");
        movie.Set(TagId.MediaKind, TagCatalog.MediaKindMovie);
        var tv = new MetadataSet();
        tv.Set(TagId.Name, "Pilot");
        tv.Set(TagId.TvShow, "The Show");
        tv.Set(TagId.TvSeason, 1);
        tv.Set(TagId.TvEpisodeNumber, 2);
        tv.Set(TagId.MediaKind, TagCatalog.MediaKindTvShow);
        MoviePreview = (FileNameFormatter.Format(MovieFormat, movie) ?? "(empty)") + ".m4v";
        TvPreview = (FileNameFormatter.Format(TvFormat, tv) ?? "(empty)") + ".m4v";
    }

    [RelayCommand]
    private void RestoreFormats()
    {
        MovieFormat = FileNameFormatter.DefaultMovieFormat;
        TvFormat = FileNameFormatter.DefaultTvFormat;
    }

    private bool HasPreset() => SelectedPreset is not null;

    [RelayCommand(CanExecute = nameof(HasPreset))]
    private void RemovePreset() => Presets.Remove(SelectedPreset!);

    [RelayCommand(CanExecute = nameof(HasPreset))]
    private void MovePresetUp()
    {
        var i = Presets.IndexOf(SelectedPreset!);
        if (i > 0)
            Presets.Move(i, i - 1);
    }

    // ------------------------------------------------------------------ audio

    public static IReadOnlyList<Choice<MMW.Core.Media.AudioMixdown>> Mixdowns { get; } =
        Enum.GetValues<MMW.Core.Media.AudioMixdown>().Select(m => new Choice<MMW.Core.Media.AudioMixdown>(m, MMW.Core.Media.ConversionDefaults.MixdownName(m))).ToList();

    public static string FFmpegStatus => MMW.Media.Conversion.MediaConversion.IsAvailable
        ? $"FFmpeg {MMW.Media.Conversion.MediaConversion.Version} is available."
        : $"FFmpeg libraries were not found: {MMW.Media.Conversion.MediaConversion.Error}";

    [ObservableProperty]
    private Choice<MMW.Core.Media.AudioMixdown> _mixdown;

    [ObservableProperty]
    private int _bitratePerChannel;

    [ObservableProperty]
    private double _drc;

    [ObservableProperty]
    private bool _convertAc3;

    [ObservableProperty]
    private bool _convertDts;

    // ------------------------------------------------------------------ metadata

    public bool HasMetadata => _metadata is not null;

    public IReadOnlyList<string> MovieProviders => _metadata?.Registry.MovieProviders.Select(p => p.Name).ToList() ?? [];

    public IReadOnlyList<string> TvProviders => _metadata?.Registry.TvProviders.Select(p => p.Name).ToList() ?? [];

    [ObservableProperty]
    private string? _defaultMovieProvider;

    [ObservableProperty]
    private string? _defaultTvProvider;

    [ObservableProperty]
    private bool _metadataOverwrite;

    [ObservableProperty]
    private bool _metadataKeepEmpty;

    [ObservableProperty]
    private bool _autodetect4K;

    [ObservableProperty]
    private bool _replaceArtworkOnSearch;

    [ObservableProperty]
    private string _tmdbApiKey;

    [ObservableProperty]
    private string _tvdbApiKey;

    [ObservableProperty]
    private string _cacheStatus = string.Empty;

    /// <summary>Which map the editor shows: false = movies, true = TV shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MapEntries))]
    private bool _editTvMap;

    public ObservableCollection<MapEntryViewModel> MapEntries => EditTvMap ? _tvMap : _movieMap;

    public static IReadOnlyList<TagDefinition> MapTags => TagCatalog.All;

    public static IReadOnlyList<string> MapTokens => typeof(MMW.Metadata.Search.MetadataTokens)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.FieldType == typeof(string))
        .Select(f => "{" + (string)f.GetValue(null)! + "}")
        .ToList();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveMapEntryCommand))]
    private MapEntryViewModel? _selectedMapEntry;

    [RelayCommand]
    private void AddMapEntry()
    {
        var entry = new MapEntryViewModel(TagId.Comments, string.Empty);
        MapEntries.Add(entry);
        SelectedMapEntry = entry;
    }

    private bool HasMapEntry() => SelectedMapEntry is not null;

    [RelayCommand(CanExecute = nameof(HasMapEntry))]
    private void RemoveMapEntry() => MapEntries.Remove(SelectedMapEntry!);

    [RelayCommand]
    private void RestoreMap()
    {
        var map = MMW.Metadata.Mapping.MetadataMap.CreateDefault(EditTvMap ? MMW.Metadata.Search.MediaSearchKind.TvEpisode : MMW.Metadata.Search.MediaSearchKind.Movie);
        MapEntries.Clear();
        foreach (var e in map.Entries)
            MapEntries.Add(new MapEntryViewModel(e.Tag, e.Template));
    }

    [RelayCommand]
    private void ClearCache()
    {
        _metadata?.ProviderSettings.Cache?.Clear();
        _metadata?.RecentSearches.Clear();
        CacheStatus = "Cached results and recent searches deleted.";
    }

    [RelayCommand]
    private void Accept()
    {
        _settings.DefaultMovieProvider = DefaultMovieProvider;
        _settings.DefaultTvProvider = DefaultTvProvider;
        _settings.MetadataOverwrite = MetadataOverwrite;
        _settings.MetadataKeepEmpty = MetadataKeepEmpty;
        _settings.Autodetect4K = Autodetect4K;
        _settings.ReplaceArtworkOnSearch = ReplaceArtworkOnSearch;
        _settings.Mixdown = Mixdown.Value;
        _settings.BitratePerChannel = Math.Clamp(BitratePerChannel, MMW.Core.Media.AudioConversionSettings.MinBitratePerChannel, MMW.Core.Media.AudioConversionSettings.MaxBitratePerChannel);
        _settings.Drc = Math.Clamp(Drc, 0, MMW.Core.Media.AudioConversionSettings.MaxDrc);
        _settings.ConvertAc3 = ConvertAc3;
        _settings.ConvertDts = ConvertDts;
        _settings.ApplyConversionDefaults();
        _settings.TmdbApiKey = string.IsNullOrWhiteSpace(TmdbApiKey) ? null : TmdbApiKey.Trim();
        _settings.TvdbApiKey = string.IsNullOrWhiteSpace(TvdbApiKey) ? null : TvdbApiKey.Trim();
        if (_metadata is not null)
        {
            _metadata.Maps.Movie.Entries = _movieMap.Where(e => e.Template.Length > 0).Select(e => new MMW.Metadata.Mapping.MetadataMapEntry(e.Tag.Id, e.Template)).ToList();
            _metadata.Maps.Tv.Entries = _tvMap.Where(e => e.Template.Length > 0).Select(e => new MMW.Metadata.Mapping.MetadataMapEntry(e.Tag.Id, e.Template)).ToList();
            _metadata.SaveMaps();
        }

        _settings.Theme = Theme.Value;
        _settings.RememberWindowSize = RememberWindowSize;
        _settings.CreateChapterPreviews = CreateChapterPreviews;
        _settings.ChapterPreviewPosition = ChapterPreviewPosition.Value;
        _settings.RatingsCountry = RatingsCountry;
        _settings.Use64BitOffsets = Use64BitOffsets;
        _settings.Use64BitTimes = Use64BitTimes;
        _settings.OptimizeOnSave = OptimizeOnSave;
        _settings.UseFileNameFormat = UseFileNameFormat;
        _settings.MovieFileNameFormat = MovieFormat;
        _settings.TvFileNameFormat = TvFormat;
        _settings.Presets.Clear();
        _settings.Presets.AddRange(Presets);
        Close(true);
    }
}

/// <summary>One editable row of a metadata map: a tag and its {Token} template.</summary>
public sealed partial class MapEntryViewModel(TagId tag, string template) : ViewModelBase
{
    [ObservableProperty]
    private TagDefinition _tag = TagCatalog.Get(tag);

    [ObservableProperty]
    private string _template = template;
}
