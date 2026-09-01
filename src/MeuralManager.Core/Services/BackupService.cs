using MeuralManager.Core.Api;
using MeuralManager.Core.Models;

namespace MeuralManager.Core.Services;

public readonly record struct BackupSummary(int Saved, int Failed, int Skipped);

// Downloads image files to local disk before deletion. Uses its own HttpClient
// deliberately without the Meural auth header - images are served from a
// separate CDN host.
public static class BackupService
{
    public static async Task<BackupSummary> BackupItemsAsync(
        IReadOnlyList<MeuralItem> items, string backupDir, IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(backupDir);
        using var downloadHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        int saved = 0, failed = 0, skipped = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var displayName = item.Name ?? $"item {item.Id}";

            if (string.IsNullOrWhiteSpace(item.Image))
            {
                skipped++;
                progress?.Report($"[{saved + failed + skipped}/{items.Count}] Skipping \"{displayName}\" - no image available.");
                continue;
            }

            try
            {
                await DownloadToFolderAsync(downloadHttp, item.Id!.Value, item.Name, item.Image, backupDir, ct);
                saved++;
                progress?.Report($"[{saved + failed + skipped}/{items.Count}] Saved \"{displayName}\".");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                progress?.Report($"[{saved + failed + skipped}/{items.Count}] Couldn't save \"{displayName}\": {ex.Message}");
            }
        }

        return new BackupSummary(saved, failed, skipped);
    }

    public static async Task<BackupSummary> BackupGalleryAsync(
        MeuralApiClient apiClient, MeuralGallery gallery, string galleriesBackupDir, IProgress<string>? progress, CancellationToken ct)
    {
        var gallerySafeName = FileNaming.SanitizeFileName(gallery.Name ?? "untitled");
        var galleryFolder = Path.Combine(galleriesBackupDir, $"{gallery.Id}_{gallerySafeName}");
        Directory.CreateDirectory(galleryFolder);

        using var downloadHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var itemIds = gallery.ItemIds ?? new List<long>();
        int saved = 0, failed = 0, skipped = 0;

        foreach (var itemId in itemIds)
        {
            ct.ThrowIfCancellationRequested();

            MeuralItem? item;
            try
            {
                item = await apiClient.GetItemAsync(itemId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                progress?.Report($"    Couldn't look up item {itemId}: {ex.Message}");
                continue;
            }

            if (item is null || string.IsNullOrWhiteSpace(item.Image))
            {
                skipped++;
                progress?.Report($"    Skipping item {itemId} - no image available.");
                continue;
            }

            try
            {
                await DownloadToFolderAsync(downloadHttp, itemId, item.Name, item.Image, galleryFolder, ct);
                saved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                progress?.Report($"    Couldn't save \"{item.Name ?? $"item {itemId}"}\": {ex.Message}");
            }
        }

        return new BackupSummary(saved, failed, skipped);
    }

    private static async Task DownloadToFolderAsync(
        HttpClient downloadHttp, long itemId, string? itemName, string imageUrl, string folder, CancellationToken ct)
    {
        using var resp = await downloadHttp.GetAsync(imageUrl, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

        var ext = FileNaming.GuessExtension(imageUrl, resp.Content.Headers.ContentType?.MediaType);
        var safeName = FileNaming.SanitizeFileName(itemName ?? "untitled");
        var fileName = $"{itemId}_{safeName}{ext}";
        var filePath = Path.Combine(folder, fileName);

        await File.WriteAllBytesAsync(filePath, bytes, ct);
    }
}
