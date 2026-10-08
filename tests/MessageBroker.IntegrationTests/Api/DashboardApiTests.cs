using System.Net;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using MessageBroker.IntegrationTests.Infrastructure;

namespace MessageBroker.IntegrationTests.Api;

/// <summary>A12–A16: the admin dashboard endpoints.</summary>
public sealed class DashboardApiTests(SqlServerFixture sql) : ApiTest(sql)
{
    private static readonly string[] Endpoints = ["/api/v1/admin/overview", "/api/v1/admin/messages", "/api/v1/admin/deadletters"];

    [Fact(DisplayName = "A12 Dashboard endpoints: Admin only (403), authenticated only (401)")]
    public async Task A12_AdminOnly()
    {
        var (_, _, client) = await CreateAppClientAsync();
        var anonymous = Api.CreateClient(null);
        foreach (var url in Endpoints)
        {
            await ProblemAsync(await client.GetAsync(url), HttpStatusCode.Forbidden, ProblemTypes.Forbidden);
            await ExpectAsync(anonymous.GetAsync(url), HttpStatusCode.Unauthorized);
            await ExpectAsync(Admin.GetAsync(url), HttpStatusCode.OK);
        }
    }

    [Fact(DisplayName = "A13 Overview reflects a publish and an ack, with subscription health")]
    public async Task A13_Overview()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var signalR = await CreateSubscriptionAsync(topic.TopicId, subscriber, mode: "SignalR");
        var webhook = await CreateSubscriptionAsync(topic.TopicId, subscriber, mode: "Webhook");
        await PublishAsync(topic.Name, publisher);
        await PublishAsync(topic.Name, publisher);
        var leased = Assert.Single(await Deliveries.LeaseAsync(sub.SubscriptionId, 1, "Pull", subscriber));
        await Deliveries.AckAsync(leased.DeliveryId, leased.LockToken, subscriber);

        var overview = await ReadAsync<OverviewResponse>(await Admin.GetAsync("/api/v1/admin/overview?windowMinutes=15"));

        Assert.Equal(15, overview.Totals.WindowMinutes);
        Assert.Equal(15, overview.Series.Count);
        Assert.Equal((5L, 0L, 2L, 1L), (overview.Totals.Pending, overview.Totals.Leased, overview.Totals.PublishedInWindow, overview.Totals.CompletedInWindow));
        Assert.Equal(2, overview.Series.Sum(p => p.Published));

        var pull = overview.Subscriptions.Single(s => s.SubscriptionId == sub.SubscriptionId);
        Assert.Equal((1, 0, null, null), (pull.Pending, pull.Leased, pull.ConnectedClients, pull.CircuitState));
        Assert.Equal(0, overview.Subscriptions.Single(s => s.SubscriptionId == signalR.SubscriptionId).ConnectedClients);
        Assert.Equal("Closed", overview.Subscriptions.Single(s => s.SubscriptionId == webhook.SubscriptionId).CircuitState);
        Assert.Equal(topic.Name, pull.TopicName);
    }

    [Fact(DisplayName = "A14 Message search over HTTP: query-string filters and the cursor round trip")]
    public async Task A14_MessageSearch()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add((await PublishAsync(topic.Name, publisher, correlationId: i < 3 ? "ORDER-7" : null)).MessageId);

        var page1 = await ReadAsync<MessageSearchResponse>(await Admin.GetAsync($"/api/v1/admin/messages?topicId={topic.TopicId}&pageSize=3"));
        Assert.Equal([ids[4], ids[3], ids[2]], page1.Items.Select(m => m.MessageId));
        Assert.NotNull(page1.NextCursor);
        var page2 = await ReadAsync<MessageSearchResponse>(await Admin.GetAsync($"/api/v1/admin/messages?topicId={topic.TopicId}&pageSize=3&cursor={page1.NextCursor}"));
        Assert.Equal([ids[1], ids[0]], page2.Items.Select(m => m.MessageId));
        Assert.Null(page2.NextCursor);

        var byCorrelation = await ReadAsync<MessageSearchResponse>(await Admin.GetAsync("/api/v1/admin/messages?correlationId=ORDER-7&status=inprogress"));
        Assert.Equal([ids[2], ids[1], ids[0]], byCorrelation.Items.Select(m => m.MessageId));
        var item = byCorrelation.Items[0];
        Assert.Equal(("InProgress", 1, 1, topic.Name), (item.Status, item.DeliveryCount, item.Pending, item.TopicName));
        Assert.False(string.IsNullOrEmpty(item.PublisherName));

        // Times with an offset are compared in UTC.
        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToOffset(TimeSpan.FromHours(5.5)).ToString("o"));
        Assert.Empty((await ReadAsync<MessageSearchResponse>(await Admin.GetAsync($"/api/v1/admin/messages?from={future}"))).Items);
        Assert.Equal(5, (await ReadAsync<MessageSearchResponse>(await Admin.GetAsync($"/api/v1/admin/messages?to={future}"))).Items.Count);
    }

    [Theory(DisplayName = "A15 Bad dashboard parameters are 400 problems naming the field")]
    [InlineData("/api/v1/admin/overview?windowMinutes=2", "windowMinutes")]
    [InlineData("/api/v1/admin/messages?pageSize=0", "pageSize")]
    [InlineData("/api/v1/admin/messages?pageSize=201", "pageSize")]
    [InlineData("/api/v1/admin/messages?status=Stuck", "status")]
    [InlineData("/api/v1/admin/messages?from=2026-10-08T12:00:00Z&to=2026-10-08T11:00:00Z", "from")]
    [InlineData("/api/v1/admin/deadletters?reason=Bored", "reason")]
    [InlineData("/api/v1/admin/deadletters?pageSize=500", "pageSize")]
    public async Task A15_Validation(string url, string field)
    {
        var problem = await ProblemAsync(await Admin.GetAsync(url), HttpStatusCode.BadRequest, ProblemTypes.Validation);
        Assert.Contains(field, problem.GetProperty("errors").EnumerateObject().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "A16 Broker-wide DLQ search, requeue, and the entry leaves the default list")]
    public async Task A16_DeadLetters()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: 1);
        for (var i = 0; i < 2; i++)
        {
            await PublishAsync(topic.Name, publisher);
            var leased = Assert.Single(await Deliveries.LeaseAsync(sub.SubscriptionId, 1, "Pull", subscriber));
            await Deliveries.NackAsync(leased.DeliveryId, leased.LockToken, subscriber, new("Http500", $"failure {i}"));
        }

        var page = await ReadAsync<DeadLetterPage>(await Admin.GetAsync($"/api/v1/admin/deadletters?topicId={topic.TopicId}&reason=maxattemptsexceeded"));
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(("failure 1", sub.Name), (page.Items[0].LastError, page.Items[0].SubscriptionName));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, page.Items[0].Payload.ValueKind); // payloads are left out

        await ExpectAsync(Admin.PostAsync($"/api/v1/deadletters/{page.Items[0].DeliveryId}/requeue", null), HttpStatusCode.NoContent);

        var after = await ReadAsync<DeadLetterPage>(await Admin.GetAsync("/api/v1/admin/deadletters"));
        Assert.Equal([page.Items[1].DeliveryId], after.Items.Select(d => d.DeliveryId));
        var history = await ReadAsync<DeadLetterPage>(await Admin.GetAsync("/api/v1/admin/deadletters?includeRequeued=true&pageSize=1"));
        Assert.Single(history.Items);
        Assert.NotNull(history.NextBefore);
    }
}
