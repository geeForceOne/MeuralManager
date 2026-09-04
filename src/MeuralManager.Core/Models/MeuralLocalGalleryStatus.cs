using System.Text.Json.Serialization;

namespace MeuralManager.Core.Models;

// Maps the Canvas frame's own local get_gallery_status_json/ response (see
// MeuralLocalDeviceClient) - what's actually showing right now, as the frame itself reports it,
// not what the cloud API last knew.
public record MeuralLocalGalleryStatus
{
    [JsonPropertyName("current_gallery")]
    public long? CurrentGalleryId { get; init; }

    [JsonPropertyName("current_gallery_name")]
    public string? CurrentGalleryName { get; init; }

    [JsonPropertyName("current_item")]
    public long? CurrentItemId { get; init; }
}
