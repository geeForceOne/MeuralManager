using System.Collections.Concurrent;
using MeuralManager.Core.Api;
using MeuralManager.Core.Data;
using MeuralManager.Core.Models;
using MeuralManager.Core.Services;

namespace MeuralManager.Web.Services;

public readonly record struct ImageCacheProgress(int Cached, int Total, bool IsRunning);

// App-wide singleton that quietly downloads every uploaded image to local disk in the
// background, so the app can serve pictures from the local cache instead of Meural's CDN (whose
// signed URLs also expire - see ItemImage.razor's old retry dance, now replaced by this cache).
//
// Deliberately keyed by account (email) and independent of any Blazor circuit: MeuralSessionState
// is scoped per browser tab and gets torn down when that tab disconnects, but a multi-thousand-
// image warmup can easily outlive a single tab session. So this holds its own MeuralApiClient per
// account, restored from the same refresh token WebSessionStore already persists, and keeps
// running via Task.Run on the thread pool regardless of whether any circuit for that account is
// still connected. It only stops when the user explicitly signs out (UnregisterAndDisposeAsync)
// or the process exits.
public sealed class ImageCacheManager : IDisposable
{
    private const int MaxConcurrentDownloads = 4;
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(400);

    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<string, AccountState> _accounts = new();

    // Fired (with the account's email) whenever that account's cache progress changes -
    // including when a warmup starts/finishes - so the UI can subscribe without polling.
    public event Action<string>? Changed;

    private sealed class AccountState
    {
        public required MeuralApiClient Client { get; init; }
        public required string ImagesDir { get; init; }
        public required string DbPath { get; init; }
        public CancellationTokenSource? RunCts;
        public int Cached;
        public int Total;
        public bool IsRunning;
        public DateTime LastReportUtc;
    }

    public ImageCacheManager(IHostEnvironment env)
    {
        _cacheRoot = Environment.GetEnvironmentVariable("CACHE_ROOT_PATH")
            ?? Path.Combine(env.ContentRootPath, "cache");
    }

    // Establishes (or reuses) this account's independent background client. Safe to call on
    // every sign-in - a re-login for an already-registered account is a no-op, since the
    // existing background client and any in-flight warmup are still perfectly good.
    public async Task RegisterAccountAsync(string email, string trustId, string refreshToken)
    {
        if (_accounts.ContainsKey(email))
            return;

        var sanitized = FileNaming.SanitizeFileName(email);
        var client = new MeuralApiClient(trustId);
        if (!await client.TryRestoreSessionAsync(refreshToken))
        {
            client.Dispose();
            return; // Best-effort - the warmup just won't run until the next successful sign-in.
        }

        var state = new AccountState
        {
            Client = client,
            ImagesDir = Path.Combine(_cacheRoot, sanitized, "images"),
            DbPath = Path.Combine(_cacheRoot, sanitized, "meural-cache.db"),
        };

        if (!_accounts.TryAdd(email, state))
            client.Dispose(); // Lost a race with a concurrent registration for the same account.
    }

    // Starts (or restarts) a background pass that ensures every given item is cached, skipping
    // anything already on disk. Safe to call after every scan - a run already in progress is
    // cancelled in favor of the new (presumably more current) item list.
    public void QueueWarmup(string email, IReadOnlyList<MeuralItem> items)
    {
        if (!_accounts.TryGetValue(email, out var state))
            return;

        state.RunCts?.Cancel();
        var cts = new CancellationTokenSource();
        state.RunCts = cts;
        state.IsRunning = true;
        state.Cached = 0;
        state.Total = items.Count;
        Changed?.Invoke(email);

        _ = Task.Run(() => RunWarmupAsync(email, state, items, cts.Token));
    }

    public ImageCacheProgress? GetProgress(string email) =>
        _accounts.TryGetValue(email, out var state)
            ? new ImageCacheProgress(state.Cached, state.Total, state.IsRunning)
            : null;

    // Serves one item's image: from disk if already cached, otherwise downloads it (refreshing
    // its signed URL first if needed) and caches it before returning. Returns null if the
    // account isn't registered (never signed in this process) or the item has no image.
    public async Task<string?> GetOrDownloadImageAsync(string email, long itemId, CancellationToken ct)
    {
        if (!_accounts.TryGetValue(email, out var state))
            return null;

        if (ImageCacheStore.TryGetCachedFile(state.ImagesDir, itemId, out var cached))
            return cached;

        string? knownUrl = null;
        try
        {
            var db = new PlaylistCacheStore(state.DbPath);
            knownUrl = await db.GetItemImageAsync(itemId, ct);
        }
        catch
        {
            // Best-effort - fall through and let EnsureCachedAsync fetch a fresh URL instead.
        }

        return await ImageCacheStore.EnsureCachedAsync(
            state.ImagesDir, itemId, knownUrl, state.Client,
            (id, url) => PersistRefreshedUrlAsync(state.DbPath, id, url, ct),
            ct);
    }

    // Removes a set of items' cached files - called when items are deleted from the Meural
    // account, so orphaned local copies don't linger.
    public void RemoveCached(string email, IEnumerable<long> itemIds)
    {
        if (!_accounts.TryGetValue(email, out var state))
            return;

        foreach (var itemId in itemIds)
            ImageCacheStore.RemoveCached(state.ImagesDir, itemId);
    }

    // Stops any in-progress warmup and disposes this account's background client - called on an
    // explicit sign-out (as opposed to just closing the browser tab, which intentionally leaves
    // this running).
    public void UnregisterAndDispose(string email)
    {
        if (_accounts.TryRemove(email, out var state))
        {
            state.RunCts?.Cancel();
            state.Client.Dispose();
        }
    }

    private async Task RunWarmupAsync(string email, AccountState state, IReadOnlyList<MeuralItem> items, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(state.ImagesDir);
            using var gate = new SemaphoreSlim(MaxConcurrentDownloads);

            var tasks = items.Where(i => i.Id.HasValue).Select(async item =>
            {
                var id = item.Id!.Value;
                await gate.WaitAsync(ct);
                try
                {
                    if (!ImageCacheStore.IsCached(state.ImagesDir, id))
                    {
                        await ImageCacheStore.EnsureCachedAsync(
                            state.ImagesDir, id, item.Image, state.Client,
                            (refreshedId, url) => PersistRefreshedUrlAsync(state.DbPath, refreshedId, url, ct),
                            ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Best-effort - one bad item shouldn't stop the rest of the warmup.
                }
                finally
                {
                    gate.Release();
                    ReportProgress(email, state);
                }
            });

            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer QueueWarmup call, or the account was unregistered.
        }
        finally
        {
            state.IsRunning = false;
            Changed?.Invoke(email);
        }
    }

    private void ReportProgress(string email, AccountState state)
    {
        Interlocked.Increment(ref state.Cached);

        var now = DateTime.UtcNow;
        if (now - state.LastReportUtc < ProgressReportInterval && state.Cached != state.Total)
            return;

        state.LastReportUtc = now;
        Changed?.Invoke(email);
    }

    private static async Task PersistRefreshedUrlAsync(string dbPath, long itemId, string? image, CancellationToken ct)
    {
        try
        {
            var db = new PlaylistCacheStore(dbPath);
            await db.UpdateItemImageAsync(itemId, image, ct);
        }
        catch
        {
            // Best-effort - a failed write just means the next request refreshes the URL again.
        }
    }

    public void Dispose()
    {
        foreach (var state in _accounts.Values)
        {
            state.RunCts?.Cancel();
            state.Client.Dispose();
        }
    }
}
