using System.Diagnostics;

namespace MMW.App.Services;

/// <summary>Reveals files in the platform's file manager.</summary>
public static class FileManager
{
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
            }
            else if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(path);
                Process.Start(psi)?.Dispose();
            }
            else
            {
                // FreeDesktop FileManager1 "ShowItems" would select the file; opening the folder works everywhere.
                var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(Path.GetDirectoryName(path) ?? path);
                Process.Start(psi)?.Dispose();
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Core.Diagnostics.AppLog.Warn($"Could not open the file manager: {ex.Message}");
        }
    }
}
