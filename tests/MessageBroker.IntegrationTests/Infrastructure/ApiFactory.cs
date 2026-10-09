using System.Net.Http.Headers;
using MessageBroker.Api.Hosting;
using MessageBroker.Application.Security;
using MessageBroker.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MessageBroker.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the API in-process against the fixture's database. The schema is already deployed by the
/// fixture, so startup only seeds the allowlist and the bootstrap admin key.
/// </summary>
public sealed class ApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? settings = null)
    : KestrelWebApplicationFactory<Program>
{
    public const string SeededHost = "hooks.internal";

    public static string AdminKey { get; } = ApiKeys.Generate().Key;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:BrokerDb", connectionString);
        builder.UseSetting("Broker:Database:DeploySchemaOnStartup", "false");
        // Background loops would race the tests' own time travel; tests run single passes instead.
        builder.UseSetting("Broker:Dispatcher:Enabled", "false");
        builder.UseSetting("Broker:Bootstrap:AdminApiKey", AdminKey);
        builder.UseSetting("Broker:Webhooks:AllowedHosts:0", SeededHost);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            builder.UseSetting(key, value);
    }

    public HttpClient CreateClient(string? apiKey)
    {
        var client = CreateClient();
        if (apiKey is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(ApiKeys.Scheme, apiKey);
        return client;
    }

    /// <summary>Re-runs the startup seeding, e.g. after Respawn has cleared the tables.</summary>
    public Task RunStartupTasksAsync() =>
        Services.GetServices<IHostedService>().OfType<StartupTasks>().Single().StartAsync(CancellationToken.None);
}
