using MessageBroker.Application.Services;
using MessageBroker.Contracts.Models;
using Microsoft.AspNetCore.Mvc;

namespace MessageBroker.Api.Controllers;

/// <summary>Pull receive and settlement (Subscriber), and the DLQ (owner reads, Admin requeues).</summary>
[Route("api/v1")]
public sealed class DeliveriesController(DeliveryService deliveries) : BrokerController
{
    /// <summary>Leases up to maxMessages (1–32), waiting up to waitSeconds (0–30). 200 with a possibly empty list.</summary>
    [HttpPost("subscriptions/{subscriptionId:guid}/receive")]
    public Task<IReadOnlyList<Delivery>> Receive(Guid subscriptionId, ReceiveRequest? request, CancellationToken ct) =>
        deliveries.ReceiveAsync(Caller, subscriptionId, request, ct);

    /// <summary>Marks the delivery completed. 410 when the lease was lost: do not retry, it will be redelivered.</summary>
    [HttpPost("deliveries/{deliveryId:long}/ack")]
    public async Task<IActionResult> Ack(long deliveryId, AckRequest request, CancellationToken ct)
    {
        await deliveries.AckAsync(Caller, deliveryId, request, ct);
        return NoContent();
    }

    [HttpPost("deliveries/{deliveryId:long}/nack")]
    public async Task<IActionResult> Nack(long deliveryId, NackRequest request, CancellationToken ct)
    {
        await deliveries.NackAsync(Caller, deliveryId, request, ct);
        return NoContent();
    }

    /// <summary>Extends the lease by the subscription's lock duration (at most 600 s).</summary>
    [HttpPost("deliveries/{deliveryId:long}/renew")]
    public async Task<IActionResult> Renew(long deliveryId, RenewRequest request, CancellationToken ct)
    {
        await deliveries.RenewAsync(Caller, deliveryId, request, ct);
        return NoContent();
    }

    /// <summary>DLQ entries, newest first. Pass the previous page's nextBefore as before.</summary>
    [HttpGet("subscriptions/{subscriptionId:guid}/deadletters")]
    public Task<DeadLetterPage> ListDeadLetters(
        Guid subscriptionId,
        [FromQuery] int? pageSize,
        [FromQuery] long? before,
        [FromQuery] bool includeRequeued,
        CancellationToken ct) =>
        deliveries.ListDeadLettersAsync(Caller, subscriptionId, pageSize, before, includeRequeued, ct);

    /// <summary>Returns the entry to Pending with its attempts reset (Admin).</summary>
    [HttpPost("deadletters/{deliveryId:long}/requeue")]
    public async Task<IActionResult> Requeue(long deliveryId, CancellationToken ct)
    {
        await deliveries.RequeueAsync(Caller, deliveryId, ct);
        return NoContent();
    }
}
