using System.Text.Json;
using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using MessageBroker.Contracts.Webhooks;
using Samples.Shared;

namespace WebhookSubscriber;

/// <summary>
/// The webhook endpoint (spec section 8.2). It verifies the signature, deduplicates by MessageId and
/// answers 2xx only after the invoice change has committed:
/// <list type="bullet">
/// <item>Processed, or already processed: 200, which completes the delivery.</item>
/// <item>Bad signature: 401, so the broker retries; check the secret.</item>
/// <item>Invalid payload: a REST NACK with deadLetter, then 202 (the lease is already settled).</item>
/// <item>Other failures: 503, so the broker retries with backoff.</item>
/// </list>
/// </summary>
public static class PaymentWebhook
{
    public const string Path = "/webhooks/payments";

    public static RouteHandlerBuilder MapPaymentWebhook(this IEndpointRouteBuilder app) =>
        app.MapPost(Path, HandleAsync);

    private static async Task<IResult> HandleAsync(HttpRequest request, PaymentHandler handler, BrokerClient broker,
        SampleSettings settings, TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        var logger = loggers.CreateLogger(typeof(PaymentWebhook));
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(ct);

        if (!WebhookSignature.Verify(request.Headers[WebhookHeaders.Signature], request.Headers[WebhookHeaders.Timestamp],
                body, time.GetUtcNow(), settings.Webhook.Secrets))
        {
            logger.LogWarning("Webhook call for delivery {DeliveryId} has an invalid signature", request.Headers[WebhookHeaders.DeliveryId].ToString());
            return Results.Unauthorized();
        }

        var delivery = JsonSerializer.Deserialize<Delivery>(body, JsonSerializerOptions.Web)!;
        var result = await handler.HandleAsync(delivery, ct);
        if (result.Success)
            return Results.Ok();

        if (result.DeadLetter)
        {
            // The status code alone cannot ask for the DLQ, so settle through the REST API instead.
            await broker.NackAsync(delivery, result.ErrorCode, result.ErrorMessage, deadLetter: true, ct);
            return Results.Accepted();
        }
        return Results.Problem(result.ErrorMessage, statusCode: StatusCodes.Status503ServiceUnavailable, title: result.ErrorCode);
    }
}
