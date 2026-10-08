namespace MessageBroker.Contracts.Models;

public sealed record CreateTopicRequest(string? Name, int? DefaultTtlSeconds);

/// <summary>PATCH body. DefaultTtlSeconds null clears the default.</summary>
public sealed record UpdateTopicRequest(int? DefaultTtlSeconds);

public sealed record TopicResponse(Guid TopicId, string Name, int? DefaultTtlSeconds, DateTime CreatedAt);

/// <summary>Settings left null take the broker defaults (Broker:Defaults).</summary>
public sealed record CreateSubscriptionRequest
{
    public string? Name { get; init; }
    public Guid? OwnerAppId { get; init; }
    /// <summary>Webhook, SignalR or Pull.</summary>
    public string? DeliveryMode { get; init; }
    public string? WebhookUrl { get; init; }
    public int? WebhookTimeoutSeconds { get; init; }
    public int? MaxConcurrentDeliveries { get; init; }
    public int? MaxAttempts { get; init; }
    public int? LockDurationSeconds { get; init; }
    public int? RetryBaseDelaySeconds { get; init; }
    public int? RetryMaxDelaySeconds { get; init; }
    public int? TtlSeconds { get; init; }
}

/// <summary>
/// PATCH body: only the fields present change. Status Paused stops delivery while messages keep
/// queuing; Active resumes it. Set ClearTtl to remove the subscription TTL. The delivery mode cannot
/// change; create a new subscription instead.
/// </summary>
public sealed record UpdateSubscriptionRequest
{
    public string? Status { get; init; }
    public string? WebhookUrl { get; init; }
    public int? WebhookTimeoutSeconds { get; init; }
    public int? MaxConcurrentDeliveries { get; init; }
    public int? MaxAttempts { get; init; }
    public int? LockDurationSeconds { get; init; }
    public int? RetryBaseDelaySeconds { get; init; }
    public int? RetryMaxDelaySeconds { get; init; }
    public int? TtlSeconds { get; init; }
    public bool ClearTtl { get; init; }
}

public sealed record SubscriptionResponse
{
    public Guid SubscriptionId { get; init; }
    public Guid TopicId { get; init; }
    public string TopicName { get; init; } = "";
    public string Name { get; init; } = "";
    public Guid OwnerAppId { get; init; }
    public string Status { get; init; } = "";
    public string DeliveryMode { get; init; } = "";
    public string? WebhookUrl { get; init; }
    public int WebhookTimeoutSeconds { get; init; }
    public int MaxConcurrentDeliveries { get; init; }
    public int MaxAttempts { get; init; }
    public int LockDurationSeconds { get; init; }
    public int RetryBaseDelaySeconds { get; init; }
    public int RetryMaxDelaySeconds { get; init; }
    public int? TtlSeconds { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    /// <summary>While set and in the future, webhook calls carry signatures for both secrets.</summary>
    public DateTime? PreviousSecretExpiresAt { get; init; }
    public DeliveryCountsResponse Counts { get; init; } = new(0, 0, 0);
    /// <summary>The plain signing secret. Returned only when it is created or rotated; store it then.</summary>
    public string? WebhookSecret { get; init; }
}

public sealed record DeliveryCountsResponse(int Pending, int Leased, int DeadLettered);
