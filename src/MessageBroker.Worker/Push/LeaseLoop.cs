using System.Collections.Concurrent;
using MessageBroker.Application;
using MessageBroker.Application.Dispatch;
using MessageBroker.Application.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MessageBroker.Worker.Push;

/// <summary>
/// Leases due deliveries of active push subscriptions and hands them to their channel. Runs on the
/// publish signal, when a delivery finishes (capacity freed), or every PollIntervalMs. Per
/// subscription it never has more than MaxConcurrentDeliveries calls in flight, and leases nothing
/// while the channel allows none (open circuit, no connected client).
/// </summary>
public sealed class LeaseLoop(
    ISubscriptionRepository subscriptions,
    IDeliveryRepository deliveries,
    IEnumerable<IPushChannel> channels,
    IDispatcherSignal published,
    IOptions<BrokerOptions> options,
    TimeProvider time,
    ILogger<LeaseLoop> logger) : BackgroundService
{
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, IPushChannel> _channels = channels.ToDictionary(c => c.Mode, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, int> _inFlight = new();
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private readonly DispatcherSignal _capacityFreed = new();

    public int InFlight(Guid subscriptionId) => _inFlight.GetValueOrDefault(subscriptionId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(options.Value.Dispatcher.PollIntervalMs);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var signals = new[]
                {
                    published.WaitAsync(wake.Token),
                    _capacityFreed.WaitAsync(wake.Token),
                    Task.Delay(interval, time, wake.Token),
                };

                try
                {
                    await PassAsync(waitForDeliveries: false, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Lease pass failed; retrying");
                }

                await Task.WhenAny(signals);
                await wake.CancelAsync();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Let calls in flight record their result; anything unfinished is redelivered after its lease.
            await Task.WhenAll(_running.Keys).WaitAsync(ShutdownGrace, CancellationToken.None)
                .ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// One pass, for tests and diagnostics. By default it also waits for the deliveries it started to
    /// finish (for SignalR: until the client settles them or the lease ends).
    /// </summary>
    public Task<int> RunOnceAsync(CancellationToken ct, bool waitForDeliveries = true) => PassAsync(waitForDeliveries, ct);

    private async Task<int> PassAsync(bool waitForDeliveries, CancellationToken ct)
    {
        var started = new List<Task>();
        foreach (var subscription in await subscriptions.ListActivePushAsync(ct))
        {
            if (!subscription.HasDueDeliveries || !_channels.TryGetValue(subscription.DeliveryMode, out var channel))
                continue;

            var capacity = Math.Min(subscription.MaxConcurrentDeliveries - InFlight(subscription.SubscriptionId), channel.Allowance(subscription));
            if (capacity <= 0)
                continue;

            var leased = await deliveries.LeaseAsync(subscription.SubscriptionId, capacity, channel.Mode, appId: null, ct);
            foreach (var delivery in leased)
                started.Add(Track(RunDeliveryAsync(channel, subscription, delivery, ct)));
        }

        if (waitForDeliveries)
            await Task.WhenAll(started);
        return started.Count;
    }

    private async Task RunDeliveryAsync(IPushChannel channel, PushSubscriptionRecord subscription, LeasedDeliveryRecord delivery, CancellationToken ct)
    {
        _inFlight.AddOrUpdate(subscription.SubscriptionId, 1, (_, n) => n + 1);
        try
        {
            await channel.DeliverAsync(subscription, delivery, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The lease expires and maintenance retries the delivery.
            logger.LogError(ex, "Delivery {DeliveryId} on subscription {SubscriptionId} failed unexpectedly",
                delivery.DeliveryId, subscription.SubscriptionId);
        }
        finally
        {
            _inFlight.AddOrUpdate(subscription.SubscriptionId, 0, (_, n) => n - 1);
            _capacityFreed.Notify();
        }
    }

    private Task Track(Task task)
    {
        _running.TryAdd(task, 0);
        _ = task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
        return task;
    }
}
