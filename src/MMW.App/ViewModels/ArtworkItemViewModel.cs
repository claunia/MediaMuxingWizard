using System.Globalization;
using Avalonia.Media.Imaging;
using MMW.App.Resources;
using MMW.Core.Diagnostics;
using MMW.Core.Metadata;

namespace MMW.App.ViewModels;

/// <summary>An artwork image with a decoded thumbnail.</summary>
public sealed class ArtworkItemViewModel : ViewModelBase, IDisposable
{
    public ArtworkItemViewModel(Artwork artwork)
    {
        Artwork = artwork;
        try
        {
            using var ms = new MemoryStream(artwork.Data);
            Thumbnail = Bitmap.DecodeToWidth(ms, 400);
            Description = string.Format(CultureInfo.CurrentCulture, Strings.Artwork_DescriptionFormat, Thumbnail.PixelSize.Width, Thumbnail.PixelSize.Height, artwork.Format.ToString().ToUpperInvariant(), artwork.Data.Length / 1024);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            AppLog.Warn($"Could not decode artwork: {ex.Message}");
            Description = string.Format(CultureInfo.CurrentCulture, Strings.Artwork_UnreadableFormat, artwork.Format, artwork.Data.Length / 1024);
        }
    }

    public Artwork Artwork { get; }

    public Bitmap? Thumbnail { get; }

    public string Description { get; }

    public void Dispose() => Thumbnail?.Dispose();
}
