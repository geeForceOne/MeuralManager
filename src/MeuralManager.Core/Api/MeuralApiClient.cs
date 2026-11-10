using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MeuralManager.Core.Models;
using MeuralManager.Core.Services;

namespace MeuralManager.Core.Api;

// Talks to the Meural cloud API. Auth goes through Netgear Accounts' Cognito-backed login
// (CUSTOM_AUTH, falling back to USER_PASSWORD_AUTH for unmigrated accounts) and then exchanges
// the resulting Cognito session for a separate Meural OAuth access/refresh token pair - the same
// flow Meural's own official Home Assistant integration uses (github.com/GuySie/ha-meural). See
// NetgearAuthenticator for the details; this class just holds the resulting session and adds
// `Authorization: Token <token>` + the required x-meural-* headers to API requests.
public sealed partial class MeuralApiClient : IDisposable
{
    private const string ApiBaseUrl = "https://api.meural.com/v1/";

    // Matches the timeout BackupService/ImageDownloader already use for Meural's CDN. Without an
    // explicit value here, HttpClient defaults to 100 seconds - long enough that a hung request
    // (e.g. a slow/blocked call to a Canvas frame endpoint) looks indistinguishable from the UI
    // being stuck, rather than failing fast and visibly.
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http = new() { Timeout = HttpTimeout };

    // Uploads only, and only with SmartTimeouts on: sending the file and Meural processing it can
    // take well over 30 seconds, and a cut-off upload usually still lands. Same headers as _http.
    private static readonly TimeSpan UploadHttpTimeout = TimeSpan.FromMinutes(2);
    private readonly HttpClient _uploadHttp = new() { Timeout = UploadHttpTimeout };
    private readonly NetgearAuthenticator _authenticator;

    private string? _accessToken;
    private string? _refreshToken;
    private double _expiresAt;
    private PendingChallenge? _pendingChallenge;

