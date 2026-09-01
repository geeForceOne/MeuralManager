using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace MeuralManager.Web.Services;

// Browser-side equivalent of the WinForms app's AppSettings: persists the Meural refresh
// session (not the password) so a returning visit can silently re-authenticate. Backed by
// ProtectedLocalStorage, which encrypts the payload with ASP.NET Core Data Protection before
// it ever reaches the browser's localStorage - there's no server-side session store, so this
// works the same whether one person or several share the container.
public sealed class WebSessionStore(ProtectedLocalStorage storage)
{
    private const string Key = "meuralmanager.session";

    public sealed record StoredSession(string Email, string RefreshToken, string TrustId, double ExpiresAt);

    public async Task<StoredSession?> LoadAsync()
    {
        try
        {
            var result = await storage.GetAsync<StoredSession>(Key);
            return result.Success ? result.Value : null;
        }
        catch
        {
            // JS interop isn't available yet (e.g. still prerendering) or the stored payload
            // can't be decrypted (e.g. Data Protection keys rotated) - treat both as "no session".
            return null;
        }
    }

    public async Task SaveAsync(StoredSession session)
    {
        try
        {
            await storage.SetAsync(Key, session);
        }
        catch
        {
            // Best-effort only - failing to persist the session shouldn't block sign-in.
        }
    }

    public async Task ClearAsync()
    {
        try
        {
            await storage.DeleteAsync(Key);
        }
        catch
        {
            // Best-effort only.
        }
    }
}
