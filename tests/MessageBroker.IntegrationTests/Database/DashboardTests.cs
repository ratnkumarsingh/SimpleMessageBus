using MessageBroker.Application.Persistence;
using MessageBroker.Domain;
using MessageBroker.Infrastructure.Data;
using MessageBroker.IntegrationTests.Infrastructure;

namespace MessageBroker.IntegrationTests.Database;

/// <summary>D31–D39: the admin dashboard procedures (Dashboard.sql) and the MessageId the settle procedures return.</summary>
public class DashboardTests(SqlServerFixture sql) : DatabaseTest(sql)
{
    private DashboardRepository Dashboard => new(Db);

    private Task SetStatusAsync(Guid subscriptionId, Guid messageId, DeliveryStatus status) =>
        ExecAsync("UPDATE broker.Deliveries SET Status = @status WHERE SubscriptionId = @subscriptionId AND MessageId = @messageId",
            new { status = (byte)status, subscriptionId, messageId });

    /// <summary>Leases the one due delivery of a pull subscription.</summary>
    private async Task<LeasedDeliveryRecord> LeaseOneAsync(Guid subscriptionId, Guid subscriber) =>
        Assert.Single(await Deliveries.LeaseAsync(subscriptionId, 1, "Pull", subscriber));

    [Fact(DisplayName = "D31 usp_Admin_GetOverview totals and a zero-filled per-minute series match the data")]
    public async Task D31_Overview()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: 1);
        for (var i = 0; i < 4; i++)
            await PublishAsync(topic.Name, publisher);

        // One completed, one dead-lettered by NACK (one failed attempt), one leased, one pending.
        var acked = await LeaseOneAsync(sub.SubscriptionId, subscriber);
        await Deliveries.AckAsync(acked.DeliveryId, acked.LockToken, subscriber);
        var nacked = await LeaseOneAsync(sub.SubscriptionId, subscriber);
        await Deliveries.NackAsync(nacked.DeliveryId, nacked.LockToken, subscriber, new FailureDetails("E", "fail"));
        await LeaseOneAsync(sub.SubscriptionId, subscriber);

        // A message from two hours ago is outside the window.
        var old = await PublishAsync(topic.Name, publisher);
        await ExecAsync("UPDATE broker.Messages SET CreatedAt = DATEADD(hour, -2, CreatedAt) WHERE MessageId = @id", new { id = old.MessageId });

        var overview = await Dashboard.GetOverviewAsync(60);

        Assert.Equal(2, overview.Totals.PendingCount);       // the untouched one and the old one
        Assert.Equal(1, overview.Totals.LeasedCount);
        Assert.Equal(1, overview.Totals.DeadLetteredCount);
        Assert.Equal(4, overview.Totals.PublishedInWindow);
        Assert.Equal(1, overview.Totals.CompletedInWindow);
        Assert.Equal(60, overview.Totals.WindowMinutes);

        Assert.Equal(60, overview.Series.Count);
        Assert.All(overview.Series.Zip(overview.Series.Skip(1)), p => Assert.Equal(TimeSpan.FromMinutes(1), p.Second.Minute - p.First.Minute));
        Assert.Equal(0, overview.Series[0].Minute.Second);
        Assert.Equal(4, overview.Series.Sum(p => p.Published));
        Assert.Equal(1, overview.Series.Sum(p => p.Completed));
        Assert.Equal(1, overview.Series.Sum(p => p.Failed));
        Assert.Equal(1, overview.Series.Sum(p => p.DeadLettered));
        Assert.True(overview.Series.Count(p => p.Published == 0) >= 58); // zero-filled

        var row = Assert.Single(overview.Subscriptions);
        Assert.Equal(sub.SubscriptionId, row.SubscriptionId);
        Assert.Equal(topic.Name, row.TopicName);
        Assert.Equal((2, 1, 1), (row.PendingCount, row.LeasedCount, row.DeadLetteredCount));
    }

    [Fact(DisplayName = "D31b Overview rejects a window outside 5–1440 minutes")]
    public async Task D31b_OverviewWindow()
    {
        await ThrowsBrokerAsync(BrokerErrorKind.Validation, () => Dashboard.GetOverviewAsync(4));
        await ThrowsBrokerAsync(BrokerErrorKind.Validation, () => Dashboard.GetOverviewAsync(1441));
        Assert.Equal(1440, (await Dashboard.GetOverviewAsync(1440)).Series.Count);
    }

    [Fact(DisplayName = "D32 Overview subscription rows carry no webhook URL or secret columns, and skip deleted subscriptions")]
    public async Task D32_OverviewHasNoSecrets()
    {
        var (topic, _, subscriber) = await ArrangeTopicAsync();
        await CreateSubscriptionAsync(topic.TopicId, subscriber, mode: "Webhook", protectedSecret: "protected-secret");
        var deleted = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        await Subscriptions.DeleteAsync(deleted.SubscriptionId);

        var overview = await Dashboard.GetOverviewAsync(60);
        Assert.Single(overview.Subscriptions);
        Assert.DoesNotContain(typeof(SubscriptionHealthRecord).GetProperties(), p => p.Name.Contains("Secret") || p.Name.Contains("Webhook"));

        // The procedure selects explicit columns, none of them secret.
        var text = await ScalarAsync<string>("SELECT OBJECT_DEFINITION(OBJECT_ID('broker.usp_Admin_GetOverview'))");
        Assert.DoesNotContain("Secret", text);
        Assert.DoesNotContain("WebhookUrl", text);
    }

    [Fact(DisplayName = "D33 Message search filters by topic, type, correlation, publisher and time range")]
    public async Task D33_SearchFilters()
    {
        var (topicA, publisherA, subscriber) = await ArrangeTopicAsync();
        var (topicB, publisherB, _) = await ArrangeTopicAsync();
        await CreateSubscriptionAsync(topicA.TopicId, subscriber);
        var a1 = await PublishAsync(topicA.Name, publisherA, correlationId: "ORDER-1");
        var a2 = await PublishAsync(topicA.Name, publisherA, correlationId: "ORDER-2");
        var b1 = await PublishAsync(topicB.Name, publisherB, correlationId: "ORDER-1");
        await ExecAsync("UPDATE broker.Messages SET MessageType = 'Refund.v1' WHERE MessageId = @id", new { id = a2.MessageId });
        await ExecAsync("UPDATE broker.Messages SET CreatedAt = DATEADD(day, -1, CreatedAt) WHERE MessageId = @id", new { id = a1.MessageId });

        async Task<Guid[]> Ids(MessageSearchQuery q) => (await Dashboard.SearchMessagesAsync(q)).Select(m => m.MessageId).ToArray();

        Assert.Equal([b1.MessageId, a2.MessageId, a1.MessageId], await Ids(new()));
        Assert.Equal([a2.MessageId, a1.MessageId], await Ids(new(TopicId: topicA.TopicId)));
        Assert.Equal([a2.MessageId], await Ids(new(MessageType: "Refund.v1")));
        Assert.Equal([b1.MessageId, a1.MessageId], await Ids(new(CorrelationId: "ORDER-1")));
        Assert.Equal([b1.MessageId], await Ids(new(PublisherAppId: publisherB)));
        var now = await DbNowAsync();
        Assert.Equal([b1.MessageId, a2.MessageId], await Ids(new(From: now.AddHours(-1))));
        Assert.Equal([a1.MessageId], await Ids(new(To: now.AddHours(-1))));

        var row = (await Dashboard.SearchMessagesAsync(new(CorrelationId: "ORDER-2"))).Single();
        Assert.Equal(topicA.Name, row.TopicName);
        Assert.Equal(publisherA, row.PublisherAppId);
        Assert.False(string.IsNullOrEmpty(row.PublisherName));
        Assert.Equal(("InProgress", 1, 1), (row.Status, row.DeliveryCount, row.PendingCount));
        // Topic B has no subscription: no deliveries, so Completed.
        Assert.Equal("Completed", (await Dashboard.SearchMessagesAsync(new(TopicId: topicB.TopicId))).Single().Status);
    }

    [Fact(DisplayName = "D34 Message search status matches vw_MessageStatus for every delivery-status combination")]
    public async Task D34_SearchStatusMatchesView()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var a = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var b = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var m = (await PublishAsync(topic.Name, publisher)).MessageId;

        var statuses = new[] { DeliveryStatus.Pending, DeliveryStatus.Leased, DeliveryStatus.Completed, DeliveryStatus.DeadLettered, DeliveryStatus.Cancelled };
        foreach (var sa in statuses)
        foreach (var sb in statuses)
        {
            await SetStatusAsync(a.SubscriptionId, m, sa);
            await SetStatusAsync(b.SubscriptionId, m, sb);
            var expected = await ScalarAsync<string>("SELECT Status FROM broker.vw_MessageStatus WHERE MessageId = @m", new { m });

            Assert.Equal(expected, (await Dashboard.SearchMessagesAsync(new())).Single().Status);
            Assert.Single(await Dashboard.SearchMessagesAsync(new(Status: expected)));
            foreach (var other in new[] { "InProgress", "Completed", "PartiallyDeadLettered", "DeadLettered" }.Where(s => s != expected))
                Assert.Empty(await Dashboard.SearchMessagesAsync(new(Status: other)));
        }
    }

    [Fact(DisplayName = "D35 Keyset paging returns every message exactly once, newest first")]
    public async Task D35_Paging()
    {
        var (topic, publisher, _) = await ArrangeTopicAsync();
        var published = new List<Guid>();
        for (var i = 0; i < 12; i++)
            published.Add((await PublishAsync(topic.Name, publisher)).MessageId);

        var seen = new List<MessageSearchRecord>();
        long? cursor = null;
        while (true)
        {
            var page = await Dashboard.SearchMessagesAsync(new(BeforeSeq: cursor, PageSize: 5));
            if (page.Count == 0)
                break;
            Assert.True(page.Count <= 5);
            seen.AddRange(page);
            cursor = page[^1].MessageSeq;
        }

        Assert.Equal(Enumerable.Reverse(published), seen.Select(m => m.MessageId));
        Assert.Equal(seen.Select(m => m.MessageSeq).OrderDescending(), seen.Select(m => m.MessageSeq));
    }

    [Fact(DisplayName = "D36 Search validation: page size 1–200, from not after to, known status and reason")]
    public async Task D36_SearchValidation()
    {
        var now = await DbNowAsync();
        await ThrowsBrokerAsync(BrokerErrorKind.Validation, () => Dashboard.SearchMessagesAsync(new(PageSize: 0)));
        await ThrowsBrokerAsync(BrokerErrorKind.Validation, () => Dashboard.SearchMessagesAsync(new(PageSize: 201)));
        await ThrowsBrokerAsync(BrokerErrorKind.Validation, () => Dashboard.SearchMessagesAsync(new(From: now, To: now.AddSeconds(-1))));
        await ThrowsBrokerAsync(BrokerErrorKind.Validation, () => Dashboard.SearchMessagesAsync(new(Status: "Stuck")));
        await ThrowsBrokerAsync(BrokerErrorKind.Validation, () => Dashboard.SearchDeadLettersAsync(new(PageSize: 0)));
        await ThrowsBrokerAsync(BrokerErrorKind.Validation, () => Dashboard.SearchDeadLettersAsync(new(From: now, To: now.AddSeconds(-1))));
        await ThrowsBrokerAsync(BrokerErrorKind.Validation, () => Dashboard.SearchDeadLettersAsync(new(Reason: "Bored")));
        Assert.Empty(await Dashboard.SearchMessagesAsync(new(PageSize: 200)));
    }

    [Fact(DisplayName = "D37 Dead-letter search filters, pages and hides requeued entries unless asked")]
    public async Task D37_DeadLetterSearch()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var admin = await CreateAppAsync(isAdmin: true);
        var subA = await CreateSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: 1);
        var subB = await CreateSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: 1);
        for (var i = 0; i < 3; i++)
            await PublishAsync(topic.Name, publisher);

        // Every delivery dead-lettered: on A by exhausting attempts, on B by rejection.
        for (var i = 0; i < 3; i++)
        {
            var la = await LeaseOneAsync(subA.SubscriptionId, subscriber);
            await Deliveries.NackAsync(la.DeliveryId, la.LockToken, subscriber, new FailureDetails("Http500", "boom"));
            var lb = await LeaseOneAsync(subB.SubscriptionId, subscriber);
            await Deliveries.NackAsync(lb.DeliveryId, lb.LockToken, subscriber, new FailureDetails("Bad", "no", DeadLetter: true));
        }

        var all = await Dashboard.SearchDeadLettersAsync(new());
        Assert.Equal(6, all.Count);
        Assert.Equal(all.Select(d => d.DeadLetterId).OrderDescending(), all.Select(d => d.DeadLetterId));
        Assert.All(all, d => Assert.Equal("", d.Payload)); // the payload is not returned
        Assert.Equal(3, (await Dashboard.SearchDeadLettersAsync(new(SubscriptionId: subA.SubscriptionId))).Count);
        Assert.All(await Dashboard.SearchDeadLettersAsync(new(Reason: "RejectedBySubscriber")), d => Assert.Equal(subB.SubscriptionId, d.SubscriptionId));
        Assert.Equal(6, (await Dashboard.SearchDeadLettersAsync(new(TopicId: topic.TopicId))).Count);
        Assert.Empty(await Dashboard.SearchDeadLettersAsync(new(TopicId: Guid.NewGuid())));

        var page1 = await Dashboard.SearchDeadLettersAsync(new(PageSize: 4));
        var page2 = await Dashboard.SearchDeadLettersAsync(new(PageSize: 4, BeforeId: page1[^1].DeadLetterId));
        Assert.Equal(all.Select(d => d.DeadLetterId), page1.Concat(page2).Select(d => d.DeadLetterId));

        await Deliveries.RequeueAsync(all[0].DeliveryId, admin);
        Assert.Equal(5, (await Dashboard.SearchDeadLettersAsync(new())).Count);
        var withRequeued = await Dashboard.SearchDeadLettersAsync(new(IncludeRequeued: true));
        Assert.Equal(6, withRequeued.Count);
        Assert.Equal(admin, withRequeued.Single(d => d.RequeuedAt is not null).RequeuedBy);
    }

    [Fact(DisplayName = "D38 Ack, Nack and Requeue return the delivery's MessageId")]
    public async Task D38_SettleReturnsMessageId()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var admin = await CreateAppAsync(isAdmin: true);
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var first = await PublishAsync(topic.Name, publisher);
        var second = await PublishAsync(topic.Name, publisher);

        var l1 = await LeaseOneAsync(sub.SubscriptionId, subscriber);
        Assert.Equal(first.MessageId, await Deliveries.AckAsync(l1.DeliveryId, l1.LockToken, subscriber));

        var l2 = await LeaseOneAsync(sub.SubscriptionId, subscriber);
        var nack = await Deliveries.NackAsync(l2.DeliveryId, l2.LockToken, subscriber, new FailureDetails("E", "x", DeadLetter: true));
        Assert.Equal(second.MessageId, nack.MessageId);
        Assert.True(nack.DeadLettered);

        Assert.Equal(second.MessageId, await Deliveries.RequeueAsync(l2.DeliveryId, admin));
    }

    [Fact(DisplayName = "D40 Dead letters of deleted subscriptions are left out of the overview total and the DLQ search")]
    public async Task D40_DeletedSubscriptionDeadLetters()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var kept = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var deleted = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        await PublishAsync(topic.Name, publisher);
        foreach (var sub in new[] { kept, deleted })
        {
            var leased = await LeaseOneAsync(sub.SubscriptionId, subscriber);
            await Deliveries.NackAsync(leased.DeliveryId, leased.LockToken, subscriber, new FailureDetails("E", "x", DeadLetter: true));
        }
        Assert.Equal(2, (await Dashboard.GetOverviewAsync(60)).Totals.DeadLetteredCount);

        await Subscriptions.DeleteAsync(deleted.SubscriptionId);

        Assert.Equal(1, (await Dashboard.GetOverviewAsync(60)).Totals.DeadLetteredCount);
        Assert.Equal(kept.SubscriptionId, Assert.Single(await Dashboard.SearchDeadLettersAsync(new())).SubscriptionId);
        Assert.Empty(await Dashboard.SearchDeadLettersAsync(new(SubscriptionId: deleted.SubscriptionId, IncludeRequeued: true)));
        // The history is kept: the rows are still in the table.
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.DeadLetters"));
    }

    [Fact(DisplayName = "D39 Migration 0003 creates the dashboard indexes")]
    public async Task D39_Indexes()
    {
        var indexes = await QueryAsync<string>("SELECT name FROM sys.indexes WHERE object_id IN " +
            "(OBJECT_ID('broker.Messages'), OBJECT_ID('broker.DeadLetters'), OBJECT_ID('broker.DeliveryAttempts'))");
        Assert.Contains("IX_Messages_Topic", indexes);
        Assert.Contains("IX_DeadLetters_DeadLetteredAt", indexes);
        Assert.Contains("IX_DeliveryAttempts_EndedAt", indexes);
        Assert.Contains(SchemaDeployer.ReadAllScripts(), s => s.Name.EndsWith("0003_DashboardIndexes.sql"));
    }
}
