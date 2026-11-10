using System.Collections.Concurrent;
using System.Text.Json;
using MeuralManager.Core.Api;
using MeuralManager.Core.Data;
using MeuralManager.Core.Models;
using MeuralManager.Core.Services;
using Microsoft.AspNetCore.DataProtection;

namespace MeuralManager.Web.Services;

// A frame to send a message to, as the caller knows it from the account scan.
public sealed record FrameMessageTarget(long DeviceId, string Name, string? LocalIp, FrameOrientation Orientation);

// One message pinned on one frame: what was put there, and everything needed to put the frame
// back the way it was. Stored per account (the FrameMessages settings row) so the restore still
// happens after a container restart.
public sealed record PinnedMessage
{
    public long DeviceId { get; init; }
    public string DeviceName { get; init; } = "";
    public string? LocalIp { get; init; }
    public string Text { get; init; } = "";
    public long ItemId { get; init; }
    public long GalleryId { get; init; }
    public DateTime SentUtc { get; init; }
    public DateTime UntilUtc { get; init; }

    // What the frame was showing (from its local API - null if it couldn't be reached) and its
    // playback settings before the message, put back on restore.
    public long? PreviousGalleryId { get; init; }
    public long? PreviousItemId { get; init; }
    public int? PreviousImageDuration { get; init; }
    public bool? PreviousSchedulerEnabled { get; init; }
    public bool? PreviousGalleryRotation { get; init; }

    public string? LastError { get; init; }
}

