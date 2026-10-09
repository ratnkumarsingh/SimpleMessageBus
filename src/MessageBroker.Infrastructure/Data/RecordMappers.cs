using MessageBroker.Application.Persistence;
using Microsoft.Data.SqlClient;
using static MessageBroker.Infrastructure.Data.SqlHelper;

namespace MessageBroker.Infrastructure.Data;

/// <summary>
/// Hand-written mappers between the stored procedures and the persistence records: one "row to record"
/// method per result-set shape (column names equal property names), and the parameter lists for the
/// records that are passed to a procedure whole. A procedure that changes its columns makes these throw.
/// </summary>
public static class RecordMap
{
    // ---- rows to records ----

    public static ApplicationRecord Application(SqlDataReader r) => new()
    {
        AppId = r.GetGuid("AppId"),
        Name = r.GetString("Name"),
        IsAdmin = r.GetBoolean("IsAdmin"),
        IsActive = r.GetBoolean("IsActive"),
        CreatedAt = r.GetUtcDateTime("CreatedAt"),
    };

    public static ApiKeyRecord ApiKey(SqlDataReader r) => new()
    {
        KeyId = r.GetGuid("KeyId"),
        AppId = r.GetGuid("AppId"),
        Prefix = r.GetString("Prefix"),
        IsActive = r.GetBoolean("IsActive"),
        CreatedAt = r.GetUtcDateTime("CreatedAt"),
        ExpiresAt = r.GetNullableUtcDateTime("ExpiresAt"),
    };

    public static ApiKeyLookup ApiKeyLookup(SqlDataReader r) => new()
    {
        KeyId = r.GetGuid("KeyId"),
        AppId = r.GetGuid("AppId"),
        Hash = r.GetBytes("Hash"),
        AppName = r.GetString("AppName"),
        IsAdmin = r.GetBoolean("IsAdmin"),
    };

    public static PermissionRecord Permission(SqlDataReader r) => new()
    {
        AppId = r.GetGuid("AppId"),
        ResourceType = r.GetString("ResourceType"),
        ResourceId = r.GetGuid("ResourceId"),
        Permission = r.GetString("Permission"),
    };

    public static AllowedHostRecord AllowedHost(SqlDataReader r) => new()
    {
        Host = r.GetString("Host"),
        AddedBy = r.GetNullableGuid("AddedBy"),
        AddedAt = r.GetUtcDateTime("AddedAt"),
    };

    public static TopicRecord Topic(SqlDataReader r) => new()
    {
        TopicId = r.GetGuid("TopicId"),
        Name = r.GetString("Name"),
        DefaultTtlSeconds = r.GetNullableInt32("DefaultTtlSeconds"),
        CreatedAt = r.GetUtcDateTime("CreatedAt"),
    };

    public static SubscriptionRecord Subscription(SqlDataReader r) => new()
    {
        SubscriptionId = r.GetGuid("SubscriptionId"),
        TopicId = r.GetGuid("TopicId"),
        TopicName = r.GetString("TopicName"),
        Name = r.GetString("Name"),
        OwnerAppId = r.GetGuid("OwnerAppId"),
        Status = r.GetString("Status"),
        DeliveryMode = r.GetString("DeliveryMode"),
        WebhookUrl = r.GetNullableString("WebhookUrl"),
        WebhookSecret = r.GetNullableString("WebhookSecret"),
        PreviousWebhookSecret = r.GetNullableString("PreviousWebhookSecret"),
        PreviousSecretExpiresAt = r.GetNullableUtcDateTime("PreviousSecretExpiresAt"),
        WebhookTimeoutSeconds = r.GetInt32("WebhookTimeoutSeconds"),
        MaxConcurrentDeliveries = r.GetInt32("MaxConcurrentDeliveries"),
        MaxAttempts = r.GetInt32("MaxAttempts"),
        LockDurationSeconds = r.GetInt32("LockDurationSeconds"),
        RetryBaseDelaySeconds = r.GetInt32("RetryBaseDelaySeconds"),
        RetryMaxDelaySeconds = r.GetInt32("RetryMaxDelaySeconds"),
        TtlSeconds = r.GetNullableInt32("TtlSeconds"),
        CreatedAt = r.GetUtcDateTime("CreatedAt"),
        UpdatedAt = r.GetUtcDateTime("UpdatedAt"),
        PendingCount = r.GetInt32("PendingCount"),
        LeasedCount = r.GetInt32("LeasedCount"),
        DeadLetteredCount = r.GetInt32("DeadLetteredCount"),
    };

