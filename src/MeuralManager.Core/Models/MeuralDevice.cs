using System.Text.Json.Serialization;

namespace MeuralManager.Core.Models;

public record MeuralDevice
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("alias")]
    public string? Alias { get; init; }
}
