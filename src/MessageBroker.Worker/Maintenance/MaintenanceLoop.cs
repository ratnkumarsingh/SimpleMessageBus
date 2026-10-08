using MessageBroker.Application;
using MessageBroker.Application.Dispatch;
using MessageBroker.Application.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MessageBroker.Worker.Maintenance;

/// <summary>
/// Every MaintenanceIntervalSeconds (default 5): returns lapsed leases to the retry path, dead-letters
/// Pending deliveries past their TTL, and writes the dispatcher heartbeat that /health/ready checks.
/// </summary>
public sealed class MaintenanceLoop(
    IDeliveryRepository deliveries,
    IOperationsRepository operations,
    IBrokerActivityFeed activity,
    IOptions<BrokerOptions> options,
    TimeProvider time,
    ILogger<MaintenanceLoop> logger) : BackgroundService
{
    public const int LeaseBatchSize = 500;
    public const int PendingBatchSize = 1000;

    /// <summary>Identifies this broker process in broker.BrokerHeartbeats.</summary>
    public static string InstanceId { get; } = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.Dispatcher.MaintenanceIntervalSeconds), time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Maintenance pass failed; retrying on the next interval");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass. Large backlogs are worked through in batches within the pass.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        int count, leases = 0, expired = 0;
        do leases += count = await deliveries.ExpireLeasesAsync(LeaseBatchSize, ct);
        while (count == LeaseBatchSize);
        do expired += count = await deliveries.ExpirePendingAsync(PendingBatchSize, ct);
        while (count == PendingBatchSize);

        if (leases > 0)
            logger.LogInformation("{Count} lapsed leases returned for retry or dead-lettered", leases);
        if (expired > 0)
            logger.LogInformation("{Count} pending deliveries expired and were dead-lettered", expired);
        if (leases + expired > 0)
            activity.Changed();

        await operations.WriteHeartbeatAsync(InstanceId, ct);
    }
}
