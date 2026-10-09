using System.Data;
using MessageBroker.Application.Persistence;
using MessageBroker.Domain;
using MessageBroker.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace MessageBroker.IntegrationTests.Infrastructure;

/// <summary>
/// Base for tests that call the stored procedures through the real repositories. Data is reset
/// before each test. Inline SQL appears only in test helpers (seeding checks and backdating).
/// </summary>
[Collection(SqlCollection.Name)]
public abstract class DatabaseTest(SqlServerFixture sql) : IAsyncLifetime
{
    protected SqlServerFixture Sql { get; } = sql;
    protected Db Db { get; } = new(new BrokerDbOptions { ConnectionString = sql.ConnectionString });

    protected ApplicationRepository Apps => new(Db);
    protected AllowedHostRepository Hosts => new(Db);
    protected TopicRepository Topics => new(Db);
    protected SubscriptionRepository Subscriptions => new(Db);
    protected MessageRepository Messages => new(Db);
    protected DeliveryRepository Deliveries => new(Db);
    protected OperationsRepository Operations => new(Db);

    public virtual Task InitializeAsync() => Sql.ResetAsync();
    public virtual Task DisposeAsync() => Task.CompletedTask;

    // ---- seeding ----

    protected async Task<Guid> CreateAppAsync(string? name = null, bool isAdmin = false)
    {
        var app = await Apps.CreateAsync(Guid.NewGuid(), name ?? $"app-{Guid.NewGuid():N}"[..20], isAdmin);
        return app.AppId;
    }

    protected async Task<TopicRecord> CreateTopicAsync(string? name = null, int? defaultTtl = null) =>
        await Topics.CreateAsync(Guid.NewGuid(), name ?? $"topic-{Guid.NewGuid():N}"[..20], defaultTtl);

    protected async Task<SubscriptionRecord> CreateSubscriptionAsync(
        Guid topicId,
        Guid ownerAppId,
        string mode = "Pull",
        string? name = null,
        int maxAttempts = 4,
        int lockSeconds = 60,
        int retryBase = 30,
        int retryMax = 900,
        int? ttlSeconds = null,
        int maxConcurrent = 8,
        string? webhookUrl = null,
        string? protectedSecret = null,
        int webhookTimeout = 30)
    {
        return await Subscriptions.CreateAsync(new SubscriptionCreate(
            Guid.NewGuid(), topicId, name ?? $"sub-{Guid.NewGuid():N}"[..16], ownerAppId, mode,
            mode == "Webhook" ? webhookUrl ?? "https://hooks.internal/receive" : null,
            mode == "Webhook" ? protectedSecret ?? "protected-secret" : null,
            webhookTimeout, maxConcurrent, maxAttempts, lockSeconds, retryBase, retryMax, ttlSeconds));
    }

    protected Task GrantPublishAsync(Guid appId, Guid topicId) =>
        Apps.GrantAsync(new PermissionRecord { AppId = appId, ResourceType = "Topic", ResourceId = topicId, Permission = "Publish" });

    /// <summary>A topic, a publisher with Publish permission, and a subscriber app.</summary>
    protected async Task<(TopicRecord Topic, Guid Publisher, Guid Subscriber)> ArrangeTopicAsync(int? defaultTtl = null)
    {
        var topic = await CreateTopicAsync(defaultTtl: defaultTtl);
        var publisher = await CreateAppAsync();
        var subscriber = await CreateAppAsync();
        await GrantPublishAsync(publisher, topic.TopicId);
        return (topic, publisher, subscriber);
    }

    protected Task<PublishRecord> PublishAsync(
        string topicName, Guid publisher, string? idempotencyKey = null, int? ttl = null,
        string payload = """{"paymentId":"PAY-1"}""", string? correlationId = null)
    {
        var id = Guid.CreateVersion7();
        return Messages.PublishAsync(new PublishCommand(id, topicName, publisher, "PaymentProcessed.v1",
            correlationId ?? id.ToString(), idempotencyKey, payload, """{"tenant":"in-01"}""", ttl));
    }

    // ---- inspection and time travel ----

    /// <summary>A parameter for the inline SQL below, e.g. <c>P("id", messageId)</c> for <c>@id</c>.</summary>
    protected static SqlParameter P(string name, object? value) => SqlHelper.Param(name, value);

    /// <summary>Column <paramref name="ordinal"/> converted like a scalar: numeric widths, UTC DateTimes, NULL as default.</summary>
    protected static T Col<T>(SqlDataReader reader, int ordinal) => SqlHelper.ConvertScalar<T>(reader.GetValue(ordinal));

    /// <summary>The first column of the first row; a missing or NULL value throws unless <typeparamref name="T"/> is a value type.</summary>
    protected async Task<T> ScalarAsync<T>(string sql, params SqlParameter[] args) =>
        SqlHelper.ConvertScalar<T>(await SqlHelper.ExecuteScalarAsync(Sql.ConnectionString, CommandType.Text, sql, CancellationToken.None, args))
        ?? throw new InvalidOperationException("No value.");

