using System.Diagnostics;
using MMW.Core.Diagnostics;

namespace MMW.App.Services;

public interface INotificationService
{
    void Notify(string title, string message);
}

/// <summary>Desktop notifications via notify-send (Linux) or osascript (macOS); logged elsewhere.</summary>
public sealed class NotificationService : INotificationService
{
    public void Notify(string title, string message)
    {
        AppLog.Info($"{title}: {message}");
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("--app-name=Media Metadata Wizard");
                psi.ArgumentList.Add(title);
                psi.ArgumentList.Add(message);
                Process.Start(psi)?.Dispose();
            }
            else if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("osascript") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add($"display notification \"{Escape(message)}\" with title \"{Escape(title)}\"");
                Process.Start(psi)?.Dispose();
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No notification tool available; the log entry is enough.
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
