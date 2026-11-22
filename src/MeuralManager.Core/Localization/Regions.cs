using System.Globalization;

namespace MeuralManager.Core.Localization;

// The region formats and time zones people can pick in Settings, and how dates are read from
// Meural. The region format only changes how dates and times look; the UI stays English.
public static class Regions
{
    public const string DefaultRegion = "en-US";
    public const string DefaultTimeZone = "UTC";

    public static readonly IReadOnlyList<string> RegionFormats =
    [
        "en-US", "en-GB", "en-IE", "en-CA", "en-AU",
        "de-CH", "de-DE", "de-AT", "de-LI", "fr-CH", "it-CH",
        "fr-FR", "it-IT", "es-ES", "nl-NL",
    ];

    public static CultureInfo Culture(string? region) =>
        CultureInfo.GetCultureInfo(Normalize(region) ?? DefaultRegion);

    // The listed region that matches, ignoring case, or null.
    public static string? Normalize(string? region) =>
        RegionFormats.FirstOrDefault(r => r.Equals(region?.Trim(), StringComparison.OrdinalIgnoreCase));

    // The region format that best matches a browser's language: an exact match, else the first
    // listed region of that language ("de" -> "de-CH"), else the default.
    public static string FromBrowser(string? browserLanguage)
    {
        var tag = (browserLanguage ?? "").Trim();
        if (Normalize(tag) is { } exact)
            return exact;
        var language = tag.Split('-')[0];
        return RegionFormats.FirstOrDefault(r => r.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase))
               ?? DefaultRegion;
    }

    // Zones that must always be offered. Windows lists one zone per Windows id (so its
    // "W. Europe Standard Time" becomes only Europe/Berlin), but resolves these IANA ids through ICU.
    private static readonly string[] CommonTimeZones =
    [
        "Europe/Zurich", "Europe/Vaduz", "Europe/Berlin", "Europe/Vienna", "Europe/Paris", "Europe/Rome", "Europe/Milan",
        "Europe/London", "Europe/Dublin", "Europe/Amsterdam", "Europe/Brussels", "Europe/Luxembourg", "Europe/Madrid",
        "Europe/Lisbon", "Europe/Stockholm", "Europe/Oslo", "Europe/Copenhagen", "Europe/Helsinki", "Europe/Warsaw",
        "Europe/Prague", "Europe/Budapest", "Europe/Athens", "Europe/Istanbul", "Europe/Moscow",
        "America/New_York", "America/Chicago", "America/Denver", "America/Los_Angeles", "America/Toronto", "America/Sao_Paulo",
        "Asia/Dubai", "Asia/Kolkata", "Asia/Singapore", "Asia/Shanghai", "Asia/Tokyo", "Australia/Sydney", "Pacific/Auckland",
        "Africa/Johannesburg", "UTC",
    ];

    // IANA ids of the time zones this machine knows, sorted.
    public static IReadOnlyList<string> TimeZoneIds => TimeZoneIdList.Value;

    // The list for a dropdown: always contains `current`, so a saved zone is never shown (and
    // saved) as a different one.
    public static IReadOnlyList<string> TimeZoneIdsWith(string? current) =>
        current is null || TimeZoneIds.Contains(current) || !IsKnownTimeZone(current)
            ? TimeZoneIds
            : TimeZoneIds.Append(current).Order(StringComparer.Ordinal).ToList();

    private static readonly Lazy<IReadOnlyList<string>> TimeZoneIdList = new(() =>
        TimeZoneInfo.GetSystemTimeZones()
            .Select(tz => tz.HasIanaId ? tz.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) ? iana : null)
            .OfType<string>()
            .Where(id => id.Contains('/') || id == "UTC")
            .Concat(CommonTimeZones.Where(IsKnownTimeZone))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList());

    public static TimeZoneInfo TimeZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    public static bool IsKnownTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
    }

    // Meural sends upload dates as text with an offset that has no colon, e.g.
    // "2026-11-10T16:10:32-0500". Anything else DateTimeOffset can read is accepted too.
    public static DateTimeOffset? ParseMeuralDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var s = text.Trim();
        // "-0500" -> "-05:00" so the round-trip parser takes it.
        if (s.Length > 5 && (s[^5] is '+' or '-') && s[^4..].All(char.IsDigit) && s.Contains('T'))
            s = s[..^2] + ":" + s[^2..];
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }
}
