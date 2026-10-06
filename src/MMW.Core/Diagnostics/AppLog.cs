using System.Collections.ObjectModel;

namespace MMW.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Message);

/// <summary>Application-wide message log shown in the Log window.</summary>
public static class AppLog
{
    private static readonly Lock s_lock = new();
    private static readonly List<LogEntry> s_entries = [];

    public static event EventHandler<LogEntry>? EntryAdded;

    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (s_lock)
            return s_entries.ToArray();
    }

    public static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        lock (s_lock)
        {
            s_entries.Add(entry);
            if (s_entries.Count > 5000)
                s_entries.RemoveRange(0, 1000);
        }

        System.Diagnostics.Trace.WriteLine($"[{level}] {message}");
        EntryAdded?.Invoke(null, entry);
    }

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message}: {ex.Message}");

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Clear()
    {
        lock (s_lock)
            s_entries.Clear();
    }
}
