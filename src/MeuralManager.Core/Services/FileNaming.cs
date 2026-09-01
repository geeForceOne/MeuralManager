using System.Text;
using System.Text.RegularExpressions;

namespace MeuralManager.Core.Services;

public static class FileNaming
{
    // Matches generic camera/screenshot filenames, bare UUIDs, all-digit names, a short letter
    // prefix glued to a digit run (p1234, a1234567), an Unsplash download name
    // (ming-han-low-tfcP-jGlY7c-unsplash), and "(untitled)"-style placeholders - names that
    // carry no actual information about what's in the picture, and are therefore good
    // candidates to flag for an AI-suggested rename. This is deliberately conservative (false
    // negatives are fine, a false positive nags the user for no reason) - it only flags the whole
    // name, never a substring match.
    private static readonly Regex GenericNamePattern = new(
        """^(?:(?:img|dsc|dscn|pxl|dji|photo|screenshot|screen shot|image)[-_ ]?\d*|[a-z]{1,4}[-_]?\d{4,}|.*-unsplash|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|\d+|untitled|\(untitled\))$""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksGeneric(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        var trimmed = name.Trim();
        if (GenericNamePattern.IsMatch(trimmed))
            return true;

        // Beyond the specific shapes above: a name carrying more than a handful of digits is
        // almost always machine-generated (a camera body code plus shutter count like
        // "I76A6947_hdr", a social export's chain of numeric IDs) rather than something a person
        // typed - a real, descriptive title rarely contains more digits than, say, a year.
        return trimmed.Count(char.IsDigit) > 4;
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? "untitled" : Truncate(result, 60);
    }

    public static string GuessExtension(string url, string? contentType)
    {
        // Prefer the extension already in the URL if it looks like an image extension.
        var pathPart = url.Split('?')[0];
        var urlExt = Path.GetExtension(pathPart);
        if (!string.IsNullOrEmpty(urlExt) && urlExt.Length <= 5)
            return urlExt;

        return contentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => ".bin",
        };
    }

    public static string GuessContentType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream",
        };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
