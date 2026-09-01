using System.Text.Json.Serialization;

namespace MeuralManager.Core.Models;

public record MeuralPage<T>
{
    [JsonPropertyName("isLast")]
    public bool? IsLast { get; init; }

    [JsonPropertyName("count")]
    public int? Count { get; init; }

    [JsonPropertyName("data")]
    public List<T> Data { get; init; } = new();
}
