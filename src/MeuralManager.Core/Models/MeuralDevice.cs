using System.Text.Json.Serialization;

namespace MeuralManager.Core.Models;

public record MeuralDevice
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    // The frame's own IP address on the local network - used to talk to its local /remote/...
    // HTTP API (see MeuralLocalDeviceClient), a completely separate surface from api.meural.com.
    [JsonPropertyName("localIp")]
    public string? LocalIp { get; init; }
}
