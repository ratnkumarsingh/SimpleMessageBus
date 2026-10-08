using MessageBroker.Api.Auth;
using MessageBroker.Application.Dispatch;
using MessageBroker.Application.Security;
using MessageBroker.Application.Services;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;
using Microsoft.AspNetCore.SignalR;

namespace MessageBroker.Api.Hubs;

/// <summary>
/// SignalR delivery hub (spec sections 8.3 and 9). Clients authenticate with their API key, join
/// subscriptions they hold Receive on, and settle deliveries with Ack, Nack and Renew. Errors reach
/// the client as HubException messages carrying the error kind; SignalR prefixes them, so a client
/// sees "... HubException: LeaseLost: The lease was lost ...".
/// </summary>
public sealed class DeliveriesHub(
    DeliveryService deliveries,
    IConnectionRegistry connections,
    IDispatcherSignal signal,
    ILogger<DeliveriesHub> logger) : Hub
{
    private Caller Caller => Context.User!.ToCaller();

    public override Task OnConnectedAsync()
    {
        logger.LogInformation("SignalR client {ConnectionId} connected for {AppId}", Context.ConnectionId, Caller.AppId);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.RemoveConnection(Context.ConnectionId);
        logger.LogInformation("SignalR client {ConnectionId} disconnected", Context.ConnectionId);
        return Task.CompletedTask;
    }

    [HubMethodName(DeliveryHub.Subscribe)]
    public async Task Subscribe(Guid subscriptionId)
    {
        await RunAsync(() => deliveries.AuthorizeSignalRSubscribeAsync(Caller, subscriptionId, Context.ConnectionAborted));
        connections.Add(subscriptionId, Context.ConnectionId);
        logger.LogInformation("SignalR client {ConnectionId} joined subscription {SubscriptionId}", Context.ConnectionId, subscriptionId);
        signal.Notify(); // deliver any backlog now
    }

    /// <summary>Stops new deliveries to this connection; deliveries it holds finish or expire normally.</summary>
    [HubMethodName(DeliveryHub.Unsubscribe)]
    public Task Unsubscribe(Guid subscriptionId)
    {
        if (connections.Remove(subscriptionId, Context.ConnectionId))
            logger.LogInformation("SignalR client {ConnectionId} left subscription {SubscriptionId}", Context.ConnectionId, subscriptionId);
        return Task.CompletedTask;
    }

    [HubMethodName(DeliveryHub.Ack)]
    public Task Ack(long deliveryId, Guid lockToken) =>
        RunAsync(() => deliveries.AckAsync(Caller, deliveryId, new AckRequest(lockToken), Context.ConnectionAborted));

    [HubMethodName(DeliveryHub.Nack)]
    public Task Nack(long deliveryId, Guid lockToken, string? errorCode, string? errorMessage, bool deadLetter) =>
        RunAsync(() => deliveries.NackAsync(Caller, deliveryId,
            new NackRequest(lockToken, errorCode, errorMessage, DeadLetter: deadLetter), Context.ConnectionAborted));

    /// <summary>Extends the lease; returns the new LockedUntil.</summary>
    [HubMethodName(DeliveryHub.Renew)]
    public async Task<DateTime> Renew(long deliveryId, Guid lockToken)
    {
        var lockedUntil = DateTime.MinValue;
        await RunAsync(async () => lockedUntil = await deliveries.RenewAsync(Caller, deliveryId, new RenewRequest(lockToken), Context.ConnectionAborted));
        return lockedUntil;
    }

    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (BrokerException ex)
        {
            throw new HubException($"{ex.Kind}: {ex.Message}");
        }
    }
}

/// <summary>[Fix 10] The Worker's SignalR channel sends through this, so it never references the hub.</summary>
public sealed class HubDeliveryPushChannel(IHubContext<DeliveriesHub> hub) : IDeliveryPushChannel
{
    public Task SendAsync(string connectionId, Delivery delivery, CancellationToken ct) =>
        hub.Clients.Client(connectionId).SendAsync(DeliveryHub.Deliver, delivery, ct);
}
