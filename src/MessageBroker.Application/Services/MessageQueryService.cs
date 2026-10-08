using MessageBroker.Application.Persistence;
using MessageBroker.Application.Security;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;

namespace MessageBroker.Application.Services;

/// <summary>Traceability (spec section 10): a message by ID, or every message sharing a correlation ID.</summary>
public sealed class MessageQueryService(IMessageRepository messages)
{
    /// <summary>Admins read any message; a publisher reads only its own (checked in usp_Message_GetById).</summary>
    public async Task<MessageResponse> GetAsync(Caller caller, Guid messageId, CancellationToken ct) =>
        (await messages.GetByIdAsync(messageId, caller.AppId, ct)).ToResponse();

    public async Task<IReadOnlyList<MessageSummaryResponse>> ListByCorrelationAsync(
        Caller caller, string? correlationId, CancellationToken ct)
    {
        caller.RequireAdmin();
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new BrokerValidationException([new("correlationId", "correlationId is required.")]);
        if (correlationId.Length > EnvelopeValidator.MaxCorrelationIdLength)
            throw new BrokerValidationException(
                [new("correlationId", $"correlationId must be at most {EnvelopeValidator.MaxCorrelationIdLength} characters.")]);

        return (await messages.ListByCorrelationAsync(correlationId, ct)).Select(m => m.ToResponse()).ToList();
    }
}
