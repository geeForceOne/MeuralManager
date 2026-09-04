using MeuralManager.Core.Api;
using MeuralManager.Core.Data;
using MeuralManager.Core.Models;
using MeuralManager.Core.Services;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace MeuralManager.Web.Services;

// Scoped per Blazor circuit (one browser tab = one signed-in session), holding the one
// MeuralApiClient for that session - the web equivalent of MainForm holding a single shared
// client for the whole desktop app's lifetime. Disposed automatically when the circuit ends.
//
// Also owns the one shared "full scan" of the account (all uploads, all playlists, all frames,
// and which playlists are loaded on which frame) that the Dashboard, Orphan Uploads, Unused
// Playlists, and Playlists pages all read from - previously each of those three pages ran its
// own overlapping set of API calls on every visit. Scanning once here and having mutations
// (delete/add/remove/upload/install-on-frame) patch these lists in place means the three pages
// only ever hit the network for a full re-scan when the user explicitly asks for one.
//
// Backed by the same MeuralManager.Core.Data.PlaylistCacheStore (SQLite) the WinForms app's playlist
// manager uses, one file per signed-in email under CACHE_ROOT_PATH - so a returning visit from
// the same account loads instantly from disk instead of needing a fresh scan, and the scan
// survives a container restart the way the in-memory-only version didn't.
public sealed class MeuralSessionState(IHostEnvironment env, ImageCacheManager imageCache, IDataProtectionProvider dataProtection) : IDisposable
{
    private readonly string _cacheRoot = Environment.GetEnvironmentVariable("CACHE_ROOT_PATH")
        ?? Path.Combine(env.ContentRootPath, "cache");

    // Encrypts API keys before they reach the per-account SQLite settings table - the DB file
    // lives on disk in a Docker volume, so keys shouldn't sit there in plain text any more than
    // the login session does in browser storage (see WebSessionStore).
    private readonly IDataProtector _protector = dataProtection.CreateProtector("MeuralManager.Web.AiSettings");

    private PlaylistCacheStore? _cacheStore;

    // Where non-destructive crop's pre-crop originals live for this account - see
    // EditOriginalStore. Parallel to ImageCacheManager's own per-account "images" directory
    // under the same cache root.
    private string OriginalsDir => Path.Combine(_cacheRoot, FileNaming.SanitizeFileName(Email!), "originals");

    public MeuralApiClient? Client { get; private set; }
    public string? Email { get; private set; }
    public bool IsAuthenticated => Client is not null;

    // Fired whenever sign-in/sign-out/scan-progress happens, so MainLayout can re-render
    // without every page needing to know about it.
    public event Action? Changed;

    public DateTime? LastScannedUtc { get; private set; }
    public bool HasScanned => LastScannedUtc is not null;

    // Session-owned (not page-owned) so a scan started from the Dashboard keeps running -
    // and stays visible - even though MainLayout blocks navigation away from it while it's in
    // progress. Previously this lived on the Dashboard page itself, so navigating away tore
    // down its state and made a scan look like it had silently stopped.
    public bool IsScanning { get; private set; }
    public ActivityLog ScanLog { get; } = new();

    public List<MeuralItem>? AllItemsCache { get; private set; }
    public List<MeuralGallery>? AllGalleriesCache { get; private set; }
    public List<MeuralDevice>? AllDevicesCache { get; private set; }
    public Dictionary<long, List<MeuralGallery>>? DeviceGalleriesCache { get; private set; }
    public HashSet<long> FavoriteGalleryIds { get; private set; } = [];

    public async Task SetAuthenticatedAsync(MeuralApiClient client, string email)
    {
        Client = client;
        Email = email;
        _cacheStore = new PlaylistCacheStore(
            Path.Combine(_cacheRoot, FileNaming.SanitizeFileName(email), "meural-cache.db"));

        await TryHydrateFromCacheAsync();

        // Registers (or reuses) this account's independent background client and kicks off a
        // warmup for whatever items were just hydrated from disk - so a returning visit resumes
        // filling in the image cache without needing a fresh scan first.
        if (client.RefreshToken is not null)
        {
            await imageCache.RegisterAccountAsync(email, client.TrustId, client.RefreshToken);
            if (AllItemsCache is { Count: > 0 } items)
                imageCache.QueueWarmup(email, items);
        }

        Changed?.Invoke();
    }

