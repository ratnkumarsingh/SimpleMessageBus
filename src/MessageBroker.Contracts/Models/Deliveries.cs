using System.Text.Json;

namespace MessageBroker.Contracts.Models;

/// <summary>
/// Body of POST /api/v1/subscriptions/{id}/receive. Values out of range are clamped: maxMessages to
/// 1–32 (default 1), waitSeconds to 0–30 (default 0, which returns at once).
/// </summary>
public sealed record ReceiveRequest(int? MaxMessages = null, int? WaitSeconds = null);

/// <summary>
/// A leased delivery, in the same JSON on every channel (spec section 9): the pull receive response,
/// the webhook request body and the SignalR Deliver event. Settle it with <see cref="LockToken"/>
/// before <see cref="LockedUntil"/>.
/// </summary>
public sealed record Delivery
{
    public long DeliveryId { get; init; }
    public Guid LockToken { get; init; }
    public DateTime LockedUntil { get; init; }
    /// <summary>
    /// Attempt within the retry budget: 1 on the first delivery, up to the subscription's maxAttempts.
    /// Starts again at 1 after a DLQ requeue; the message history keeps numbering every attempt.
    /// </summary>
    public int Attempt { get; init; }
    public DeliveredMessage Message { get; init; } = new();
}

/// <summary>The message inside a <see cref="Delivery"/>. Subscribers deduplicate by <see cref="MessageId"/>.</summary>
public sealed record DeliveredMessage
{
    public Guid MessageId { get; init; }
    public string MessageType { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public JsonElement? Properties { get; init; }
    public JsonElement Payload { get; init; }
}

public sealed record AckRequest(Guid LockToken);

/// <summary>
/// Reports a failed attempt. The delivery is retried with backoff, or dead-lettered when attempts
/// run out; DeadLetter = true dead-letters it at once (RejectedBySubscriber).
/// </summary>
public sealed record NackRequest(Guid LockToken, string? ErrorCode = null, string? ErrorMessage = null,
    string? ErrorDetail = null, bool DeadLetter = false);

public sealed record RenewRequest(Guid LockToken);

/// <summary>A DLQ entry with the message it holds.</summary>
public sealed record DeadLetterResponse
{
    public long DeadLetterId { get; init; }
    public long DeliveryId { get; init; }
    public Guid MessageId { get; init; }
    public Guid SubscriptionId { get; init; }
    public string SubscriptionName { get; init; } = "";
    public string TopicName { get; init; } = "";
    /// <summary>MaxAttemptsExceeded, Expired or RejectedBySubscriber.</summary>
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
    public JsonElement? Properties { get; init; }
    /// <summary>JSON null in GET /api/v1/admin/deadletters, which leaves payloads out; read the message for it.</summary>
    public JsonElement Payload { get; init; }
}

/// <summary>
/// One page of DLQ entries, newest first. Pass <see cref="NextBefore"/> as the <c>before</c> query
/// parameter for the next page; it is null on the last page.
/// </summary>
public sealed record DeadLetterPage(IReadOnlyList<DeadLetterResponse> Items, long? NextBefore);
