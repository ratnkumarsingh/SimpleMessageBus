using System.Text.Json;

namespace MessageBroker.Contracts.Models;

/// <summary>Body of POST /api/v1/topics/{topicName}/messages. The Idempotency-Key travels as a header.</summary>
public sealed record PublishRequest
{
    public string? MessageType { get; init; }
    /// <summary>Defaults to the new message ID when omitted.</summary>
    public string? CorrelationId { get; init; }
    public int? TtlSeconds { get; init; }
    /// <summary>A JSON object whose values are all strings.</summary>
    public JsonElement? Properties { get; init; }
    public JsonElement? Payload { get; init; }
}

/// <summary>201 for a new message; 200 with the original ID for a duplicate Idempotency-Key.</summary>
public sealed record PublishResponse(Guid MessageId, int DeliveryCount);

public sealed record MessageResponse
{
    public Guid MessageId { get; init; }
    public string TopicName { get; init; } = "";
    public string MessageType { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public Guid PublisherAppId { get; init; }
    public string? IdempotencyKey { get; init; }
    /// <summary>InProgress, Completed, PartiallyDeadLettered or DeadLettered.</summary>
    public string Status { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public JsonElement? Properties { get; init; }
    public JsonElement Payload { get; init; }
    public IReadOnlyList<MessageDeliveryResponse> Deliveries { get; init; } = [];
}

public sealed record MessageDeliveryResponse
{
    public long DeliveryId { get; init; }
    public Guid SubscriptionId { get; init; }
    public string SubscriptionName { get; init; } = "";
    public string DeliveryMode { get; init; } = "";
    /// <summary>Pending, Leased, Completed, DeadLettered or Cancelled.</summary>
    public string Status { get; init; } = "";
    public int AttemptCount { get; init; }
    public int TotalAttemptCount { get; init; }
    public DateTime AvailableAt { get; init; }
    public DateTime? LockedUntil { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? DeadLetterReason { get; init; }
    public IReadOnlyList<DeliveryAttemptResponse> Attempts { get; init; } = [];
}

public sealed record DeliveryAttemptResponse
{
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

/// <summary>A row of GET /api/v1/messages?correlationId=.</summary>
public sealed record MessageSummaryResponse
{
    public Guid MessageId { get; init; }
    public string TopicName { get; init; } = "";
    public string MessageType { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public Guid PublisherAppId { get; init; }
    public string Status { get; init; } = "";
    public int DeliveryCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
}
