using System.Net;
using System.Net.Http.Json;
using System.Text;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using MessageBroker.IntegrationTests.Infrastructure;

namespace MessageBroker.IntegrationTests.Api;

public sealed class PublishApiTests(SqlServerFixture sql) : ApiTest(sql)
{
    private async Task<(TopicResponse Topic, HttpClient Publisher)> ArrangePublisherAsync(int subscriptions = 2)
    {
        var topic = await CreateTopicViaApiAsync();
        var (publisherId, _, publisher) = await CreateAppClientAsync();
        await GrantViaApiAsync(publisherId, "Topic", topic.TopicId, "Publish");
        var subscriber = await CreateAppAsync();
        for (var i = 0; i < subscriptions; i++)
            await CreateSubscriptionAsync(topic.TopicId, subscriber);
        return (topic, publisher);
    }

    [Fact(DisplayName = "A01 Publish is 201 with messageId and deliveryCount; same Idempotency-Key is 200 with the same id")]
    public async Task A01_PublishAndIdempotency()
    {
        var (topic, publisher) = await ArrangePublisherAsync(subscriptions: 2);

        var first = await PublishViaApiAsync(publisher, topic.Name, idempotencyKey: "outbox-000184223");
        var created = await ReadAsync<PublishResponse>(first, HttpStatusCode.Created);
        Assert.NotEqual(Guid.Empty, created.MessageId);
        Assert.Equal(2, created.DeliveryCount);
        Assert.EndsWith($"/api/v1/messages/{created.MessageId}", first.Headers.Location!.ToString());

        var again = await ReadAsync<PublishResponse>(
            await PublishViaApiAsync(publisher, topic.Name, idempotencyKey: "outbox-000184223"), HttpStatusCode.OK);
        Assert.Equal(created, again);
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Messages"));

        // Without a key every publish is a new message.
        var third = await ReadAsync<PublishResponse>(await PublishViaApiAsync(publisher, topic.Name), HttpStatusCode.Created);
        var fourth = await ReadAsync<PublishResponse>(await PublishViaApiAsync(publisher, topic.Name), HttpStatusCode.Created);
        Assert.NotEqual(third.MessageId, fourth.MessageId);
    }

    [Fact(DisplayName = "A01b Publish stores the envelope; correlationId defaults to the message id")]
    public async Task A01b_EnvelopeIsStored()
    {
        var (topic, publisher) = await ArrangePublisherAsync(subscriptions: 0);

        var created = await ReadAsync<PublishResponse>(await PublishViaApiAsync(publisher, topic.Name, new
        {
            messageType = "PaymentProcessed.v1",
            ttlSeconds = 600,
            payload = new { paymentId = "PAY-9" },
        }), HttpStatusCode.Created);
        Assert.Equal(0, created.DeliveryCount);

        var row = (await QueryAsync<(string CorrelationId, string Payload, string? Properties)>(
            "SELECT CorrelationId, Payload, Properties FROM broker.Messages WHERE MessageId = @id", r => (Col<string>(r, 0), Col<string>(r, 1), Col<string?>(r, 2)),
            P("id", created.MessageId))).Single();
        Assert.Equal(created.MessageId.ToString(), row.CorrelationId);
        Assert.Equal("""{"paymentId":"PAY-9"}""", row.Payload);
        Assert.Null(row.Properties);
    }

    [Fact(DisplayName = "A03 Problem details for 400 (validation and malformed JSON), 404, 409 and 413")]
    public async Task A03_ProblemDetails()
    {
        var (topic, publisher) = await ArrangePublisherAsync(subscriptions: 0);

        // 400: envelope validation, with field errors.
        var invalid = await ProblemAsync(await PublishViaApiAsync(publisher, topic.Name,
                new { payload = new { a = 1 }, properties = new { n = 5 } }),
            HttpStatusCode.BadRequest, ProblemTypes.Validation);
        var errors = invalid.GetProperty("errors");
        Assert.True(errors.TryGetProperty("messageType", out _));
        Assert.True(errors.TryGetProperty("properties", out _));

        // 400: body that is not JSON.
        var malformed = await publisher.PostAsync($"/api/v1/topics/{topic.Name}/messages",
            new StringContent("{ not json", Encoding.UTF8, "application/json"));
        await ProblemAsync(malformed, HttpStatusCode.BadRequest, ProblemTypes.Validation);

        // 404: unknown topic.
        var missing = await ProblemAsync(await PublishViaApiAsync(publisher, "no-such-topic"), HttpStatusCode.NotFound, ProblemTypes.NotFound);
        Assert.Contains("Topic", missing.GetProperty("detail").GetString());

        // 409: duplicate topic name, case-insensitive.
        await ProblemAsync(await Admin.PostAsJsonAsync("/api/v1/topics", new CreateTopicRequest(topic.Name.ToUpperInvariant(), null)),
            HttpStatusCode.Conflict, ProblemTypes.Conflict);

        // 413: payload over 256 KB.
        var big = new string('x', 262_144);
        await ProblemAsync(await PublishViaApiAsync(publisher, topic.Name, new { messageType = "Big.v1", payload = new { big } }),
            (HttpStatusCode)413, ProblemTypes.PayloadTooLarge);
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Messages"));
    }
}
