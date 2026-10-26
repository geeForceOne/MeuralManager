using MeuralManager.Core.Models;
using MeuralManager.Web.Services;

namespace MeuralManager.Tests;

public class FrameOrientationTests
{
    [Fact]
    public void Parse_ReadsEntriesAndSkipsBrokenOnes()
    {
        var parsed = FrameOrientationExtensions.Parse("1:h,2:v,junk,x:h,3:?");

        Assert.Equal(FrameOrientation.Horizontal, parsed[1]);
        Assert.Equal(FrameOrientation.Vertical, parsed[2]);
        Assert.Equal(FrameOrientation.Vertical, parsed[3]);
        Assert.Equal(3, parsed.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_NothingStored(string? raw) => Assert.Empty(FrameOrientationExtensions.Parse(raw));

    [Fact]
    public void Format_RoundTrips()
    {
        var original = new Dictionary<long, FrameOrientation> { [1] = FrameOrientation.Horizontal, [2] = FrameOrientation.Vertical };

        Assert.Equal(original, FrameOrientationExtensions.Parse(FrameOrientationExtensions.Format(original)));
    }

    private static readonly MeuralDevice Living = new() { Id = 1, Alias = "Living room" };
    private static readonly MeuralDevice Hall = new() { Id = 2, Alias = "Hall" };
    private static readonly MeuralGallery Playlist = new() { Id = 100 };

    private static CropSuggestion Suggest(Dictionary<long, FrameOrientation> orientations, params MeuralDevice[] framesWithPlaylist) =>
        CropSuggestion.For(
            Playlist.Id,
            [Living, Hall],
            framesWithPlaylist.ToDictionary(d => d.Id!.Value, _ => new List<MeuralGallery> { Playlist }),
            orientations);

    [Fact]
    public void Crop_AllFramesHorizontal_SuggestsHorizontal()
    {
        var suggestion = Suggest(new() { [1] = FrameOrientation.Horizontal, [2] = FrameOrientation.Horizontal }, Living, Hall);

        Assert.Equal(FrameOrientation.Horizontal, suggestion.Orientation);
        Assert.Equal(["Living room", "Hall"], suggestion.FrameNames);
    }

    [Fact]
    public void Crop_MixedFrames_SuggestsVertical() =>
        Assert.Equal(FrameOrientation.Vertical, Suggest(new() { [1] = FrameOrientation.Horizontal }, Living, Hall).Orientation);

    [Fact]
    public void Crop_OnlyFramesWithThePlaylistCount() =>
        Assert.Equal(FrameOrientation.Horizontal, Suggest(new() { [1] = FrameOrientation.Horizontal }, Living).Orientation);

    [Fact]
    public void Crop_PlaylistOnNoFrame_SuggestsVertical()
    {
        var suggestion = Suggest(new() { [1] = FrameOrientation.Horizontal });

        Assert.Equal(FrameOrientation.Vertical, suggestion.Orientation);
        Assert.Empty(suggestion.FrameNames);
    }

    [Fact]
    public void Crop_NotScannedYet_SuggestsVertical() =>
        Assert.Equal(FrameOrientation.Vertical, CropSuggestion.For(100, null, null, new Dictionary<long, FrameOrientation>()).Orientation);
}
