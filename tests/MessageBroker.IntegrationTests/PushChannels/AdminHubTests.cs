using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using MessageBroker.Application.Dispatch;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace MessageBroker.IntegrationTests.PushChannels;

/// <summary>S06–S09: the admin hub's live activity feed.</summary>
public sealed class AdminHubTests(SqlServerFixture sql) : WebhookTest(sql)
{
    private readonly List<HubConnection> _connections = [];

    public override async Task DisposeAsync()
    {
        foreach (var connection in _connections)
            await connection.DisposeAsync();
        await base.DisposeAsync();
    }

    private sealed class Watcher
    {
        public ConcurrentQueue<MessageActivity> Activity { get; } = new();
        public ConcurrentQueue<DateTime> OverviewPings { get; } = new();
    }

    private async Task<Watcher> WatchAsync(string apiKey)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Api.Server.BaseAddress, AdminHub.Path), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => Api.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(apiKey);
            })
            .Build();
        _connections.Add(connection);
        var watcher = new Watcher();
        connection.On<MessageActivity[]>(AdminHub.Activity, batch =>
        {
            foreach (var a in batch)
                watcher.Activity.Enqueue(a);
        });
        connection.On(AdminHub.OverviewChanged, () => watcher.OverviewPings.Enqueue(DateTime.UtcNow));
        await connection.StartAsync();
        return watcher;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting for admin hub activity.");
            await Task.Delay(50);
        }
    }

    [Fact(DisplayName = "S06 The admin hub turns away non-admin and missing keys at negotiate")]
    public async Task S06_NonAdminRejected()
    {
        var (_, key, _) = await CreateAppClientAsync();
        var forbidden = await Assert.ThrowsAsync<HttpRequestException>(() => WatchAsync(key));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var unauthorized = await Assert.ThrowsAsync<HttpRequestException>(() => WatchAsync("mbk_bogus_key"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact(DisplayName = "S07 An admin receives Published activity and an overview ping after a publish")]
    public async Task S07_Published()
    {
        var (topic, _, subscriber) = await ArrangeTopicAsync();
        await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var (_, key, _) = await CreateAppClientAsync(isAdmin: true);
        var watcher = await WatchAsync(key);

        var (publisherId, _, client) = await CreateAppClientAsync();
        await GrantViaApiAsync(publisherId, "Topic", topic.TopicId, "Publish");
        var published = await ReadAsync<PublishResponse>(await PublishViaApiAsync(client, topic.Name), HttpStatusCode.Created);

        await WaitUntilAsync(() => watcher.Activity.Any(a => a.MessageId == published.MessageId) && !watcher.OverviewPings.IsEmpty);
        var activity = watcher.Activity.Single(a => a.MessageId == published.MessageId);
        Assert.Equal((ActivityKinds.Published, null), (activity.Kind, activity.DeliveryId));
    }

    [Fact(DisplayName = "S08 An admin receives DeliveryChanged after a pull lease and ack, and after a webhook ack")]
    public async Task S08_DeliveryChanged()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var (ownerId, _, owner) = await CreateAppClientAsync();
        var pull = await CreateSubscriptionAsync(topic.TopicId, ownerId);
        var hook = await CreateWebhookSubscriptionAsync(topic.TopicId, subscriber);
        Respond(200);
        var (_, key, _) = await CreateAppClientAsync(isAdmin: true);
        var watcher = await WatchAsync(key);

        var messageId = (await PublishAsync(topic.Name, publisher)).MessageId;

        // Pull: the lease and the ack each report the delivery (through the REST endpoints).
        var leased = await ReadAsync<List<Delivery>>(await owner.PostAsync(
            $"/api/v1/subscriptions/{pull.SubscriptionId}/receive", JsonContent.Create(new ReceiveRequest(1))));
        var delivery = Assert.Single(leased);
        await ExpectAsync(owner.PostAsync($"/api/v1/deliveries/{delivery.DeliveryId}/ack",
            JsonContent.Create(new AckRequest(delivery.LockToken))), HttpStatusCode.NoContent);
        await WaitUntilAsync(() => watcher.Activity.Count(a => a.DeliveryId == delivery.DeliveryId) >= 2);
        Assert.All(watcher.Activity.Where(a => a.DeliveryId == delivery.DeliveryId),
            a => Assert.Equal((ActivityKinds.DeliveryChanged, messageId), (a.Kind, a.MessageId)));

        // Webhook: one lease pass leases, calls the endpoint and ACKs on its 200.
        await LeasePassAsync();
        var hookDeliveryId = (await GetDeliveriesForMessageAsync(messageId)).Single(d => d.SubscriptionId == hook.SubscriptionId).DeliveryId;
        await WaitUntilAsync(() => watcher.Activity.Count(a => a.DeliveryId == hookDeliveryId) >= 2);
        Assert.Equal(DeliveryStatus.Completed, (await GetDeliveryAsync(hookDeliveryId)).State);
    }

    [Fact(DisplayName = "S09 OverviewChanged is sent at most once a second under a burst of publishes")]
    public async Task S09_OverviewThrottle()
    {
        var (topic, _, subscriber) = await ArrangeTopicAsync();
        await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var (_, key, _) = await CreateAppClientAsync(isAdmin: true);
        var watcher = await WatchAsync(key);
        var (publisherId, _, client) = await CreateAppClientAsync();
        await GrantViaApiAsync(publisherId, "Topic", topic.TopicId, "Publish");

        var clock = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => PublishViaApiAsync(client, topic.Name)));
        await WaitUntilAsync(() => watcher.Activity.Count(a => a.Kind == ActivityKinds.Published) >= 100);
        await Task.Delay(BrokerActivityFeed.OverviewInterval * 2); // let the last ping out
        var elapsed = clock.Elapsed;

        var pings = watcher.OverviewPings.ToArray();
        Assert.NotEmpty(pings);
        Assert.True(pings.Length <= (int)Math.Ceiling(elapsed.TotalSeconds) + 1, $"{pings.Length} pings in {elapsed}");
        // Consecutive pings are about a second apart (allowing for long-polling delivery jitter).
        Assert.All(pings.Zip(pings.Skip(1)), p => Assert.True(p.Second - p.First > TimeSpan.FromMilliseconds(500), $"{p.Second - p.First}"));
        Assert.Equal(100, watcher.Activity.Where(a => a.Kind == ActivityKinds.Published).Select(a => a.MessageId).Distinct().Count());
    }
}
