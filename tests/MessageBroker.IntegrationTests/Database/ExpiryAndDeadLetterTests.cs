using MessageBroker.Application.Persistence;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;

namespace MessageBroker.IntegrationTests.Database;

public class ExpiryAndDeadLetterTests(SqlServerFixture sql) : DatabaseTest(sql)
{
    [Fact(DisplayName = "D14 ExpireLeases records LeaseExpired and retries, then dead-letters on the last attempt")]
    public async Task D14_ExpireLeases()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: 2);
        await PublishAsync(topic.Name, publisher);

        var first = Assert.Single(await Deliveries.LeaseAsync(sub.SubscriptionId, 1, "Pull", subscriber));
        await LapseLeaseAsync(first.DeliveryId);
        Assert.Equal(1, await Deliveries.ExpireLeasesAsync());

        var row = await GetDeliveryAsync(first.DeliveryId);
        Assert.Equal(DeliveryStatus.Pending, row.State);
        Assert.Null(row.LockToken);
        var attempt = Assert.Single(await GetAttemptsAsync(first.DeliveryId));
        Assert.Equal("LeaseExpired", attempt.Outcome);

        await MakeDueAsync(first.DeliveryId);
        var second = Assert.Single(await Deliveries.LeaseAsync(sub.SubscriptionId, 1, "Pull", subscriber));
        await LapseLeaseAsync(second.DeliveryId);
        Assert.Equal(1, await Deliveries.ExpireLeasesAsync());

        Assert.Equal(DeliveryStatus.DeadLettered, (await GetDeliveryAsync(first.DeliveryId)).State);
        Assert.Equal("MaxAttemptsExceeded", Assert.Single(await GetDeadLettersAsync(first.DeliveryId)).Reason);
        Assert.Equal(0, await Deliveries.ExpireLeasesAsync());
    }

    [Fact(DisplayName = "D15 ExpirePending dead-letters Pending past ExpiresAt as Expired; leased ones finish")]
    public async Task D15_ExpirePending()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var pending = await PublishAsync(topic.Name, publisher, ttl: 60);
        var leasedMessage = await PublishAsync(topic.Name, publisher, ttl: 60);
        var fresh = await PublishAsync(topic.Name, publisher, ttl: 60);
        var pendingId = (await GetDeliveriesForMessageAsync(pending.MessageId)).Single().DeliveryId;
        var leasedId = (await GetDeliveriesForMessageAsync(leasedMessage.MessageId)).Single().DeliveryId;
        var freshId = (await GetDeliveriesForMessageAsync(fresh.MessageId)).Single().DeliveryId;

        // Lease the second one, then push both of the first two past their expiry.
        await ExecAsync("UPDATE broker.Deliveries SET AvailableAt = DATEADD(minute, 1, SYSUTCDATETIME()) WHERE DeliveryId IN (@pendingId, @freshId)", new { pendingId, freshId });
        var leased = Assert.Single(await Deliveries.LeaseAsync(sub.SubscriptionId, 1, "Pull", subscriber));
        Assert.Equal(leasedId, leased.DeliveryId);
        await BackdateAsync("Deliveries", "DeliveryId", pendingId, "ExpiresAt", 120);
        await BackdateAsync("Deliveries", "DeliveryId", leasedId, "ExpiresAt", 120);

        Assert.Equal(1, await Deliveries.ExpirePendingAsync());

        Assert.Equal(DeliveryStatus.DeadLettered, (await GetDeliveryAsync(pendingId)).State);
        var dl = Assert.Single(await GetDeadLettersAsync(pendingId));
        Assert.Equal("Expired", dl.Reason);
        Assert.Equal(0, dl.AttemptCount);
        Assert.Equal(DeliveryStatus.Leased, (await GetDeliveryAsync(leasedId)).State);
        Assert.Equal(DeliveryStatus.Pending, (await GetDeliveryAsync(freshId)).State);

        // The leased delivery may still be acknowledged.
        await Deliveries.AckAsync(leased.DeliveryId, leased.LockToken, subscriber);
        Assert.Equal(DeliveryStatus.Completed, (await GetDeliveryAsync(leasedId)).State);
    }

    [Fact(DisplayName = "D16 [Fix 1] Requeue resets the budget; the delivery can fail and dead-letter again")]
    public async Task D16_RequeueTwice()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var admin = await CreateAppAsync(isAdmin: true);
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: 2);
        await PublishAsync(topic.Name, publisher, ttl: 60);
        long deliveryId = 0;

        async Task FailAllAttempts()
        {
            for (var i = 0; i < 2; i++)
            {
                var leased = Assert.Single(await Deliveries.LeaseAsync(sub.SubscriptionId, 1, "Pull", subscriber));
                deliveryId = leased.DeliveryId;
                await Deliveries.NackAsync(leased.DeliveryId, leased.LockToken, subscriber, new FailureDetails("E", "fail"));
                await MakeDueAsync(leased.DeliveryId);
            }
        }

        await FailAllAttempts();
        Assert.Equal(DeliveryStatus.DeadLettered, (await GetDeliveryAsync(deliveryId)).State);

        await Deliveries.RequeueAsync(deliveryId, admin);
        var requeued = await GetDeliveryAsync(deliveryId);
        Assert.Equal(DeliveryStatus.Pending, requeued.State);
        Assert.Equal(0, requeued.AttemptCount);
        Assert.Equal(2, requeued.TotalAttemptCount);
        Assert.Null(requeued.ExpiresAt);
        var first = Assert.Single(await GetDeadLettersAsync(deliveryId));
        Assert.NotNull(first.RequeuedAt);
        Assert.Equal(admin, first.RequeuedBy);

        await FailAllAttempts(); // would violate the old unique keys
        Assert.Equal(DeliveryStatus.DeadLettered, (await GetDeliveryAsync(deliveryId)).State);
        Assert.Equal([1, 2, 3, 4], (await GetAttemptsAsync(deliveryId)).Select(a => a.AttemptNumber));
        var history = await GetDeadLettersAsync(deliveryId);
        Assert.Equal(2, history.Count);
        Assert.Null(history[1].RequeuedAt);
        Assert.True(history[1].FirstFailureAt >= first.RequeuedAt);

        // Requeue again and succeed this time.
        await Deliveries.RequeueAsync(deliveryId, admin);
        var leasedAgain = Assert.Single(await Deliveries.LeaseAsync(sub.SubscriptionId, 1, "Pull", subscriber));
        Assert.Equal(1, leasedAgain.Attempt);
        await Deliveries.AckAsync(leasedAgain.DeliveryId, leasedAgain.LockToken, subscriber);
    }

    [Fact(DisplayName = "D16b Requeue of a missing or not dead-lettered delivery fails")]
    public async Task D16b_RequeueGuards()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var result = await PublishAsync(topic.Name, publisher);
        var deliveryId = (await GetDeliveriesForMessageAsync(result.MessageId)).Single().DeliveryId;

        await ThrowsBrokerAsync(BrokerErrorKind.NotFound, () => Deliveries.RequeueAsync(long.MaxValue, subscriber));
        await ThrowsBrokerAsync(BrokerErrorKind.Conflict, () => Deliveries.RequeueAsync(deliveryId, subscriber));
    }

    [Fact(DisplayName = "D17 Deleting a subscription cancels its deliveries; late ACK is 410; topic delete needs no subscriptions")]
    public async Task D17_DeleteSubscription()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var keep = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        await PublishAsync(topic.Name, publisher);
        await PublishAsync(topic.Name, publisher);
        var leased = (await Deliveries.LeaseAsync(sub.SubscriptionId, 1, "Pull", subscriber)).Single();

        await ThrowsBrokerAsync(BrokerErrorKind.Conflict, () => Topics.DeleteAsync(topic.TopicId));
        await Subscriptions.DeleteAsync(sub.SubscriptionId);

        var rows = await QueryAsync<DeliveryRow>("SELECT * FROM broker.Deliveries WHERE SubscriptionId = @id", new { id = sub.SubscriptionId });
        Assert.All(rows, r => Assert.Equal(DeliveryStatus.Cancelled, r.State));
        Assert.Equal("Cancelled", Assert.Single(await GetAttemptsAsync(leased.DeliveryId)).Outcome);
        await ThrowsBrokerAsync(BrokerErrorKind.LeaseLost, () => Deliveries.AckAsync(leased.DeliveryId, leased.LockToken, null));
        Assert.Null(await Subscriptions.GetAsync(sub.SubscriptionId));
        await ThrowsBrokerAsync(BrokerErrorKind.NotFound, () => Subscriptions.DeleteAsync(sub.SubscriptionId));

        // The other subscription is untouched.
        var kept = await QueryAsync<DeliveryRow>("SELECT * FROM broker.Deliveries WHERE SubscriptionId = @id", new { id = keep.SubscriptionId });
        Assert.All(kept, r => Assert.Equal(DeliveryStatus.Pending, r.State));

        await Subscriptions.DeleteAsync(keep.SubscriptionId);
        await Topics.DeleteAsync(topic.TopicId);
        Assert.Null(await Topics.GetAsync(topic.TopicId));
    }
}
