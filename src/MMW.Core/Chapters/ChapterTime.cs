using System.Globalization;

namespace MMW.Core.Chapters;

/// <summary>Formats and parses chapter timestamps (<c>hh:mm:ss.fff</c>).</summary>
public static class ChapterTime
{
    public static string Format(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}");

    /// <summary>Parses <c>hh:mm:ss[.fff]</c>, <c>mm:ss[.fff]</c> or a plain number of seconds.</summary>
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim().Replace(',', '.');
        var parts = text.Split(':');
        if (parts.Length > 3)
            return false;

        double seconds = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0)
                return false;
            seconds = seconds * 60 + n;
        }

        value = TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
        return true;
    }
}
