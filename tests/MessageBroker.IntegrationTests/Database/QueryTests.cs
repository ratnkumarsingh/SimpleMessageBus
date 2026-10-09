using MessageBroker.Application.Persistence;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;

namespace MessageBroker.IntegrationTests.Database;

public class QueryTests(SqlServerFixture sql) : DatabaseTest(sql)
{
    private Task<string> StatusAsync(Guid messageId) =>
        ScalarAsync<string>("SELECT Status FROM broker.vw_MessageStatus WHERE MessageId = @messageId", P("messageId", messageId));

    private Task SetStatusAsync(Guid subscriptionId, Guid messageId, DeliveryStatus status) =>
        ExecAsync("UPDATE broker.Deliveries SET Status = @status WHERE SubscriptionId = @subscriptionId AND MessageId = @messageId",
            P("status", (byte)status), P("subscriptionId", subscriptionId), P("messageId", messageId));

    [Fact(DisplayName = "D18 [Fix 12] vw_MessageStatus derives status and ignores Cancelled deliveries")]
    public async Task D18_MessageStatus()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var a = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var b = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var m = (await PublishAsync(topic.Name, publisher)).MessageId;

        Assert.Equal("InProgress", await StatusAsync(m));

        (DeliveryStatus A, DeliveryStatus B, string Expected)[] cases =
        [
            (DeliveryStatus.Leased, DeliveryStatus.Completed, "InProgress"),
            (DeliveryStatus.Completed, DeliveryStatus.Completed, "Completed"),
            (DeliveryStatus.Completed, DeliveryStatus.Cancelled, "Completed"),
            (DeliveryStatus.Completed, DeliveryStatus.DeadLettered, "PartiallyDeadLettered"),
            (DeliveryStatus.DeadLettered, DeliveryStatus.DeadLettered, "DeadLettered"),
            (DeliveryStatus.DeadLettered, DeliveryStatus.Cancelled, "DeadLettered"),
            (DeliveryStatus.Cancelled, DeliveryStatus.Cancelled, "Completed"),
        ];
        foreach (var (sa, sb, expected) in cases)
        {
            await SetStatusAsync(a.SubscriptionId, m, sa);
            await SetStatusAsync(b.SubscriptionId, m, sb);
            Assert.Equal(expected, await StatusAsync(m));
            // The domain derivation agrees with the view.
            Assert.Equal(expected, DeliveryStateMachine.DeriveMessageStatus([sa, sb]).ToString());
        }

