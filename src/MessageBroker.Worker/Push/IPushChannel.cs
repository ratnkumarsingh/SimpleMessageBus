using MessageBroker.Application.Persistence;

namespace MessageBroker.Worker.Push;

/// <summary>A push delivery mode (Webhook, SignalR) driven by the <see cref="LeaseLoop"/>.</summary>
public interface IPushChannel
{
    /// <summary>The subscription DeliveryMode this channel serves.</summary>
    string Mode { get; }

    /// <summary>How many more deliveries may start now for the subscription (circuit state, connections).</summary>
    int Allowance(PushSubscriptionRecord subscription);

    /// <summary>
    /// Sends one leased delivery and settles it. Must not throw for delivery failures; it records them
    /// as NACKs. Work that limits <see cref="Allowance"/> must happen before the first await.
    /// </summary>
    Task DeliverAsync(PushSubscriptionRecord subscription, LeasedDeliveryRecord delivery, CancellationToken ct);
}
