using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace MeuralManager.Web.Services;

// Per-browser UI preferences - not tied to any particular Meural account (unlike WebSessionStore's
// login session) and not cleared on sign-out, since these are just personal display choices for
// whoever uses this browser, not account data.
public sealed class UserPreferencesStore(ProtectedLocalStorage storage)
{
    private const string Key = "meuralmanager.preferences";

    // PlaylistListPaneWidth/PreviewPaneWidth are the Playlists page's two draggable splitter
    // panes' last widths in pixels (see splitter.js) - null means "never dragged, use the CSS
    // default".
    public sealed record StoredPreferences(
        bool ShowPictureOfTheMoment = true,
        double? PlaylistListPaneWidth = null,
        double? PreviewPaneWidth = null);

    public async Task<StoredPreferences> LoadAsync()
    {
        try
        {
            var result = await storage.GetAsync<StoredPreferences>(Key);
            return result is { Success: true, Value: not null } ? result.Value : new StoredPreferences();
        }
        catch
        {
            // JS interop unavailable or the stored payload can't be decrypted - fall back to defaults.
            return new StoredPreferences();
        }
    }

    public async Task SaveAsync(StoredPreferences preferences)
    {
        try
        {
            await storage.SetAsync(Key, preferences);
        }
        catch
        {
            // Best-effort only - failing to persist a preference shouldn't break the toggle.
        }
    }
}
