using System.Data;
using MessageBroker.Application.Persistence;
using MessageBroker.Domain;
using static MessageBroker.Infrastructure.Data.SqlHelper;

namespace MessageBroker.Infrastructure.Data;

public sealed class ApplicationRepository(Db db) : IApplicationRepository
{
    public Task<ApplicationRecord> CreateAsync(Guid appId, string name, bool isAdmin, CancellationToken ct = default) =>
        db.QuerySingleAsync("usp_Application_Create", RecordMap.Application, ct,
            Param("AppId", appId), Param("Name", name), Param("IsAdmin", isAdmin));

    public Task<ApplicationRecord?> GetAsync(Guid appId, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("usp_Application_Get", RecordMap.Application, ct, Param("AppId", appId));

    public Task<IReadOnlyList<ApplicationRecord>> ListAsync(CancellationToken ct = default) =>
        db.QueryAsync("usp_Application_List", RecordMap.Application, ct);

    public Task SetActiveAsync(Guid appId, bool isActive, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_Application_SetActive", ct, Param("AppId", appId), Param("IsActive", isActive));

    public Task EnsureBootstrapAdminAsync(Guid appId, string name, Guid keyId, string prefix, byte[] hash, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_Application_EnsureBootstrapAdmin", ct,
            Param("AppId", appId), Param("Name", name), Param("KeyId", keyId), Param("Prefix", prefix), Param("Hash", hash));

    public Task<ApiKeyRecord> CreateKeyAsync(Guid keyId, Guid appId, string prefix, byte[] hash, DateTime? expiresAt, CancellationToken ct = default) =>
        db.QuerySingleAsync("usp_ApiKey_Create", RecordMap.ApiKey, ct,
            Param("KeyId", keyId), Param("AppId", appId), Param("Prefix", prefix), Param("Hash", hash), Param("ExpiresAt", expiresAt));

    public Task DeactivateKeyAsync(Guid appId, Guid keyId, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_ApiKey_Deactivate", ct, Param("AppId", appId), Param("KeyId", keyId));

    public Task<IReadOnlyList<ApiKeyRecord>> ListKeysAsync(Guid appId, CancellationToken ct = default) =>
        db.QueryAsync("usp_ApiKey_List", RecordMap.ApiKey, ct, Param("AppId", appId));

    public Task<ApiKeyLookup?> GetKeyByPrefixAsync(string prefix, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("usp_ApiKey_GetByPrefix", RecordMap.ApiKeyLookup, ct, Param("Prefix", prefix));

    public Task GrantAsync(PermissionRecord p, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_Permission_Grant", ct, RecordMap.Parameters(p));

    public Task RevokeAsync(PermissionRecord p, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_Permission_Revoke", ct, RecordMap.Parameters(p));

    public Task<IReadOnlyList<PermissionRecord>> ListPermissionsAsync(Guid appId, CancellationToken ct = default) =>
        db.QueryAsync("usp_Permission_List", RecordMap.Permission, ct, Param("AppId", appId));

    public Task<bool> HasPermissionAsync(Guid appId, string resourceType, Guid resourceId, string permission, CancellationToken ct = default) =>
        db.ScalarAsync<bool>("usp_Permission_Check", ct,
            Param("AppId", appId), Param("ResourceType", resourceType), Param("ResourceId", resourceId), Param("Permission", permission));
}

public sealed class AllowedHostRepository(Db db) : IAllowedHostRepository
{
    public Task AddAsync(string host, Guid? addedBy, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_AllowedHost_Add", ct, Param("Host", host), Param("AddedBy", addedBy));

    public Task RemoveAsync(string host, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_AllowedHost_Remove", ct, Param("Host", host));

    public Task<IReadOnlyList<AllowedHostRecord>> ListAsync(CancellationToken ct = default) =>
        db.QueryAsync("usp_AllowedHost_List", RecordMap.AllowedHost, ct);

    public Task SeedAsync(IEnumerable<string> hosts, CancellationToken ct = default)
    {
        var table = new DataTable();
        table.Columns.Add("Host", typeof(string));
        foreach (var host in hosts.Select(h => h.Trim()).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            table.Rows.Add(host);
        return db.ExecuteAsync("usp_AllowedHost_Seed", ct, Table("Hosts", "broker.HostList", table));
    }
}

public sealed class TopicRepository(Db db) : ITopicRepository
{
    public Task<TopicRecord> CreateAsync(Guid topicId, string name, int? defaultTtlSeconds, CancellationToken ct = default) =>
        db.QuerySingleAsync("usp_Topic_Create", RecordMap.Topic, ct,
            Param("TopicId", topicId), Param("Name", name), Param("DefaultTtlSeconds", defaultTtlSeconds));

    public Task<TopicRecord?> GetAsync(Guid topicId, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("usp_Topic_Get", RecordMap.Topic, ct, Param("TopicId", topicId));

    public Task<TopicRecord?> GetByNameAsync(string name, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("usp_Topic_Get", RecordMap.Topic, ct, Param("Name", name));

    public Task<IReadOnlyList<TopicRecord>> ListAsync(CancellationToken ct = default) =>
        db.QueryAsync("usp_Topic_List", RecordMap.Topic, ct);

    public Task<TopicRecord> UpdateAsync(Guid topicId, int? defaultTtlSeconds, CancellationToken ct = default) =>
        db.QuerySingleAsync("usp_Topic_Update", RecordMap.Topic, ct,
            Param("TopicId", topicId), Param("DefaultTtlSeconds", defaultTtlSeconds));

    public Task DeleteAsync(Guid topicId, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_Topic_Delete", ct, Param("TopicId", topicId));
}

public sealed class SubscriptionRepository(Db db) : ISubscriptionRepository
{
    public Task<SubscriptionRecord> CreateAsync(SubscriptionCreate create, CancellationToken ct = default) =>
        db.QuerySingleAsync("usp_Subscription_Create", RecordMap.Subscription, ct, RecordMap.Parameters(create));

    public Task<SubscriptionRecord?> GetAsync(Guid subscriptionId, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("usp_Subscription_Get", RecordMap.Subscription, ct, Param("SubscriptionId", subscriptionId));

    public Task<IReadOnlyList<SubscriptionRecord>> ListAsync(Guid topicId, CancellationToken ct = default) =>
        db.QueryAsync("usp_Subscription_List", RecordMap.Subscription, ct, Param("TopicId", topicId));

    public Task<SubscriptionRecord> UpdateAsync(SubscriptionUpdate update, CancellationToken ct = default) =>
        db.QuerySingleAsync("usp_Subscription_Update", RecordMap.Subscription, ct, RecordMap.Parameters(update));

    public Task DeleteAsync(Guid subscriptionId, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_Subscription_Delete", ct, Param("SubscriptionId", subscriptionId));

    public Task<SubscriptionRecord> RotateSecretAsync(Guid subscriptionId, string newSecret, CancellationToken ct = default) =>
        db.QuerySingleAsync("usp_Subscription_RotateSecret", RecordMap.Subscription, ct,
            Param("SubscriptionId", subscriptionId), Param("NewSecret", newSecret));

    public Task<IReadOnlyList<PushSubscriptionRecord>> ListActivePushAsync(CancellationToken ct = default) =>
        db.QueryAsync("usp_Subscription_ListActivePush", RecordMap.PushSubscription, ct);
}

public sealed class MessageRepository(Db db) : IMessageRepository
{
    public Task<PublishRecord> PublishAsync(PublishCommand c, CancellationToken ct = default) =>
        db.QuerySingleAsync("usp_Message_Publish", RecordMap.Publish, ct, RecordMap.Parameters(c));

    public Task<MessageDetailsRecord> GetByIdAsync(Guid messageId, Guid? appId, CancellationToken ct = default) =>
        db.ReadMultipleAsync("usp_Message_GetById", async reader =>
        {
            var message = await ReadSingleAsync(reader, RecordMap.Message, ct);
            await reader.NextResultAsync(ct);
            var deliveries = await ReadAllAsync(reader, RecordMap.MessageDelivery, ct);
            await reader.NextResultAsync(ct);
            var attempts = await ReadAllAsync(reader, RecordMap.Attempt, ct);
            return new MessageDetailsRecord(message, deliveries, attempts);
        }, ct, Param("MessageId", messageId), Param("AppId", appId));

    public Task<IReadOnlyList<MessageSummaryRecord>> ListByCorrelationAsync(string correlationId, CancellationToken ct = default) =>
        db.QueryAsync("usp_Message_ListByCorrelation", RecordMap.MessageSummary, ct, Param("CorrelationId", correlationId));
}

public sealed class DeliveryRepository(Db db) : IDeliveryRepository
{
    private const int MaxErrorMessageLength = 1000;
    private const int MaxErrorCodeLength = 100;

    public Task<IReadOnlyList<LeasedDeliveryRecord>> LeaseAsync(Guid subscriptionId, int maxMessages, string channel, Guid? appId, CancellationToken ct = default) =>
        db.QueryAsync("usp_Delivery_Lease", RecordMap.LeasedDelivery, ct,
            Param("SubscriptionId", subscriptionId), Param("MaxMessages", maxMessages), Param("Channel", channel), Param("AppId", appId));

    public Task<Guid> AckAsync(long deliveryId, Guid lockToken, Guid? appId, int? httpStatusCode = null, CancellationToken ct = default) =>
        db.ScalarAsync<Guid>("usp_Delivery_Ack", ct,
            Param("DeliveryId", deliveryId), Param("LockToken", lockToken), Param("AppId", appId), Param("HttpStatusCode", httpStatusCode));

    public Task<NackRecord> NackAsync(long deliveryId, Guid lockToken, Guid? appId, FailureDetails f, CancellationToken ct = default) =>
        db.QuerySingleAsync("usp_Delivery_Nack", RecordMap.Nack, ct,
            Param("DeliveryId", deliveryId),
            Param("LockToken", lockToken),
            Param("AppId", appId),
            Param("ErrorCode", Truncate(f.ErrorCode, MaxErrorCodeLength)),
            Param("ErrorMessage", Truncate(f.ErrorMessage, MaxErrorMessageLength)),
            Param("ErrorDetail", f.ErrorDetail),
            Param("HttpStatusCode", f.HttpStatusCode),
            Param("DeadLetter", f.DeadLetter));

    public Task<DateTime> RenewAsync(long deliveryId, Guid lockToken, Guid? appId, CancellationToken ct = default) =>
        db.ScalarAsync<DateTime>("usp_Delivery_Renew", ct, Param("DeliveryId", deliveryId), Param("LockToken", lockToken), Param("AppId", appId));

    public Task<int> ExpireLeasesAsync(int maxRows = 500, CancellationToken ct = default) =>
        db.ScalarAsync<int>("usp_Delivery_ExpireLeases", ct, Param("MaxRows", maxRows));

    public Task<int> ExpirePendingAsync(int maxRows = 1000, CancellationToken ct = default) =>
        db.ScalarAsync<int>("usp_Delivery_ExpirePending", ct, Param("MaxRows", maxRows));

    public Task<Guid> RequeueAsync(long deliveryId, Guid requeuedBy, CancellationToken ct = default) =>
        db.ScalarAsync<Guid>("usp_DeadLetter_Requeue", ct, Param("DeliveryId", deliveryId), Param("RequeuedBy", requeuedBy));

    public Task<IReadOnlyList<DeadLetterRecord>> ListDeadLettersAsync(Guid subscriptionId, Guid? appId, int pageSize, long? beforeId, bool includeRequeued, CancellationToken ct = default) =>
        db.QueryAsync("usp_DeadLetter_List", RecordMap.DeadLetter, ct,
            Param("SubscriptionId", subscriptionId),
            Param("AppId", appId),
            Param("PageSize", pageSize),
            Param("BeforeId", beforeId),
            Param("IncludeRequeued", includeRequeued));

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}

public sealed class DashboardRepository(Db db) : IDashboardRepository
{
    public Task<OverviewRecord> GetOverviewAsync(int windowMinutes, CancellationToken ct = default) =>
        db.ReadMultipleAsync("usp_Admin_GetOverview", async reader =>
        {
            var totals = await ReadSingleAsync(reader, RecordMap.OverviewTotals, ct);
            await reader.NextResultAsync(ct);
            var series = await ReadAllAsync(reader, RecordMap.Throughput, ct);
            await reader.NextResultAsync(ct);
            var subscriptions = await ReadAllAsync(reader, RecordMap.SubscriptionHealth, ct);
            await reader.NextResultAsync(ct);
            var heartbeat = await ReadSingleAsync(reader, RecordMap.Heartbeat, ct);
            return new OverviewRecord(totals, series, subscriptions, heartbeat);
        }, ct, Param("WindowMinutes", windowMinutes));

    public Task<IReadOnlyList<MessageSearchRecord>> SearchMessagesAsync(MessageSearchQuery query, CancellationToken ct = default) =>
        db.QueryAsync("usp_Admin_Message_Search", RecordMap.MessageSearch, ct, RecordMap.Parameters(query));

    public Task<IReadOnlyList<DeadLetterRecord>> SearchDeadLettersAsync(DeadLetterSearchQuery query, CancellationToken ct = default) =>
        db.QueryAsync("usp_Admin_DeadLetter_Search", RecordMap.DeadLetterSummary, ct, RecordMap.Parameters(query));
}

public sealed class OperationsRepository(Db db) : IOperationsRepository
{
    public Task WriteHeartbeatAsync(string instanceId, CancellationToken ct = default) =>
        db.ExecuteAsync("usp_Heartbeat_Write", ct, Param("InstanceId", instanceId));

    public Task<HeartbeatRecord> GetLatestHeartbeatAsync(CancellationToken ct = default) =>
        db.QuerySingleAsync("usp_Heartbeat_GetLatest", RecordMap.Heartbeat, ct);

    public Task<PurgeRecord> PurgeAsync(int completedDays, int deadLetterDays, int batchSize, CancellationToken ct = default) =>
        db.QuerySingleAsync("usp_Retention_Purge", RecordMap.Purge, ct,
            Param("CompletedDays", completedDays), Param("DeadLetterDays", deadLetterDays), Param("BatchSize", batchSize));

    public Task PingAsync(CancellationToken ct = default) =>
        db.ScalarAsync<int>("usp_Health_Ping", ct);
}
