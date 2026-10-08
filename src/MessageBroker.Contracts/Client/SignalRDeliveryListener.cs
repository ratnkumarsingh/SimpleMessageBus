using MessageBroker.Contracts.Models;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace MessageBroker.Contracts.Client;

/// <summary>What the handler decided about a delivery.</summary>
public sealed record DeliveryResult(bool Success, string? ErrorCode = null, string? ErrorMessage = null, bool DeadLetter = false)
{
    public static DeliveryResult Ack { get; } = new(true);

    /// <param name="deadLetter">True for failures that will never succeed, such as invalid content.</param>
    public static DeliveryResult Fail(string errorCode, string? errorMessage = null, bool deadLetter = false) =>
        new(false, errorCode, errorMessage, deadLetter);
}

/// <summary>
/// Receives deliveries over SignalR and settles each one with the handler's result: ACK on success,
/// NACK on failure or exception. Reconnects automatically and joins its subscriptions again, because
/// the broker rebuilds its connection registry from Subscribe calls (spec section 8.6). Delivery is
/// at-least-once: deduplicate by <see cref="DeliveredMessage.MessageId"/>.
/// </summary>
public sealed class SignalRDeliveryListener : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly Func<Delivery, CancellationToken, Task<DeliveryResult>> _handler;
    private readonly HashSet<Guid> _subscriptions = [];
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <param name="brokerBaseAddress">The broker's base address; the hub is at /hubs/deliveries.</param>
    /// <param name="configureConnection">Optional transport settings (e.g. a test handler).</param>
    public SignalRDeliveryListener(
        Uri brokerBaseAddress,
        string apiKey,
        Func<Delivery, CancellationToken, Task<DeliveryResult>> handler,
        Action<HttpConnectionOptions>? configureConnection = null)
    {
        _handler = handler;
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(brokerBaseAddress, DeliveryHub.Path), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(apiKey);
                configureConnection?.Invoke(options);
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();

        // Handle deliveries concurrently; the broker limits how many are outstanding per subscription.
        _connection.On<Delivery>(DeliveryHub.Deliver, delivery =>
        {
            _ = HandleAsync(delivery);
            return Task.CompletedTask;
        });
        _connection.Reconnected += async _ =>
        {
            foreach (var subscriptionId in Subscriptions())
                await _connection.InvokeAsync(DeliveryHub.Subscribe, subscriptionId, _stopping.Token);
        };
    }

    public HubConnectionState State => _connection.State;

    /// <summary>Raised when settling a delivery fails, e.g. because the lease was lost.</summary>
    public event Action<Delivery, Exception>? SettlementFailed;

    public async Task StartAsync(IEnumerable<Guid> subscriptionIds, CancellationToken ct = default)
    {
        await _connection.StartAsync(ct);
        foreach (var subscriptionId in subscriptionIds)
            await SubscribeAsync(subscriptionId, ct);
    }

    /// <exception cref="HubException">The subscription is unknown, not SignalR, or not permitted.</exception>
    public async Task SubscribeAsync(Guid subscriptionId, CancellationToken ct = default)
    {
        await _connection.InvokeAsync(DeliveryHub.Subscribe, subscriptionId, ct);
        lock (_lock)
            _subscriptions.Add(subscriptionId);
    }

    public async Task UnsubscribeAsync(Guid subscriptionId, CancellationToken ct = default)
    {
        lock (_lock)
            _subscriptions.Remove(subscriptionId);
        await _connection.InvokeAsync(DeliveryHub.Unsubscribe, subscriptionId, ct);
    }

    /// <summary>Extends a lease for long-running work; returns the new LockedUntil.</summary>
    public Task<DateTime> RenewAsync(Delivery delivery, CancellationToken ct = default) =>
        _connection.InvokeAsync<DateTime>(DeliveryHub.Renew, delivery.DeliveryId, delivery.LockToken, ct);

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _stopping.CancelAsync();
        await _connection.StopAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _connection.DisposeAsync();
        _stopping.Dispose();
    }

    private Guid[] Subscriptions()
    {
        lock (_lock)
            return [.. _subscriptions];
    }

    private async Task HandleAsync(Delivery delivery)
    {
        DeliveryResult result;
        try
        {
            result = await _handler(delivery, _stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return; // stopping: the lease expires and the delivery is redelivered
        }
        catch (Exception ex)
        {
            result = DeliveryResult.Fail("HandlerException", ex.Message);
        }

        try
        {
            if (result.Success)
                await _connection.InvokeAsync(DeliveryHub.Ack, delivery.DeliveryId, delivery.LockToken, _stopping.Token);
            else
                await _connection.InvokeAsync(DeliveryHub.Nack, delivery.DeliveryId, delivery.LockToken,
                    result.ErrorCode, result.ErrorMessage, result.DeadLetter, _stopping.Token);
        }
        catch (Exception ex) when (!_stopping.IsCancellationRequested)
        {
            SettlementFailed?.Invoke(delivery, ex);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>0, 2, 5, 10 seconds, then every 30 seconds for as long as it takes.</summary>
    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

        public TimeSpan? NextRetryDelay(RetryContext context) =>
            context.PreviousRetryCount < Delays.Length ? Delays[context.PreviousRetryCount] : TimeSpan.FromSeconds(30);
    }
}
