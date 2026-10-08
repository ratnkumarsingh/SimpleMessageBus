using System.Text.Json;
using MessageBroker.Application.Persistence;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;

namespace MessageBroker.Application.Services;

/// <summary>Persistence records to wire models. Secrets never leave through these.</summary>
public static class Mapping
{
    public static TopicResponse ToResponse(this TopicRecord t) => new(t.TopicId, t.Name, t.DefaultTtlSeconds, t.CreatedAt);

    public static SubscriptionResponse ToResponse(this SubscriptionRecord s, string? plainSecret = null) => new()
    {
        SubscriptionId = s.SubscriptionId,
        TopicId = s.TopicId,
        TopicName = s.TopicName,
        Name = s.Name,
        OwnerAppId = s.OwnerAppId,
        Status = s.Status,
        DeliveryMode = s.DeliveryMode,
        WebhookUrl = s.WebhookUrl,
        WebhookTimeoutSeconds = s.WebhookTimeoutSeconds,
        MaxConcurrentDeliveries = s.MaxConcurrentDeliveries,
        MaxAttempts = s.MaxAttempts,
        LockDurationSeconds = s.LockDurationSeconds,
        RetryBaseDelaySeconds = s.RetryBaseDelaySeconds,
        RetryMaxDelaySeconds = s.RetryMaxDelaySeconds,
        TtlSeconds = s.TtlSeconds,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
        PreviousSecretExpiresAt = s.PreviousSecretExpiresAt,
        Counts = new DeliveryCountsResponse(s.PendingCount, s.LeasedCount, s.DeadLetteredCount),
        WebhookSecret = plainSecret,
    };

    public static ApplicationResponse ToResponse(this ApplicationRecord a) => new(a.AppId, a.Name, a.IsAdmin, a.IsActive, a.CreatedAt);

    public static ApiKeyResponse ToResponse(this ApiKeyRecord k, string? plainKey = null) =>
        new(k.KeyId, k.AppId, k.Prefix, k.IsActive, k.CreatedAt, k.ExpiresAt) { ApiKey = plainKey };

    public static PermissionResponse ToResponse(this PermissionRecord p) => new(p.AppId, p.ResourceType, p.ResourceId, p.Permission);

    public static AllowedHostResponse ToResponse(this AllowedHostRecord h) => new(h.Host, h.AddedBy, h.AddedAt);

    public static MessageResponse ToResponse(this MessageDetailsRecord details)
    {
        var attemptsByDelivery = details.Attempts.ToLookup(a => a.DeliveryId);
        var m = details.Message;
        return new MessageResponse
        {
            MessageId = m.MessageId,
            TopicName = m.TopicName,
            MessageType = m.MessageType,
            CorrelationId = m.CorrelationId,
            PublisherAppId = m.PublisherAppId,
            IdempotencyKey = m.IdempotencyKey,
            Status = m.Status,
            CreatedAt = m.CreatedAt,
            ExpiresAt = m.ExpiresAt,
            Properties = m.Properties is null ? null : ParseJson(m.Properties),
            Payload = ParseJson(m.Payload),
            Deliveries = details.Deliveries.Select(d => new MessageDeliveryResponse
            {
                DeliveryId = d.DeliveryId,
                SubscriptionId = d.SubscriptionId,
                SubscriptionName = d.SubscriptionName,
                DeliveryMode = d.DeliveryMode,
                Status = ((DeliveryStatus)d.Status).ToString(),
                AttemptCount = d.AttemptCount,
                TotalAttemptCount = d.TotalAttemptCount,
                AvailableAt = d.AvailableAt,
                LockedUntil = d.LockedUntil,
                ExpiresAt = d.ExpiresAt,
                CompletedAt = d.CompletedAt,
                DeadLetterReason = d.DeadLetterReason,
                Attempts = attemptsByDelivery[d.DeliveryId].Select(a => new DeliveryAttemptResponse
                {
                    AttemptNumber = a.AttemptNumber,
                    Channel = a.Channel,
                    LeasedAt = a.LeasedAt,
                    EndedAt = a.EndedAt,
                    Outcome = a.Outcome,
                    HttpStatusCode = a.HttpStatusCode,
                    DurationMs = a.DurationMs,
                    ErrorCode = a.ErrorCode,
                    ErrorMessage = a.ErrorMessage,
                    ErrorDetail = a.ErrorDetail,
                }).ToList(),
            }).ToList(),
        };
    }

    public static MessageSummaryResponse ToResponse(this MessageSummaryRecord m) => new()
    {
        MessageId = m.MessageId,
        TopicName = m.TopicName,
        MessageType = m.MessageType,
        CorrelationId = m.CorrelationId,
        PublisherAppId = m.PublisherAppId,
        Status = m.Status,
        DeliveryCount = m.DeliveryCount,
        CreatedAt = m.CreatedAt,
        ExpiresAt = m.ExpiresAt,
    };

    public static Delivery ToDelivery(this LeasedDeliveryRecord d) => new()
    {
        DeliveryId = d.DeliveryId,
        LockToken = d.LockToken,
        LockedUntil = d.LockedUntil,
        Attempt = d.Attempt,
        Message = new DeliveredMessage
        {
            MessageId = d.MessageId,
            MessageType = d.MessageType,
            CorrelationId = d.CorrelationId,
            CreatedAt = d.CreatedAt,
            Properties = d.Properties is null ? null : ParseJson(d.Properties),
            Payload = ParseJson(d.Payload),
        },
    };

    public static DeadLetterResponse ToResponse(this DeadLetterRecord d) => new()
    {
        DeadLetterId = d.DeadLetterId,
        DeliveryId = d.DeliveryId,
        MessageId = d.MessageId,
        SubscriptionId = d.SubscriptionId,
        SubscriptionName = d.SubscriptionName,
        TopicName = d.TopicName,
        Reason = d.Reason,
        AttemptCount = d.AttemptCount,
        LastError = d.LastError,
        FirstFailureAt = d.FirstFailureAt,
        LastFailureAt = d.LastFailureAt,
        DeadLetteredAt = d.DeadLetteredAt,
        RequeuedAt = d.RequeuedAt,
        RequeuedBy = d.RequeuedBy,
        MessageType = d.MessageType,
        CorrelationId = d.CorrelationId,
        MessageCreatedAt = d.MessageCreatedAt,
        Properties = d.Properties is null ? null : ParseJson(d.Properties),
        // The broker-wide DLQ search leaves the payload out; it is JSON null there.
        Payload = d.Payload.Length == 0 ? NullJson : ParseJson(d.Payload),
    };

    private static readonly JsonElement NullJson = ParseJson("null");

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
