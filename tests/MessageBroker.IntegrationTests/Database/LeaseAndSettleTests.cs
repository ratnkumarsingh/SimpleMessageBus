using MessageBroker.Application.Persistence;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;

namespace MessageBroker.IntegrationTests.Database;

public class LeaseAndSettleTests(SqlServerFixture sql) : DatabaseTest(sql)
{
    private async Task<(SubscriptionRecord Sub, Guid Publisher, Guid Subscriber, string Topic)> ArrangeAsync(
        int maxAttempts = 4, int lockSeconds = 60, int retryBase = 30, int retryMax = 900)
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: maxAttempts,
            lockSeconds: lockSeconds, retryBase: retryBase, retryMax: retryMax);
        return (sub, publisher, subscriber, topic.Name);
    }

    private async Task<LeasedDeliveryRecord> PublishAndLeaseAsync(SubscriptionRecord sub, string topic, Guid publisher, Guid subscriber)
    {
        await PublishAsync(topic, publisher);
        return Assert.Single(await Deliveries.LeaseAsync(sub.SubscriptionId, 1, "Pull", subscriber));
    }

    [Fact(DisplayName = "D06 [Fix 3] Lease sets token, lock, counts and inserts the attempt row in the same call")]
    public async Task D06_Lease()
    {
        var (sub, publisher, subscriber, topic) = await ArrangeAsync();
        for (var i = 0; i < 3; i++) await PublishAsync(topic, publisher);
        var before = await DbNowAsync();

        var leased = await Deliveries.LeaseAsync(sub.SubscriptionId, 2, "Pull", subscriber);

        Assert.Equal(2, leased.Count);
        Assert.Equal(2, leased.Select(l => l.LockToken).Distinct().Count());
        foreach (var l in leased)
        {
            Assert.Equal(1, l.Attempt);
            Assert.Equal("PaymentProcessed.v1", l.MessageType);
            Assert.Equal("""{"paymentId":"PAY-1"}""", l.Payload);
            Assert.InRange((l.LockedUntil - before).TotalSeconds, 59, 61);

            var row = await GetDeliveryAsync(l.DeliveryId);
            Assert.Equal(DeliveryStatus.Leased, row.State);
            Assert.Equal(l.LockToken, row.LockToken);
            Assert.Equal(1, row.AttemptCount);
            Assert.Equal(1, row.TotalAttemptCount);

            var attempt = Assert.Single(await GetAttemptsAsync(l.DeliveryId));
            Assert.Equal(1, attempt.AttemptNumber);
            Assert.Equal("Pull", attempt.Channel);
            Assert.Null(attempt.EndedAt);
        }
    }

    [Fact(DisplayName = "D07 [Fix 12] Lease skips not-yet-due, expired, leased and paused; oldest first")]
    public async Task D07_LeaseFilters()
    {
        var (sub, publisher, subscriber, topic) = await ArrangeAsync();
        var first = await PublishAsync(topic, publisher);
        var second = await PublishAsync(topic, publisher);
        var future = await PublishAsync(topic, publisher);
        var expired = await PublishAsync(topic, publisher);
        var futureId = (await GetDeliveriesForMessageAsync(future.MessageId)).Single().DeliveryId;
        var expiredId = (await GetDeliveriesForMessageAsync(expired.MessageId)).Single().DeliveryId;
        await ExecAsync("UPDATE broker.Deliveries SET AvailableAt = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE DeliveryId = @futureId", new { futureId });
        await ExecAsync("UPDATE broker.Deliveries SET ExpiresAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE DeliveryId = @expiredId", new { expiredId });

        var batch1 = await Deliveries.LeaseAsync(sub.SubscriptionId, 1, "Pull", subscriber);
        Assert.Equal(first.MessageId, Assert.Single(batch1).MessageId);
        var batch2 = await Deliveries.LeaseAsync(sub.SubscriptionId, 10, "Pull", subscriber);
        Assert.Equal(second.MessageId, Assert.Single(batch2).MessageId); // first is leased; future and expired skipped

        // Paused: nothing is handed out, even when due.
        var other = await PublishAsync(topic, publisher);
        await Subscriptions.UpdateAsync(PublishTests.ToUpdate(sub, "Paused"));
        Assert.Empty(await Deliveries.LeaseAsync(sub.SubscriptionId, 10, "Pull", subscriber));
        await Subscriptions.UpdateAsync(PublishTests.ToUpdate(sub, "Active"));
        Assert.Equal(other.MessageId, Assert.Single(await Deliveries.LeaseAsync(sub.SubscriptionId, 10, "Pull", subscriber)).MessageId);
    }

    [Fact(DisplayName = "D07b Lease checks Receive permission, pull mode and range")]
    public async Task D07b_LeaseGuards()
    {
        var (sub, publisher, _, topic) = await ArrangeAsync();
        var stranger = await CreateAppAsync();
        await ThrowsBrokerAsync(BrokerErrorKind.Forbidden, () => Deliveries.LeaseAsync(sub.SubscriptionId, 1, "Pull", stranger));
        await ThrowsBrokerAsync(BrokerErrorKind.NotFound, () => Deliveries.LeaseAsync(Guid.NewGuid(), 1, "Pull", null));
        await ThrowsBrokerAsync(BrokerErrorKind.Validation, () => Deliveries.LeaseAsync(sub.SubscriptionId, 0, "Pull", null));

        var topicRecord = await Topics.GetByNameAsync(topic);
        var webhook = await CreateSubscriptionAsync(topicRecord!.TopicId, publisher, mode: "Webhook");
        await ThrowsBrokerAsync(BrokerErrorKind.Conflict, () => Deliveries.LeaseAsync(webhook.SubscriptionId, 1, "Pull", null));
    }

    [Fact(DisplayName = "D08 Ack with a valid token completes the delivery and closes the attempt")]
    public async Task D08_Ack()
    {
        var (sub, publisher, subscriber, topic) = await ArrangeAsync();
        var leased = await PublishAndLeaseAsync(sub, topic, publisher, subscriber);

        await Deliveries.AckAsync(leased.DeliveryId, leased.LockToken, subscriber);

        var row = await GetDeliveryAsync(leased.DeliveryId);
        Assert.Equal(DeliveryStatus.Completed, row.State);
        Assert.Null(row.LockToken);
        Assert.NotNull(row.CompletedAt);
        var attempt = Assert.Single(await GetAttemptsAsync(leased.DeliveryId));
        Assert.Equal("Acked", attempt.Outcome);
        Assert.NotNull(attempt.EndedAt);
        Assert.NotNull(attempt.DurationMs);
    }

    [Fact(DisplayName = "D09 Ack with a wrong token, an expired lease or twice is 410 (LeaseLost)")]
    public async Task D09_AckLeaseLost()
    {
        var (sub, publisher, subscriber, topic) = await ArrangeAsync();
        var leased = await PublishAndLeaseAsync(sub, topic, publisher, subscriber);

        await ThrowsBrokerAsync(BrokerErrorKind.LeaseLost, () => Deliveries.AckAsync(leased.DeliveryId, Guid.NewGuid(), subscriber));

        await LapseLeaseAsync(leased.DeliveryId);
        await ThrowsBrokerAsync(BrokerErrorKind.LeaseLost, () => Deliveries.AckAsync(leased.DeliveryId, leased.LockToken, subscriber));

        var second = await PublishAndLeaseAsync(sub, topic, publisher, subscriber);
        await Deliveries.AckAsync(second.DeliveryId, second.LockToken, subscriber);
        await ThrowsBrokerAsync(BrokerErrorKind.LeaseLost, () => Deliveries.AckAsync(second.DeliveryId, second.LockToken, subscriber));

        await ThrowsBrokerAsync(BrokerErrorKind.NotFound, () => Deliveries.AckAsync(long.MaxValue, Guid.NewGuid(), subscriber));
        var stranger = await CreateAppAsync();
        await ThrowsBrokerAsync(BrokerErrorKind.Forbidden, () => Deliveries.AckAsync(second.DeliveryId, second.LockToken, stranger));
    }

    [Fact(DisplayName = "D10 [Fix 4] Nack with attempts left reschedules with jittered exponential backoff on the DB clock")]
    public async Task D10_Backoff()
    {
        var (sub, publisher, subscriber, topic) = await ArrangeAsync(maxAttempts: 10, retryBase: 30, retryMax: 100);
        await PublishAsync(topic, publisher);

        // Attempt n fails -> delay = min(30 * 2^(n-1), 100) * [0.8, 1.2]
        double[] expected = [30, 60, 100, 100];
        for (var n = 1; n <= expected.Length; n++)
        {
            var leased = Assert.Single(await Deliveries.LeaseAsync(sub.SubscriptionId, 1, "Pull", subscriber));
            Assert.Equal(n, leased.Attempt);
            var before = await DbNowAsync();

            var result = await Deliveries.NackAsync(leased.DeliveryId, leased.LockToken, subscriber,
                new FailureDetails("Boom", "Processing failed"));
            var after = await DbNowAsync();

            Assert.False(result.DeadLettered);
            var row = await GetDeliveryAsync(leased.DeliveryId);
            Assert.Equal(DeliveryStatus.Pending, row.State);
            Assert.Equal(result.NextAvailableAt, row.AvailableAt);
            Assert.InRange((row.AvailableAt - before).TotalSeconds, expected[n - 1] * 0.8 - 0.01, expected[n - 1] * 1.2 + (after - before).TotalSeconds + 0.01);

            var attempt = (await GetAttemptsAsync(leased.DeliveryId)).Last();
            Assert.Equal("Nacked", attempt.Outcome);
            Assert.Equal("Boom", attempt.ErrorCode);
            Assert.Equal("Processing failed", attempt.ErrorMessage);

            await MakeDueAsync(leased.DeliveryId);
        }
    }

    [Fact(DisplayName = "D11 Nack on the last attempt dead-letters with MaxAttemptsExceeded and full history")]
    public async Task D11_MaxAttempts()
    {
        var (sub, publisher, subscriber, topic) = await ArrangeAsync(maxAttempts: 3);
        await PublishAsync(topic, publisher);
        long deliveryId = 0;

        for (var n = 1; n <= 3; n++)
        {
            var leased = Assert.Single(await Deliveries.LeaseAsync(sub.SubscriptionId, 1, "Pull", subscriber));
            deliveryId = leased.DeliveryId;
            var result = await Deliveries.NackAsync(leased.DeliveryId, leased.LockToken, subscriber,
                new FailureDetails("E" + n, "failure " + n, "stack " + n, 500));
            Assert.Equal(n == 3, result.DeadLettered);
            await MakeDueAsync(leased.DeliveryId);
        }

        var row = await GetDeliveryAsync(deliveryId);
        Assert.Equal(DeliveryStatus.DeadLettered, row.State);
        var dl = Assert.Single(await GetDeadLettersAsync(deliveryId));
        Assert.Equal("MaxAttemptsExceeded", dl.Reason);
        Assert.Equal(3, dl.AttemptCount);
        Assert.Equal("failure 3", dl.LastError);
        Assert.NotNull(dl.FirstFailureAt);
        Assert.True(dl.FirstFailureAt <= dl.LastFailureAt);
        var attempts = await GetAttemptsAsync(deliveryId);
        Assert.Equal([1, 2, 3], attempts.Select(a => a.AttemptNumber));
        Assert.All(attempts, a => Assert.Equal(500, a.HttpStatusCode));
        Assert.Empty(await Deliveries.LeaseAsync(sub.SubscriptionId, 1, "Pull", subscriber));
    }

    [Fact(DisplayName = "D12 Nack with deadLetter on attempt 1 dead-letters as RejectedBySubscriber")]
    public async Task D12_Rejected()
    {
        var (sub, publisher, subscriber, topic) = await ArrangeAsync();
        var leased = await PublishAndLeaseAsync(sub, topic, publisher, subscriber);

        var result = await Deliveries.NackAsync(leased.DeliveryId, leased.LockToken, subscriber,
            new FailureDetails("Validation", "Amount is negative", DeadLetter: true));

        Assert.True(result.DeadLettered);
        Assert.Null(result.NextAvailableAt);
        var dl = Assert.Single(await GetDeadLettersAsync(leased.DeliveryId));
        Assert.Equal("RejectedBySubscriber", dl.Reason);
        Assert.Equal(1, dl.AttemptCount);
    }

    [Fact(DisplayName = "D13 Renew extends the lease by the lock duration (max 600 s); stale token is 410")]
    public async Task D13_Renew()
    {
        var (sub, publisher, subscriber, topic) = await ArrangeAsync(lockSeconds: 600);
        var leased = await PublishAndLeaseAsync(sub, topic, publisher, subscriber);
        await BackdateAsync("Deliveries", "DeliveryId", leased.DeliveryId, "LockedUntil", 300);
        var before = await DbNowAsync();

        var lockedUntil = await Deliveries.RenewAsync(leased.DeliveryId, leased.LockToken, subscriber);

        Assert.InRange((lockedUntil - before).TotalSeconds, 599, 601);
        Assert.Equal(lockedUntil, (await GetDeliveryAsync(leased.DeliveryId)).LockedUntil);
        await ThrowsBrokerAsync(BrokerErrorKind.LeaseLost, () => Deliveries.RenewAsync(leased.DeliveryId, Guid.NewGuid(), subscriber));

        await LapseLeaseAsync(leased.DeliveryId);
        await ThrowsBrokerAsync(BrokerErrorKind.LeaseLost, () => Deliveries.RenewAsync(leased.DeliveryId, leased.LockToken, subscriber));
    }

    [Fact(DisplayName = "D28 Concurrent ACKs, NACKs and dead-letters of one subscription's deliveries never deadlock")]
    public async Task D28_ConcurrentSettles()
    {
        const int count = 60;
        var (sub, publisher, subscriber, topic) = await ArrangeAsync(maxAttempts: 2);
        for (var i = 0; i < count; i++) await PublishAsync(topic, publisher);

        // Round 1: a third each ACKed, NACKed for retry and rejected to the DLQ, all at once.
        var round1 = await Deliveries.LeaseAsync(sub.SubscriptionId, count, "Pull", subscriber);
        Assert.Equal(count, round1.Count);
        await Task.WhenAll(round1.Select((d, i) => (i % 3) switch
        {
            0 => Deliveries.AckAsync(d.DeliveryId, d.LockToken, subscriber),
            1 => Deliveries.NackAsync(d.DeliveryId, d.LockToken, subscriber, new FailureDetails("E1", "retry")),
            _ => Deliveries.NackAsync(d.DeliveryId, d.LockToken, subscriber, new FailureDetails("E2", "reject", DeadLetter: true)),
        }));

        // Round 2: the retried third fails again together and reaches MaxAttempts.
        await ExecAsync("UPDATE broker.Deliveries SET AvailableAt = SYSUTCDATETIME() WHERE SubscriptionId = @id AND Status = 0",
            new { id = sub.SubscriptionId });
        var round2 = await Deliveries.LeaseAsync(sub.SubscriptionId, count, "Pull", subscriber);
        Assert.Equal(count / 3, round2.Count);
        await Task.WhenAll(round2.Select(d =>
            Deliveries.NackAsync(d.DeliveryId, d.LockToken, subscriber, new FailureDetails("E1", "retry"))));

        var states = await QueryAsync<(byte Status, int Count)>(
            "SELECT Status, COUNT(*) FROM broker.Deliveries WHERE SubscriptionId = @id GROUP BY Status ORDER BY Status",
            new { id = sub.SubscriptionId });
        Assert.Equal([((byte)DeliveryStatus.Completed, count / 3), ((byte)DeliveryStatus.DeadLettered, 2 * count / 3)], states);
        Assert.Equal(0, await ScalarAsync<int>("""
            SELECT COUNT(*) FROM broker.DeliveryAttempts a JOIN broker.Deliveries d ON d.DeliveryId = a.DeliveryId
            WHERE d.SubscriptionId = @id AND a.EndedAt IS NULL
            """, new { id = sub.SubscriptionId }));
    }

    [Fact(DisplayName = "D30 A delivery published this millisecond counts as due for the push lease loop (datetime2(3) rounding)")]
    public async Task D30_DueThisMillisecond()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber, mode: "Webhook");
        await PublishAsync(topic.Name, publisher);
        var deliveryId = (await QueryAsync<long>("SELECT DeliveryId FROM broker.Deliveries")).Single();

        // Store AvailableAt the way publish does, at a moment past the middle of a millisecond, so it
        // rounds up to a time after the clock; then read the view in the same batch.
        for (var i = 0; i < 20; i++)
        {
            var due = await ScalarAsync<bool>("""
                DECLARE @t datetime2(7);
                WHILE 1 = 1
                BEGIN
                    SET @t = SYSUTCDATETIME();
                    IF DATEPART(microsecond, @t) % 1000 BETWEEN 600 AND 700 BREAK;
                END
                UPDATE broker.Deliveries SET AvailableAt = CAST(@t AS datetime2(3)) WHERE DeliveryId = @deliveryId;
                SELECT HasDueDeliveries FROM broker.vw_ActivePushSubscriptions WHERE SubscriptionId = @subscriptionId;
                """, new { deliveryId, subscriptionId = sub.SubscriptionId });
            Assert.True(due, $"Not due on try {i + 1}");
        }
    }
}
