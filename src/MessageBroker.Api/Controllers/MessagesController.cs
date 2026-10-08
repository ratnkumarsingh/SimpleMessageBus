using MessageBroker.Application.Services;
using MessageBroker.Contracts.Models;
using Microsoft.AspNetCore.Mvc;

namespace MessageBroker.Api.Controllers;

[Route("api/v1")]
public sealed class MessagesController(PublishService publisher, MessageQueryService queries) : BrokerController
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>
    /// Publishes one message. 201 once it is committed; 200 with the original ID when the
    /// Idempotency-Key was already used by this publisher on this topic.
    /// </summary>
    [HttpPost("topics/{topicName}/messages")]
    [ProducesResponseType<PublishResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<PublishResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PublishResponse>> Publish(
        string topicName,
        PublishRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        CancellationToken ct)
    {
        var result = await publisher.PublishAsync(Caller, topicName, request, idempotencyKey, ct);
        return result.IsDuplicate
            ? Ok(result.Response)
            : CreatedAtAction(nameof(Get), new { messageId = result.Response.MessageId }, result.Response);
    }

    /// <summary>The message, its deliveries and every attempt. Admins, or the application that published it.</summary>
    [HttpGet("messages/{messageId:guid}")]
    public Task<MessageResponse> Get(Guid messageId, CancellationToken ct) => queries.GetAsync(Caller, messageId, ct);

    /// <summary>Every message sharing a correlation ID (Admin).</summary>
    [HttpGet("messages")]
    public Task<IReadOnlyList<MessageSummaryResponse>> ListByCorrelation([FromQuery] string? correlationId, CancellationToken ct) =>
        queries.ListByCorrelationAsync(Caller, correlationId, ct);
}
