using MeuralManager.Core.Api;
using MeuralManager.Core.Models;

namespace MeuralManager.Core.Services;

public readonly record struct DeleteSummary(int Done, int Failed);

// Set-difference logic shared by the two cleanup flows: uploaded items not
// referenced by any playlist, and playlists not loaded on any Canvas frame.
public static class CleanupService
{
    public static List<MeuralItem> FindOrphanItems(IEnumerable<MeuralItem> items, IEnumerable<MeuralGallery> galleries)
    {
        var galleryItemIds = galleries.SelectMany(g => g.ItemIds ?? new List<long>()).ToHashSet();
        return items
            .Where(i => i.Id is long id && !galleryItemIds.Contains(id))
            .OrderBy(i => i.CreatedAt)
            .ToList();
    }

    public static List<MeuralGallery> FindUnusedGalleries(IEnumerable<MeuralGallery> galleries, HashSet<long> frameGalleryIds)
    {
        return galleries
            .Where(g => g.Id is long id && !frameGalleryIds.Contains(id))
            .ToList();
    }

    public static async Task<DeleteSummary> DeleteItemsAsync(
        MeuralApiClient client, IReadOnlyList<MeuralItem> items, IProgress<string>? progress, CancellationToken ct)
    {
        int done = 0, failed = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var displayName = item.Name ?? $"item {item.Id}";
            try
            {
                var outcome = await client.DeleteItemAsync(item.Id!.Value, ct);
                if (outcome.Success) done++; else failed++;
                progress?.Report(outcome.Success
                    ? $"({done + failed}/{items.Count}) Deleted \"{displayName}\"."
                    : $"({done + failed}/{items.Count}) Couldn't delete \"{displayName}\" (server said: {outcome.StatusCode}).");
            }
            // A real user-initiated cancel (ct.IsCancellationRequested) should stop the whole
            // batch, same as any other cancellation. But a single slow request timing out
            // (MeuralApiClient's HttpClient) throws the same OperationCanceledException without
            // ct itself being cancelled - that should only fail this one item and continue,
            // not abort every remaining item in the batch.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed++;
                progress?.Report($"({done + failed}/{items.Count}) Couldn't delete \"{displayName}\": {ex.Message}");
            }

            // Be polite to the API - avoid hammering it on large batches.
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return new DeleteSummary(done, failed);
    }

    public static async Task<DeleteSummary> DeleteGalleriesAsync(
        MeuralApiClient client, IReadOnlyList<MeuralGallery> galleries, IProgress<string>? progress, CancellationToken ct)
    {
        int done = 0, failed = 0;
        foreach (var gallery in galleries)
        {
            ct.ThrowIfCancellationRequested();
            var displayName = gallery.Name ?? $"playlist {gallery.Id}";
            try
            {
                var outcome = await client.DeleteGalleryAsync(gallery.Id!.Value, ct);
                if (outcome.Success) done++; else failed++;
                progress?.Report(outcome.Success
                    ? $"({done + failed}/{galleries.Count}) Deleted \"{displayName}\"."
                    : $"({done + failed}/{galleries.Count}) Couldn't delete \"{displayName}\" (server said: {outcome.StatusCode}).");
            }
            // A real user-initiated cancel (ct.IsCancellationRequested) should stop the whole
            // batch, same as any other cancellation. But a single slow request timing out
            // (MeuralApiClient's HttpClient) throws the same OperationCanceledException without
            // ct itself being cancelled - that should only fail this one item and continue,
            // not abort every remaining item in the batch.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed++;
                progress?.Report($"({done + failed}/{galleries.Count}) Couldn't delete \"{displayName}\": {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return new DeleteSummary(done, failed);
    }
}
