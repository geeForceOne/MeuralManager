using System.Text.Json.Serialization;

namespace MeuralManager.Core.Models;

// The playback settings of GET devices/{id} that a pinned message changes and later puts back
// (see MeuralApiClient.GetDeviceSettingsAsync). Field meanings are from Meural's Postman API
// documentation's PUT devices/:id: imageDuration is seconds per image, where 0 "prevents images
// from rotating"; schedulerEnabled is the frame's playlist schedule; galleryRotation moves on to
// the next playlist when one ends.
public record MeuralDeviceSettings
{
    [JsonPropertyName("imageDuration")]
    public int? ImageDuration { get; init; }

    [JsonPropertyName("schedulerEnabled")]
    public bool? SchedulerEnabled { get; init; }

    [JsonPropertyName("galleryRotation")]
    public bool? GalleryRotation { get; init; }
}

// Wraps GET devices/{id}'s single-object response - mirrors GalleryEnvelope.
public record DeviceSettingsEnvelope
{
    [JsonPropertyName("data")]
    public MeuralDeviceSettings? Data { get; init; }
}
