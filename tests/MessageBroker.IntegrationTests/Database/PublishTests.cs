using MessageBroker.Application.Persistence;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace MessageBroker.IntegrationTests.Database;

public class PublishTests(SqlServerFixture sql) : DatabaseTest(sql)
{
    [Fact(DisplayName = "D01 Publish fans out to Active and Paused subscriptions, not Deleted")]
    public async Task D01_FanOut()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var active = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var paused = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var deleted = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        await Subscriptions.UpdateAsync(ToUpdate(paused, "Paused"));
        await Subscriptions.DeleteAsync(deleted.SubscriptionId);

        var result = await PublishAsync(topic.Name, publisher);

        Assert.False(result.IsDuplicate);
        Assert.Equal(2, result.DeliveryCount);
        var deliveries = await GetDeliveriesForMessageAsync(result.MessageId);
        Assert.Equal(
            new[] { active.SubscriptionId, paused.SubscriptionId }.Order(),
            deliveries.Select(d => d.SubscriptionId).Order());
        Assert.All(deliveries, d =>
        {
            Assert.Equal(DeliveryStatus.Pending, d.State);
            Assert.Equal(0, d.AttemptCount);
        });
    }

    [Fact(DisplayName = "D02 Publish with no subscriptions stores the message with zero deliveries")]
    public async Task D02_NoSubscriptions()
    {
        var (topic, publisher, _) = await ArrangeTopicAsync();

        var result = await PublishAsync(topic.Name, publisher);

        Assert.Equal(0, result.DeliveryCount);
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Messages WHERE MessageId = @id", new { id = result.MessageId }));
    }

    [Fact(DisplayName = "D03 Same idempotency key returns the original message; other topic or publisher does not")]
    public async Task D03_Idempotency()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var otherTopic = await CreateTopicAsync();
        await GrantPublishAsync(publisher, otherTopic.TopicId);
        var otherPublisher = await CreateAppAsync();
        await GrantPublishAsync(otherPublisher, topic.TopicId);

        var first = await PublishAsync(topic.Name, publisher, "outbox-1");
        var repeat = await PublishAsync(topic.Name, publisher, "outbox-1");
        var otherTopicResult = await PublishAsync(otherTopic.Name, publisher, "outbox-1");
        var otherPublisherResult = await PublishAsync(topic.Name, otherPublisher, "outbox-1");

        Assert.True(repeat.IsDuplicate);
        Assert.Equal(first.MessageId, repeat.MessageId);
        Assert.Equal(first.DeliveryCount, repeat.DeliveryCount);
        Assert.False(otherTopicResult.IsDuplicate);
        Assert.False(otherPublisherResult.IsDuplicate);
        Assert.Equal(3, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Messages"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries"));
    }

    [Fact(DisplayName = "D04 [Fix 2] 20 concurrent publishes with one key create exactly one message")]
    public async Task D04_IdempotencyRace()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        await CreateSubscriptionAsync(topic.TopicId, subscriber);

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            return await PublishAsync(topic.Name, publisher, "race-key");
        })).ToList();
        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results.Select(r => r.MessageId).Distinct());
        Assert.Single(results, r => !r.IsDuplicate);
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Messages"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries"));
    }

    [Fact(DisplayName = "D05 Delivery ExpiresAt is the earlier of message TTL (or topic default) and subscription TTL")]
    public async Task D05_ExpiresAt()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync(defaultTtl: 600);
        var noTtl = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var shortTtl = await CreateSubscriptionAsync(topic.TopicId, subscriber, ttlSeconds: 60);
        var longTtl = await CreateSubscriptionAsync(topic.TopicId, subscriber, ttlSeconds: 6000);

        // Topic default applies when the message has no TTL.
        var withDefault = await PublishAsync(topic.Name, publisher);
        var d = (await GetDeliveriesForMessageAsync(withDefault.MessageId)).ToDictionary(x => x.SubscriptionId);
        AssertSecondsAfterCreate(600, d[noTtl.SubscriptionId]);
        AssertSecondsAfterCreate(60, d[shortTtl.SubscriptionId]);
        AssertSecondsAfterCreate(600, d[longTtl.SubscriptionId]);

        // Message TTL overrides the topic default.
        var withTtl = await PublishAsync(topic.Name, publisher, ttl: 30);
        d = (await GetDeliveriesForMessageAsync(withTtl.MessageId)).ToDictionary(x => x.SubscriptionId);
        AssertSecondsAfterCreate(30, d[noTtl.SubscriptionId]);
        AssertSecondsAfterCreate(30, d[shortTtl.SubscriptionId]);

        // Neither topic default nor message TTL: only the subscription TTL counts.
        var bare = await CreateTopicAsync();
        await GrantPublishAsync(publisher, bare.TopicId);
        var bareNoTtl = await CreateSubscriptionAsync(bare.TopicId, subscriber);
        var bareTtl = await CreateSubscriptionAsync(bare.TopicId, subscriber, ttlSeconds: 90);
        var bareResult = await PublishAsync(bare.Name, publisher);
        d = (await GetDeliveriesForMessageAsync(bareResult.MessageId)).ToDictionary(x => x.SubscriptionId);
        Assert.Null(d[bareNoTtl.SubscriptionId].ExpiresAt);
        AssertSecondsAfterCreate(90, d[bareTtl.SubscriptionId]);
        Assert.Null((await QueryAsync<DateTime?>("SELECT ExpiresAt FROM broker.Messages WHERE MessageId = @id", new { id = bareResult.MessageId })).Single());

        static void AssertSecondsAfterCreate(int seconds, DeliveryRow row) =>
            Assert.Equal(seconds, (row.ExpiresAt!.Value - row.CreatedAt).TotalSeconds, precision: 0);
    }

    [Fact(DisplayName = "D05b Publish to an unknown topic is 404; without Publish permission is 403")]
    public async Task D05b_TopicAndPermission()
    {
        var (topic, _, subscriber) = await ArrangeTopicAsync();
        await ThrowsBrokerAsync(BrokerErrorKind.NotFound, () => PublishAsync("no-such-topic", subscriber));
        await ThrowsBrokerAsync(BrokerErrorKind.Forbidden, () => PublishAsync(topic.Name, subscriber));
    }

    [Fact(DisplayName = "D22 [Fix 5] CHECK constraints reject invalid JSON payload and properties")]
    public async Task D22_JsonCheck()
    {
        var (topic, publisher, _) = await ArrangeTopicAsync();

        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(
            "EXEC broker.usp_Message_Publish @MessageId=@id, @TopicName=@t, @AppId=@a, @MessageType='T', @CorrelationId='c', @Payload='{not json'",
            new { id = Guid.NewGuid(), t = topic.Name, a = publisher }));
        Assert.Equal(547, ex.Number); // CHECK constraint violation

        ex = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(
            "EXEC broker.usp_Message_Publish @MessageId=@id, @TopicName=@t, @AppId=@a, @MessageType='T', @CorrelationId='c', @Payload='{}', @Properties='[oops'",
            new { id = Guid.NewGuid(), t = topic.Name, a = publisher }));
        Assert.Equal(547, ex.Number);
    }

    internal static SubscriptionUpdate ToUpdate(SubscriptionRecord s, string status) => new(
        s.SubscriptionId, status, s.WebhookUrl, s.WebhookTimeoutSeconds, s.MaxConcurrentDeliveries, s.MaxAttempts,
        s.LockDurationSeconds, s.RetryBaseDelaySeconds, s.RetryMaxDelaySeconds, s.TtlSeconds);
}
