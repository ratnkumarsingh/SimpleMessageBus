using System.Text.Json;
using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using MessageBroker.Contracts.Webhooks;
using Samples.Shared;

namespace ConsoleSubscriber;

/// <summary>
/// The webhook channel's endpoint (spec section 8.2), answered like WebhookSubscriber's: 401 for a bad
/// signature (the broker retries), 200 once handled, and for an unreadable notification a REST NACK
/// with deadLetter followed by 202 (the lease is already settled).
/// </summary>
public static class NotificationWebhook
{
    public const string Path = "/webhooks/notifications";

    public static RouteHandlerBuilder MapNotificationWebhook(this IEndpointRouteBuilder app) =>
        app.MapPost(Path, HandleAsync);

    private static async Task<IResult> HandleAsync(HttpRequest request, SampleSettings settings, TimeProvider time,
        [FromKeyedServices("Webhook")] NotificationHandler handler, [FromKeyedServices("Webhook")] BrokerClient broker,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var logger = loggers.CreateLogger(typeof(NotificationWebhook));
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(ct);

        if (!WebhookSignature.Verify(request.Headers[WebhookHeaders.Signature], request.Headers[WebhookHeaders.Timestamp],
                body, time.GetUtcNow(), settings.ConsoleWebhook.Secrets))
        {
            logger.LogWarning("Webhook call for delivery {DeliveryId} has an invalid signature", request.Headers[WebhookHeaders.DeliveryId].ToString());
            return Results.Unauthorized();
        }

        var delivery = JsonSerializer.Deserialize<Delivery>(body, JsonSerializerOptions.Web)!;
        var result = await handler.HandleAsync(delivery, ct);
        if (result.Success)
            return Results.Ok();

        // NotificationHandler fails only for a payload it cannot read, and asks for the DLQ; the status
        // code alone cannot ask for that, so settle through the REST API instead.
        await broker.NackAsync(delivery, result.ErrorCode, result.ErrorMessage, result.DeadLetter, ct);
        return Results.Accepted();
    }
}
