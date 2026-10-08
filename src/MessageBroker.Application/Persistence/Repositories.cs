namespace MessageBroker.Application.Persistence;

// Each method calls exactly one stored procedure (named in the comment). Business errors raised by
// the procedures surface as BrokerException via the infrastructure's SqlErrorMapper.

public interface IApplicationRepository
{
    Task<ApplicationRecord> CreateAsync(Guid appId, string name, bool isAdmin, CancellationToken ct = default);   // usp_Application_Create
    Task<ApplicationRecord?> GetAsync(Guid appId, CancellationToken ct = default);                              // usp_Application_Get
    Task<IReadOnlyList<ApplicationRecord>> ListAsync(CancellationToken ct = default);                           // usp_Application_List
    Task SetActiveAsync(Guid appId, bool isActive, CancellationToken ct = default);                             // usp_Application_SetActive
    Task EnsureBootstrapAdminAsync(Guid appId, string name, Guid keyId, string prefix, byte[] hash, CancellationToken ct = default); // usp_Application_EnsureBootstrapAdmin

    Task<ApiKeyRecord> CreateKeyAsync(Guid keyId, Guid appId, string prefix, byte[] hash, DateTime? expiresAt, CancellationToken ct = default); // usp_ApiKey_Create
    Task DeactivateKeyAsync(Guid appId, Guid keyId, CancellationToken ct = default);                           // usp_ApiKey_Deactivate
    Task<IReadOnlyList<ApiKeyRecord>> ListKeysAsync(Guid appId, CancellationToken ct = default);              // usp_ApiKey_List
    Task<ApiKeyLookup?> GetKeyByPrefixAsync(string prefix, CancellationToken ct = default);                    // usp_ApiKey_GetByPrefix

    Task GrantAsync(PermissionRecord permission, CancellationToken ct = default);                               // usp_Permission_Grant
    Task RevokeAsync(PermissionRecord permission, CancellationToken ct = default);                              // usp_Permission_Revoke
    Task<IReadOnlyList<PermissionRecord>> ListPermissionsAsync(Guid appId, CancellationToken ct = default);   // usp_Permission_List
    Task<bool> HasPermissionAsync(Guid appId, string resourceType, Guid resourceId, string permission, CancellationToken ct = default); // usp_Permission_Check
}

public interface IAllowedHostRepository
{
    Task AddAsync(string host, Guid? addedBy, CancellationToken ct = default);          // usp_AllowedHost_Add
    Task RemoveAsync(string host, CancellationToken ct = default);                      // usp_AllowedHost_Remove
    Task<IReadOnlyList<AllowedHostRecord>> ListAsync(CancellationToken ct = default);  // usp_AllowedHost_List
    Task SeedAsync(IEnumerable<string> hosts, CancellationToken ct = default);          // usp_AllowedHost_Seed
}

public interface ITopicRepository
{
    Task<TopicRecord> CreateAsync(Guid topicId, string name, int? defaultTtlSeconds, CancellationToken ct = default); // usp_Topic_Create
    Task<TopicRecord?> GetAsync(Guid topicId, CancellationToken ct = default);           // usp_Topic_Get
    Task<TopicRecord?> GetByNameAsync(string name, CancellationToken ct = default);      // usp_Topic_Get
    Task<IReadOnlyList<TopicRecord>> ListAsync(CancellationToken ct = default);         // usp_Topic_List
    Task<TopicRecord> UpdateAsync(Guid topicId, int? defaultTtlSeconds, CancellationToken ct = default); // usp_Topic_Update
    Task DeleteAsync(Guid topicId, CancellationToken ct = default);                      // usp_Topic_Delete
}

public sealed record SubscriptionCreate(
    Guid SubscriptionId,
    Guid TopicId,
    string Name,
    Guid OwnerAppId,
    string DeliveryMode,
    string? WebhookUrl,
    string? WebhookSecret,
    int WebhookTimeoutSeconds,
    int MaxConcurrentDeliveries,
    int MaxAttempts,
    int LockDurationSeconds,
    int RetryBaseDelaySeconds,
    int RetryMaxDelaySeconds,
    int? TtlSeconds);

public sealed record SubscriptionUpdate(
    Guid SubscriptionId,
    string Status,
    string? WebhookUrl,
    int WebhookTimeoutSeconds,
    int MaxConcurrentDeliveries,
    int MaxAttempts,
    int LockDurationSeconds,
    int RetryBaseDelaySeconds,
    int RetryMaxDelaySeconds,
    int? TtlSeconds);

