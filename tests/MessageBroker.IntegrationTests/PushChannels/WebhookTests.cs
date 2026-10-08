using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using MessageBroker.Contracts.Models;
using MessageBroker.Contracts.Webhooks;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;
using MessageBroker.Worker.Webhooks;
using Microsoft.Extensions.DependencyInjection;

namespace MessageBroker.IntegrationTests.PushChannels;

public sealed class WebhookTests(SqlServerFixture sql) : WebhookTest(sql)
{
    [Fact(DisplayName = "P01 Webhook 200 completes the delivery; X-Broker headers are present and the signature verifies")]
    public async Task P01_Success()
    {
        Respond(200);
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        await CreateWebhookSubscriptionAsync(topic.TopicId, subscriber);
        var published = await PublishAsync(topic.Name, publisher, correlationId: "PAY-12345");

        Assert.Equal(1, await LeasePassAsync());

        var delivery = (await GetDeliveriesForMessageAsync(published.MessageId)).Single();
        Assert.Equal(DeliveryStatus.Completed, delivery.State);
        var attempt = Assert.Single(await GetAttemptsAsync(delivery.DeliveryId));
        Assert.Equal(("Webhook", "Acked", 200), (attempt.Channel, attempt.Outcome, attempt.HttpStatusCode!.Value));

        var call = Assert.Single(Calls());
        Assert.Equal(delivery.DeliveryId.ToString(), Header(call, WebhookHeaders.DeliveryId));
        Assert.Equal("1", Header(call, WebhookHeaders.Attempt));
        Assert.True(Guid.TryParse(Header(call, WebhookHeaders.LockToken), out _));
        Assert.InRange(long.Parse(Header(call, WebhookHeaders.Timestamp)), DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1);
        Assert.StartsWith("application/json", Header(call, "Content-Type"));

        // Signature: the Contracts verifier and the spec's own sample code both accept it.
        Assert.True(Verifies(call, Secret));
        Assert.False(Verifies(call, "some-other-secret"));
        Assert.True(SpecSampleIsValidSignature(Secret, Header(call, WebhookHeaders.Timestamp), call.Body, Header(call, WebhookHeaders.Signature)));

        // Body: the spec's delivery JSON.
        var body = Body(call);
        Assert.Equal((delivery.DeliveryId, 1, published.MessageId, "PaymentProcessed.v1", "PAY-12345"),
            (body.DeliveryId, body.Attempt, body.Message.MessageId, body.Message.MessageType, body.Message.CorrelationId));
        Assert.Equal(Guid.Parse(Header(call, WebhookHeaders.LockToken)), body.LockToken);
        Assert.Equal("PAY-1", body.Message.Payload.GetProperty("paymentId").GetString());
        Assert.Equal("in-01", body.Message.Properties!.Value.GetProperty("tenant").GetString());

        // Nothing left to deliver.
        Assert.Equal(0, await LeasePassAsync());
    }

    [Fact(DisplayName = "P02 Webhook 500 is retried with backoff and dead-lettered after MaxAttempts with the full history")]
    public async Task P02_RetryThenDeadLetter()
    {
        Respond(500, body: "ledger database down");
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var subscription = await CreateWebhookSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: 3);
        var published = await PublishAsync(topic.Name, publisher);
        var deliveryId = (await GetDeliveriesForMessageAsync(published.MessageId)).Single().DeliveryId;

        Assert.Equal(1, await LeasePassAsync());
        var afterFirst = await GetDeliveryAsync(deliveryId);
        Assert.Equal(DeliveryStatus.Pending, afterFirst.State);
        Assert.InRange((afterFirst.AvailableAt - await DbNowAsync()).TotalSeconds, 20, 37); // 30 s ± 20 %

        Assert.Equal(0, await LeasePassAsync()); // backoff not over

        for (var attempt = 2; attempt <= 3; attempt++)
        {
            await MakeAllDueAsync(subscription.SubscriptionId);
            Assert.Equal(1, await LeasePassAsync());
        }

