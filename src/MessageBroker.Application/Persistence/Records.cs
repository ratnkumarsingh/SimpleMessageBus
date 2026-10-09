namespace MessageBroker.Application.Persistence;

// Rows returned by the stored procedures. Property names match the result-set column names, and
// every datetime2 value is UTC (RecordMap in Infrastructure stamps DateTimeKind.Utc when reading).

public sealed record ApplicationRecord
{
    public Guid AppId { get; init; }
    public string Name { get; init; } = "";
    public bool IsAdmin { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record ApiKeyRecord
{
    public Guid KeyId { get; init; }
    public Guid AppId { get; init; }
    public string Prefix { get; init; } = "";
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>What authentication needs to verify a presented key.</summary>
public sealed record ApiKeyLookup
{
    public Guid KeyId { get; init; }
    public Guid AppId { get; init; }
    public byte[] Hash { get; init; } = [];
    public string AppName { get; init; } = "";
    public bool IsAdmin { get; init; }
}

public sealed record PermissionRecord
{
    public Guid AppId { get; init; }
    public string ResourceType { get; init; } = "";
    public Guid ResourceId { get; init; }
    public string Permission { get; init; } = "";
}

public sealed record AllowedHostRecord
{
    public string Host { get; init; } = "";
    public Guid? AddedBy { get; init; }
    public DateTime AddedAt { get; init; }
}

public sealed record TopicRecord
{
    public Guid TopicId { get; init; }
    public string Name { get; init; } = "";
    public int? DefaultTtlSeconds { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>A row of broker.vw_SubscriptionDetails. Secrets are Data Protection payloads.</summary>
public sealed record SubscriptionRecord
{
    public Guid SubscriptionId { get; init; }
    public Guid TopicId { get; init; }
    public string TopicName { get; init; } = "";
    public string Name { get; init; } = "";
    public Guid OwnerAppId { get; init; }
    public string Status { get; init; } = "";
    public string DeliveryMode { get; init; } = "";
    public string? WebhookUrl { get; init; }
    public string? WebhookSecret { get; init; }
    public string? PreviousWebhookSecret { get; init; }
    public DateTime? PreviousSecretExpiresAt { get; init; }
    public int WebhookTimeoutSeconds { get; init; }
    public int MaxConcurrentDeliveries { get; init; }
    public int MaxAttempts { get; init; }
    public int LockDurationSeconds { get; init; }
    public int RetryBaseDelaySeconds { get; init; }
    public int RetryMaxDelaySeconds { get; init; }
    public int? TtlSeconds { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public int PendingCount { get; init; }
    public int LeasedCount { get; init; }
    public int DeadLetteredCount { get; init; }
}

/// <summary>A row of broker.vw_ActivePushSubscriptions.</summary>
public sealed record PushSubscriptionRecord
{
    public Guid SubscriptionId { get; init; }
    public Guid TopicId { get; init; }
    public string Name { get; init; } = "";
    public string DeliveryMode { get; init; } = "";
    public string? WebhookUrl { get; init; }
    public string? WebhookSecret { get; init; }
    public string? PreviousWebhookSecret { get; init; }
    public DateTime? PreviousSecretExpiresAt { get; init; }
    public int WebhookTimeoutSeconds { get; init; }
    public int MaxConcurrentDeliveries { get; init; }
    public int LockDurationSeconds { get; init; }
    public bool HasDueDeliveries { get; init; }
}

public sealed record PublishCommand(
    Guid MessageId,
    string TopicName,
    Guid AppId,
    string MessageType,
    string CorrelationId,
    string? IdempotencyKey,
    string Payload,
    string? Properties,
    int? TtlSeconds);

public sealed record PublishRecord
{
    public Guid MessageId { get; init; }
    public int DeliveryCount { get; init; }
    public bool IsDuplicate { get; init; }
}

public sealed record LeasedDeliveryRecord
{
    public long DeliveryId { get; init; }
    public Guid LockToken { get; init; }
    public DateTime LockedUntil { get; init; }
    public int Attempt { get; init; }
    public Guid MessageId { get; init; }
    public string MessageType { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public string? Properties { get; init; }
    public string Payload { get; init; } = "";
}

public sealed record NackRecord
{
    public bool DeadLettered { get; init; }
    public DateTime? NextAvailableAt { get; init; }
    public Guid MessageId { get; init; }
}

public sealed record FailureDetails(
    string? ErrorCode,
    string? ErrorMessage,
    string? ErrorDetail = null,
    int? HttpStatusCode = null,
    bool DeadLetter = false);

public sealed record MessageRecord
{
    public Guid MessageId { get; init; }
    public string TopicName { get; init; } = "";
    public string MessageType { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public Guid PublisherAppId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? Properties { get; init; }
    public string Payload { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string Status { get; init; } = "";
}

public sealed record MessageDeliveryRecord
{
    public long DeliveryId { get; init; }
    public Guid SubscriptionId { get; init; }
    public string SubscriptionName { get; init; } = "";
    public string DeliveryMode { get; init; } = "";
    public byte Status { get; init; }
    public int AttemptCount { get; init; }
    public int TotalAttemptCount { get; init; }
    public DateTime AvailableAt { get; init; }
    public DateTime? LockedUntil { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? DeadLetterReason { get; init; }
}

public sealed record AttemptRecord
{
    public long AttemptId { get; init; }
    public long DeliveryId { get; init; }
    public int AttemptNumber { get; init; }
    public string Channel { get; init; } = "";
    public DateTime LeasedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public string? Outcome { get; init; }
    public int? HttpStatusCode { get; init; }
    public int? DurationMs { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorDetail { get; init; }
}

public sealed record MessageDetailsRecord(
    MessageRecord Message,
    IReadOnlyList<MessageDeliveryRecord> Deliveries,
    IReadOnlyList<AttemptRecord> Attempts);

public sealed record MessageSummaryRecord
{
    public Guid MessageId { get; init; }
    public string TopicName { get; init; } = "";
    public string MessageType { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public Guid PublisherAppId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string Status { get; init; } = "";
    public int DeliveryCount { get; init; }
}

/// <summary>A row of broker.vw_DeadLetterDetails.</summary>
public sealed record DeadLetterRecord
{
    public long DeadLetterId { get; init; }
    public long DeliveryId { get; init; }
    public Guid MessageId { get; init; }
    public Guid SubscriptionId { get; init; }
    public string SubscriptionName { get; init; } = "";
    public Guid TopicId { get; init; }
    public string TopicName { get; init; } = "";
    public string Reason { get; init; } = "";
    public int AttemptCount { get; init; }
    public string? LastError { get; init; }
    public DateTime? FirstFailureAt { get; init; }
    public DateTime? LastFailureAt { get; init; }
    public DateTime DeadLetteredAt { get; init; }
    public DateTime? RequeuedAt { get; init; }
    public Guid? RequeuedBy { get; init; }
    public string MessageType { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public DateTime MessageCreatedAt { get; init; }
    public string? Properties { get; init; }
    public string Payload { get; init; } = "";
}

public sealed record HeartbeatRecord
{
    public DateTime? LastBeatAt { get; init; }
    public DateTime DbNow { get; init; }
}

public sealed record PurgeRecord
{
    public int DeliveriesDeleted { get; init; }
    public int MessagesDeleted { get; init; }
}

// ---- admin dashboard (Dashboard.sql) ----

public sealed record OverviewTotalsRecord
{
    public long PendingCount { get; init; }
    public long LeasedCount { get; init; }
    public long DeadLetteredCount { get; init; }
    public long PublishedInWindow { get; init; }
    public long CompletedInWindow { get; init; }
    public int WindowMinutes { get; init; }
    public DateTime GeneratedAt { get; init; }
}

/// <summary>One minute of the throughput series.</summary>
public sealed record ThroughputRecord
{
    public DateTime Minute { get; init; }
    public int Published { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
    public int DeadLettered { get; init; }
}

/// <summary>A subscription with its backlog; deliberately without the webhook URL or secrets.</summary>
public sealed record SubscriptionHealthRecord
{
    public Guid SubscriptionId { get; init; }
    public string Name { get; init; } = "";
    public Guid TopicId { get; init; }
    public string TopicName { get; init; } = "";
    public Guid OwnerAppId { get; init; }
    public string Status { get; init; } = "";
    public string DeliveryMode { get; init; } = "";
    public int PendingCount { get; init; }
    public int LeasedCount { get; init; }
    public int DeadLetteredCount { get; init; }
}

public sealed record OverviewRecord(
    OverviewTotalsRecord Totals,
    IReadOnlyList<ThroughputRecord> Series,
    IReadOnlyList<SubscriptionHealthRecord> Subscriptions,
    HeartbeatRecord Heartbeat);

/// <summary>Parameters of usp_Admin_Message_Search; null means "any".</summary>
public sealed record MessageSearchQuery(
    Guid? TopicId = null,
    string? Status = null,
    string? MessageType = null,
    string? CorrelationId = null,
    Guid? PublisherAppId = null,
    DateTime? From = null,
    DateTime? To = null,
    long? BeforeSeq = null,
    int PageSize = 50);

public sealed record MessageSearchRecord
{
    public long MessageSeq { get; init; }
    public Guid MessageId { get; init; }
    public string TopicName { get; init; } = "";
    public string MessageType { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public Guid PublisherAppId { get; init; }
    public string PublisherName { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string Status { get; init; } = "";
    public int DeliveryCount { get; init; }
    public int PendingCount { get; init; }
    public int LeasedCount { get; init; }
    public int CompletedCount { get; init; }
    public int DeadLetteredCount { get; init; }
}

/// <summary>Parameters of usp_Admin_DeadLetter_Search; null means "any".</summary>
public sealed record DeadLetterSearchQuery(
    Guid? TopicId = null,
    Guid? SubscriptionId = null,
    string? Reason = null,
    DateTime? From = null,
    DateTime? To = null,
    bool IncludeRequeued = false,
    long? BeforeId = null,
    int PageSize = 50);
