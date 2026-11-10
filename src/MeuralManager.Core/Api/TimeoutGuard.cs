using System.Net;

namespace MeuralManager.Core.Api;

// "Smarter timeout handling" (an experimental, per-account setting): Meural often finishes a change
// but answers slowly, or not before our time limit, so "no answer" doesn't mean "it failed". This
// runs a call and, once it has waited CheckAfter, checks alongside it every CheckEvery whether the
// change has already happened - whichever finishes first wins. When the call fails in a way that
// leaves the outcome unknown (our time limit, a dropped connection, a gateway timeout from Meural's
// side), a last few checks decide between success and a real failure. Only a confirmed "not done"
// is reported as a failure, so nothing is ever retried blindly (an upload retried that way would
// leave a duplicate).
public static class TimeoutGuard
{
    public static readonly TimeSpan DefaultCheckAfter = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultCheckEvery = TimeSpan.FromSeconds(10);

    // How many checks run after an unknown outcome, CheckEvery apart: Meural may still be working
    // on the change when the call gives up.
    public const int FinalChecks = 3;

    // A check answers "done, and this is the result the call would have returned", or "not (yet)".
    public readonly record struct Probe<T>(bool Done, T Value)
    {
        public static Probe<T> No => new(false, default!);
        public static Probe<T> Yes(T value) => new(true, value);
    }

    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> call,
        Func<CancellationToken, Task<Probe<T>>> check,
        CancellationToken ct,
        Func<T, bool>? isUncertainResult = null,
        TimeSpan? checkAfter = null,
        TimeSpan? checkEvery = null)
    {
        var after = checkAfter ?? DefaultCheckAfter;
        var every = checkEvery ?? DefaultCheckEvery;

        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var callTask = InvokeAsync(call, callCts.Token);

        await Task.WhenAny(callTask, Task.Delay(after, ct));
        ct.ThrowIfCancellationRequested();

        while (!callTask.IsCompleted)
        {
            var probe = await TryCheckAsync(check, ct);
            if (probe.Done)
            {
                // Meural already did it; stop waiting for its reply. The abandoned call's outcome
                // (most likely a cancellation) is observed so it doesn't surface as unobserved.
                callCts.Cancel();
                _ = callTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                return probe.Value;
            }

            await Task.WhenAny(callTask, Task.Delay(every, ct));
            ct.ThrowIfCancellationRequested();
        }

        T result;
        try
        {
            result = await callTask;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsUncertain(ex))
        {
            var probe = await FinalCheckAsync(check, every, ct);
            if (probe.Done)
                return probe.Value;
            throw;
        }

        if (isUncertainResult?.Invoke(result) == true)
        {
            var probe = await FinalCheckAsync(check, every, ct);
            if (probe.Done)
                return probe.Value;
        }

        return result;
    }

    // A call that throws before its first await still ends up as a faulted task, so it gets the
    // same checks as one that fails later.
    private static async Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct) => await call(ct);

    // Failures after which the change may or may not have happened on Meural's side.
    public static bool IsUncertain(Exception ex) => ex switch
    {
        OperationCanceledException or TimeoutException or HttpRequestException => true,
        MeuralApiException { StatusCode: { } status } => IsUncertain(status),
        _ => false,
    };

    // Gateway-style answers: something between us and Meural gave up, not Meural saying no.
    public static bool IsUncertain(HttpStatusCode status) => (int)status is 408 or 502 or 503 or 504 or 522 or 524;

    private static async Task<Probe<T>> FinalCheckAsync<T>(Func<CancellationToken, Task<Probe<T>>> check, TimeSpan every, CancellationToken ct)
    {
        for (var i = 0; i < FinalChecks; i++)
        {
            if (i > 0)
                await Task.Delay(every, ct);
            var probe = await TryCheckAsync(check, ct);
            if (probe.Done)
                return probe;
        }

        return Probe<T>.No;
    }

    // A check that fails (slow too, or Meural down) just means "don't know yet".
    private static async Task<Probe<T>> TryCheckAsync<T>(Func<CancellationToken, Task<Probe<T>>> check, CancellationToken ct)
    {
        try
        {
            return await check(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Probe<T>.No;
        }
    }
}
