using MeuralManager.Core.Api;
using MeuralManager.Core.Models;

namespace MeuralManager.Core.Services;

public readonly record struct UploadSummary(int Done, int Failed, IReadOnlyList<(string FilePath, MeuralItem Item)> Uploaded);

// Bulk playlist-membership operations, following the same loop + per-item
// try/catch + IProgress<string> + CancellationToken + politeness-delay shape
// as CleanupService's delete loops.
public static class PlaylistService
{
    private static readonly TimeSpan CallDelay = TimeSpan.FromMilliseconds(300);

    public static async Task<DeleteSummary> AddItemsToGalleryAsync(
        MeuralApiClient client, long galleryId, IReadOnlyList<MeuralItem> items, IProgress<string>? progress, CancellationToken ct)
    {
        int done = 0, failed = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var displayName = item.Name ?? $"item {item.Id}";
            try
            {
                var outcome = await client.AddItemToGalleryAsync(galleryId, item.Id!.Value, ct);
                if (outcome.Success) done++; else failed++;
                progress?.Report(outcome.Success
                    ? $"({done + failed}/{items.Count}) Added \"{displayName}\"."
                    : $"({done + failed}/{items.Count}) Couldn't add \"{displayName}\" (server said: {outcome.StatusCode}).");
            }
            // A real user-initiated cancel (ct.IsCancellationRequested) should stop the whole
            // batch, same as any other cancellation. But a single slow request timing out
            // (MeuralApiClient's HttpClient) throws the same OperationCanceledException without
            // ct itself being cancelled - that should only fail this one item and continue,
            // not abort every remaining item in the batch.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed++;
                progress?.Report($"({done + failed}/{items.Count}) Couldn't add \"{displayName}\": {ex.Message}");
            }

            await Task.Delay(CallDelay, ct);
        }

        return new DeleteSummary(done, failed);
    }

    public static async Task<DeleteSummary> RemoveItemsFromGalleryAsync(
        MeuralApiClient client, long galleryId, IReadOnlyList<MeuralItem> items, IProgress<string>? progress, CancellationToken ct)
    {
        int done = 0, failed = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var displayName = item.Name ?? $"item {item.Id}";
            try
            {
                var outcome = await client.RemoveItemFromGalleryAsync(galleryId, item.Id!.Value, ct);
                if (outcome.Success) done++; else failed++;
                progress?.Report(outcome.Success
                    ? $"({done + failed}/{items.Count}) Removed \"{displayName}\"."
                    : $"({done + failed}/{items.Count}) Couldn't remove \"{displayName}\" (server said: {outcome.StatusCode}).");
            }
            // A real user-initiated cancel (ct.IsCancellationRequested) should stop the whole
            // batch, same as any other cancellation. But a single slow request timing out
            // (MeuralApiClient's HttpClient) throws the same OperationCanceledException without
            // ct itself being cancelled - that should only fail this one item and continue,
            // not abort every remaining item in the batch.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed++;
                progress?.Report($"({done + failed}/{items.Count}) Couldn't remove \"{displayName}\": {ex.Message}");
            }

            await Task.Delay(CallDelay, ct);
        }

        return new DeleteSummary(done, failed);
    }

    // Meural's API has no way to replace an existing item's image bytes in place (PUT
    // items/{id} only accepts name/description JSON, and there's no documented endpoint for
    // swapping the file) - confirmed by reading davemorin/meural-manager's server.js, which
    // never sends a file to that route. So "replace" here means: upload newFilePath as a brand
    // new item, add it to every gallery oldItem belonged to, then delete oldItem - the same
    // create+swap+delete sequence the official Meural web app almost certainly performs under
    // the hood for its own crop-and-replace feature. Also reused for Revert (Playlists.razor's
    // RevertPreviewToOriginalAsync), just with the stored pre-crop original as newFilePath and
    // the currently-cropped item as oldItem - hence the outcome-neutral "new version"/"previous
    // version" wording below rather than "cropped"/"original", which would be backwards on a
    // revert.
    public static async Task<MeuralItem> ReplaceItemImageAsync(
        MeuralApiClient client, MeuralItem oldItem, IReadOnlyList<long> galleryIds, string newFilePath,
        IProgress<string>? progress, CancellationToken ct)
    {
        var displayName = oldItem.Name ?? $"item {oldItem.Id}";
        progress?.Report($"Uploading new version of \"{displayName}\"...");
        var newItem = await client.UploadItemAsync(newFilePath, oldItem.Name, progress, ct);

        foreach (var galleryId in galleryIds)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await client.AddItemToGalleryAsync(galleryId, newItem.Id!.Value, ct);
            progress?.Report(outcome.Success
                ? $"Added the new version to playlist {galleryId}."
                : $"Couldn't add the new version to playlist {galleryId} (server said: {outcome.StatusCode}).");
            await Task.Delay(CallDelay, ct);
        }

        if (oldItem.Id is long oldId)
        {
            var deleteOutcome = await client.DeleteItemAsync(oldId, ct);
            progress?.Report(deleteOutcome.Success
                ? $"Removed the previous version of \"{displayName}\"."
                : $"Couldn't remove the previous version of \"{displayName}\" (server said: {deleteOutcome.StatusCode}).");
        }

        return newItem;
    }

    public static async Task<UploadSummary> UploadAndAddToGalleryAsync(
        MeuralApiClient client, long galleryId, IReadOnlyList<string> filePaths, IProgress<string>? progress, CancellationToken ct)
    {
        int done = 0, failed = 0;
        // Keyed by input file path so callers can correlate a successful upload back to
        // whatever they associated with that path (e.g. Playlists.razor's crop-before-upload
        // flow, which needs to know which resulting item id to store a pre-crop original under).
        var uploaded = new List<(string FilePath, MeuralItem Item)>();
        foreach (var filePath in filePaths)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(filePath);
            try
            {
                var name = Path.GetFileNameWithoutExtension(filePath);
                var item = await client.UploadItemAsync(filePath, name, progress, ct);

                var outcome = await client.AddItemToGalleryAsync(galleryId, item.Id!.Value, ct);
                if (outcome.Success)
                {
                    done++;
                    uploaded.Add((filePath, item));
                }
                else
                {
                    failed++;
                }
                progress?.Report(outcome.Success
                    ? $"({done + failed}/{filePaths.Count}) Uploaded and added \"{fileName}\"."
                    : $"({done + failed}/{filePaths.Count}) Uploaded \"{fileName}\" but couldn't add it to the playlist (server said: {outcome.StatusCode}).");
            }
            // A real user-initiated cancel (ct.IsCancellationRequested) should stop the whole
            // batch, same as any other cancellation. But a single slow request timing out
            // (MeuralApiClient's HttpClient) throws the same OperationCanceledException without
            // ct itself being cancelled - that should only fail this one item and continue,
            // not abort every remaining item in the batch.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed++;
                progress?.Report($"({done + failed}/{filePaths.Count}) Couldn't upload \"{fileName}\": {ex.Message}");
            }

            await Task.Delay(CallDelay, ct);
        }

        return new UploadSummary(done, failed, uploaded);
    }
}
