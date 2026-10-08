using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Samples.Shared;

namespace SamplePublisher;

/// <summary>The "Generator" configuration section.</summary>
public sealed class GeneratorOptions
{
    /// <summary>How many payments to record; 0 keeps going until stopped.</summary>
    public int Count { get; set; }
    public double IntervalSeconds { get; set; } = 2;
    /// <summary>Every Nth payment gets the FAIL- prefix, so subscribers fail it until it is dead-lettered (0: never).</summary>
    public int FailEvery { get; set; } = 5;
    /// <summary>Every Nth payment has amount 0, which subscribers reject straight to the DLQ (0: never).</summary>
    public int InvalidEvery { get; set; } = 7;
}

/// <summary>
/// Stands in for the publisher's business logic: records a payment and its event in one transaction.
/// It writes only to the local database, so it keeps working while the broker is down.
/// </summary>
public sealed class PaymentGenerator(OutboxStore outbox, SampleSettings settings, GeneratorOptions options,
    ILogger<PaymentGenerator> logger, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var run = time.GetUtcNow().ToString("MMddHHmmss");
        for (var n = 1; options.Count == 0 || n <= options.Count; n++)
        {
            var fail = options.FailEvery > 0 && n % options.FailEvery == 0;
            var invalid = options.InvalidEvery > 0 && n % options.InvalidEvery == 0;
            var paymentId = $"{(fail ? settings.FailPrefix : "PAY-")}{run}-{n:D4}";
            var amount = invalid ? 0m : 100m + n;

            var outboxId = await outbox.RecordPaymentAsync(paymentId, amount, "INR", settings.TopicName, stoppingToken);
            logger.LogInformation("Payment {PaymentId} of {Amount} recorded with outbox row {OutboxId}", paymentId, amount, outboxId);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        logger.LogInformation("Recorded {Count} payments; the relay keeps sending until stopped", options.Count);
    }
}
