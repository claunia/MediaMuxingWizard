using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace MMW.App.Views;

/// <summary>Small value converters used across views.</summary>
public static class Converters
{
    public static readonly IValueConverter FileName = new FuncValueConverter<string?, string>(p => p is null ? string.Empty : Path.GetFileName(p));

    public static readonly IValueConverter ChapterTime = new ChapterTimeConverter();

    /// <summary>Looks up a geometry resource (e.g. "IconAudio") by key.</summary>
    public static readonly IValueConverter IconResource = new FuncValueConverter<string?, Avalonia.Media.Geometry?>(key =>
        key is not null && Avalonia.Application.Current?.TryFindResource(key, out var value) == true ? value as Avalonia.Media.Geometry : null);
}

/// <summary>Two-way TimeSpan ↔ "hh:mm:ss.fff" text.</summary>
public sealed class ChapterTimeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is TimeSpan t ? Core.Chapters.ChapterTime.Format(t) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        Core.Chapters.ChapterTime.TryParse(value as string, out var t)
            ? t
            : new Avalonia.Data.BindingNotification(new FormatException("Use hh:mm:ss.fff"), Avalonia.Data.BindingErrorType.DataValidationError);
}
