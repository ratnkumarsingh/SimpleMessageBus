using System.Net;
using Microsoft.Data.SqlClient;
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
    [Fact(DisplayName = "S12 Stored procedure publishing: usp_Outbox_Enqueue commits and rolls back with the caller's transaction; relay-once drains the outbox and reports 0 sent, 1 rejected, 2 broker unavailable")]
    public async Task S12_StoredProcedureEventsAndRelayOnce()
    {
        var topic = await CreateTopicViaApiAsync();
        var (publisherId, publisherKey, _) = await CreateAppClientAsync();
        await GrantViaApiAsync(publisherId, "Topic", topic.TopicId, "Publish");
        var (subscriberId, _, _) = await CreateAppClientAsync();
        await CreateSubscriptionAsync(topic.TopicId, subscriberId, mode: "Pull");

        var network = new FlakyNetwork(Api.Server.CreateHandler());
        var broker = BrokerClient.Create(new HttpClient(network) { BaseAddress = Api.Server.BaseAddress }, publisherKey);
        var relay = new OutboxRelay(new OutboxStore(new SampleDatabase(Sql.ConnectionString)), broker,
            NullLogger<OutboxRelay>.Instance, TimeProvider.System);
        Task<RelayPass> RelayOnceAsync() => relay.DrainAsync(CancellationToken.None);

        // A business procedure's transaction: the event is queued only if the transaction commits.
        const string enqueue = """
            BEGIN TRAN;
            DECLARE @id bigint;
            EXEC sample.usp_Outbox_Enqueue @TopicName = @topic, @MessageType = N'OrderShipped.v1',
                 @Payload = N'{"orderId":"ORD-1"}', @CorrelationId = N'ORD-1', @OutboxId = @id OUTPUT;
            IF @commit = 1 COMMIT ELSE ROLLBACK;
            """;
        await ExecAsync(enqueue, new { topic = topic.Name, commit = false });
        Assert.Empty(await OutboxAsync());
        await ExecAsync(enqueue, new { topic = topic.Name, commit = true });
        var queued = Assert.Single(await OutboxAsync());
        Assert.Null(queued.SentAt);

        // Bad input is refused inside the procedure, so the caller's transaction fails too.
        var bad = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(
            "EXEC sample.usp_Outbox_Enqueue @TopicName = @topic, @MessageType = N'X.v1', @Payload = N'not json'",
            new { topic = topic.Name }));
        Assert.Equal(50002, bad.Number);
        var blank = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(
            "EXEC sample.usp_Outbox_Enqueue @TopicName = N' ', @MessageType = N'X.v1', @Payload = N'{}'"));
        Assert.Equal(50001, blank.Number);
        // A missing correlation ID gets a generated one (the column is required).
        await ExecAsync("EXEC sample.usp_Outbox_Enqueue @TopicName = @topic, @MessageType = N'X.v1', @Payload = N'{}'",
            new { topic = topic.Name });
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM sample.Outbox WHERE TRY_CONVERT(uniqueidentifier, CorrelationId) IS NOT NULL"));

        // More rows than one batch, so relay-once has to loop until the outbox is empty.
        await ExecAsync("""
            DECLARE @i int = 1, @id bigint, @p nvarchar(max);
            WHILE @i <= 60
            BEGIN
                SET @p = CONCAT(N'{"orderId":"ORD-B', @i, N'"}');
                EXEC sample.usp_Outbox_Enqueue @TopicName = @topic, @MessageType = N'OrderShipped.v1', @Payload = @p, @OutboxId = @id OUTPUT;
                SET @i += 1;
            END
            """, new { topic = topic.Name });

        // Broker down: exit code 2, nothing sent, rows stay pending for the next run.
        network.Mode = FlakyNetwork.State.Unreachable;
        var down = await RelayOnceAsync();
        Assert.Equal(new RelayPass(0, 0, Faulted: true), down);
        Assert.Equal(2, OutboxRelay.ExitCode(down));
        Assert.All(await OutboxAsync(), r => Assert.Null(r.SentAt));

        // Back up: one run sends all 62 rows (exit 0); the next run has nothing to do (exit 0).
        network.Mode = FlakyNetwork.State.Up;
        var up = await RelayOnceAsync();
        Assert.Equal(new RelayPass(62, 0, Faulted: false), up);
        Assert.Equal(0, OutboxRelay.ExitCode(up));
        Assert.Equal(new RelayPass(0, 0, Faulted: false), await RelayOnceAsync());
        Assert.Equal(62, (await BrokerMessagesAsync()).Count);
        Assert.Equal(62, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries"));

        // A row the broker refuses (a blank message type is a 400) is set aside: exit code 1.
        await ExecAsync("""
            INSERT sample.Outbox (TopicName, MessageType, CorrelationId, Payload)
            VALUES (@topic, N'   ', N'ORD-BAD', N'{"orderId":"ORD-BAD"}')
            """, new { topic = topic.Name });
        var rejected = await RelayOnceAsync();
        Assert.Equal(new RelayPass(0, 1, Faulted: false), rejected);
        Assert.Equal(1, OutboxRelay.ExitCode(rejected));
        Assert.Equal(62, (await BrokerMessagesAsync()).Count);
    }
}