    public void InvalidateCaches()
    {
        AllItemsCache = null;
        AllGalleriesCache = null;
        AllDevicesCache = null;
        DeviceGalleriesCache = null;
        FavoriteGalleryIds = [];
        LastScannedUtc = null;
    }

    public void SignOut()
    {
        // An explicit sign-out (as opposed to just closing the browser tab) means "stop working
        // on this account" - so unlike the tab-close case, this does stop the background image
        // cache and dispose its independent client.
        if (Email is not null)
            imageCache.UnregisterAndDispose(Email);

        Client?.Dispose();
        Client = null;
        Email = null;
        _cacheStore = null;
        IsScanning = false;
        ScanLog.Clear();
        InvalidateCaches();
        Changed?.Invoke();
    }

    // Loads whatever was cached on disk from a previous scan (any browser session, even after a
    // container restart), without hitting the Meural API. A no-op if this account has never
    // been scanned before.
    private async Task TryHydrateFromCacheAsync()
    {
        if (_cacheStore is null)
            return;

        try
        {
            var lastRefreshed = await _cacheStore.GetLastRefreshedUtcAsync(CancellationToken.None);
            if (lastRefreshed is null)
                return;

            AllGalleriesCache = await _cacheStore.GetGalleriesAsync(CancellationToken.None);
            AllItemsCache = await _cacheStore.GetAllItemsAsync(CancellationToken.None);
            AllDevicesCache = await _cacheStore.GetDevicesAsync(CancellationToken.None);
            DeviceGalleriesCache = await _cacheStore.GetDeviceGalleriesAsync(CancellationToken.None);
            FavoriteGalleryIds = await _cacheStore.GetFavoriteGalleryIdsAsync(CancellationToken.None);
            LastScannedUtc = lastRefreshed;
        }
        catch (Exception ex)
        {
            // Best-effort - a missing/corrupt cache file just means the Dashboard prompts to scan.
            ScanLog.Append($"Couldn't load the saved cache: {ex.Message}");
        }
    }

