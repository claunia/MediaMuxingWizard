using Avalonia;
using MMW.App.Services;

namespace MMW.App;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        MMW.Core.AppDataFolders.MigrateLegacyFolders();

        // A second launch with files hands them to the running editor and exits.
        var files = args.Where(File.Exists).ToList();
        if (!OperatingSystem.IsMacOS() && files.Count > 0 && SingleInstance.TryForward(files))
            return 0;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
