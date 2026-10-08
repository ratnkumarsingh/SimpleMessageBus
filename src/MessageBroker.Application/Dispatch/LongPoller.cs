using MessageBroker.Contracts.Models;

namespace MessageBroker.Application.Dispatch;

/// <summary>Pull receive limits (spec section 11): maxMessages 1–32, waitSeconds 0–30. Out-of-range values are clamped.</summary>
public static class ReceiveRules
{
    public const int MaxMessages = 32;
    public const int MaxWaitSeconds = 30;

    public static (int MaxMessages, TimeSpan Wait) Clamp(ReceiveRequest? request) =>
        (Math.Clamp(request?.MaxMessages ?? 1, 1, MaxMessages),
         TimeSpan.FromSeconds(Math.Clamp(request?.WaitSeconds ?? 0, 0, MaxWaitSeconds)));
}

/// <summary>
/// Long polling for pull receive: tries to lease, and while nothing is available waits for the
/// publish signal or a one-second tick (which catches retries whose backoff has elapsed), until the
/// wait time is used up.
/// </summary>
public sealed class LongPoller(IDispatcherSignal signal, TimeProvider time)
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    public async Task<IReadOnlyList<T>> PollAsync<T>(
        Func<CancellationToken, Task<IReadOnlyList<T>>> attempt, TimeSpan wait, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + wait;
        while (true)
        {
            // Listen before trying, so a publish between the attempt and the wait is not missed.
            using var waitScope = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var published = signal.WaitAsync(waitScope.Token);

            var items = await attempt(ct);
            var remaining = deadline - time.GetUtcNow();
            if (items.Count > 0 || remaining <= TimeSpan.Zero)
                return items;

            var tick = Task.Delay(remaining < Tick ? remaining : Tick, time, waitScope.Token);
            await Task.WhenAny(published, tick);
            await waitScope.CancelAsync(); // releases whichever of the two is still pending
            ct.ThrowIfCancellationRequested();
        }
    }
}
