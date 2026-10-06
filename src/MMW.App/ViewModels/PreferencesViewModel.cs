using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Services;
using MMW.Core.Metadata;

namespace MMW.App.ViewModels;

/// <summary>Preferences dialog; changes are applied when the user presses OK.</summary>
public sealed partial class PreferencesViewModel : DialogViewModel<bool>
{
    private readonly AppSettings _settings;

    public PreferencesViewModel(AppSettings settings)
    {
        _settings = settings;
        _theme = Themes.First(t => t.Value == settings.Theme);
        _rememberWindowSize = settings.RememberWindowSize;
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

    [RelayCommand]
    private void Accept()
    {
        _settings.Theme = Theme.Value;
        _settings.RememberWindowSize = RememberWindowSize;
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
