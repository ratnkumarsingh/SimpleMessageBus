using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Samples.Shared;

/// <summary>
/// Joins one SignalR subscription and hands each delivery to <c>handler</c>; the Contracts listener
/// ACKs or NACKs with the result. The broker may not be up yet, so joining is retried with backoff;
/// once joined, the listener reconnects and rejoins by itself.
/// </summary>
public class SignalRSubscriberWorker(
    Uri brokerUrl,
    SampleApp app,
    Func<Delivery, CancellationToken, Task<DeliveryResult>> handler,
    ILogger logger,
    Action<HttpConnectionOptions>? configureConnection = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = await ConnectAsync(stoppingToken);
        if (listener is null)
            return;
        await using (listener)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
            await listener.StopAsync(CancellationToken.None);
        }
    }

    private async Task<SignalRDeliveryListener?> ConnectAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(app.ApiKey))
            throw new InvalidOperationException("No API key for this subscriber. Run 'SamplePublisher setup --AdminKey <key>' first.");

        for (var delay = TimeSpan.FromSeconds(1); !stoppingToken.IsCancellationRequested; delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30)))
        {
            var listener = new SignalRDeliveryListener(brokerUrl, app.ApiKey, handler, configureConnection);
            listener.SettlementFailed += (delivery, ex) =>
                logger.LogWarning("Could not settle delivery {DeliveryId}: {Error}", delivery.DeliveryId, ex.Message);
            try
            {
                await listener.StartAsync([app.SubscriptionId], stoppingToken);
                logger.LogInformation("Listening on subscription {SubscriptionId}", app.SubscriptionId);
                return listener;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                await listener.DisposeAsync();
                logger.LogWarning("Could not join the broker ({Error}); retrying in {Seconds} s", ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
            catch
            {
                await listener.DisposeAsync();
                throw;
            }
        }
        return null;
    }
}
