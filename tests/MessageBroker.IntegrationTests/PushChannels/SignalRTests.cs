using System.Collections.Concurrent;
using MessageBroker.Application.Persistence;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;
using MessageBroker.Worker.Maintenance;
using MessageBroker.Worker.Push;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace MessageBroker.IntegrationTests.PushChannels;

public sealed class SignalRTests(SqlServerFixture sql) : ApiTest(sql)
{
    private readonly List<IAsyncDisposable> _clients = [];

    public override async Task DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        await base.DisposeAsync();
    }

    // In-process SignalR runs over long polling through the test server.
    private void UseTestServer(HttpConnectionOptions options)
    {
        options.Transports = HttpTransportType.LongPolling;
        options.HttpMessageHandlerFactory = _ => Api.Server.CreateHandler();
    }

    /// <summary>A raw hub connection that records deliveries; it settles only when told to.</summary>
    private async Task<(HubConnection Connection, ConcurrentQueue<Delivery> Received)> ConnectAsync(string? apiKey, params Guid[] subscriptions)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Api.Server.BaseAddress, DeliveryHub.Path), o =>
            {
                UseTestServer(o);
                if (apiKey is not null)
                    o.AccessTokenProvider = () => Task.FromResult<string?>(apiKey);
            })
            .Build();
        _clients.Add(connection);
        var received = new ConcurrentQueue<Delivery>();
        connection.On<Delivery>(DeliveryHub.Deliver, d => received.Enqueue(d));
        await connection.StartAsync();
        foreach (var id in subscriptions)
            await connection.InvokeAsync(DeliveryHub.Subscribe, id);
        return (connection, received);
    }

    private async Task<SignalRDeliveryListener> ListenAsync(string apiKey, Func<Delivery, DeliveryResult> handle, params Guid[] subscriptions)
    {
        var listener = new SignalRDeliveryListener(Api.Server.BaseAddress, apiKey, (d, _) => Task.FromResult(handle(d)), UseTestServer);
        _clients.Add(listener);
        await listener.StartAsync(subscriptions);
        return listener;
    }

    private Task<int> LeasePassAsync(bool wait = true) =>
        Api.Services.GetRequiredService<LeaseLoop>().RunOnceAsync(CancellationToken.None, wait).WaitAsync(TimeSpan.FromSeconds(30));

    private async Task<(TopicRecord Topic, Guid Publisher, Guid OwnerId, string OwnerKey, SubscriptionRecord Subscription)> ArrangeAsync(
        int lockSeconds = 60, int maxConcurrent = 8, int maxAttempts = 4)
    {
        var (topic, publisher, _) = await ArrangeTopicAsync();
        var (ownerId, ownerKey, _) = await CreateAppClientAsync();
        var subscription = await CreateSubscriptionAsync(topic.TopicId, ownerId, mode: "SignalR",
            lockSeconds: lockSeconds, maxConcurrent: maxConcurrent, maxAttempts: maxAttempts);
        return (topic, publisher, ownerId, ownerKey, subscription);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition was not met in time.");
            await Task.Delay(20);
        }
    }

    [Fact(DisplayName = "P08 SignalR: Subscribe, Deliver, Ack completes the delivery")]
    public async Task P08_SubscribeDeliverAck()
    {
        var s = await ArrangeAsync();
        var handled = new ConcurrentQueue<Delivery>();
        await ListenAsync(s.OwnerKey, d => { handled.Enqueue(d); return DeliveryResult.Ack; }, s.Subscription.SubscriptionId);
        var published = await PublishAsync(s.Topic.Name, s.Publisher, correlationId: "PAY-8");

        Assert.Equal(1, await LeasePassAsync());

        var delivery = Assert.Single(handled);
        Assert.Equal((published.MessageId, "PAY-8", 1), (delivery.Message.MessageId, delivery.Message.CorrelationId, delivery.Attempt));
        Assert.Equal("PAY-1", delivery.Message.Payload.GetProperty("paymentId").GetString());
        Assert.Equal(DeliveryStatus.Completed, (await GetDeliveryAsync(delivery.DeliveryId)).State);
        var attempt = Assert.Single(await GetAttemptsAsync(delivery.DeliveryId));
        Assert.Equal(("SignalR", "Acked"), (attempt.Channel, attempt.Outcome));
    }

    [Fact(DisplayName = "P09 SignalR: a client that disconnects before ACK gets the delivery again after the lease expires")]
    public async Task P09_RedeliveredAfterDisconnect()
    {
        var s = await ArrangeAsync(lockSeconds: 2);
        var (first, firstReceived) = await ConnectAsync(s.OwnerKey, s.Subscription.SubscriptionId);
        await PublishAsync(s.Topic.Name, s.Publisher);

        Assert.Equal(1, await LeasePassAsync(wait: false));
        await WaitUntilAsync(() => !firstReceived.IsEmpty);
        var lost = Assert.Single(firstReceived);
        await first.StopAsync(); // crashes before ACK

        await LapseLeaseAsync(lost.DeliveryId);
        await Api.Services.GetRequiredService<MaintenanceLoop>().RunOnceAsync(CancellationToken.None);
        await MakeDueAsync(lost.DeliveryId);

        var handled = new ConcurrentQueue<Delivery>();
        await ListenAsync(s.OwnerKey, d => { handled.Enqueue(d); return DeliveryResult.Ack; }, s.Subscription.SubscriptionId);
        Assert.Equal(1, await LeasePassAsync());

        var again = Assert.Single(handled);
        Assert.Equal((lost.DeliveryId, 2), (again.DeliveryId, again.Attempt));
        Assert.Equal(DeliveryStatus.Completed, (await GetDeliveryAsync(lost.DeliveryId)).State);
        Assert.Equal(["LeaseExpired", "Acked"], (await GetAttemptsAsync(lost.DeliveryId)).Select(a => a.Outcome));
    }

    [Fact(DisplayName = "P10 SignalR: two clients share deliveries round-robin; each delivery goes to exactly one connection")]
    public async Task P10_RoundRobin()
    {
        var s = await ArrangeAsync();
        var a = await ConnectAsync(s.OwnerKey, s.Subscription.SubscriptionId);
        var b = await ConnectAsync(s.OwnerKey, s.Subscription.SubscriptionId);
        foreach (var (connection, _) in new[] { a, b })
            connection.On<Delivery>(DeliveryHub.Deliver, d => { _ = connection.InvokeAsync(DeliveryHub.Ack, d.DeliveryId, d.LockToken); });
        for (var i = 0; i < 6; i++)
            await PublishAsync(s.Topic.Name, s.Publisher);

        Assert.Equal(6, await LeasePassAsync());

        Assert.Equal(3, a.Received.Count);
        Assert.Equal(3, b.Received.Count);
        var all = a.Received.Concat(b.Received).Select(d => d.DeliveryId).ToList();
        Assert.Equal(6, all.Distinct().Count());
        Assert.Equal(6, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries WHERE Status = 2"));
        Assert.Equal(6, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.DeliveryAttempts"));
    }

    [Fact(DisplayName = "P11 SignalR: with no connected client nothing is leased")]
    public async Task P11_NoClientNoLease()
    {
        var s = await ArrangeAsync();
        var published = await PublishAsync(s.Topic.Name, s.Publisher);
        var deliveryId = (await GetDeliveriesForMessageAsync(published.MessageId)).Single().DeliveryId;

        Assert.Equal(0, await LeasePassAsync());

        // Joined and left again.
        var (connection, _) = await ConnectAsync(s.OwnerKey, s.Subscription.SubscriptionId);
        await connection.InvokeAsync(DeliveryHub.Unsubscribe, s.Subscription.SubscriptionId);
        Assert.Equal(0, await LeasePassAsync());

        // Joined and disconnected.
        var (other, _) = await ConnectAsync(s.OwnerKey, s.Subscription.SubscriptionId);
        await other.StopAsync();
        var registry = Api.Services.GetRequiredService<MessageBroker.Application.Dispatch.IConnectionRegistry>();
        await WaitUntilAsync(() => registry.Count(s.Subscription.SubscriptionId) == 0);
        Assert.Equal(0, await LeasePassAsync());

        var delivery = await GetDeliveryAsync(deliveryId);
        Assert.Equal((DeliveryStatus.Pending, 0), (delivery.State, delivery.AttemptCount));
    }

    [Fact(DisplayName = "P12 SignalR: Nack with deadLetter is RejectedBySubscriber; Subscribe needs Receive; hub errors carry the kind")]
    public async Task P12_NackAndPermissions()
    {
        var s = await ArrangeAsync();
        var listener = await ListenAsync(s.OwnerKey, _ => DeliveryResult.Fail("InvalidPayload", "missing amount", deadLetter: true),
            s.Subscription.SubscriptionId);
        var published = await PublishAsync(s.Topic.Name, s.Publisher);
        Assert.Equal(1, await LeasePassAsync());

        var deliveryId = (await GetDeliveriesForMessageAsync(published.MessageId)).Single().DeliveryId;
        var deadLetter = Assert.Single(await GetDeadLettersAsync(deliveryId));
        Assert.Equal(("RejectedBySubscriber", "missing amount"), (deadLetter.Reason, deadLetter.LastError));
        Assert.Equal("InvalidPayload", Assert.Single(await GetAttemptsAsync(deliveryId)).ErrorCode);

        // Another application without Receive cannot join.
        var (_, strangerKey, _) = await CreateAppClientAsync();
        var (stranger, _) = await ConnectAsync(strangerKey);
        var denied = await Assert.ThrowsAsync<HubException>(() => stranger.InvokeAsync(DeliveryHub.Subscribe, s.Subscription.SubscriptionId));
        Assert.Contains("HubException: Forbidden:", denied.Message);

        // Unknown subscription; a subscription that is not SignalR.
        Assert.Contains("HubException: NotFound:", (await Assert.ThrowsAsync<HubException>(() => stranger.InvokeAsync(DeliveryHub.Subscribe, Guid.NewGuid()))).Message);
        var pull = await CreateSubscriptionAsync(s.Topic.TopicId, s.OwnerId, mode: "Pull");
        var (owner, _) = await ConnectAsync(s.OwnerKey);
        Assert.Contains("HubException: Conflict:", (await Assert.ThrowsAsync<HubException>(() => owner.InvokeAsync(DeliveryHub.Subscribe, pull.SubscriptionId))).Message);

        // Settling with a stale token reports the lost lease; a stranger cannot settle at all.
        Assert.Contains("HubException: LeaseLost:", (await Assert.ThrowsAsync<HubException>(() =>
            owner.InvokeAsync(DeliveryHub.Ack, deliveryId, Guid.NewGuid()))).Message);
        Assert.Contains("HubException: Forbidden:", (await Assert.ThrowsAsync<HubException>(() =>
            stranger.InvokeAsync(DeliveryHub.Ack, deliveryId, Guid.NewGuid()))).Message);

        // Admins may join any SignalR subscription.
        var (admin, _) = await ConnectAsync(ApiFactory.AdminKey);
        await admin.InvokeAsync(DeliveryHub.Subscribe, s.Subscription.SubscriptionId);

        // No key or a bad key: the connection is refused.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => ConnectAsync(apiKey: null));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => ConnectAsync("mbk_aaaaaaaaaaaa_" + new string('x', 43)));
        Assert.Equal(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected, listener.State);
    }

    [Fact(DisplayName = "P14 SignalR: MaxConcurrentDeliveries bounds unsettled deliveries; settling frees a slot")]
    public async Task P14_ConcurrencyLimit()
    {
        var s = await ArrangeAsync(lockSeconds: 10, maxConcurrent: 2);
        var (connection, received) = await ConnectAsync(s.OwnerKey, s.Subscription.SubscriptionId);
        for (var i = 0; i < 5; i++)
            await PublishAsync(s.Topic.Name, s.Publisher);
        Task<int> LeasedAsync() => ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries WHERE Status = 1");
        var loop = Api.Services.GetRequiredService<LeaseLoop>();

        Assert.Equal(2, await LeasePassAsync(wait: false));
        await WaitUntilAsync(() => received.Count == 2);
        Assert.Equal(0, await LeasePassAsync(wait: false));
        Assert.Equal(2, await LeasedAsync());

        var first = received.First();
        await connection.InvokeAsync(DeliveryHub.Ack, first.DeliveryId, first.LockToken);
        await WaitUntilAsync(() => loop.InFlight(s.Subscription.SubscriptionId) == 1);

        Assert.Equal(1, await LeasePassAsync(wait: false));
        await WaitUntilAsync(() => received.Count == 3);
        Assert.Equal(2, await LeasedAsync());

        // A renewal keeps the delivery in flight past its original lease.
        var held = received.Last();
        var renewed = await connection.InvokeAsync<DateTime>(DeliveryHub.Renew, held.DeliveryId, held.LockToken);
        Assert.True(renewed > held.LockedUntil);
    }
}
