using System.Globalization;
using System.Runtime.CompilerServices;

namespace MMW.Tests;

/// <summary>
/// Runs every test assembly with English messages (the libraries' neutral language), whatever the machine's
/// language, so tests can check the text the code produces. Number and date formatting are left alone.
/// </summary>
internal static class TestCulture
{
#pragma warning disable CA2255 // a module initializer is the way to set the culture before any test of the assembly runs
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void UseEnglishMessages()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentUICulture = english;
    }
}
