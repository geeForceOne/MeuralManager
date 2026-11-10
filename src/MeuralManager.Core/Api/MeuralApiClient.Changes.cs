using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MeuralManager.Core.Models;
using MeuralManager.Core.Services;

namespace MeuralManager.Core.Api;

// Every call that changes something on the Meural account. With SmartTimeouts off (the default)
// each one is a single request with the 30-second limit, as it always was. With it on, each runs
// through TimeoutGuard with a cheap check of its own that tells whether Meural already made the
// change, so a slow or missing reply no longer reads as a failure.
public sealed partial class MeuralApiClient
{
    // The account's "Experimental: smarter timeout handling" setting, applied by the web app on
    // sign-in and whenever it changes.
    public bool SmartTimeouts { get; set; }

    // Items this client has uploaded in this session, so a check never takes one of them for the
    // next upload's result.
    private readonly HashSet<long> _uploadedItemIds = [];

    private Task<T> GuardedAsync<T>(
        Func<CancellationToken, Task<T>> call,
        Func<CancellationToken, Task<TimeoutGuard.Probe<T>>> check,
        CancellationToken ct,
        Func<T, bool>? isUncertainResult = null) =>
        SmartTimeouts ? TimeoutGuard.RunAsync(call, check, ct, isUncertainResult) : call(ct);

    private static bool IsUncertainOutcome(DeleteOutcome outcome) =>
        !outcome.Success && TimeoutGuard.IsUncertain(outcome.StatusCode);

    private static TimeoutGuard.Probe<DeleteOutcome> Done(bool done) =>
        done ? TimeoutGuard.Probe<DeleteOutcome>.Yes(new DeleteOutcome(true, HttpStatusCode.OK)) : TimeoutGuard.Probe<DeleteOutcome>.No;

    public async Task<DeleteOutcome> DeleteItemAsync(long itemId, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        return await GuardedAsync(
            async t =>
            {
                using var response = await _http.DeleteAsync($"{ApiBaseUrl}items/{itemId}", t);
                return new DeleteOutcome(response.IsSuccessStatusCode, response.StatusCode);
            },
            async t => Done(await IsGoneAsync($"items/{itemId}", t)),
            ct, IsUncertainOutcome);
    }

    public async Task<DeleteOutcome> DeleteGalleryAsync(long galleryId, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        return await GuardedAsync(
            async t =>
            {
                using var response = await _http.DeleteAsync($"{ApiBaseUrl}galleries/{galleryId}", t);
                return new DeleteOutcome(response.IsSuccessStatusCode, response.StatusCode);
            },
            async t => Done(await IsGoneAsync($"galleries/{galleryId}", t)),
            ct, IsUncertainOutcome);
    }

    public async Task<MeuralGallery> CreateGalleryAsync(string name, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        // To recognize the new playlist, the check needs to know which ones by that name existed
        // before - so with the setting on, creating a playlist costs one listing up front.
        HashSet<long>? existing = null;
        if (SmartTimeouts)
        {
            existing = (await GetAllGalleriesAsync(ct: ct))
                .Where(g => g.Id is not null && string.Equals(g.Name, name, StringComparison.Ordinal))
                .Select(g => g.Id!.Value)
                .ToHashSet();
        }

        return await GuardedAsync(
            async t =>
            {
                var gallery = await PostJsonAsync<GalleryEnvelope>("galleries", new { name }, t);
                return gallery.Data ?? throw new MeuralApiException("Create gallery response had no data.");
            },
            async t =>
            {
                var created = (await GetAllGalleriesAsync(ct: t)).FirstOrDefault(g =>
                    g.Id is long id && !existing!.Contains(id) && string.Equals(g.Name, name, StringComparison.Ordinal));
                return created is null ? TimeoutGuard.Probe<MeuralGallery>.No : TimeoutGuard.Probe<MeuralGallery>.Yes(created);
            },
            ct);
    }

