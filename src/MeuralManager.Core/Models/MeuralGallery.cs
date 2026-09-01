using System.Text.Json.Serialization;

namespace MeuralManager.Core.Models;

public record MeuralGallery
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("itemIds")]
    public List<long>? ItemIds { get; init; }
}
