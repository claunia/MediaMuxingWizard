using System.Net;
using MMW.Core.Metadata;
using MMW.Metadata.Artwork;
using MMW.Metadata.Search;

namespace MMW.Metadata.Tests.Infrastructure;

public sealed class ArtworkDownloaderTests
{
    private static readonly byte[] s_png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private static readonly byte[] s_jpeg = [.. new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, .. new byte[2000]];

    private static ArtworkDownloader Create(long max = ArtworkDownloader.DefaultMaxBytes) => new(new HttpClient(new FakeHttpHandler()
        .OnBytes(s_png, "image/png", "/art.png")
        .OnBytes(s_jpeg, "image/jpeg", "/art.jpg")
        .OnBytes("RIFF....WEBPVP8 "u8.ToArray(), "image/webp", "/art.webp")
        .OnStatus(HttpStatusCode.NotFound, "/missing.jpg")), max);

    [Fact]
    public async Task DownloadsAndDetectsFormat()
    {
        var downloader = Create();

        var png = await downloader.DownloadAsync(new Uri("https://img.example/art.png"), TestContext.Current.CancellationToken);
        var jpeg = await downloader.DownloadAsync(new RemoteArtwork(new Uri("https://img.example/t.jpg"), new Uri("https://img.example/art.jpg"), ArtworkKind.Poster, "Test"), TestContext.Current.CancellationToken);

        Assert.Equal(ArtworkFormat.Png, png.Format);
        Assert.Equal(s_png, png.Data);
        Assert.Equal(ArtworkFormat.Jpeg, jpeg.Format);
    }

    [Fact]
    public async Task RejectsOversizedUnsupportedAndMissingImages()
    {
        var downloader = Create(max: 1000);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(new Uri("https://img.example/art.jpg"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(new Uri("https://img.example/art.webp"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => downloader.DownloadAsync(new Uri("https://img.example/missing.jpg"), TestContext.Current.CancellationToken));
        Assert.Null(await downloader.TryDownloadAsync(new Uri("https://img.example/missing.jpg"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadAll_SkipsFailures()
    {
        var art = new[]
        {
            new RemoteArtwork(new Uri("https://img.example/a"), new Uri("https://img.example/art.png"), ArtworkKind.Poster, "Test"),
            new RemoteArtwork(new Uri("https://img.example/b"), new Uri("https://img.example/missing.jpg"), ArtworkKind.Poster, "Test"),
        };

        var downloaded = await Create().DownloadAllAsync(art, TestContext.Current.CancellationToken);

        Assert.Single(downloaded);
    }
}