public interface ISubscriptionRepository
{
    Task<SubscriptionRecord> CreateAsync(SubscriptionCreate create, CancellationToken ct = default);      // usp_Subscription_Create
    Task<SubscriptionRecord?> GetAsync(Guid subscriptionId, CancellationToken ct = default);              // usp_Subscription_Get
    Task<IReadOnlyList<SubscriptionRecord>> ListAsync(Guid topicId, CancellationToken ct = default);     // usp_Subscription_List
    Task<SubscriptionRecord> UpdateAsync(SubscriptionUpdate update, CancellationToken ct = default);      // usp_Subscription_Update
    Task DeleteAsync(Guid subscriptionId, CancellationToken ct = default);                                // usp_Subscription_Delete
    Task<SubscriptionRecord> RotateSecretAsync(Guid subscriptionId, string newSecret, CancellationToken ct = default); // usp_Subscription_RotateSecret
    Task<IReadOnlyList<PushSubscriptionRecord>> ListActivePushAsync(CancellationToken ct = default);     // vw_ActivePushSubscriptions (via usp_Subscription_ListActivePush)
}

public interface IMessageRepository
{
    Task<PublishRecord> PublishAsync(PublishCommand command, CancellationToken ct = default);                        // usp_Message_Publish
    Task<MessageDetailsRecord> GetByIdAsync(Guid messageId, Guid? appId, CancellationToken ct = default);           // usp_Message_GetById
    Task<IReadOnlyList<MessageSummaryRecord>> ListByCorrelationAsync(string correlationId, CancellationToken ct = default); // usp_Message_ListByCorrelation
}

public interface IDeliveryRepository
{
    Task<IReadOnlyList<LeasedDeliveryRecord>> LeaseAsync(Guid subscriptionId, int maxMessages, string channel, Guid? appId, CancellationToken ct = default); // usp_Delivery_Lease
    /// <returns>The delivery's message ID.</returns>
    Task<Guid> AckAsync(long deliveryId, Guid lockToken, Guid? appId, int? httpStatusCode = null, CancellationToken ct = default); // usp_Delivery_Ack
    Task<NackRecord> NackAsync(long deliveryId, Guid lockToken, Guid? appId, FailureDetails failure, CancellationToken ct = default); // usp_Delivery_Nack
    Task<DateTime> RenewAsync(long deliveryId, Guid lockToken, Guid? appId, CancellationToken ct = default);  // usp_Delivery_Renew
    Task<int> ExpireLeasesAsync(int maxRows = 500, CancellationToken ct = default);                           // usp_Delivery_ExpireLeases
    Task<int> ExpirePendingAsync(int maxRows = 1000, CancellationToken ct = default);                         // usp_Delivery_ExpirePending
    /// <returns>The delivery's message ID.</returns>
    Task<Guid> RequeueAsync(long deliveryId, Guid requeuedBy, CancellationToken ct = default);                // usp_DeadLetter_Requeue
    Task<IReadOnlyList<DeadLetterRecord>> ListDeadLettersAsync(Guid subscriptionId, Guid? appId, int pageSize, long? beforeId, bool includeRequeued, CancellationToken ct = default); // usp_DeadLetter_List
}

/// <summary>Admin dashboard queries (Dashboard.sql).</summary>
public interface IDashboardRepository
{
    Task<OverviewRecord> GetOverviewAsync(int windowMinutes, CancellationToken ct = default);                                 // usp_Admin_GetOverview
    Task<IReadOnlyList<MessageSearchRecord>> SearchMessagesAsync(MessageSearchQuery query, CancellationToken ct = default);   // usp_Admin_Message_Search
    Task<IReadOnlyList<DeadLetterRecord>> SearchDeadLettersAsync(DeadLetterSearchQuery query, CancellationToken ct = default); // usp_Admin_DeadLetter_Search
}

public interface IOperationsRepository
{
    Task WriteHeartbeatAsync(string instanceId, CancellationToken ct = default);                             // usp_Heartbeat_Write
    Task<HeartbeatRecord> GetLatestHeartbeatAsync(CancellationToken ct = default);                           // usp_Heartbeat_GetLatest
    Task<PurgeRecord> PurgeAsync(int completedDays, int deadLetterDays, int batchSize, CancellationToken ct = default); // usp_Retention_Purge
    Task PingAsync(CancellationToken ct = default);                                                          // SELECT 1
}
