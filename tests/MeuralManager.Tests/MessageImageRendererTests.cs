using MeuralManager.Core.Services;

namespace MeuralManager.Tests;

public class MessageImageRendererTests
{
    // Every character is 10 units wide, so widths are easy to reason about.
    private static float Measure(string s) => s.Length * 10f;

    [Fact]
    public void WrapLines_FitsWordsOnALine() =>
        Assert.Equal(["Dinner is", "at seven"], MessageImageRenderer.WrapLines("Dinner is at seven", 90, Measure));

    [Fact]
    public void WrapLines_KeepsTypedLineBreaks() =>
        Assert.Equal(["Hi", "", "Bye"], MessageImageRenderer.WrapLines("Hi\r\n\r\nBye", 100, Measure));

    [Fact]
    public void WrapLines_BreaksAWordTooLongForALine() =>
        Assert.Equal(["abcd", "efgh", "ij"], MessageImageRenderer.WrapLines("abcdefghij", 40, Measure));

    [Fact]
    public void WrapLines_IgnoresLeadingAndTrailingBlankLines() =>
        Assert.Equal(["Hi"], MessageImageRenderer.WrapLines("\n\nHi\n", 100, Measure));

    [Fact]
    public void Fit_ShortTextUsesTheLargestSize()
    {
        var (size, lines) = MessageImageRenderer.Fit("Hi", 1000, 1000, 100, size => s => s.Length * size / 2);

        Assert.Equal(100, size);
        Assert.Equal(["Hi"], lines);
    }

    [Fact]
    public void Fit_LongTextShrinksUntilEveryLineFits()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 60));
        var (size, lines) = MessageImageRenderer.Fit(text, 800, 450, 200, size => s => s.Length * size / 2);

        Assert.True(size < 200);
        Assert.True(lines.Count * size * 1.22f <= 450);
        Assert.All(lines, line => Assert.True(line.Length * size / 2 <= 800));
    }

    [Fact]
    public void RenderJpeg_ProducesAJpegOfTheGivenSize()
    {
        var jpeg = MessageImageRenderer.RenderJpeg("Happy birthday!", MessagePalette.Bronze, 1080, 1920);

        Assert.Equal([0xFF, 0xD8], jpeg[..2]);
        using var bitmap = SkiaSharp.SKBitmap.Decode(jpeg);
        Assert.Equal((1080, 1920), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public void Palette_UnknownNameFallsBackToBlack() => Assert.Same(MessagePalette.Black, MessagePalette.FromName("Purple"));
}
