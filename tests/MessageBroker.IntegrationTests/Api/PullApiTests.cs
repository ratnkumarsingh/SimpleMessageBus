using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;

namespace MessageBroker.IntegrationTests.Api;

public sealed class PullApiTests(SqlServerFixture sql) : ApiTest(sql)
{
    private sealed record PullSetup(TopicResponse Topic, Guid PublisherId, HttpClient Publisher, Guid SubscriberId, HttpClient Subscriber, Guid SubscriptionId);

    private async Task<PullSetup> ArrangeAsync(int maxAttempts = 4, string mode = "Pull")
    {
        var topic = await CreateTopicViaApiAsync();
        var (publisherId, _, publisher) = await CreateAppClientAsync();
        await GrantViaApiAsync(publisherId, "Topic", topic.TopicId, "Publish");
        var (subscriberId, _, subscriber) = await CreateAppClientAsync();
        var subscription = await CreateSubscriptionAsync(topic.TopicId, subscriberId, mode: mode, maxAttempts: maxAttempts);
        return new PullSetup(topic, publisherId, publisher, subscriberId, subscriber, subscription.SubscriptionId);
    }

    private static async Task<List<Delivery>> ReceiveAsync(HttpClient client, Guid subscriptionId, int? max = null, int? wait = null) =>
        await ReadAsync<List<Delivery>>(await client.PostAsJsonAsync(
            $"/api/v1/subscriptions/{subscriptionId}/receive", new ReceiveRequest(max, wait)));

