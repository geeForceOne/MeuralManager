using System.Collections.Concurrent;
using MeuralManager.Core.Api;
using MeuralManager.Core.Models;

namespace MeuralManager.Web.Services;

// Holds the live ImmichApiClient per account, keyed by an unguessable token, so the thumbnail
// proxy endpoints (plain HTTP requests, which can't see a Blazor circuit's state) can reach
// Immich on the browser's behalf - which keeps the Immich URL and API key server-side instead of
// putting the key in every <img src>. The token is stable per account (see
// MeuralSessionState.GetImmichClientAsync) so the browser's HTTP cache keeps working across page
// loads; it's a capability URL, the same trust model the image-cache endpoint already uses.
public sealed class ImmichProxyRegistry : IDisposable
{
    private sealed record Entry(ImmichSettings Settings, ImmichApiClient Client);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    // Returns the client for token, reusing the existing one when the settings haven't changed
    // (so tabs of the same account share one HttpClient) and swapping it out when they have.
    public ImmichApiClient Register(string token, ImmichSettings settings)
    {
        lock (_entries)
        {
            if (_entries.TryGetValue(token, out var existing) && existing.Settings == settings)
                return existing.Client;

            var client = new ImmichApiClient(settings.ServerUrl!, settings.ApiKey!);
            _entries[token] = new Entry(settings, client);
            existing?.Client.Dispose();
            return client;
        }
    }

    public ImmichApiClient? Get(string token) => _entries.TryGetValue(token, out var entry) ? entry.Client : null;

    public void Unregister(string token)
    {
        lock (_entries)
        {
            if (_entries.TryRemove(token, out var entry))
                entry.Client.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
            entry.Client.Dispose();
        _entries.Clear();
    }
}
