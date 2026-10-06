using System.Diagnostics;
using System.Runtime.InteropServices;
using MMW.Core.Diagnostics;
using MMW.Queue;

namespace MMW.App.Services;

/// <summary>Prevents system sleep while the queue runs, using the native mechanism of each OS.</summary>
public sealed partial class PowerService : IPowerService
{
    public IDisposable PreventSleep(string reason)
    {
        if (OperatingSystem.IsWindows())
            return new WindowsAssertion();
        if (OperatingSystem.IsMacOS())
            return ProcessAssertion.Start("caffeinate", $"-i -w {Environment.ProcessId}");
        if (OperatingSystem.IsLinux())
            return ProcessAssertion.Start("systemd-inhibit", $"--what=sleep:idle --who=\"Media Metadata Wizard\" --why=\"{reason}\" --mode=block sleep infinity");
        return new NoAssertion();
    }

    private sealed class NoAssertion : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>Keeps a helper process alive for the duration of the assertion.</summary>
    private sealed class ProcessAssertion : IDisposable
    {
        private readonly Process? _process;

        private ProcessAssertion(Process? process) => _process = process;

        public static IDisposable Start(string tool, string arguments)
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo(tool, arguments) { UseShellExecute = false, CreateNoWindow = true });
                return new ProcessAssertion(process);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                AppLog.Warn($"Cannot prevent sleep ({tool}: {ex.Message}).");
                return new NoAssertion();
            }
        }

        public void Dispose()
        {
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            _process?.Dispose();
        }
    }

    private sealed partial class WindowsAssertion : IDisposable
    {
        private const uint EsContinuous = 0x80000000;
        private const uint EsSystemRequired = 0x00000001;

        public WindowsAssertion()
        {
            if (SetThreadExecutionState(EsContinuous | EsSystemRequired) == 0)
                AppLog.Warn("Cannot prevent sleep (SetThreadExecutionState failed).");
        }

        public void Dispose() => _ = SetThreadExecutionState(EsContinuous);

        [LibraryImport("kernel32.dll")]
        private static partial uint SetThreadExecutionState(uint flags);
    }
}