    [Fact(DisplayName = "A04 Long poll returns empty after waitSeconds, and early when a message is published mid-wait")]
    public async Task A04_LongPoll()
    {
        var s = await ArrangeAsync();

        var timer = Stopwatch.StartNew();
        Assert.Empty(await ReceiveAsync(s.Subscriber, s.SubscriptionId, wait: 2));
        Assert.InRange(timer.Elapsed.TotalSeconds, 1.8, 6);

        // waitSeconds 0 (the default) returns at once.
        timer.Restart();
        Assert.Empty(await ReceiveAsync(s.Subscriber, s.SubscriptionId));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1.5), $"took {timer.Elapsed}");

        // A publish wakes a waiting receiver well before its 20-second wait ends.
        timer.Restart();
        var waiting = ReceiveAsync(s.Subscriber, s.SubscriptionId, wait: 20);
        await Task.Delay(300);
        var published = await ReadAsync<PublishResponse>(await PublishViaApiAsync(s.Publisher, s.Topic.Name), HttpStatusCode.Created);
        var received = Assert.Single(await waiting);
        Assert.Equal(published.MessageId, received.Message.MessageId);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
    }

    [Fact(DisplayName = "A05 Pull: receive, ack 204, ack again 410; nack 204 schedules a retry; renew 204 extends the lease")]
    public async Task A05_PullFlow()
    {
        var s = await ArrangeAsync();
        var first = await ReadAsync<PublishResponse>(await PublishViaApiAsync(s.Publisher, s.Topic.Name), HttpStatusCode.Created);

        var delivery = Assert.Single(await ReceiveAsync(s.Subscriber, s.SubscriptionId, max: 10));
        Assert.Equal((first.MessageId, 1, "PaymentProcessed.v1", "PAY-12345"),
            (delivery.Message.MessageId, delivery.Attempt, delivery.Message.MessageType, delivery.Message.CorrelationId));
        Assert.Equal("PAY-12345", delivery.Message.Payload.GetProperty("paymentId").GetString());
        Assert.Equal("in-01", delivery.Message.Properties!.Value.GetProperty("tenant").GetString());
        Assert.InRange((delivery.LockedUntil - await DbNowAsync()).TotalSeconds, 55, 61);

        // Leased deliveries are not handed out twice.
        Assert.Empty(await ReceiveAsync(s.Subscriber, s.SubscriptionId));

        await ExpectAsync(s.Subscriber.PostAsJsonAsync($"/api/v1/deliveries/{delivery.DeliveryId}/ack", new AckRequest(delivery.LockToken)),
            HttpStatusCode.NoContent);
        Assert.Equal(DeliveryStatus.Completed, (await GetDeliveryAsync(delivery.DeliveryId)).State);

        // A03 410: settling again with the same token.
        var gone = await ProblemAsync(await s.Subscriber.PostAsJsonAsync($"/api/v1/deliveries/{delivery.DeliveryId}/ack", new AckRequest(delivery.LockToken)),
            HttpStatusCode.Gone, ProblemTypes.LeaseLost);
        Assert.Contains("lease", gone.GetProperty("detail").GetString());

        // NACK schedules a retry with backoff.
        await PublishViaApiAsync(s.Publisher, s.Topic.Name);
        var second = Assert.Single(await ReceiveAsync(s.Subscriber, s.SubscriptionId));
        await ExpectAsync(s.Subscriber.PostAsJsonAsync($"/api/v1/deliveries/{second.DeliveryId}/nack",
            new NackRequest(second.LockToken, "DownstreamTimeout", "Ledger did not answer")), HttpStatusCode.NoContent);
        var nacked = await GetDeliveryAsync(second.DeliveryId);
        Assert.Equal(DeliveryStatus.Pending, nacked.State);
        Assert.True(nacked.AvailableAt > await DbNowAsync());
        var attempt = Assert.Single(await GetAttemptsAsync(second.DeliveryId));
        Assert.Equal(("Nacked", "DownstreamTimeout", "Ledger did not answer"), (attempt.Outcome, attempt.ErrorCode, attempt.ErrorMessage));

        // The retry comes back as attempt 2 once due.
        Assert.Empty(await ReceiveAsync(s.Subscriber, s.SubscriptionId));
        await MakeDueAsync(second.DeliveryId);
        var retry = Assert.Single(await ReceiveAsync(s.Subscriber, s.SubscriptionId));
        Assert.Equal((second.DeliveryId, 2), (retry.DeliveryId, retry.Attempt));

        // Renew extends the lease; a stale token is 410.
        await BackdateAsync("Deliveries", "DeliveryId", retry.DeliveryId, "LockedUntil", 50);
        await ExpectAsync(s.Subscriber.PostAsJsonAsync($"/api/v1/deliveries/{retry.DeliveryId}/renew", new RenewRequest(retry.LockToken)),
            HttpStatusCode.NoContent);
        Assert.InRange(((await GetDeliveryAsync(retry.DeliveryId)).LockedUntil!.Value - await DbNowAsync()).TotalSeconds, 55, 61);
        await ProblemAsync(await s.Subscriber.PostAsJsonAsync($"/api/v1/deliveries/{retry.DeliveryId}/renew", new RenewRequest(Guid.NewGuid())),
            HttpStatusCode.Gone, ProblemTypes.LeaseLost);

        // NACK with deadLetter goes straight to the DLQ.
        await ExpectAsync(s.Subscriber.PostAsJsonAsync($"/api/v1/deliveries/{retry.DeliveryId}/nack",
            new NackRequest(retry.LockToken, "PoisonMessage", "Cannot parse", DeadLetter: true)), HttpStatusCode.NoContent);
        Assert.Equal("RejectedBySubscriber", Assert.Single(await GetDeadLettersAsync(retry.DeliveryId)).Reason);

        await ProblemAsync(await s.Subscriber.PostAsJsonAsync("/api/v1/deliveries/999999999/ack", new AckRequest(Guid.NewGuid())),
            HttpStatusCode.NotFound, ProblemTypes.NotFound);
    }

    [Fact(DisplayName = "A05b maxMessages and waitSeconds are clamped; messages come oldest first")]
    public async Task A05b_Clamping()
    {
        var s = await ArrangeAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 35; i++)
            ids.Add((await PublishAsync(s.Topic.Name, s.PublisherId)).MessageId);

        var batch = await ReceiveAsync(s.Subscriber, s.SubscriptionId, max: 500, wait: -5);
        Assert.Equal(32, batch.Count);
        Assert.Equal(ids.Take(32), batch.Select(d => d.Message.MessageId));

        Assert.Single(await ReceiveAsync(s.Subscriber, s.SubscriptionId, max: 0));
        Assert.Equal(2, (await ReceiveAsync(s.Subscriber, s.SubscriptionId, max: 10)).Count);
    }

    [Fact(DisplayName = "A02d Receive and settle on another application's subscription is 403; push subscriptions cannot be pulled")]
    public async Task A02d_ReceiveForbidden()
    {
        var s = await ArrangeAsync();
        var (_, _, stranger) = await CreateAppClientAsync();
        await PublishViaApiAsync(s.Publisher, s.Topic.Name);

        await ProblemAsync(await stranger.PostAsJsonAsync($"/api/v1/subscriptions/{s.SubscriptionId}/receive", new ReceiveRequest()),
            HttpStatusCode.Forbidden, ProblemTypes.Forbidden);

        var delivery = Assert.Single(await ReceiveAsync(s.Subscriber, s.SubscriptionId));
        foreach (var action in new[] { "ack", "nack", "renew" })
            await ProblemAsync(await stranger.PostAsJsonAsync($"/api/v1/deliveries/{delivery.DeliveryId}/{action}", new AckRequest(delivery.LockToken)),
                HttpStatusCode.Forbidden, ProblemTypes.Forbidden);
        Assert.Equal(DeliveryStatus.Leased, (await GetDeliveryAsync(delivery.DeliveryId)).State);

        // The publisher holds Publish on the topic, not Receive on the subscription.
        await ProblemAsync(await s.Publisher.PostAsJsonAsync($"/api/v1/subscriptions/{s.SubscriptionId}/receive", new ReceiveRequest()),
            HttpStatusCode.Forbidden, ProblemTypes.Forbidden);

        // Admins may receive from any pull subscription.
        Assert.Empty(await ReceiveAsync(Admin, s.SubscriptionId));

        var webhook = await CreateSubscriptionAsync(s.Topic.TopicId, s.SubscriberId, mode: "Webhook");
        await ProblemAsync(await s.Subscriber.PostAsJsonAsync($"/api/v1/subscriptions/{webhook.SubscriptionId}/receive", new ReceiveRequest()),
            HttpStatusCode.Conflict, ProblemTypes.Conflict);
        await ProblemAsync(await s.Subscriber.PostAsJsonAsync($"/api/v1/subscriptions/{Guid.NewGuid()}/receive", new ReceiveRequest()),
            HttpStatusCode.NotFound, ProblemTypes.NotFound);

        // Without a key: 401.
        await ProblemAsync(await Api.CreateClient(apiKey: null).PostAsJsonAsync($"/api/v1/subscriptions/{s.SubscriptionId}/receive", new ReceiveRequest()),
            HttpStatusCode.Unauthorized, ProblemTypes.Unauthorized);
    }
}
