using MeuralManager.Core.Models;
using MeuralManager.Web.Components.Shared;

namespace MeuralManager.Web.Services;

// How a frame hangs on the wall - the frame itself only supports one or the other. Set per frame in
// Settings and used to pre-select the crop aspect wherever an image is about to be cropped for a
// playlist. Vertical is the default for a frame nobody has set yet.
public enum FrameOrientation { Vertical, Horizontal }

public static class FrameOrientationExtensions
{
    public static CropDialog.CropAspect ToCropAspect(this FrameOrientation orientation) =>
        orientation == FrameOrientation.Horizontal ? CropDialog.CropAspect.Wide : CropDialog.CropAspect.Tall;

    // The settings-table form: one "id:h,id:v" row holding only frames someone has explicitly set.
    // Unreadable entries are skipped; anything but "h" counts as Vertical.
    public static Dictionary<long, FrameOrientation> Parse(string? raw)
    {
        var result = new Dictionary<long, FrameOrientation>();
        if (string.IsNullOrEmpty(raw))
            return result;

        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length == 2 && long.TryParse(parts[0], out var id))
                result[id] = parts[1] == "h" ? FrameOrientation.Horizontal : FrameOrientation.Vertical;
        }
        return result;
    }

    public static string Format(IReadOnlyDictionary<long, FrameOrientation> orientations) =>
        string.Join(",", orientations.Select(kv => $"{kv.Key}:{(kv.Value == FrameOrientation.Horizontal ? "h" : "v")}"));
}

// What to pre-select when cropping for a playlist, and why - FrameNames is every frame the
// playlist is installed on (empty if none), so the UI can show where the suggestion came from.
public sealed record CropSuggestion(FrameOrientation Orientation, IReadOnlyList<string> FrameNames)
{
    public CropDialog.CropAspect Aspect => Orientation.ToCropAspect();

    // Looks at every frame the playlist is installed on - horizontal only if they're ALL set to
    // horizontal, so a mix of orientations (or a frame nobody's set yet, or no frame at all) comes
    // out vertical, which is the default.
    public static CropSuggestion For(
        long? galleryId,
        IEnumerable<MeuralDevice>? devices,
        IReadOnlyDictionary<long, List<MeuralGallery>>? deviceGalleries,
        IReadOnlyDictionary<long, FrameOrientation> orientations)
    {
        var frames = new List<MeuralDevice>();
        if (galleryId is long id && devices is not null && deviceGalleries is not null)
        {
            frames = devices
                .Where(d => d.Id is long deviceId
                    && deviceGalleries.TryGetValue(deviceId, out var galleries)
                    && galleries.Any(g => g.Id == id))
                .ToList();
        }

        var names = frames.Select(f => f.Alias ?? $"Frame {f.Id}").ToList();
        var allHorizontal = frames.Count > 0 && frames.All(f => f.Id is long deviceId
            && orientations.TryGetValue(deviceId, out var o) && o == FrameOrientation.Horizontal);
        return new CropSuggestion(allHorizontal ? FrameOrientation.Horizontal : FrameOrientation.Vertical, names);
    }
}
