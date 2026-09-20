using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeuralManager.Core.Models;
using MeuralManager.Core.Services;

namespace MeuralManager.Core.Api;

public sealed class ImmichApiException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

// Talks to a user's own Immich server (immich.app) - a self-hosted photo library. Unlike the
// Meural API this one is officially documented (an OpenAPI spec ships with every server), and
// auth is just an `x-api-key` header, so there's no login dance to port.
//
// Only what browsing-and-picking-photos needs: the timeline/search, albums, people, and image
// bytes. Endpoint shapes follow Immich's current (v1.1xx+/v2) REST API.
public sealed class ImmichApiClient : IDisposable
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    // How many photos one timeline/search request asks for. Big enough that a screenful of
    // thumbnails plus some scroll-ahead arrives in one round trip, small enough that the first
    // paint isn't waiting on a huge JSON body.
    public const int PageSize = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public ImmichApiClient(string serverUrl, string apiKey)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(NormalizeServerUrl(serverUrl) + "/api/"),
            Timeout = HttpTimeout,
        };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    // Accepts whatever a person is likely to paste - "immich.lan:2283", "http://host:2283/",
    // "https://photos.example.com/api" - and reduces it to a bare scheme://host[:port][/base]
    // with no trailing slash or "/api" (the constructor appends that itself). A missing scheme
    // is assumed to be http:// since a self-hosted server on a LAN is the common case.
    public static string NormalizeServerUrl(string serverUrl)
    {
        var url = serverUrl.Trim().TrimEnd('/');
        if (url.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            url = url[..^4].TrimEnd('/');
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;
        return url;
    }

    // Confirms the URL is reachable AND the key is accepted, returning the account it belongs to
    // (a bare ping would pass with a wrong key and only fail later, when browsing).
    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "users/me", null, HttpCompletionOption.ResponseContentRead, ct);
        var me = await response.Content.ReadFromJsonSafeAsync<ImmichUser>(ct);
        return me?.Email ?? me?.Name ?? "unknown user";
    }

    // Newest photos first, optionally narrowed by date range / album / person / free text. Photos
    // only - videos can't be shown on a Canvas frame's image playlists.
    public async Task<ImmichAssetPage> SearchAssetsAsync(ImmichAssetFilter filter, int page, CancellationToken ct = default)
    {
        var useSmartSearch = !string.IsNullOrWhiteSpace(filter.Query);
        var body = new SearchRequest
        {
            Type = "IMAGE",
            Order = useSmartSearch ? null : "desc",
            Page = page,
            Size = PageSize,
            TakenAfter = filter.TakenAfter,
            TakenBefore = filter.TakenBefore,
            AlbumIds = filter.AlbumId is null ? null : [filter.AlbumId],
            PersonIds = filter.PersonId is null ? null : [filter.PersonId],
            Query = useSmartSearch ? filter.Query!.Trim() : null,
        };

        using var response = await SendAsync(
            HttpMethod.Post, useSmartSearch ? "search/smart" : "search/metadata", JsonContent(body),
            HttpCompletionOption.ResponseContentRead, ct);

        var result = await response.Content.ReadFromJsonSafeAsync<SearchResponse>(ct);
        var assets = result?.Assets?.Items ?? [];
        // Immich reports the next page as a string (or null/empty when there is none).
        int? nextPage = int.TryParse(result?.Assets?.NextPage, out var next) ? next : null;
        return new ImmichAssetPage(assets, nextPage);
    }

    public async Task<List<ImmichAlbum>> GetAlbumsAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "albums", null, HttpCompletionOption.ResponseContentRead, ct);
        return await response.Content.ReadFromJsonSafeAsync<List<ImmichAlbum>>(ct) ?? [];
    }

    // Immich only recently started paging this endpoint (older servers return everyone in one
    // response and ignore page/size), so this loops while the server says there's another page
    // and otherwise just returns what the first response held.
    public async Task<List<ImmichPerson>> GetPeopleAsync(bool includeHidden = false, CancellationToken ct = default)
    {
        var people = new List<ImmichPerson>();
        for (var page = 1; page <= 50; page++)
        {
            using var response = await SendAsync(
                HttpMethod.Get, $"people?withHidden={(includeHidden ? "true" : "false")}&page={page}&size=500", null,
                HttpCompletionOption.ResponseContentRead, ct);
            var result = await response.Content.ReadFromJsonSafeAsync<PeopleResponse>(ct);
            people.AddRange(result?.People ?? []);
            if (result?.HasNextPage != true)
                break;
        }

        return people;
    }

    // Streams a photo's rendition ("thumbnail" ~250px, "preview" ~1440px, "fullsize") straight to
    // the caller - the web app's thumbnail proxy uses this so the API key never reaches the
    // browser. Caller owns disposing the returned response.
    public Task<HttpResponseMessage> GetAssetThumbnailAsync(string assetId, string size, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"assets/{Uri.EscapeDataString(assetId)}/thumbnail?size={Uri.EscapeDataString(size)}",
            null, HttpCompletionOption.ResponseHeadersRead, ct);

    public Task<HttpResponseMessage> GetPersonThumbnailAsync(string personId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"people/{Uri.EscapeDataString(personId)}/thumbnail",
            null, HttpCompletionOption.ResponseHeadersRead, ct);

    // The best version of a photo that Meural can actually ingest: the original when it's already
    // a JPEG/PNG (no needless re-encode, full quality), otherwise Immich's converted "fullsize"
    // JPEG (it only exists for formats a browser can't show, e.g. HEIC/RAW, and on servers set to
    // generate it), falling back to the ~1440px "preview" - always present, always JPEG.
    public async Task<HttpResponseMessage> GetBestImageAsync(ImmichAsset asset, CancellationToken ct = default)
    {
        if (IsMeuralFriendly(asset.OriginalMimeType))
            return await SendAsync(HttpMethod.Get, $"assets/{Uri.EscapeDataString(asset.Id)}/original",
                null, HttpCompletionOption.ResponseHeadersRead, ct);

        try
        {
            return await GetAssetThumbnailAsync(asset.Id, "fullsize", ct);
        }
        catch (ImmichApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return await GetAssetThumbnailAsync(asset.Id, "preview", ct);
        }
    }

    // Downloads GetBestImageAsync's result into destDir as "<original name>.<ext>" and returns
    // its path - the shape PlaylistService.UploadAndAddToGalleryAsync expects (it names the
    // Meural item after the file). Each asset gets its own subfolder so two photos that share
    // a filename (IMG_0001.jpg from two cameras) can't overwrite each other.
    public async Task<ImmichDownload> DownloadForUploadAsync(ImmichAsset asset, string destDir, CancellationToken ct = default)
    {
        using var response = await GetBestImageAsync(asset, ct);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

        var assetDir = Path.Combine(destDir, FileNaming.SanitizeFileName(asset.Id));
        Directory.CreateDirectory(assetDir);
        var baseName = FileNaming.SanitizeFileName(Path.GetFileNameWithoutExtension(asset.OriginalFileName ?? asset.Id));
        var path = Path.Combine(assetDir, baseName + ExtensionFor(contentType));

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(path);
        await source.CopyToAsync(file, ct);
        return new ImmichDownload(path, contentType);
    }

    private static bool IsMeuralFriendly(string? mimeType) =>
        mimeType is "image/jpeg" or "image/jpg" or "image/png";

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".jpg",
    };

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string relativeUrl, HttpContent? content, HttpCompletionOption completion, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, relativeUrl) { Content = content };
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ImmichApiException($"Couldn't reach the Immich server: {ex.Message}");
        }

        if (response.IsSuccessStatusCode)
            return response;

        var status = response.StatusCode;
        response.Dispose();
        throw new ImmichApiException(status switch
        {
            HttpStatusCode.Unauthorized => "Immich rejected the API key.",
            HttpStatusCode.Forbidden => "The API key is missing a permission - it needs asset.read, asset.view, asset.download, album.read, person.read and user.read.",
            HttpStatusCode.NotFound => "Immich answered 404 - check the server URL, and that the server is a recent version.",
            _ => $"Immich answered {(int)status} {status}.",
        }, status);
    }

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");

    public void Dispose() => _http.Dispose();

    private sealed record ImmichUser
    {
        [JsonPropertyName("email")] public string? Email { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
    }

    private sealed record SearchRequest
    {
        [JsonPropertyName("type")] public string? Type { get; init; }
        [JsonPropertyName("order")] public string? Order { get; init; }
        [JsonPropertyName("page")] public int Page { get; init; }
        [JsonPropertyName("size")] public int Size { get; init; }
        [JsonPropertyName("takenAfter")] public DateTimeOffset? TakenAfter { get; init; }
        [JsonPropertyName("takenBefore")] public DateTimeOffset? TakenBefore { get; init; }
        [JsonPropertyName("albumIds")] public string[]? AlbumIds { get; init; }
        [JsonPropertyName("personIds")] public string[]? PersonIds { get; init; }
        [JsonPropertyName("query")] public string? Query { get; init; }
    }

    private sealed record SearchResponse
    {
        [JsonPropertyName("assets")] public SearchAssets? Assets { get; init; }
    }

    private sealed record SearchAssets
    {
        [JsonPropertyName("items")] public List<ImmichAsset>? Items { get; init; }
        [JsonPropertyName("nextPage")] public string? NextPage { get; init; }
    }

    private sealed record PeopleResponse
    {
        [JsonPropertyName("people")] public List<ImmichPerson>? People { get; init; }
        [JsonPropertyName("hasNextPage")] public bool? HasNextPage { get; init; }
    }
}

internal static class ImmichHttpContentExtensions
{
    // System.Text.Json's own ReadFromJsonAsync lives in System.Net.Http.Json and needs case-
    // insensitive options for Immich's camelCase - this wraps that once so call sites stay short.
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static async Task<T?> ReadFromJsonSafeAsync<T>(this HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct);
    }
}
