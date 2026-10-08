using System.Text.Json;
using MessageBroker.Application.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MessageBroker.Api.Hosting;

/// <summary>
/// /health/live reports the process is running. /health/ready (spec section 13) is healthy only when
/// SQL Server answers within 2 seconds and the dispatcher heartbeat is under 30 seconds old.
/// </summary>
public static class BrokerHealth
{
    public const string ReadyTag = "ready";
    public static readonly TimeSpan SqlTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxHeartbeatAge = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddBrokerHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<SqlServerCheck>("sql", tags: [ReadyTag])
            .AddCheck<DispatcherHeartbeatCheck>("dispatcher", tags: [ReadyTag]);
        return services;
    }

    public static void MapBrokerHealthChecks(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync })
            .AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(ReadyTag), ResponseWriter = WriteAsync })
            .AllowAnonymous();
    }

    /// <summary>Runs <paramref name="work"/> with the 2-second budget; a timeout is reported, not thrown.</summary>
    internal static async Task<T> WithinSqlTimeoutAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SqlTimeout);
        return await work(cts.Token).WaitAsync(SqlTimeout, ct);
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), description = e.Value.Description }),
        }, JsonSerializerOptions.Web));
    }
}

internal sealed class SqlServerCheck(IOperationsRepository operations) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await BrokerHealth.WithinSqlTimeoutAsync(async token => { await operations.PingAsync(token); return 0; }, ct);
            return HealthCheckResult.Healthy("SQL Server answered.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"SQL Server did not answer within {BrokerHealth.SqlTimeout.TotalSeconds:0} seconds.", ex);
        }
    }
}

internal sealed class DispatcherHeartbeatCheck(IOperationsRepository operations) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        HeartbeatRecord heartbeat;
        try
        {
            heartbeat = await BrokerHealth.WithinSqlTimeoutAsync(token => operations.GetLatestHeartbeatAsync(token), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("The dispatcher heartbeat could not be read.", ex);
        }

        if (heartbeat.LastBeatAt is not { } lastBeat)
            return HealthCheckResult.Unhealthy("The dispatcher has not written a heartbeat.");

        // Both times come from the database clock.
        var age = heartbeat.DbNow - lastBeat;
        return age <= BrokerHealth.MaxHeartbeatAge
            ? HealthCheckResult.Healthy($"Dispatcher heartbeat is {age.TotalSeconds:0} s old.")
            : HealthCheckResult.Unhealthy($"Dispatcher heartbeat is {age.TotalSeconds:0} s old (limit {BrokerHealth.MaxHeartbeatAge.TotalSeconds:0} s).");
    }
}
