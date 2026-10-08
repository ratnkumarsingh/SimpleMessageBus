using MessageBroker.Application;
using MessageBroker.Application.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MessageBroker.Worker.Maintenance;

/// <summary>
/// Hourly purge (spec section 15): completed work after CompletedDays (14), dead letters after
/// DeadLetterDays (90). The first pass runs at startup.
/// </summary>
public sealed class RetentionLoop(
    IOperationsRepository operations,
    IOptions<BrokerOptions> options,
    TimeProvider time,
    ILogger<RetentionLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.Retention.IntervalMinutes), time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Retention purge failed; retrying on the next interval");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var retention = options.Value.Retention;
        var purged = await operations.PurgeAsync(retention.CompletedDays, retention.DeadLetterDays, retention.BatchSize, ct);
        if (purged.DeliveriesDeleted > 0 || purged.MessagesDeleted > 0)
            logger.LogInformation("Retention purge removed {Deliveries} deliveries and {Messages} messages",
                purged.DeliveriesDeleted, purged.MessagesDeleted);
    }
}