// Shows a message on one or more frames for a while, then puts each frame back.
//
// A message is its own one-picture playlist installed on the frame, with image rotation, the
// playlist schedule and playlist rotation switched off (PUT devices/{id}: imageDuration 0,
// schedulerEnabled and galleryRotation false), so neither the frame nor its schedule moves away
// from it - and a swipe at the frame has nowhere else to go. The frame's local "postcard" would
// need none of this, but it only stays up for the device's previewDuration, at most 100 seconds.
//
// App-wide and independent of any browser tab, like ImageCacheManager: the restore has to happen
// on time whether or not anyone has the app open. So the restore runs here as a background
// loop, signed in with the account's own refresh token, saved (Data-Protection-encrypted) when a
// message is sent. Sending goes through a caller-supplied client, so a future caller without a
// browser session (a Home Assistant webhook) can reuse SendAsync as is.
public sealed class FrameMessageService(IHostEnvironment env, IDataProtectionProvider dataProtection, ILogger<FrameMessageService> logger)
    : BackgroundService
{
    private const string MessagesSettingKey = "FrameMessages";
    private const string SessionSettingKey = "FrameMessageSession";
    private const string GalleryName = "Meural Manager message";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(90);

    private readonly string _cacheRoot = Environment.GetEnvironmentVariable("CACHE_ROOT_PATH")
        ?? Path.Combine(env.ContentRootPath, "cache");
    private readonly IDataProtector _protector = dataProtection.CreateProtector("MeuralManager.Web.FrameMessage");

    // Keyed by account folder name (the sanitized email), which is all the background loop knows
    // after a restart.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, MeuralApiClient> _clients = new();

    // Fired with the account key whenever that account's pinned messages change.
    public event Action<string>? Changed;

    public static string AccountKey(string email) => FileNaming.SanitizeFileName(email);

    private sealed record SavedSession(string TrustId, string RefreshToken);

    public async Task<List<PinnedMessage>> GetPinnedAsync(string email) =>
        await LoadAsync(StoreFor(AccountKey(email)), CancellationToken.None);

    // Pins the message on each target frame until untilUtc. Returns how many frames got it; what
    // happened on each goes to progress. A frame already showing a message gets the new one, and
    // still goes back to what it was showing before the first.
    public async Task<int> SendAsync(
        string email, MeuralApiClient client, string text, MessagePalette palette,
        IReadOnlyList<FrameMessageTarget> targets, DateTime untilUtc, IProgress<string>? progress, CancellationToken ct)
    {
        var key = AccountKey(email);
        var store = StoreFor(key);
        await SaveSessionAsync(store, client, ct);

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        var sent = new List<(FrameMessageTarget Target, long GalleryId)>();
        try
        {
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                if (await SendOneAsync(store, client, text, palette, target, untilUtc, progress, ct) is long galleryId)
                    sent.Add((target, galleryId));
            }
        }
        finally
        {
            gate.Release();
            Changed?.Invoke(key);
        }

        // Each frame takes a while to download its message; wait for them all at once.
        await Task.WhenAll(sent.Select(async s => progress?.Report(await WaitUntilShowingAsync(s.Target, s.GalleryId, ct)
            ? $"{s.Target.Name}: showing the message."
            : $"{s.Target.Name}: sent. The frame shows it once it has downloaded it.")));

        return sent.Count;
    }

    public async Task RestoreNowAsync(string email, MeuralApiClient client, long deviceId, IProgress<string>? progress)
    {
        var key = AccountKey(email);
        var store = StoreFor(key);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var pinned = await LoadAsync(store, CancellationToken.None);
            if (pinned.Find(m => m.DeviceId == deviceId) is not { } message)
                return;

            await RestoreAndForgetAsync(store, client, pinned, message, progress);
        }
        finally
        {
            gate.Release();
            Changed?.Invoke(key);
        }
    }

    // The message's playlist id once it's on its way to the frame, null if it couldn't be sent.
    private async Task<long?> SendOneAsync(
        PlaylistCacheStore store, MeuralApiClient client, string text, MessagePalette palette,
        FrameMessageTarget target, DateTime untilUtc, IProgress<string>? progress, CancellationToken ct)
    {
        var pinned = await LoadAsync(store, ct);
        var previous = pinned.Find(m => m.DeviceId == target.DeviceId);
        var file = Path.Combine(Path.GetTempPath(), $"meural-message-{Guid.NewGuid():N}.jpg");
        PinnedMessage? message = null;

        try
        {
            progress?.Report($"{target.Name}: preparing the message...");

            // What to go back to. A frame already showing a message keeps what it had before that
            // one, not the message itself.
            var before = previous ?? await CaptureCurrentStateAsync(client, target, ct);

            var (width, height) = target.Orientation == FrameOrientation.Horizontal
                ? (MessageImageRenderer.LongSide, MessageImageRenderer.ShortSide)
                : (MessageImageRenderer.ShortSide, MessageImageRenderer.LongSide);
            await File.WriteAllBytesAsync(file, MessageImageRenderer.RenderJpeg(text, palette, width, height), ct);

            var item = await client.UploadItemAsync(file, $"Message {DateTime.Now:yyyy-MM-dd HH:mm}", ct: ct);
            var gallery = await client.CreateGalleryAsync(GalleryName, ct);
            if (item.Id is not long itemId || gallery.Id is not long galleryId)
                throw new MeuralApiException("Meural didn't return an id for the uploaded message.");

            // Saved before the frame is touched, so whatever happens next, the restore knows what
            // to clean up and what to put back.
            message = before with
            {
                DeviceId = target.DeviceId,
                DeviceName = target.Name,
                LocalIp = target.LocalIp,
                Text = text,
                ItemId = itemId,
                GalleryId = galleryId,
                SentUtc = DateTime.UtcNow,
                UntilUtc = untilUtc,
                LastError = null,
            };
            pinned.RemoveAll(m => m.DeviceId == target.DeviceId);
            pinned.Add(message);
            await SaveAsync(store, pinned, ct);

            await ExpectSuccess(client.AddItemToGalleryAsync(galleryId, itemId, ct), "add the message to its playlist");
            await client.UpdateDeviceSettingsAsync(target.DeviceId,
                new MeuralDeviceSettings { ImageDuration = 0, SchedulerEnabled = false, GalleryRotation = false }, ct);
            await ExpectSuccess(client.AddGalleryToDeviceAsync(target.DeviceId, galleryId, ct), "send the message to the frame");

            if (previous is not null)
                await RemoveMessageFromAccountAsync(client, previous);

            progress?.Report($"{target.Name}: sent, waiting for the frame to show it...");
            return galleryId;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report($"{target.Name}: couldn't send the message ({ex.Message}).");
            // Half-sent - put the frame back now rather than leaving it frozen until the end time.
            if (message is not null)
            {
                await RestoreAndForgetAsync(store, client, await LoadAsync(store, CancellationToken.None), message, progress);
                // The new message replaced an earlier one in the saved list, so nothing else
                // would clean that one up.
                if (previous is not null)
                    await RemoveMessageFromAccountAsync(client, previous);
            }
            return null;
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task<PinnedMessage> CaptureCurrentStateAsync(MeuralApiClient client, FrameMessageTarget target, CancellationToken ct)
    {
        var settings = await client.GetDeviceSettingsAsync(target.DeviceId, ct);
        var status = target.LocalIp is { Length: > 0 } ip
            ? await MeuralLocalDeviceClient.GetGalleryStatusAsync(ip, ct)
            : null;

        return new PinnedMessage
        {
            PreviousGalleryId = status?.CurrentGalleryId,
            PreviousItemId = status?.CurrentItemId,
            PreviousImageDuration = settings.ImageDuration,
            PreviousSchedulerEnabled = settings.SchedulerEnabled,
            PreviousGalleryRotation = settings.GalleryRotation,
        };
    }

    // Installing a playlist from the cloud makes the frame download it, and it only switches once
    // that's done; asking it locally to switch confirms (and hurries) that. False if the frame
    // can't be reached locally or hasn't switched within the timeout.
    private static async Task<bool> WaitUntilShowingAsync(FrameMessageTarget target, long galleryId, CancellationToken ct)
    {
        if (target.LocalIp is not { Length: > 0 } ip)
            return false;

        var deadline = DateTime.UtcNow + SwitchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            if ((await MeuralLocalDeviceClient.GetGalleryStatusAsync(ip, ct))?.CurrentGalleryId == galleryId)
                return true;
            await MeuralLocalDeviceClient.ChangeGalleryAsync(ip, galleryId, ct);
        }
        return false;
    }

    // Puts the frame back and removes the message from the account. Throws if the frame's
    // settings couldn't be put back (the message is then kept, with the error, for a retry);
    // anything after that is best-effort.
    private static async Task RestoreAsync(MeuralApiClient client, PinnedMessage message, IProgress<string>? progress)
    {
        await client.UpdateDeviceSettingsAsync(message.DeviceId, new MeuralDeviceSettings
        {
            ImageDuration = message.PreviousImageDuration,
            SchedulerEnabled = message.PreviousSchedulerEnabled,
            GalleryRotation = message.PreviousGalleryRotation,
        });

        // Back to the playlist and picture it was on, before the message's playlist goes away -
        // otherwise the frame picks some other playlist by itself.
        if (message.LocalIp is { Length: > 0 } ip && message.PreviousGalleryId is long galleryId)
        {
            await MeuralLocalDeviceClient.ChangeGalleryAsync(ip, galleryId);
            if (message.PreviousItemId is long itemId)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                await MeuralLocalDeviceClient.ChangeItemAsync(ip, itemId);
            }
        }

        await RemoveMessageFromAccountAsync(client, message);
        progress?.Report($"{message.DeviceName}: back to normal.");
    }

    private static async Task RemoveMessageFromAccountAsync(MeuralApiClient client, PinnedMessage message)
    {
        try
        {
            await client.RemoveGalleryFromDeviceAsync(message.DeviceId, message.GalleryId);
            await client.DeleteGalleryAsync(message.GalleryId);
            await client.DeleteItemAsync(message.ItemId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or MeuralApiException)
        {
            // Best-effort: a leftover picture shows up under Orphan Uploads, a leftover playlist
            // under Unused Playlists.
        }
    }

    private async Task RestoreAndForgetAsync(
        PlaylistCacheStore store, MeuralApiClient client, List<PinnedMessage> pinned, PinnedMessage message, IProgress<string>? progress)
    {
        try
        {
            await RestoreAsync(client, message, progress);
            pinned.RemoveAll(m => m.DeviceId == message.DeviceId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or MeuralApiException or MeuralAuthException)
        {
            progress?.Report($"{message.DeviceName}: couldn't put the frame back ({ex.Message}). Trying again shortly.");
            var index = pinned.FindIndex(m => m.DeviceId == message.DeviceId);
            if (index >= 0)
                pinned[index] = pinned[index] with { LastError = ex.Message };
        }

        await SaveAsync(store, pinned, CancellationToken.None);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                await RestoreDueMessagesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Restoring frames after a message failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RestoreDueMessagesAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_cacheRoot))
            return;

        foreach (var dbPath in Directory.EnumerateFiles(_cacheRoot, "meural-cache.db", SearchOption.AllDirectories))
        {
            var key = Path.GetFileName(Path.GetDirectoryName(dbPath)!);
            var store = new PlaylistCacheStore(dbPath);
            if (!(await LoadAsync(store, ct)).Any(m => m.UntilUtc <= DateTime.UtcNow))
                continue;

            var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                // Re-read under the lock: a send or "Restore now" may have just changed the list.
                var pinned = await LoadAsync(store, ct);
                var due = pinned.Where(m => m.UntilUtc <= DateTime.UtcNow).ToList();
                if (due.Count == 0)
                    continue;

                var client = await GetBackgroundClientAsync(key, store, ct);
                if (client is null)
                {
                    foreach (var message in due)
                    {
                        var index = pinned.FindIndex(m => m.DeviceId == message.DeviceId);
                        pinned[index] = message with { LastError = "Couldn't sign in to Meural. Sign in to the app to put the frame back." };
                    }
                    await SaveAsync(store, pinned, ct);
                    continue;
                }

                foreach (var message in due)
                    await RestoreAndForgetAsync(store, client, pinned, message, progress: null);

                // A refresh token that stopped working leaves a client that can't do anything;
                // drop it so the next round signs in again from the saved session.
                if (pinned.Any(m => m.LastError is not null) && _clients.TryRemove(key, out var stale))
                    stale.Dispose();
            }
            finally
            {
                gate.Release();
                Changed?.Invoke(key);
            }
        }
    }

    // The account's own client for the background restore, signed in with the refresh token
    // saved at the last send.
    private async Task<MeuralApiClient?> GetBackgroundClientAsync(string key, PlaylistCacheStore store, CancellationToken ct)
    {
        if (_clients.TryGetValue(key, out var existing))
            return existing;

        var raw = await store.GetSettingAsync(SessionSettingKey, ct);
        SavedSession? saved;
        try
        {
            saved = raw is null ? null : JsonSerializer.Deserialize<SavedSession>(_protector.Unprotect(raw));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            saved = null;
        }
        if (saved is null)
            return null;

        var client = new MeuralApiClient(saved.TrustId);
        if (!await client.TryRestoreSessionAsync(saved.RefreshToken, ct))
        {
            client.Dispose();
            return null;
        }

        return _clients.GetOrAdd(key, client);
    }

    private async Task SaveSessionAsync(PlaylistCacheStore store, MeuralApiClient client, CancellationToken ct)
    {
        if (client.RefreshToken is not { } refreshToken)
            return;

        var json = JsonSerializer.Serialize(new SavedSession(client.TrustId, refreshToken));
        await store.SetSettingAsync(SessionSettingKey, _protector.Protect(json), ct);
    }

    private static async Task ExpectSuccess(Task<DeleteOutcome> call, string what)
    {
        var outcome = await call;
        if (!outcome.Success)
            throw new MeuralApiException($"Couldn't {what} ({(int)outcome.StatusCode}).");
    }

    private PlaylistCacheStore StoreFor(string key) => new(Path.Combine(_cacheRoot, key, "meural-cache.db"));

    private static async Task<List<PinnedMessage>> LoadAsync(PlaylistCacheStore store, CancellationToken ct)
    {
        var raw = await store.GetSettingAsync(MessagesSettingKey, ct);
        if (string.IsNullOrEmpty(raw))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<PinnedMessage>>(raw) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Task SaveAsync(PlaylistCacheStore store, List<PinnedMessage> pinned, CancellationToken ct) =>
        store.SetSettingAsync(MessagesSettingKey, pinned.Count == 0 ? null : JsonSerializer.Serialize(pinned), ct);

    public override void Dispose()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        base.Dispose();
    }
}
