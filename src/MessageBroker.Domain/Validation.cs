using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MessageBroker.Domain;

public sealed record BrokerLimits(int MaxPayloadBytes = 262_144, int MaxPropertiesBytes = 4_096);

/// <summary>A publish request as received, before validation.</summary>
public sealed record PublishEnvelope(
    string? MessageType,
    string? CorrelationId,
    string? IdempotencyKey,
    int? TtlSeconds,
    JsonElement? Properties,
    JsonElement? Payload);

/// <summary>A publish request that passed validation, with payload and properties as raw JSON text.</summary>
public sealed record ValidatedEnvelope(
    string MessageType,
    string? CorrelationId,
    string? IdempotencyKey,
    int? TtlSeconds,
    string PayloadJson,
    string? PropertiesJson);

public static class EnvelopeValidator
{
    public const int MaxMessageTypeLength = 200;
    public const int MaxCorrelationIdLength = 100;
    public const int MaxIdempotencyKeyLength = 100;

    /// <exception cref="BrokerValidationException">The envelope breaks a rule.</exception>
    /// <exception cref="BrokerException">Kind PayloadTooLarge when the payload exceeds the limit.</exception>
    public static ValidatedEnvelope Validate(PublishEnvelope envelope, BrokerLimits limits)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(envelope.MessageType))
            errors.Add(new("messageType", "messageType is required."));
        else if (envelope.MessageType.Length > MaxMessageTypeLength)
            errors.Add(new("messageType", $"messageType must be at most {MaxMessageTypeLength} characters."));

        if (envelope.CorrelationId is { Length: > MaxCorrelationIdLength })
            errors.Add(new("correlationId", $"correlationId must be at most {MaxCorrelationIdLength} characters."));
        if (envelope.CorrelationId is { Length: 0 })
            errors.Add(new("correlationId", "correlationId must not be empty when present."));

        if (envelope.IdempotencyKey is { Length: > MaxIdempotencyKeyLength })
            errors.Add(new("Idempotency-Key", $"Idempotency-Key must be at most {MaxIdempotencyKeyLength} characters."));
        if (envelope.IdempotencyKey is { Length: 0 })
            errors.Add(new("Idempotency-Key", "Idempotency-Key must not be empty when present."));

        if (envelope.TtlSeconds is <= 0)
            errors.Add(new("ttlSeconds", "ttlSeconds must be greater than zero."));

        string? propertiesJson = null;
        if (envelope.Properties is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } properties)
        {
            if (properties.ValueKind != JsonValueKind.Object)
                errors.Add(new("properties", "properties must be a JSON object."));
            else if (properties.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String))
                errors.Add(new("properties", "Every property value must be a string."));
            else
            {
                propertiesJson = properties.GetRawText();
                if (Encoding.UTF8.GetByteCount(propertiesJson) > limits.MaxPropertiesBytes)
                    errors.Add(new("properties", $"properties must be at most {limits.MaxPropertiesBytes} bytes."));
            }
        }

        string? payloadJson = null;
        if (envelope.Payload is not { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } payload)
            errors.Add(new("payload", "payload is required."));
        else
            payloadJson = payload.GetRawText();

        BrokerValidationException.ThrowIfAny(errors);

        if (Encoding.UTF8.GetByteCount(payloadJson!) > limits.MaxPayloadBytes)
            throw new BrokerException(BrokerErrorKind.PayloadTooLarge,
                $"The payload exceeds the limit of {limits.MaxPayloadBytes} bytes. Pass large documents by reference.");

        return new ValidatedEnvelope(envelope.MessageType!, envelope.CorrelationId, envelope.IdempotencyKey,
            envelope.TtlSeconds, payloadJson!, propertiesJson);
    }
}

public static partial class NameRules
{
    public const int MaxLength = 100;

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex NamePattern();

    public static bool IsValid(string? name) => name is not null && NamePattern().IsMatch(name);

    public static void Validate(string? name, string field)
    {
        if (!IsValid(name))
            throw new BrokerValidationException(
                [new(field, $"{field} must be 1-{MaxLength} characters of letters, digits, '.', '_' and '-'.")]);
    }
}

public sealed record SubscriptionSettings(
    DeliveryMode DeliveryMode,
    string? WebhookUrl,
    int WebhookTimeoutSeconds,
    int MaxConcurrentDeliveries,
    int MaxAttempts,
    int LockDurationSeconds,
    int RetryBaseDelaySeconds,
    int RetryMaxDelaySeconds,
    int? TtlSeconds);

public static class SubscriptionSettingsValidator
{
    public const int MaxLockDurationSeconds = 600;

    /// <param name="requireHttps">False only in development and tests; production always requires HTTPS.</param>
    public static IReadOnlyList<ValidationError> Validate(SubscriptionSettings s, bool requireHttps = true)
    {
        var errors = new List<ValidationError>();

        if (s.DeliveryMode == DeliveryMode.Webhook)
        {
            if (!Uri.TryCreate(s.WebhookUrl, UriKind.Absolute, out var uri))
                errors.Add(new("webhookUrl", "webhookUrl must be an absolute URL for Webhook subscriptions."));
            else if (uri.Scheme != Uri.UriSchemeHttps && (requireHttps || uri.Scheme != Uri.UriSchemeHttp))
                errors.Add(new("webhookUrl", "webhookUrl must use HTTPS."));

            if (s.WebhookTimeoutSeconds < 1)
                errors.Add(new("webhookTimeoutSeconds", "webhookTimeoutSeconds must be at least 1."));
            else if (s.WebhookTimeoutSeconds >= s.LockDurationSeconds)
                errors.Add(new("webhookTimeoutSeconds", "webhookTimeoutSeconds must be shorter than lockDurationSeconds."));
        }
        else if (s.WebhookUrl is not null)
        {
            errors.Add(new("webhookUrl", "webhookUrl is only allowed for Webhook subscriptions."));
        }

        if (s.LockDurationSeconds is < 1 or > MaxLockDurationSeconds)
            errors.Add(new("lockDurationSeconds", $"lockDurationSeconds must be between 1 and {MaxLockDurationSeconds}."));
        if (s.MaxAttempts is < 1 or > 100)
            errors.Add(new("maxAttempts", "maxAttempts must be between 1 and 100."));
        if (s.MaxConcurrentDeliveries is < 1 or > 256)
            errors.Add(new("maxConcurrentDeliveries", "maxConcurrentDeliveries must be between 1 and 256."));
        if (s.RetryBaseDelaySeconds < 1)
            errors.Add(new("retryBaseDelaySeconds", "retryBaseDelaySeconds must be at least 1."));
        if (s.RetryMaxDelaySeconds < s.RetryBaseDelaySeconds)
            errors.Add(new("retryMaxDelaySeconds", "retryMaxDelaySeconds must be at least retryBaseDelaySeconds."));
        if (s.RetryMaxDelaySeconds > 86_400)
            errors.Add(new("retryMaxDelaySeconds", "retryMaxDelaySeconds must be at most 86400."));
        if (s.TtlSeconds is <= 0)
            errors.Add(new("ttlSeconds", "ttlSeconds must be greater than zero."));

        return errors;
    }
}
