namespace MessageBroker.Application.Dispatch;

/// <summary>
/// Wakes the lease loop and long-polling receivers as soon as a message is published, instead of
/// waiting for the next poll. In-memory, so it only reaches this process ([Fix 11]).
/// </summary>
public interface IDispatcherSignal
{
    void Notify();

    /// <summary>Completes on the next <see cref="Notify"/> after the call, or when cancelled.</summary>
    Task WaitAsync(CancellationToken ct);
}

public sealed class DispatcherSignal : IDispatcherSignal
{
    private TaskCompletionSource _next = NewSource();

    public void Notify() => Interlocked.Exchange(ref _next, NewSource()).TrySetResult();

    public Task WaitAsync(CancellationToken ct) => Volatile.Read(ref _next).Task.WaitAsync(ct);

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
