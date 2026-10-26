using MeuralManager.Core.Services;

namespace MeuralManager.Tests;

public class FileNamingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("IMG_1234")]
    [InlineData("DSC01234")]
    [InlineData("PXL_20260101")]
    [InlineData("I76A6947_hdr")]
    [InlineData("3f2b8c1e-1a2b-4c3d-8e9f-0a1b2c3d4e5f")]
    [InlineData("untitled")]
    public void LooksGeneric_CameraAndMachineNames(string? name) => Assert.True(FileNaming.LooksGeneric(name));

    [Theory]
    [InlineData("Sunset over Lake Como")]
    [InlineData("The Scream")]
    [InlineData("Paris 2024")]
    public void LooksGeneric_DescriptiveNames(string name) => Assert.False(FileNaming.LooksGeneric(name));

    [Fact]
    public void SanitizeFileName_ReplacesInvalidCharacters()
    {
        var invalid = Path.GetInvalidFileNameChars()[0];

        Assert.Equal("a_b", FileNaming.SanitizeFileName($"a{invalid}b"));
    }

    [Fact]
    public void SanitizeFileName_EmptyBecomesUntitled() => Assert.Equal("untitled", FileNaming.SanitizeFileName("   "));

    [Fact]
    public void SanitizeFileName_CapsLength() => Assert.True(FileNaming.SanitizeFileName(new string('x', 200)).Length <= 60);

    [Theory]
    [InlineData("https://cdn.example/a/photo.png?sig=1", null, ".png")]
    [InlineData("https://cdn.example/a/photo", "image/jpeg", ".jpg")]
    [InlineData("https://cdn.example/a/photo", "image/webp", ".webp")]
    [InlineData("https://cdn.example/a/photo", null, ".bin")]
    public void GuessExtension(string url, string? contentType, string expected) =>
        Assert.Equal(expected, FileNaming.GuessExtension(url, contentType));

    [Theory]
    [InlineData("a.JPG", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.png", "image/png")]
    public void GuessContentType(string path, string expected) => Assert.Equal(expected, FileNaming.GuessContentType(path));
}
