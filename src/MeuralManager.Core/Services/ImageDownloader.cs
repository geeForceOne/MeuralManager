namespace MeuralManager.Core.Services;

// Shared, unauthenticated HttpClient for pulling item images from Meural's CDN
// (a separate host from api.meural.com, so no auth header is needed or sent).
// Used for thumbnails, where many short-lived requests are made over the
// app's lifetime - a single shared client avoids socket exhaustion.
public static class ImageDownloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<byte[]> DownloadAsync(string url, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