    public static PushSubscriptionRecord PushSubscription(SqlDataReader r) => new()
    {
        SubscriptionId = r.GetGuid("SubscriptionId"),
        TopicId = r.GetGuid("TopicId"),
        Name = r.GetString("Name"),
        DeliveryMode = r.GetString("DeliveryMode"),
        WebhookUrl = r.GetNullableString("WebhookUrl"),
        WebhookSecret = r.GetNullableString("WebhookSecret"),
        PreviousWebhookSecret = r.GetNullableString("PreviousWebhookSecret"),
        PreviousSecretExpiresAt = r.GetNullableUtcDateTime("PreviousSecretExpiresAt"),
        WebhookTimeoutSeconds = r.GetInt32("WebhookTimeoutSeconds"),
        MaxConcurrentDeliveries = r.GetInt32("MaxConcurrentDeliveries"),
        LockDurationSeconds = r.GetInt32("LockDurationSeconds"),
        HasDueDeliveries = r.GetBoolean("HasDueDeliveries"),
    };

    public static PublishRecord Publish(SqlDataReader r) => new()
    {
        MessageId = r.GetGuid("MessageId"),
        DeliveryCount = r.GetInt32("DeliveryCount"),
        IsDuplicate = r.GetBoolean("IsDuplicate"),
    };

    public static LeasedDeliveryRecord LeasedDelivery(SqlDataReader r) => new()
    {
        DeliveryId = r.GetInt64("DeliveryId"),
        LockToken = r.GetGuid("LockToken"),
        LockedUntil = r.GetUtcDateTime("LockedUntil"),
        Attempt = r.GetInt32("Attempt"),
        MessageId = r.GetGuid("MessageId"),
        MessageType = r.GetString("MessageType"),
        CorrelationId = r.GetString("CorrelationId"),
        CreatedAt = r.GetUtcDateTime("CreatedAt"),
        Properties = r.GetNullableString("Properties"),
        Payload = r.GetString("Payload"),
    };

    public static NackRecord Nack(SqlDataReader r) => new()
    {
        DeadLettered = r.GetBoolean("DeadLettered"),
        NextAvailableAt = r.GetNullableUtcDateTime("NextAvailableAt"),
        MessageId = r.GetGuid("MessageId"),
    };

    public static MessageRecord Message(SqlDataReader r) => new()
    {
        MessageId = r.GetGuid("MessageId"),
        TopicName = r.GetString("TopicName"),
        MessageType = r.GetString("MessageType"),
        CorrelationId = r.GetString("CorrelationId"),
        PublisherAppId = r.GetGuid("PublisherAppId"),
        IdempotencyKey = r.GetNullableString("IdempotencyKey"),
        Properties = r.GetNullableString("Properties"),
        Payload = r.GetString("Payload"),
        CreatedAt = r.GetUtcDateTime("CreatedAt"),
        ExpiresAt = r.GetNullableUtcDateTime("ExpiresAt"),
        Status = r.GetString("Status"),
    };

    public static MessageDeliveryRecord MessageDelivery(SqlDataReader r) => new()
    {
        DeliveryId = r.GetInt64("DeliveryId"),
        SubscriptionId = r.GetGuid("SubscriptionId"),
        SubscriptionName = r.GetString("SubscriptionName"),
        DeliveryMode = r.GetString("DeliveryMode"),
        Status = r.GetByte("Status"),
        AttemptCount = r.GetInt32("AttemptCount"),
        TotalAttemptCount = r.GetInt32("TotalAttemptCount"),
        AvailableAt = r.GetUtcDateTime("AvailableAt"),
        LockedUntil = r.GetNullableUtcDateTime("LockedUntil"),
        ExpiresAt = r.GetNullableUtcDateTime("ExpiresAt"),
        CompletedAt = r.GetNullableUtcDateTime("CompletedAt"),
        DeadLetterReason = r.GetNullableString("DeadLetterReason"),
    };

    public static AttemptRecord Attempt(SqlDataReader r) => new()
    {
        AttemptId = r.GetInt64("AttemptId"),
        DeliveryId = r.GetInt64("DeliveryId"),
        AttemptNumber = r.GetInt32("AttemptNumber"),
        Channel = r.GetString("Channel"),
        LeasedAt = r.GetUtcDateTime("LeasedAt"),
        EndedAt = r.GetNullableUtcDateTime("EndedAt"),
        Outcome = r.GetNullableString("Outcome"),
        HttpStatusCode = r.GetNullableInt32("HttpStatusCode"),
        DurationMs = r.GetNullableInt32("DurationMs"),
        ErrorCode = r.GetNullableString("ErrorCode"),
        ErrorMessage = r.GetNullableString("ErrorMessage"),
        ErrorDetail = r.GetNullableString("ErrorDetail"),
    };