    protected async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Func<SqlDataReader, T> map, params SqlParameter[] args)
    {
        await using var c = new SqlConnection(Sql.ConnectionString);
        await c.OpenAsync();
        await using var reader = await SqlHelper.ExecuteReaderAsync(c, CommandType.Text, sql, CancellationToken.None, args);
        return await SqlHelper.ReadAllAsync(reader, map, CancellationToken.None);
    }

    /// <summary>The first column of every row.</summary>
    protected Task<IReadOnlyList<T>> QueryColumnAsync<T>(string sql, params SqlParameter[] args) =>
        QueryAsync(sql, r => Col<T>(r, 0), args);

    protected Task ExecAsync(string sql, params SqlParameter[] args) =>
        SqlHelper.ExecuteNonQueryAsync(Sql.ConnectionString, CommandType.Text, sql, CancellationToken.None, args);

    protected Task<DeliveryRow> GetDeliveryAsync(long deliveryId) =>
        QueryAsync("SELECT * FROM broker.Deliveries WHERE DeliveryId = @deliveryId", DeliveryRow.Read, P("deliveryId", deliveryId))
            .ContinueWith(t => t.Result.Single());

    protected async Task<IReadOnlyList<DeliveryRow>> GetDeliveriesForMessageAsync(Guid messageId) =>
        await QueryAsync("SELECT * FROM broker.Deliveries WHERE MessageId = @messageId ORDER BY DeliveryId", DeliveryRow.Read, P("messageId", messageId));

    protected async Task<IReadOnlyList<AttemptRecord>> GetAttemptsAsync(long deliveryId) =>
        await QueryAsync("SELECT * FROM broker.DeliveryAttempts WHERE DeliveryId = @deliveryId ORDER BY AttemptNumber", RecordMap.Attempt, P("deliveryId", deliveryId));

    protected async Task<IReadOnlyList<DeadLetterRow>> GetDeadLettersAsync(long deliveryId) =>
        await QueryAsync("SELECT * FROM broker.DeadLetters WHERE DeliveryId = @deliveryId ORDER BY DeadLetterId", DeadLetterRow.Read, P("deliveryId", deliveryId));

    protected Task<DateTime> DbNowAsync() => ScalarAsync<DateTime>("SELECT SYSUTCDATETIME()");

    /// <summary>
    /// Moves a datetime column into the past. The database clock cannot be faked, so expiry, backoff
    /// and retention tests shift the stored times instead.
    /// </summary>
    protected Task BackdateAsync(string table, string keyColumn, object key, string column, int seconds) =>
        ExecAsync($"UPDATE broker.{table} SET {column} = DATEADD(second, -@seconds, {column}) WHERE {keyColumn} = @key",
            P("seconds", seconds), P("key", key));

    /// <summary>Makes a Pending delivery due now (skips its backoff).</summary>
    protected Task MakeDueAsync(long deliveryId) =>
        ExecAsync("UPDATE broker.Deliveries SET AvailableAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE DeliveryId = @deliveryId", P("deliveryId", deliveryId));

    /// <summary>Makes a Leased delivery's lease lapse.</summary>
    protected Task LapseLeaseAsync(long deliveryId) =>
        ExecAsync("UPDATE broker.Deliveries SET LockedUntil = DATEADD(second, -1, SYSUTCDATETIME()) WHERE DeliveryId = @deliveryId", P("deliveryId", deliveryId));

    protected static Task<BrokerException> ThrowsBrokerAsync(BrokerErrorKind kind, Func<Task> action) =>
        Assert.ThrowsAsync<BrokerException>(action).ContinueWith(t =>
        {
            Assert.Equal(kind, t.Result.Kind);
            return t.Result;
        });
}

public sealed record DeliveryRow
{
    public long DeliveryId { get; init; }
    public Guid MessageId { get; init; }
    public Guid SubscriptionId { get; init; }
    public byte Status { get; init; }
    public int AttemptCount { get; init; }
    public int TotalAttemptCount { get; init; }
    public DateTime AvailableAt { get; init; }
    public DateTime? LockedUntil { get; init; }
    public Guid? LockToken { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }

    public DeliveryStatus State => (DeliveryStatus)Status;

    public static DeliveryRow Read(SqlDataReader r) => new()
    {
        DeliveryId = r.GetInt64("DeliveryId"),
        MessageId = r.GetGuid("MessageId"),
        SubscriptionId = r.GetGuid("SubscriptionId"),
        Status = r.GetByte("Status"),
        AttemptCount = r.GetInt32("AttemptCount"),
        TotalAttemptCount = r.GetInt32("TotalAttemptCount"),
        AvailableAt = r.GetUtcDateTime("AvailableAt"),
        LockedUntil = r.GetNullableUtcDateTime("LockedUntil"),
        LockToken = r.GetNullableGuid("LockToken"),
        ExpiresAt = r.GetNullableUtcDateTime("ExpiresAt"),
        CreatedAt = r.GetUtcDateTime("CreatedAt"),
        CompletedAt = r.GetNullableUtcDateTime("CompletedAt"),
    };
}

public sealed record DeadLetterRow
{
    public long DeadLetterId { get; init; }
    public long DeliveryId { get; init; }
    public string Reason { get; init; } = "";
    public int AttemptCount { get; init; }
    public string? LastError { get; init; }
    public DateTime? FirstFailureAt { get; init; }
    public DateTime? LastFailureAt { get; init; }
    public DateTime DeadLetteredAt { get; init; }
    public DateTime? RequeuedAt { get; init; }
    public Guid? RequeuedBy { get; init; }

    public static DeadLetterRow Read(SqlDataReader r) => new()
    {
        DeadLetterId = r.GetInt64("DeadLetterId"),
        DeliveryId = r.GetInt64("DeliveryId"),
        Reason = r.GetString("Reason"),
        AttemptCount = r.GetInt32("AttemptCount"),
        LastError = r.GetNullableString("LastError"),
        FirstFailureAt = r.GetNullableUtcDateTime("FirstFailureAt"),
        LastFailureAt = r.GetNullableUtcDateTime("LastFailureAt"),
        DeadLetteredAt = r.GetUtcDateTime("DeadLetteredAt"),
        RequeuedAt = r.GetNullableUtcDateTime("RequeuedAt"),
        RequeuedBy = r.GetNullableGuid("RequeuedBy"),
    };
}
