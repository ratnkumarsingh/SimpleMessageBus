using System.Net;
using MessageBroker.Application.Persistence;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;

namespace MessageBroker.IntegrationTests.Api;

public sealed class MessageQueryApiTests(SqlServerFixture sql) : ApiTest(sql)
{
    [Fact(DisplayName = "A09 GET /messages/{id}: Admin and the publisher can read it, another publisher gets 403, unknown is 404")]
    public async Task A09_GetById()
    {
        var topic = await CreateTopicViaApiAsync();
        var (publisherId, _, publisher) = await CreateAppClientAsync();
        var (otherId, _, other) = await CreateAppClientAsync();
        await GrantViaApiAsync(publisherId, "Topic", topic.TopicId, "Publish");
        await GrantViaApiAsync(otherId, "Topic", topic.TopicId, "Publish");
        var subscriber = await CreateAppAsync();
        var subscription = await CreateSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: 1);

        var published = await ReadAsync<PublishResponse>(await PublishViaApiAsync(publisher, topic.Name), HttpStatusCode.Created);

        // One failed attempt, so the history has content.
        var leased = Assert.Single(await Deliveries.LeaseAsync(subscription.SubscriptionId, 1, "Pull", subscriber));
        await Deliveries.NackAsync(leased.DeliveryId, leased.LockToken, subscriber, new FailureDetails("Http500", "Server error", HttpStatusCode: 500));

        var message = await ReadAsync<MessageResponse>(await Admin.GetAsync($"/api/v1/messages/{published.MessageId}"));
        Assert.Equal((published.MessageId, topic.Name, "PaymentProcessed.v1", "PAY-12345", publisherId),
            (message.MessageId, message.TopicName, message.MessageType, message.CorrelationId, message.PublisherAppId));
        Assert.Equal(nameof(MessageStatus.DeadLettered), message.Status);
        Assert.Equal("PAY-12345", message.Payload.GetProperty("paymentId").GetString());
        Assert.Equal("in-01", message.Properties!.Value.GetProperty("tenant").GetString());

        var delivery = Assert.Single(message.Deliveries);
        Assert.Equal((subscription.SubscriptionId, "DeadLettered", "MaxAttemptsExceeded"),
            (delivery.SubscriptionId, delivery.Status, delivery.DeadLetterReason));
        var attempt = Assert.Single(delivery.Attempts);
        Assert.Equal((1, "Nacked", 500, "Http500"), (attempt.AttemptNumber, attempt.Outcome, attempt.HttpStatusCode!.Value, attempt.ErrorCode));

        Assert.Equal(published.MessageId, (await ReadAsync<MessageResponse>(await publisher.GetAsync($"/api/v1/messages/{published.MessageId}"))).MessageId);
        await ProblemAsync(await other.GetAsync($"/api/v1/messages/{published.MessageId}"), HttpStatusCode.Forbidden, ProblemTypes.Forbidden);
        await ProblemAsync(await Admin.GetAsync($"/api/v1/messages/{Guid.NewGuid()}"), HttpStatusCode.NotFound, ProblemTypes.NotFound);
    }

    [Fact(DisplayName = "A09b Correlation query is Admin only, returns every match and nothing else")]
    public async Task A09b_ByCorrelation()
    {
        var payments = await CreateTopicViaApiAsync();
        var invoices = await CreateTopicViaApiAsync();
        var (publisherId, _, publisher) = await CreateAppClientAsync();
        await GrantViaApiAsync(publisherId, "Topic", payments.TopicId, "Publish");
        await GrantViaApiAsync(publisherId, "Topic", invoices.TopicId, "Publish");

        object Body(string correlationId) => new { messageType = "Step.v1", correlationId, payload = new { } };
        var a = await ReadAsync<PublishResponse>(await PublishViaApiAsync(publisher, payments.Name, Body("ORDER-1")), HttpStatusCode.Created);
        var b = await ReadAsync<PublishResponse>(await PublishViaApiAsync(publisher, invoices.Name, Body("ORDER-1")), HttpStatusCode.Created);
        await PublishViaApiAsync(publisher, payments.Name, Body("ORDER-2"));

        var found = await ReadAsync<List<MessageSummaryResponse>>(await Admin.GetAsync("/api/v1/messages?correlationId=ORDER-1"));
        Assert.Equal([a.MessageId, b.MessageId], found.Select(m => m.MessageId));
        Assert.Equal([payments.Name, invoices.Name], found.Select(m => m.TopicName));

        Assert.Empty(await ReadAsync<List<MessageSummaryResponse>>(await Admin.GetAsync("/api/v1/messages?correlationId=ORDER-9")));
        await ProblemAsync(await Admin.GetAsync("/api/v1/messages"), HttpStatusCode.BadRequest, ProblemTypes.Validation);
        await ProblemAsync(await publisher.GetAsync("/api/v1/messages?correlationId=ORDER-1"), HttpStatusCode.Forbidden, ProblemTypes.Forbidden);
    }
}