    public static MessageSummaryRecord MessageSummary(SqlDataReader r) => new()
    {
        MessageId = r.GetGuid("MessageId"),
        TopicName = r.GetString("TopicName"),
        MessageType = r.GetString("MessageType"),
        CorrelationId = r.GetString("CorrelationId"),
        PublisherAppId = r.GetGuid("PublisherAppId"),
        CreatedAt = r.GetUtcDateTime("CreatedAt"),
        ExpiresAt = r.GetNullableUtcDateTime("ExpiresAt"),
        Status = r.GetString("Status"),
        DeliveryCount = r.GetInt32("DeliveryCount"),
    };

    /// <summary>A dead letter without the message body: usp_Admin_DeadLetter_Search lists no Properties or Payload.</summary>
    public static DeadLetterRecord DeadLetterSummary(SqlDataReader r) => new()
    {
        DeadLetterId = r.GetInt64("DeadLetterId"),
        DeliveryId = r.GetInt64("DeliveryId"),
        MessageId = r.GetGuid("MessageId"),
        SubscriptionId = r.GetGuid("SubscriptionId"),
        SubscriptionName = r.GetString("SubscriptionName"),
        TopicId = r.GetGuid("TopicId"),
        TopicName = r.GetString("TopicName"),
        Reason = r.GetString("Reason"),
        AttemptCount = r.GetInt32("AttemptCount"),
        LastError = r.GetNullableString("LastError"),
        FirstFailureAt = r.GetNullableUtcDateTime("FirstFailureAt"),
        LastFailureAt = r.GetNullableUtcDateTime("LastFailureAt"),
        DeadLetteredAt = r.GetUtcDateTime("DeadLetteredAt"),
        RequeuedAt = r.GetNullableUtcDateTime("RequeuedAt"),
        RequeuedBy = r.GetNullableGuid("RequeuedBy"),
        MessageType = r.GetString("MessageType"),
        CorrelationId = r.GetString("CorrelationId"),
        MessageCreatedAt = r.GetUtcDateTime("MessageCreatedAt"),
    };

    /// <summary>A row of broker.vw_DeadLetterDetails, with the message body (usp_DeadLetter_List).</summary>
    public static DeadLetterRecord DeadLetter(SqlDataReader r) => DeadLetterSummary(r) with
    {
        Properties = r.GetNullableString("Properties"),
        Payload = r.GetString("Payload"),
    };

    public static HeartbeatRecord Heartbeat(SqlDataReader r) => new()
    {
        LastBeatAt = r.GetNullableUtcDateTime("LastBeatAt"),
        DbNow = r.GetUtcDateTime("DbNow"),
    };

    public static PurgeRecord Purge(SqlDataReader r) => new()
    {
        DeliveriesDeleted = r.GetInt32("DeliveriesDeleted"),
        MessagesDeleted = r.GetInt32("MessagesDeleted"),
    };

    public static OverviewTotalsRecord OverviewTotals(SqlDataReader r) => new()
    {
        PendingCount = r.GetInt64("PendingCount"),
        LeasedCount = r.GetInt64("LeasedCount"),
        DeadLetteredCount = r.GetInt64("DeadLetteredCount"),
        PublishedInWindow = r.GetInt64("PublishedInWindow"),
        CompletedInWindow = r.GetInt64("CompletedInWindow"),
        WindowMinutes = r.GetInt32("WindowMinutes"),
        GeneratedAt = r.GetUtcDateTime("GeneratedAt"),
    };

    public static ThroughputRecord Throughput(SqlDataReader r) => new()
    {
        Minute = r.GetUtcDateTime("Minute"),
        Published = r.GetInt32("Published"),
        Completed = r.GetInt32("Completed"),
        Failed = r.GetInt32("Failed"),
        DeadLettered = r.GetInt32("DeadLettered"),
    };

    public static SubscriptionHealthRecord SubscriptionHealth(SqlDataReader r) => new()
    {
        SubscriptionId = r.GetGuid("SubscriptionId"),
        Name = r.GetString("Name"),
        TopicId = r.GetGuid("TopicId"),
        TopicName = r.GetString("TopicName"),
        OwnerAppId = r.GetGuid("OwnerAppId"),
        Status = r.GetString("Status"),
        DeliveryMode = r.GetString("DeliveryMode"),
        PendingCount = r.GetInt32("PendingCount"),
        LeasedCount = r.GetInt32("LeasedCount"),
        DeadLetteredCount = r.GetInt32("DeadLetteredCount"),
    };

