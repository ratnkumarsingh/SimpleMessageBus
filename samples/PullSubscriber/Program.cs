using MessageBroker.Contracts.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Samples.Shared;

// dotnet run --project samples/PullSubscriber
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddSampleSettings().AddEnvironmentVariables().AddCommandLine(args);
var settings = builder.Configuration.GetSampleSettings();

builder.Services.AddPaymentSubscriber(settings, settings.Pull, subscriber: "pull");
builder.Services.AddHostedService<PullPaymentWorker>();

var host = builder.Build();
await host.Services.GetRequiredService<SampleDatabase>().DeployAsync();
await host.RunAsync();

/// <summary>
/// Long-polls the pull subscription (spec section 8.4), hands each delivery to the
/// <see cref="PaymentHandler"/>, and ACKs or NACKs it with the delivery's lock token.
/// </summary>
public sealed class PullPaymentWorker(SampleSettings settings, BrokerClient broker, PaymentHandler handler, ILogger<PullPaymentWorker> logger)
    : BackgroundService
{
    public const int MaxMessages = 8;

    /// <summary>One receive (waiting up to <paramref name="waitSeconds"/>) and the settlement of what it returned.</summary>
    public async Task<int> RunOnceAsync(int waitSeconds, CancellationToken ct)
    {
        var deliveries = await broker.ReceiveAsync(settings.Pull.SubscriptionId, MaxMessages, waitSeconds, ct);
        foreach (var delivery in deliveries)
        {
            DeliveryResult result;
            try
            {
                result = await handler.HandleAsync(delivery, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Delivery {DeliveryId} failed", delivery.DeliveryId);
                result = DeliveryResult.Fail("HandlerException", ex.Message);
            }

            try
            {
                if (result.Success)
                    await broker.AckAsync(delivery, ct);
                else
                    await broker.NackAsync(delivery, result.ErrorCode, result.ErrorMessage, result.DeadLetter, ct);
            }
            catch (BrokerApiException ex) when (ex.IsLeaseLost)
            {
                // The lease ran out first; the broker redelivers and deduplication skips the repeat.
                logger.LogWarning("Lease on delivery {DeliveryId} was lost before it could be settled", delivery.DeliveryId);
            }
        }
        return deliveries.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Receiving from subscription {SubscriptionId}", settings.Pull.SubscriptionId);
        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(waitSeconds: 20, stoppingToken);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Receive failed ({Error}); retrying in {Seconds} s", ex.Message, backoff.TotalSeconds);
                await Task.Delay(backoff, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }
}
