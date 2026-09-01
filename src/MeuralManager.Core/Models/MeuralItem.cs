using System.Text.Json.Serialization;

namespace MeuralManager.Core.Models;

public record MeuralItem
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; init; }
}
