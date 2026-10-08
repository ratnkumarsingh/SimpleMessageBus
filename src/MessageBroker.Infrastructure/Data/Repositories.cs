using System.Data;
using Dapper;
using MessageBroker.Application.Persistence;
using MessageBroker.Domain;

namespace MessageBroker.Infrastructure.Data;

public sealed class ApplicationRepository(Db db) : IApplicationRepository
{
    public Task<ApplicationRecord> CreateAsync(Guid appId, string name, bool isAdmin, CancellationToken ct = default) =>
        db.QuerySingleAsync<ApplicationRecord>("usp_Application_Create", new { AppId = appId, Name = name, IsAdmin = isAdmin }, ct);

    public Task<ApplicationRecord?> GetAsync(Guid appId, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync<ApplicationRecord>("usp_Application_Get", new { AppId = appId }, ct);

    public Task<IReadOnlyList<ApplicationRecord>> ListAsync(CancellationToken ct = default) =>
        db.QueryAsync<ApplicationRecord>("usp_Application_List", null, ct);

    public Task SetActiveAsync(Guid appId, bool isActive, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_Application_SetActive", new { AppId = appId, IsActive = isActive }, ct);

    public Task EnsureBootstrapAdminAsync(Guid appId, string name, Guid keyId, string prefix, byte[] hash, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_Application_EnsureBootstrapAdmin",
            new { AppId = appId, Name = name, KeyId = keyId, Prefix = prefix, Hash = hash }, ct);

    public Task<ApiKeyRecord> CreateKeyAsync(Guid keyId, Guid appId, string prefix, byte[] hash, DateTime? expiresAt, CancellationToken ct = default) =>
        db.QuerySingleAsync<ApiKeyRecord>("usp_ApiKey_Create",
            new { KeyId = keyId, AppId = appId, Prefix = prefix, Hash = hash, ExpiresAt = expiresAt }, ct);

    public Task DeactivateKeyAsync(Guid appId, Guid keyId, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_ApiKey_Deactivate", new { AppId = appId, KeyId = keyId }, ct);

    public Task<IReadOnlyList<ApiKeyRecord>> ListKeysAsync(Guid appId, CancellationToken ct = default) =>
        db.QueryAsync<ApiKeyRecord>("usp_ApiKey_List", new { AppId = appId }, ct);

    public Task<ApiKeyLookup?> GetKeyByPrefixAsync(string prefix, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync<ApiKeyLookup>("usp_ApiKey_GetByPrefix", new { Prefix = prefix }, ct);

    public Task GrantAsync(PermissionRecord p, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_Permission_Grant", p, ct);

    public Task RevokeAsync(PermissionRecord p, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_Permission_Revoke", p, ct);

    public Task<IReadOnlyList<PermissionRecord>> ListPermissionsAsync(Guid appId, CancellationToken ct = default) =>
        db.QueryAsync<PermissionRecord>("usp_Permission_List", new { AppId = appId }, ct);

    public Task<bool> HasPermissionAsync(Guid appId, string resourceType, Guid resourceId, string permission, CancellationToken ct = default) =>
        db.QuerySingleAsync<bool>("usp_Permission_Check",
            new { AppId = appId, ResourceType = resourceType, ResourceId = resourceId, Permission = permission }, ct);
}

public sealed class AllowedHostRepository(Db db) : IAllowedHostRepository
{
    public Task AddAsync(string host, Guid? addedBy, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_AllowedHost_Add", new { Host = host, AddedBy = addedBy }, ct);

    public Task RemoveAsync(string host, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_AllowedHost_Remove", new { Host = host }, ct);

    public Task<IReadOnlyList<AllowedHostRecord>> ListAsync(CancellationToken ct = default) =>
        db.QueryAsync<AllowedHostRecord>("usp_AllowedHost_List", null, ct);

    public Task SeedAsync(IEnumerable<string> hosts, CancellationToken ct = default)
    {
        var table = new DataTable();
        table.Columns.Add("Host", typeof(string));
        foreach (var host in hosts.Select(h => h.Trim()).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            table.Rows.Add(host);
        return db.ExecuteAsync("usp_AllowedHost_Seed", new { Hosts = table.AsTableValuedParameter("broker.HostList") }, ct);
    }
}

public sealed class TopicRepository(Db db) : ITopicRepository
{
    public Task<TopicRecord> CreateAsync(Guid topicId, string name, int? defaultTtlSeconds, CancellationToken ct = default) =>
        db.QuerySingleAsync<TopicRecord>("usp_Topic_Create", new { TopicId = topicId, Name = name, DefaultTtlSeconds = defaultTtlSeconds }, ct);

    public Task<TopicRecord?> GetAsync(Guid topicId, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync<TopicRecord>("usp_Topic_Get", new { TopicId = topicId }, ct);

    public Task<TopicRecord?> GetByNameAsync(string name, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync<TopicRecord>("usp_Topic_Get", new { Name = name }, ct);

    public Task<IReadOnlyList<TopicRecord>> ListAsync(CancellationToken ct = default) =>
        db.QueryAsync<TopicRecord>("usp_Topic_List", null, ct);

    public Task<TopicRecord> UpdateAsync(Guid topicId, int? defaultTtlSeconds, CancellationToken ct = default) =>
        db.QuerySingleAsync<TopicRecord>("usp_Topic_Update", new { TopicId = topicId, DefaultTtlSeconds = defaultTtlSeconds }, ct);

    public Task DeleteAsync(Guid topicId, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_Topic_Delete", new { TopicId = topicId }, ct);
}

public sealed class SubscriptionRepository(Db db) : ISubscriptionRepository
{
    public Task<SubscriptionRecord> CreateAsync(SubscriptionCreate create, CancellationToken ct = default) =>
        db.QuerySingleAsync<SubscriptionRecord>("usp_Subscription_Create", create, ct);

    public Task<SubscriptionRecord?> GetAsync(Guid subscriptionId, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync<SubscriptionRecord>("usp_Subscription_Get", new { SubscriptionId = subscriptionId }, ct);

    public Task<IReadOnlyList<SubscriptionRecord>> ListAsync(Guid topicId, CancellationToken ct = default) =>
        db.QueryAsync<SubscriptionRecord>("usp_Subscription_List", new { TopicId = topicId }, ct);

    public Task<SubscriptionRecord> UpdateAsync(SubscriptionUpdate update, CancellationToken ct = default) =>
        db.QuerySingleAsync<SubscriptionRecord>("usp_Subscription_Update", update, ct);

    public Task DeleteAsync(Guid subscriptionId, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_Subscription_Delete", new { SubscriptionId = subscriptionId }, ct);

    public Task<SubscriptionRecord> RotateSecretAsync(Guid subscriptionId, string newSecret, CancellationToken ct = default) =>
        db.QuerySingleAsync<SubscriptionRecord>("usp_Subscription_RotateSecret", new { SubscriptionId = subscriptionId, NewSecret = newSecret }, ct);

    public Task<IReadOnlyList<PushSubscriptionRecord>> ListActivePushAsync(CancellationToken ct = default) =>
        db.QueryAsync<PushSubscriptionRecord>("usp_Subscription_ListActivePush", null, ct);
}

public sealed class MessageRepository(Db db) : IMessageRepository
{
    public Task<PublishRecord> PublishAsync(PublishCommand c, CancellationToken ct = default) =>
        db.QuerySingleAsync<PublishRecord>("usp_Message_Publish", c, ct);

    public Task<MessageDetailsRecord> GetByIdAsync(Guid messageId, Guid? appId, CancellationToken ct = default) =>
        db.QueryMultipleAsync("usp_Message_GetById", new { MessageId = messageId, AppId = appId }, async grid =>
        {
            var message = await grid.ReadSingleAsync<MessageRecord>();
            var deliveries = (await grid.ReadAsync<MessageDeliveryRecord>()).AsList();
            var attempts = (await grid.ReadAsync<AttemptRecord>()).AsList();
            return new MessageDetailsRecord(message, deliveries, attempts);
        }, ct);

    public Task<IReadOnlyList<MessageSummaryRecord>> ListByCorrelationAsync(string correlationId, CancellationToken ct = default) =>
        db.QueryAsync<MessageSummaryRecord>("usp_Message_ListByCorrelation", new { CorrelationId = correlationId }, ct);
}

public sealed class DeliveryRepository(Db db) : IDeliveryRepository
{
    private const int MaxErrorMessageLength = 1000;
    private const int MaxErrorCodeLength = 100;

    public Task<IReadOnlyList<LeasedDeliveryRecord>> LeaseAsync(Guid subscriptionId, int maxMessages, string channel, Guid? appId, CancellationToken ct = default) =>
        db.QueryAsync<LeasedDeliveryRecord>("usp_Delivery_Lease",
            new { SubscriptionId = subscriptionId, MaxMessages = maxMessages, Channel = channel, AppId = appId }, ct);

    public Task<Guid> AckAsync(long deliveryId, Guid lockToken, Guid? appId, int? httpStatusCode = null, CancellationToken ct = default) =>
        db.QuerySingleAsync<Guid>("usp_Delivery_Ack",
            new { DeliveryId = deliveryId, LockToken = lockToken, AppId = appId, HttpStatusCode = httpStatusCode }, ct);

    public Task<NackRecord> NackAsync(long deliveryId, Guid lockToken, Guid? appId, FailureDetails f, CancellationToken ct = default) =>
        db.QuerySingleAsync<NackRecord>("usp_Delivery_Nack", new
        {
            DeliveryId = deliveryId,
            LockToken = lockToken,
            AppId = appId,
            ErrorCode = Truncate(f.ErrorCode, MaxErrorCodeLength),
            ErrorMessage = Truncate(f.ErrorMessage, MaxErrorMessageLength),
            f.ErrorDetail,
            f.HttpStatusCode,
            f.DeadLetter,
        }, ct);

    public Task<DateTime> RenewAsync(long deliveryId, Guid lockToken, Guid? appId, CancellationToken ct = default) =>
        db.QuerySingleAsync<DateTime>("usp_Delivery_Renew", new { DeliveryId = deliveryId, LockToken = lockToken, AppId = appId }, ct);

    public Task<int> ExpireLeasesAsync(int maxRows = 500, CancellationToken ct = default) =>
        db.QuerySingleAsync<int>("usp_Delivery_ExpireLeases", new { MaxRows = maxRows }, ct);

    public Task<int> ExpirePendingAsync(int maxRows = 1000, CancellationToken ct = default) =>
        db.QuerySingleAsync<int>("usp_Delivery_ExpirePending", new { MaxRows = maxRows }, ct);

    public Task<Guid> RequeueAsync(long deliveryId, Guid requeuedBy, CancellationToken ct = default) =>
        db.QuerySingleAsync<Guid>("usp_DeadLetter_Requeue", new { DeliveryId = deliveryId, RequeuedBy = requeuedBy }, ct);

    public Task<IReadOnlyList<DeadLetterRecord>> ListDeadLettersAsync(Guid subscriptionId, Guid? appId, int pageSize, long? beforeId, bool includeRequeued, CancellationToken ct = default) =>
        db.QueryAsync<DeadLetterRecord>("usp_DeadLetter_List", new
        {
            SubscriptionId = subscriptionId,
            AppId = appId,
            PageSize = pageSize,
            BeforeId = beforeId,
            IncludeRequeued = includeRequeued,
        }, ct);

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}

public sealed class DashboardRepository(Db db) : IDashboardRepository
{
    public Task<OverviewRecord> GetOverviewAsync(int windowMinutes, CancellationToken ct = default) =>
        db.QueryMultipleAsync("usp_Admin_GetOverview", new { WindowMinutes = windowMinutes }, async grid =>
        {
            var totals = await grid.ReadSingleAsync<OverviewTotalsRecord>();
            var series = (await grid.ReadAsync<ThroughputRecord>()).AsList();
            var subscriptions = (await grid.ReadAsync<SubscriptionHealthRecord>()).AsList();
            var heartbeat = await grid.ReadSingleAsync<HeartbeatRecord>();
            return new OverviewRecord(totals, series, subscriptions, heartbeat);
        }, ct);

    public Task<IReadOnlyList<MessageSearchRecord>> SearchMessagesAsync(MessageSearchQuery query, CancellationToken ct = default) =>
        db.QueryAsync<MessageSearchRecord>("usp_Admin_Message_Search", query, ct);

    public Task<IReadOnlyList<DeadLetterRecord>> SearchDeadLettersAsync(DeadLetterSearchQuery query, CancellationToken ct = default) =>
        db.QueryAsync<DeadLetterRecord>("usp_Admin_DeadLetter_Search", query, ct);
}

public sealed class OperationsRepository(Db db) : IOperationsRepository
{
    public Task WriteHeartbeatAsync(string instanceId, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_Heartbeat_Write", new { InstanceId = instanceId }, ct);

    public Task<HeartbeatRecord> GetLatestHeartbeatAsync(CancellationToken ct = default) =>
        db.QuerySingleAsync<HeartbeatRecord>("usp_Heartbeat_GetLatest", null, ct);

    public Task<PurgeRecord> PurgeAsync(int completedDays, int deadLetterDays, int batchSize, CancellationToken ct = default) =>
        db.QuerySingleAsync<PurgeRecord>("usp_Retention_Purge",
            new { CompletedDays = completedDays, DeadLetterDays = deadLetterDays, BatchSize = batchSize }, ct);

    public Task PingAsync(CancellationToken ct = default) =>
        db.QuerySingleAsync<int>("usp_Health_Ping", null, ct);
}