        Assert.Equal(DeliveryStatus.DeadLettered, (await GetDeliveryAsync(deliveryId)).State);
        var attempts = await GetAttemptsAsync(deliveryId);
        Assert.Equal([1, 2, 3], attempts.Select(a => a.AttemptNumber));
        Assert.All(attempts, a => Assert.Equal(("Nacked", "Http500", 500, "ledger database down"),
            (a.Outcome, a.ErrorCode, a.HttpStatusCode!.Value, a.ErrorDetail)));
        Assert.Equal(["1", "2", "3"], Calls().Select(c => Header(c, WebhookHeaders.Attempt)));
        var deadLetter = Assert.Single(await GetDeadLettersAsync(deliveryId));
        Assert.Equal(("MaxAttemptsExceeded", 3), (deadLetter.Reason, deadLetter.AttemptCount));
    }

    [Fact(DisplayName = "P03 A webhook that does not answer within its timeout is NACKed with ErrorCode Timeout")]
    public async Task P03_Timeout()
    {
        Respond(200, delay: TimeSpan.FromSeconds(5));
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        await CreateWebhookSubscriptionAsync(topic.TopicId, subscriber, timeout: 1);
        var published = await PublishAsync(topic.Name, publisher);

        var timer = Stopwatch.StartNew();
        await LeasePassAsync();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4), $"took {timer.Elapsed}");

        var delivery = (await GetDeliveriesForMessageAsync(published.MessageId)).Single();
        Assert.Equal(DeliveryStatus.Pending, delivery.State);
        var attempt = Assert.Single(await GetAttemptsAsync(delivery.DeliveryId));
        Assert.Equal(("Nacked", "Timeout", (int?)null), (attempt.Outcome, attempt.ErrorCode, attempt.HttpStatusCode));
    }

    [Fact(DisplayName = "P03b An unreachable endpoint is NACKed with ErrorCode ConnectionError")]
    public async Task P03b_ConnectionError()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var protector = Api.Services.GetRequiredService<MessageBroker.Application.Security.ISecretProtector>();
        await CreateSubscriptionAsync(topic.TopicId, subscriber, mode: "Webhook", webhookUrl: "http://127.0.0.1:1/hook",
            protectedSecret: protector.Protect(Secret), webhookTimeout: 5);
        var published = await PublishAsync(topic.Name, publisher);

        await LeasePassAsync();

        var attempt = Assert.Single(await GetAttemptsAsync((await GetDeliveriesForMessageAsync(published.MessageId)).Single().DeliveryId));
        Assert.Equal(("Nacked", "ConnectionError"), (attempt.Outcome, attempt.ErrorCode));
    }

    [Fact(DisplayName = "P04 202 then a REST ACK completes the delivery; 202 without an ACK is redelivered after the lease expires")]
    public async Task P04_AcceptedThenSettledLater()
    {
        Respond(202);
        var (topic, publisher, _) = await ArrangeTopicAsync();
        var (ownerId, _, owner) = await CreateAppClientAsync();
        await CreateWebhookSubscriptionAsync(topic.TopicId, ownerId);

        var first = await PublishAsync(topic.Name, publisher);
        Assert.Equal(1, await LeasePassAsync());
        var held = (await GetDeliveriesForMessageAsync(first.MessageId)).Single();
        Assert.Equal(DeliveryStatus.Leased, held.State);
        Assert.Null(Assert.Single(await GetAttemptsAsync(held.DeliveryId)).Outcome);

        // The subscriber settles later with the lock token from the webhook headers.
        var call = Assert.Single(Calls());
        await ExpectAsync(owner.PostAsJsonAsync($"/api/v1/deliveries/{Header(call, WebhookHeaders.DeliveryId)}/ack",
            new AckRequest(Guid.Parse(Header(call, WebhookHeaders.LockToken)))), HttpStatusCode.NoContent);
        Assert.Equal(DeliveryStatus.Completed, (await GetDeliveryAsync(held.DeliveryId)).State);

        // A held delivery that is never settled comes back after its lease.
        var second = await PublishAsync(topic.Name, publisher);
        Assert.Equal(1, await LeasePassAsync());
        var unsettled = (await GetDeliveriesForMessageAsync(second.MessageId)).Single().DeliveryId;
        Assert.Equal(0, await LeasePassAsync()); // still leased

        await LapseLeaseAsync(unsettled);
        await MaintenancePassAsync();
        await MakeDueAsync(unsettled);
        Assert.Equal(1, await LeasePassAsync());

        var redelivered = Calls().Last();
        Assert.Equal((unsettled.ToString(), "2"), (Header(redelivered, WebhookHeaders.DeliveryId), Header(redelivered, WebhookHeaders.Attempt)));
        Assert.Equal("LeaseExpired", (await GetAttemptsAsync(unsettled))[0].Outcome);
    }

    [Fact(DisplayName = "P05 A 302 redirect is not followed and counts as a failed attempt")]
    public async Task P05_RedirectNotFollowed()
    {
        Respond(302, headers: new Dictionary<string, string> { ["Location"] = Endpoint.Url + "/internal-admin" });
        Respond(200, path: "/internal-admin");
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        await CreateWebhookSubscriptionAsync(topic.TopicId, subscriber);
        var published = await PublishAsync(topic.Name, publisher);

        await LeasePassAsync();

        Assert.Empty(Calls("/internal-admin"));
        var attempt = Assert.Single(await GetAttemptsAsync((await GetDeliveriesForMessageAsync(published.MessageId)).Single().DeliveryId));
        Assert.Equal(("Nacked", "Http302", 302), (attempt.Outcome, attempt.ErrorCode, attempt.HttpStatusCode!.Value));
    }

    [Fact(DisplayName = "P06 Circuit opens after 5 failures; while open nothing is leased; half-open trial; closes on success")]
    public async Task P06_CircuitBreaker()
    {
        await using var api = new ApiFactory(Sql.ConnectionString, new Dictionary<string, string?>
        {
            ["Broker:Webhooks:CircuitOpenSeconds"] = "1",
        });
        var circuits = api.Services.GetRequiredService<CircuitBreakerRegistry>();

        Respond(500);
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var subscription = await CreateWebhookSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: 100, maxConcurrent: 1, api: api);
        for (var i = 0; i < 7; i++)
            await PublishAsync(topic.Name, publisher);
        Task<int> TotalAttemptsAsync() => ScalarAsync<int>("SELECT COUNT(*) FROM broker.DeliveryAttempts");

        for (var i = 1; i <= 5; i++)
        {
            Assert.Equal(CircuitState.Closed, circuits.GetState(subscription.SubscriptionId));
            Assert.Equal(1, await LeasePassAsync(api));
            await MakeAllDueAsync(subscription.SubscriptionId);
        }
        Assert.Equal(CircuitState.Open, circuits.GetState(subscription.SubscriptionId));

        // Open: nothing is leased, so no attempts are used.
        Assert.Equal(0, await LeasePassAsync(api));
        Assert.Equal(5, await TotalAttemptsAsync());

        // Half-open: exactly one trial; it fails and the circuit opens again.
        await Task.Delay(1100);
        Assert.Equal(CircuitState.HalfOpen, circuits.GetState(subscription.SubscriptionId));
        Assert.Equal(1, await LeasePassAsync(api));
        Assert.Equal(CircuitState.Open, circuits.GetState(subscription.SubscriptionId));
        await MakeAllDueAsync(subscription.SubscriptionId);
        Assert.Equal(0, await LeasePassAsync(api));
        Assert.Equal(6, await TotalAttemptsAsync());

        // The endpoint recovers: the trial succeeds, the circuit closes, and the backlog drains.
        Endpoint.ResetReplies();
        Respond(200);
        await Task.Delay(1100);
        Assert.Equal(1, await LeasePassAsync(api));
        Assert.Equal(CircuitState.Closed, circuits.GetState(subscription.SubscriptionId));
        for (var i = 0; i < 10 && await LeasePassAsync(api) > 0; i++)
        {
        }
        Assert.Equal(7, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries WHERE Status = 2"));
    }

    [Fact(DisplayName = "P07 [Fix 7] During secret rotation the signature header verifies with either the old or the new secret")]
    public async Task P07_SecretRotation()
    {
        Respond(200);
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var subscription = await CreateWebhookSubscriptionAsync(topic.TopicId, subscriber);
        await Subscriptions.RotateSecretAsync(subscription.SubscriptionId, Protect("whsec-new-9876543210"));

        await PublishAsync(topic.Name, publisher);
        await LeasePassAsync();

        var during = Assert.Single(Calls());
        Assert.Equal(2, Header(during, WebhookHeaders.Signature).Split(',').Length);
        Assert.True(Verifies(during, "whsec-new-9876543210"));
        Assert.True(Verifies(during, Secret));
        Assert.False(Verifies(during, "unrelated-secret"));

        // After the 24-hour window only the new secret signs.
        await BackdateAsync("Subscriptions", "SubscriptionId", subscription.SubscriptionId, "PreviousSecretExpiresAt", 25 * 3600);
        await PublishAsync(topic.Name, publisher);
        await LeasePassAsync();

        var after = Calls().Last();
        Assert.Single(Header(after, WebhookHeaders.Signature).Split(','));
        Assert.True(Verifies(after, "whsec-new-9876543210"));
        Assert.False(Verifies(after, Secret));
    }

    [Fact(DisplayName = "P13 A failing webhook subscription does not affect a pull subscription on the same topic")]
    public async Task P13_Independence()
    {
        Respond(500);
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var webhook = await CreateWebhookSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: 10);
        var pull = await CreateSubscriptionAsync(topic.TopicId, subscriber, mode: "Pull");
        for (var i = 0; i < 3; i++)
            await PublishAsync(topic.Name, publisher);

        // Two passes of 3 failed calls open the webhook's circuit (5 consecutive failures); the third leases nothing.
        for (var pass = 0; pass < 2; pass++)
        {
            Assert.Equal(3, await LeasePassAsync());
            await MakeAllDueAsync(webhook.SubscriptionId);
        }
        Assert.Equal(0, await LeasePassAsync());

        var pulled = await Deliveries.LeaseAsync(pull.SubscriptionId, 10, "Pull", subscriber);
        Assert.Equal(3, pulled.Count);
        Assert.All(pulled, d => Assert.Equal(1, d.Attempt));
        foreach (var d in pulled)
            await Deliveries.AckAsync(d.DeliveryId, d.LockToken, subscriber);

        var counts = await QueryAsync<(Guid SubscriptionId, byte Status, int Attempts)>(
            "SELECT SubscriptionId, Status, AttemptCount FROM broker.Deliveries");
        Assert.All(counts.Where(c => c.SubscriptionId == pull.SubscriptionId), c => Assert.Equal(((byte)2, 1), (c.Status, c.Attempts)));
        Assert.All(counts.Where(c => c.SubscriptionId == webhook.SubscriptionId), c => Assert.Equal(((byte)0, 2), (c.Status, c.Attempts)));
    }

    [Fact(DisplayName = "P14a MaxConcurrentDeliveries caps the webhook calls in flight per subscription")]
    public async Task P14a_ConcurrencyCap()
    {
        Respond(200, delay: TimeSpan.FromMilliseconds(300));
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        await CreateWebhookSubscriptionAsync(topic.TopicId, subscriber, maxConcurrent: 3);
        for (var i = 0; i < 7; i++)
            await PublishAsync(topic.Name, publisher);

        Assert.Equal(3, await LeasePassAsync());
        Assert.Equal(3, await LeasePassAsync());
        Assert.Equal(1, await LeasePassAsync());
        Assert.Equal(7, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries WHERE Status = 2"));
    }

    /// <summary>The verification code from spec section 9, verbatim.</summary>
    private static bool SpecSampleIsValidSignature(string secret, string timestamp, string body, string header)
    {
        var data = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data);
        var expected = "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header));
    }
}
