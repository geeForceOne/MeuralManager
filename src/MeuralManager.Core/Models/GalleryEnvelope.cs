using System.Text.Json.Serialization;

namespace MeuralManager.Core.Models;

// Wraps the single-object response of POST/PUT galleries (unlike the paginated
// list endpoints, where "data" is an array) - mirrors MeuralItemEnvelope.
public record GalleryEnvelope
{
    [JsonPropertyName("data")]
    public MeuralGallery? Data { get; init; }
}
