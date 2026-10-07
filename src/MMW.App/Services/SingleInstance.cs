using System.Globalization;
using System.IO.Pipes;
using System.Text;
using MMW.App.Resources;
using MMW.Core.Diagnostics;

namespace MMW.App.Services;

/// <summary>
/// Forwards files opened while the editor is already running to the existing instance through a per-user named
/// pipe (a Unix domain socket on Linux), so the file manager opens them as new tabs instead of new windows.
/// macOS uses Launch Services activation instead.
/// </summary>
public static class SingleInstance
{
    /// <summary>Pipe name; tests use their own so they never talk to a real running editor.</summary>
    internal static string PipeName { get; set; } = "MediaMuxingWizard-" + Sanitize(Environment.UserName);

    /// <summary>Sends <paramref name="paths"/> to a running instance; true when one received them.</summary>
    public static bool TryForward(IReadOnlyList<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(300);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (var p in paths)
                writer.WriteLine(Path.GetFullPath(p));
            writer.WriteLine();
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Listens for paths from later launches; <paramref name="onPaths"/> runs on a background thread.</summary>
    public static void StartServer(Action<IReadOnlyList<string>> onPaths, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(cancellationToken);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var paths = new List<string>();
                    while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
                        paths.Add(line);
                    onPaths(paths);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException ex)
                {
                    // Another instance owns the pipe (or it broke); retry a little later.
                    AppLog.Debug(string.Format(CultureInfo.CurrentCulture, Strings.Log_SingleInstancePipeFormat, ex.Message));
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
            }
        }, cancellationToken);
    }

    private static string Sanitize(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
}
