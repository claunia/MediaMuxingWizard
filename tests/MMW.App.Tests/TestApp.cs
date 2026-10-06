using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(MMW.App.Tests.TestAppBuilder))]

namespace MMW.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>
    /// Tests assert on English UI text, so the interface language is English whatever the machine's language;
    /// tests of other languages switch the UI thread's culture themselves and restore it.
    /// </summary>
#pragma warning disable CA2255 // The initializer only pins the culture of this test assembly.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void UseEnglishInterface()
    {
        var english = CultureInfo.GetCultureInfo("en");
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentUICulture = english;
    }
}