    // The one shared full account scan. Safe to call repeatedly - it always re-fetches
    // everything (that's what makes it a "rescan"); callers decide when that's warranted
    // (explicit Dashboard button) versus just reading the already-cached lists. Runs to
    // completion rather than accepting a CancellationToken: MainLayout blocks navigation for
    // the duration, so there's no "switched away" case to cancel for.
    public async Task ScanAsync()
    {
        if (IsScanning || _cacheStore is null)
            return;

        IsScanning = true;
        ScanLog.Clear();
        Changed?.Invoke();
        try
        {
            var client = Client ?? throw new InvalidOperationException("Not signed in.");
            await _cacheStore.FullRefreshAsync(client, ScanLog.AsProgress(), CancellationToken.None);
            await TryHydrateFromCacheAsync();

            if (Email is not null && AllItemsCache is { Count: > 0 } items)
                imageCache.QueueWarmup(Email, items);
        }
        catch (Exception ex)
        {
            ScanLog.Append($"Scan failed: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
            Changed?.Invoke();
        }
    }

    // Every gallery id loaded on at least one frame - used by Unused Playlists.
    public HashSet<long> GetFrameGalleryIds() =>
        (DeviceGalleriesCache?.Values ?? Enumerable.Empty<List<MeuralGallery>>())
            .SelectMany(galleries => galleries)
            .Where(g => g.Id.HasValue)
            .Select(g => g.Id!.Value)
            .ToHashSet();

    // Gallery id -> the alias(es) of every frame it's loaded on - used by the Playlists table's
    // Frames column, mirroring the WinForms cache's GetGalleryFrameNamesAsync.
    public Dictionary<long, List<string>> GetFrameNamesByGallery()
    {
        var result = new Dictionary<long, List<string>>();
        if (DeviceGalleriesCache is null || AllDevicesCache is null)
            return result;

        foreach (var device in AllDevicesCache)
        {
            if (device.Id is not long deviceId || !DeviceGalleriesCache.TryGetValue(deviceId, out var galleries))
                continue;

            var label = device.Alias ?? $"Frame {deviceId}";
            foreach (var gallery in galleries)
            {
                if (gallery.Id is not long galleryId)
                    continue;
                if (!result.TryGetValue(galleryId, out var names))
                    result[galleryId] = names = [];
                if (!names.Contains(label))
                    names.Add(label);
            }
        }

        return result;
    }

    // Item id -> the name(s) of every playlist it belongs to - used by the All Pictures page's
    // Playlists column, mirroring GetFrameNamesByGallery's shape.
    public Dictionary<long, List<string>> GetPlaylistNamesByItem()
    {
        var result = new Dictionary<long, List<string>>();
        if (AllGalleriesCache is null)
            return result;

        foreach (var gallery in AllGalleriesCache)
        {
            if (gallery.ItemIds is not { Count: > 0 } itemIds)
                continue;

            var label = gallery.Name ?? "(untitled)";
            foreach (var itemId in itemIds)
            {
                if (!result.TryGetValue(itemId, out var names))
                    result[itemId] = names = [];
                if (!names.Contains(label))
                    names.Add(label);
            }
        }

        return result;
    }

    public async Task AddGalleryAsync(MeuralGallery gallery)
    {
        AllGalleriesCache ??= [];
        AllGalleriesCache.Add(gallery);

        if (_cacheStore is not null && gallery.Id is long id)
            await _cacheStore.UpsertGalleryAsync(id, gallery.Name, CancellationToken.None);
    }

    public async Task RenameGalleryAsync(long galleryId, string? name)
    {
        if (AllGalleriesCache is not null)
        {
            var idx = AllGalleriesCache.FindIndex(g => g.Id == galleryId);
            if (idx >= 0)
                AllGalleriesCache[idx] = AllGalleriesCache[idx] with { Name = name };
        }

        if (_cacheStore is not null)
            await _cacheStore.UpsertGalleryAsync(galleryId, name, CancellationToken.None);
    }

    public async Task RenameItemAsync(long itemId, string? name)
    {
        if (AllItemsCache is not null)
        {
            var idx = AllItemsCache.FindIndex(i => i.Id == itemId);
            if (idx >= 0)
                AllItemsCache[idx] = AllItemsCache[idx] with { Name = name };
        }

        if (_cacheStore is not null)
            await _cacheStore.UpdateItemNameAsync(itemId, name, CancellationToken.None);
    }

    // Patches an item's cached signed Image URL - called by ItemImage after it re-fetches a
    // stale (expired-signature) URL from the API, so the next hydration doesn't hit the same
    // dead link.
    public async Task UpdateItemImageAsync(long itemId, string? image)
    {
        if (AllItemsCache is not null)
        {
            var idx = AllItemsCache.FindIndex(i => i.Id == itemId);
            if (idx >= 0)
                AllItemsCache[idx] = AllItemsCache[idx] with { Image = image };
        }

        if (_cacheStore is not null)
            await _cacheStore.UpdateItemImageAsync(itemId, image, CancellationToken.None);
    }

    // Replaces a playlist's items both in memory and on disk, and merges the (possibly new,
    // e.g. just-uploaded) items into the account-wide items cache - the combined replacement
    // for what used to be two separate calls (SetGalleryItemIds + AddOrUpdateItems).
    public async Task SetGalleryItemsAsync(long galleryId, List<MeuralItem> items)
    {
        var itemIds = items.Where(i => i.Id.HasValue).Select(i => i.Id!.Value).ToList();

        if (AllGalleriesCache is not null)
        {
            var idx = AllGalleriesCache.FindIndex(g => g.Id == galleryId);
            if (idx >= 0)
                AllGalleriesCache[idx] = AllGalleriesCache[idx] with { ItemIds = itemIds };
        }

        AllItemsCache ??= [];
        foreach (var item in items)
        {
            if (item.Id is not long id)
                continue;
            var idx = AllItemsCache.FindIndex(i => i.Id == id);
            if (idx >= 0)
                AllItemsCache[idx] = item;
            else
                AllItemsCache.Add(item);
        }

        if (_cacheStore is not null)
            await _cacheStore.ReplaceGalleryItemsAsync(galleryId, items, CancellationToken.None);
    }

    public async Task RemoveGalleryAsync(long galleryId)
    {
        AllGalleriesCache?.RemoveAll(g => g.Id == galleryId);
        FavoriteGalleryIds.Remove(galleryId);

        if (_cacheStore is not null)
            await _cacheStore.RemoveGalleryAsync(galleryId, CancellationToken.None);
    }

    public async Task SetGalleryFavoriteAsync(long galleryId, bool favorite)
    {
        if (favorite)
            FavoriteGalleryIds.Add(galleryId);
        else
            FavoriteGalleryIds.Remove(galleryId);

        if (_cacheStore is not null)
            await _cacheStore.SetGalleryFavoriteAsync(galleryId, favorite, CancellationToken.None);
    }

    public async Task RemoveItemsAsync(IEnumerable<long> itemIds)
    {
        var ids = itemIds.ToHashSet();
        AllItemsCache?.RemoveAll(i => i.Id is long id && ids.Contains(id));

        if (Email is not null)
        {
            imageCache.RemoveCached(Email, ids);
            // A cropped item deleted here (e.g. via Orphan Uploads cleanup) without ever being
            // reverted would otherwise leave its stored pre-crop original on disk forever.
            foreach (var id in ids)
                EditOriginalStore.RemoveOriginal(OriginalsDir, id);
        }

        if (AllGalleriesCache is not null)
        {
            for (var i = 0; i < AllGalleriesCache.Count; i++)
            {
                var gallery = AllGalleriesCache[i];
                if (gallery.ItemIds is not { Count: > 0 } galleryItemIds || !galleryItemIds.Any(ids.Contains))
                    continue;
                AllGalleriesCache[i] = gallery with { ItemIds = galleryItemIds.Where(id => !ids.Contains(id)).ToList() };
            }
        }

        if (_cacheStore is not null)
            await _cacheStore.RemoveItemsAsync(ids, CancellationToken.None);
    }

    public async Task AddDeviceGalleryAsync(long deviceId, MeuralGallery gallery)
    {
        DeviceGalleriesCache ??= [];
        if (!DeviceGalleriesCache.TryGetValue(deviceId, out var list))
            DeviceGalleriesCache[deviceId] = list = [];
        if (gallery.Id is long id && !list.Any(g => g.Id == id))
            list.Add(gallery);

        if (_cacheStore is not null && gallery.Id is long galleryId)
            await _cacheStore.AddDeviceGalleryAsync(deviceId, galleryId, CancellationToken.None);
    }

    // Whether itemId has a locally-stored pre-crop original it can be reverted to - used by the
    // Playlists preview pane's Revert button.
    public bool HasEditOriginal(long itemId) =>
        Email is not null && EditOriginalStore.TryGetOriginalFile(OriginalsDir, itemId, out _);

    public string? GetEditOriginalPath(long itemId) =>
        Email is not null && EditOriginalStore.TryGetOriginalFile(OriginalsDir, itemId, out var path) ? path : null;

    // Every item id that currently has a locally-stored pre-crop original - one directory
    // listing instead of a HasEditOriginal disk check per tile, for badging the item grid.
    public HashSet<long> GetEditedItemIds()
    {
        if (Email is null || !Directory.Exists(OriginalsDir))
            return [];

        var ids = new HashSet<long>();
        foreach (var file in Directory.EnumerateFiles(OriginalsDir))
        {
            if (long.TryParse(Path.GetFileNameWithoutExtension(file), out var id))
                ids.Add(id);
        }
        return ids;
    }

    // Called after cropping an existing item (oldItemId -> newItemId, via the replace flow's
    // upload+swap+delete). If oldItemId already had a stored original - i.e. it was itself the
    // result of an earlier crop - that original is carried forward under newItemId so Revert
    // always restores the true pre-edit source rather than just undoing the latest crop.
    // Otherwise freshOriginalPath (oldItemId's own pre-crop bytes, captured by the caller before
    // oldItemId got deleted) becomes the new stored original.
    public void RecordCropOriginal(long oldItemId, long newItemId, string? freshOriginalPath)
    {
        if (Email is null)
            return;

        if (EditOriginalStore.TryRenameOriginal(OriginalsDir, oldItemId, newItemId, out _))
            return;

        if (freshOriginalPath is not null)
            EditOriginalStore.SaveOriginal(OriginalsDir, newItemId, freshOriginalPath);
    }

    // Called after uploading a cropped brand-new file - there's no prior item to carry an
    // original forward from, so this just stores the pre-crop bytes directly under the newly
    // uploaded item's id.
    public void SaveUploadOriginal(long itemId, string originalFilePath)
    {
        if (Email is not null)
            EditOriginalStore.SaveOriginal(OriginalsDir, itemId, originalFilePath);
    }

    // Clears a stored original - called after a successful Revert, since there's no more edit
    // left to undo.
    public void ClearEditOriginal(long itemId)
    {
        if (Email is not null)
            EditOriginalStore.RemoveOriginal(OriginalsDir, itemId);
    }

    // AI naming settings (provider, API keys, rename style) - stored per-account in the same
    // SQLite cache DB as playlists/items, not browser localStorage, so they survive a container
    // restart and aren't tied to one browser. API keys are Data-Protection-encrypted before they
    // reach the DB and decrypted here on the way back out.
    public async Task<AiSettings> LoadAiSettingsAsync()
    {
        if (_cacheStore is null)
            return new AiSettings();

        var providerRaw = await _cacheStore.GetSettingAsync("AiProvider", CancellationToken.None);
        var styleRaw = await _cacheStore.GetSettingAsync("AiRenameStyle", CancellationToken.None);
        var claudeKeyEnc = await _cacheStore.GetSettingAsync("AiClaudeApiKey", CancellationToken.None);
        var claudeWorkspaceId = await _cacheStore.GetSettingAsync("AiClaudeWorkspaceId", CancellationToken.None);
        var claudeModel = await _cacheStore.GetSettingAsync("AiClaudeModel", CancellationToken.None);
        var openAiKeyEnc = await _cacheStore.GetSettingAsync("AiOpenAiApiKey", CancellationToken.None);
        var openAiModel = await _cacheStore.GetSettingAsync("AiOpenAiModel", CancellationToken.None);

        return new AiSettings
        {
            Provider = Enum.TryParse<AiProvider>(providerRaw, out var provider) ? provider : AiProvider.Claude,
            RenameStyle = Enum.TryParse<RenameStyle>(styleRaw, out var style) ? style : RenameStyle.Professional,
            ClaudeApiKey = TryUnprotect(claudeKeyEnc),
            ClaudeWorkspaceId = claudeWorkspaceId,
            ClaudeModel = claudeModel,
            OpenAiApiKey = TryUnprotect(openAiKeyEnc),
            OpenAiModel = openAiModel,
        };
    }

    public async Task SaveAiSettingsAsync(AiSettings settings)
    {
        if (_cacheStore is null)
            return;

        await _cacheStore.SetSettingAsync("AiProvider", settings.Provider.ToString(), CancellationToken.None);
        await _cacheStore.SetSettingAsync("AiRenameStyle", settings.RenameStyle.ToString(), CancellationToken.None);
        await _cacheStore.SetSettingAsync("AiClaudeApiKey", Protect(settings.ClaudeApiKey), CancellationToken.None);
        // Not a secret (just an identifier used for request routing, not a credential) - stored
        // as plain text, unlike the API keys above.
        await _cacheStore.SetSettingAsync("AiClaudeWorkspaceId", settings.ClaudeWorkspaceId, CancellationToken.None);
        await _cacheStore.SetSettingAsync("AiClaudeModel", settings.ClaudeModel, CancellationToken.None);
        await _cacheStore.SetSettingAsync("AiOpenAiApiKey", Protect(settings.OpenAiApiKey), CancellationToken.None);
        await _cacheStore.SetSettingAsync("AiOpenAiModel", settings.OpenAiModel, CancellationToken.None);
    }

    // The frame remote control toolbar's instances (one device id per toolbar, in order - the
    // first is the non-removable master) - stored per-account like AiSettings above, since a
    // toolbar instance points at a specific device id from this account. Comma-separated with
    // empty segments for "no device picked yet" rather than JSON, since it's just a flat list of
    // nullable longs.
    public async Task<List<long?>> LoadRemoteToolbarDeviceIdsAsync()
    {
        if (_cacheStore is null)
            return [];

        var raw = await _cacheStore.GetSettingAsync("RemoteToolbarDeviceIds", CancellationToken.None);
        if (string.IsNullOrEmpty(raw))
            return [];

        return raw.Split(',')
            .Select(segment => long.TryParse(segment, out var id) ? (long?)id : null)
            .ToList();
    }

    public async Task SaveRemoteToolbarDeviceIdsAsync(IEnumerable<long?> deviceIds)
    {
        if (_cacheStore is null)
            return;

        var raw = string.Join(",", deviceIds.Select(id => id?.ToString() ?? ""));
        await _cacheStore.SetSettingAsync("RemoteToolbarDeviceIds", raw, CancellationToken.None);
    }

    private string? Protect(string? plainText) =>
        string.IsNullOrEmpty(plainText) ? null : _protector.Protect(plainText);

    private string? TryUnprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return null;

        try
        {
            return _protector.Unprotect(cipherText);
        }
        catch (CryptographicException)
        {
            // Data Protection keys were rotated/lost since this was saved - treat as "not set"
            // rather than throwing, the same way WebSessionStore treats an undecryptable session.
            return null;
        }
    }

    public void Dispose() => Client?.Dispose();
}