    public async Task<MeuralGallery> RenameGalleryAsync(long galleryId, string name, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        return await GuardedAsync(
            async t =>
            {
                var gallery = await PutJsonAsync<GalleryEnvelope>($"galleries/{galleryId}", new { name }, t);
                return gallery.Data ?? throw new MeuralApiException("Rename gallery response had no data.");
            },
            async t => await GetGalleryOrNullAsync(galleryId, t) is { } g && string.Equals(g.Name, name, StringComparison.Ordinal)
                ? TimeoutGuard.Probe<MeuralGallery>.Yes(g)
                : TimeoutGuard.Probe<MeuralGallery>.No,
            ct);
    }

    public async Task<MeuralItem> RenameItemAsync(long itemId, string name, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        return await GuardedAsync(
            async t =>
            {
                var item = await PutJsonAsync<MeuralItemEnvelope>($"items/{itemId}", new { name }, t);
                return item.Data ?? throw new MeuralApiException("Rename item response had no data.");
            },
            async t => await GetItemAsync(itemId, t) is { } i && string.Equals(i.Name, name, StringComparison.Ordinal)
                ? TimeoutGuard.Probe<MeuralItem>.Yes(i)
                : TimeoutGuard.Probe<MeuralItem>.No,
            ct);
    }

    public async Task<DeleteOutcome> AddItemToGalleryAsync(long galleryId, long itemId, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        return await GuardedAsync(
            async t =>
            {
                using var response = await _http.PostAsync($"{ApiBaseUrl}galleries/{galleryId}/items/{itemId}", content: null, t);
                return new DeleteOutcome(response.IsSuccessStatusCode, response.StatusCode);
            },
            async t => Done((await GetGalleryOrNullAsync(galleryId, t))?.ItemIds?.Contains(itemId) == true),
            ct, IsUncertainOutcome);
    }

    public async Task<DeleteOutcome> RemoveItemFromGalleryAsync(long galleryId, long itemId, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        return await GuardedAsync(
            async t =>
            {
                using var response = await _http.DeleteAsync($"{ApiBaseUrl}galleries/{galleryId}/items/{itemId}", t);
                return new DeleteOutcome(response.IsSuccessStatusCode, response.StatusCode);
            },
            async t => Done(await GetGalleryOrNullAsync(galleryId, t) is { ItemIds: { } ids } && !ids.Contains(itemId)),
            ct, IsUncertainOutcome);
    }

    // Loads (installs) a gallery on a device - confirmed against davemorin/meural-manager's
    // server.js and the Home Assistant integration's pymeural.py (device_load_gallery), per
    // CLAUDE.md's rule to verify undocumented endpoints there rather than guess. Neither of those
    // two references exposes a way to unload a gallery from a device, but Meural's own Postman
    // API documentation (documenter.getpostman.com/view/1657302/RVnWjKUL) does - see
    // RemoveGalleryFromDeviceAsync below.
    public async Task<DeleteOutcome> AddGalleryToDeviceAsync(long deviceId, long galleryId, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        return await GuardedAsync(
            async t =>
            {
                using var response = await _http.PostAsync($"{ApiBaseUrl}devices/{deviceId}/galleries/{galleryId}", content: null, t);
                return new DeleteOutcome(response.IsSuccessStatusCode, response.StatusCode);
            },
            async t => Done((await GetDeviceGalleriesAsync(deviceId, ct: t)).Any(g => g.Id == galleryId)),
            ct, IsUncertainOutcome);
    }

    // Unloads (removes) a gallery from a device - the DELETE counterpart to
    // AddGalleryToDeviceAsync's POST, confirmed via Meural's own Postman API documentation
    // (documenter.getpostman.com/view/1657302/RVnWjKUL, "Remove a gallery from the specified
    // device"), since neither davemorin/meural-manager nor ha-meural expose this one.
    public async Task<DeleteOutcome> RemoveGalleryFromDeviceAsync(long deviceId, long galleryId, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        return await GuardedAsync(
            async t =>
            {
                using var response = await _http.DeleteAsync($"{ApiBaseUrl}devices/{deviceId}/galleries/{galleryId}", t);
                return new DeleteOutcome(response.IsSuccessStatusCode, response.StatusCode);
            },
            async t => Done(!(await GetDeviceGalleriesAsync(deviceId, ct: t)).Any(g => g.Id == galleryId)),
            ct, IsUncertainOutcome);
    }

