using System.Collections.Concurrent;
using MessageBroker.Contracts.Models;

namespace MessageBroker.Application.Dispatch;

/// <summary>
/// [Fix 10] SignalR connections that joined each subscription. Defined here so the Worker does not
/// depend on the API; in memory, so per process ([Fix 11]).
/// </summary>
public interface IConnectionRegistry
{
    void Add(Guid subscriptionId, string connectionId);
    bool Remove(Guid subscriptionId, string connectionId);
    /// <summary>Removes a disconnected connection from every subscription it joined.</summary>
    void RemoveConnection(string connectionId);
    int Count(Guid subscriptionId);
    /// <summary>The next connection in round-robin order, or null when none is connected.</summary>
    string? Next(Guid subscriptionId);
}

/// <summary>[Fix 10] Sends a delivery to one SignalR connection. Implemented by the API with its hub context.</summary>
public interface IDeliveryPushChannel
{
    Task SendAsync(string connectionId, Delivery delivery, CancellationToken ct);
}

/// <summary>
/// Lets a push channel wait until a delivery it handed out is settled (ACK or NACK through the hub or
/// REST) or its lease runs out, so in-flight limits count unsettled deliveries.
/// </summary>
public interface IDeliverySettlements
{
    /// <summary>Start tracking before handing the delivery out, so a fast settlement is not missed.</summary>
    DeliverySettlementWaiter Track(long deliveryId, DateTime lockedUntil);

    void Settled(long deliveryId);

    void Renewed(long deliveryId, DateTime lockedUntil);
}

public sealed class DeliverySettlementWaiter : IDisposable
{
    private readonly Action _release;
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lockedUntilTicks;

    internal DeliverySettlementWaiter(DateTime lockedUntil, Action release)
    {
        _lockedUntilTicks = lockedUntil.Ticks;
        _release = release;
    }

    internal void Settle() => _settled.TrySetResult();

    internal void Extend(DateTime lockedUntil) => Interlocked.Exchange(ref _lockedUntilTicks, lockedUntil.Ticks);

    public DateTime LockedUntil => new(Interlocked.Read(ref _lockedUntilTicks), DateTimeKind.Utc);

    /// <summary>True when settled; false when the lease ran out first (renewals extend the wait).</summary>
    public async Task<bool> WaitAsync(TimeProvider time, CancellationToken ct)
    {
        while (true)
        {
            var remaining = LockedUntil - time.GetUtcNow().UtcDateTime;
            if (_settled.Task.IsCompleted)
                return true;
            if (remaining <= TimeSpan.Zero)
                return false;

            using var delayScope = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var finished = await Task.WhenAny(_settled.Task, Task.Delay(remaining, time, delayScope.Token));
            await delayScope.CancelAsync();
            ct.ThrowIfCancellationRequested();
            if (finished == _settled.Task)
                return true;
        }
    }

    public void Dispose() => _release();
}

public sealed class DeliverySettlements : IDeliverySettlements
{
    private readonly ConcurrentDictionary<long, DeliverySettlementWaiter> _waiters = new();

    public DeliverySettlementWaiter Track(long deliveryId, DateTime lockedUntil)
    {
        DeliverySettlementWaiter? waiter = null;
        waiter = new DeliverySettlementWaiter(lockedUntil, () => _waiters.TryRemove(new(deliveryId, waiter!)));
        _waiters[deliveryId] = waiter;
        return waiter;
    }

    public void Settled(long deliveryId)
    {
        if (_waiters.TryGetValue(deliveryId, out var waiter))
            waiter.Settle();
    }

    public void Renewed(long deliveryId, DateTime lockedUntil)
    {
        if (_waiters.TryGetValue(deliveryId, out var waiter))
            waiter.Extend(lockedUntil);
    }
}
