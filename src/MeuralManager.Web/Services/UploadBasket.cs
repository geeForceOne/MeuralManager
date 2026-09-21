using MeuralManager.Core.Models;
using MeuralManager.Core.Services;
using MeuralManager.Web.Components.Shared;

namespace MeuralManager.Web.Services;

// One photo waiting in an upload basket, whatever it came from (Immich today, Google Photos next).
// Source is the page's own photo object, handed back to its prepare/AI-image callbacks.
public sealed record BasketEntry(string Id, string ThumbUrl, string SourceName, string? Taken, object Source)
{
    // What the Meural item is called unless the user (or the AI) picks something else: the file
    // name without its extension - the upload names items after the file.
    public string DefaultName => Path.GetFileNameWithoutExtension(SourceName);
}

// What a source's prepare step hands back for one entry: the file to upload and, if it was cropped,
// a file holding the pre-crop bytes so the crop can be reverted later like any other cropped item.
public sealed record PreparedUpload(string FilePath, string? OriginalPath = null);

// Shared state for one upload run, passed to the source's prepare step for every entry.
public sealed class PrepareContext
{
    public required string TempDir { get; init; }
    public required CropDialog.CropAspect CropAspect { get; init; }
    public required int Total { get; init; }

    // Starts as "is the crop tool on", and the source sets it false when the user picks "upload all
    // as-is", so the rest of the batch skips the crop prompt.
    public bool Cropping { get; set; }

    public int Index { get; set; }
    public int Remaining => Total - Index;
}

// Turns one basket entry into a file ready to upload (downloading it, and cropping it if asked to),
// named after baseName. Return null to leave the entry out (e.g. its crop was cancelled); throw to
// report it as failed.
public delegate Task<PreparedUpload?> PrepareUploadAsync(
    BasketEntry entry, string baseName, PrepareContext context, CancellationToken ct);

// Everything about an upload basket that doesn't depend on where the photos come from: what's in
// it, the names chosen for them, the target playlist, the AI naming, and the upload itself. A page
// (Immich, Google Photos) owns one, feeds it entries, and supplies the source-specific parts as
// callbacks. UploadBasketPane and UploadPreviewPane render it.
public sealed class UploadBasket : IDisposable
{
    private readonly MeuralSessionState _session;
    private readonly ActivityLog _log;
    private readonly Action<string> _showError;
    private readonly Action<string> _showSuccess;
    private readonly Func<BasketEntry, CancellationToken, Task<(byte[] Bytes, string MediaType)>> _loadAiImage;

    private readonly List<BasketEntry> _entries = [];
    private readonly HashSet<string> _selectedIds = [];

    // Names the user (or the AI) has chosen, by entry id. An entry without one uses its DefaultName.
    private readonly Dictionary<string, string> _names = [];

    private CancellationTokenSource? _cts;

    // The last existing-items list built for the "already in this playlist" view - see ExistingItems.
    private (List<long>? ItemIds, List<MeuralItem>? All, List<MeuralItem> Items)? _existingMemo;

    public UploadBasket(
        MeuralSessionState session,
        ActivityLog log,
        Action<string> showError,
        Action<string> showSuccess,
        Func<BasketEntry, CancellationToken, Task<(byte[] Bytes, string MediaType)>> loadAiImage)
    {
        _session = session;
        _log = log;
        _showError = showError;
        _showSuccess = showSuccess;
        _loadAiImage = loadAiImage;
    }

    // Fired whenever the basket's state changes. The grid, the basket pane and the preview pane are
    // sibling components, and a click in one only re-renders that one - so the page listens to this
    // and re-renders them all (a ✕ in the basket has to clear the tick on the photo grid, and so on).
    // Also covers changes outside any UI event, like AI names arriving one by one.
    public event Action? Changed;

    // ---------- contents ----------

    // In the order they were added.
    public IReadOnlyList<BasketEntry> Entries => _entries;
    public HashSet<string> SelectedIds => _selectedIds;
    public bool Contains(string id) => _selectedIds.Contains(id);

    public void SetSelected(BasketEntry entry, bool selected)
    {
        if (selected)
        {
            if (_selectedIds.Add(entry.Id))
                _entries.Add(entry);
            Changed?.Invoke();
        }
        else
        {
            Remove(entry.Id);
        }
    }

