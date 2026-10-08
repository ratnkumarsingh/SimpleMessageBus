using MessageBroker.Application.Dispatch;
using MessageBroker.Application.Persistence;
using MessageBroker.Application.Services;
using MessageBroker.Domain;
using MessageBroker.Worker.Push;
using Microsoft.Extensions.Logging;

namespace MessageBroker.Worker.SignalR;

/// <summary>
/// SignalR delivery (spec section 8.3): each delivery goes to exactly one connected client of the
/// subscription, in round-robin order, never to a group. The delivery counts as in flight until the
/// client settles it or its lease runs out, so MaxConcurrentDeliveries bounds unsettled deliveries.
/// With no client connected nothing is leased.
/// </summary>
public sealed class SignalRChannel(
    IConnectionRegistry connections,
    IDeliveryPushChannel push,
    IDeliverySettlements settlements,
    TimeProvider time,
    ILogger<SignalRChannel> logger) : IPushChannel
{
    public string Mode => nameof(DeliveryMode.SignalR);

    public int Allowance(PushSubscriptionRecord subscription) =>
        connections.Count(subscription.SubscriptionId) > 0 ? int.MaxValue : 0;

    public async Task DeliverAsync(PushSubscriptionRecord subscription, LeasedDeliveryRecord delivery, CancellationToken ct)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = delivery.MessageId,
            ["CorrelationId"] = delivery.CorrelationId,
            ["DeliveryId"] = delivery.DeliveryId,
            ["SubscriptionId"] = subscription.SubscriptionId,
        });

        using var waiter = settlements.Track(delivery.DeliveryId, delivery.LockedUntil);
        var connectionId = connections.Next(subscription.SubscriptionId);
        if (connectionId is null)
        {
            // The last client left between leasing and sending; the lease expires and the delivery is retried.
            logger.LogWarning("No SignalR connection for delivery {DeliveryId}; it will be retried after its lease", delivery.DeliveryId);
            return;
        }

        await push.SendAsync(connectionId, delivery.ToDelivery(), ct);
        logger.LogInformation("Delivery {DeliveryId} attempt {Attempt} sent over SignalR to connection {ConnectionId}",
            delivery.DeliveryId, delivery.Attempt, connectionId);

        if (!await waiter.WaitAsync(time, ct))
            logger.LogInformation("Delivery {DeliveryId} was not settled before its lease ended", delivery.DeliveryId);
    }
}
