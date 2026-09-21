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
}

// What to pre-select when cropping for a playlist, and why - FrameNames is every frame the
// playlist is installed on (empty if none), so the UI can show where the suggestion came from.
public sealed record CropSuggestion(FrameOrientation Orientation, IReadOnlyList<string> FrameNames)
{
    public CropDialog.CropAspect Aspect => Orientation.ToCropAspect();
}