    public async Task<MeuralItem> UploadItemAsync(string filePath, string? name, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        // An uploaded image first gets a random name from Meural (renamed below), so the check
        // can't look for it by name. Instead it looks for an image newer than the newest one
        // before the upload. Meural lists uploads newest first (its Postman docs' example); if it
        // ever didn't, the first page would hold nothing newer and the check would just never
        // match - a missed recovery, never a wrong one.
        long newestBefore = 0;
        if (SmartTimeouts)
            newestBefore = (await GetNewestItemsAsync(ct)).Select(i => i.Id ?? 0).DefaultIfEmpty(0).Max();

        progress?.Report($"Uploading \"{Path.GetFileName(filePath)}\"...");
        var item = await GuardedAsync(
            t => PostItemAsync(filePath, SmartTimeouts ? _uploadHttp : _http, t),
            async t =>
            {
                var candidates = (await GetNewestItemsAsync(t))
                    .Where(i => i.Id is long id && id > newestBefore && !_uploadedItemIds.Contains(id))
                    .ToList();
                // More than one new image means something else uploaded at the same time; rather
                // than guess, keep waiting for the reply.
                if (candidates.Count != 1)
                    return TimeoutGuard.Probe<MeuralItem>.No;
                progress?.Report($"Meural already has \"{Path.GetFileName(filePath)}\"; not waiting for its reply.");
                return TimeoutGuard.Probe<MeuralItem>.Yes(candidates[0]);
            },
            ct);

        if (item.Id is long itemId)
        {
            _uploadedItemIds.Add(itemId);
            if (!string.IsNullOrWhiteSpace(name))
                item = await RenameItemAsync(itemId, name, ct);
        }

        return item;
    }

    private async Task<MeuralItem> PostItemAsync(string filePath, HttpClient http, CancellationToken ct)
    {
        using var fileStream = File.OpenRead(filePath);
        using var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(FileNaming.GuessContentType(filePath));

        using var form = new MultipartFormDataContent
        {
            { streamContent, "image", Path.GetFileName(filePath) },
        };

        using var response = await http.PostAsync($"{ApiBaseUrl}items", form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new MeuralApiException($"Upload failed ({(int)response.StatusCode}): {body}", response.StatusCode);

        var envelope = JsonSerializer.Deserialize<MeuralItemEnvelope>(body)
            ?? throw new MeuralApiException("Failed to deserialize upload response.");
        return envelope.Data ?? throw new MeuralApiException("Upload response had no data.");
    }

    // The account's newest uploads, one page - all an upload check needs.
    public async Task<List<MeuralItem>> GetNewestItemsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{ApiBaseUrl}user/items?count=20&page=1", ct);
        response.EnsureSuccessStatusCode();
        var page = JsonSerializer.Deserialize<MeuralPage<MeuralItem>>(await response.Content.ReadAsStringAsync(ct))
            ?? throw new MeuralApiException("Failed to deserialize the newest uploads.");
        return page.Data;
    }

    // GET galleries/{id} (in Meural's Postman docs): the playlist with its itemIds, in one request
    // however long it is. Null when it no longer exists.
    private async Task<MeuralGallery?> GetGalleryOrNullAsync(long galleryId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{ApiBaseUrl}galleries/{galleryId}", ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return null;
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<GalleryEnvelope>(await response.Content.ReadAsStringAsync(ct))?.Data;
    }

    // True when Meural says the thing is gone (404/410); any other error throws, which a check
    // treats as "don't know yet".
    private async Task<bool> IsGoneAsync(string endpoint, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{ApiBaseUrl}{endpoint}", ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return true;
        response.EnsureSuccessStatusCode();
        return false;
    }
}
