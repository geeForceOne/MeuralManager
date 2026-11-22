using MeuralManager.Core.Localization;
using MeuralManager.Web.Services;

namespace MeuralManager.Tests;

public class RegionTests
{
    [Fact]
    public void ParseMeuralDate_reads_offset_without_colon()
    {
        var value = Regions.ParseMeuralDate("2026-11-10T16:10:32-0500");
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 16, 10, 32, TimeSpan.FromHours(-5)), value);
    }

    [Theory]
    [InlineData("2026-11-10T21:10:32Z")]
    [InlineData("2026-11-10T16:10:32-05:00")]
    public void ParseMeuralDate_reads_other_iso_forms(string text) =>
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 21, 10, 32, TimeSpan.Zero), Regions.ParseMeuralDate(text)!.Value.ToUniversalTime());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void ParseMeuralDate_returns_null_for_unreadable(string? text) =>
        Assert.Null(Regions.ParseMeuralDate(text));

    [Theory]
    [InlineData("de-CH", "de-CH")]
    [InlineData("DE-ch", "de-CH")]
    [InlineData("de", "de-CH")]
    [InlineData("fr-BE", "fr-CH")]
    [InlineData("en-NZ", "en-US")]
    [InlineData("ja-JP", "en-US")]
    [InlineData(null, "en-US")]
    public void FromBrowser_picks_closest_listed_region(string? browser, string expected) =>
        Assert.Equal(expected, Regions.FromBrowser(browser));

    [Fact]
    public void UploadedLine_uses_region_and_time_zone()
    {
        var dates = new DateDisplay();
        dates.Apply("de-CH", "Europe/Zurich");
        // 16:10 in New York is 22:10 in Zurich.
        Assert.Equal("Uploaded 10.11.2026 22:10 (ID 42)", dates.UploadedLine("2026-11-10T16:10:32-0500", 42));
    }

    [Fact]
    public void UploadedLine_en_US()
    {
        var dates = new DateDisplay();
        dates.Apply("en-US", "America/New_York");
        Assert.Equal("Uploaded 11/10/2026 4:10 PM (ID 42)", dates.UploadedLine("2026-11-10T16:10:32-0500", 42).Replace(' ', ' '));
    }

    [Fact]
    public void UploadedLine_without_date_is_just_the_id() =>
        Assert.Equal("(ID 7)", new DateDisplay().UploadedLine(null, 7));

    [Fact]
    public void UploadedLine_keeps_unreadable_text() =>
        Assert.Equal("Uploaded sometime (ID 7)", new DateDisplay().UploadedLine("sometime", 7));

    [Fact]
    public void Apply_falls_back_for_unknown_values()
    {
        var dates = new DateDisplay();
        dates.Apply("xx-XX", "Nowhere/Land");
        Assert.Equal("en-US", dates.Region);
        Assert.Equal("UTC", dates.TimeZoneId);
    }
}