    public void Remove(string id)
    {
        if (_selectedIds.Remove(id))
        {
            _entries.RemoveAll(e => e.Id == id);
            _names.Remove(id);
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        _entries.Clear();
        _selectedIds.Clear();
        _names.Clear();
        Changed?.Invoke();
    }

    // ---------- preview ----------

    // The photo shown in the preview pane - independent of the basket (a plain click previews, it
    // doesn't select). Or, instead, an image already in the target playlist; only one is ever set.
    public BasketEntry? PreviewEntry { get; private set; }
    public MeuralItem? PreviewItem { get; private set; }

    public void Preview(BasketEntry entry)
    {
        PreviewEntry = entry;
        PreviewItem = null;
        Changed?.Invoke();
    }

    public void PreviewExisting(MeuralItem item)
    {
        PreviewItem = item;
        PreviewEntry = null;
        Changed?.Invoke();
    }

    // ---------- names ----------

    public string NameFor(BasketEntry entry) => _names.TryGetValue(entry.Id, out var name) ? name : entry.DefaultName;

    public void SetName(BasketEntry entry, string? value)
    {
        var name = value?.Trim();
        // Blank, or just the default again, isn't a choice - fall back to the default.
        if (string.IsNullOrEmpty(name) || name == entry.DefaultName)
            _names.Remove(entry.Id);
        else
            _names[entry.Id] = name;
        Changed?.Invoke();
    }

    // ---------- target playlist ----------

    private long? _targetGalleryId;

    public long? TargetGalleryId
    {
        get => _targetGalleryId;
        set
        {
            _targetGalleryId = value;
            Changed?.Invoke();
        }
    }

    // Whether the basket also lists what the chosen playlist already holds (read-only).
    public bool ShowExisting { get; set; }

    // Favorites float to the top, same as on the Playlists page, then everything else A-Z.
    public List<MeuralGallery> AllPlaylists =>
        (_session.AllGalleriesCache ?? [])
            .OrderByDescending(IsFavorite)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool IsFavorite(MeuralGallery gallery) => gallery.Id is long id && _session.FavoriteGalleryIds.Contains(id);

    public MeuralGallery? SelectedPlaylist => AllPlaylists.FirstOrDefault(p => p.Id == TargetGalleryId);

    // The playlist's images in playlist order, from the scan's cache (so as fresh as the last scan
    // or app action - no Meural call). Rebuilt only when the playlist's id list or the item cache
    // is replaced, since render calls this every time.
    public List<MeuralItem> ExistingItems(MeuralGallery playlist)
    {
        var all = _session.AllItemsCache;
        if (_existingMemo is { } memo && ReferenceEquals(memo.ItemIds, playlist.ItemIds) && ReferenceEquals(memo.All, all))
            return memo.Items;

        var byId = (all ?? []).Where(i => i.Id.HasValue).ToDictionary(i => i.Id!.Value);
        var items = (playlist.ItemIds ?? [])
            .Select(id => byId.TryGetValue(id, out var item) ? item : null)
            .OfType<MeuralItem>()
            .ToList();
        _existingMemo = (playlist.ItemIds, all, items);
        return items;
    }

    // Says which crop format the dialog will open on for the chosen playlist, and why - see
    // MeuralSessionState.GetCropSuggestion.
    public string CropHint
    {
        get
        {
            if (TargetGalleryId is null)
                return "Choose a playlist - crops open on the format of the frames it's installed on (9:16 by default).";

            var suggestion = _session.GetCropSuggestion(TargetGalleryId);
            var format = suggestion.Orientation == FrameOrientation.Horizontal ? "16:9 (horizontal)" : "9:16 (vertical)";
            return suggestion.FrameNames.Count > 0
                ? $"Crops open as {format} - this playlist is on {string.Join(", ", suggestion.FrameNames)}."
                : $"Crops open as {format} - this playlist isn't installed on a frame yet.";
        }
    }

    // Creates the playlist and makes it the target. The page asks for the name (it owns the dialog).
    public async Task CreatePlaylistAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        Busy = true;
        try
        {
            var gallery = await _session.Client!.CreateGalleryAsync(name);
            await _session.AddGalleryAsync(gallery);
            TargetGalleryId = gallery.Id;
            _log.Append($"Created playlist \"{gallery.Name}\".");
        }
        catch (Exception ex)
        {
            _log.Append($"Couldn't create playlist: {ex.Message}");
            _showError($"Couldn't create playlist: {ex.Message}");
        }
        finally
        {
            Busy = false;
        }
    }

    // ---------- AI naming ----------

    public AiSettings AiSettings { get; private set; } = new();

    public bool HasKey(AiProvider provider) => AiSettings.HasKeyFor(provider);
    public bool HasAnyKey => HasKey(AiProvider.Claude) || HasKey(AiProvider.ChatGpt);

    public string? SuggestingId { get; private set; }
    public AiProvider? SuggestingProvider { get; private set; }

