using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace MeuralManager.Web.Services;

// UI preferences, split by what they're about:
//  - The feature toggles (Picture of the Moment, crop, remote control, Immich) live per-account in
//    the SQLite settings table via MeuralSessionState, so they follow the account across browsers
//    and survive clearing site data - the same place the Immich URL/key and the remote toolbar's
//    device list already are.
//  - The Playlists and Immich pages' splitter widths depend on one browser's screen, so they stay in
//    ProtectedLocalStorage. Not cleared on sign-out.
// Callers see one StoredPreferences record either way.
public sealed class UserPreferencesStore(ProtectedLocalStorage storage, MeuralSessionState session)
{
    private const string Key = "meuralmanager.preferences";

    // PlaylistListPaneWidth/PreviewPaneWidth are the Playlists page's two draggable splitter
    // panes' last widths in pixels (see splitter.js) - null means "never dragged, use the CSS
    // default".
    public sealed record StoredPreferences(
        bool ShowPictureOfTheMoment = true,
        double? PlaylistListPaneWidth = null,
        double? PreviewPaneWidth = null,
        bool CropFeatureEnabled = true,
        bool RemoteControlEnabled = true,
        // Off by default (unlike the other feature toggles): most people running this don't have
        // an Immich server, so the nav entry and settings stay out of their way until they opt in.
        bool ImmichEnabled = false,
        // The Immich page's basket and preview panes, same idea as the Playlists widths above.
        double? ImmichBasketPaneWidth = null,
        double? ImmichPreviewPaneWidth = null);

    // What's in the browser's localStorage. Only the widths are written now; the toggle fields are
    // legacy - they used to live here before moving to the account DB - and are only read, as a
    // one-time migration source (nullable so "never stored" differs from "stored the default").
    // Save rewrites the entry without them, so they fade out after the next preference change.
    public sealed record BrowserPreferences(
        double? PlaylistListPaneWidth = null,
        double? PreviewPaneWidth = null,
        bool? ShowPictureOfTheMoment = null,
        bool? CropFeatureEnabled = null,
        bool? RemoteControlEnabled = null,
        bool? ImmichEnabled = null,
        double? ImmichBasketPaneWidth = null,
        double? ImmichPreviewPaneWidth = null);

    // Fired after a successful save so MainLayout (which stays mounted for the whole circuit,
    // unlike a page) can pick up a toggle - e.g. the remote control toolbar - flipped on the
    // Settings page without needing a full reload. Mirrors MeuralSessionState.Changed.
    public event Action? Changed;

    // Only meaningful once signed in: the toggles come from the account's DB, which isn't open
    // before then, so a signed-out call sees just the browser's legacy values (or the defaults).
    public async Task<StoredPreferences> LoadAsync()
    {
        var browser = await LoadBrowserAsync() ?? new BrowserPreferences();
        var toggles = new MeuralSessionState.FeatureToggles(
            browser.ShowPictureOfTheMoment, browser.CropFeatureEnabled, browser.RemoteControlEnabled, browser.ImmichEnabled);

        if (session.IsAuthenticated)
        {
            var saved = await session.LoadFeatureTogglesAsync();

            // Anything the account has never saved is filled from what this browser used to hold,
            // and written through so it's the account's from now on. An already-saved value always
            // wins, so a second browser with stale legacy values can't overwrite it.
            var merged = new MeuralSessionState.FeatureToggles(
                saved.ShowPictureOfTheMoment ?? toggles.ShowPictureOfTheMoment,
                saved.CropFeatureEnabled ?? toggles.CropFeatureEnabled,
                saved.RemoteControlEnabled ?? toggles.RemoteControlEnabled,
                saved.ImmichEnabled ?? toggles.ImmichEnabled);
            if (merged != saved)
                await session.SaveFeatureTogglesAsync(merged);

            toggles = merged;
        }

        var defaults = new StoredPreferences();
        return new StoredPreferences(
            ShowPictureOfTheMoment: toggles.ShowPictureOfTheMoment ?? defaults.ShowPictureOfTheMoment,
            PlaylistListPaneWidth: browser.PlaylistListPaneWidth,
            PreviewPaneWidth: browser.PreviewPaneWidth,
            CropFeatureEnabled: toggles.CropFeatureEnabled ?? defaults.CropFeatureEnabled,
            RemoteControlEnabled: toggles.RemoteControlEnabled ?? defaults.RemoteControlEnabled,
            ImmichEnabled: toggles.ImmichEnabled ?? defaults.ImmichEnabled,
            ImmichBasketPaneWidth: browser.ImmichBasketPaneWidth,
            ImmichPreviewPaneWidth: browser.ImmichPreviewPaneWidth);
    }

    public async Task SaveAsync(StoredPreferences preferences)
    {
        if (session.IsAuthenticated)
        {
            await session.SaveFeatureTogglesAsync(new MeuralSessionState.FeatureToggles(
                preferences.ShowPictureOfTheMoment,
                preferences.CropFeatureEnabled,
                preferences.RemoteControlEnabled,
                preferences.ImmichEnabled));
        }

        try
        {
            await storage.SetAsync(Key, new BrowserPreferences(
                preferences.PlaylistListPaneWidth, preferences.PreviewPaneWidth,
                ImmichBasketPaneWidth: preferences.ImmichBasketPaneWidth,
                ImmichPreviewPaneWidth: preferences.ImmichPreviewPaneWidth));
        }
        catch
        {
            // Best-effort only - failing to persist a pane width shouldn't break the toggle.
        }

        Changed?.Invoke();
    }

    private async Task<BrowserPreferences?> LoadBrowserAsync()
    {
        try
        {
            var result = await storage.GetAsync<BrowserPreferences>(Key);
            return result is { Success: true, Value: not null } ? result.Value : null;
        }
        catch
        {
            // JS interop unavailable or the stored payload can't be decrypted - treat as nothing stored.
            return null;
        }
    }
}
