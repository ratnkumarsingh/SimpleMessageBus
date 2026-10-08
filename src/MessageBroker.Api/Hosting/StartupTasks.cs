using MessageBroker.Application;
using MessageBroker.Application.Persistence;
using MessageBroker.Application.Security;
using MessageBroker.Application.Services;
using MessageBroker.Infrastructure.Data;
using Microsoft.Extensions.Options;

namespace MessageBroker.Api.Hosting;

/// <summary>
/// Runs before the server accepts requests: deploys the schema (when enabled), seeds the webhook host
/// allowlist from configuration, and ensures the bootstrap admin key exists.
/// </summary>
public sealed class StartupTasks(
    BrokerDbOptions db,
    IAllowedHostRepository allowedHosts,
    IApplicationRepository applications,
    IOptions<BrokerOptions> options,
    ILogger<StartupTasks> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var broker = options.Value;

        if (broker.Database.DeploySchemaOnStartup)
        {
            logger.LogInformation("Deploying the broker schema");
            new SchemaDeployer(db.ConnectionString, logger).Deploy(broker.Database.CreateDatabase);
        }

        // [Fix 9] Configuration only adds hosts; Admins remove them through the API.
        var hosts = broker.Webhooks.AllowedHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(AdminService.NormalizeHost).ToList();
        if (hosts.Count > 0)
            await allowedHosts.SeedAsync(hosts, ct);

        if (!string.IsNullOrWhiteSpace(broker.Bootstrap.AdminApiKey))
        {
            var key = broker.Bootstrap.AdminApiKey.Trim();
            if (!ApiKeys.TryGetPrefix(key, out var prefix))
                throw new InvalidOperationException("Broker:Bootstrap:AdminApiKey is not a valid broker API key.");
            await applications.EnsureBootstrapAdminAsync(Guid.CreateVersion7(), broker.Bootstrap.AdminName,
                Guid.CreateVersion7(), prefix, ApiKeys.Hash(key), ct);
            logger.LogInformation("Bootstrap admin key {KeyPrefix} is registered", prefix);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
