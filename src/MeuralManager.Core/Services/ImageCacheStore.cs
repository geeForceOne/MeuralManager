using MeuralManager.Core.Api;

namespace MeuralManager.Core.Services;

// Disk cache of downloaded item images, one file per item id (named "{itemId}{ext}" so the
// extension can vary without a lookup table). Used both by the background warmup loop that
// eagerly downloads everything and by the on-demand cache-serving endpoint that downloads a
// single item the first time it's requested - same code path either way, so "already cached"
// and "not cached yet" behave identically no matter which caller asks first.
public static class ImageCacheStore
{
    public static bool TryGetCachedFile(string cacheDir, long itemId, out string path)
    {
        if (Directory.Exists(cacheDir))
        {
            var match = Directory.EnumerateFiles(cacheDir, $"{itemId}.*").FirstOrDefault();
            if (match is not null)
            {
                path = match;
                return true;
            }
        }

        path = "";
        return false;
    }

    public static bool IsCached(string cacheDir, long itemId) => TryGetCachedFile(cacheDir, itemId, out _);

    // Downloads an item's image and caches it to disk, returning the cached file's path - or
    // null if the item has no image or it couldn't be fetched. If knownImageUrl is missing or
    // has expired (Meural signs Image URLs with a short-lived Expires param), re-fetches the
    // item via the API client for a freshly-signed URL and retries once; onImageUrlRefreshed
    // lets the caller persist that fresh URL so future callers don't hit the same dead link.
    public static async Task<string?> EnsureCachedAsync(
        string cacheDir,
        long itemId,
        string? knownImageUrl,
        MeuralApiClient client,
        Func<long, string?, Task>? onImageUrlRefreshed,
        CancellationToken ct)
    {
        if (TryGetCachedFile(cacheDir, itemId, out var existing))
            return existing;

        var url = knownImageUrl;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                var fresh = await client.GetItemAsync(itemId, ct);
                url = fresh?.Image;
                if (onImageUrlRefreshed is not null)
                    await onImageUrlRefreshed(itemId, url);
                if (string.IsNullOrWhiteSpace(url))
                    return null;
            }

            try
            {
                var bytes = await ImageDownloader.DownloadAsync(url, ct);
                Directory.CreateDirectory(cacheDir);
                var ext = FileNaming.GuessExtension(url, contentType: null);
                var path = Path.Combine(cacheDir, $"{itemId}{ext}");
                await File.WriteAllBytesAsync(path, bytes, ct);
                return path;
            }
            catch (HttpRequestException) when (attempt == 0)
            {
                // Likely an expired signed URL - force a re-fetch of a fresh one and retry once.
                url = null;
            }
        }

        return null;
    }

    // Deletes a cached image file (if any) - called when an item is removed from the Meural
    // account, so a stale local copy doesn't linger forever.
    public static void RemoveCached(string cacheDir, long itemId)
    {
        if (TryGetCachedFile(cacheDir, itemId, out var path))
            File.Delete(path);
    }
}