    public MeuralApiClient(string? trustId = null)
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(NetgearAuthenticator.BrowserUserAgent);
        _uploadHttp.DefaultRequestHeaders.UserAgent.ParseAdd(NetgearAuthenticator.BrowserUserAgent);
        _authenticator = new NetgearAuthenticator(_http, trustId);
    }

    public bool IsAuthenticated => _accessToken is not null;
    public string TrustId => _authenticator.TrustId;
    public string? RefreshToken => _refreshToken;
    public double ExpiresAt => _expiresAt;

    public async Task AuthenticateAsync(string email, string password, CancellationToken ct = default)
    {
        try
        {
            var result = await _authenticator.AuthenticateAsync(email, password, ct);
            ApplyAuthResult(result);
        }
        catch (MeuralChallengeRequiredException ex)
        {
            _pendingChallenge = ex.Challenge;
            throw;
        }
    }

    // Answers a pending challenge raised by AuthenticateAsync (e.g. an emailed verification
    // code). Throws MeuralChallengeRequiredException again if Cognito demands a further step.
    public async Task CompleteChallengeAsync(string answer, CancellationToken ct = default)
    {
        if (_pendingChallenge is null)
            throw new MeuralAuthException("There is no pending sign-in challenge to answer.");

        try
        {
            var result = await _authenticator.CompleteChallengeAsync(_pendingChallenge, answer, ct);
            _pendingChallenge = null;
            ApplyAuthResult(result);
        }
        catch (MeuralChallengeRequiredException ex)
        {
            _pendingChallenge = ex.Challenge;
            throw;
        }
    }

    public string? PendingChallengeDestination => _pendingChallenge?.Destination;

    // Restores a previous session from a saved refresh token instead of a full password login.
    // Returns false (rather than throwing) on any auth failure, since callers use this as a
    // "try silently, fall back to the login form" step.
    public async Task<bool> TryRestoreSessionAsync(string refreshToken, CancellationToken ct = default)
    {
        try
        {
            var result = await _authenticator.RefreshAsync(refreshToken, ct);
            ApplyAuthResult(result);
            return true;
        }
        catch (MeuralAuthException)
        {
            return false;
        }
    }

    private void ApplyAuthResult(AuthResult result)
    {
        _accessToken = result.AccessToken;
        _refreshToken = result.RefreshToken;
        _expiresAt = result.ExpiresAt;

        foreach (var http in new[] { _http, _uploadHttp })
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", _accessToken);
            http.DefaultRequestHeaders.Remove("x-meural-api-version");
            http.DefaultRequestHeaders.Add("x-meural-api-version", "4");
            http.DefaultRequestHeaders.Remove("x-meural-source-platform");
            http.DefaultRequestHeaders.Add("x-meural-source-platform", "web");
        }
    }

    // Refreshes the access token if it's expired or about to (matching pymeural's proactive
    // refresh), so callers don't need their own 401-retry logic around every request.
    private async Task EnsureFreshTokenAsync(CancellationToken ct)
    {
        if (_refreshToken is null)
            return;
        if (_expiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60)
            return;

        var result = await _authenticator.RefreshAsync(_refreshToken, ct);
        ApplyAuthResult(result);
    }

    public async Task<List<MeuralItem>> GetAllItemsAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        return await GetAllPagesAsync<MeuralItem>("user/items", pageSize: 200, progress, ct, "upload");
    }

    public async Task<List<MeuralGallery>> GetAllGalleriesAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        return await GetAllPagesAsync<MeuralGallery>("user/galleries", pageSize: 100, progress, ct, "playlist");
    }

    public async Task<List<MeuralDevice>> GetAllDevicesAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        return await GetAllPagesAsync<MeuralDevice>("user/devices", pageSize: 100, progress, ct, "frame");
    }

    public async Task<List<MeuralGallery>> GetDeviceGalleriesAsync(long deviceId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        return await GetAllPagesAsync<MeuralGallery>($"devices/{deviceId}/galleries", pageSize: 100, progress, ct, "playlist");
    }

    public async Task<List<MeuralItem>> GetGalleryItemsAsync(long galleryId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        return await GetAllPagesAsync<MeuralItem>($"galleries/{galleryId}/items", pageSize: 100, progress, ct, "item");
    }

    public async Task<MeuralItem?> GetItemAsync(long itemId, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        var url = $"{ApiBaseUrl}items/{itemId}";
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);

        var envelope = JsonSerializer.Deserialize<MeuralItemEnvelope>(body)
            ?? throw new MeuralApiException("Failed to deserialize item response.");

        return envelope.Data;
    }

    private Task<TEnvelope> PostJsonAsync<TEnvelope>(string endpoint, object body, CancellationToken ct) =>
        SendJsonAsync<TEnvelope>(HttpMethod.Post, endpoint, body, ct);

    private Task<TEnvelope> PutJsonAsync<TEnvelope>(string endpoint, object body, CancellationToken ct) =>
        SendJsonAsync<TEnvelope>(HttpMethod.Put, endpoint, body, ct);

    private async Task<TEnvelope> SendJsonAsync<TEnvelope>(HttpMethod method, string endpoint, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(method, $"{ApiBaseUrl}{endpoint}") { Content = content };

        using var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new MeuralApiException($"{method} {endpoint} returned {(int)response.StatusCode}: {responseBody}", response.StatusCode);

        return JsonSerializer.Deserialize<TEnvelope>(responseBody)
            ?? throw new MeuralApiException($"Failed to deserialize response from {method} {endpoint}.");
    }

    private async Task<List<T>> GetAllPagesAsync<T>(string endpoint, int pageSize, IProgress<string>? progress, CancellationToken ct, string itemLabel = "item")
    {
        var results = new List<T>();
        int page = 1;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{ApiBaseUrl}{endpoint}?count={pageSize}&page={page}";
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);

            var pageResult = JsonSerializer.Deserialize<MeuralPage<T>>(body)
                ?? throw new MeuralApiException($"Failed to deserialize page from {endpoint}.");

            results.AddRange(pageResult.Data);
            progress?.Report($"...found {results.Count} {itemLabel}(s) so far.");

            if (pageResult.IsLast == true || pageResult.Data.Count < pageSize)
                break;

            page++;
        }

        return results;
    }

    public void Dispose()
    {
        _http.Dispose();
        _uploadHttp.Dispose();
    }
}
