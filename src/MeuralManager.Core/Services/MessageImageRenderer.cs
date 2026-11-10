using SkiaSharp;

namespace MeuralManager.Core.Services;

// The color pairs a message picture can use - background and text, chosen so the text stays
// readable from across a room (each pair is well above WCAG's 7:1 contrast).
public sealed record MessagePalette(string Name, string Background, string Text)
{
    public static readonly MessagePalette Black = new("Black", "#111111", "#F3F0EA");
    public static readonly MessagePalette Bronze = new("Bronze", "#BC8F68", "#1D1712");
    public static readonly MessagePalette White = new("White", "#F6F3EE", "#1D1D1F");

    public static readonly IReadOnlyList<MessagePalette> All = [Black, Bronze, White];

    public static MessagePalette FromName(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Black;
}

// Draws a message (plain text on a solid color) as a picture for a Canvas frame - server-side,
// so anything that can call the server (the Message page now, a Home Assistant webhook later)
// gets the same picture without a browser. The text is wrapped and set as large as fits.
//
// Uses Inter SemiBold embedded in this assembly (Resources/Fonts, SIL Open Font License) rather
// than a system font: the Linux container has no fonts installed, and SkiaSharp's
// NoDependencies native build can't look any up anyway.
public static class MessageImageRenderer
{
    // A Canvas II screen; a vertical frame gets the same size turned on its side.
    public const int LongSide = 1920;
    public const int ShortSide = 1080;

    private const float MarginFraction = 0.08f;
    private const float LineHeightFactor = 1.22f;
    private const float MaxSizeFraction = 0.2f; // of the shorter side - a single word shouldn't fill the frame
    private const float MinSize = 12f;

    private static readonly Lazy<SKTypeface> Typeface = new(LoadTypeface);

    public static byte[] RenderJpeg(string text, MessagePalette palette, int width, int height, int quality = 92)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Couldn't create a drawing surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColor.Parse(palette.Background));

        var boxWidth = width * (1 - 2 * MarginFraction);
        var boxHeight = height * (1 - 2 * MarginFraction);

        using var font = new SKFont(Typeface.Value) { Subpixel = true, Edging = SKFontEdging.Antialias };
        var (size, lines) = Fit(text, boxWidth, boxHeight, Math.Min(width, height) * MaxSizeFraction, s =>
        {
            font.Size = s;
            return line => font.MeasureText(line);
        });
        font.Size = size;

        using var paint = new SKPaint { Color = SKColor.Parse(palette.Text), IsAntialias = true };
        var lineHeight = size * LineHeightFactor;
        var metrics = font.Metrics;
        // Centers the block on the cap height rather than the full line box, which would sit the
        // text visibly low (the descender space below the last line is mostly empty).
        var textHeight = (lines.Count - 1) * lineHeight + metrics.CapHeight;
        var baseline = (height - textHeight) / 2 + metrics.CapHeight;

        foreach (var line in lines)
        {
            canvas.DrawText(line, width / 2f, baseline, SKTextAlign.Center, font, paint);
            baseline += lineHeight;
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    // The largest font size (from maxSize down) at which the wrapped text fits the box, and its
    // lines. measureAt(size) returns a width measurer for that size. Kept free of SkiaSharp so
    // the layout rules can be tested with a fake measurer.
    public static (float Size, List<string> Lines) Fit(
        string text, float boxWidth, float boxHeight, float maxSize, Func<float, Func<string, float>> measureAt)
    {
        var size = maxSize;
        while (true)
        {
            var measure = measureAt(size);
            var lines = WrapLines(text, boxWidth, measure);
            var fits = lines.Count * size * LineHeightFactor <= boxHeight && lines.All(l => measure(l) <= boxWidth);
            if (fits || size <= MinSize)
                return (size, lines);
            size = Math.Max(MinSize, size * 0.94f);
        }
    }

    // Greedy word wrap. Line breaks the user typed are kept (an empty line stays an empty line);
    // a word too long for a line on its own is broken between characters.
    public static List<string> WrapLines(string text, float maxWidth, Func<string, float> measure)
    {
        var lines = new List<string>();
        var paragraphs = text.Replace("\r\n", "\n").Trim('\n').Split('\n');

        foreach (var paragraph in paragraphs)
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                lines.Add("");
                continue;
            }

            var current = "";
            foreach (var word in words)
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (measure(candidate) <= maxWidth)
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0)
                    lines.Add(current);

                current = word;
                while (current.Length > 1 && measure(current) > maxWidth)
                {
                    var take = current.Length - 1;
                    while (take > 1 && measure(current[..take]) > maxWidth)
                        take--;
                    lines.Add(current[..take]);
                    current = current[take..];
                }
            }

            lines.Add(current);
        }

        return lines;
    }

    private static SKTypeface LoadTypeface()
    {
        using var stream = typeof(MessageImageRenderer).Assembly.GetManifestResourceStream("Inter-SemiBold.ttf")
            ?? throw new InvalidOperationException("The embedded message font is missing.");
        return SKTypeface.FromStream(stream)
            ?? throw new InvalidOperationException("The embedded message font couldn't be loaded.");
    }
}
