using System.Text.Json;
using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace Samples.Shared;

/// <summary>
/// The business logic all three sample subscribers share: mark the invoice for a PaymentProcessed.v1
/// event paid, exactly once per message.
/// <list type="bullet">
/// <item>Invalid payload (no paymentId, or amount not positive): fail with deadLetter, since a retry cannot help.</item>
/// <item>A paymentId starting with <see cref="FailPrefix"/>: fail without deadLetter, so the broker retries
/// with backoff and dead-letters it after MaxAttempts. Clear the prefix and requeue to see it succeed.</item>
/// <item>Otherwise apply the payment; a message processed before is skipped and still ACKed.</item>
/// </list>
/// </summary>
public sealed class PaymentHandler(InvoiceStore invoices, string subscriber, ILogger logger, string? failPrefix = PaymentHandler.DefaultFailPrefix)
{
    public const string DefaultFailPrefix = "FAIL-";

    public string? FailPrefix { get; set; } = failPrefix;

    public async Task<DeliveryResult> HandleAsync(Delivery delivery, CancellationToken ct)
    {
        var message = delivery.Message;
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = message.MessageId,
            ["CorrelationId"] = message.CorrelationId,
            ["DeliveryId"] = delivery.DeliveryId,
        });

        if (!TryRead(message.Payload, out var paymentId, out var amount))
        {
            logger.LogWarning("Delivery {DeliveryId} rejected: the payload is not a valid payment", delivery.DeliveryId);
            return DeliveryResult.Fail("InvalidPayload", "paymentId is required and amount must be positive", deadLetter: true);
        }

        if (!string.IsNullOrEmpty(FailPrefix) && paymentId.StartsWith(FailPrefix, StringComparison.Ordinal))
        {
            logger.LogWarning("Payment {PaymentId} failed (simulated, attempt {Attempt})", paymentId, delivery.Attempt);
            return DeliveryResult.Fail("DownstreamUnavailable", $"Simulated failure for {paymentId}");
        }

        if (await invoices.ApplyPaymentAsync(subscriber, message.MessageId, paymentId, amount, ct))
            logger.LogInformation("Payment {PaymentId} was already processed; skipped and acknowledged", paymentId);
        else
            logger.LogInformation("Invoice for payment {PaymentId} marked paid (attempt {Attempt})", paymentId, delivery.Attempt);
        return DeliveryResult.Ack;
    }

    private static bool TryRead(JsonElement payload, out string paymentId, out decimal amount)
    {
        paymentId = "";
        amount = 0;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("paymentId", out var id) || id.ValueKind != JsonValueKind.String
            || !payload.TryGetProperty("amount", out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDecimal(out amount))
            return false;
        paymentId = id.GetString()!;
        return paymentId.Length is > 0 and <= 50 && amount > 0;
    }
}
