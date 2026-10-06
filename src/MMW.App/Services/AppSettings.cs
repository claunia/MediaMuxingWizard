using MMW.Core.Metadata;

namespace MMW.App.Services;

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

/// <summary>User preferences persisted between sessions.</summary>
public sealed class AppSettings
{
    public List<string> RecentFiles { get; set; } = [];

    public double WindowWidth { get; set; } = 1100;

    public double WindowHeight { get; set; } = 760;

    public bool RememberWindowSize { get; set; } = true;

    public bool ShowOpenDialogAtLaunch { get; set; }

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    /// <summary>Country whose content ratings are offered (USA is always offered).</summary>
    public string RatingsCountry { get; set; } = "USA";

    public List<MetadataPreset> Presets { get; set; } = [];

    // Saving
    public bool Use64BitOffsets { get; set; }

    public bool Use64BitTimes { get; set; }

    public bool OptimizeOnSave { get; set; }

    public bool LogIncludesDate { get; set; }

    // Metadata search
    public string? DefaultMovieProvider { get; set; }

    public string? DefaultTvProvider { get; set; }

    /// <summary>Last language used per provider name.</summary>
    public Dictionary<string, string> ProviderLanguages { get; set; } = [];

    /// <summary>Overwrite existing tags with search results.</summary>
    public bool MetadataOverwrite { get; set; } = true;

    /// <summary>Keep existing values for mapped tags the result does not provide.</summary>
    public bool MetadataKeepEmpty { get; set; } = true;

    public bool Autodetect4K { get; set; }

    /// <summary>Replace existing artwork with the artwork chosen in the search window.</summary>
    public bool ReplaceArtworkOnSearch { get; set; } = true;

    /// <summary>User overrides for the provider API keys (empty = use appsettings.json or the environment).</summary>
    public string? TmdbApiKey { get; set; }

    public string? TvdbApiKey { get; set; }

    // File naming
    public bool UseFileNameFormat { get; set; } = true;

    public string MovieFileNameFormat { get; set; } = FileNameFormatter.DefaultMovieFormat;

    public string TvFileNameFormat { get; set; } = FileNameFormatter.DefaultTvFormat;

    public const int MaxRecentFiles = 15;

    public void AddRecent(string path)
    {
        RecentFiles.Remove(path);
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles)
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }
}
