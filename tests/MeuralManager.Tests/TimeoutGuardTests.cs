using System.Net;
using MeuralManager.Core.Api;
using MeuralManager.Core.Models;
using MeuralManager.Core.Services;
using MeuralManager.Web.Services;

namespace MeuralManager.Tests;

public class TimeoutGuardTests
{
    private static readonly TimeSpan After = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(20);

    private static TimeoutGuard.Probe<string> Yes(string v) => TimeoutGuard.Probe<string>.Yes(v);
    private static TimeoutGuard.Probe<string> No => TimeoutGuard.Probe<string>.No;

    [Fact]
    public async Task FastCallWinsWithoutAnyCheck()
    {
        var checks = 0;
        var result = await TimeoutGuard.RunAsync(
            _ => Task.FromResult("reply"),
            _ => { checks++; return Task.FromResult(Yes("check")); },
            CancellationToken.None, checkAfter: After, checkEvery: Every);

        Assert.Equal("reply", result);
        Assert.Equal(0, checks);
    }

    [Fact]
    public async Task SlowCallIsOvertakenByACheckThatSeesTheChange()
    {
        var callCanceled = false;
        var result = await TimeoutGuard.RunAsync(
            async t =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), t); }
                catch (OperationCanceledException) { callCanceled = true; throw; }
                return "reply";
            },
            _ => Task.FromResult(Yes("already done")),
            CancellationToken.None, checkAfter: After, checkEvery: Every);

        Assert.Equal("already done", result);
        await Task.Delay(50);
        Assert.True(callCanceled);
    }

    [Fact]
    public async Task ChecksKeepGoingUntilTheChangeShowsUp()
    {
        var checks = 0;
        var result = await TimeoutGuard.RunAsync(
            async t => { await Task.Delay(TimeSpan.FromSeconds(10), t); return "reply"; },
            _ => Task.FromResult(++checks >= 3 ? Yes("third time") : No),
            CancellationToken.None, checkAfter: After, checkEvery: Every);

        Assert.Equal("third time", result);
        Assert.Equal(3, checks);
    }

    [Fact]
    public async Task TimeoutWithTheChangeMadeCountsAsSuccess()
    {
        var result = await TimeoutGuard.RunAsync<string>(
            _ => throw new TaskCanceledException("our time limit"),
            _ => Task.FromResult(Yes("it worked")),
            CancellationToken.None, checkAfter: After, checkEvery: Every);

        Assert.Equal("it worked", result);
    }

    [Fact]
    public async Task TimeoutWithTheChangeNotMadeStillFails()
    {
        var checks = 0;
        await Assert.ThrowsAsync<TaskCanceledException>(() => TimeoutGuard.RunAsync<string>(
            _ => throw new TaskCanceledException("our time limit"),
            _ => { checks++; return Task.FromResult(No); },
            CancellationToken.None, checkAfter: After, checkEvery: Every));

        Assert.Equal(TimeoutGuard.FinalChecks, checks);
    }

    [Fact]
    public async Task ARealRefusalIsNotCheckedOrHidden()
    {
        var checks = 0;
        await Assert.ThrowsAsync<MeuralApiException>(() => TimeoutGuard.RunAsync<string>(
            _ => throw new MeuralApiException("bad request", HttpStatusCode.BadRequest),
            _ => { checks++; return Task.FromResult(Yes("no")); },
            CancellationToken.None, checkAfter: After, checkEvery: Every));

        Assert.Equal(0, checks);
    }

    [Fact]
    public async Task GatewayTimeoutAnswerIsCheckedBeforeReportingIt()
    {
        var result = await TimeoutGuard.RunAsync(
            _ => Task.FromResult(new DeleteOutcome(false, HttpStatusCode.GatewayTimeout)),
            _ => Task.FromResult(TimeoutGuard.Probe<DeleteOutcome>.Yes(new DeleteOutcome(true, HttpStatusCode.OK))),
            CancellationToken.None,
            isUncertainResult: o => !o.Success && TimeoutGuard.IsUncertain(o.StatusCode),
            checkAfter: After, checkEvery: Every);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task UserCancelStopsEverything()
    {
        using var cts = new CancellationTokenSource();
        var task = TimeoutGuard.RunAsync(
            async t => { await Task.Delay(TimeSpan.FromSeconds(10), t); return "reply"; },
            _ => Task.FromResult(No),
            cts.Token, checkAfter: After, checkEvery: Every);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task AFailingCheckJustMeansNotYet()
    {
        var checks = 0;
        var result = await TimeoutGuard.RunAsync(
            async t => { await Task.Delay(TimeSpan.FromMilliseconds(150), t); return "reply"; },
            _ => { checks++; throw new HttpRequestException("Meural is slow too"); },
            CancellationToken.None, checkAfter: After, checkEvery: Every);

        Assert.Equal("reply", result);
        Assert.True(checks > 0);
    }
}

public class TimedOutUploadTests
{
    private static MeuralItem Item(long id, string name) => new() { Id = id, Name = name };

    [Fact]
    public void FindsTheUploadByItsIntendedName()
    {
        var found = PlaylistService.FindTimedOutUpload(
            [Item(1, "old"), Item(7, "other new"), Item(8, "Sunset")], new HashSet<long> { 1 }, new HashSet<long>(), "Sunset");

        Assert.Equal(8, found?.Id);
    }

    [Fact]
    public void FindsTheOnlyNewImageWhenTheRenameNeverHappened()
    {
        // Meural gives a fresh upload a random name until it's renamed.
        var found = PlaylistService.FindTimedOutUpload(
            [Item(1, "old"), Item(9, "6tFy4lv")], new HashSet<long> { 1 }, new HashSet<long>(), "Sunset");

        Assert.Equal(9, found?.Id);
    }

    [Fact]
    public void IgnoresImagesThisBatchAlreadyUploaded()
    {
        var found = PlaylistService.FindTimedOutUpload(
            [Item(1, "old"), Item(9, "First"), Item(10, "x7Kq2Lm")], new HashSet<long> { 1 }, new HashSet<long> { 9 }, "Second");

        Assert.Equal(10, found?.Id);
    }

    [Fact]
    public void SeveralUnexplainedNewImagesAreAmbiguous()
    {
        var found = PlaylistService.FindTimedOutUpload(
            [Item(1, "old"), Item(9, "aaaaaaa"), Item(10, "bbbbbbb")], new HashSet<long> { 1 }, new HashSet<long>(), "Sunset");

        Assert.Null(found);
    }
}

public class PhotoSourceTests
{
    [Theory]
    [InlineData("Water Lilies · gArt", "gArt", "Water Lilies")]
    [InlineData("Water Lilies", "gArt", "Water Lilies")]
    [InlineData("Water Lilies", null, "Water Lilies")]
    [InlineData(" · gArt", "gArt", "· gArt")]
    [InlineData("", "gArt", "Restored gallery")]
    [InlineData(null, null, "Restored gallery")]
    public void PlaylistNameDropsTheFrameSuffix(string? albumName, string? description, string expected) =>
        Assert.Equal(expected, PhotoSource.PlaylistNameFor(albumName, description));

    [Fact]
    public void ImmichKeepsItsOldSettingNames() => Assert.Equal("Immich", PhotoSource.Immich.Key);

    [Theory]
    [InlineData("3f1c9a52-6b1e-4f0c-9a1d-2b7f0e4c8d11", true)] // Immich
    [InlineData("58", true)]                                    // MeuralImageBackup
    [InlineData("", false)]
    [InlineData("58/../users/me", false)]
    [InlineData("-1", false)]
    [InlineData("1234567890123456789", false)]
    public void ProxyAcceptsBothSourcesIdsAndNothingElse(string id, bool ok) =>
        Assert.Equal(ok, ImmichEndpoints.IsAssetId(id));
}
