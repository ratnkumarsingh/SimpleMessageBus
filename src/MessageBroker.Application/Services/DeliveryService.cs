using MessageBroker.Application.Dispatch;
using MessageBroker.Application.Persistence;
using MessageBroker.Application.Security;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;
using Microsoft.Extensions.Logging;

namespace MessageBroker.Application.Services;

/// <summary>
/// Pull delivery and the DLQ (spec sections 8 and 10). Permission checks run inside the procedures:
/// the caller needs Receive on the subscription (or Manage, or Admin).
/// </summary>
public sealed class DeliveryService(
    IDeliveryRepository deliveries,
    ISubscriptionRepository subscriptions,
    IApplicationRepository applications,
    LongPoller poller,
    IDispatcherSignal signal,
    IDeliverySettlements settlements,
    ILogger<DeliveryService> logger)
{
    public const int DefaultDeadLetterPageSize = 50;
    public const int MaxDeadLetterPageSize = 200;

    /// <summary>Leases up to maxMessages, long-polling up to waitSeconds when none are available.</summary>
    public async Task<IReadOnlyList<Delivery>> ReceiveAsync(
        Caller caller, Guid subscriptionId, ReceiveRequest? request, CancellationToken ct)
    {
        var (maxMessages, wait) = ReceiveRules.Clamp(request);
        var leased = await poller.PollAsync(
            token => deliveries.LeaseAsync(subscriptionId, maxMessages, nameof(DeliveryChannel.Pull), caller.AppId, token), wait, ct);

        foreach (var d in leased)
        {
            using var scope = logger.BeginScope(new Dictionary<string, object>
            {
                ["MessageId"] = d.MessageId,
                ["CorrelationId"] = d.CorrelationId,
                ["DeliveryId"] = d.DeliveryId,
                ["SubscriptionId"] = subscriptionId,
            });
            logger.LogInformation("Delivery {DeliveryId} of message {MessageId} leased to {AppId} on subscription {SubscriptionId}, attempt {Attempt}",
                d.DeliveryId, d.MessageId, caller.AppId, subscriptionId, d.Attempt);
        }
        return leased.Select(d => d.ToDelivery()).ToList();
    }

    /// <exception cref="BrokerException">LeaseLost (410) when the token is stale or the lease expired.</exception>
    public async Task AckAsync(Caller caller, long deliveryId, AckRequest request, CancellationToken ct)
    {
        await deliveries.AckAsync(deliveryId, request.LockToken, caller.AppId, ct: ct);
        settlements.Settled(deliveryId);
        logger.LogInformation("Delivery {DeliveryId} acknowledged by {AppId}", deliveryId, caller.AppId);
    }

    public async Task NackAsync(Caller caller, long deliveryId, NackRequest request, CancellationToken ct)
    {
        var result = await deliveries.NackAsync(deliveryId, request.LockToken, caller.AppId,
            new FailureDetails(request.ErrorCode, request.ErrorMessage, request.ErrorDetail, DeadLetter: request.DeadLetter), ct);
        settlements.Settled(deliveryId);

        if (result.DeadLettered)
            logger.LogWarning("Delivery {DeliveryId} dead-lettered after NACK from {AppId}: {ErrorCode}", deliveryId, caller.AppId, request.ErrorCode);
        else
            logger.LogInformation("Delivery {DeliveryId} NACKed by {AppId}: {ErrorCode}; retry scheduled at {NextAvailableAt:o}",
                deliveryId, caller.AppId, request.ErrorCode, result.NextAvailableAt);
    }

    public async Task<DateTime> RenewAsync(Caller caller, long deliveryId, RenewRequest request, CancellationToken ct)
    {
        var lockedUntil = await deliveries.RenewAsync(deliveryId, request.LockToken, caller.AppId, ct);
        settlements.Renewed(deliveryId, lockedUntil);
        logger.LogInformation("Lease on delivery {DeliveryId} renewed by {AppId} until {LockedUntil:o}", deliveryId, caller.AppId, lockedUntil);
        return lockedUntil;
    }

    /// <summary>
    /// Checks that the caller may join a subscription over SignalR: it must exist, use SignalR delivery,
    /// and the caller needs Receive on it (or Manage, or Admin).
    /// </summary>
    public async Task AuthorizeSignalRSubscribeAsync(Caller caller, Guid subscriptionId, CancellationToken ct)
    {
        var subscription = await subscriptions.GetAsync(subscriptionId, ct) ?? throw BrokerException.NotFound("Subscription");
        if (subscription.DeliveryMode != nameof(DeliveryMode.SignalR))
            throw new BrokerException(BrokerErrorKind.Conflict, "The subscription does not use SignalR delivery.");
        if (!await applications.HasPermissionAsync(caller.AppId, nameof(ResourceType.Subscription), subscriptionId, nameof(Permission.Receive), ct))
        {
            logger.LogWarning("Permission denied: {AppId} may not receive from subscription {SubscriptionId}", caller.AppId, subscriptionId);
            throw BrokerException.Forbidden("The application is not permitted to receive from this subscription.");
        }
    }

    /// <summary>Admins and applications with Receive on the subscription (its owner) may list.</summary>
    public async Task<DeadLetterPage> ListDeadLettersAsync(
        Caller caller, Guid subscriptionId, int? pageSize, long? before, bool includeRequeued, CancellationToken ct)
    {
        var size = Math.Clamp(pageSize ?? DefaultDeadLetterPageSize, 1, MaxDeadLetterPageSize);
        // One extra row tells whether another page exists.
        var rows = await deliveries.ListDeadLettersAsync(subscriptionId, caller.AppId, size + 1, before, includeRequeued, ct);
        var page = rows.Take(size).Select(r => r.ToResponse()).ToList();
        return new DeadLetterPage(page, rows.Count > size ? page[^1].DeadLetterId : null);
    }

    /// <summary>Returns a dead-lettered delivery to Pending with a fresh attempt budget (Admin).</summary>
    public async Task RequeueAsync(Caller caller, long deliveryId, CancellationToken ct)
    {
        caller.RequireAdmin();
        await deliveries.RequeueAsync(deliveryId, caller.AppId, ct);
        logger.LogInformation("Delivery {DeliveryId} requeued from the DLQ by {AppId}", deliveryId, caller.AppId);
        signal.Notify();
    }
}