        var none = await CreateTopicAsync();
        await GrantPublishAsync(publisher, none.TopicId);
        Assert.Equal("Completed", await StatusAsync((await PublishAsync(none.Name, publisher)).MessageId));
    }

    [Fact(DisplayName = "D19 Subscription counts and DLQ details views")]
    public async Task D19_CountsAndDeadLetterView()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        for (var i = 0; i < 4; i++) await PublishAsync(topic.Name, publisher, correlationId: "CORR-1");
        var leased = await Deliveries.LeaseAsync(sub.SubscriptionId, 2, "Pull", subscriber);
        await Deliveries.NackAsync(leased[0].DeliveryId, leased[0].LockToken, subscriber, new FailureDetails("Bad", "rejected", DeadLetter: true));

        var details = await Subscriptions.GetAsync(sub.SubscriptionId);
        Assert.Equal(2, details!.PendingCount);
        Assert.Equal(1, details.LeasedCount);
        Assert.Equal(1, details.DeadLetteredCount);
        Assert.Equal(topic.Name, details.TopicName);

        var dl = Assert.Single(await Deliveries.ListDeadLettersAsync(sub.SubscriptionId, subscriber, 50, null, false));
        Assert.Equal(leased[0].DeliveryId, dl.DeliveryId);
        Assert.Equal("RejectedBySubscriber", dl.Reason);
        Assert.Equal(topic.Name, dl.TopicName);
        Assert.Equal(sub.Name, dl.SubscriptionName);
        Assert.Equal("CORR-1", dl.CorrelationId);
        Assert.Equal("""{"paymentId":"PAY-1"}""", dl.Payload);
        Assert.Equal("rejected", dl.LastError);
    }

    [Fact(DisplayName = "D19b DLQ list pages newest first and checks access")]
    public async Task D19b_DeadLetterPaging()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        for (var i = 0; i < 5; i++) await PublishAsync(topic.Name, publisher);
        foreach (var l in await Deliveries.LeaseAsync(sub.SubscriptionId, 5, "Pull", subscriber))
            await Deliveries.NackAsync(l.DeliveryId, l.LockToken, subscriber, new FailureDetails("X", "x", DeadLetter: true));

        var page1 = await Deliveries.ListDeadLettersAsync(sub.SubscriptionId, subscriber, 2, null, false);
        var page2 = await Deliveries.ListDeadLettersAsync(sub.SubscriptionId, subscriber, 2, page1[^1].DeadLetterId, false);
        var page3 = await Deliveries.ListDeadLettersAsync(sub.SubscriptionId, subscriber, 2, page2[^1].DeadLetterId, false);

        var ids = page1.Concat(page2).Concat(page3).Select(d => d.DeadLetterId).ToList();
        Assert.Equal(5, ids.Count);
        Assert.Equal(ids.OrderDescending(), ids);

        var stranger = await CreateAppAsync();
        await ThrowsBrokerAsync(BrokerErrorKind.Forbidden, () => Deliveries.ListDeadLettersAsync(sub.SubscriptionId, stranger, 10, null, false));
        var admin = await CreateAppAsync(isAdmin: true);
        Assert.Equal(5, (await Deliveries.ListDeadLettersAsync(sub.SubscriptionId, admin, 10, null, false)).Count);
    }

    [Fact(DisplayName = "D20 GetById returns message, deliveries and attempts; correlation lookup returns only matches")]
    public async Task D20_Traceability()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var a = await CreateSubscriptionAsync(topic.TopicId, subscriber, name: "a-sub");
        await CreateSubscriptionAsync(topic.TopicId, subscriber, name: "b-sub");
        var m1 = await PublishAsync(topic.Name, publisher, correlationId: "PAY-42");
        await PublishAsync(topic.Name, publisher, correlationId: "PAY-42");
        await PublishAsync(topic.Name, publisher, correlationId: "OTHER");
        var leased = (await Deliveries.LeaseAsync(a.SubscriptionId, 1, "Pull", subscriber)).Single();
        await Deliveries.NackAsync(leased.DeliveryId, leased.LockToken, subscriber, new FailureDetails("Timeout", "slow"));

        var details = await Messages.GetByIdAsync(m1.MessageId, publisher);

        Assert.Equal(m1.MessageId, details.Message.MessageId);
        Assert.Equal(topic.Name, details.Message.TopicName);
        Assert.Equal("PAY-42", details.Message.CorrelationId);
        Assert.Equal("InProgress", details.Message.Status);
        Assert.Equal(2, details.Deliveries.Count);
        Assert.Equal(["a-sub", "b-sub"], details.Deliveries.Select(d => d.SubscriptionName).Order());
        var attempt = Assert.Single(details.Attempts);
        Assert.Equal("Nacked", attempt.Outcome);
        Assert.Equal("Timeout", attempt.ErrorCode);

        var correlated = await Messages.ListByCorrelationAsync("PAY-42");
        Assert.Equal(2, correlated.Count);
        Assert.All(correlated, c => Assert.Equal("PAY-42", c.CorrelationId));

        var otherPublisher = await CreateAppAsync();
        await ThrowsBrokerAsync(BrokerErrorKind.Forbidden, () => Messages.GetByIdAsync(m1.MessageId, otherPublisher));
        var admin = await CreateAppAsync(isAdmin: true);
        Assert.Equal(m1.MessageId, (await Messages.GetByIdAsync(m1.MessageId, admin)).Message.MessageId);
        await ThrowsBrokerAsync(BrokerErrorKind.NotFound, () => Messages.GetByIdAsync(Guid.NewGuid(), admin));
    }

    [Fact(DisplayName = "D21 Retention purge removes old terminal work and keeps live and DLQ messages")]
    public async Task D21_Retention()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var empty = await CreateTopicAsync();
        await GrantPublishAsync(publisher, empty.TopicId);

        var oldCompleted = await PublishAsync(topic.Name, publisher);
        var oldDeadLetter = await PublishAsync(topic.Name, publisher);
        var recentDeadLetter = await PublishAsync(topic.Name, publisher);
        var stillPending = await PublishAsync(topic.Name, publisher);
        var oldNoSubs = await PublishAsync(empty.Name, publisher);
        var recentNoSubs = await PublishAsync(empty.Name, publisher);

        var leased = (await Deliveries.LeaseAsync(sub.SubscriptionId, 3, "Pull", subscriber)).ToDictionary(l => l.MessageId);
        await Deliveries.AckAsync(leased[oldCompleted.MessageId].DeliveryId, leased[oldCompleted.MessageId].LockToken, subscriber);
        foreach (var m in new[] { oldDeadLetter, recentDeadLetter })
            await Deliveries.NackAsync(leased[m.MessageId].DeliveryId, leased[m.MessageId].LockToken, subscriber, new FailureDetails("X", "x", DeadLetter: true));

        const int day = 86_400;
        await ExecAsync("UPDATE broker.Messages SET CreatedAt = DATEADD(day, -100, CreatedAt) WHERE MessageId IN (@m1, @m2, @m3, @m4, @m5)",
            P("m1", oldCompleted.MessageId), P("m2", oldDeadLetter.MessageId), P("m3", recentDeadLetter.MessageId),
            P("m4", stillPending.MessageId), P("m5", oldNoSubs.MessageId));
        await BackdateAsync("Deliveries", "DeliveryId", leased[oldCompleted.MessageId].DeliveryId, "CompletedAt", 15 * day);
        await BackdateAsync("DeadLetters", "DeliveryId", leased[oldDeadLetter.MessageId].DeliveryId, "DeadLetteredAt", 91 * day);
        await BackdateAsync("DeadLetters", "DeliveryId", leased[recentDeadLetter.MessageId].DeliveryId, "DeadLetteredAt", 30 * day);

        var result = await Operations.PurgeAsync(14, 90, batchSize: 1);

        Assert.Equal(2, result.DeliveriesDeleted);
        Assert.Equal(3, result.MessagesDeleted);
        var remaining = await QueryAsync<Guid>("SELECT MessageId FROM broker.Messages", r => Col<Guid>(r, 0));
        Assert.Equal(
            new[] { recentDeadLetter.MessageId, stillPending.MessageId, recentNoSubs.MessageId }.Order(),
            remaining.Order());
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.DeliveryAttempts WHERE DeliveryId IN (@d1, @d2)",
            P("d1", leased[oldCompleted.MessageId].DeliveryId), P("d2", leased[oldDeadLetter.MessageId].DeliveryId)));
        Assert.Single(await GetDeadLettersAsync(leased[recentDeadLetter.MessageId].DeliveryId));
    }
}
