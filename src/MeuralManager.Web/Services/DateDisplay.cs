using System.Globalization;
using MeuralManager.Core.Localization;

namespace MeuralManager.Web.Services;

// How dates and times are shown in this tab: the account's region format (Settings) for the look,
// its time zone for moments in time. Every date or time on screen goes through here, so no page
// formats one on its own. MainLayout applies the saved values on sign-in; until then it's en-US
// and UTC.
public sealed class DateDisplay
{
    public CultureInfo Culture { get; private set; } = Regions.Culture(null);
    public string Region => Culture.Name;
    public string TimeZoneId { get; private set; } = Regions.DefaultTimeZone;
    private TimeZoneInfo _zone = TimeZoneInfo.Utc;

    public event Action? Changed;

    public void Apply(string? region, string? timeZoneId)
    {
        Culture = Regions.Culture(region);
        _zone = Regions.TimeZone(timeZoneId);
        TimeZoneId = Regions.IsKnownTimeZone(timeZoneId) ? timeZoneId! : Regions.DefaultTimeZone;
        Changed?.Invoke();
    }

    // A moment in time, moved into the account's time zone.
    public DateTime Local(DateTimeOffset moment) => TimeZoneInfo.ConvertTime(moment, _zone).DateTime;

    public DateTime Local(DateTime utc) =>
        Local(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));

    public DateTime Today => Local(DateTimeOffset.UtcNow).Date;

    public string DateAndTime(DateTimeOffset moment) => Local(moment).ToString("g", Culture);
    public string DateAndTime(DateTime utc) => Local(utc).ToString("g", Culture);
    public string Time(DateTime utc) => Local(utc).ToString("T", Culture);

    // A clock time as it was where it happened (a photo's "taken" time), not moved to a time zone.
    public string WallClock(DateTime wallClock) => wallClock.ToString("g", Culture);

    // Meural's upload date: the region's short date and time, or the raw text if it can't be read.
    public string? Uploaded(string? createdAt) =>
        Regions.ParseMeuralDate(createdAt) is { } moment ? DateAndTime(moment) : createdAt;

    // "Uploaded 11/10/2026 4:10 PM (ID 4718293)", or just "(ID 4718293)" without a date.
    public string UploadedLine(string? createdAt, long? id)
    {
        var date = Uploaded(createdAt);
        var idPart = id is long value ? $"(ID {value})" : "";
        return string.IsNullOrWhiteSpace(date) ? idPart : $"Uploaded {date} {idPart}".TrimEnd();
    }

    // A photo day's heading: "Today", "Yesterday", the weekday and date within this year, and
    // the long date for older years.
    public string DayLabel(DateTime day)
    {
        if (day == DateTime.MinValue)
            return "Unknown date";
        var today = Today;
        if (day == today)
            return "Today";
        if (day == today.AddDays(-1))
            return "Yesterday";
        return day.Year == today.Year
            ? $"{day.ToString("ddd", Culture)}, {day.ToString(Culture.DateTimeFormat.MonthDayPattern, Culture)}"
            : day.ToString("D", Culture);
    }

    // A dropdown label for a region: its name plus a sample, e.g. "English (United States) - 11/10/2026 4:10 PM".
    public static string RegionLabel(string region)
    {
        var culture = Regions.Culture(region);
        var sample = new DateTime(2026, 11, 10, 16, 10, 0);
        return $"{culture.EnglishName} - {sample.ToString("g", culture)}";
    }
}
