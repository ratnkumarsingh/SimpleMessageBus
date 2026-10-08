using System.Net;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;

namespace MessageBroker.IntegrationTests.Api;

public sealed class ContractsClientTests(SqlServerFixture sql) : ApiTest(sql)
{
    [Fact(DisplayName = "K01 Contracts BrokerClient: publish (with duplicate detection), receive, ack, nack, trace, and typed errors")]
    public async Task K01_BrokerClient()
    {
        var topic = await CreateTopicViaApiAsync();
        var (publisherId, publisherKey, _) = await CreateAppClientAsync();
        await GrantViaApiAsync(publisherId, "Topic", topic.TopicId, "Publish");
        var (subscriberId, subscriberKey, _) = await CreateAppClientAsync();
        var subscription = await CreateSubscriptionAsync(topic.TopicId, subscriberId, mode: "Pull");

        var publisher = BrokerClient.Create(Api.CreateClient(), publisherKey);
        var subscriber = BrokerClient.Create(Api.CreateClient(), subscriberKey);
        var request = new PublishRequest
        {
            MessageType = "PaymentProcessed.v1",
            CorrelationId = "PAY-K01",
            Payload = System.Text.Json.JsonDocument.Parse("""{"paymentId":"PAY-K01"}""").RootElement,
        };

        var first = await publisher.PublishAsync(topic.Name, request, "outbox-1");
        Assert.Equal((1, false), (first.DeliveryCount, first.IsDuplicate));
        var again = await publisher.PublishAsync(topic.Name, request, "outbox-1");
        Assert.Equal((first.MessageId, true), (again.MessageId, again.IsDuplicate));
        await publisher.PublishAsync(topic.Name, request, "outbox-2");

        var received = await subscriber.ReceiveAsync(subscription.SubscriptionId, maxMessages: 5);
        Assert.Equal(2, received.Count);
        Assert.Equal(first.MessageId, received[0].Message.MessageId);

        await subscriber.RenewAsync(received[0]);
        await subscriber.AckAsync(received[0]);
        await subscriber.NackAsync(received[1], "Busy", "try later");

        var lost = await Assert.ThrowsAsync<BrokerApiException>(() => subscriber.AckAsync(received[0]));
        Assert.True(lost.IsLeaseLost);
        Assert.Equal(ProblemTypes.LeaseLost, lost.ProblemType);

        var message = await publisher.GetMessageAsync(first.MessageId);
        Assert.Equal(nameof(DeliveryStatus.Completed), Assert.Single(message.Deliveries).Status);

        var missing = await Assert.ThrowsAsync<BrokerApiException>(() => publisher.PublishAsync("no-such-topic", request));
        Assert.Equal((HttpStatusCode.NotFound, ProblemTypes.NotFound, false), (missing.StatusCode, missing.ProblemType, missing.IsTransient));
        var forbidden = await Assert.ThrowsAsync<BrokerApiException>(() => subscriber.PublishAsync(topic.Name, request));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact(DisplayName = "K02 Contracts BrokerClient admin reads: overview, message and DLQ search, requeue, topology")]
    public async Task K02_AdminReads()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var subscription = await CreateSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: 1);
        var messageId = (await PublishAsync(topic.Name, publisher, correlationId: "K02")).MessageId;
        var leased = Assert.Single(await Deliveries.LeaseAsync(subscription.SubscriptionId, 1, "Pull", subscriber));
        await Deliveries.NackAsync(leased.DeliveryId, leased.LockToken, subscriber, new("E", "fail"));
        var admin = BrokerClient.Create(Api.CreateClient(), ApiFactory.AdminKey);

        var overview = await admin.GetOverviewAsync(30);
        Assert.Equal((30, 1L), (overview.Totals.WindowMinutes, overview.Totals.DeadLettered));

        var search = await admin.SearchMessagesAsync(new MessageSearchRequest
        {
            TopicId = topic.TopicId, Status = "DeadLettered", CorrelationId = "K02",
            From = DateTime.UtcNow.AddHours(-1), To = DateTime.UtcNow.AddHours(1), PageSize = 10,
        });
        Assert.Equal(messageId, Assert.Single(search.Items).MessageId);
        Assert.Null(search.NextCursor);
        Assert.Equal(messageId, Assert.Single(await admin.ListMessagesByCorrelationAsync("K02")).MessageId);

        var dlq = await admin.SearchDeadLettersAsync(new DeadLetterSearchRequest { SubscriptionId = subscription.SubscriptionId, Reason = "MaxAttemptsExceeded" });
        Assert.Equal(leased.DeliveryId, Assert.Single(dlq.Items).DeliveryId);
        await admin.RequeueDeadLetterAsync(leased.DeliveryId);
        Assert.Empty((await admin.SearchDeadLettersAsync(new DeadLetterSearchRequest())).Items);
        Assert.Single((await admin.SearchDeadLettersAsync(new DeadLetterSearchRequest { IncludeRequeued = true })).Items);

        Assert.Contains(await admin.ListTopicsAsync(), t => t.TopicId == topic.TopicId);
        Assert.Equal(subscription.SubscriptionId, Assert.Single(await admin.ListSubscriptionsAsync(topic.TopicId)).SubscriptionId);
        Assert.Contains(await admin.ListApplicationsAsync(), a => a.AppId == publisher);
        Assert.Contains(await admin.ListPermissionsAsync(publisher), p => p.ResourceId == topic.TopicId && p.Permission == "Publish");

        var error = await Assert.ThrowsAsync<BrokerApiException>(() => admin.SearchMessagesAsync(new MessageSearchRequest { PageSize = 0 }));
        Assert.Equal((HttpStatusCode.BadRequest, ProblemTypes.Validation), (error.StatusCode, error.ProblemType));
    }
}
