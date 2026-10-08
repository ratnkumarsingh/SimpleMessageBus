using MessageBroker.Application.Dispatch;
using MessageBroker.Application.Persistence;
using MessageBroker.Application.Security;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MessageBroker.Application.Services;

public sealed record PublishResult(PublishResponse Response, bool IsDuplicate);

/// <summary>Publish (spec section 8.1). The response is returned only after usp_Message_Publish commits.</summary>
public sealed class PublishService(
    IMessageRepository messages,
    IDispatcherSignal signal,
    IOptions<BrokerOptions> options,
    ILogger<PublishService> logger)
{
    public async Task<PublishResult> PublishAsync(
        Caller caller, string topicName, PublishRequest request, string? idempotencyKey, CancellationToken ct)
    {
        NameRules.Validate(topicName, "topicName");
        var limits = options.Value.Limits;
        var envelope = EnvelopeValidator.Validate(
            new PublishEnvelope(request.MessageType, request.CorrelationId, idempotencyKey, request.TtlSeconds,
                request.Properties, request.Payload),
            new BrokerLimits(limits.MaxPayloadBytes, limits.MaxPropertiesBytes));

        var messageId = Guid.CreateVersion7();
        var correlationId = envelope.CorrelationId ?? messageId.ToString();
        var result = await messages.PublishAsync(new PublishCommand(
            messageId, topicName, caller.AppId, envelope.MessageType, correlationId, envelope.IdempotencyKey,
            envelope.PayloadJson, envelope.PropertiesJson, envelope.TtlSeconds), ct);

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = result.MessageId,
            ["CorrelationId"] = correlationId,
            ["AppId"] = caller.AppId,
        }))
        {
            if (result.IsDuplicate)
                logger.LogInformation("Duplicate publish ignored for idempotency key {IdempotencyKey} on topic {Topic}",
                    envelope.IdempotencyKey, topicName);
            else if (result.DeliveryCount == 0)
                logger.LogInformation("Message {MessageType} published to {Topic} with no active subscriptions",
                    envelope.MessageType, topicName);
            else
                logger.LogInformation("Message {MessageType} published to {Topic} with {DeliveryCount} deliveries",
                    envelope.MessageType, topicName, result.DeliveryCount);
        }

        if (!result.IsDuplicate && result.DeliveryCount > 0)
            signal.Notify();

        return new PublishResult(new PublishResponse(result.MessageId, result.DeliveryCount), result.IsDuplicate);
    }
}
