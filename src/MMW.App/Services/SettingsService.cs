using System.Text.Json;
using MMW.Core.Diagnostics;

namespace MMW.App.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }

    void Save();
}

/// <summary>Stores <see cref="AppSettings"/> as JSON in the per-user application data folder.</summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true };

    private readonly string _path;

    public SettingsService(string? path = null)
    {
        _path = path ?? Path.Combine(AppDataDirectory, "settings.json");
        Settings = Load();
    }

    public static string AppDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "MediaMetadataWizard");

    public AppSettings Settings { get; }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, s_options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Could not save settings", ex);
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), s_options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Error("Could not read settings; using defaults", ex);
        }

        return new AppSettings();
    }
}
