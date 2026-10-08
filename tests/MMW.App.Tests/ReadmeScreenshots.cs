using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using MMW.App.Services;
using MMW.App.ViewModels;
using MMW.App.Views;
using MMW.Core.Model;

namespace MMW.App.Tests;

/// <summary>
/// Renders the README screenshots (docs/screenshots) from real files: MMW_README_SHOTS names the output folder and
/// MMW_README_MEDIA the media library they come from. Skipped otherwise. Never saves anything.
/// </summary>
public class ReadmeScreenshots
{
    private static readonly string Media = Environment.GetEnvironmentVariable("MMW_README_MEDIA") ?? string.Empty;
    private static readonly string? s_out = Environment.GetEnvironmentVariable("MMW_README_SHOTS") is { Length: > 0 } o && Media.Length > 0 ? o : null;

    private static (MainWindow Window, MainWindowViewModel Vm) Open(int width = 1280, int height = 820, string[]? recent = null)
    {
        if (s_out is null)
            Assert.Skip("Set MMW_README_SHOTS and MMW_README_MEDIA to render the README screenshots.");
        MMW.Media.Remux.MediaRemux.EnsureRegistered();
        var settings = new SettingsService(Path.Combine(Path.GetTempPath(), "mmw-shots", Guid.NewGuid().ToString("N"), "settings.json"));
        settings.Settings.RememberWindowSize = false;
        foreach (var path in recent ?? [])
            settings.Settings.AddRecent(Path.Combine(Media, path));
        var vm = new MainWindowViewModel(new DocumentService(), new FakeDialogService(), settings);
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        return (window, vm);
    }

    private static async Task Until(Func<bool> condition, int seconds = 180)
    {
        for (var i = 0; i < seconds * 10 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Shot(TopLevel window, string name)
    {
        for (var i = 0; i < 5; i++)
            Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(s_out!);
        window.CaptureRenderedFrame()?.Save(Path.Combine(s_out!, name + ".png"));
    }

    [AvaloniaFact]
    public async Task Dolby_vision_repair()
    {
        var (window, vm) = Open();
        await vm.OpenPathsAsync([Path.Combine(Media, "Series/Fundación (2021)/S02/Fundación - 02x02 - Un atisbo de la oscuridad.mkv")]);
        var doc = vm.Document!;
        await Until(() => doc.HasDolbyVisionNotice);
        doc.SelectedRow = doc.Rows.First(r => r.Track is VideoTrack);
        await Until(() => false, 3);
        Shot(window, "dolby-vision-repair");
    }

    [AvaloniaFact]
    public async Task Audio_names_and_conversions()
    {
        var (window, vm) = Open();
        await vm.OpenPathsAsync([Path.Combine(Media, "Pelis/Capitán América Brave New World (2025) (DV)/Capitán América Brave New World.mp4")]);
        var doc = vm.Document!;
        await doc.PrettifyAudioNamesCommand.ExecuteAsync(null);
        doc.SelectedRow = doc.Rows.Last(r => r.Track is AudioTrack);
        var inspector = (TrackInspectorViewModel)doc.Inspector!;
        await Until(() => inspector.HasConversionChoices, 60);
        Shot(window, "audio-tracks");
    }

    [AvaloniaFact]
    public async Task Metadata_and_artwork()
    {
        var (window, vm) = Open();
        await vm.OpenPathsAsync([Path.Combine(Media, "Pelis/Una película de Minecraft (2025) (DV)/Una película de Minecraft.mp4")]);
        await Until(() => false, 5);
        Shot(window, "metadata");
        vm.Document!.MetadataInspector.SelectedTab = 1;
        await Until(() => false, 3);
        Shot(window, "artwork");
    }

    [AvaloniaFact]
    public async Task Dark_theme_atmos_movie()
    {
        var (window, vm) = Open();
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        try
        {
            await vm.OpenPathsAsync([Path.Combine(Media, "Pelis/Sword Art Online Progressive Movie II - Kuraki Yuuyami no Scherzo (2022) (THD)/Sword Art Online Progressive Movie II - Kuraki Yuuyami no Scherzo.mp4")]);
            var doc = vm.Document!;
            await doc.PrettifyAudioNamesCommand.ExecuteAsync(null);
            doc.SelectedRow = doc.Rows.First(r => r.Track is AudioTrack);
            var inspector = (TrackInspectorViewModel)doc.Inspector!;
            await Until(() => inspector.HasConversionChoices, 60);
            Shot(window, "dark-truehd-atmos");
        }
        finally
        {
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    [AvaloniaFact]
    public async Task Import_dialog()
    {
        if (s_out is null)
            Assert.Skip("Set MMW_README_SHOTS and MMW_README_MEDIA to render the README screenshots.");
        MMW.Media.Remux.MediaRemux.EnsureRegistered();
        var document = new DocumentViewModel(new MediaDocument(null, ContainerKind.Mp4), new DocumentService(), new FakeDialogService(),
            new SettingsService(Path.Combine(Path.GetTempPath(), "mmw-shots", Guid.NewGuid().ToString("N"), "settings.json")));
        var dialog = new ImportDialogViewModel(document, [Path.Combine(Media, "Codec tests/Multichannel audio/{Dolby TrueHD + Atmos - M2TS} Unfold (15 objects).m2ts"),
            Path.Combine(Media, "Codec tests/Subtitles/Embedded/WebVTT.mkv")]);
        await dialog.LoadAsync();
        var window = new Window { Content = new ImportDialogView { DataContext = dialog }, Width = 980, Height = 560, Padding = new Avalonia.Thickness(20) };
        window.Show();
        await Until(() => false, 3);
        Shot(window, "import");
    }

    [AvaloniaFact]
    public async Task Home_screen()
    {
        var (window, _) = Open(1100, 720,
        [
            "Pelis/Capitán América Brave New World (2025) (DV)/Capitán América Brave New World.mp4",
            "Series/Fundación (2021)/S02/Fundación - 02x02 - Un atisbo de la oscuridad.mkv",
            "Pelis/Una película de Minecraft (2025) (DV)/Una película de Minecraft.mp4",
        ]);
        await Until(() => false, 2);
        Shot(window, "home");
    }
}
