namespace MMW.Core;

/// <summary>The per-user folders where the application keeps its settings, queue, OCR models and caches.</summary>
public static class AppDataFolders
{
    /// <summary>The application's folder name under the per-user data folders.</summary>
    public const string Name = "MediaMuxingWizard";

    /// <summary>The folder name used before the application was renamed from Media Metadata Wizard.</summary>
    private const string LegacyName = "MediaMetadataWizard";

    /// <summary>The roaming per-user folder (settings, queue, OCR models).</summary>
    public static string Roaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), Name);

    /// <summary>The local per-user folder (caches).</summary>
    public static string Local => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), Name);

    /// <summary>
    /// Moves the folders of Media Metadata Wizard to the new name, once, so settings, the queue and downloaded OCR
    /// models are kept. Call at start-up, before anything reads them.
    /// </summary>
    public static void MigrateLegacyFolders()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var root = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (root.Length == 0)
                continue;
            var legacy = Path.Combine(root, LegacyName);
            var current = Path.Combine(root, Name);
            try
            {
                if (Directory.Exists(legacy) && !Directory.Exists(current))
                    Directory.Move(legacy, current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The old folder stays where it is; the application starts with defaults.
            }
        }
    }
}