    public async Task LoadAiSettingsAsync() => AiSettings = await _session.LoadAiSettingsAsync();

    // Asks the given provider (regardless of the default in Settings - the basket shows one button
    // per configured provider) to name the photo, and fills the result into its name field.
    public async Task SuggestNameAsync(BasketEntry entry, AiProvider provider)
    {
        if (Busy || SuggestingId is not null)
            return;

        var aiSettings = (await _session.LoadAiSettingsAsync()) with { Provider = provider };
        if (!aiSettings.HasKeyFor(provider))
        {
            _showError($"No API key configured for {provider} - add one in Settings.");
            return;
        }

        SuggestingId = entry.Id;
        SuggestingProvider = provider;
        try
        {
            _names[entry.Id] = await SuggestAsync(entry, aiSettings, CancellationToken.None);
        }
        catch (AiNamingException ex)
        {
            _log.Append($"Couldn't suggest a name for \"{entry.SourceName}\": {ex.Message}");
            _showError(ex.Message);
        }
        catch (Exception ex)
        {
            _log.Append($"Couldn't suggest a name for \"{entry.SourceName}\": {ex.Message}");
            _showError($"Couldn't suggest a name: {ex.Message}");
        }
        finally
        {
            SuggestingId = null;
            SuggestingProvider = null;
        }
    }

    // Same, for the whole basket with the default provider, politely paced like the other bulk
    // AI/Meural loops. Only fills in the name fields - nothing is sent to Meural until Upload.
    public async Task SuggestAllAsync()
    {
        if (Busy || _entries.Count == 0)
            return;

        var aiSettings = await _session.LoadAiSettingsAsync();
        if (!aiSettings.HasKeyFor(aiSettings.Provider))
        {
            _showError($"No API key configured for {aiSettings.Provider} - add one in Settings.");
            return;
        }

        var entries = _entries.ToList();
        Busy = true;
        var cts = _cts = new CancellationTokenSource();
        try
        {
            _log.Append($"AI-naming {entries.Count} photo(s) with {aiSettings.Provider}...");
            int done = 0, failed = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                try
                {
                    _names[entry.Id] = await SuggestAsync(entry, aiSettings, cts.Token);
                    done++;
                    Changed?.Invoke();
                }
                catch (AiNamingException ex)
                {
                    failed++;
                    _log.Append($"[{i + 1}/{entries.Count}] Couldn't suggest a name for \"{entry.SourceName}\": {ex.Message}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    _log.Append($"[{i + 1}/{entries.Count}] Couldn't suggest a name for \"{entry.SourceName}\": {ex.Message}");
                }

                if (i < entries.Count - 1)
                    await Task.Delay(TimeSpan.FromMilliseconds(300), cts.Token);
            }

            _log.Append($"AI naming finished: {done} named, {failed} failed.");
            if (failed > 0)
                _showError($"{failed} of {entries.Count} photo(s) couldn't be named.");
            else
                _showSuccess($"Named {done} photo(s) - review them, then upload.");
        }
        catch (OperationCanceledException)
        {
            HandleCancellation(cts, "AI naming");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Busy = false;
        }
    }

    private async Task<string> SuggestAsync(BasketEntry entry, AiSettings aiSettings, CancellationToken ct)
    {
        var (bytes, mediaType) = await _loadAiImage(entry, ct);
        return await ImageNamingService.SuggestNameAsync(aiSettings, bytes, mediaType, SelectedPlaylist?.Name, _log.AsProgress(), ct);
    }

    // ---------- upload ----------

    // True while the basket is doing something (uploading, AI-naming everything, creating a
    // playlist) - the pane disables its controls and shows a Cancel button.
    private bool _busy;

    public bool Busy
    {
        get => _busy;
        private set
        {
            _busy = value;
            Changed?.Invoke();
        }
    }

    public void Cancel() => _cts?.Cancel();

