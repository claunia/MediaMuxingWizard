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

    /// <summary>The container last chosen for a new document made from dropped track files ("mp4" or "mkv").</summary>
    public string NewDocumentFormat { get; set; } = "mp4";

    public double WindowWidth { get; set; } = 1100;

    public double WindowHeight { get; set; } = 760;

    public bool RememberWindowSize { get; set; } = true;

    public bool ShowOpenDialogAtLaunch { get; set; }

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    /// <summary>User interface language ("en", "es"…); null follows the system. Applied at startup.</summary>
    public string? UiCulture { get; set; }

    /// <summary>Country whose content ratings are offered (USA is always offered).</summary>
    public string RatingsCountry { get; set; } = "USA";

    public List<MetadataPreset> Presets { get; set; } = [];

    // Saving
    public bool Use64BitOffsets { get; set; }

    public bool Use64BitTimes { get; set; }

    public bool OptimizeOnSave { get; set; }

    /// <summary>
    /// Store AV1 Dolby Vision profile 10.0 with the 'dav1' sample entry Dolby's specification requires, instead of
    /// 'av01' (the default, because most players cannot read 'dav1').
    /// </summary>
    public bool DolbyVisionAv1UsesDav1 { get; set; }

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

    // OCR
    /// <summary>Tesseract language for OCR ("eng", "fra+eng"…); null derives it from each track's language.</summary>
    public string? OcrLanguage { get; set; }

    // Chapters
    /// <summary>Create missing chapter preview images when saving MP4 files.</summary>
    public bool CreateChapterPreviews { get; set; }

    /// <summary>Where in each chapter the preview is taken: 0 = beginning, 0.5 = middle, 1 = end.</summary>
    public double ChapterPreviewPosition { get; set; }

    // Audio conversion
    public MMW.Core.Media.AudioMixdown Mixdown { get; set; } = MMW.Core.Media.AudioMixdown.DolbyProLogicII;

    public int BitratePerChannel { get; set; } = MMW.Core.Media.AudioConversionSettings.DefaultBitratePerChannel;

    public double Drc { get; set; }

    /// <summary>Suggest converting AC-3 to AAC when importing into MP4.</summary>
    public bool ConvertAc3 { get; set; }

    /// <summary>Suggest "AAC + Passthru" for DTS when importing into MP4.</summary>
    public bool ConvertDts { get; set; } = true;

    /// <summary>Pushes the conversion and muxing preferences to the shared defaults used by the importer and muxers.</summary>
    public void ApplyConversionDefaults()
    {
        MMW.Formats.Mp4.Boxes.DolbyVisionEntry.Av1UsesDav1 = DolbyVisionAv1UsesDav1;
        MMW.Core.Media.ConversionDefaults.Settings = new MMW.Core.Media.AudioConversionSettings { Mixdown = Mixdown, BitratePerChannel = BitratePerChannel, Drc = Drc };
        MMW.Core.Media.ConversionDefaults.ConvertAc3 = ConvertAc3;
        MMW.Core.Media.ConversionDefaults.ConvertDts = ConvertDts;
    }

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