    public static MessageSearchRecord MessageSearch(SqlDataReader r) => new()
    {
        MessageSeq = r.GetInt64("MessageSeq"),
        MessageId = r.GetGuid("MessageId"),
        TopicName = r.GetString("TopicName"),
        MessageType = r.GetString("MessageType"),
        CorrelationId = r.GetString("CorrelationId"),
        PublisherAppId = r.GetGuid("PublisherAppId"),
        PublisherName = r.GetString("PublisherName"),
        CreatedAt = r.GetUtcDateTime("CreatedAt"),
        ExpiresAt = r.GetNullableUtcDateTime("ExpiresAt"),
        Status = r.GetString("Status"),
        DeliveryCount = r.GetInt32("DeliveryCount"),
        PendingCount = r.GetInt32("PendingCount"),
        LeasedCount = r.GetInt32("LeasedCount"),
        CompletedCount = r.GetInt32("CompletedCount"),
        DeadLetteredCount = r.GetInt32("DeadLetteredCount"),
    };

    // ---- records to procedure parameters ----

    public static SqlParameter[] Parameters(PublishCommand c) =>
    [
        Param("MessageId", c.MessageId),
        Param("TopicName", c.TopicName),
        Param("AppId", c.AppId),
        Param("MessageType", c.MessageType),
        Param("CorrelationId", c.CorrelationId),
        Param("IdempotencyKey", c.IdempotencyKey),
        Param("Payload", c.Payload),
        Param("Properties", c.Properties),
        Param("TtlSeconds", c.TtlSeconds),
    ];

    public static SqlParameter[] Parameters(PermissionRecord p) =>
    [
        Param("AppId", p.AppId),
        Param("ResourceType", p.ResourceType),
        Param("ResourceId", p.ResourceId),
        Param("Permission", p.Permission),
    ];

    public static SqlParameter[] Parameters(SubscriptionCreate c) =>
    [
        Param("SubscriptionId", c.SubscriptionId),
        Param("TopicId", c.TopicId),
        Param("Name", c.Name),
        Param("OwnerAppId", c.OwnerAppId),
        Param("DeliveryMode", c.DeliveryMode),
        Param("WebhookUrl", c.WebhookUrl),
        Param("WebhookSecret", c.WebhookSecret),
        Param("WebhookTimeoutSeconds", c.WebhookTimeoutSeconds),
        Param("MaxConcurrentDeliveries", c.MaxConcurrentDeliveries),
        Param("MaxAttempts", c.MaxAttempts),
        Param("LockDurationSeconds", c.LockDurationSeconds),
        Param("RetryBaseDelaySeconds", c.RetryBaseDelaySeconds),
        Param("RetryMaxDelaySeconds", c.RetryMaxDelaySeconds),
        Param("TtlSeconds", c.TtlSeconds),
    ];

    public static SqlParameter[] Parameters(SubscriptionUpdate u) =>
    [
        Param("SubscriptionId", u.SubscriptionId),
        Param("Status", u.Status),
        Param("WebhookUrl", u.WebhookUrl),
        Param("WebhookTimeoutSeconds", u.WebhookTimeoutSeconds),
        Param("MaxConcurrentDeliveries", u.MaxConcurrentDeliveries),
        Param("MaxAttempts", u.MaxAttempts),
        Param("LockDurationSeconds", u.LockDurationSeconds),
        Param("RetryBaseDelaySeconds", u.RetryBaseDelaySeconds),
        Param("RetryMaxDelaySeconds", u.RetryMaxDelaySeconds),
        Param("TtlSeconds", u.TtlSeconds),
    ];

    public static SqlParameter[] Parameters(MessageSearchQuery q) =>
    [
        Param("TopicId", q.TopicId),
        Param("Status", q.Status),
        Param("MessageType", q.MessageType),
        Param("CorrelationId", q.CorrelationId),
        Param("PublisherAppId", q.PublisherAppId),
        Param("From", q.From),
        Param("To", q.To),
        Param("BeforeSeq", q.BeforeSeq),
        Param("PageSize", q.PageSize),
    ];

    public static SqlParameter[] Parameters(DeadLetterSearchQuery q) =>
    [
        Param("TopicId", q.TopicId),
        Param("SubscriptionId", q.SubscriptionId),
        Param("Reason", q.Reason),
        Param("From", q.From),
        Param("To", q.To),
        Param("IncludeRequeued", q.IncludeRequeued),
        Param("BeforeId", q.BeforeId),
        Param("PageSize", q.PageSize),
    ];
}
