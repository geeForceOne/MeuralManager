using System.Text.Json.Serialization;

namespace MeuralManager.Core.Models;

// Wraps the response of GET items/{id}, which returns a single item object
// under "data" (unlike the paginated list endpoints, where "data" is an array).
public record MeuralItemEnvelope
{
    [JsonPropertyName("data")]
    public MeuralItem? Data { get; init; }
}
