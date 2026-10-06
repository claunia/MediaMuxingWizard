using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(MMW.App.Tests.TestAppBuilder))]

namespace MMW.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
