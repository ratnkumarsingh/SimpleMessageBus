using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using MessageBroker.Api.Hosting;
using MessageBroker.Application.Security;
using MessageBroker.Contracts.Models;
using MessageBroker.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MessageBroker.LoadTests;

/// <summary>
/// The broker the load test talks to: either one already running, or one hosted in this process on a
/// real Kestrel port (with its background loops) against a throwaway database that is dropped afterwards.
/// </summary>
public sealed class BrokerUnderTest : IAsyncDisposable
{
    private const string DefaultServer = "Server=localhost;Integrated Security=true;TrustServerCertificate=true";

    private readonly WebApplicationFactory<Program>? _host;
    private readonly string? _server;
    private readonly string? _database;

    private BrokerUnderTest(Uri baseAddress, string adminKey, WebApplicationFactory<Program>? host = null, string? server = null, string? database = null)
    {
        BaseAddress = baseAddress;
        AdminKey = adminKey;
        _host = host;
        _server = server;
        _database = database;
    }

    public Uri BaseAddress { get; }
    public string AdminKey { get; }

    public static BrokerUnderTest Existing(Uri baseAddress, string adminKey) => new(baseAddress, adminKey);

    public static Task<BrokerUnderTest> StartAsync()
    {
        var server = Environment.GetEnvironmentVariable("BROKER_TEST_SQL") is { Length: > 0 } configured ? configured : DefaultServer;
        var database = $"BrokerDb_Load_{Guid.NewGuid():N}"[..28];
        var connectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = database }.ConnectionString;
        var adminKey = ApiKeys.Generate().Key;
        var port = FreePort();

        var host = new Host(connectionString, adminKey);
        host.UseKestrel(port);
        host.StartServer(); // deploys the schema, seeds the admin key, starts the loops
        return Task.FromResult(new BrokerUnderTest(new Uri($"http://127.0.0.1:{port}/"), adminKey, host, server, database));
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null)
            return;
        await _host.DisposeAsync();

        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID('{_database}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_database}];
            END
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class Host(string connectionString, string adminKey) : KestrelWebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("LoadTest");
            builder.UseContentRoot(AppContext.BaseDirectory);
            // Added last so they win over the API's appsettings.json.
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:BrokerDb"] = connectionString,
                ["Broker:Database:DeploySchemaOnStartup"] = "true",
                ["Broker:Database:CreateDatabase"] = "true",
                ["Broker:Bootstrap:AdminApiKey"] = adminKey,
                // Per-request information logs would dominate the run; warnings still show.
                ["Logging:LogLevel:Default"] = "Warning",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
            }));
            builder.ConfigureLogging(logging => logging.AddFilter(RequestLogging.ActionInvokerCategory, LogLevel.Warning));
        }
    }
}

/// <summary>A topic, a publisher allowed to publish to it, and a pull subscription with its consumer's key.</summary>
public sealed record LoadTopology(string TopicName, string PublisherKey, Guid SubscriptionId, string ConsumerKey)
{
    public static async Task<LoadTopology> CreateAsync(Uri broker, string adminKey)
    {
        using var admin = new HttpClient { BaseAddress = broker };
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(ApiKeys.Scheme, adminKey);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var topic = await PostAsync<TopicResponse>(admin, "api/v1/topics", new CreateTopicRequest($"load-{suffix}", null));
        var (publisherId, publisherKey) = await CreateAppAsync(admin, $"load-publisher-{suffix}");
        using (var granted = await admin.PostAsJsonAsync($"api/v1/admin/applications/{publisherId}/permissions",
                   new PermissionRequest("Topic", topic.TopicId, "Publish")))
            await EnsureAsync(granted);

        var (consumerId, consumerKey) = await CreateAppAsync(admin, $"load-consumer-{suffix}");
        var subscription = await PostAsync<SubscriptionResponse>(admin, $"api/v1/topics/{topic.TopicId}/subscriptions",
            new CreateSubscriptionRequest { Name = $"load-pull-{suffix}", OwnerAppId = consumerId, DeliveryMode = "Pull" });
        return new LoadTopology(topic.Name, publisherKey, subscription.SubscriptionId, consumerKey);
    }

    private static async Task<(Guid AppId, string Key)> CreateAppAsync(HttpClient admin, string name)
    {
        var app = await PostAsync<ApplicationResponse>(admin, "api/v1/admin/applications", new CreateApplicationRequest(name));
        var key = await PostAsync<ApiKeyResponse>(admin, $"api/v1/admin/applications/{app.AppId}/keys", new CreateApiKeyRequest());
        return (app.AppId, key.ApiKey!);
    }

    private static async Task<T> PostAsync<T>(HttpClient http, string path, object body)
    {
        using var response = await http.PostAsJsonAsync(path, body);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