    // Uploads the given entries (normally a copy of Entries, perhaps minus ones the page decided to
    // skip) into the target playlist. The source turns each entry into a file via prepare; the rest
    // - the upload, saving originals for Revert, refreshing the playlist, removing what made it from
    // the basket, the toasts - is the same for every source. onUploaded gets the entries that made
    // it, right after the upload and before the playlist refresh, so the page can remember them.
    // A failed entry stays in the basket so fixing whatever went wrong and clicking Upload again is
    // all it takes.
    public async Task UploadAsync(
        IReadOnlyList<BasketEntry> entries,
        string sourceName,
        bool cropEnabled,
        PrepareUploadAsync prepare,
        Func<IReadOnlyList<BasketEntry>, Task> onUploaded)
    {
        if (Busy || entries.Count == 0)
            return;
        if (TargetGalleryId is not long galleryId || SelectedPlaylist is not { } playlist)
        {
            _showError("Choose a playlist to upload into first.");
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"meuralmanager-web-{sourceName.ToLowerInvariant()}", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var uploadPaths = new List<string>();
        var entryByPath = new Dictionary<string, BasketEntry>();
        // Keyed by the path that actually gets uploaded, to a file holding that photo's pre-crop
        // bytes, so a cropped upload can be reverted later like any other cropped item.
        var originalByUploadPath = new Dictionary<string, string>();
        var prepFailures = 0;

        Busy = true;
        var cts = _cts = new CancellationTokenSource();
        try
        {
            _log.Append($"Fetching {entries.Count} photo(s) from {sourceName}...");
            var context = new PrepareContext
            {
                TempDir = tempDir,
                // The crop dialog opens on the format of the frame(s) this playlist is installed on.
                CropAspect = _session.GetCropSuggestion(galleryId).Aspect,
                Total = entries.Count,
                Cropping = cropEnabled,
            };

            for (var i = 0; i < entries.Count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var entry = entries[i];
                context.Index = i;
                // The Meural item is named after the file, so the basket's name field decides it.
                var baseName = FileNaming.SanitizeFileName(NameFor(entry));
                try
                {
                    var prepared = await prepare(entry, baseName, context, cts.Token);
                    if (prepared is null)
                        continue;

                    uploadPaths.Add(prepared.FilePath);
                    entryByPath[prepared.FilePath] = entry;
                    if (prepared.OriginalPath is not null)
                        originalByUploadPath[prepared.FilePath] = prepared.OriginalPath;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    prepFailures++;
                    _log.Append($"Couldn't get \"{entry.SourceName}\" from {sourceName}: {ex.Message}");
                }
            }

            if (uploadPaths.Count == 0)
            {
                if (prepFailures > 0)
                    _showError($"Couldn't fetch {prepFailures} photo(s) from {sourceName} - see the activity log.");
                return;
            }

            _log.Append($"Uploading {uploadPaths.Count} image(s) to \"{playlist.Name}\"...");
            var summary = await PlaylistService.UploadAndAddToGalleryAsync(
                _session.Client!, galleryId, uploadPaths, _log.AsProgress(), cts.Token, KnownItemIds);
            var failed = summary.Failed + prepFailures;
            _log.Append($"Finished: {summary.Done} uploaded, {failed} failed.");

            foreach (var (path, uploadedItem) in summary.Uploaded)
            {
                if (originalByUploadPath.TryGetValue(path, out var originalPath) && uploadedItem.Id is long newId)
                    _session.SaveUploadOriginal(newId, originalPath);
            }

            // Only the photos that made it leave the basket.
            var uploadedEntries = summary.Uploaded
                .Where(u => entryByPath.ContainsKey(u.FilePath))
                .Select(u => entryByPath[u.FilePath])
                .ToList();
            foreach (var entry in uploadedEntries)
                Remove(entry.Id);

            if (uploadedEntries.Count > 0)
                await onUploaded(uploadedEntries);

            var items = await _session.Client!.GetGalleryItemsAsync(galleryId, ct: cts.Token);
            await _session.SetGalleryItemsAsync(galleryId, items);

            if (failed > 0)
                _showError($"{failed} photo(s) couldn't be uploaded - they're still in the basket so you can try again.");
            else
                _showSuccess($"Uploaded {summary.Done} photo(s) to \"{playlist.Name}\".");
        }
        catch (OperationCanceledException)
        {
            HandleCancellation(cts, "Upload");
        }
        catch (Exception ex)
        {
            _log.Append($"Upload failed: {ex.Message}");
            _showError($"Upload failed: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
            _cts?.Dispose();
            _cts = null;
            Busy = false;
        }
    }

    // Snapshot of every upload id already known before an upload attempt - lets
    // UploadAndAddToGalleryAsync recognize a just-created item after a timeout.
    private HashSet<long> KnownItemIds =>
        (_session.AllItemsCache ?? []).Where(i => i.Id.HasValue).Select(i => i.Id!.Value).ToHashSet();

    // Distinguishes the user clicking Cancel from an HttpClient timeout firing on its own - the
    // latter is a real failure that deserves a toast, not a silent "Cancelled." nobody sees.
    private void HandleCancellation(CancellationTokenSource cts, string operation)
    {
        if (cts.Token.IsCancellationRequested)
        {
            _log.Append("Cancelled.");
            return;
        }

        _log.Append($"{operation} timed out.");
        _showError($"{operation} timed out - the server took too long to respond.");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
