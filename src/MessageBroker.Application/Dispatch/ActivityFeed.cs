using System.Threading.Channels;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MessageBroker.Application.Dispatch;

/// <summary>
/// Tells connected admin dashboards what changed. Callers report and move on: the methods never
/// block, never throw, and do nothing while no admin is watching.
/// </summary>
public interface IBrokerActivityFeed
{
    void Published(Guid messageId);

    /// <summary>A delivery was leased, settled, expired or requeued.</summary>
    void DeliveryChanged(Guid messageId, long deliveryId);

    /// <summary>Counts changed without a known message (for example a maintenance pass).</summary>
    void Changed();
}

/// <summary>The default when the host has no admin hub (tests, tools).</summary>
public sealed class NullBrokerActivityFeed : IBrokerActivityFeed
{
    public void Published(Guid messageId) { }
    public void DeliveryChanged(Guid messageId, long deliveryId) { }
    public void Changed() { }
}

/// <summary>[Fix 10] Sends to the admin hub's clients. Implemented by the API with its hub context.</summary>
public interface IAdminActivitySink
{
    Task SendActivityAsync(IReadOnlyList<MessageActivity> batch, CancellationToken ct);
    Task SendOverviewChangedAsync(CancellationToken ct);
}

/// <summary>
/// Collects activity in a bounded queue (oldest dropped when full) and sends it to admin dashboards in
/// batches every <see cref="BatchInterval"/>, with an <see cref="AdminHub.OverviewChanged"/> ping at
/// most once per <see cref="OverviewInterval"/> while anything happened. In memory, per process ([Fix 11]).
/// </summary>
public sealed class BrokerActivityFeed(IAdminActivitySink sink, TimeProvider time, ILogger<BrokerActivityFeed> logger)
    : BackgroundService, IBrokerActivityFeed
{
    public const int Capacity = 10_000;
    public const int MaxBatchSize = 500;
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan OverviewInterval = TimeSpan.FromSeconds(1);

    private readonly Channel<MessageActivity> _queue = Channel.CreateBounded<MessageActivity>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private int _admins;
    private int _changed;
    private DateTimeOffset _lastOverview = DateTimeOffset.MinValue;

    public int ConnectedAdmins => Volatile.Read(ref _admins);

    public void AdminConnected() => Interlocked.Increment(ref _admins);

    public void AdminDisconnected() => Interlocked.Decrement(ref _admins);

    public void Published(Guid messageId) => Enqueue(ActivityKinds.Published, messageId, null);

    public void DeliveryChanged(Guid messageId, long deliveryId) => Enqueue(ActivityKinds.DeliveryChanged, messageId, deliveryId);

    public void Changed()
    {
        if (ConnectedAdmins > 0)
            Volatile.Write(ref _changed, 1);
    }

    private void Enqueue(string kind, Guid messageId, long? deliveryId)
    {
        if (ConnectedAdmins == 0)
            return;
        _queue.Writer.TryWrite(new MessageActivity(kind, messageId, deliveryId, time.GetUtcNow().UtcDateTime));
        Volatile.Write(ref _changed, 1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(BatchInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await FlushAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Sending activity to admin dashboards failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One send: the queued activity (up to <see cref="MaxBatchSize"/>) and, when due, the overview ping.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var batch = new List<MessageActivity>();
        while (batch.Count < MaxBatchSize && _queue.Reader.TryRead(out var activity))
            batch.Add(activity);

        if (ConnectedAdmins == 0)
            return; // whatever was queued before the last admin left is dropped

        if (batch.Count > 0)
            await sink.SendActivityAsync(batch, ct);

        var now = time.GetUtcNow();
        if (now - _lastOverview >= OverviewInterval && Interlocked.Exchange(ref _changed, 0) == 1)
        {
            _lastOverview = now;
            await sink.SendOverviewChangedAsync(ct);
        }
    }
}
