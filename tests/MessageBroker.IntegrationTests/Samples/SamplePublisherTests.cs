using System.Net;
using MessageBroker.Contracts.Client;
using MessageBroker.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using SamplePublisher;
using Samples.Shared;

namespace MessageBroker.IntegrationTests.Samples;

/// <summary>Stands in for the network between the publisher and the broker.</summary>
public sealed class FlakyNetwork(HttpMessageHandler broker) : DelegatingHandler(broker)
{
    public enum State { Up, Unavailable, Unreachable, ResponseLost }

    public State Mode { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        switch (Mode)
        {
            case State.Unavailable:
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            case State.Unreachable:
                throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
            case State.ResponseLost:
                // The broker commits, then the connection drops before the answer arrives.
                (await base.SendAsync(request, ct)).Dispose();
                throw new HttpRequestException("The response ended prematurely.");
            default:
                return await base.SendAsync(request, ct);
        }
    }
}

public sealed class SamplePublisherTests(SqlServerFixture sql) : ApiTest(sql)
{
    private sealed record OutboxState(long OutboxId, int Attempts, string? LastError, DateTime? SentAt, Guid? MessageId, DateTime? RejectedAt);

    private Task<IReadOnlyList<OutboxState>> OutboxAsync() =>
        QueryAsync<OutboxState>("SELECT OutboxId, Attempts, LastError, SentAt, MessageId, RejectedAt FROM sample.Outbox ORDER BY OutboxId");

    private Task<IReadOnlyList<(string Key, Guid MessageId)>> BrokerMessagesAsync() =>
        QueryAsync<(string, Guid)>("SELECT IdempotencyKey, MessageId FROM broker.Messages ORDER BY IdempotencyKey");

    [Fact(DisplayName = "S01 Sample publisher: outbox rows written during a broker outage are sent afterwards with the same key, with no duplicates")]
    public async Task S01_OutboxSurvivesOutage()
    {
        var topic = await CreateTopicViaApiAsync();
        var (publisherId, publisherKey, _) = await CreateAppClientAsync();
        await GrantViaApiAsync(publisherId, "Topic", topic.TopicId, "Publish");
        var (subscriberId, _, _) = await CreateAppClientAsync();
        await CreateSubscriptionAsync(topic.TopicId, subscriberId, mode: "Pull");

        var outbox = new OutboxStore(new SampleDatabase(Sql.ConnectionString));
        var network = new FlakyNetwork(Api.Server.CreateHandler());
        var broker = BrokerClient.Create(new HttpClient(network) { BaseAddress = Api.Server.BaseAddress }, publisherKey);
        var relay = new OutboxRelay(outbox, broker, NullLogger<OutboxRelay>.Instance, TimeProvider.System);
        Task<RelayPass> PassAsync() => relay.RunOnceAsync(CancellationToken.None);

        // The business transaction works while the broker is down: payment and outbox row commit together.
        var ids = new List<long>();
        for (var i = 1; i <= 3; i++)
            ids.Add(await outbox.RecordPaymentAsync($"PAY-S01-{i}", 100 + i, "INR", topic.Name));
        Assert.Equal(3, await ScalarAsync<int>("SELECT COUNT(*) FROM sample.Payments"));

        // Broker answering 503, then unreachable: the pass stops at the first row and keeps order.
        network.Mode = FlakyNetwork.State.Unavailable;
        Assert.Equal(new RelayPass(0, 0, Faulted: true), await PassAsync());
        network.Mode = FlakyNetwork.State.Unreachable;
        Assert.Equal(new RelayPass(0, 0, Faulted: true), await PassAsync());
        var rows = await OutboxAsync();
        Assert.Equal([2, 0, 0], rows.Select(r => r.Attempts));
        Assert.Contains("refused", rows[0].LastError);
        Assert.All(rows, r => Assert.Null(r.SentAt));
        Assert.Empty(await BrokerMessagesAsync());

        // The broker accepts the first row but the answer is lost: still pending on the publisher side.
        network.Mode = FlakyNetwork.State.ResponseLost;
        Assert.Equal(new RelayPass(0, 0, Faulted: true), await PassAsync());
        var accepted = Assert.Single(await BrokerMessagesAsync());
        Assert.Equal(OutboxRelay.IdempotencyKey(ids[0]), accepted.Key);
        Assert.Null((await OutboxAsync())[0].SentAt);

        // Back up: every row is sent once. The first resolves to the message the broker already holds.
        network.Mode = FlakyNetwork.State.Up;
        Assert.Equal(new RelayPass(3, 0, Faulted: false), await PassAsync());
        Assert.Equal(new RelayPass(0, 0, Faulted: false), await PassAsync());

        var messages = await BrokerMessagesAsync();
        Assert.Equal(ids.Select(OutboxRelay.IdempotencyKey), messages.Select(m => m.Key));
        rows = await OutboxAsync();
        Assert.Equal(messages.Select(m => (Guid?)m.MessageId), rows.Select(r => r.MessageId));
        Assert.Equal(accepted.MessageId, rows[0].MessageId);
        Assert.Equal([4, 1, 1], rows.Select(r => r.Attempts));
        Assert.All(rows, r => Assert.Null(r.LastError));
        Assert.Equal(3, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries"));

        // A row the broker refuses (a blank message type is a 400) is set aside and does not hold up the rows behind it.
        await ExecAsync("""
            INSERT sample.Outbox (TopicName, MessageType, CorrelationId, Payload)
            VALUES (@topic, N'   ', N'PAY-BAD', N'{"paymentId":"PAY-BAD","amount":1}')
            """, new { topic = topic.Name });
        var after = await outbox.RecordPaymentAsync("PAY-S01-4", 104, "INR", topic.Name);
        Assert.Equal(new RelayPass(1, 1, Faulted: false), await PassAsync());
        rows = await OutboxAsync();
        Assert.NotNull(rows.Single(r => r.OutboxId == after).SentAt);
        var rejected = rows.Single(r => r.OutboxId != after && r.SentAt is null);
        Assert.NotNull(rejected.RejectedAt);
        Assert.StartsWith("400", rejected.LastError);
        Assert.Equal(new RelayPass(0, 0, Faulted: false), await PassAsync());
    }
}
