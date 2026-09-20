using System.Text.Json.Serialization;

namespace MeuralManager.Core.Models;

// Where to reach an Immich server and how to authenticate against it. Stored per Meural account
// (see MeuralSessionState.SaveImmichSettingsAsync), with the API key encrypted at rest.
public sealed record ImmichSettings
{
    public string? ServerUrl { get; init; }
    public string? ApiKey { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

public record ImmichAsset
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("originalFileName")]
    public string? OriginalFileName { get; init; }

    // Decides whether the original can go to Meural as-is (JPEG/PNG) or whether Immich's own
    // converted rendition is needed instead (HEIC, RAW, ...) - see ImmichApiClient.DownloadForUploadAsync.
    [JsonPropertyName("originalMimeType")]
    public string? OriginalMimeType { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    // The photo's wall-clock time in the timezone it was taken in - what Immich's own timeline
    // groups by, so a photo shot at 11pm doesn't jump to the next day just because the server
    // runs in UTC.
    [JsonPropertyName("localDateTime")]
    public DateTimeOffset? LocalDateTime { get; init; }

    [JsonPropertyName("fileCreatedAt")]
    public DateTimeOffset? FileCreatedAt { get; init; }

    public DateTime TakenDate => (LocalDateTime ?? FileCreatedAt)?.Date ?? DateTime.MinValue;
}

public record ImmichAlbum
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("albumName")]
    public string? AlbumName { get; init; }

    [JsonPropertyName("assetCount")]
    public int AssetCount { get; init; }

    [JsonPropertyName("albumThumbnailAssetId")]
    public string? AlbumThumbnailAssetId { get; init; }
}

public record ImmichPerson
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("isHidden")]
    public bool IsHidden { get; init; }

    public bool HasName => !string.IsNullOrWhiteSpace(Name);
}

// One page of a photo query. NextPage is null once there's nothing left to load.
public sealed record ImmichAssetPage(IReadOnlyList<ImmichAsset> Assets, int? NextPage);

// Everything the Photos/Albums/People tabs can narrow a photo query by - all optional, all
// combinable, since Immich's metadata search takes them together.
public sealed record ImmichAssetFilter
{
    public DateTimeOffset? TakenAfter { get; init; }
    public DateTimeOffset? TakenBefore { get; init; }
    public string? AlbumId { get; init; }
    public string? PersonId { get; init; }
    // Free-text ("beach at sunset") - routed to Immich's CLIP-based smart search instead of the
    // metadata search, which needs the server's machine-learning service to be enabled.
    public string? Query { get; init; }
}

public sealed record ImmichDownload(string FilePath, string ContentType);
